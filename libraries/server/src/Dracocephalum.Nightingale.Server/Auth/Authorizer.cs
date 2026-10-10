namespace Dracocephalum.Nightingale.Server.Auth;

/// <summary>
/// What a call must satisfy before it is served, decided on the principal and on nothing of any
/// transport: a role at least this high, and a tenant to work in. The tenant is the credential's
/// own, or, for a global credential, the one the call named, which a global credential must name
/// on every data call and which may be the wildcard on a read. Nothing is served to a disabled
/// tenant. Until the stores take a tenant per call, only the tenant the instance serves can be
/// worked in. A refusal is one of the shared exceptions; each transport reads the named tenant
/// from wherever it carries it and maps the refusals to its own statuses.
/// </summary>
/// <param name="directory">The tenants; none in a host that keeps no tenants, where the instance's one tenant is every call's scope.</param>
/// <param name="served">The tenant the instance serves; none in a host without a store.</param>
public sealed class Authorizer(TenantDirectory? directory = null, StoreTenant? served = null)
{
    /// <summary>Checks the call's role and resolves its tenant.</summary>
    /// <param name="principal">The caller.</param>
    /// <param name="required">The role the call needs.</param>
    /// <param name="access">Whether the call reads, which admits the wildcard, or writes.</param>
    /// <param name="selection">The tenant the call named.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The tenant the call works in.</returns>
    /// <exception cref="AccessDeniedException">The role is too low, or the wildcard was named on a write.</exception>
    /// <exception cref="TenantNotFoundException">No tenant was named where one is needed, or the one named is unknown or not the credential's own.</exception>
    /// <exception cref="TenantDisabledException">The tenant is disabled.</exception>
    /// <exception cref="NotSupportedException">The tenant is not the one this instance serves.</exception>
    public async Task<TenantScope> AuthorizeAsync(NightingalePrincipal principal, CredentialRole required, TenantAccess access, TenantSelection selection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        Require(principal, required);
        TenantScope scope;
        if (principal.TenantId is { } bound)
        {
            // A tenant-bound credential works in its tenant; naming another is refused without
            // saying whether that other tenant exists.
            if (!selection.IsNone && selection.TenantId != bound)
            {
                throw new TenantNotFoundException(selection.TenantId);
            }

            scope = await ResolveAsync(bound, cancellationToken).ConfigureAwait(false);
        }
        else if (directory is null || (selection.IsNone && principal.Scheme == NightingalePrincipal.Anonymous.Scheme))
        {
            // No tenants are kept here, a test host; or authentication is off and the call names
            // no tenant, which is every caller of a host on a private network. The instance's
            // one tenant is the scope.
            scope = TenantScope.Default;
        }
        else if (selection.IsNone)
        {
            throw new TenantNotFoundException((Guid?)null);
        }
        else if (selection.IsWildcard)
        {
            if (access != TenantAccess.Read)
            {
                throw new AccessDeniedException("The wildcard tenant is for reads and subscriptions only.");
            }

            scope = TenantScope.Every;
        }
        else
        {
            scope = await ResolveAsync(selection.TenantId!.Value, cancellationToken).ConfigureAwait(false);
        }

        // One tenant's data is served today; a call for another is refused rather than served
        // from the wrong rows. The wildcard spans that one tenant, which is every tenant there is.
        if (served is not null && scope.StoreTenantId is { } store && !string.Equals(store, served.StoreTenantId, StringComparison.Ordinal))
        {
            throw new NotSupportedException("Data access for a tenant other than the one this instance serves");
        }

        return scope;
    }

    /// <summary>
    /// Checks the call's role only: for a management call, which works in the caller's own
    /// tenant or across all of them by the caller's binding, not by a named tenant. A
    /// tenant-bound caller whose tenant is disabled is refused like any of its calls.
    /// </summary>
    /// <param name="principal">The caller.</param>
    /// <param name="required">The role the call needs.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The principal.</returns>
    /// <exception cref="AccessDeniedException">The role is too low.</exception>
    /// <exception cref="TenantNotFoundException">The caller's tenant is gone.</exception>
    /// <exception cref="TenantDisabledException">The caller's tenant is disabled.</exception>
    public async Task<NightingalePrincipal> RequireRoleAsync(NightingalePrincipal principal, CredentialRole required, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        Require(principal, required);
        if (principal.TenantId is { } bound)
        {
            await ResolveAsync(bound, cancellationToken).ConfigureAwait(false);
        }

        return principal;
    }

    private static void Require(NightingalePrincipal principal, CredentialRole required)
    {
        if (!principal.IsAtLeast(required))
        {
            throw new AccessDeniedException($"The call needs the {required.ToString().ToLowerInvariant()} role.", required);
        }
    }

    private async Task<TenantScope> ResolveAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (directory is null)
        {
            return TenantScope.Default;
        }

        var tenant = await directory.FindAsync(tenantId, cancellationToken).ConfigureAwait(false)
            ?? throw new TenantNotFoundException(tenantId);
        if (tenant.IsDisabled)
        {
            throw new TenantDisabledException(tenantId);
        }

        return new TenantScope(tenant.Id, tenant.StoreTenantId);
    }
}
