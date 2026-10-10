namespace Dracocephalum.Nightingale.Server.Auth;

/// <summary>A credential to make.</summary>
/// <param name="Name">The user name.</param>
/// <param name="Password">The password as typed; it is hashed on the way in.</param>
/// <param name="Role">The role.</param>
/// <param name="TenantId">The tenant it is bound to, or <see langword="null"/> for a global credential.</param>
public sealed record NewCredential(string Name, string Password, CredentialRole Role, Guid? TenantId);
