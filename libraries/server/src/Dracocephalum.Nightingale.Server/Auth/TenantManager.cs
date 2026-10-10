using Dracocephalum.Nightingale.Server.Data;

namespace Dracocephalum.Nightingale.Server.Auth;

/// <summary>
/// Manages tenants, for whichever transport carries the request; a global admin's call, every
/// one of them. A refusal is one of the shared exceptions; a request that is not well formed is
/// an <see cref="ArgumentException"/> that says what is wrong with it.
/// </summary>
/// <param name="tenants">The tenants table.</param>
/// <param name="authorizer">Checks the caller's role.</param>
public sealed class TenantManager(ITenantStore tenants, Authorizer authorizer)
{
    /// <summary>Creates a tenant.</summary>
    /// <param name="caller">The caller, a global admin.</param>
    /// <param name="name">The name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The row.</returns>
    /// <exception cref="TenantExistsException">The name is taken.</exception>
    public async Task<Tenant> CreateAsync(NightingalePrincipal caller, string name, CancellationToken cancellationToken)
    {
        await RequireGlobalAdminAsync(caller, cancellationToken).ConfigureAwait(false);
        return await tenants.CreateAsync(RequireName(name), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Renames a tenant, or disables or enables it.</summary>
    /// <param name="caller">The caller, a global admin.</param>
    /// <param name="id">The tenant's id.</param>
    /// <param name="name">The new name, or <see langword="null"/> to keep it.</param>
    /// <param name="disabled">The new flag, or <see langword="null"/> to keep it.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The row as it now is.</returns>
    /// <exception cref="TenantNotFoundException">No such tenant.</exception>
    /// <exception cref="TenantExistsException">The new name is taken.</exception>
    public async Task<Tenant> UpdateAsync(NightingalePrincipal caller, Guid id, string? name, bool? disabled, CancellationToken cancellationToken)
    {
        await RequireGlobalAdminAsync(caller, cancellationToken).ConfigureAwait(false);
        var named = name is null ? null : RequireName(name);
        return await tenants.UpdateAsync(id, named, disabled, cancellationToken).ConfigureAwait(false)
            ?? throw new TenantNotFoundException(id);
    }

    /// <summary>Lists all tenants.</summary>
    /// <param name="caller">The caller, a global admin.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The rows, by name.</returns>
    public async Task<IReadOnlyList<Tenant>> ListAsync(NightingalePrincipal caller, CancellationToken cancellationToken)
    {
        await RequireGlobalAdminAsync(caller, cancellationToken).ConfigureAwait(false);
        return await tenants.ListAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string RequireName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 250)
        {
            throw new ArgumentException("A tenant's name is 1 to 250 characters.");
        }

        return name;
    }

    private async Task RequireGlobalAdminAsync(NightingalePrincipal caller, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);

        // Tenants are above any tenant, so the call names none and is authorized on the role
        // alone: a tenant-bound admin is refused.
        var principal = await authorizer.RequireRoleAsync(caller, CredentialRole.Admin, cancellationToken).ConfigureAwait(false);
        if (principal.TenantId is not null)
        {
            throw new AccessDeniedException("Tenants are managed by a global administrator.");
        }
    }
}
