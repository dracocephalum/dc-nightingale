using System.Threading.Channels;

namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// One consumer connected to a running group: where its events go, how many delivered events it
/// may hold at once, and how many it holds. Made by the group when the consumer joins and
/// released when it leaves; the group counts its room and the service pumps its channel.
/// </summary>
internal sealed class SubscriptionConsumer
{
    internal SubscriptionConsumer(int bufferSize, string? address, DateTimeOffset connectedAt)
    {
        BufferSize = bufferSize;
        Address = address;
        ConnectedAt = connectedAt;
        Outgoing = Channel.CreateUnbounded<PersistentSubscriptionMessage.Recorded>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    }

    /// <summary>Gets the consumer's id within the group, for the life of its connection.</summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>Gets how many delivered, unacknowledged events the consumer holds at once.</summary>
    public int BufferSize { get; }

    /// <summary>Gets where the consumer connected from, as the server saw it.</summary>
    public string? Address { get; }

    /// <summary>Gets when the consumer connected.</summary>
    public DateTimeOffset ConnectedAt { get; }

    /// <summary>Gets the events to send to the consumer, in delivery order; completed, with the cause, when the group has no more for it.</summary>
    public Channel<PersistentSubscriptionMessage.Recorded> Outgoing { get; }

    /// <summary>Gets or sets how many events are delivered to this consumer and neither acknowledged nor refused yet. The group's, under its lock.</summary>
    internal int InFlightCount { get; set; }

    /// <summary>Gets a value indicating whether the consumer can take one more event.</summary>
    internal bool HasRoom => InFlightCount < BufferSize;
}
