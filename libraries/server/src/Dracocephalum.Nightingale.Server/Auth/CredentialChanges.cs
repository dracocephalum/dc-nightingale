namespace Dracocephalum.Nightingale.Server.Auth;

/// <summary>What to change on a credential; a part left <see langword="null"/> stays as it is.</summary>
/// <param name="Role">The new role.</param>
/// <param name="Tenant">The new binding: a tenant, or global.</param>
/// <param name="Disabled">Whether the credential is disabled.</param>
public sealed record CredentialChanges(CredentialRole? Role = null, TenantBinding? Tenant = null, bool? Disabled = null);
