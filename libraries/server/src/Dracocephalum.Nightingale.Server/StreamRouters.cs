using Dracocephalum.Nightingale.Server.Auth;

namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// The routers of the process, one per tenant's store, made the first time a subscription in
/// that tenant follows a plain stream and kept for the life of the process, as the stores are.
/// Keyed by the store itself rather than by the tenant's name, so every scope that resolves to
/// the same store shares the one router, whatever the backend calls its default tenant.
/// </summary>
/// <param name="stores">The backend's stores and tails.</param>
public sealed class StreamRouters(ITenantStores stores) : IAsyncDisposable
{
    private readonly Dictionary<IStreamStore, StreamRouter> _routers = new(ReferenceEqualityComparer.Instance);
    private readonly Lock _gate = new();
    private bool _disposed;

    /// <summary>The router of a scope's store.</summary>
    /// <param name="scope">The tenant the call works in.</param>
    /// <returns>The router.</returns>
    public StreamRouter For(TenantScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var store = stores.GetStreams(scope);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_routers.TryGetValue(store, out var router))
            {
                router = new StreamRouter(store, stores.GetTail(scope));
                _routers[store] = router;
            }

            return router;
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        StreamRouter[] routers;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            routers = [.. _routers.Values];
        }

        foreach (var router in routers)
        {
            await router.DisposeAsync().ConfigureAwait(false);
        }
    }
}
