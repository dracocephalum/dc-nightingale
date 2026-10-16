namespace Dracocephalum.Nightingale.Server.Data;

/// <summary>
/// A sequence head as the tails keep it: the position up to which every event of the sequence is
/// final, committed or never to be, and when a tail last looked. One row per sequence: the one row
/// of a shared sequence, whose tenant is <see langword="null"/>, or a tenant's own under a sequence
/// per tenant. Every instance raises it and none lowers it, since a position one instance gave up
/// can never commit; a reader of a replica takes it as the bound of what the replica holds, and a
/// tail that starts takes it as where to begin. The time it was checked moves at every look, so a
/// head that has caught up is told from one nobody keeps any more.
/// </summary>
public sealed class SequenceHead
{
    /// <summary>Gets or sets the row's id, its key.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the id the store keeps for the tenant whose sequence it is, or <see langword="null"/> for the shared sequence; unique, the shared sequence's included.</summary>
    public string? TenantId { get; set; }

    /// <summary>Gets or sets the position up to which every event of the sequence is final.</summary>
    public long Position { get; set; }

    /// <summary>Gets or sets when a tail last looked at the sequence.</summary>
    public DateTimeOffset CheckedAt { get; set; }
}
