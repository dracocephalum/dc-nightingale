namespace Dracocephalum.Nightingale.Server.Auth;

/// <summary>
/// Who a call is from, once its credentials were accepted: a name, a role, the tenant the
/// credential is bound to or none, and the scheme it came in under. Authorization sees the role
/// and the tenant only; how the caller was authenticated is the handler's business.
/// </summary>
/// <param name="Name">The user name.</param>
/// <param name="Role">The role.</param>
/// <param name="TenantId">The tenant the credential is bound to, or <see langword="null"/> for a global one.</param>
/// <param name="Scheme">The authorization scheme the credentials came in under, or <c>none</c> when authentication is off.</param>
public sealed record NightingalePrincipal(string Name, CredentialRole Role, Guid? TenantId, string Scheme)
{
    /// <summary>Gets the caller every call is from while authentication is off: the built-in administrator, global.</summary>
    public static NightingalePrincipal Anonymous { get; } = new(UserCredentials.AdminUserName, CredentialRole.Admin, null, "none");

    /// <summary>Whether the role is at least the one asked.</summary>
    /// <param name="required">The role a call needs.</param>
    /// <returns>True when the principal's role includes it.</returns>
    public bool IsAtLeast(CredentialRole required) => Role >= required;
}
