using Dracocephalum.Nightingale.Server.Auth;
using Grpc.AspNetCore.Server;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Dracocephalum.Nightingale.Server.Grpc;

/// <summary>
/// The gRPC side of <see cref="Authorizer"/>: the principal the interceptor left on the call,
/// the tenant the <c>nightingale-tenant</c> header names, and the shared exceptions turned into
/// the statuses the contract promises.
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

    /// <summary>Checks the call's role and resolves its tenant, from the header.</summary>
    /// <param name="context">The call.</param>
    /// <param name="required">The role the call needs.</param>
    /// <param name="access">Whether the call reads, which admits the wildcard, or writes.</param>
    /// <returns>The tenant the call works in.</returns>
    /// <exception cref="RpcException">The role is too low, the tenant is missing, unknown or disabled, or not the one the instance serves.</exception>
    public static async Task<TenantScope> AuthorizeAsync(this ServerCallContext context, CredentialRole required, TenantAccess access)
    {
        ArgumentNullException.ThrowIfNull(context);
        var header = context.RequestHeaders.GetValue(TenantHeader)?.Trim() ?? string.Empty;
        TenantSelection selection;
        if (header.Length == 0)
        {
            selection = TenantSelection.None;
        }
        else if (header == Wildcard)
        {
            selection = TenantSelection.Wildcard;
        }
        else if (Guid.TryParseExact(header, "D", out var named))
        {
            selection = new TenantSelection(named, false);
        }
        else
        {
            throw NightingaleErrors.TenantNotFound(header);
        }

        try
        {
            return await GetAuthorizer(context).AuthorizeAsync(context.GetPrincipal(), required, access, selection, context.CancellationToken).ConfigureAwait(false);
        }
        catch (TenantNotFoundException notFound)
        {
            // Named as the header did, so the client sees what it sent.
            throw NightingaleErrors.TenantNotFound(notFound.TenantId?.ToString("D") ?? header);
        }
        catch (Exception exception) when (NightingaleErrors.TryTranslate(exception, out var status))
        {
            throw status;
        }
    }

    /// <summary>Checks the call's role only: for a management call, which is authorized by the caller's binding, not by a header.</summary>
    /// <param name="context">The call.</param>
    /// <param name="required">The role the call needs.</param>
    /// <returns>The principal.</returns>
    /// <exception cref="RpcException">The role is too low, or the caller's tenant is gone or disabled.</exception>
    public static async Task<NightingalePrincipal> RequireRoleAsync(this ServerCallContext context, CredentialRole required)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            return await GetAuthorizer(context).RequireRoleAsync(context.GetPrincipal(), required, context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (NightingaleErrors.TryTranslate(exception, out var status))
        {
            throw status;
        }
    }

    private static Authorizer GetAuthorizer(ServerCallContext context) =>
        context.GetHttpContext().RequestServices.GetRequiredService<Authorizer>();
}
