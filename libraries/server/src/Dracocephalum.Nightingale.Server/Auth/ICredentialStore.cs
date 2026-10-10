using Dracocephalum.Nightingale.Server.Data;

namespace Dracocephalum.Nightingale.Server.Auth;

/// <summary>The credentials table: a row by name and the failure bookkeeping for authentication, and the writes for management.</summary>
public interface ICredentialStore
{
    /// <summary>Finds a credential by name, under the database's rule for which names are the same.</summary>
    /// <param name="name">The user name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The row, or <see langword="null"/>.</returns>
    Task<Credential?> FindAsync(string name, CancellationToken cancellationToken);

    /// <summary>Counts a wrong password, and locks the credential out when the count reaches the threshold.</summary>
    /// <param name="id">The credential's id.</param>
    /// <param name="threshold">How many wrong passwords in a row lock it out; 0 for never.</param>
    /// <param name="lockout">For how long.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the row is written.</returns>
    Task RecordFailureAsync(Guid id, int threshold, TimeSpan lockout, CancellationToken cancellationToken);

    /// <summary>Clears the failure count after a right password, and stores a remade hash when the parameters moved on.</summary>
    /// <param name="id">The credential's id.</param>
    /// <param name="rehashed">The hash under the current parameters, or <see langword="null"/> to keep the stored one.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the row is written.</returns>
    Task RecordSuccessAsync(Guid id, string? rehashed, CancellationToken cancellationToken);

    /// <summary>Creates a credential.</summary>
    /// <param name="credential">The row, hash included.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the row is written.</returns>
    /// <exception cref="CredentialExistsException">The name is taken.</exception>
    Task CreateAsync(Credential credential, CancellationToken cancellationToken);

    /// <summary>Changes a credential's role, tenant, disabled flag or hash; a value left <see langword="null"/> keeps the stored one. The security stamp moves.</summary>
    /// <param name="id">The credential's id.</param>
    /// <param name="role">The role, or <see langword="null"/>.</param>
    /// <param name="tenantId">The tenant, <see cref="Guid.Empty"/> for global, or <see langword="null"/> to keep.</param>
    /// <param name="disabled">The flag, or <see langword="null"/>.</param>
    /// <param name="passwordHash">A new hash, which also clears the lockout, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The row as it now is, or <see langword="null"/> when there is no such credential.</returns>
    Task<Credential?> UpdateAsync(Guid id, CredentialRole? role, Guid? tenantId, bool? disabled, string? passwordHash, CancellationToken cancellationToken);

    /// <summary>Deletes a credential.</summary>
    /// <param name="id">The credential's id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True when a row was deleted.</returns>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Lists credentials by name: all of them, or those bound to one tenant.</summary>
    /// <param name="tenantId">The tenant, or <see langword="null"/> for every credential.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The rows.</returns>
    Task<IReadOnlyList<Credential>> ListAsync(Guid? tenantId, CancellationToken cancellationToken);
}
