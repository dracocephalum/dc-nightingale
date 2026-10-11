using Dracocephalum.Nightingale.Server.Auth;

namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// The stores of a host that keeps one tenant: every scope resolves to the one stream store, the
/// one group store and the one tail registered beside it. A backend that serves many tenants
/// registers its own <see cref="ITenantStores"/> in its place.
/// </summary>
/// <param name="streams">The stream store.</param>
/// <param name="groups">The group store, or <see langword="null"/> in a host that keeps no groups.</param>
/// <param name="tail">The tail, or <see langword="null"/> in a host that follows no head.</param>
public sealed class TenantStores(IStreamStore streams, ISubscriptionGroupStore? groups = null, IStoreTail? tail = null) : ITenantStores
{
    /// <inheritdoc/>
    public bool SupportsAllTenants => true;

    /// <inheritdoc/>
    public IEqualityComparer<string> NameComparer => StringComparer.Ordinal;

    /// <inheritdoc/>
    public IStreamStore GetStreams(TenantScope scope) => streams;

    /// <inheritdoc/>
    public ISubscriptionGroupStore GetGroups(TenantScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.IsWildcard)
        {
            throw new ArgumentException("Persistent subscriptions are per tenant; the wildcard has none.", nameof(scope));
        }

        return groups ?? throw new InvalidOperationException("This host keeps no persistent-subscription groups.");
    }

    /// <inheritdoc/>
    public IStoreTail GetTail(TenantScope scope) => tail ?? throw new InvalidOperationException("This host follows no head.");
}
