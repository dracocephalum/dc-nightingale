using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Dracocephalum.Nightingale.Protocol;
using Dracocephalum.Nightingale.Protocol.V1;
using Grpc.Core;

namespace Dracocephalum.Nightingale.Client;

/// <summary>
/// A subscription in progress. The call starts at once and is pumped into a one-slot channel, so
/// a slow consumer holds the server back rather than the client buffering. Enumerating the
/// subscription itself yields the events; enumerating <see cref="Messages"/> yields everything
/// the server says, the confirmation first, then events with the caught-up and fell-behind notes
/// between them. Enumerate one or the other, once. <see cref="Confirmed"/> completes with the
/// first message either way. Disposal cancels the call; so does leaving the enumeration.
/// </summary>
public sealed class StreamSubscription : IAsyncEnumerable<EventRecord>, IAsyncDisposable
{
    private readonly AsyncServerStreamingCall<ReadResponse> _call;
    private readonly Channel<SubscriptionMessage> _messages = Channel.CreateBounded<SubscriptionMessage>(new BoundedChannelOptions(1) { SingleReader = true, SingleWriter = true });
    private readonly TaskCompletionSource<SubscriptionMessage.Confirmed> _confirmed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _pump;

    internal StreamSubscription(string stream, AsyncServerStreamingCall<ReadResponse> call, CancellationToken cancellationToken)
    {
        Stream = stream;
        _call = call;
        _pump = Pump(cancellationToken);
    }

    /// <summary>Gets the stream name, or <c>$all</c>.</summary>
    public string Stream { get; }

    /// <summary>Gets the confirmation, known once the first message arrives.</summary>
    public Task<SubscriptionMessage.Confirmed> Confirmed => _confirmed.Task;

    /// <summary>Gets every message the server sends, in order.</summary>
    public IAsyncEnumerable<SubscriptionMessage> Messages => new MessageEnumerable(this);

    /// <inheritdoc/>
    public async IAsyncEnumerator<EventRecord> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        await foreach (var message in _messages.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (message is SubscriptionMessage.Recorded recorded)
            {
                yield return recorded.Record;
            }
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
            // through the confirmation or the channel, so there is nothing left to report here.
        }
    }

    private static SubscriptionMessage? ToMessage(ReadResponse message) => message.ContentCase switch
    {
        ReadResponse.ContentOneofCase.Confirmed => new SubscriptionMessage.Confirmed(message.Confirmed.SubscriptionId, message.Confirmed.Head),
        ReadResponse.ContentOneofCase.Event => new SubscriptionMessage.Recorded(message.Event.ToEventRecord()),
        ReadResponse.ContentOneofCase.CaughtUp => new SubscriptionMessage.CaughtUp(message.CaughtUp.Head, message.CaughtUp.At.ToDateTimeOffset()),
        ReadResponse.ContentOneofCase.FellBehind => new SubscriptionMessage.FellBehind(message.FellBehind.Head, message.FellBehind.EventsBehind, message.FellBehind.At.ToDateTimeOffset()),
        ReadResponse.ContentOneofCase.Checkpoint => new SubscriptionMessage.Checkpoint(message.Checkpoint.Position, message.Checkpoint.At.ToDateTimeOffset()),
        _ => null,
    };

    private async IAsyncEnumerable<SubscriptionMessage> ReadMessages([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var message in _messages.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return message;
        }
    }

    private async Task Pump(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var response in _call.ResponseStream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var message = ToMessage(response);
                if (message is null)
                {
                    continue;
                }

                if (message is SubscriptionMessage.Confirmed confirmed)
                {
                    _confirmed.TrySetResult(confirmed);
                }

                await _messages.Writer.WriteAsync(message, cancellationToken).ConfigureAwait(false);
            }

            _messages.Writer.TryComplete();
        }
        catch (RpcException exception)
        {
            var mapped = NightingaleErrorMapping.ToException(exception);
            _confirmed.TrySetException(mapped);
            _messages.Writer.TryComplete(mapped);
        }
        catch (OperationCanceledException exception)
        {
            _confirmed.TrySetCanceled(exception.CancellationToken);
            _messages.Writer.TryComplete(exception);
        }
        finally
        {
            _confirmed.TrySetException(new InvalidOperationException("The subscription ended before the server confirmed it."));
        }
    }

    private sealed class MessageEnumerable(StreamSubscription subscription) : IAsyncEnumerable<SubscriptionMessage>
    {
        public IAsyncEnumerator<SubscriptionMessage> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            subscription.ReadMessages(cancellationToken).GetAsyncEnumerator(cancellationToken);
    }
}
