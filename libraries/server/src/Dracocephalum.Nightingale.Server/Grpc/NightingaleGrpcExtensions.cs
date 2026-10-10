using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Dracocephalum.Nightingale.Server.Grpc;

/// <summary>
/// The gRPC transport: the services over the application core, the interceptor that
/// authenticates every call, and what a host mounts. A second transport is a sibling with the
/// same two methods, over the same core.
/// </summary>
public static class NightingaleGrpcExtensions
{
    /// <summary>Registers gRPC with the interceptor every call goes through.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddNightingaleGrpc(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddGrpc(grpc => grpc.Interceptors.Add<AuthenticationInterceptor>());
        return services;
    }

    /// <summary>Maps the gRPC services onto the host's endpoints.</summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapNightingaleGrpc(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGrpcService<ServerFeaturesService>();
        endpoints.MapGrpcService<StreamsService>();
        endpoints.MapGrpcService<PersistentSubscriptionsService>();
        endpoints.MapGrpcService<CredentialsService>();
        endpoints.MapGrpcService<TenantsService>();
        return endpoints;
    }
}
