using Dracocephalum.Nightingale.Server.Auth;
using Dracocephalum.Nightingale.Server.Data;
using Dracocephalum.Nightingale.Server.Grpc;
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
    /// <summary>
    /// Registers the application core a Nightingale server runs on, and the gRPC transport over
    /// it. The options come from the backend's registration, through
    /// <see cref="AddNightingaleOptions{TOptions}"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddNightingaleServer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddNightingaleGrpc();
        services.TryAddSingleton(provider => new Authorizer(provider.GetService<TenantDirectory>()));
        services.TryAddSingleton<ITenantStores>(provider => new TenantStores(provider.GetRequiredService<IStreamStore>(), provider.GetService<ISubscriptionGroupStore>()));
        services.TryAddSingleton<CredentialManager>();
        services.TryAddSingleton<TenantManager>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<SubscriptionGroupRegistry>();
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
        options.Auth.Validate();
        options.Tenants.Validate();
        if (options.MaxEventsPerAppend < 1)
        {
            throw new InvalidOperationException($"{NightingaleOptionsBase.SectionName}:{nameof(NightingaleOptionsBase.MaxEventsPerAppend)} must be at least 1. It was {options.MaxEventsPerAppend}.");
        }

        if (!IsIdentifier(options.Schema))
        {
            throw new InvalidOperationException($"{NightingaleOptionsBase.SectionName}:{nameof(NightingaleOptionsBase.Schema)} must be a plain identifier: a letter or an underscore, then letters, digits or underscores, at most 128 characters. It was '{options.Schema}'.");
        }

        services.AddSingleton(options);
        services.AddSingleton<NightingaleOptionsBase>(options);
        return services;
    }

    /// <summary>
    /// Registers the gateway's own tables through the context, and the group store over it. A
    /// backend calls this with its provider, its migrations and the schema the host named for the
    /// gateway's tables; the context owns them and the backend's migrations create them.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="schema">The schema the tables live in.</param>
    /// <param name="tenantId">The tenant every group belongs to.</param>
    /// <param name="configure">The provider and connection.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddNightingaleSubscriptionGroupStore(this IServiceCollection services, string schema, string tenantId, Action<DbContextOptionsBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(configure);
        services.AddSingleton(new NightingaleSchema(schema));
        services.AddDbContextFactory<NightingaleDbContext>(configure);
        services.TryAddSingleton<ICredentialStore, CredentialStore>();
        services.TryAddSingleton<ITenantStore, TenantStore>();
        services.TryAddSingleton<ITenantProvisioner, NoTenantProvisioning>();
        services.TryAddSingleton(provider => new PasswordHasher(provider.GetRequiredService<NightingaleOptionsBase>().Auth));
        services.TryAddSingleton(provider => new BasicAuthenticator(
            provider.GetRequiredService<NightingaleOptionsBase>().Auth,
            provider.GetRequiredService<PasswordHasher>(),
            provider.GetRequiredService<ICredentialStore>(),
            provider.GetService<TimeProvider>() ?? TimeProvider.System,
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<BasicAuthenticator>>()));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<SubscriptionGroupRegistry>();
        services.TryAddSingleton<TenantDirectory>();
        services.AddSingleton<ISubscriptionGroupStore>(provider => new SubscriptionGroupStore(
            provider.GetRequiredService<IDbContextFactory<NightingaleDbContext>>(),
            tenantId,
            provider.GetService<TimeProvider>() ?? TimeProvider.System));
        return services;
    }

    private static bool IsIdentifier(string? name) =>
        !string.IsNullOrEmpty(name)
        && name.Length <= 128
        && (char.IsAsciiLetter(name[0]) || name[0] == '_')
        && name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    /// <summary>Maps the Nightingale services onto the host's endpoints, over gRPC.</summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapNightingaleServer(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapNightingaleGrpc();
}
