using Grpc.AspNetCore.Server;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Dracocephalum.Nightingale.Server.Auth;

/// <summary>
/// What a service asks of a call before serving it: a role at least this high, and a tenant to
/// work in. The tenant is the credential's own, or, for a global credential, the one the
/// <c>nightingale-tenant</c> header names, which a global credential must send on every data
/// call, and which may be <c>*</c> on a read. Nothing is served to a disabled tenant. Until the
/// stores take a tenant per call, only the tenant the instance serves can be worked in.
/// </summary>
public static class Authorization
{
    /// <summary>The header a global credential names its tenant in.</summary>
    public const string TenantHeader = "nightingale-tenant";

    /// <summary>The value of the header that spans every tenant, on a read.</summary>
    public const string Wildcard = "*";

    /// <summary>The principal the interceptor left on the call.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The principal; the administrator when no interceptor ran.</returns>
    public static NightingalePrincipal GetPrincipal(this ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.UserState.TryGetValue(AuthenticationInterceptor.PrincipalKey, out var principal) && principal is NightingalePrincipal known
            ? known
            : NightingalePrincipal.Anonymous;
    }

    /// <summary>Checks the call's role and resolves its tenant.</summary>
    /// <param name="context">The call.</param>
    /// <param name="required">The role the call needs.</param>
    /// <param name="access">Whether the call reads, which admits the wildcard, or writes.</param>
    /// <returns>The tenant the call works in.</returns>
    /// <exception cref="RpcException">The role is too low, the tenant is missing, unknown or disabled, or not the one the instance serves.</exception>
    public static async Task<TenantScope> AuthorizeAsync(this ServerCallContext context, CredentialRole required, TenantAccess access)
    {
        ArgumentNullException.ThrowIfNull(context);
        var principal = context.GetPrincipal();
        if (!principal.IsAtLeast(required))
        {
            throw NightingaleErrors.AccessDenied(required);
        }

        var services = context.GetHttpContext().RequestServices;
        var directory = services.GetService<TenantDirectory>();
        var header = context.RequestHeaders.GetValue(TenantHeader)?.Trim();
        TenantScope scope;
        if (principal.TenantId is { } bound)
        {
            // A tenant-bound credential works in its tenant; a header saying otherwise is refused
            // without saying whether that other tenant exists.
            if (!string.IsNullOrEmpty(header) && (!Guid.TryParseExact(header, "D", out var named) || named != bound))
            {
                throw NightingaleErrors.TenantNotFound(header);
            }

            scope = await ResolveAsync(directory, bound, context.CancellationToken).ConfigureAwait(false);
        }
        else if (directory is null || (string.IsNullOrEmpty(header) && principal.Scheme == NightingalePrincipal.Anonymous.Scheme))
        {
            // No tenants are kept here, a test host; or authentication is off and the call names
            // no tenant, which is every caller of a host on a private network. The instance's
            // one tenant is the scope.
            scope = TenantScope.Default;
        }
        else if (string.IsNullOrEmpty(header))
        {
            throw NightingaleErrors.TenantNotFound(string.Empty);
        }
        else if (header == Wildcard)
        {
            if (access != TenantAccess.Read)
            {
                throw NightingaleErrors.AccessDenied("The wildcard tenant is for reads and subscriptions only.");
            }

            scope = TenantScope.Every;
        }
        else
        {
            if (!Guid.TryParseExact(header, "D", out var named))
            {
                throw NightingaleErrors.TenantNotFound(header);
            }

            scope = await ResolveAsync(directory, named, context.CancellationToken).ConfigureAwait(false);
        }

        // One tenant's data is served today; a call for another is refused rather than served
        // from the wrong rows. The wildcard spans that one tenant, which is every tenant there is.
        var served = services.GetService<StoreTenant>();
        if (served is not null && scope.StoreTenantId is { } store && !string.Equals(store, served.StoreTenantId, StringComparison.Ordinal))
        {
            throw NightingaleErrors.NotImplemented("Data access for a tenant other than the one this instance serves");
        }

        return scope;
    }

    private static async Task<TenantScope> ResolveAsync(TenantDirectory? directory, Guid tenantId, CancellationToken cancellationToken)
    {
        if (directory is null)
        {
            return TenantScope.Default;
        }

        var tenant = await directory.FindAsync(tenantId, cancellationToken).ConfigureAwait(false)
            ?? throw NightingaleErrors.TenantNotFound(tenantId.ToString("D"));
        if (tenant.IsDisabled)
        {
            throw NightingaleErrors.TenantDisabled(tenantId);
        }

        return new TenantScope(tenant.Id, tenant.StoreTenantId);
    }
}
