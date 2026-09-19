using Dracocephalum.Nightingale.Protocol;
using Dracocephalum.Nightingale.Protocol.V1;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// The <c>Streams</c> service over an <see cref="IStreamStore"/> and its <see cref="IStoreTail"/>.
/// It validates requests, keeps the contract's message order on a read, and translates the store's
/// domain exceptions into the contract's errors. It knows nothing about the backend: revisions and
/// positions arrive from the store already in the contract's numbering. This slice serves plain
/// streams and <c>$all</c>, bounded and as subscriptions; the virtual streams and filters answer
/// with an unimplemented status until their slice lands.
/// </summary>
/// <param name="store">The backend.</param>
/// <param name="tail">The backend's head as it moves.</param>
/// <param name="timeProvider">The clock the progress notes are stamped with.</param>
public sealed class StreamsService(IStreamStore store, IStoreTail tail, TimeProvider timeProvider) : Streams.StreamsBase
{
    /// <summary>The longest stream name the store column holds.</summary>
    public const int MaxStreamNameLength = 250;

    /// <summary>
    /// How many events one store call asks for. Small enough that a slow consumer never makes the
    /// server hold a large page in memory, large enough that a full read is not one call per event.
    /// </summary>
    public const int PageSize = 512;

    /// <inheritdoc/>
    public override async Task Read(ReadRequest request, IServerStreamWriter<ReadResponse> responseStream, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(responseStream);
        ArgumentNullException.ThrowIfNull(context);

        var stream = ValidStreamName(request.Stream);
        var all = stream == StreamNames.All;
        if (request.Filter is not null)
        {
            throw all ? NightingaleErrors.NotImplemented("Filtering $all") : NightingaleErrors.FilterNotAllowed(stream);
        }

        var direction = request.Direction == ReadDirection.Backwards ? Direction.Backwards : Direction.Forwards;
        if (request.FromCase == ReadRequest.FromOneofCase.Position && request.Position < 0)
        {
            throw NightingaleErrors.InvalidArgument("A position is never negative.");
        }

        var cancellationToken = context.CancellationToken;
        switch (request.ModeCase)
        {
            case ReadRequest.ModeOneofCase.Count when request.Count > 0:
                if (all)
                {
                    await ReadAllBounded(request, direction, responseStream, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await ReadStreamBounded(stream, request, direction, responseStream, cancellationToken).ConfigureAwait(false);
                }

                return;
            case ReadRequest.ModeOneofCase.Subscription:
                if (direction == Direction.Backwards)
                {
                    throw NightingaleErrors.InvalidArgument("A subscription reads forwards.");
                }

                if (all)
                {
                    await SubscribeAll(request, responseStream, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await SubscribeStream(stream, request, responseStream, cancellationToken).ConfigureAwait(false);
                }

                return;
            default:
                throw NightingaleErrors.InvalidArgument("A read needs a positive count.");
        }
    }

    /// <inheritdoc/>
    public override async Task<AppendResponse> Append(IAsyncStreamReader<AppendRequest> requestStream, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(requestStream);
        ArgumentNullException.ThrowIfNull(context);

        if (!await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false)
            || requestStream.Current.ContentCase != AppendRequest.ContentOneofCase.Options)
        {
            throw NightingaleErrors.InvalidArgument("The first message of an append carries the options.");
        }

        var options = requestStream.Current.Options;
        var stream = PlainStreamName(options.Stream);
        StreamState expected;
        try
        {
            expected = StreamState.FromInt64(options.ExpectedRevision);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw NightingaleErrors.InvalidArgument("The expected revision is neither a revision nor a named state.");
        }

        var events = new List<EventData>();
        while (await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false))
        {
            if (requestStream.Current.ContentCase != AppendRequest.ContentOneofCase.Event)
            {
                throw NightingaleErrors.InvalidArgument("After the options, every message of an append carries one event.");
            }

            var proposed = requestStream.Current.Event;
            if (string.IsNullOrEmpty(proposed.EventType))
            {
                throw NightingaleErrors.InvalidArgument("Every event needs a type.");
            }

            try
            {
                events.Add(proposed.ToEventData());
            }
            catch (FormatException)
            {
                throw NightingaleErrors.InvalidArgument("An event id is a UUID in its canonical form.");
            }
            catch (System.Text.Json.JsonException)
            {
                throw NightingaleErrors.InvalidArgument("Event metadata is a JSON object.");
            }
        }

        if (events.Count == 0)
        {
            throw NightingaleErrors.InvalidArgument("An append carries at least one event.");
        }

        try
        {
            var result = await store.AppendAsync(stream, expected, events, context.CancellationToken).ConfigureAwait(false);
            return new AppendResponse { Revision = result.Revision, Position = result.Position };
        }
        catch (RevisionConflictException conflict)
        {
            throw NightingaleErrors.RevisionConflict(conflict);
        }
        catch (StreamDeletedException deleted)
        {
            throw NightingaleErrors.StreamDeleted(deleted.Stream);
        }
    }

