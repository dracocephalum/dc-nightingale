namespace Dracocephalum.Nightingale.Server.Auth;

/// <summary>The one tenant whose data this instance serves, by the id the store keeps for it, until the stores take a tenant per call.</summary>
/// <param name="StoreTenantId">The id the store keeps on the tenant's rows.</param>
public sealed record StoreTenant(string StoreTenantId);
