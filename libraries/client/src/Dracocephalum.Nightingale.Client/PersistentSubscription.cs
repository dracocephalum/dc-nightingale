using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Dracocephalum.Nightingale.Protocol;
using Dracocephalum.Nightingale.Protocol.V1;
using Grpc.Core;

namespace Dracocephalum.Nightingale.Client;

/// <summary>
/// A consumer connected to a persistent-subscription group. Events arrive through a one-slot
/// channel, each with the number of times it was delivered before, and the consumer answers for
/// every one with <see cref="AckAsync"/> or <see cref="NackAsync"/>; an event left unanswered is
/// redelivered after the group's message timeout. Enumerate the subscription itself for the
/// events, or <see cref="Messages"/> for the confirmation as well; one or the other, once.
/// Disposal ends the connection, and whatever was in flight is redelivered to the next consumer.
/// </summary>
public sealed class PersistentSubscription : IAsyncEnumerable<PersistentSubscriptionMessage.Recorded>, IAsyncDisposable
{
    private readonly AsyncDuplexStreamingCall<PersistentReadRequest, PersistentReadResponse> _call;
    private readonly Channel<PersistentSubscriptionMessage> _messages = Channel.CreateBounded<PersistentSubscriptionMessage>(new BoundedChannelOptions(1) { SingleReader = true, SingleWriter = true });
    private readonly TaskCompletionSource<PersistentSubscriptionMessage.Confirmed> _confirmed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _writing = new(1, 1);
    private readonly Task _pump;

    internal PersistentSubscription(string stream, string group, AsyncDuplexStreamingCall<PersistentReadRequest, PersistentReadResponse> call, CancellationToken cancellationToken)
    {
        Stream = stream;
        Group = group;
        _call = call;
        _pump = Pump(cancellationToken);
    }

    /// <summary>Gets the stream the group is over.</summary>
    public string Stream { get; }

    /// <summary>Gets the group name.</summary>
    public string Group { get; }

    /// <summary>Gets the confirmation, known once the first message arrives.</summary>
    public Task<PersistentSubscriptionMessage.Confirmed> Confirmed => _confirmed.Task;

    /// <summary>Gets every message the server sends, in order.</summary>
    public IAsyncEnumerable<PersistentSubscriptionMessage> Messages => new MessageEnumerable(this);

    /// <summary>Tells the server these events are done.</summary>
    /// <param name="ids">The event ids, at most 2000.</param>
    /// <returns>A task that completes when the acknowledgement is sent.</returns>
    public Task AckAsync(params IEnumerable<Guid> ids) =>
        WriteAsync(new PersistentReadRequest { Ack = new Ack { Ids = { ids.Select(id => id.ToString("D")) } } });

    /// <summary>Tells the server these events could not be processed, and what to do with them.</summary>
    /// <param name="action">Retry, park, or skip.</param>
    /// <param name="reason">Why, kept with a parked message.</param>
    /// <param name="ids">The event ids, at most 2000.</param>
    /// <returns>A task that completes when the refusal is sent.</returns>
    public Task NackAsync(NackAction action, string reason, params IEnumerable<Guid> ids)
    {
        var wire = action switch
        {
            NackAction.Park => Protocol.V1.NackAction.Park,
            NackAction.Skip => Protocol.V1.NackAction.Skip,
            _ => Protocol.V1.NackAction.Retry,
        };
        return WriteAsync(new PersistentReadRequest { Nack = new Nack { Action = wire, Reason = reason ?? string.Empty, Ids = { ids.Select(id => id.ToString("D")) } } });
    }

    /// <inheritdoc/>
    public async IAsyncEnumerator<PersistentSubscriptionMessage.Recorded> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        await foreach (var message in _messages.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (message is PersistentSubscriptionMessage.Recorded recorded)
            {
                yield return recorded;
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
            // Disposal cancels the call; whatever the pump saw was already handed to the consumer.
        }

        _writing.Dispose();
    }

    internal async Task WriteAsync(PersistentReadRequest request)
    {
        await _writing.WaitAsync().ConfigureAwait(false);
        try
        {
            await _call.RequestStream.WriteAsync(request).ConfigureAwait(false);
        }
        catch (RpcException exception)
        {
            throw NightingaleErrorMapping.ToException(exception);
        }
        finally
        {
            _writing.Release();
        }
    }

    private async IAsyncEnumerable<PersistentSubscriptionMessage> ReadMessages([EnumeratorCancellation] CancellationToken cancellationToken)
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
                PersistentSubscriptionMessage? message = response.ContentCase switch
                {
                    PersistentReadResponse.ContentOneofCase.Confirmed => new PersistentSubscriptionMessage.Confirmed(response.Confirmed.SubscriptionId, response.Confirmed.Checkpoint),
                    PersistentReadResponse.ContentOneofCase.Event => new PersistentSubscriptionMessage.Recorded(response.Event.Event.ToEventRecord(), response.Event.RetryCount),
                    _ => null,
                };
                if (message is null)
                {
                    continue;
                }

                if (message is PersistentSubscriptionMessage.Confirmed confirmed)
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

    private sealed class MessageEnumerable(PersistentSubscription subscription) : IAsyncEnumerable<PersistentSubscriptionMessage>
    {
        public IAsyncEnumerator<PersistentSubscriptionMessage> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            subscription.ReadMessages(cancellationToken).GetAsyncEnumerator(cancellationToken);
    }
}
