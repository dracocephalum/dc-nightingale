using JasperFx;
using JasperFx.Events;
using JasperFx.MultiTenancy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Polecat;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// What a host registers to back a Nightingale server with the store. One call configures the
/// store the way the gateway needs it and registers the port and the initializer that owns the
/// database. The store is never exposed for a host to configure: the gateway's schema and settings
/// are what make a database a Nightingale store.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the store as the backend, configured from the <c>Nightingale</c> section and the
    /// connection string it names under <c>ConnectionStrings</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The host's configuration.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="InvalidOperationException">The named connection string is not configured.</exception>
    public static IServiceCollection AddNightingalePolecat(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new NightingaleOptions();
        configuration.GetSection(NightingaleOptionsBase.SectionName).Bind(options);
        var connectionString = configuration.GetConnectionString(options.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"ConnectionStrings:{options.ConnectionStringName} is not configured. {NightingaleOptionsBase.SectionName}:{nameof(NightingaleOptions.ConnectionStringName)} names the connection string the server uses.");
        }

        return Register(services, connectionString, options);
    }

    /// <summary>
    /// Registers the store as the backend, for a host that has the connection string in hand.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">The SQL Server connection string, naming the database to use.</param>
    /// <param name="configure">Adjusts the options; the defaults create a missing database with the server's default collation and no partitioning.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddNightingalePolecat(this IServiceCollection services, string connectionString, Action<NightingaleOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var options = new NightingaleOptions();
        configure?.Invoke(options);
        return Register(services, connectionString, options);
    }

    /// <summary>
    /// The store is configured with string stream identity, conjoined tenancy with the default
    /// tenant, the partitioning the settings ask for, the metadata columns the contract maps, and
    /// its own schema management off; the initializer applies the schema instead, and the tailer
    /// starts after it, once the progression table it writes to exists.
    /// </summary>
    private static IServiceCollection Register(IServiceCollection services, string connectionString, NightingaleOptions options)
    {
        options.Store.Validate();
        services.AddNightingaleOptions(options);
        services.AddPolecat((StoreOptions store) =>
        {
            store.Connection(connectionString);
            store.Events.StreamIdentity = StreamIdentity.AsString;
            store.Events.TenancyStyle = TenancyStyle.Conjoined;
            store.EventGraph.UseTenantPartitionedEvents = options.Store.Partitioning == NightingaleOptions.StoreSettings.PartitioningMode.Tenant;
            store.EventGraph.UseArchivedStreamPartitioning = options.Store.Partitioning == NightingaleOptions.StoreSettings.PartitioningMode.ArchivedStream;
            store.Events.EnableCorrelationId = true;
            store.Events.EnableCausationId = true;
            store.Events.EnableHeaders = true;
            store.AutoCreateSchemaObjects = AutoCreate.None;
            store.Tenancy = new SingleTenancy(new AugmentedPolecatDatabase(store), connectionString);
        }).UseLightweightSessions();

        services.AddSingleton<IStreamStore>(provider => new PolecatStreamStore(provider.GetRequiredService<IDocumentStore>(), connectionString));
        services.AddSingleton(provider => new PolecatStoreTail(provider.GetRequiredService<IDocumentStore>(), connectionString, provider.GetRequiredService<ILoggerFactory>()));
        services.AddSingleton<IStoreTail>(provider => provider.GetRequiredService<PolecatStoreTail>());
        services.AddSingleton<IHostedService>(provider => new StoreInitializer(
            provider.GetRequiredService<IDocumentStore>(),
            connectionString,
            options,
            provider.GetService<TimeProvider>() ?? TimeProvider.System,
            provider.GetRequiredService<ILogger<StoreInitializer>>()));
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<PolecatStoreTail>());
        return services;
    }
}
