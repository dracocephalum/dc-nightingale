using Dracocephalum.Nightingale.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Dracocephalum.Nightingale.Server.Polecat.Persistence;

/// <summary>
/// A read-only mirror of two tables the store owns: its events, with the columns the gateway
/// adds to them, and the row its high-water mark is kept in. The store creates and migrates
/// both; this context only reads, so the gateway's reads by category, by event type and by
/// position are LINQ a unit test can run on the in-memory provider, where they were SQL text
/// before. The column types are the store's exactly, and the string ones matter: a parameter
/// sent as Unicode to a column that is not makes the database convert the column and scan the
/// index it should seek. A unit test holds every mapped column to the store's own definition.
/// A query selects the columns it needs by name, never a whole row, because the ordinal columns
/// exist only on a store initialized with ordinals.
/// </summary>
/// <param name="options">The options, which name the provider and the connection.</param>
/// <param name="schema">The schema the store's tables live in.</param>
internal sealed class EventsDbContext(DbContextOptions<EventsDbContext> options, string schema) : DbContext(options), ISchemaScoped
{
    /// <summary>The name of the progression row that holds the high-water mark.</summary>
    public const string HighWaterMark = "HighWaterMark";

    /// <summary>Gets the events.</summary>
    public DbSet<EventRow> Events => Set<EventRow>();

    /// <summary>Gets the progression rows; the high-water mark is one of them.</summary>
    public DbSet<ProgressionRow> Progression => Set<ProgressionRow>();

    /// <inheritdoc/>
    public string Schema => schema;

    /// <inheritdoc/>
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.ReplaceService<IModelCacheKeyFactory, SchemaModelCacheKeyFactory>();

    /// <inheritdoc/>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(schema);

        modelBuilder.Entity<EventRow>(row =>
        {
            row.ToTable("pc_events");
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

        modelBuilder.Entity<ProgressionRow>(row =>
        {
            row.ToTable("pc_event_progression");
            row.HasKey(p => p.Name);
            row.Property(p => p.Name).HasColumnName("name").HasColumnType("varchar(200)");
            row.Property(p => p.LastSeqId).HasColumnName("last_seq_id").HasColumnType("bigint");
        });
    }
}
