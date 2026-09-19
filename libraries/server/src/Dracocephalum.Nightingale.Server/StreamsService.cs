using Dracocephalum.Nightingale.Protocol;
using Dracocephalum.Nightingale.Protocol.V1;
using Grpc.Core;

namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// The <c>Streams</c> service over an <see cref="IStreamStore"/>. It validates requests, keeps the
/// contract's message order on a read, and translates the store's domain exceptions into the
/// contract's errors. It knows nothing about the backend: revisions and positions arrive from the
/// store already in the contract's numbering. This slice serves plain streams only; <c>$all</c>, the
/// virtual streams and subscriptions answer with an unimplemented status until their slices land.
/// </summary>
/// <param name="store">The backend.</param>
public sealed class StreamsService(IStreamStore store) : Streams.StreamsBase
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

        var stream = PlainStreamName(request.Stream);
        if (request.Filter is not null)
        {
            throw NightingaleErrors.FilterNotAllowed(stream);
        }

        if (request.ModeCase == ReadRequest.ModeOneofCase.Subscription)
        {
            throw NightingaleErrors.NotImplemented("Subscribing");
        }

        if (request.ModeCase != ReadRequest.ModeOneofCase.Count || request.Count == 0)
        {
            throw NightingaleErrors.InvalidArgument("A read needs a positive count.");
        }

        var direction = request.Direction == ReadDirection.Backwards ? Direction.Backwards : Direction.Forwards;
        long? from = request.FromCase switch
        {
            ReadRequest.FromOneofCase.Position when request.Position < 0 =>
                throw NightingaleErrors.InvalidArgument("A position is never negative."),
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
            var page = await ReadPage(stream, direction, from, pageSize, context.CancellationToken).ConfigureAwait(false);
            if (page is null)
            {
                await responseStream.WriteAsync(new ReadResponse { StreamNotFound = new StreamNotFound { Stream = stream } }, context.CancellationToken).ConfigureAwait(false);
                return;
            }

            if (first)
            {
                await responseStream.WriteAsync(new ReadResponse { Head = new StreamBounds { First = page.Head.First, Last = page.Head.Last } }, context.CancellationToken).ConfigureAwait(false);
                first = false;
            }

            foreach (var record in page.Events)
            {
                await responseStream.WriteAsync(new ReadResponse { Event = record.ToRecordedEvent() }, context.CancellationToken).ConfigureAwait(false);
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
    /// served by later slices, so they are refused as not implemented rather than as invalid.
    /// </summary>
    private static string PlainStreamName(string stream)
    {
        if (string.IsNullOrEmpty(stream))
        {
            throw NightingaleErrors.InvalidStreamName(stream, "empty");
        }

        if (stream.Length > MaxStreamNameLength)
        {
            throw NightingaleErrors.InvalidStreamName(stream, $"longer than {MaxStreamNameLength} characters");
        }

        if (stream[0] == '$')
        {
            throw NightingaleErrors.NotImplemented("Reading $all and the virtual streams");
        }

        return stream;
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