    /// <summary>
    /// Validates a name where a plain stream is required. Reserved names are valid in the contract but
    /// not appended to.
    /// </summary>
    private static string PlainStreamName(string stream)
    {
        var name = ValidStreamName(stream);
        if (StreamNames.IsReserved(name))
        {
            throw NightingaleErrors.InvalidStreamName(name, "reserved; only plain streams are appended to");
        }

        return name;
    }

    /// <summary>
    /// Validates a name for a read: a plain stream or <c>$all</c>. The virtual streams are valid in
    /// the contract but served by a later slice, so they are refused as not implemented rather than
    /// as invalid.
    /// </summary>
    private static string ValidStreamName(string stream)
    {
        if (string.IsNullOrEmpty(stream))
        {
            throw NightingaleErrors.InvalidStreamName(stream, "empty");
        }

        if (stream.Length > MaxStreamNameLength)
        {
            throw NightingaleErrors.InvalidStreamName(stream, $"longer than {MaxStreamNameLength} characters");
        }

        if (StreamNames.IsReserved(stream) && stream != StreamNames.All)
        {
            throw NightingaleErrors.NotImplemented("Reading the virtual streams");
        }

        return stream;
    }

    private static ReadResponse CaughtUpAt(long head, DateTimeOffset at) =>
        new() { CaughtUp = new CaughtUp { Head = head, At = Timestamp.FromDateTimeOffset(at) } };

    private static ReadResponse FellBehindAt(long head, long behind, DateTimeOffset at) =>
        new() { FellBehind = new FellBehind { Head = head, EventsBehind = behind, At = Timestamp.FromDateTimeOffset(at) } };

