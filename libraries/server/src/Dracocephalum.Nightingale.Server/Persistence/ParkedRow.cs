namespace Dracocephalum.Nightingale.Server.Persistence;

/// <summary>
/// A parked message as its row: one per group and event, keyed by the event's global position,
/// carrying its revision and, for a group numbered by ordinal, its ordinal, so it can be
/// addressed by whichever number the group speaks; with why it was parked, how many times it
/// had been tried, and when. A replay moves the row to the outbox.
/// </summary>
public sealed class ParkedRow
{
    /// <summary>Gets or sets the id of the group the message belongs to.</summary>
    public Guid GroupId { get; set; }

    /// <summary>Gets or sets the event's global position.</summary>
    public long Position { get; set; }

    /// <summary>Gets or sets the event's revision within its own stream.</summary>
    public long Revision { get; set; }

    /// <summary>Gets or sets the event's ordinal within the group's virtual stream, when the group is numbered by ordinal.</summary>
    public long? Ordinal { get; set; }

    /// <summary>Gets or sets the event's id.</summary>
    public Guid EventId { get; set; }

    /// <summary>Gets or sets the consumer's reason.</summary>
    public required string Reason { get; set; }

    /// <summary>Gets or sets how many times the event had been delivered.</summary>
    public int Attempts { get; set; }

    /// <summary>Gets or sets when it was parked.</summary>
    public DateTimeOffset ParkedAt { get; set; }
}
