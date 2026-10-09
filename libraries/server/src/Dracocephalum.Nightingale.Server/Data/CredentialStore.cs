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

    /// <inheritdoc/>
    public async Task CreateAsync(Credential credential, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (await context.Credentials.AnyAsync(row => row.Name == credential.Name, cancellationToken).ConfigureAwait(false))
        {
            throw new CredentialExistsException($"A credential named '{credential.Name}' exists.");
        }

        context.Credentials.Add(credential);
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception)
        {
            throw new CredentialExistsException($"A credential named '{credential.Name}' exists.", exception);
        }
    }

    /// <inheritdoc/>
    public async Task<Credential?> UpdateAsync(Guid id, CredentialRole? role, Guid? tenantId, bool? disabled, string? passwordHash, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await context.Credentials.SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var now = time.GetUtcNow();
        if (role is { } newRole)
        {
            row.Role = newRole;
        }

        if (tenantId is { } tenant)
        {
            row.TenantId = tenant == Guid.Empty ? null : tenant;
        }

        if (disabled is { } flag)
        {
            row.IsDisabled = flag;
        }

        if (passwordHash is not null)
        {
            row.PasswordHash = passwordHash;
            row.PasswordChangedAt = now;
            row.FailedAttempts = 0;
            row.LockedUntil = null;
        }

        row.SecurityStamp = Guid.NewGuid();
        row.UpdatedAt = now;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return row;
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await context.Credentials.SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return false;
        }

        context.Credentials.Remove(row);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Credential>> ListAsync(Guid? tenantId, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = context.Credentials.AsNoTracking();
        if (tenantId is { } tenant)
        {
            query = query.Where(row => row.TenantId == tenant);
        }

        return await query.OrderBy(row => row.Name).ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}
