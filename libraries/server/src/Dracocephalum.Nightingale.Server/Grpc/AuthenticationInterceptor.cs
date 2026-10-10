using Dracocephalum.Nightingale.Protocols.Grpc.V1;
using Dracocephalum.Nightingale.Server.Auth;
using Grpc.AspNetCore.Server;
using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Dracocephalum.Nightingale.Server.Grpc;

/// <summary>
/// Authenticates every call before its service sees it, from the <c>authorization</c> header,
/// by scheme: Basic goes to <see cref="BasicAuthenticator"/>; any other scheme, and no header,
/// is refused. The features call is the exception, so a client can learn that it needs
/// credentials. The principal is left on the call for <see cref="Authorization"/> to read. With
/// authentication off, or a host that registered no options, every call is the administrator's.
/// </summary>
public sealed class AuthenticationInterceptor(NightingaleOptionsBase? options = null, BasicAuthenticator? basic = null) : Interceptor
{
    /// <summary>The key the principal is kept under on the call.</summary>
    public const string PrincipalKey = "nightingale.principal";

    private const string Header = "authorization";
    private static readonly string Features = "/" + ServerFeatures.Descriptor.FullName + "/";

    /// <inheritdoc/>
    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        await AuthenticateAsync(context).ConfigureAwait(false);
        return await continuation(request, context).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(TRequest request, IServerStreamWriter<TResponse> responseStream, ServerCallContext context, ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        await AuthenticateAsync(context).ConfigureAwait(false);
        await continuation(request, responseStream, context).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public override async Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(IAsyncStreamReader<TRequest> requestStream, ServerCallContext context, ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        await AuthenticateAsync(context).ConfigureAwait(false);
        return await continuation(requestStream, context).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public override async Task DuplexStreamingServerHandler<TRequest, TResponse>(IAsyncStreamReader<TRequest> requestStream, IServerStreamWriter<TResponse> responseStream, ServerCallContext context, DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        await AuthenticateAsync(context).ConfigureAwait(false);
        await continuation(requestStream, responseStream, context).ConfigureAwait(false);
    }

    private async Task AuthenticateAsync(ServerCallContext context)
    {
        var settings = options?.Auth;
        if (settings is null || !settings.Enabled || basic is null)
        {
            context.UserState[PrincipalKey] = NightingalePrincipal.Anonymous;
            return;
        }

        if (context.Method.StartsWith(Features, StringComparison.Ordinal))
        {
            context.UserState[PrincipalKey] = NightingalePrincipal.Anonymous with { Role = CredentialRole.User, Scheme = "anonymous" };
            return;
        }

        var header = context.RequestHeaders.GetValue(Header);
        if (string.IsNullOrWhiteSpace(header))
        {
            throw NightingaleErrors.AuthenticationFailed("The call carries no credentials; see the authorization header in the contract.");
        }

        // Basic sends the password as it is: over a connection that is not TLS, that is a
        // password on the wire, which the host has to opt into for a private network.
        if (!settings.AllowInsecureTransport && !context.GetHttpContext().Request.IsHttps)
        {
            throw NightingaleErrors.AuthenticationFailed("Credentials are not accepted over a connection that is not TLS.");
        }

        var space = header.IndexOf(' ', StringComparison.Ordinal);
        var scheme = space < 0 ? header : header[..space];
        var parameter = space < 0 ? string.Empty : header[(space + 1)..];
        var principal = string.Equals(scheme, BasicAuthenticator.Scheme, StringComparison.OrdinalIgnoreCase)
            ? await basic.AuthenticateAsync(parameter, context.Peer, context.CancellationToken).ConfigureAwait(false)
            : null;
        context.UserState[PrincipalKey] = principal ?? throw NightingaleErrors.AuthenticationFailed("The call's credentials were not accepted.");
    }
}
