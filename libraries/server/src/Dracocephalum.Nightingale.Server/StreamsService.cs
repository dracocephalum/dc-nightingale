using Dracocephalum.Nightingale.Protocol;
using Dracocephalum.Nightingale.Protocol.V1;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// The <c>Streams</c> service over an <see cref="IStreamStore"/> and its <see cref="IStoreTail"/>.
/// It validates requests, keeps the contract's message order on a read, and translates the store's
/// domain exceptions into the contract's errors. It knows nothing about the backend: revisions and
/// positions arrive from the store already in the contract's numbering. It serves plain streams,
/// <c>$all</c> and the virtual streams, bounded and as subscriptions, the virtual streams under
/// either numbering; filters on <c>$all</c> answer with an unimplemented status until their slice
/// lands.
/// </summary>
/// <param name="store">The backend.</param>
/// <param name="tail">The backend's head as it moves.</param>
/// <param name="timeProvider">The clock the progress notes are stamped with.</param>
/// <param name="options">The common settings: which deletions the host allows.</param>
public sealed class StreamsService(IStreamStore store, IStoreTail tail, TimeProvider timeProvider, NightingaleOptionsBase options) : Streams.StreamsBase
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
        var isVirtual = StreamNames.TryParseVirtual(stream, out var virtualStream);
        if (request.Filter is not null)
        {
            throw all ? NightingaleErrors.NotImplemented("Filtering $all") : NightingaleErrors.FilterNotAllowed(stream);
        }

        var ordinal = request.Numbering.ToNumbering() == Numbering.Ordinal;
        if (ordinal && !isVirtual)
        {
            throw NightingaleErrors.InvalidArgument("Ordinal numbering applies to $ce- and $et- streams only.");
        }

        if (ordinal && !store.OrdinalsEnabled)
        {
            throw NightingaleErrors.OrdinalsNotEnabled(stream);
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
                else if (ordinal)
                {
                    await ReadOrdinalBounded(virtualStream, request, direction, responseStream, cancellationToken).ConfigureAwait(false);
                }
                else if (isVirtual)
                {
                    await ReadVirtualBounded(virtualStream, request, direction, responseStream, cancellationToken).ConfigureAwait(false);
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
                else if (ordinal)
                {
                    await SubscribeOrdinal(virtualStream, request, responseStream, cancellationToken).ConfigureAwait(false);
                }
                else if (isVirtual)
                {
                    await SubscribeVirtual(virtualStream, request, responseStream, cancellationToken).ConfigureAwait(false);
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

        var appendOptions = requestStream.Current.Options;
        var stream = PlainStreamName(appendOptions.Stream);
        var expected = Expected(appendOptions.ExpectedRevision);

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

    /// <inheritdoc/>
    public override async Task<DeleteResponse> Delete(DeleteRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var stream = PlainStreamName(request.Stream);
        if (!options.Deletion.AllowDelete)
        {
            throw NightingaleErrors.DeletionDisabled(stream, "Delete");
        }

        await Deleting(stream, () => store.DeleteAsync(stream, Expected(request.ExpectedRevision), context.CancellationToken)).ConfigureAwait(false);
        return new DeleteResponse();
    }

    /// <inheritdoc/>
    public override async Task<TombstoneResponse> Tombstone(TombstoneRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var stream = PlainStreamName(request.Stream);
        if (!options.Deletion.AllowTombstone)
        {
            throw NightingaleErrors.DeletionDisabled(stream, "Tombstone");
        }

        await Deleting(stream, () => store.TombstoneAsync(stream, Expected(request.ExpectedRevision), context.CancellationToken)).ConfigureAwait(false);
        return new TombstoneResponse();
    }

    private static StreamState Expected(long expectedRevision)
    {
        try
        {
            return StreamState.FromInt64(expectedRevision);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw NightingaleErrors.InvalidArgument("The expected revision is neither a revision nor a named state.");
        }
    }

    private static async Task Deleting(string stream, Func<Task> operation)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (StreamNotFoundException)
        {
            throw NightingaleErrors.StreamNotFound(stream);
        }
        catch (StreamDeletedException)
        {
            throw NightingaleErrors.StreamDeleted(stream);
        }
        catch (RevisionConflictException conflict)
        {
            throw NightingaleErrors.RevisionConflict(conflict);
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
    /// Validates a name for a read: a plain stream, <c>$all</c>, or a virtual stream with a key. Any
    /// other reserved name is invalid.
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

        if (StreamNames.IsReserved(stream) && stream != StreamNames.All && !StreamNames.TryParseVirtual(stream, out _))
        {
            throw NightingaleErrors.InvalidStreamName(stream, "reserved and not $all, $ce-<category> or $et-<event type>");
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

    /// <summary>
    /// A bounded read of a virtual stream: a snapshot up to the refreshed global head, whose bounds
    /// are the stream's own first and last events. A virtual stream with no events reads as empty,
    /// with bounds of zero, never as not found.
    /// </summary>
    private async Task ReadVirtualBounded(VirtualStreamName stream, ReadRequest request, Direction direction, IServerStreamWriter<ReadResponse> responseStream, CancellationToken cancellationToken)
    {
        var head = await tail.RefreshAsync(cancellationToken).ConfigureAwait(false);
        var bounds = await store.VirtualHeadAsync(stream, head, cancellationToken).ConfigureAwait(false);
        await responseStream.WriteAsync(new ReadResponse { Head = new StreamBounds { First = bounds?.First ?? 0, Last = bounds?.Last ?? 0 } }, cancellationToken).ConfigureAwait(false);
        if (bounds is null)
        {
            return;
        }

        var from = request.FromCase switch
        {
            ReadRequest.FromOneofCase.Position => request.Position,
            ReadRequest.FromOneofCase.End => direction == Direction.Backwards ? bounds.Last : bounds.Last + 1,
            _ => direction == Direction.Forwards ? bounds.First : bounds.Last,
        };

        var remaining = request.Count;
        while (remaining > 0)
        {
            if (direction == Direction.Forwards ? from > bounds.Last : from < bounds.First)
            {
                return;
            }

            var pageSize = (int)Math.Min(remaining, PageSize);
            var page = await store.ReadVirtualAsync(stream, direction, from, head, pageSize, cancellationToken).ConfigureAwait(false);
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
    /// A subscription to a virtual stream: the shape of <c>$all</c>'s, over the predicate. The head
    /// it reports is always the stream's own last event, so a quiet category keeps a stable head
    /// while the store moves on, and an advance of the store that brings it nothing is silent.
    /// </summary>
    private async Task SubscribeVirtual(VirtualStreamName stream, ReadRequest request, IServerStreamWriter<ReadResponse> responseStream, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("D");
        var head = request.FromCase == ReadRequest.FromOneofCase.End ? await tail.RefreshAsync(cancellationToken).ConfigureAwait(false) : tail.Head;
        var bounds = await store.VirtualHeadAsync(stream, head, cancellationToken).ConfigureAwait(false);
        var own = bounds?.Last ?? -1;
        var next = request.FromCase switch
        {
            ReadRequest.FromOneofCase.Position => request.Position,
            ReadRequest.FromOneofCase.End => head + 1,
            _ => 0,
        };
        await responseStream.WriteAsync(new ReadResponse { Confirmed = new SubscriptionConfirmed { SubscriptionId = id, Head = own } }, cancellationToken).ConfigureAwait(false);

        var delivered = true;
        while (true)
        {
            var behind = false;
            while (head > 0 && next <= head)
            {
                var page = await store.ReadVirtualAsync(stream, Direction.Forwards, next, head, PageSize, cancellationToken).ConfigureAwait(false);
                if (page.Count == PageSize && !behind)
                {
                    var rest = await store.CountVirtualAsync(stream, page[^1].Position, head, cancellationToken).ConfigureAwait(false);
                    if (rest > 0)
                    {
                        behind = true;
                        await responseStream.WriteAsync(FellBehindAt(own, rest + page.Count, timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
                    }
                }

                foreach (var record in page)
                {
                    await responseStream.WriteAsync(new ReadResponse { Event = record.ToRecordedEvent() }, cancellationToken).ConfigureAwait(false);
                    own = record.Position;
                    delivered = true;
                }

                next = page.Count < PageSize ? head + 1 : page[^1].Position + 1;
            }

            if (delivered)
            {
                await responseStream.WriteAsync(CaughtUpAt(own, timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
                delivered = false;
            }

            head = await tail.WaitForAdvanceAsync(head, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A bounded read of a virtual stream by ordinal: a snapshot of what is numbered, whose bounds
    /// are the lowest and highest ordinal assigned. No head is refreshed, because ordinals are
    /// assigned in atomic batches after commit and every assigned one is readable; what the
    /// sequencer has not reached yet is not in the snapshot. A stream with nothing numbered reads
    /// as empty, with bounds of zero.
    /// </summary>
    private async Task ReadOrdinalBounded(VirtualStreamName stream, ReadRequest request, Direction direction, IServerStreamWriter<ReadResponse> responseStream, CancellationToken cancellationToken)
    {
        var bounds = await store.OrdinalHeadAsync(stream, cancellationToken).ConfigureAwait(false);
        await responseStream.WriteAsync(new ReadResponse { Head = new StreamBounds { First = bounds?.First ?? 0, Last = bounds?.Last ?? 0 } }, cancellationToken).ConfigureAwait(false);
        if (bounds is null)
        {
            return;
        }

        var from = request.FromCase switch
        {
            ReadRequest.FromOneofCase.Position => request.Position,
            ReadRequest.FromOneofCase.End => direction == Direction.Backwards ? bounds.Last : bounds.Last + 1,
            _ => direction == Direction.Forwards ? bounds.First : bounds.Last,
        };

        var remaining = request.Count;
        while (remaining > 0)
        {
            if (direction == Direction.Forwards ? from > bounds.Last : from < bounds.First)
            {
                return;
            }

            var pageSize = (int)Math.Min(remaining, PageSize);
            var page = await store.ReadByOrdinalAsync(stream, direction, from, pageSize, cancellationToken).ConfigureAwait(false);
            foreach (var record in page)
            {
                await responseStream.WriteAsync(new ReadResponse { Event = record.ToRecordedEvent() }, cancellationToken).ConfigureAwait(false);
            }

            remaining -= (ulong)page.Count;
            if (page.Count < pageSize)
            {
                return;
            }

            from = direction == Direction.Forwards ? OrdinalOf(page[^1]) + 1 : OrdinalOf(page[^1]) - 1;
        }
    }

    /// <summary>
    /// A subscription to a virtual stream by ordinal. Every number is an ordinal: the confirmation
    /// carries the highest one assigned, or -1 when none is, and from the end means after that.
    /// The catch-up drains the numbered rows; the live phase waits on the tail like every other
    /// subscription, then looks for newly numbered rows, and while the sequencer is still behind
    /// the head it woke for, looks again on the feed's poll interval rather than waiting for an
    /// advance that a quiet store would never bring. A store advance that brings this
    /// stream nothing is silent, as under global numbering.
    /// </summary>
    private async Task SubscribeOrdinal(VirtualStreamName stream, ReadRequest request, IServerStreamWriter<ReadResponse> responseStream, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("D");
        var observed = tail.Head;
        var bounds = await store.OrdinalHeadAsync(stream, cancellationToken).ConfigureAwait(false);
        var own = bounds?.Last ?? -1;
        var next = request.FromCase switch
        {
            ReadRequest.FromOneofCase.Position => request.Position,
            ReadRequest.FromOneofCase.End => own + 1,
            _ => 0,
        };
        await responseStream.WriteAsync(new ReadResponse { Confirmed = new SubscriptionConfirmed { SubscriptionId = id, Head = own } }, cancellationToken).ConfigureAwait(false);

        var delivered = true;
        while (true)
        {
            var behind = false;
            while (true)
            {
                var page = await store.ReadByOrdinalAsync(stream, Direction.Forwards, next, PageSize, cancellationToken).ConfigureAwait(false);
                if (page.Count == 0)
                {
                    break;
                }

                if (page.Count == PageSize && !behind)
                {
                    // Ordinals are dense, so the distance to the highest assigned one is the count,
                    // holes included.
                    var head = (await store.OrdinalHeadAsync(stream, cancellationToken).ConfigureAwait(false))?.Last ?? own;
                    if (head > OrdinalOf(page[^1]))
                    {
                        behind = true;
                        await responseStream.WriteAsync(FellBehindAt(head, head - next + 1, timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
                    }
                }

                foreach (var record in page)
                {
                    await responseStream.WriteAsync(new ReadResponse { Event = record.ToRecordedEvent() }, cancellationToken).ConfigureAwait(false);
                    own = OrdinalOf(record);
                    delivered = true;
                }

                next = own + 1;
                if (page.Count < PageSize)
                {
                    break;
                }
            }

            if (delivered)
            {
                await responseStream.WriteAsync(CaughtUpAt(own, timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
                delivered = false;
            }

            // Everything up to the observed head is numbered and was just read, so the next thing
            // to wait for is an advance; otherwise the sequencer is behind, and the next look is soon.
            observed = await EventSource.AwaitNumberingAsync(store, tail, observed, timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private static long OrdinalOf(EventRecord record) =>
        record.Ordinal ?? throw new InvalidOperationException("The store returned an event without its ordinal from an ordinal read.");

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
