namespace Dracocephalum.Nightingale;

/// <summary>
/// What a subscription delivers, in order: the confirmation first, then events, with the server's
/// progress notes between them. A consumer that only wants the events enumerates the subscription
/// itself; one that tracks its own progress enumerates the messages.
/// </summary>
public abstract record SubscriptionMessage
{
    private SubscriptionMessage()
    {
    }

    /// <summary>The first message: the server accepted the subscription.</summary>
    /// <param name="SubscriptionId">The server's id for this subscription.</param>
    /// <param name="Head">The head at that moment: the last revision of a stream, or -1 when it has no events yet;
    /// the high-water mark of <c>$all</c>, 0 when there are no events yet.</param>
    public sealed record Confirmed(string SubscriptionId, long Head) : SubscriptionMessage;

    /// <summary>An event, delivered in order.</summary>
    /// <param name="Record">The event.</param>
    public sealed record Recorded(EventRecord Record) : SubscriptionMessage;

    /// <summary>Every event up to the head at that moment has been delivered; what follows is live.</summary>
    /// <param name="Head">The head at that moment.</param>
    /// <param name="At">When the server noted it.</param>
    public sealed record CaughtUp(long Head, DateTimeOffset At) : SubscriptionMessage;

    /// <summary>The stream grew faster than this subscription consumed; a <see cref="CaughtUp"/> follows once it has caught up again.</summary>
    /// <param name="Head">The head at that moment.</param>
    /// <param name="EventsBehind">How many events lie between the last delivered one and the head.</param>
    /// <param name="At">When the server noted it.</param>
    public sealed record FellBehind(long Head, long EventsBehind, DateTimeOffset At) : SubscriptionMessage;

    /// <summary>The position the server has read to without an event to deliver, so a checkpoint can advance.</summary>
    /// <param name="Position">The position.</param>
    /// <param name="At">When the server noted it.</param>
    public sealed record Checkpoint(long Position, DateTimeOffset At) : SubscriptionMessage;
}
