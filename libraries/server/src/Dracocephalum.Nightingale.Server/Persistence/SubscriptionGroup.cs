namespace Dracocephalum.Nightingale.Server.Persistence;

/// <summary>
/// A persistent-subscription group as its row: found by tenant, stream and name, which are
/// unique together, and known by its id from then on. The settings are a JSON document of
/// primitives, so the row outlives the domain type's shape; the checkpoint is in the group's
/// numbering.
/// </summary>
public sealed class SubscriptionGroup
{
    /// <summary>Gets or sets the group's id, its key.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the tenant.</summary>
    public required string TenantId { get; set; }

    /// <summary>Gets or sets the stream the group reads.</summary>
    public required string Stream { get; set; }

    /// <summary>Gets or sets the group's name, unique per tenant and stream.</summary>
    public required string Name { get; set; }

    /// <summary>Gets or sets the settings, as JSON.</summary>
    public required string Settings { get; set; }

    /// <summary>Gets or sets the checkpoint: the last number every event up to which is done, or -1.</summary>
    public long CheckpointPosition { get; set; }

    /// <summary>Gets or sets when the group was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
