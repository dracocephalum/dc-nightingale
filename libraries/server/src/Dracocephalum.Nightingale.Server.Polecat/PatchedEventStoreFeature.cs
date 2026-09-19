using Weasel.Core;
using Weasel.Core.Migrations;
using Weasel.SqlServer.Tables;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// The store's event-store feature with the gateway's additions declared on the events table: a
/// persisted computed <c>category</c> column, everything before the first hyphen of the stream name,
/// and two filtered indexes that make a category or event-type read a seek instead of a scan; and,
/// on a store initialized with ordinals, two nullable columns holding each event's place within
/// its category stream and its event-type stream, each with a filtered index over the numbered
/// rows only, so a read by ordinal is one seek and never returns an unnumbered row. They are
/// declared rather than applied by hand because the migration drops any column or index it does
/// not know; declared, they are created when missing and left alone afterwards, and the store's own
/// changes to the table still flow through the same definition. Every index leads with the tenant
/// column so it holds under conjoined tenancy. A test applies the schema twice and asserts the
/// second delta is empty.
/// </summary>
/// <param name="inner">The store's own event-store feature.</param>
/// <param name="ordinals">Whether the ordinal columns and their indexes are part of the table.</param>
internal sealed class PatchedEventStoreFeature(IFeatureSchema inner, bool ordinals) : IFeatureSchema
{
    /// <summary>The identifier the store gives its event-store feature.</summary>
    public const string EventStoreIdentifier = "EventStore";

    /// <summary>The column holding an event's place within its category stream.</summary>
    public const string CategoryOrdinalColumn = "category_ordinal";

    /// <summary>The column holding an event's place within its event-type stream.</summary>
    public const string TypeOrdinalColumn = "type_ordinal";

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

                if (!ordinals)
                {
                    continue;
                }

                // The ordinal indexes are not filtered on the archived flag: the numberer finds the
                // last ordinal of a key through them, and an archived event keeps its ordinal so
                // the next one is never reused. The reader skips archived rows as holes.
                table.AddColumn(CategoryOrdinalColumn, "bigint");
                table.AddColumn(TypeOrdinalColumn, "bigint");
                table.Indexes.Add(new IndexDefinition("ix_pc_events_category_ordinal")
                {
                    Columns = [TenantColumn, "category", CategoryOrdinalColumn],
                    Predicate = CategoryOrdinalColumn + " IS NOT NULL",
                });
                table.Indexes.Add(new IndexDefinition("ix_pc_events_type_ordinal")
                {
                    Columns = [TenantColumn, "type", TypeOrdinalColumn],
                    Predicate = TypeOrdinalColumn + " IS NOT NULL",
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
