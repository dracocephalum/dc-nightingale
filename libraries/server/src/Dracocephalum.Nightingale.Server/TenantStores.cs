using Dracocephalum.Nightingale.Server.Auth;

namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// The stores of a host that keeps one tenant: every scope resolves to the one stream store and
/// the one group store registered beside it. A backend that serves many tenants registers its
/// own <see cref="ITenantStores"/> in its place.
/// </summary>
/// <param name="streams">The stream store.</param>
/// <param name="groups">The group store, or <see langword="null"/> in a host that keeps no groups.</param>
public sealed class TenantStores(IStreamStore streams, ISubscriptionGroupStore? groups = null) : ITenantStores
{
    /// <inheritdoc/>
    public bool SupportsEveryTenant => true;

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
}
