using Weasel.Core;
using Weasel.Core.Migrations;
using Weasel.SqlServer;
using Weasel.SqlServer.Tables;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// The gateway's own table in the store, <c>nightingale_store</c>, declared as a feature beside the
/// store's so the same migration creates it and the same assertion notices when it is missing. It
/// holds the one <see cref="StoreMarker"/> row.
/// </summary>
/// <param name="schemaName">The schema the store's tables live in.</param>
internal sealed class StoreMarkerFeature(string schemaName) : FeatureSchemaBase(Identifier, new SqlServerMigrator())
{
    /// <summary>The feature identifier, also the exported script's file name.</summary>
    public new const string Identifier = "NightingaleStore";

    /// <summary>The table name.</summary>
    public const string TableName = "nightingale_store";

    /// <inheritdoc/>
    protected override IEnumerable<ISchemaObject> schemaObjects()
    {
        var table = new Table(new SqlServerObjectName(schemaName, TableName));
        table.AddColumn("id", "int").NotNull().AsPrimaryKey();
        table.AddColumn("schema_version", "int").NotNull();
        table.AddColumn("partitioning", "varchar(20)").NotNull();
        table.AddColumn("collation", "varchar(128)").NotNull();
        table.AddColumn("created_at", "datetimeoffset").NotNull();
        table.AddColumn("created_by", "varchar(200)").NotNull();
        yield return table;
    }
}
