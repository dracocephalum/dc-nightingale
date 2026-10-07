namespace Dracocephalum.Nightingale.Server.Auth;

/// <summary>What a call does with a tenant's data, which decides whether the wildcard tenant is admitted.</summary>
public enum TenantAccess
{
    /// <summary>Reads or subscribes; the wildcard spans every tenant.</summary>
    Read = 0,

    /// <summary>Appends, deletes or manages; one tenant, named.</summary>
    Write = 1,
}
