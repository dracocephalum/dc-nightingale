namespace Dracocephalum.Nightingale.Server.Auth;

/// <summary>The tenant a call works in: one, by its ids, or all tenants, on a read under the wildcard.</summary>
/// <param name="TenantId">The tenant's id, or <see langword="null"/> for all tenants or for the instance's own.</param>
/// <param name="StoreTenantId">The id the store keeps for it, or <see langword="null"/> for all tenants or for the instance's own.</param>
/// <param name="IsWildcard">Whether the scope spans all tenants.</param>
public sealed record TenantScope(Guid? TenantId, string? StoreTenantId, bool IsWildcard = false)
{
    /// <summary>Gets the scope of a call under the wildcard: all tenants.</summary>
    public static TenantScope AllTenants { get; } = new(null, null, true);

    /// <summary>Gets the scope of a call in a host that keeps no tenants: the store's default tenant.</summary>
    public static TenantScope Default { get; } = new(null, null);

    /// <summary>The scope of a tenant known by the id the store keeps for it: a group's, when its runtime is started from its row.</summary>
    /// <param name="storeTenantId">The id the store keeps.</param>
    /// <returns>The scope.</returns>
    public static TenantScope OfStoreTenant(string storeTenantId)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeTenantId);
        return new TenantScope(null, storeTenantId);
    }
}
