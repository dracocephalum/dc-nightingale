using Dracocephalum.Nightingale.Server.Data;

namespace Dracocephalum.Nightingale.Server.Auth;

/// <summary>What authentication needs from the credentials table: a row by name, and the failure bookkeeping.</summary>
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
}
