using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// What a host does to run a Nightingale server: register, then map.
/// </summary>
public static class NightingaleServerExtensions
{
    /// <summary>Registers the gRPC services a Nightingale server needs.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddNightingaleServer(this IServiceCollection services)
    {
        services.AddGrpc();
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }

    /// <summary>Maps the Nightingale gRPC services onto the host's endpoints.</summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapNightingaleServer(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGrpcService<ServerFeaturesService>();
        endpoints.MapGrpcService<StreamsService>();
        return endpoints;
    }
}
