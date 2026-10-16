using Dracocephalum.Nightingale.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Dracocephalum.Nightingale.Server.Polecat.Data;

/// <summary>
/// A read-only mirror of the tables the store owns: its events, with the columns the gateway
/// adds to them, its streams, and, partitioned by tenant, the registry of each tenant's partition
/// key; and of the two gateway tables read beside them, the ordinal links and the sequence heads.
/// The store creates and
/// migrates them; this context only reads, and refuses to save, so the gateway's reads of a
/// stream, by category, by event type and by position are LINQ a unit test can run on the
/// in-memory provider. The column
/// types are the store's exactly, and the string ones matter: a parameter sent as Unicode to a
/// column that is not makes the database convert the column and scan the index it should seek.
/// A unit test holds every mapped column to the store's own definition. A query selects the
/// columns it needs by name, never a whole row, because the partition key exists only on a
/// store partitioned by tenant. This type reads over the main connection;
/// <see cref="ReadOnlyEventsDbContext"/> is the same mirror over the read-only one.
/// </summary>
internal class EventsDbContext : DbContext, ISchemaScoped
{
    /// <summary>The store's partitioning column on a store partitioned by tenant: the tenant's partition key, which the store calls an ordinal and the gateway does not.</summary>
    public const string TenantPartitionColumn = "tenant_ordinal";

