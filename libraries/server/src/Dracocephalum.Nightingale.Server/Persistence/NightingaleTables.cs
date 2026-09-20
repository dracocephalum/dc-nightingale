namespace Dracocephalum.Nightingale.Server.Persistence;

/// <summary>
/// Where the gateway's own tables live: the schema the backend's store keeps its tables in, so
/// ours sit beside them. Registered once per host by the backend; the context reads it when it
/// builds its model.
/// </summary>
/// <param name="Schema">The schema name.</param>
public sealed record NightingaleTables(string Schema)
{
    /// <summary>The marker table.</summary>
    public const string StoreTable = "nightingale_store";

    /// <summary>The groups table.</summary>
    public const string GroupsTable = "nightingale_groups";

    /// <summary>The parked messages table.</summary>
    public const string ParkedTable = "nightingale_parked";

    /// <summary>The outbox table: messages due to be delivered to a group again.</summary>
    public const string OutboxTable = "nightingale_outbox";

    /// <summary>The leases table.</summary>
    public const string LeasesTable = "nightingale_leases";

    /// <summary>The sequencer's progress table: one row, the position numbered through.</summary>
    public const string OrdinalsTable = "nightingale_ordinals";
}
