namespace Dracocephalum.Nightingale.Server.Persistence;

/// <summary>
/// A parked message as its row: one per group and event, keyed by the event's global position,
/// carrying its revision and, for a group numbered by ordinal, its ordinal, so it can be
/// addressed by whichever number the group speaks; with why it was parked, how many times it
/// had been tried, when, and whether it is marked for replay.
/// </summary>
public sealed class ParkedRow
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

    /// <summary>Gets or sets the consumer's reason.</summary>
    public required string Reason { get; set; }

    /// <summary>Gets or sets how many times the event had been delivered.</summary>
    public int Attempts { get; set; }

    /// <summary>Gets or sets when it was parked.</summary>
    public DateTimeOffset ParkedAt { get; set; }

    /// <summary>Gets or sets a value indicating whether it is marked for replay.</summary>
    public bool Replay { get; set; }
}
