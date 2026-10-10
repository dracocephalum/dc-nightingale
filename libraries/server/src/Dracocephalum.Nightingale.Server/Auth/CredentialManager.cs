using System.Text;

using Dracocephalum.Nightingale.Server.Data;

namespace Dracocephalum.Nightingale.Server.Auth;

/// <summary>
/// Manages credentials, for whichever transport carries the request. Every call but the last
/// needs the admin role; a tenant-bound admin sees and changes its own tenant's credentials
/// only, and nobody gives a credential a role above their own or a tenant other than their own.
/// A change drops what this instance remembered of the credential; the other instances drop it
/// when their memory lapses. A refusal is one of the shared exceptions; a request that is not
/// well formed is an <see cref="ArgumentException"/> that says what is wrong with it.
/// </summary>
/// <param name="credentials">The credentials table.</param>
/// <param name="hasher">Makes and checks hashes.</param>
/// <param name="options">The host's options, for the password rule.</param>
/// <param name="time">The clock.</param>
/// <param name="authorizer">Checks the caller's role and that its tenant is live.</param>
/// <param name="basic">Where this instance remembers verified credentials; none in a host without authentication.</param>
public sealed class CredentialManager(ICredentialStore credentials, PasswordHasher hasher, NightingaleOptionsBase options, TimeProvider time, Authorizer authorizer, BasicAuthenticator? basic = null)
{
    private const string AdminPasswordIsConfiguration = "The built-in administrator's password is the server's configuration.";

