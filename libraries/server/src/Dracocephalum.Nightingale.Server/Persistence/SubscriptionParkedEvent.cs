namespace Dracocephalum.Nightingale.Server.Persistence;

/// <summary>
/// An event a group could not process, kept until a replay: one row per group and position,
/// with every number the event has so it can be addressed by whichever one the group speaks.
/// </summary>
public sealed class SubscriptionParkedEvent
{
    /// <summary>Gets or sets the row's id, its key.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the id of the group the event is parked for.</summary>
    public Guid SubscriptionGroupId { get; set; }

    /// <summary>Gets or sets the event's global position, unique within the group.</summary>
    public long Position { get; set; }

    /// <summary>Gets or sets the event's revision within its own stream.</summary>
    public long Revision { get; set; }

    /// <summary>Gets or sets the event's ordinal, when the group is numbered by ordinal.</summary>
    public long? Ordinal { get; set; }

    /// <summary>Gets or sets the event's id.</summary>
    public Guid EventId { get; set; }

    /// <summary>Gets or sets why the event was parked.</summary>
    public required string Reason { get; set; }

    /// <summary>Gets or sets how many deliveries the event had.</summary>
    public int Attempts { get; set; }

    /// <summary>Gets or sets when the event was parked.</summary>
    public DateTimeOffset ParkedAt { get; set; }
}
