using Weasel.Core;
using Weasel.Core.Migrations;
using Weasel.SqlServer.Tables;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// The store's event-store feature with the gateway's additions declared on the events table: a
/// persisted computed <c>category</c> column, everything before the first hyphen of the stream name,
/// and two filtered indexes that make a category or event-type read a seek instead of a scan. They
/// are declared rather than applied by hand because the migration drops any column or index it does
/// not know; declared, they are created when missing and left alone afterwards, and the store's own
/// changes to the table still flow through the same definition. Both indexes lead with the tenant
/// column so they hold under conjoined tenancy. A test applies the schema twice and asserts the
/// second delta is empty.
/// </summary>
internal sealed class PatchedEventStoreFeature(IFeatureSchema inner) : IFeatureSchema
{
    /// <summary>The identifier the store gives its event-store feature.</summary>
    public const string EventStoreIdentifier = "EventStore";

    private const string EventsTable = "pc_events";
    private const string TenantColumn = "tenant_id";

    /// <inheritdoc/>
    public ISchemaObject[] Objects
    {
        get
        {
            var objects = inner.Objects;
            foreach (var table in objects.OfType<Table>().Where(table => table.Identifier.Name == EventsTable))
            {
                table.AddColumn("category", "varchar(250)")
                    .ComputedAs("LEFT(stream_id, CHARINDEX('-', stream_id + '-') - 1)", persisted: true);
                table.Indexes.Add(new IndexDefinition("ix_pc_events_category_seq")
                {
                    Columns = [TenantColumn, "category", "seq_id"],
                    Predicate = "is_archived = 0",
                });
                table.Indexes.Add(new IndexDefinition("ix_pc_events_type_seq")
                {
                    Columns = [TenantColumn, "type", "seq_id"],
                    Predicate = "is_archived = 0",
                });
            }

            return objects;
        }
    }

    /// <inheritdoc/>
    public string Identifier => inner.Identifier;

    /// <inheritdoc/>
    public Migrator Migrator => inner.Migrator;

    /// <inheritdoc/>
    public Type StorageType => inner.StorageType;

    /// <inheritdoc/>
    public void WritePermissions(Migrator rules, TextWriter writer) => inner.WritePermissions(rules, writer);

    /// <inheritdoc/>
    public IEnumerable<Type> DependentTypes() => inner.DependentTypes();
}
