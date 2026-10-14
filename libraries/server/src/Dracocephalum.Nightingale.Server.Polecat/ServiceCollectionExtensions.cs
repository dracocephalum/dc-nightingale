using Dracocephalum.Nightingale.Server.Auth;
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
        return Register(services, connectionString, options, ReadOnlyConnection.Resolve(connectionString, options, namedConnectionStrings: null, readOnlyConnectionString));
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
        if (!Enum.IsDefined(options.SchemaMode))
        {
            throw new InvalidOperationException(
                $"{NightingaleOptionsBase.SectionName}:{nameof(NightingaleOptions.SchemaMode)} {options.SchemaMode} is not one of {string.Join(", ", Enum.GetNames<SchemaMode>())}.");
        }

        if (options.MaxEventsPerAppend > PolecatStreamStore.MaxEventsPerAppend)
        {
            throw new InvalidOperationException($"{NightingaleOptionsBase.SectionName}:{nameof(NightingaleOptionsBase.MaxEventsPerAppend)} is {options.MaxEventsPerAppend}; this backend takes at most {PolecatStreamStore.MaxEventsPerAppend} events in one append.");
        }

        if (options.TailPollingInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{NightingaleOptionsBase.SectionName}:{nameof(NightingaleOptions.TailPollingInterval)} must be positive. It was {options.TailPollingInterval}.");
        }

        if (options.IndexMigrationStatementTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{NightingaleOptionsBase.SectionName}:{nameof(NightingaleOptions.IndexMigrationStatementTimeout)} must be positive. It was {options.IndexMigrationStatementTimeout}.");
        }

        if (options.SchemaLockWaitTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{NightingaleOptionsBase.SectionName}:{nameof(NightingaleOptions.SchemaLockWaitTimeout)} must be positive. It was {options.SchemaLockWaitTimeout}.");
        }

        services.AddNightingaleOptions(options);

        // The signal every append pulses, which whichever tail the host runs waits on: the store's
        // high-water agent takes it as the daemon's wake-up, the per-tenant poller in place of its
        // interval. The slow polling time is the host's setting; the fast one is the store's own
        // unless the setting is shorter still, and is what the numbering follower reads on too.
        var slowInterval = options.TailPollingInterval;
        var fastInterval = slowInterval < PolecatTenantTails.FastInterval ? slowInterval : PolecatTenantTails.FastInterval;
        services.AddSingleton(provider => new AppendSignal(provider.GetService<TimeProvider>() ?? TimeProvider.System));

        // How names are compared, as the initializer learns it from the database's collation.
        services.AddSingleton<NameComparison>();
        services.AddPolecat(provider =>
        {
            var store = new StoreOptions();
            Configure(store, connectionString, options);
            store.Tenancy = new SingleTenancy(new AugmentedPolecatDatabase(store, options.Store.AssignOrdinals), connectionString);
            store.Projections.Wakeup = provider.GetRequiredService<AppendSignal>();
            store.Projections.SlowPollingTime = slowInterval;
            if (fastInterval < store.Projections.FastPollingTime)
            {
                store.Projections.FastPollingTime = fastInterval;
            }

            return store;
        }).UseLightweightSessions();

        // The mirror of the store's tables, once per connection: each is a type of its own,
        // so a reader asks for the one it means. The read-only one is always there; with the
        // read-only connection off it reaches the main one and nothing reads through it.
        services.AddSingleton(new EventStoreSchema(options.Store.Schema));
        services.AddDbContextFactory<EventsDbContext>(context => context.UseSqlServer(connectionString));
        services.AddDbContextFactory<ReadOnlyEventsDbContext>(context => context.UseSqlServer(readOnlyConnectionString ?? connectionString));

        // One stream store, one group store and one tail per tenant, made as tenants are served;
        // the default tenant's stream store and tail are also those of a host that resolves no
        // tenant. The tail is the store's own agent followed once while the sequence is shared,
        // and a poll of every served tenant's mark under a sequence per tenant.
        var partitionedByTenant = options.Store.Partitioning == PartitioningMode.Tenant;
        if (partitionedByTenant)
        {
            services.AddSingleton(provider => new PolecatTenantTails(
                new EventRowPositions(provider.GetRequiredService<IDbContextFactory<EventsDbContext>>()),
                provider.GetRequiredService<AppendSignal>(),
                provider.GetService<TimeProvider>() ?? TimeProvider.System,
                provider.GetService<ILogger<PolecatTenantTails>>() ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PolecatTenantTails>.Instance,
                fastInterval,
                slowInterval));
            services.AddSingleton<IStoreTail>(provider => provider.GetRequiredService<PolecatTenantTails>().For(JasperFx.StorageConstants.DefaultTenantId));
        }
        else
        {
            services.AddSingleton(provider => new PolecatStoreTail(provider.GetRequiredService<IDocumentStore>(), provider.GetRequiredService<IDbContextFactory<EventsDbContext>>(), provider.GetRequiredService<AppendSignal>(), provider.GetRequiredService<ILoggerFactory>(), options.TailQuietWait));
            services.AddSingleton<IStoreTail>(provider => provider.GetRequiredService<PolecatStoreTail>());
        }

        services.AddSingleton(provider => new PolecatTenantStores(
            provider.GetRequiredService<IDocumentStore>(),
            provider.GetRequiredService<IDbContextFactory<EventsDbContext>>(),
            options.Store.AssignOrdinals,
            readOnlyConnectionString is null ? null : provider.GetRequiredService<IDbContextFactory<ReadOnlyEventsDbContext>>(),
            readOnlyConnectionString is not null && options.ReadStreamsFromReadOnlyConnection
                ? provider.GetRequiredService<IDbContextFactory<ReadOnlyEventsDbContext>>()
                : null,
            provider.GetRequiredService<IDbContextFactory<NightingaleDbContext>>(),
            !partitionedByTenant,
            partitionedByTenant
                ? provider.GetRequiredService<PolecatTenantTails>().For
                : _ => provider.GetRequiredService<IStoreTail>(),
            provider.GetRequiredService<AppendSignal>(),
            provider.GetRequiredService<NameComparison>(),
            provider.GetService<TimeProvider>() ?? TimeProvider.System,
            provider.GetService<ILogger<PolecatStreamStore>>() ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PolecatStreamStore>.Instance));
        services.AddSingleton<ITenantStores>(provider => provider.GetRequiredService<PolecatTenantStores>());
        services.AddSingleton(provider => provider.GetRequiredService<PolecatTenantStores>().GetStreams(TenantScope.Default));
        services.AddNightingaleSubscriptionGroupStore(options.Schema, JasperFx.StorageConstants.DefaultTenantId, context => NightingaleDbContextFactory.Configure(context, connectionString, options.Schema));
        services.Replace(ServiceDescriptor.Singleton<ITenantProvisioner, PolecatTenantProvisioner>());
        services.AddSingleton<IStoreDatabase>(provider => new StoreDatabase(connectionString, provider.GetService<ILogger<StoreDatabase>>()));
        services.AddSingleton(provider => new StoreSchema(
            provider.GetRequiredService<IDocumentStore>(),
            provider.GetRequiredService<IDbContextFactory<NightingaleDbContext>>(),
            provider.GetRequiredService<IStoreDatabase>(),
            options,
            provider.GetService<TimeProvider>() ?? TimeProvider.System,
            provider.GetService<ILogger<StoreSchema>>() ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<StoreSchema>.Instance));
        services.AddSingleton(provider => new StoreInitializer(
            provider.GetRequiredService<IStoreDatabase>(),
            provider.GetRequiredService<StoreSchema>(),
            options,
            provider.GetRequiredService<NameComparison>(),
            provider.GetService<TimeProvider>() ?? TimeProvider.System,
            provider.GetService<ILogger<StoreInitializer>>() ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<StoreInitializer>.Instance));
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<StoreInitializer>());

        // After the initializer, so the first refresh reads tables that are there.
        services.AddHostedService<TenantRefresher>();
        if (partitionedByTenant)
        {
            services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<PolecatTenantTails>());
        }
        else
        {
            services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<PolecatStoreTail>());
        }

        if (options.Store.AssignOrdinals)
        {
            // After the tailer, so the sequencer's first look at the head is a real one. The
            // registry is the server's, but a host that registers only the backend still numbers.
            services.TryAddSingleton<SubscriptionGroupRegistry>();
            services.AddSingleton<INumberedSequences>(provider => partitionedByTenant
                ? new TenantSequences(provider.GetRequiredService<PolecatTenantTails>(), provider.GetRequiredService<IDbContextFactory<NightingaleDbContext>>())
                : new SharedSequence(provider.GetRequiredService<IStoreTail>(), provider.GetRequiredService<PolecatStoreTail>().Numbering));
            services.AddSingleton<IHostedService>(provider => new OrdinalSequencer(
                provider.GetRequiredService<ISubscriptionGroupStore>(),
                provider.GetRequiredService<INumberedSequences>(),
                new NumberingFollower(
                    provider.GetRequiredService<INumberedSequences>(),
                    new SequencerProgressReader(provider.GetRequiredService<IDbContextFactory<NightingaleDbContext>>()),
                    provider.GetService<TimeProvider>() ?? TimeProvider.System,
                    provider.GetService<ILogger<NumberingFollower>>() ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<NumberingFollower>.Instance,
                    fastInterval),
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
