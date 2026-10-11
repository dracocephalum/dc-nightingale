namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>A sequence the sequencer numbers: the tenant it belongs to, or none for a shared sequence, and the tail that says how far it is committed.</summary>
/// <param name="TenantId">The id the store keeps for the tenant, or <see langword="null"/> for the shared sequence.</param>
/// <param name="Tail">The tail of the sequence.</param>
internal sealed record NumberedSequence(string? TenantId, IStoreTail Tail);
