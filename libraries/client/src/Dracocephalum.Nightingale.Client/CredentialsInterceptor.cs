using System.Text;

using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Dracocephalum.Nightingale.Client;

/// <summary>
/// Sends the credentials with every call, in the standard <c>authorization</c> header under the
/// Basic scheme, and the tenant, when the settings name one, in the <c>nightingale-tenant</c>
/// header, unless the call already carries the header. The values are made once.
/// </summary>
internal sealed class CredentialsInterceptor : Interceptor
{
    private const string Header = "authorization";
    private const string TenantHeader = "nightingale-tenant";
    private readonly string? _value;
    private readonly string? _tenant;

    /// <summary>Initializes a new instance of the <see cref="CredentialsInterceptor"/> class.</summary>
    /// <param name="credentials">The user name and password to send, or <see langword="null"/> for none.</param>
    /// <param name="tenant">The tenant to send, or <see langword="null"/> for none.</param>
    public CredentialsInterceptor(UserCredentials? credentials, string? tenant)
    {
        _value = credentials is null ? null : "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials.UserName + ":" + credentials.Password));
        _tenant = tenant;
    }

    /// <inheritdoc/>
    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(TRequest request, ClientInterceptorContext<TRequest, TResponse> context, AsyncUnaryCallContinuation<TRequest, TResponse> continuation) =>
        continuation(request, WithCredentials(context));

    /// <inheritdoc/>
    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(TRequest request, ClientInterceptorContext<TRequest, TResponse> context, AsyncServerStreamingCallContinuation<TRequest, TResponse> continuation) =>
        continuation(request, WithCredentials(context));

    /// <inheritdoc/>
    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(ClientInterceptorContext<TRequest, TResponse> context, AsyncClientStreamingCallContinuation<TRequest, TResponse> continuation) =>
        continuation(WithCredentials(context));

    /// <inheritdoc/>
    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(ClientInterceptorContext<TRequest, TResponse> context, AsyncDuplexStreamingCallContinuation<TRequest, TResponse> continuation) =>
        continuation(WithCredentials(context));

    private ClientInterceptorContext<TRequest, TResponse> WithCredentials<TRequest, TResponse>(ClientInterceptorContext<TRequest, TResponse> context)
        where TRequest : class
        where TResponse : class
    {
        var headers = context.Options.Headers;
        var sent = new Metadata();
        if (headers is not null)
        {
            foreach (var entry in headers)
            {
                sent.Add(entry);
            }
        }

        var added = false;
        if (_value is not null && !Has(sent, Header))
        {
            sent.Add(Header, _value);
            added = true;
        }

        if (_tenant is not null && !Has(sent, TenantHeader))
        {
            sent.Add(TenantHeader, _tenant);
            added = true;
        }

        return added ? new(context.Method, context.Host, context.Options.WithHeaders(sent)) : context;
    }

    private static bool Has(Metadata headers, string key) =>
        headers.Any(entry => string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase));
}
