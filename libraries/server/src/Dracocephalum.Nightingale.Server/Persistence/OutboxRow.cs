namespace Dracocephalum.Nightingale.Server.Persistence;

/// <summary>
/// A message on a group's outbox as its row: due to be delivered again, keyed like a parked
/// message by the event's global position and carrying the same numbers, with when it is due
/// and when it was put there. A replay moves a parked row here; a delivery that fails again
/// moves it back.
/// </summary>
public sealed class OutboxRow
{
    /// <summary>Gets or sets the tenant.</summary>
    public required string TenantId { get; set; }

    /// <summary>Gets or sets the stream.</summary>
    public required string Stream { get; set; }

    /// <summary>Gets or sets the group name.</summary>
    public required string GroupName { get; set; }

    /// <summary>Gets or sets the event's global position.</summary>
    public long Position { get; set; }

    /// <summary>Gets or sets the event's revision within its own stream.</summary>
    public long Revision { get; set; }

    /// <summary>Gets or sets the event's ordinal within the group's virtual stream, when the group is numbered by ordinal.</summary>
    public long? Ordinal { get; set; }

    /// <summary>Gets or sets the event's id.</summary>
    public Guid EventId { get; set; }

    /// <summary>Gets or sets why it was parked.</summary>
    public required string Reason { get; set; }

    /// <summary>Gets or sets how many times the event had been delivered.</summary>
    public int Attempts { get; set; }

    /// <summary>Gets or sets when it becomes deliverable.</summary>
    public DateTimeOffset DueAt { get; set; }

    /// <summary>Gets or sets when it was put on the outbox.</summary>
    public DateTimeOffset QueuedAt { get; set; }
}
