using Microsoft.Extensions.DependencyInjection;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// What a host calls to see what a database needs, and to bring it there, without starting the
/// server: a deployment step, an operator's tool, or the default host's own switches. All of it
/// goes through the same code startup does, so a report says exactly what startup would refuse
/// or apply, and the two applies do what the two modes that change the database do at startup,
/// whatever <c>Nightingale:SchemaMode</c> says.
/// </summary>
public static class NightingaleSchemaExtensions
{
    /// <summary>
    /// Reports where the configured database stands against what this server expects; changes
    /// nothing. The event store's tables are always compared, whatever <c>Nightingale:FastBoot</c> says.
    /// </summary>
    /// <param name="services">The host's services, with the backend registered.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The report.</returns>
    public static Task<SchemaReport> GetNightingaleSchemaReportAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.GetRequiredService<StoreSchema>().ReportAsync(trustStoreHash: false, cancellationToken);
    }

    /// <summary>
    /// Applies what an initialized database is behind on, as <see cref="SchemaMode.Migrate"/>
    /// does at startup: the event store's changes first and the gateway's migrations after. A
    /// database that is missing or not initialized is refused, as are a store newer than this
    /// server and one whose settings the configuration contradicts. The event store's tables are
    /// always compared, whatever <c>Nightingale:FastBoot</c> says.
    /// </summary>
    /// <param name="services">The host's services, with the backend registered.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the database is current.</returns>
    /// <exception cref="StoreInitializationException">The database cannot be brought up to date; the message says why.</exception>
    public static Task ApplyNightingaleSchemaAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.GetRequiredService<StoreInitializer>().InitializeAsync(SchemaMode.Migrate, fastBoot: false, cancellationToken);
    }

    /// <summary>
    /// Brings the configured database to what this server expects from wherever it is, as
    /// <see cref="SchemaMode.Initialize"/> does at startup: a missing database is created, an
    /// empty one is initialized, an initialization that died half-way is finished, and an
    /// initialized one gets what it is behind on. A database that is not Nightingale's, a store
    /// newer than this server, and a store whose settings the configuration contradicts are
    /// refused as they are at startup. The event store's tables are always compared, whatever
    /// <c>Nightingale:FastBoot</c> says.
    /// </summary>
    /// <param name="services">The host's services, with the backend registered.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the database is current.</returns>
    /// <exception cref="StoreInitializationException">The database cannot be brought up to date; the message says why.</exception>
    public static Task InitializeNightingaleStoreAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.GetRequiredService<StoreInitializer>().InitializeAsync(SchemaMode.Initialize, fastBoot: false, cancellationToken);
    }
}
