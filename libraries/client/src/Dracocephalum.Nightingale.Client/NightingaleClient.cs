using Dracocephalum.Nightingale.Protocol;
using Dracocephalum.Nightingale.Protocol.V1;
using Grpc.Core;
using Grpc.Net.Client;

namespace Dracocephalum.Nightingale.Client;

/// <summary>
/// The client of a Nightingale server, shaped after the reference event-store client: append to a
/// stream under an expected state, read a stream or $all in either direction, subscribe to either. A failed call surfaces as a
/// domain exception when the server gave a reason, an argument exception when the request was at
/// fault, and the raw call exception otherwise. Thread-safe; one instance per server address.
/// </summary>
public sealed class NightingaleClient : IAsyncDisposable
{
    private readonly GrpcChannel? _ownedChannel;
    private readonly Streams.StreamsClient _streams;

    /// <summary>Initializes a new instance of the <see cref="NightingaleClient"/> class that owns its channel.</summary>
    /// <param name="options">Where the server is.</param>
    public NightingaleClient(NightingaleClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _ownedChannel = GrpcChannel.ForAddress(options.Address);
        _streams = new Streams.StreamsClient(_ownedChannel);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NightingaleClient"/> class over a channel or
    /// invoker the caller owns and disposes, such as an in-process test server's.
    /// </summary>
    /// <param name="invoker">The call invoker.</param>
    public NightingaleClient(CallInvoker invoker)
    {
        ArgumentNullException.ThrowIfNull(invoker);
        _streams = new Streams.StreamsClient(invoker);
    }

    /// <summary>Appends events to a stream atomically under an expected state.</summary>
    /// <param name="stream">The stream name.</param>
    /// <param name="expected">What the caller asserts about the stream.</param>
    /// <param name="events">The events, in order.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The revision and position of the last event written; the next append expects that revision.</returns>
    /// <exception cref="RevisionConflictException">The stream is not in the expected state.</exception>
    /// <exception cref="StreamDeletedException">The stream was deleted.</exception>
    /// <exception cref="ArgumentException">The server rejected the request as invalid.</exception>
    public async Task<AppendResult> AppendToStreamAsync(string stream, StreamState expected, IEnumerable<EventData> events, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(stream);
        ArgumentNullException.ThrowIfNull(events);

        try
        {
            using var call = _streams.Append(cancellationToken: cancellationToken);
            await call.RequestStream.WriteAsync(
                new AppendRequest { Options = new AppendOptions { Stream = stream, ExpectedRevision = expected.ToInt64() } },
                cancellationToken).ConfigureAwait(false);
            foreach (var eventData in events)
            {
                await call.RequestStream.WriteAsync(new AppendRequest { Event = eventData.ToProposedEvent() }, cancellationToken).ConfigureAwait(false);
            }

            await call.RequestStream.CompleteAsync().ConfigureAwait(false);
            var response = await call.ResponseAsync.ConfigureAwait(false);
            return new AppendResult(response.Revision, response.Position);
        }
        catch (RpcException exception)
        {
            throw NightingaleErrorMapping.ToException(exception);
        }
    }

    /// <summary>
    /// Reads a stream. The call starts immediately; the result reports the stream's state and head
    /// before any event and streams the events after.
    /// </summary>
    /// <param name="direction">The direction to read in.</param>
    /// <param name="stream">The stream name.</param>
    /// <param name="from">Where to begin, inclusive, in the reading direction.</param>
    /// <param name="maxCount">The most events to return.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The read in progress.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The count is not positive.</exception>
    public ReadStreamResult ReadStreamAsync(Direction direction, string stream, StreamPosition from, long maxCount = long.MaxValue, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCount);

        var request = new ReadRequest
        {
            Stream = stream,
            Direction = direction == Direction.Backwards ? ReadDirection.Backwards : ReadDirection.Forwards,
            Count = (ulong)maxCount,
        };
        SetFrom(request, from);
        return new ReadStreamResult(stream, _streams.Read(request, cancellationToken: cancellationToken), cancellationToken);
    }

    private static void SetFrom(ReadRequest request, StreamPosition from)
    {
        if (from.IsEnd)
        {
            request.End = new Google.Protobuf.WellKnownTypes.Empty();
        }
        else if (from == StreamPosition.Start)
        {
            request.Start = new Google.Protobuf.WellKnownTypes.Empty();
        }
        else
        {
            request.Position = from.Value;
        }
    }

    private StreamSubscription Subscribe(string stream, StreamPosition from, CancellationToken cancellationToken)
    {
        var request = new ReadRequest { Stream = stream, Direction = ReadDirection.Forwards, Subscription = new SubscriptionOptions() };
        SetFrom(request, from);
        return new StreamSubscription(stream, _streams.Read(request, cancellationToken: cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Reads <c>$all</c>, every event in position order. The head the result reports is the
    /// high-water mark, 0 when there are no events; the result never reports a missing stream.
    /// </summary>
    /// <param name="direction">The direction to read in.</param>
    /// <param name="from">Where to begin, inclusive, in the reading direction.</param>
    /// <param name="maxCount">The most events to return.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The read in progress.</returns>
    public ReadStreamResult ReadAllAsync(Direction direction, StreamPosition from, long maxCount = long.MaxValue, CancellationToken cancellationToken = default) =>
        ReadStreamAsync(direction, StreamNames.All, from, maxCount, cancellationToken);

    /// <summary>
    /// Subscribes to a stream: every event from <paramref name="from"/> to the head, a caught-up
    /// note, then every event as it is appended, until the subscription is disposed. A stream
    /// that does not exist yet is waited for.
    /// </summary>
    /// <param name="stream">The stream name.</param>
    /// <param name="from">Where to begin, inclusive; <see cref="StreamPosition.End"/> for only what comes after.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The subscription in progress.</returns>
    public StreamSubscription SubscribeToStreamAsync(string stream, StreamPosition from, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(stream);
        return Subscribe(stream, from, cancellationToken);
    }

    /// <summary>Subscribes to <c>$all</c>; see <see cref="SubscribeToStreamAsync"/>.</summary>
    /// <param name="from">Where to begin, inclusive; <see cref="StreamPosition.End"/> for only what comes after.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The subscription in progress.</returns>
    public StreamSubscription SubscribeToAllAsync(StreamPosition from, CancellationToken cancellationToken = default) =>
        Subscribe(StreamNames.All, from, cancellationToken);

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _ownedChannel?.Dispose();
        return ValueTask.CompletedTask;
    }
}
