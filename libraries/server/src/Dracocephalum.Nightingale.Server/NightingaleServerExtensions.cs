using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// What a host does to run a Nightingale server: register, then map.
/// </summary>
public static class NightingaleServerExtensions
{
    /// <summary>Registers the gRPC services a Nightingale server needs. The options come from the backend's registration, through <see cref="AddNightingaleOptions{TOptions}"/>.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddNightingaleServer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddGrpc();
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }

    /// <summary>
    /// Registers a bound options tree as itself and as the base the server reads, so the services
    /// see the common settings and the backend sees its own, from one instance.
    /// </summary>
    /// <typeparam name="TOptions">The backend's options, a Nightingale options tree.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="options">The bound options.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddNightingaleOptions<TOptions>(this IServiceCollection services, TOptions options)
        where TOptions : NightingaleOptionsBase
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        services.AddSingleton(options);
        services.AddSingleton<NightingaleOptionsBase>(options);
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
