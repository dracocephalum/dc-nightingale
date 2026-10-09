namespace Dracocephalum.Nightingale;

/// <summary>A credential as the server describes it; never its password.</summary>
/// <param name="Name">The user name.</param>
/// <param name="Role">The role.</param>
/// <param name="TenantId">The tenant it is bound to, or <see langword="null"/> for a global one.</param>
/// <param name="IsDisabled">Whether it is refused until enabled again.</param>
/// <param name="CreatedAt">When it was created.</param>
/// <param name="PasswordChangedAt">When its password was last set.</param>
/// <param name="LockedUntil">Until when it is refused after too many wrong passwords, or <see langword="null"/>.</param>
public sealed record CredentialInfo(string Name, CredentialRole Role, Guid? TenantId, bool IsDisabled, DateTimeOffset CreatedAt, DateTimeOffset PasswordChangedAt, DateTimeOffset? LockedUntil);
