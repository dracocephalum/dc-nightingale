namespace Dracocephalum.Nightingale;

/// <summary>
/// What a persistent subscription delivers: the confirmation first, then events, each with the
/// number of times it was delivered before.
/// </summary>
public abstract record PersistentSubscriptionMessage
{
    private PersistentSubscriptionMessage()
    {
    }

    /// <summary>The first message: the server accepted the consumer.</summary>
    /// <param name="SubscriptionId">The server's id for this connection.</param>
    /// <param name="Checkpoint">The group's checkpoint at that moment, or -1 when nothing is acknowledged yet.</param>
    public sealed record Confirmed(string SubscriptionId, long Checkpoint) : PersistentSubscriptionMessage;

    /// <summary>An event to process, then acknowledge or refuse.</summary>
    /// <param name="Record">The event.</param>
    /// <param name="RetryCount">How many times it was delivered before this time; 0 on the first delivery.</param>
    public sealed record Recorded(EventRecord Record, int RetryCount) : PersistentSubscriptionMessage;
}
