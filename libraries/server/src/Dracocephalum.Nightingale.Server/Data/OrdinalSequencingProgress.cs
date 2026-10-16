namespace Dracocephalum.Nightingale.Server.Data;

/// <summary>
/// How far the ordinal sequencer has come over one sequence: the position every event up to which
/// has its ordinals, both of them, since one batch assigns the two together. One row per sequence:
/// the one row of a shared sequence, whose tenant is <see langword="null"/>, or a tenant's own
/// under a sequence per tenant.
/// </summary>
public sealed class OrdinalSequencingProgress
{
    /// <summary>Gets or sets the row's id, its key.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the id the store keeps for the tenant whose sequence it is, or <see langword="null"/> for the shared sequence; unique, the shared sequence's included.</summary>
    public string? TenantId { get; set; }

    /// <summary>Gets or sets the position reached.</summary>
    public long Position { get; set; }

    /// <summary>
    /// Gets or sets why the sequencer is failing to number this sequence, once it has failed
    /// again and again; <see langword="null"/> while it numbers. Every instance reads it, so a read
    /// by ordinal is refused the same way wherever it is made.
    /// </summary>
    public string? Fault { get; set; }

    /// <summary>Gets or sets when the sequence faulted; <see langword="null"/> while it numbers.</summary>
    public DateTimeOffset? FaultedAt { get; set; }
}
