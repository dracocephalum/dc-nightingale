using Microsoft.EntityFrameworkCore;

namespace Dracocephalum.Nightingale.Server.Persistence;

/// <summary>
/// The gateway's own tables as one model: groups, parked messages, leases, the sequencer's
/// progress and the marker. The model mirrors the tables the backend's schema feature declares,
/// name for name and type for type, and a test holds the two equal; the backend keeps creating
/// the tables, so one initializer applies one schema, and this context never migrates. It
/// deviates on purpose from the toolkit's EF rules for a context that owns its tables: the keys
/// are the natural ones the contract addresses a group by, the names are the store's convention,
/// and the tables sit in the store's schema beside its own. Plain reads and writes go through
/// it; the statements whose shape is the point, the sequencer's batch and the tail's refresh,
/// stay raw in the backend.
/// </summary>
/// <param name="options">The provider and connection, from the backend.</param>
/// <param name="tables">Where the tables live.</param>
public sealed class NightingaleDbContext(DbContextOptions<NightingaleDbContext> options, NightingaleTables tables) : DbContext(options)
{
    private const string Name250 = "varchar(250)";

    /// <summary>Gets the groups.</summary>
    public DbSet<GroupRow> Groups => Set<GroupRow>();

    /// <summary>Gets the parked messages.</summary>
    public DbSet<ParkedRow> Parked => Set<ParkedRow>();

    /// <summary>Gets the outbox: messages due to be delivered again.</summary>
    public DbSet<OutboxRow> Outbox => Set<OutboxRow>();

    /// <summary>Gets the leases.</summary>
    public DbSet<LeaseRow> Leases => Set<LeaseRow>();

    /// <summary>Gets the sequencer's progress.</summary>
    public DbSet<SequencingProgressRow> SequencingProgress => Set<SequencingProgressRow>();

    /// <summary>Gets the marker.</summary>
    public DbSet<StoreMarkerRow> Markers => Set<StoreMarkerRow>();

    /// <inheritdoc/>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema(tables.Schema);

        modelBuilder.Entity<GroupRow>(group =>
        {
            group.ToTable(NightingaleTables.GroupsTable);
            group.HasKey(row => new { row.TenantId, row.Stream, row.GroupName });
            group.Property(row => row.TenantId).HasColumnName("tenant_id").HasColumnType(Name250);
            group.Property(row => row.Stream).HasColumnName("stream").HasColumnType(Name250);
            group.Property(row => row.GroupName).HasColumnName("group_name").HasColumnType(Name250);
            group.Property(row => row.Settings).HasColumnName("settings").HasColumnType("nvarchar(max)");
            group.Property(row => row.CheckpointPosition).HasColumnName("checkpoint_position").HasColumnType("bigint");
            group.Property(row => row.CreatedAt).HasColumnName("created_at").HasColumnType("datetimeoffset");
        });

        modelBuilder.Entity<ParkedRow>(parked =>
        {
            parked.ToTable(NightingaleTables.ParkedTable);
            parked.HasKey(row => new { row.TenantId, row.Stream, row.GroupName, row.Position });
            parked.Property(row => row.TenantId).HasColumnName("tenant_id").HasColumnType(Name250);
            parked.Property(row => row.Stream).HasColumnName("stream").HasColumnType(Name250);
            parked.Property(row => row.GroupName).HasColumnName("group_name").HasColumnType(Name250);
            parked.Property(row => row.Position).HasColumnName("position").HasColumnType("bigint");
            parked.Property(row => row.Revision).HasColumnName("revision").HasColumnType("bigint");
            parked.Property(row => row.Ordinal).HasColumnName("ordinal").HasColumnType("bigint");
            parked.Property(row => row.EventId).HasColumnName("event_id").HasColumnType("uniqueidentifier");
            parked.Property(row => row.Reason).HasColumnName("reason").HasColumnType("nvarchar(1000)");
            parked.Property(row => row.Attempts).HasColumnName("attempts").HasColumnType("int");
            parked.Property(row => row.ParkedAt).HasColumnName("parked_at").HasColumnType("datetimeoffset");
        });

        modelBuilder.Entity<OutboxRow>(outbox =>
        {
            outbox.ToTable(NightingaleTables.OutboxTable);
            outbox.HasKey(row => new { row.TenantId, row.Stream, row.GroupName, row.Position });
            outbox.Property(row => row.TenantId).HasColumnName("tenant_id").HasColumnType(Name250);
            outbox.Property(row => row.Stream).HasColumnName("stream").HasColumnType(Name250);
            outbox.Property(row => row.GroupName).HasColumnName("group_name").HasColumnType(Name250);
            outbox.Property(row => row.Position).HasColumnName("position").HasColumnType("bigint");
            outbox.Property(row => row.Revision).HasColumnName("revision").HasColumnType("bigint");
            outbox.Property(row => row.Ordinal).HasColumnName("ordinal").HasColumnType("bigint");
            outbox.Property(row => row.EventId).HasColumnName("event_id").HasColumnType("uniqueidentifier");
            outbox.Property(row => row.Reason).HasColumnName("reason").HasColumnType("nvarchar(1000)");
            outbox.Property(row => row.Attempts).HasColumnName("attempts").HasColumnType("int");
            outbox.Property(row => row.DueAt).HasColumnName("due_at").HasColumnType("datetimeoffset");
            outbox.Property(row => row.QueuedAt).HasColumnName("queued_at").HasColumnType("datetimeoffset");
        });

        modelBuilder.Entity<LeaseRow>(lease =>
        {
            lease.ToTable(NightingaleTables.LeasesTable);
            lease.HasKey(row => row.Name);
            lease.Property(row => row.Name).HasColumnName("name").HasColumnType("varchar(750)");
            lease.Property(row => row.Owner).HasColumnName("owner").HasColumnType("varchar(64)").IsConcurrencyToken();
            lease.Property(row => row.OwnerAddress).HasColumnName("owner_address").HasColumnType("varchar(500)");
            lease.Property(row => row.ExpiresAt).HasColumnName("expires_at").HasColumnType("datetimeoffset").IsConcurrencyToken();
        });

        modelBuilder.Entity<SequencingProgressRow>(progress =>
        {
            progress.ToTable(NightingaleTables.OrdinalsTable);
            progress.HasKey(row => row.Id);
            progress.Property(row => row.Id).HasColumnName("id").HasColumnType("int").ValueGeneratedNever();
            progress.Property(row => row.NumberedThrough).HasColumnName("numbered_through").HasColumnType("bigint");
        });

        modelBuilder.Entity<StoreMarkerRow>(marker =>
        {
            marker.ToTable(NightingaleTables.StoreTable);
            marker.HasKey(row => row.Id);
            marker.Property(row => row.Id).HasColumnName("id").HasColumnType("int").ValueGeneratedNever();
            marker.Property(row => row.SchemaVersion).HasColumnName("schema_version").HasColumnType("int");
            marker.Property(row => row.Partitioning).HasColumnName("partitioning").HasColumnType("varchar(20)");
            marker.Property(row => row.AssignOrdinals).HasColumnName("assign_ordinals").HasColumnType("bit");
            marker.Property(row => row.SchemaName).HasColumnName("schema_name").HasColumnType("varchar(128)");
            marker.Property(row => row.Collation).HasColumnName("collation").HasColumnType("varchar(128)");
            marker.Property(row => row.CreatedAt).HasColumnName("created_at").HasColumnType("datetimeoffset");
            marker.Property(row => row.CreatedBy).HasColumnName("created_by").HasColumnType("varchar(200)");
        });
    }
}