    private async Task ReadStreamBounded(string stream, ReadRequest request, Direction direction, IServerStreamWriter<ReadResponse> responseStream, CancellationToken cancellationToken)
    {
        long? from = request.FromCase switch
        {
            ReadRequest.FromOneofCase.Position => request.Position,
            ReadRequest.FromOneofCase.Start => direction == Direction.Forwards ? null : 0,
            ReadRequest.FromOneofCase.End => direction == Direction.Backwards ? null : long.MaxValue,
            _ => null,
        };

        var remaining = request.Count;
        var first = true;
        while (remaining > 0)
        {
            var pageSize = (int)Math.Min(remaining, PageSize);
            var page = await ReadPage(stream, direction, from, pageSize, cancellationToken).ConfigureAwait(false);
            if (page is null)
            {
                await responseStream.WriteAsync(new ReadResponse { StreamNotFound = new StreamNotFound { Stream = stream } }, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (first)
            {
                await responseStream.WriteAsync(new ReadResponse { Head = new StreamBounds { First = page.Head.First, Last = page.Head.Last } }, cancellationToken).ConfigureAwait(false);
                first = false;
            }

            foreach (var record in page.Events)
            {
                await responseStream.WriteAsync(new ReadResponse { Event = record.ToRecordedEvent() }, cancellationToken).ConfigureAwait(false);
            }

            remaining -= (ulong)page.Events.Count;
            if (page.Events.Count < pageSize)
            {
                return;
            }

            var last = page.Events[^1].Revision;
            if (direction == Direction.Backwards && last == 0)
            {
                return;
            }

            from = direction == Direction.Forwards ? last + 1 : last - 1;
        }
    }

    /// <summary>
    /// A bounded read of <c>$all</c> is a snapshot: the head is brought up to date once, before the
    /// first page, so an append that returned before the read is in it, and no page reads past it,
    /// so events committed during the read are not delivered.
    /// </summary>
    private async Task ReadAllBounded(ReadRequest request, Direction direction, IServerStreamWriter<ReadResponse> responseStream, CancellationToken cancellationToken)
    {
        var head = await tail.RefreshAsync(cancellationToken).ConfigureAwait(false);
        var from = request.FromCase switch
        {
            ReadRequest.FromOneofCase.Position => request.Position,
            ReadRequest.FromOneofCase.End => direction == Direction.Backwards ? head : head + 1,
            _ => direction == Direction.Forwards ? 0 : head,
        };

        await responseStream.WriteAsync(new ReadResponse { Head = new StreamBounds { First = 0, Last = head } }, cancellationToken).ConfigureAwait(false);
        var remaining = request.Count;
        while (remaining > 0)
        {
            if (head == 0 || (direction == Direction.Forwards ? from > head : from <= 0))
            {
                return;
            }

            var pageSize = (int)Math.Min(remaining, PageSize);
            var page = await store.ReadAllAsync(direction, from, head, pageSize, cancellationToken).ConfigureAwait(false);
            foreach (var record in page)
            {
                await responseStream.WriteAsync(new ReadResponse { Event = record.ToRecordedEvent() }, cancellationToken).ConfigureAwait(false);
            }

            remaining -= (ulong)page.Count;
            if (page.Count < pageSize)
            {
                return;
            }

            from = direction == Direction.Forwards ? page[^1].Position + 1 : page[^1].Position - 1;
        }
    }

    /// <summary>
    /// A subscription to a plain stream: confirm with the head, deliver what is there, say so, then
    /// wait for the store's head to move and read what the stream gained. A stream that does not
    /// exist yet is confirmed with head -1 and waited for. Waking on the store's head rather than
    /// on the stream's own means one query per wake per subscription; a router that reads the new
    /// range once and hands events to their subscriptions is the scaling step recorded in TODO.md.
    /// </summary>
    private async Task SubscribeStream(string stream, ReadRequest request, IServerStreamWriter<ReadResponse> responseStream, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("D");
        var liveOnly = request.FromCase == ReadRequest.FromOneofCase.End;
        var next = request.FromCase == ReadRequest.FromOneofCase.Position ? request.Position : 0;

        // The store's head is taken before every read, so an event committed between the read and
        // the wait is not missed: the wait returns as soon as the head is beyond what was taken.
        var observed = tail.Head;
        var page = await ReadPage(stream, Direction.Forwards, liveOnly ? long.MaxValue : next, PageSize, cancellationToken).ConfigureAwait(false);
        var head = page?.Head.Last ?? -1;
        await responseStream.WriteAsync(new ReadResponse { Confirmed = new SubscriptionConfirmed { SubscriptionId = id, Head = head } }, cancellationToken).ConfigureAwait(false);
        if (liveOnly)
        {
            next = head + 1;
            page = null;
        }

        while (true)
        {
            var behind = false;
            while (page is not null && page.Events.Count > 0)
            {
                if (page.Events.Count == PageSize && page.Head.Last > page.Events[^1].Revision && !behind)
                {
                    behind = true;
                    await responseStream.WriteAsync(FellBehindAt(page.Head.Last, page.Head.Last - next + 1, timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
                }

                foreach (var record in page.Events)
                {
                    await responseStream.WriteAsync(new ReadResponse { Event = record.ToRecordedEvent() }, cancellationToken).ConfigureAwait(false);
                }

                next = page.Events[^1].Revision + 1;
                head = page.Head.Last;
                page = page.Events.Count < PageSize ? null : await ReadPage(stream, Direction.Forwards, next, PageSize, cancellationToken).ConfigureAwait(false);
            }

            await responseStream.WriteAsync(CaughtUpAt(head, timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);

            do
            {
                observed = await tail.WaitForAdvanceAsync(observed, cancellationToken).ConfigureAwait(false);
                page = await ReadPage(stream, Direction.Forwards, next, PageSize, cancellationToken).ConfigureAwait(false);
            }
            while (page is null || page.Events.Count == 0);
        }
    }

    /// <summary>
    /// A subscription to <c>$all</c>: the same shape as a stream's, with the store's head as both
    /// the bound of every page and the thing waited on.
    /// </summary>
    private async Task SubscribeAll(ReadRequest request, IServerStreamWriter<ReadResponse> responseStream, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("D");

        // From the end means after now, so the head is brought up to date first; from anywhere else
        // the catch-up reads whatever the poller has seen and the live phase delivers the rest.
        var head = request.FromCase == ReadRequest.FromOneofCase.End ? await tail.RefreshAsync(cancellationToken).ConfigureAwait(false) : tail.Head;
        var next = request.FromCase switch
        {
            ReadRequest.FromOneofCase.Position => request.Position,
            ReadRequest.FromOneofCase.End => head + 1,
            _ => 0,
        };
        await responseStream.WriteAsync(new ReadResponse { Confirmed = new SubscriptionConfirmed { SubscriptionId = id, Head = head } }, cancellationToken).ConfigureAwait(false);

        while (true)
        {
            var behind = false;
            while (head > 0 && next <= head)
            {
                var page = await store.ReadAllAsync(Direction.Forwards, next, head, PageSize, cancellationToken).ConfigureAwait(false);
                if (page.Count == PageSize && page[^1].Position < head && !behind)
                {
                    behind = true;
                    await responseStream.WriteAsync(FellBehindAt(head, head - page[^1].Position, timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
                }

                foreach (var record in page)
                {
                    await responseStream.WriteAsync(new ReadResponse { Event = record.ToRecordedEvent() }, cancellationToken).ConfigureAwait(false);
                }

                next = page.Count < PageSize ? head + 1 : page[^1].Position + 1;
            }

            await responseStream.WriteAsync(CaughtUpAt(head, timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
            head = await tail.WaitForAdvanceAsync(head, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<StreamSlice?> ReadPage(string stream, Direction direction, long? from, int count, CancellationToken cancellationToken)
    {
        try
        {
            return await store.ReadAsync(stream, direction, from, count, cancellationToken).ConfigureAwait(false);
        }
        catch (StreamDeletedException deleted)
        {
            throw NightingaleErrors.StreamDeleted(deleted.Stream);
        }
    }
}
