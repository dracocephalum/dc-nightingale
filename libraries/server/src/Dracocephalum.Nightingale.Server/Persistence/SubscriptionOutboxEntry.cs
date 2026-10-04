namespace Dracocephalum.Nightingale.Server.Persistence;

/// <summary>
/// An event due to be delivered to a group again: a parked event a replay put back. An event is
/// parked or on the outbox, never both.
/// </summary>
public sealed class SubscriptionOutboxEntry
{
    /// <summary>Gets or sets the row's id, its key.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the id of the group the event is due to.</summary>
    public Guid SubscriptionGroupId { get; set; }

    /// <summary>Gets or sets the event's global position, unique within the group.</summary>
    public long Position { get; set; }

    /// <summary>Gets or sets the event's revision within its own stream.</summary>
    public long Revision { get; set; }

    /// <summary>Gets or sets the event's ordinal, when the group is numbered by ordinal.</summary>
    public long? Ordinal { get; set; }

    /// <summary>Gets or sets the event's id.</summary>
    public Guid EventId { get; set; }

    /// <summary>Gets or sets why the event was parked, kept so a second failure keeps the history.</summary>
    public required string Reason { get; set; }

    /// <summary>Gets or sets how many deliveries the event had before it was parked.</summary>
    public int Attempts { get; set; }

    /// <summary>Gets or sets when the event becomes deliverable.</summary>
    public DateTimeOffset DueAt { get; set; }

    /// <summary>Gets or sets when the event was put on the outbox.</summary>
    public DateTimeOffset QueuedAt { get; set; }
}
