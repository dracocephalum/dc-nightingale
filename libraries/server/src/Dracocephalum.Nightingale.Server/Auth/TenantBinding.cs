namespace Dracocephalum.Nightingale.Server.Auth;

/// <summary>A credential's tenant, as a change: bound to one tenant, or global.</summary>
/// <param name="TenantId">The tenant, or <see langword="null"/> for a global credential.</param>
public readonly record struct TenantBinding(Guid? TenantId)
{
    /// <summary>Gets the binding of a global credential.</summary>
    public static TenantBinding Global => default;
}
