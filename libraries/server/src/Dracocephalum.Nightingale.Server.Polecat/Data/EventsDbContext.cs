using Dracocephalum.Nightingale.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Dracocephalum.Nightingale.Server.Polecat.Data;

/// <summary>
/// A read-only mirror of three tables the store owns: its events, with the columns the gateway
/// adds to them, its streams, and the row its high-water mark is kept in. The store creates and
/// migrates them; this context only reads, and refuses to save, so the gateway's reads of a
/// stream, by category, by event type and by position are LINQ a unit test can run on the
/// in-memory provider. The column
/// types are the store's exactly, and the string ones matter: a parameter sent as Unicode to a
/// column that is not makes the database convert the column and scan the index it should seek.
/// A unit test holds every mapped column to the store's own definition. A query selects the
/// columns it needs by name, never a whole row, because the ordinal columns exist only on a
/// store initialized with ordinals. This type reads over the main connection;
/// <see cref="ReadOnlyEventsDbContext"/> is the same mirror over the read-only one.
/// </summary>
internal class EventsDbContext : DbContext, ISchemaScoped
{
    /// <summary>The name of the progression row that holds the high-water mark.</summary>
    public const string HighWaterMark = "HighWaterMark";

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
    }

    /// <summary>Gets the events.</summary>
    public DbSet<EventRow> EventRows => Set<EventRow>();

    /// <summary>Gets the streams.</summary>
    public DbSet<StreamRow> StreamRows => Set<StreamRow>();

    /// <summary>Gets the progression rows; the high-water mark is one of them.</summary>
    public DbSet<ProgressionRow> ProgressionRows => Set<ProgressionRow>();

    /// <inheritdoc/>
    public string Schema { get; }

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
            row.Property(e => e.Type).HasColumnName("type").HasColumnType("varchar(500)");
            row.Property(e => e.Timestamp).HasColumnName("timestamp").HasColumnType("datetimeoffset");
            row.Property(e => e.TenantId).HasColumnName("tenant_id").HasColumnType("varchar(250)");
            row.Property(e => e.CorrelationId).HasColumnName("correlation_id").HasColumnType("varchar(250)");
            row.Property(e => e.CausationId).HasColumnName("causation_id").HasColumnType("varchar(250)");
            row.Property(e => e.Headers).HasColumnName("headers").HasColumnType("json");
            row.Property(e => e.IsArchived).HasColumnName("is_archived").HasColumnType("bit");
            row.Property(e => e.Category).HasColumnName("category").HasColumnType("varchar(250)");
            row.Property(e => e.CategoryOrdinal).HasColumnName(PatchedEventStoreFeature.CategoryOrdinalColumn).HasColumnType("bigint");
            row.Property(e => e.TypeOrdinal).HasColumnName(PatchedEventStoreFeature.TypeOrdinalColumn).HasColumnType("bigint");
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
        });

        modelBuilder.Entity<ProgressionRow>(row =>
        {
            row.ToTable("pc_event_progression");
            row.HasKey(p => p.Name);
            row.Property(p => p.Name).HasColumnName("name").HasColumnType("varchar(200)");
            row.Property(p => p.LastSeqId).HasColumnName("last_seq_id").HasColumnType("bigint");
        });
    }

    private static InvalidOperationException CreateReadOnlyException() =>
        new("The mirror of the store's events table only reads: the store appends events and the sequencer numbers them.");
}
