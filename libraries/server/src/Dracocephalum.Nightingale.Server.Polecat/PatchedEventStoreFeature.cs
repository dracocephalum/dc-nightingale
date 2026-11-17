using Weasel.Core;
using Weasel.Core.Migrations;
using Weasel.SqlServer.Tables;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// The store's event-store feature with the gateway's additions declared on the events table: a
/// persisted computed <c>category</c> column, the stream name's category by <see
/// cref="StreamNames.CategoryOf"/>, and the filtered indexes the virtual streams and the listings
/// seek, every one leading with the tenant column so it holds under conjoined tenancy. The ordinals
/// are not here: they are the gateway's own links (<c>OrdinalLink</c>), so the table is the same
/// with them and without. Why declared rather than applied by hand, and which index serves what,
/// are <c>DESIGN.md</c>, seam 2.
/// </summary>
/// <param name="inner">The store's own event-store feature.</param>
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
