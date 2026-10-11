namespace Dracocephalum.Nightingale.Server.Data;

/// <summary>
/// How far the sequencer has come: the position every event up to which has its ordinals. One
/// row per sequence, found by its name: one for a shared sequence, one per tenant under a
/// sequence per tenant.
/// </summary>
public sealed class SequencerProgress
{
    /// <summary>The name of the sequencer's row over a shared sequence.</summary>
    public const string Ordinals = "Ordinals";

    /// <summary>Gets or sets the row's id, its key.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the process the row belongs to; unique.</summary>
    public required string Name { get; set; }

    /// <summary>Gets or sets the position reached.</summary>
    public long Position { get; set; }

    /// <summary>The name of the sequencer's row for a sequence: the one row of a shared sequence, or a tenant's own under a sequence per tenant.</summary>
    /// <param name="storeTenantId">The id the store keeps for the tenant whose sequence it is, or <see langword="null"/> for a shared sequence.</param>
    /// <returns>The name.</returns>
    public static string OrdinalsOf(string? storeTenantId) => storeTenantId is null ? Ordinals : Ordinals + ":" + storeTenantId;
}
