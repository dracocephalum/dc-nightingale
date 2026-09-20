using Dracocephalum.Nightingale.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
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
        services.TryAddSingleton<GroupRegistry>();
        services.TryAddSingleton<InstanceAddress>();
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
        options.Cluster.Validate();
        services.AddSingleton(options);
        services.AddSingleton<NightingaleOptionsBase>(options);
        return services;
    }

    /// <summary>
    /// Registers the gateway's own tables through the context, and the group store over it. A
    /// backend calls this with its provider and the schema its store keeps its tables in, so the
    /// gateway's sit beside them; the backend's schema feature creates the tables, the context
    /// never migrates.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="schema">The schema the tables live in.</param>
    /// <param name="tenantId">The tenant every group belongs to.</param>
    /// <param name="configure">The provider and connection.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddNightingaleGroupStore(this IServiceCollection services, string schema, string tenantId, Action<DbContextOptionsBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(configure);
        services.AddSingleton(new NightingaleTables(schema));
        services.AddDbContextFactory<NightingaleDbContext>(configure);
        services.AddSingleton<IGroupStore>(provider => new GroupStore(
            provider.GetRequiredService<IDbContextFactory<NightingaleDbContext>>(),
            tenantId,
            provider.GetService<TimeProvider>() ?? TimeProvider.System));
        return services;
    }

    /// <summary>Maps the Nightingale gRPC services onto the host's endpoints.</summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapNightingaleServer(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGrpcService<ServerFeaturesService>();
        endpoints.MapGrpcService<StreamsService>();
        endpoints.MapGrpcService<PersistentSubscriptionsService>();
        return endpoints;
    }
}
