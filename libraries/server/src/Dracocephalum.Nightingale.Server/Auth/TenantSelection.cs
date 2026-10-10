namespace Dracocephalum.Nightingale.Server.Auth;

/// <summary>
/// The tenant a call named, as its transport read it: none, all tenants, or one. A global
/// credential names one on every data call; a tenant-bound credential may name its own or none.
/// How a call carries it, a header or a field, is the transport's; what it means is not.
/// </summary>
/// <param name="TenantId">The tenant named, or <see langword="null"/>.</param>
/// <param name="IsWildcard">Whether the call named all tenants, which a read may.</param>
public readonly record struct TenantSelection(Guid? TenantId, bool IsWildcard)
{
    /// <summary>Gets the selection of a call that named no tenant.</summary>
    public static TenantSelection None => default;

    /// <summary>Gets the selection of a call that named all tenants.</summary>
    public static TenantSelection Wildcard => new(null, true);

    /// <summary>Gets a value indicating whether the call named nothing.</summary>
    public bool IsNone => TenantId is null && !IsWildcard;
}
