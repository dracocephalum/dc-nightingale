using Dracocephalum.Nightingale;
using Dracocephalum.Nightingale.Server.Data;

namespace Dracocephalum.Nightingale.Server;

/// <summary>A persistent-subscription group as the store keeps it.</summary>
/// <param name="Stream">The stream, <c>$all</c>, or a virtual stream.</param>
/// <param name="Group">The group name, unique per stream.</param>
/// <param name="Settings">The settings.</param>
/// <param name="Checkpoint">The last position every event up to which is done, or -1 when none is yet.</param>
public sealed record SubscriptionGroupDefinition(string Stream, string Group, GroupSettings Settings, long Checkpoint)
{
    /// <summary>
    /// Gets the group's id: made when the definition is, written with the row, and what the group
    /// is known by from then on. The names find a group; the id is the group, so its parked
    /// messages, its outbox and its lease never depend on how a name is spelt or compared.
    /// </summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>
    /// Gets the id the store keeps for the group's tenant: set from the row when a group is read,
    /// so its runtime resolves the tenant's stores and a disabled tenant stops its groups alone.
    /// A group store writes its own tenant on creation, whatever a new definition says here.
    /// </summary>
    public string TenantId { get; init; } = Tenant.DefaultStoreTenantId;
}
