using Weasel.Core;
using Weasel.Core.Migrations;
using Weasel.SqlServer;
using Weasel.SqlServer.Tables;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// The gateway's own tables in the store, declared as one feature beside the store's so the same
/// migration creates them and the same assertion notices when one is missing: the marker row that
/// says the store is ours, the persistent-subscription groups with their checkpoints, their parked
/// messages one row each, and the leases that say which instance runs a group.
/// </summary>
/// <param name="schemaName">The schema the store's tables live in.</param>
internal sealed class NightingaleTablesFeature(string schemaName) : FeatureSchemaBase(Identifier, new SqlServerMigrator())
{
    /// <summary>The feature identifier, also the exported script's file name.</summary>
    public new const string Identifier = "Nightingale";

    /// <summary>The marker table.</summary>
    public const string StoreTable = "nightingale_store";

    /// <summary>The groups table.</summary>
    public const string GroupsTable = "nightingale_groups";

    /// <summary>The parked messages table.</summary>
    public const string ParkedTable = "nightingale_parked";

    /// <summary>The leases table.</summary>
    public const string LeasesTable = "nightingale_leases";

    /// <inheritdoc/>
    protected override IEnumerable<ISchemaObject> schemaObjects()
    {
        var store = new Table(new SqlServerObjectName(schemaName, StoreTable));
        store.AddColumn("id", "int").NotNull().AsPrimaryKey();
        store.AddColumn("schema_version", "int").NotNull();
        store.AddColumn("partitioning", "varchar(20)").NotNull();
        store.AddColumn("collation", "varchar(128)").NotNull();
        store.AddColumn("created_at", "datetimeoffset").NotNull();
        store.AddColumn("created_by", "varchar(200)").NotNull();
        yield return store;

        var groups = new Table(new SqlServerObjectName(schemaName, GroupsTable));
        groups.AddColumn("tenant_id", "varchar(250)").NotNull().AsPrimaryKey();
        groups.AddColumn("stream", "varchar(250)").NotNull().AsPrimaryKey();
        groups.AddColumn("group_name", "varchar(250)").NotNull().AsPrimaryKey();
        groups.AddColumn("settings", "nvarchar(max)").NotNull();
        groups.AddColumn("checkpoint_position", "bigint").NotNull();
        groups.AddColumn("created_at", "datetimeoffset").NotNull();
        yield return groups;

        var parked = new Table(new SqlServerObjectName(schemaName, ParkedTable));
        parked.AddColumn("tenant_id", "varchar(250)").NotNull().AsPrimaryKey();
        parked.AddColumn("stream", "varchar(250)").NotNull().AsPrimaryKey();
        parked.AddColumn("group_name", "varchar(250)").NotNull().AsPrimaryKey();
        parked.AddColumn("position", "bigint").NotNull().AsPrimaryKey();
        parked.AddColumn("event_id", "uniqueidentifier").NotNull();
        parked.AddColumn("reason", "nvarchar(1000)").NotNull();
        parked.AddColumn("attempts", "int").NotNull();
        parked.AddColumn("parked_at", "datetimeoffset").NotNull();
        parked.AddColumn("replay", "bit").NotNull();
        yield return parked;

        var leases = new Table(new SqlServerObjectName(schemaName, LeasesTable));
        leases.AddColumn("name", "varchar(750)").NotNull().AsPrimaryKey();
        leases.AddColumn("owner", "varchar(64)").NotNull();
        leases.AddColumn("expires_at", "datetimeoffset").NotNull();
        yield return leases;
    }
}
