using System.Threading.Channels;

using Dracocephalum.Nightingale.Protocol;
using Dracocephalum.Nightingale.Protocol.V1;
using Grpc.Core;

namespace Dracocephalum.Nightingale.Client;

/// <summary>
/// A stream read in progress. The call starts at once and is pumped into a one-slot channel, so a
/// slow consumer holds the server back rather than the client buffering. The first message decides
/// <see cref="ReadState"/> and <see cref="Head"/> before any event is enumerated; enumerating a
/// stream that has no events throws <see cref="StreamNotFoundException"/>, and the non-throwing
/// way to learn the same thing is to await <see cref="ReadState"/> first. Enumerate once.
/// </summary>
public sealed class ReadStreamResult : IAsyncEnumerable<EventRecord>, IAsyncDisposable
{
    private readonly AsyncServerStreamingCall<ReadResponse> _call;
    private readonly Channel<EventRecord> _events = Channel.CreateBounded<EventRecord>(new BoundedChannelOptions(1) { SingleReader = true, SingleWriter = true });
    private readonly TaskCompletionSource<ReadState> _state = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<StreamHead?> _head = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _pump;

    internal ReadStreamResult(string stream, AsyncServerStreamingCall<ReadResponse> call, CancellationToken cancellationToken)
    {
        Stream = stream;
        _call = call;
        _pump = Pump(cancellationToken);
    }

    /// <summary>Gets the stream name.</summary>
    public string Stream { get; }

    /// <summary>Gets whether the stream exists, known once the first message arrives.</summary>
    public Task<ReadState> ReadState => _state.Task;

    /// <summary>Gets the stream's bounds at the time of the read, or <see langword="null"/> when it has no events.</summary>
    public Task<StreamHead?> Head => _head.Task;

    /// <inheritdoc/>
    public async IAsyncEnumerator<EventRecord> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        if (await ReadState.ConfigureAwait(false) == Client.ReadState.StreamNotFound)
        {
            throw new StreamNotFoundException(Stream);
        }

        await foreach (var record in _events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return record;
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _call.Dispose();
        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Disposal cancels the call; whatever the pump saw was already handed to the consumer
            // through the state, the head, or the channel, so there is nothing left to report here.
        }
    }

    private async Task Pump(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var message in _call.ResponseStream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                switch (message.ContentCase)
                {
                    case ReadResponse.ContentOneofCase.Head:
                        _head.TrySetResult(new StreamHead(message.Head.First, message.Head.Last));
                        _state.TrySetResult(Client.ReadState.Ok);
                        break;
                    case ReadResponse.ContentOneofCase.StreamNotFound:
                        _head.TrySetResult(null);
                        _state.TrySetResult(Client.ReadState.StreamNotFound);
                        break;
                    case ReadResponse.ContentOneofCase.Event:
                        await _events.Writer.WriteAsync(message.Event.ToEventRecord(), cancellationToken).ConfigureAwait(false);
                        break;
                    default:
                        break;
                }
            }

            _events.Writer.TryComplete();
        }
        catch (RpcException exception)
        {
            var mapped = NightingaleErrorMapping.ToException(exception);
            _state.TrySetException(mapped);
            _head.TrySetException(mapped);
            _events.Writer.TryComplete(mapped);
        }
        catch (OperationCanceledException exception)
        {
            _state.TrySetCanceled(exception.CancellationToken);
            _head.TrySetCanceled(exception.CancellationToken);
            _events.Writer.TryComplete(exception);
        }
        finally
        {
            // A read that ends without a first message is a server that broke the contract; the
            // consumer must not wait on it forever.
            _state.TrySetException(new InvalidOperationException("The read ended before the server reported the stream's state."));
            _head.TrySetException(new InvalidOperationException("The read ended before the server reported the stream's state."));
        }
    }
}
