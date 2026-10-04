namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// How the events table is partitioned. One value rather than one switch per scheme because a
/// table has exactly one partition scheme, and because the choice is made once, when the store is
/// initialized: changing it means rebuilding the table, so the server records it and refuses a
/// configuration that disagrees.
/// </summary>
public enum PartitioningMode
{
    /// <summary>One partition; one global sequence numbers every event.</summary>
    None = 0,

    /// <summary>
    /// A partition per tenant, each tenant numbering its events from its own sequence. Positions
    /// are then per tenant, and a read across all tenants has no coherent position, so the server
    /// refuses the wildcard tenant.
    /// </summary>
    Tenant = 1,

    /// <summary>
    /// Two partitions, live and archived, so the events of deleted streams sit apart and every
    /// read of live events touches only the other. The sequence stays global.
    /// </summary>
    ArchivedStream = 2,
}
