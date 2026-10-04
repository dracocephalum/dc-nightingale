using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Dracocephalum.Nightingale.Client;

/// <summary>
/// Gives the connection string's default deadline to a call that ends on its own and carries no
/// deadline: the unary calls and an append, which is one answer to a stream of requests. A read
/// or a subscription runs as long as its caller wants and is left alone.
/// </summary>
internal sealed class DefaultDeadlineInterceptor(TimeSpan deadline, TimeProvider timeProvider) : Interceptor
{
    /// <inheritdoc/>
    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(TRequest request, ClientInterceptorContext<TRequest, TResponse> context, AsyncUnaryCallContinuation<TRequest, TResponse> continuation) =>
        continuation(request, WithDeadline(context));

    /// <inheritdoc/>
    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(ClientInterceptorContext<TRequest, TResponse> context, AsyncClientStreamingCallContinuation<TRequest, TResponse> continuation) =>
        continuation(WithDeadline(context));

    private ClientInterceptorContext<TRequest, TResponse> WithDeadline<TRequest, TResponse>(ClientInterceptorContext<TRequest, TResponse> context)
        where TRequest : class
        where TResponse : class =>
        context.Options.Deadline is null
            ? new(context.Method, context.Host, context.Options.WithDeadline(timeProvider.GetUtcNow().UtcDateTime.Add(deadline)))
            : context;
}
