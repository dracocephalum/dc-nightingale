using System.Collections.Concurrent;

using Dracocephalum.Nightingale.Server.Auth;
using Dracocephalum.Nightingale.Server.Data;
using Dracocephalum.Nightingale.Server.Polecat.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Polecat;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// The stores of the tenants this instance has served, over the one document store, the one
/// sequence and the gateway's one set of tables: a stream store per tenant, which opens every
/// session for that tenant, and a group store per tenant, which reads and writes that tenant's
/// rows. Both are made on first use and kept for the instance's life; they hold no connection
/// of their own, so keeping one per tenant costs the objects and nothing else. The wildcard scope
/// gets one stream store with no tenant, which reads <c>$all</c> and the virtual streams across
/// tenants through the mirror of the events table. The store over the read-only connection, when
/// the host reads plain streams through it, is made once here and shared.
/// </summary>
/// <param name="store">The document store.</param>
/// <param name="events">Makes the mirror of the store's events table over the main connection.</param>
/// <param name="ordinals">Whether the store was initialized with ordinals.</param>
/// <param name="readOnlyEvents">Makes the same mirror over the read-only connection, or <see langword="null"/> when the host does not read through it.</param>
/// <param name="readOnlyStore">A store over the read-only connection for bounded reads of plain streams, owned here, or <see langword="null"/>.</param>
/// <param name="contexts">Makes the gateway's own context.</param>
/// <param name="supportsAllTenants">Whether positions are coherent across tenants, which they are while the sequence is shared.</param>
/// <param name="timeProvider">The clock.</param>
/// <param name="logger">The logger the stream stores log through.</param>
internal sealed class PolecatTenantStores(
    IDocumentStore store,
    IDbContextFactory<EventsDbContext> events,
    bool ordinals,
    IDbContextFactory<ReadOnlyEventsDbContext>? readOnlyEvents,
    IDocumentStore? readOnlyStore,
    IDbContextFactory<NightingaleDbContext> contexts,
    bool supportsAllTenants,
    TimeProvider timeProvider,
    ILogger<PolecatStreamStore> logger) : ITenantStores, IDisposable, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, PolecatStreamStore> _streams = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SubscriptionGroupStore> _groups = new(StringComparer.Ordinal);
    private readonly Lazy<PolecatStreamStore> _allTenants = new(() => new PolecatStreamStore(store, null, events, ordinals, readOnlyEvents, readOnlyStore, contexts, timeProvider, logger));

    /// <inheritdoc/>
    public bool SupportsAllTenants => supportsAllTenants;

    /// <inheritdoc/>
    public IStreamStore GetStreams(TenantScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.IsWildcard)
        {
            if (!supportsAllTenants)
            {
                throw new ArgumentException("The store numbers each tenant on its own, so no read spans all tenants.", nameof(scope));
            }

            return _allTenants.Value;
        }

        return _streams.GetOrAdd(GetTenantId(scope), id => new PolecatStreamStore(store, id, events, ordinals, readOnlyEvents, readOnlyStore, contexts, timeProvider, logger));
    }

    /// <inheritdoc/>
    public ISubscriptionGroupStore GetGroups(TenantScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.IsWildcard)
        {
            throw new ArgumentException("Persistent subscriptions are per tenant; the wildcard has none.", nameof(scope));
        }

        return _groups.GetOrAdd(GetTenantId(scope), id => new SubscriptionGroupStore(contexts, id, timeProvider));
    }

    /// <inheritdoc/>
    public void Dispose() => readOnlyStore?.Dispose();

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => readOnlyStore?.DisposeAsync() ?? ValueTask.CompletedTask;

    /// <summary>The id the store keeps for the scope's tenant; the store's default tenant for a scope that names none.</summary>
    private static string GetTenantId(TenantScope scope) => scope.StoreTenantId ?? JasperFx.StorageConstants.DefaultTenantId;
}