    /// <summary>Initializes a new instance of the <see cref="EventsDbContext"/> class over the main connection.</summary>
    /// <param name="options">The options, which name the provider and the connection.</param>
    /// <param name="schema">The schema the store's tables live in.</param>
    public EventsDbContext(DbContextOptions<EventsDbContext> options, EventStoreSchema schema)
        : this((DbContextOptions)options, schema)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="EventsDbContext"/> class for a derived mirror.</summary>
    /// <param name="options">The derived context's options.</param>
    /// <param name="schema">The schema the store's tables live in.</param>
    protected EventsDbContext(DbContextOptions options, EventStoreSchema schema)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(schema);
        Schema = schema.Name;
        GatewaySchema = schema.GatewayName;
    }

    /// <summary>Gets the events.</summary>
    public DbSet<EventRow> EventRows => Set<EventRow>();

    /// <summary>Gets the streams.</summary>
    public DbSet<StreamRow> StreamRows => Set<StreamRow>();

    /// <summary>Gets the tenants' partition keys, on a store partitioned by tenant only.</summary>
    public DbSet<TenantPartitionRow> TenantPartitionRows => Set<TenantPartitionRow>();

    /// <summary>Gets the ordinals of the correlations and the listings, from the gateway's own table.</summary>
    public DbSet<OrdinalLink> OrdinalLinks => Set<OrdinalLink>();

    /// <summary>Gets the sequence heads, from the gateway's own table.</summary>
    public DbSet<SequenceHead> SequenceHeads => Set<SequenceHead>();

    /// <inheritdoc/>
    public string Schema { get; }

    /// <summary>Gets the schema the gateway's own tables live in, the ordinal links among them.</summary>
    public string GatewaySchema { get; }

    /// <inheritdoc/>
    public object ModelKey => (Schema, GatewaySchema);

    /// <inheritdoc/>
    public override int SaveChanges(bool acceptAllChangesOnSuccess) => throw CreateReadOnlyException();

    /// <inheritdoc/>
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default) => throw CreateReadOnlyException();

    /// <inheritdoc/>
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .ReplaceService<IModelCacheKeyFactory, SchemaModelCacheKeyFactory>();

    /// <inheritdoc/>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<EventRow>(row =>
        {
            row.ToTable("pc_events");

            // The store's own key. Under a sequence per tenant a position repeats across tenants
            // and the store keys the table with its partition column too; the mirror's queries
            // never resolve identity, selecting columns by name, so the position serves here.
            row.HasKey(e => e.SeqId);
            row.Property(e => e.SeqId).HasColumnName("seq_id").HasColumnType("bigint").ValueGeneratedNever();
            row.Property(e => e.Id).HasColumnName("id").HasColumnType("uniqueidentifier");
            row.Property(e => e.StreamId).HasColumnName("stream_id").HasColumnType("varchar(250)");
            row.Property(e => e.Version).HasColumnName("version").HasColumnType("bigint");
            row.Property(e => e.Data).HasColumnName("data").HasColumnType("json");
            row.Property(e => e.Bdata).HasColumnName("bdata").HasColumnType("varbinary(max)");
            row.Property(e => e.Type).HasColumnName("type").HasColumnType("varchar(500)");
            row.Property(e => e.Timestamp).HasColumnName("timestamp").HasColumnType("datetimeoffset");
            row.Property(e => e.TenantId).HasColumnName("tenant_id").HasColumnType("varchar(250)");
            row.Property(e => e.CorrelationId).HasColumnName("correlation_id").HasColumnType("varchar(250)");
            row.Property(e => e.CausationId).HasColumnName("causation_id").HasColumnType("varchar(250)");
            row.Property(e => e.Headers).HasColumnName("headers").HasColumnType("json");
            row.Property(e => e.IsArchived).HasColumnName("is_archived").HasColumnType("bit");
            row.Property(e => e.Category).HasColumnName("category").HasColumnType("varchar(250)");

            // Only on a store partitioned by tenant; a query names it only there.
            row.Property(e => e.PartitionKey).HasColumnName(TenantPartitionColumn).HasColumnType("int");
        });

        modelBuilder.Entity<StreamRow>(row =>
        {
            row.ToTable("pc_streams");

            // The store's own key under conjoined tenancy, the tenant first. Partitioned by tenant,
            // the store adds the tenant's ordinal after these two; the pair still identifies a row.
            row.HasKey(s => new { s.TenantId, s.Id });
            row.Property(s => s.TenantId).HasColumnName("tenant_id").HasColumnType("varchar(250)");
            row.Property(s => s.Id).HasColumnName("id").HasColumnType("varchar(250)");
            row.Property(s => s.Version).HasColumnName("version").HasColumnType("bigint");
            row.Property(s => s.IsArchived).HasColumnName("is_archived").HasColumnType("bit");
            row.Property(s => s.CompactedVersion).HasColumnName("compacted_version").HasColumnType("bigint");
            row.Property(s => s.PartitionKey).HasColumnName(TenantPartitionColumn).HasColumnType("int");
        });

        // The registry of a store partitioned by tenant, which exists only there.
        modelBuilder.Entity<TenantPartitionRow>(row =>
        {
            row.ToTable("pc_tenant_partitions");
            row.HasKey(p => p.TenantId);
            row.Property(p => p.TenantId).HasColumnName("tenant_id").HasColumnType("varchar(200)");
            row.Property(p => p.PartitionKey).HasColumnName("ordinal").HasColumnType("int");
        });

        // The gateway's table, not the store's, read beside the events it points at: the
        // gateway's migrations create it and the sequencer fills it. Typed as those migrations
        // type it, so the key and the tenant compare without conversion.
        modelBuilder.Entity<OrdinalLink>(link =>
        {
            link.ToTable(nameof(OrdinalLink), GatewaySchema);
            link.HasKey(l => new { l.Type, l.TenantId, l.Key, l.Ordinal });
            link.Property(l => l.Type).HasConversion<byte>().HasColumnType("tinyint");
            link.Property(l => l.TenantId).HasColumnType("varchar(250)");
            link.Property(l => l.Key).HasColumnType("varchar(500)");
            link.Property(l => l.Ordinal).HasColumnType("bigint");
            link.Property(l => l.SeqId).HasColumnType("bigint");
        });

        // The gateway's table the tails keep their heads in, read on the read-only connection as
        // the bound of what the replica holds, since a head is written after the events it covers.
        modelBuilder.Entity<SequenceHead>(head =>
        {
            head.ToTable(nameof(SequenceHead), GatewaySchema);
            head.HasKey(h => h.Id);
            head.Property(h => h.TenantId).HasColumnType("varchar(250)");
            head.Property(h => h.Position).HasColumnType("bigint");
            head.Property(h => h.CheckedAt).HasColumnType("datetimeoffset");
        });
    }

    private static InvalidOperationException CreateReadOnlyException() =>
        new("The mirror of the store's events table only reads: the store appends events and the sequencer numbers them.");
}
