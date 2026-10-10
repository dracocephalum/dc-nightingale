using Dracocephalum.Nightingale.Protocols.Grpc;
using Dracocephalum.Nightingale.Protocols.Grpc.V1;
using Grpc.Core;

namespace Dracocephalum.Nightingale.Client;

/// <summary>
/// The management calls: credentials and tenants. Reached through
/// <see cref="NightingaleClient.Management"/>; every call needs the admin role, except
/// <see cref="ChangeOwnPasswordAsync"/>. A tenant-bound admin manages its own tenant's
/// credentials only; tenants are a global admin's.
/// </summary>
public sealed class NightingaleManagement
{
    private readonly Credentials.CredentialsClient _credentials;
    private readonly Tenants.TenantsClient _tenants;

    internal NightingaleManagement(CallInvoker invoker)
    {
        _credentials = new Credentials.CredentialsClient(invoker);
        _tenants = new Tenants.TenantsClient(invoker);
    }

    /// <summary>Creates a credential.</summary>
    /// <param name="name">The user name.</param>
    /// <param name="password">The password, at least as long as the server requires.</param>
    /// <param name="role">The role; not above the caller's.</param>
    /// <param name="tenantId">The tenant to bind it to, or <see langword="null"/> for a global credential, which only a global admin may make.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The credential as the server describes it.</returns>
    /// <exception cref="CredentialExistsException">The name is taken.</exception>
    /// <exception cref="AccessDeniedException">The role or the tenant is beyond the caller's.</exception>
    public Task<CredentialInfo> CreateCredentialAsync(string name, string password, CredentialRole role = CredentialRole.User, Guid? tenantId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(password);
        var request = new CreateCredentialRequest { Name = name, Password = password, Role = role.ToWire(), Tenant = tenantId?.ToString("D") ?? string.Empty };
        return CallAsync(async () => (await _credentials.CreateAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false)).Credential.ToCredentialInfo());
    }

    /// <summary>Changes a credential's role, tenant or disabled flag; an argument left <see langword="null"/> keeps the value.</summary>
    /// <param name="name">The user name.</param>
    /// <param name="role">The new role, or <see langword="null"/>.</param>
    /// <param name="tenantId">The tenant to bind to, <see cref="Guid.Empty"/> for global, or <see langword="null"/>.</param>
    /// <param name="disabled">The new flag, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The credential as it now is.</returns>
    /// <exception cref="CredentialNotFoundException">No such credential among those the caller manages.</exception>
    public Task<CredentialInfo> UpdateCredentialAsync(string name, CredentialRole? role = null, Guid? tenantId = null, bool? disabled = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var request = new UpdateCredentialRequest { Name = name, Role = role?.ToWire() ?? Protocols.Grpc.V1.CredentialRole.Unspecified, Tenant = tenantId is null ? string.Empty : tenantId == Guid.Empty ? "-" : tenantId.Value.ToString("D") };
        if (disabled is { } flag)
        {
            request.Disabled = flag;
        }

        return CallAsync(async () => (await _credentials.UpdateAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false)).Credential.ToCredentialInfo());
    }

    /// <summary>Sets a credential's password and clears its lockout.</summary>
    /// <param name="name">The user name.</param>
    /// <param name="password">The new password.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The credential as it now is.</returns>
    public Task<CredentialInfo> SetCredentialPasswordAsync(string name, string password, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(password);
        return CallAsync(async () => (await _credentials.SetPasswordAsync(new SetPasswordRequest { Name = name, Password = password }, cancellationToken: cancellationToken).ConfigureAwait(false)).Credential.ToCredentialInfo());
    }

    /// <summary>Deletes a credential.</summary>
    /// <param name="name">The user name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when it is gone.</returns>
    /// <exception cref="CredentialNotFoundException">No such credential among those the caller manages.</exception>
    public Task DeleteCredentialAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return CallAsync(async () => await _credentials.DeleteAsync(new DeleteCredentialRequest { Name = name }, cancellationToken: cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Lists the credentials the caller may manage, by name.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The credentials.</returns>
    public Task<IReadOnlyList<CredentialInfo>> ListCredentialsAsync(CancellationToken cancellationToken = default) =>
        CallAsync(async () => (IReadOnlyList<CredentialInfo>)(await _credentials.ListAsync(new ListCredentialsRequest(), cancellationToken: cancellationToken).ConfigureAwait(false)).Credentials.Select(info => info.ToCredentialInfo()).ToList());

    /// <summary>Sets the caller's own password, given the current one; any role.</summary>
    /// <param name="currentPassword">The password as it is.</param>
    /// <param name="newPassword">The password to set.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when it is set; the next call sends the new password.</returns>
    /// <exception cref="AuthenticationFailedException">The current password is not right.</exception>
    public Task ChangeOwnPasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentPassword);
        ArgumentNullException.ThrowIfNull(newPassword);
        return CallAsync(async () => await _credentials.ChangeOwnPasswordAsync(new ChangeOwnPasswordRequest { CurrentPassword = currentPassword, NewPassword = newPassword }, cancellationToken: cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Creates a tenant under a name; global admin only.</summary>
    /// <param name="name">The name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The tenant, with the id every call and credential names it by.</returns>
    /// <exception cref="TenantExistsException">The name is taken.</exception>
    public Task<TenantInfo> CreateTenantAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return CallAsync(async () => (await _tenants.CreateAsync(new CreateTenantRequest { Name = name }, cancellationToken: cancellationToken).ConfigureAwait(false)).Tenant.ToTenantInfo());
    }

    /// <summary>Renames a tenant, or disables or enables it; an argument left <see langword="null"/> keeps the value. Global admin only.</summary>
    /// <param name="id">The tenant's id.</param>
    /// <param name="name">The new name, or <see langword="null"/>.</param>
    /// <param name="disabled">The new flag, or <see langword="null"/>; disabled, the tenant's calls are refused and its consumers disconnected everywhere within the server's refresh interval.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The tenant as it now is.</returns>
    /// <exception cref="TenantNotFoundException">No such tenant.</exception>
    public Task<TenantInfo> UpdateTenantAsync(Guid id, string? name = null, bool? disabled = null, CancellationToken cancellationToken = default)
    {
        var request = new UpdateTenantRequest { Id = id.ToString("D"), Name = name ?? string.Empty };
        if (disabled is { } flag)
        {
            request.Disabled = flag;
        }

        return CallAsync(async () => (await _tenants.UpdateAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false)).Tenant.ToTenantInfo());
    }

    /// <summary>Lists the tenants, by name; global admin only.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The tenants.</returns>
    public Task<IReadOnlyList<TenantInfo>> ListTenantsAsync(CancellationToken cancellationToken = default) =>
        CallAsync(async () => (IReadOnlyList<TenantInfo>)(await _tenants.ListAsync(new ListTenantsRequest(), cancellationToken: cancellationToken).ConfigureAwait(false)).Tenants.Select(info => info.ToTenantInfo()).ToList());

    private static async Task<T> CallAsync<T>(Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (RpcException exception)
        {
            throw NightingaleErrorMapping.ToException(exception);
        }
    }

    private static async Task CallAsync(Func<Task> call)
    {
        try
        {
            await call().ConfigureAwait(false);
        }
        catch (RpcException exception)
        {
            throw NightingaleErrorMapping.ToException(exception);
        }
    }
}
