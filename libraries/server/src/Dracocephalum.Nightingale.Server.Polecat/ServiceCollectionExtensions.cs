using Dracocephalum.Nightingale.Server.Data;
using Dracocephalum.Nightingale.Server.Polecat.Data;
using JasperFx;
using JasperFx.Events;
using JasperFx.MultiTenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

        return Register(services, connectionString, options, ReadOnlyConnection.Resolve(connectionString, options, configuration.GetConnectionString));
    }

    /// <summary>
    /// Registers the store as the backend, for a host that has the connection string in hand.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">The SQL Server connection string, naming the database to use.</param>
    /// <param name="configure">Adjusts the options; the defaults create a missing database with the server's default collation and no partitioning.</param>
    /// <param name="readOnlyConnectionString">The connection string of the read-only connection, used as written; unset, the main one with a read-only application intent, unless the options turn the read-only connection off.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddNightingalePolecat(this IServiceCollection services, string connectionString, Action<NightingaleOptions>? configure = null, string? readOnlyConnectionString = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var options = new NightingaleOptions();
        configure?.Invoke(options);
        return Register(services, connectionString, options, ReadOnlyConnection.Resolve(connectionString, options, named: null, readOnlyConnectionString));
    }

    /// <summary>
    /// The store is configured with string stream identity, conjoined tenancy with the default
    /// tenant, the partitioning the settings ask for, the metadata columns the contract maps, and
    /// its own schema management off; the initializer applies the schema instead, and the tailer
    /// starts after it, once the progression table it writes to exists.
    /// </summary>
    private static IServiceCollection Register(IServiceCollection services, string connectionString, NightingaleOptions options, string? readOnlyConnectionString)
    {
        options.Store.Validate();
        if (options.MaxEventsPerAppend > PolecatStreamStore.MaxEventsPerAppend)
        {
            throw new InvalidOperationException($"{NightingaleOptionsBase.SectionName}:{nameof(NightingaleOptionsBase.MaxEventsPerAppend)} is {options.MaxEventsPerAppend}; this backend takes at most {PolecatStreamStore.MaxEventsPerAppend} events in one append.");
        }

        services.AddNightingaleOptions(options);
        services.AddPolecat((StoreOptions store) =>
        {
            Configure(store, connectionString, options);
            store.Tenancy = new SingleTenancy(new AugmentedPolecatDatabase(store, options.Store.AssignOrdinals), connectionString);
        }).UseLightweightSessions();

        // The second store exists only for bounded reads of plain streams on the read-only
        // connection, and only when the host asks for them: it reads and never migrates.
        // The mirror of the store's events table, once per connection: each is a type of its own,
        // so a reader asks for the one it means. The read-only one is always there; with the
        // read-only connection off it reaches the main one and nothing reads through it.
        services.AddSingleton(new EventStoreSchema(options.Store.Schema));
        services.AddDbContextFactory<EventsDbContext>(context => context.UseSqlServer(connectionString));
        services.AddDbContextFactory<ReadOnlyEventsDbContext>(context => context.UseSqlServer(readOnlyConnectionString ?? connectionString));
        services.AddSingleton<IStreamStore>(provider => new PolecatStreamStore(
            provider.GetRequiredService<IDocumentStore>(),
            provider.GetRequiredService<IDbContextFactory<EventsDbContext>>(),
            options.Store.AssignOrdinals,
            readOnlyConnectionString is null ? null : provider.GetRequiredService<IDbContextFactory<ReadOnlyEventsDbContext>>(),
            readOnlyConnectionString is not null && options.ReadStreamsFromReadOnlyConnection
                ? DocumentStore.For(store => Configure(store, readOnlyConnectionString, options))
                : null,
            provider.GetRequiredService<IDbContextFactory<NightingaleDbContext>>(),
            provider.GetService<TimeProvider>() ?? TimeProvider.System,
            provider.GetService<ILogger<PolecatStreamStore>>() ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PolecatStreamStore>.Instance));
        services.AddSingleton(provider => new PolecatStoreTail(provider.GetRequiredService<IDocumentStore>(), provider.GetRequiredService<IDbContextFactory<EventsDbContext>>(), provider.GetRequiredService<ILoggerFactory>(), options.TailQuietWait));
        services.AddSingleton<IStoreTail>(provider => provider.GetRequiredService<PolecatStoreTail>());
        services.AddNightingaleSubscriptionGroupStore(options.Schema, JasperFx.StorageConstants.DefaultTenantId, context => NightingaleDbContextFactory.Configure(context, connectionString, options.Schema));
        services.AddSingleton<IStoreDatabase>(new StoreDatabase(connectionString));
        services.AddSingleton(provider => new StoreSchema(
            provider.GetRequiredService<IDocumentStore>(),
            provider.GetRequiredService<IDbContextFactory<NightingaleDbContext>>(),
            provider.GetRequiredService<IStoreDatabase>(),
            options));
        services.AddSingleton(provider => new StoreInitializer(
            provider.GetRequiredService<IStoreDatabase>(),
            provider.GetRequiredService<StoreSchema>(),
            options,
            provider.GetService<TimeProvider>() ?? TimeProvider.System,
            provider.GetService<ILogger<StoreInitializer>>() ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<StoreInitializer>.Instance));
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<StoreInitializer>());
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<PolecatStoreTail>());
        if (options.Store.AssignOrdinals)
        {
            // After the tailer, so the sequencer's first look at the head is a real one. The
            // registry is the server's, but a host that registers only the backend still numbers.
            services.TryAddSingleton<SubscriptionGroupRegistry>();
            services.AddSingleton<IHostedService>(provider => new OrdinalSequencer(
                provider.GetRequiredService<ISubscriptionGroupStore>(),
                provider.GetRequiredService<IStoreTail>(),
                provider.GetRequiredService<SubscriptionGroupRegistry>(),
                connectionString,
                provider.GetRequiredService<IDocumentStore>().Options.DatabaseSchemaName,
                options.Schema,
                provider.GetService<TimeProvider>() ?? TimeProvider.System,
                provider.GetRequiredService<ILogger<OrdinalSequencer>>(),
                options.SequencerLeaseDuration));
        }

        return services;
    }

    private static void Configure(StoreOptions store, string connectionString, NightingaleOptions options)
    {
        store.Connection(connectionString);
        store.DatabaseSchemaName = options.Store.Schema;
        store.Events.StreamIdentity = StreamIdentity.AsString;
        store.Events.TenancyStyle = TenancyStyle.Conjoined;
        store.EventGraph.UseTenantPartitionedEvents = options.Store.Partitioning == PartitioningMode.Tenant;
        store.EventGraph.UseArchivedStreamPartitioning = options.Store.Partitioning == PartitioningMode.ArchivedStream;
        store.Events.EnableCorrelationId = true;
        store.Events.EnableCausationId = true;
        store.Events.EnableHeaders = true;
        store.AutoCreateSchemaObjects = AutoCreate.None;
    }
}