    /// <summary>Creates a credential.</summary>
    /// <param name="caller">The caller, an admin.</param>
    /// <param name="request">The credential to make.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The row.</returns>
    /// <exception cref="CredentialExistsException">The name is taken.</exception>
    public async Task<Credential> CreateAsync(NightingalePrincipal caller, NewCredential request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        await authorizer.RequireRoleAsync(caller, CredentialRole.Admin, cancellationToken).ConfigureAwait(false);
        var name = RequireName(request.Name);
        RequirePassword(request.Password);
        RequireWithin(caller, request.Role, request.TenantId);

        var now = time.GetUtcNow();
        var row = new Credential
        {
            Id = Guid.NewGuid(),
            Name = name,
            PasswordHash = hasher.Hash(request.Password),
            Role = request.Role,
            TenantId = request.TenantId,
            SecurityStamp = Guid.NewGuid(),
            PasswordChangedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await credentials.CreateAsync(row, cancellationToken).ConfigureAwait(false);
        return row;
    }

    /// <summary>Changes a credential's role, tenant or disabled flag.</summary>
    /// <param name="caller">The caller, an admin.</param>
    /// <param name="name">The credential's name.</param>
    /// <param name="changes">What to change.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The row as it now is.</returns>
    /// <exception cref="CredentialNotFoundException">No such credential among those the caller may manage.</exception>
    public async Task<Credential> UpdateAsync(NightingalePrincipal caller, string name, CredentialChanges changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(changes);
        await authorizer.RequireRoleAsync(caller, CredentialRole.Admin, cancellationToken).ConfigureAwait(false);
        var row = await FindManagedAsync(caller, name, cancellationToken).ConfigureAwait(false);
        RequireWithin(caller, changes.Role ?? row.Role, changes.Tenant is { } binding ? binding.TenantId : row.TenantId);

        // The store reads an empty id as "make it global" and null as "keep".
        Guid? tenant = changes.Tenant is { } change ? change.TenantId ?? Guid.Empty : null;
        var updated = await credentials.UpdateAsync(row.Id, changes.Role, tenant, changes.Disabled, null, cancellationToken).ConfigureAwait(false)
            ?? throw new CredentialNotFoundException($"No credential named '{name}'.");
        basic?.Forget(updated.Name);
        return updated;
    }

    /// <summary>Sets a credential's password, which also clears its lockout.</summary>
    /// <param name="caller">The caller, an admin.</param>
    /// <param name="name">The credential's name.</param>
    /// <param name="password">The new password as typed.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The row as it now is.</returns>
    /// <exception cref="CredentialNotFoundException">No such credential among those the caller may manage.</exception>
    public async Task<Credential> SetPasswordAsync(NightingalePrincipal caller, string name, string password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        await authorizer.RequireRoleAsync(caller, CredentialRole.Admin, cancellationToken).ConfigureAwait(false);
        var row = await FindManagedAsync(caller, name, cancellationToken).ConfigureAwait(false);
        RequirePassword(password);
        var updated = await credentials.UpdateAsync(row.Id, null, null, null, hasher.Hash(password), cancellationToken).ConfigureAwait(false)
            ?? throw new CredentialNotFoundException($"No credential named '{name}'.");
        basic?.Forget(updated.Name);
        return updated;
    }

    /// <summary>Deletes a credential.</summary>
    /// <param name="caller">The caller, an admin.</param>
    /// <param name="name">The credential's name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the row is gone.</returns>
    /// <exception cref="CredentialNotFoundException">No such credential among those the caller may manage.</exception>
    public async Task DeleteAsync(NightingalePrincipal caller, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        await authorizer.RequireRoleAsync(caller, CredentialRole.Admin, cancellationToken).ConfigureAwait(false);
        var row = await FindManagedAsync(caller, name, cancellationToken).ConfigureAwait(false);
        if (!await credentials.DeleteAsync(row.Id, cancellationToken).ConfigureAwait(false))
        {
            throw new CredentialNotFoundException($"No credential named '{name}'.");
        }

        basic?.Forget(row.Name);
    }

    /// <summary>Lists the credentials the caller may manage: every one for a global admin, its own tenant's for a bound one.</summary>
    /// <param name="caller">The caller, an admin.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The rows, by name.</returns>
    public async Task<IReadOnlyList<Credential>> ListAsync(NightingalePrincipal caller, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        await authorizer.RequireRoleAsync(caller, CredentialRole.Admin, cancellationToken).ConfigureAwait(false);
        return await credentials.ListAsync(caller.TenantId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Changes the caller's own password, given the current one; any role may.</summary>
    /// <param name="caller">The caller.</param>
    /// <param name="currentPassword">The password as it is.</param>
    /// <param name="newPassword">The password to set.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the row is written.</returns>
    /// <exception cref="AccessDeniedException">The caller is the built-in administrator, whose password is configuration.</exception>
    /// <exception cref="AuthenticationFailedException">The current password is not right.</exception>
    public async Task ChangeOwnPasswordAsync(NightingalePrincipal caller, string currentPassword, string newPassword, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (string.Equals(caller.Name, UserCredentials.AdminUserName, StringComparison.OrdinalIgnoreCase) && caller.Scheme != BasicAuthenticator.Scheme)
        {
            throw new AccessDeniedException(AdminPasswordIsConfiguration);
        }

        var row = await credentials.FindAsync(caller.Name, cancellationToken).ConfigureAwait(false)
            ?? throw new AccessDeniedException(AdminPasswordIsConfiguration);
        if (!hasher.Verify(currentPassword, row.PasswordHash).Matches)
        {
            throw new AuthenticationFailedException("The current password is not right.");
        }

        RequirePassword(newPassword);
        await credentials.UpdateAsync(row.Id, null, null, null, hasher.Hash(newPassword), cancellationToken).ConfigureAwait(false);
        basic?.Forget(row.Name);
    }

    private static string RequireName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 250 || name.Contains(':', StringComparison.Ordinal) || string.Equals(name, UserCredentials.AdminUserName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A credential's name is 1 to 250 characters, holds no ':', and is not the built-in administrator's.");
        }

        return name;
    }

    /// <summary>Nobody grants above their own role, and a tenant-bound admin grants within its tenant only.</summary>
    private static void RequireWithin(NightingalePrincipal caller, CredentialRole role, Guid? tenant)
    {
        if (role > caller.Role)
        {
            throw new AccessDeniedException("A credential cannot be given a role above its maker's.");
        }

        if (caller.TenantId is { } own && tenant != own)
        {
            throw new AccessDeniedException("A tenant-bound administrator manages credentials of its own tenant only.");
        }
    }

    private void RequirePassword(string password)
    {
        if (password is null || Encoding.UTF8.GetByteCount(password) > 1024 || password.Normalize(NormalizationForm.FormKC).Length < options.Auth.MinimumPasswordLength)
        {
            throw new ArgumentException($"A password is at least {options.Auth.MinimumPasswordLength} characters and at most 1024 bytes.");
        }
    }

    /// <summary>The credential, among those the caller may manage; another tenant's is not found, so that it is not revealed.</summary>
    private async Task<Credential> FindManagedAsync(NightingalePrincipal caller, string name, CancellationToken cancellationToken)
    {
        var row = await credentials.FindAsync(name, cancellationToken).ConfigureAwait(false);
        if (row is null || (caller.TenantId is { } own && row.TenantId != own))
        {
            throw new CredentialNotFoundException($"No credential named '{name}'.");
        }

        return row;
    }
}
