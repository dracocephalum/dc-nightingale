using Dracocephalum.Nightingale.Server.Auth;

namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// The port a call resolves its stores through: the stream store, the group store and the tail
/// of the tenant the call works in, which <see cref="Authorizer"/> decided. A backend keeps one
/// of each per tenant it has served, over the one database, so a store is cheap to hand out and
/// every read and write within it is the tenant's. The tail is the one tail of the process while
/// the sequence is shared, and the tenant's own under a sequence per tenant, where each tenant
/// has a high-water mark of its own. The wildcard scope resolves to a stream store over all
/// tenants, for <c>$all</c> and the virtual streams on a read; a stream name is per tenant and a
/// group is per tenant, so neither is served under it. A host without a backend registers
/// <see cref="TenantStores"/>, which hands every scope the one store and the one tail it has.
/// </summary>
public interface ITenantStores
{
    /// <summary>
    /// Gets a value indicating whether a read may span all tenants: the store keeps one sequence
    /// across them, so positions are coherent under the wildcard. False with a sequence per
    /// tenant, where the server says so through its features and refuses the wildcard.
    /// </summary>
    bool SupportsAllTenants { get; }

    /// <summary>
    /// Gets how the backend compares names: stream names, categories and event types, as its reads
    /// compare them. What the router matches in process with, so that a name matches live as it
    /// matches on a read. Ordinal unless the backend says otherwise.
    /// </summary>
    IEqualityComparer<string> NameComparer { get; }

    /// <summary>Gets the stream store of a scope.</summary>
    /// <param name="scope">The tenant the call works in, or all tenants.</param>
    /// <returns>The store.</returns>
    IStreamStore GetStreams(TenantScope scope);

    /// <summary>Gets the group store of a scope.</summary>
    /// <param name="scope">The tenant the call works in.</param>
    /// <returns>The store.</returns>
    /// <exception cref="ArgumentException">The scope is the wildcard; groups are per tenant.</exception>
    ISubscriptionGroupStore GetGroups(TenantScope scope);

    /// <summary>Gets the tail a scope's reads and subscriptions follow: the head of the sequence the scope's positions are numbered in.</summary>
    /// <param name="scope">The tenant the call works in, or all tenants.</param>
    /// <returns>The tail.</returns>
    /// <exception cref="ArgumentException">The scope is the wildcard and the store numbers each tenant on its own, so no one head spans them.</exception>
    IStoreTail GetTail(TenantScope scope);
}
