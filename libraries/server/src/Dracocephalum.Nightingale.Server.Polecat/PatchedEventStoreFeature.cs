using Weasel.Core;
using Weasel.Core.Migrations;
using Weasel.SqlServer.Tables;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// The store's event-store feature with the gateway's additions declared on the events table: a
/// persisted computed <c>category</c> column, the stream name's category by
/// <see cref="StreamNames.CategoryOf"/>; filtered indexes that make a read of a category, an event
/// type or a correlation a seek instead of a scan, the correlation's holding only the events that
/// carry one; two more holding one row a stream, its first event, for <c>$streams</c> and
/// <c>$category-</c>; and,
/// on a store initialized with ordinals, two nullable columns holding each event's place within
/// its category stream and its event-type stream, each with a filtered index over the numbered
/// rows only, so a read by ordinal is one seek and never returns an unnumbered row, and a third
/// over the unsequenced rows by position, so the sequencer finds what it has yet to reach, the
/// lag and any row committed late below its progress, without a scan. They are
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
                    .ComputedAs("CASE WHEN CHARINDEX('-', stream_id) > 1 THEN LEFT(stream_id, CHARINDEX('-', stream_id) - 1) ELSE stream_id END", persisted: true);
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

                // A row only for an event that carries a correlation id: an append without one
                // writes nothing here, and an equality on the column implies it is not null, so a
                // read of one correlation seeks the filtered index.
                table.Indexes.Add(new IndexDefinition("ix_pc_events_correlation_seq")
                {
                    Columns = [TenantColumn, "correlation_id", "seq_id"],
                    Predicate = "correlation_id IS NOT NULL AND is_archived = 0",
                });

                // A row a stream, its first event: what $streams and $category- read, and what
                // only an append that begins a stream writes.
                table.Indexes.Add(new IndexDefinition("ix_pc_events_stream_starts")
                {
                    Columns = [TenantColumn, "seq_id"],
                    Predicate = "version = 1 AND is_archived = 0",
                });
                table.Indexes.Add(new IndexDefinition("ix_pc_events_category_starts")
                {
                    Columns = [TenantColumn, "category", "seq_id"],
                    Predicate = "version = 1 AND is_archived = 0",
                });

                if (!ordinals)
                {
                    continue;
                }

                // The ordinal indexes are not filtered on the archived flag: the sequencer finds the
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

                // The rows the sequencer has not reached, by position: a handful at any time, its
                // lag, so the index is small whatever the table's size. One index serves both
                // ordinals, because a batch assigns the two together, so a row without the one
                // is a row without the other.
                table.Indexes.Add(new IndexDefinition("ix_pc_events_unsequenced")
                {
                    Columns = [TenantColumn, "seq_id"],
                    Predicate = CategoryOrdinalColumn + " IS NULL",
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
