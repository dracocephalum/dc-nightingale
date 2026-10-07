using Dracocephalum.Nightingale.Server.Auth;
using Microsoft.EntityFrameworkCore;

namespace Dracocephalum.Nightingale.Server.Data;

/// <summary>The credentials table, through the gateway's own context.</summary>
/// <param name="contexts">Makes a context per call.</param>
/// <param name="time">The clock.</param>
public sealed class CredentialStore(IDbContextFactory<NightingaleDbContext> contexts, TimeProvider time) : ICredentialStore
{
    /// <inheritdoc/>
    public async Task<Credential?> FindAsync(string name, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.Credentials.AsNoTracking().SingleOrDefaultAsync(row => row.Name == name, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task RecordFailureAsync(Guid id, int threshold, TimeSpan lockout, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await context.Credentials.SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return;
        }

        var now = time.GetUtcNow();
        row.FailedAttempts++;
        if (threshold > 0 && row.FailedAttempts >= threshold)
        {
            row.LockedUntil = now + lockout;
            row.FailedAttempts = 0;
        }

        row.UpdatedAt = now;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task RecordSuccessAsync(Guid id, string? rehashed, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await context.Credentials.SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken).ConfigureAwait(false);
        if (row is null || (row.FailedAttempts == 0 && row.LockedUntil is null && rehashed is null))
        {
            return;
        }

        row.FailedAttempts = 0;
        row.LockedUntil = null;
        if (rehashed is not null)
        {
            row.PasswordHash = rehashed;
        }

        row.UpdatedAt = time.GetUtcNow();
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
