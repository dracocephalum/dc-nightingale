namespace Dracocephalum.Nightingale.Server.Persistence;

/// <summary>
/// A persistent-subscription group as its row: keyed by tenant, stream and group name, with the
/// settings as a JSON document of primitives, so the row outlives the domain type's shape, and
/// the checkpoint in the group's numbering.
/// </summary>
public sealed class GroupRow
{
    /// <summary>Gets or sets the tenant.</summary>
    public required string TenantId { get; set; }

    /// <summary>Gets or sets the stream.</summary>
    public required string Stream { get; set; }

    /// <summary>Gets or sets the group name.</summary>
    public required string GroupName { get; set; }

    /// <summary>Gets or sets the settings, as JSON.</summary>
    public required string Settings { get; set; }

    /// <summary>Gets or sets the checkpoint: the last number every event up to which is done, or -1.</summary>
    public long CheckpointPosition { get; set; }

    /// <summary>Gets or sets when the group was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
