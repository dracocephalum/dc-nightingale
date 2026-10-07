namespace Dracocephalum.Nightingale.Server.Data;

/// <summary>
/// A credential as its row: a name, a password the row holds only as a salted, peppered, slow
/// hash with its parameters, a role, the tenant it is bound to or none, and what the server
/// keeps about failures. The built-in administrator is not a row: its password is the host's
/// configuration, so that nothing done through the API can lock everyone out.
/// </summary>
public sealed class Credential
{
    /// <summary>Gets or sets the id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the user name, unique under the database's collation.</summary>
    public required string Name { get; set; }

    /// <summary>Gets or sets the password's hash, in PHC string form with its parameters and the pepper's id.</summary>
    public required string PasswordHash { get; set; }

    /// <summary>Gets or sets the role; stored by name.</summary>
    public CredentialRole Role { get; set; }

    /// <summary>Gets or sets the tenant the credential is bound to, or <see langword="null"/> for a global one.</summary>
    public Guid? TenantId { get; set; }

    /// <summary>Gets or sets a value indicating whether the credential is refused until enabled again.</summary>
    public bool IsDisabled { get; set; }

    /// <summary>Gets or sets a value that changes whenever the password, the role, the tenant or the disabled flag does, so that what an instance cached is dropped.</summary>
    public Guid SecurityStamp { get; set; }

    /// <summary>Gets or sets how many times in a row the password was wrong.</summary>
    public int FailedAttempts { get; set; }

    /// <summary>Gets or sets until when the credential is refused after too many wrong passwords, or <see langword="null"/>.</summary>
    public DateTimeOffset? LockedUntil { get; set; }

    /// <summary>Gets or sets when the password was last set.</summary>
    public DateTimeOffset PasswordChangedAt { get; set; }

    /// <summary>Gets or sets when the credential was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Gets or sets when the row last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
