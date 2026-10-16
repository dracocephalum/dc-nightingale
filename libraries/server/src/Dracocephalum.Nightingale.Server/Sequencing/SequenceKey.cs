namespace Dracocephalum.Nightingale.Server.Sequencing;

/// <summary>
/// Which sequence of a store partition a tail follows: the one shared sequence, or a tenant's own
/// under a sequence per tenant. The shared sequence has no tenant.
/// </summary>
/// <param name="TenantId">The id the store keeps for the tenant whose sequence it is, or <see langword="null"/> for the shared sequence.</param>
public readonly record struct SequenceKey(string? TenantId)
{
    /// <summary>Gets the shared sequence: every event of the store partition, whatever its tenant.</summary>
    public static SequenceKey Shared => default;
}
