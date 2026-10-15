using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Dracocephalum.Nightingale.Server.Data;

/// <summary>
/// The gateway's own tables as one model: persistent-subscription groups, their parked events
/// and outbox, the leases, the progress of the gateway's background processes, and the settings
/// the store was initialized with. The context owns these tables. It names no provider and no
/// column type, so each backend generates its own migrations from this one model, in its own
/// project, for its own database; the tables are created and changed by those migrations and by
/// nothing else. They live in a schema of their own, named by the host, beside whatever the
/// event store keeps.
/// </summary>
/// <param name="options">The options, which name the provider and the connection.</param>
/// <param name="schema">The schema the tables live in.</param>
public sealed class NightingaleDbContext(DbContextOptions<NightingaleDbContext> options, NightingaleSchema schema) : DbContext(options), ISchemaScoped
{
    /// <summary>Gets the groups.</summary>
    public DbSet<SubscriptionGroup> SubscriptionGroups => Set<SubscriptionGroup>();

    /// <summary>Gets the parked events.</summary>
    public DbSet<SubscriptionParkedEvent> SubscriptionParkedEvents => Set<SubscriptionParkedEvent>();

    /// <summary>Gets the outbox.</summary>
    public DbSet<SubscriptionOutboxEntry> SubscriptionOutboxEntries => Set<SubscriptionOutboxEntry>();

    /// <summary>Gets the leases.</summary>
    public DbSet<Lease> Leases => Set<Lease>();

    /// <summary>Gets the ordinal sequencer's progress.</summary>
    public DbSet<OrdinalSequencingProgress> OrdinalSequencingProgress => Set<OrdinalSequencingProgress>();

    /// <summary>Gets the ordinals of the virtual streams that are not columns of the events table.</summary>
    public DbSet<OrdinalLink> OrdinalLinks => Set<OrdinalLink>();

    /// <summary>Gets the settings the store was initialized with.</summary>
    public DbSet<StoreProperty> StoreProperties => Set<StoreProperty>();

    /// <summary>Gets the tenants.</summary>
    public DbSet<Tenant> Tenants => Set<Tenant>();

    /// <summary>Gets the credentials.</summary>
    public DbSet<Credential> Credentials => Set<Credential>();

    /// <inheritdoc/>
    public string Schema => schema.Name;

    /// <inheritdoc/>
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.ReplaceService<IModelCacheKeyFactory, SchemaModelCacheKeyFactory>();

    /// <inheritdoc/>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(schema.Name);

        // Names are compared by the database's own rule for which names are the same; nothing
        // here folds case. Every text column is Unicode but two: a tenant and a stream are typed
        // as the event store types its own columns for them, so the same value fits both and
        // compares without conversion. Under the collation the server requires those hold any
        // character too, so no name loses one anywhere. A column that
        // refers to a row of another of these tables is named for that table and is a foreign key
        // to it, never cascading: a group's rows are deleted with it, by the code that deletes it.
        modelBuilder.Entity<SubscriptionGroup>(group =>
        {
            group.ToTable(nameof(SubscriptionGroup));
            group.HasKey(row => row.Id);
            group.Property(row => row.TenantId).HasMaxLength(250).IsUnicode(false);
            group.Property(row => row.Stream).HasMaxLength(250).IsUnicode(false);
            group.Property(row => row.Name).HasMaxLength(250);
            group.Property(row => row.Numbering).HasConversion<string>().HasMaxLength(20);
            group.Property(row => row.ConsumerStrategy).HasConversion<string>().HasMaxLength(20);
            group.Property(row => row.LiveConsumerAddress).HasMaxLength(200);
            group.HasIndex(row => new { row.TenantId, row.Stream, row.Name }).IsUnique();
        });

        modelBuilder.Entity<SubscriptionParkedEvent>(parked =>
        {
            parked.ToTable(nameof(SubscriptionParkedEvent));
            parked.HasKey(row => row.Id);
            parked.Property(row => row.Reason).HasMaxLength(1000);
            parked.HasIndex(row => new { row.SubscriptionGroupId, row.Position }).IsUnique();
            parked.HasOne<SubscriptionGroup>().WithMany().HasForeignKey(row => row.SubscriptionGroupId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SubscriptionOutboxEntry>(outbox =>
        {
            outbox.ToTable(nameof(SubscriptionOutboxEntry));
            outbox.HasKey(row => row.Id);
            outbox.Property(row => row.Reason).HasMaxLength(1000);
            outbox.HasIndex(row => new { row.SubscriptionGroupId, row.Position }).IsUnique();
            outbox.HasOne<SubscriptionGroup>().WithMany().HasForeignKey(row => row.SubscriptionGroupId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Lease>(lease =>
        {
            lease.ToTable(nameof(Lease));
            lease.HasKey(row => row.Id);
            lease.Property(row => row.Name).HasMaxLength(100);
            lease.Property(row => row.Owner).HasMaxLength(64).IsConcurrencyToken();
            lease.Property(row => row.OwnerAddress).HasMaxLength(500);
            lease.Property(row => row.ExpiresAt).IsConcurrencyToken();
            lease.HasIndex(row => row.Name).IsUnique();
        });

        modelBuilder.Entity<OrdinalSequencingProgress>(progress =>
        {
            progress.ToTable(nameof(OrdinalSequencingProgress));
            progress.HasKey(row => row.Id);
            progress.Property(row => row.TenantId).HasMaxLength(250).IsUnicode(false);

            // Unique with no filter: SQL Server's unique index admits one null, which is the shared
            // sequence's row, where the provider's default filter would admit any number of them.
            progress.HasIndex(row => row.TenantId).IsUnique().HasFilter(null);
        });

        modelBuilder.Entity<OrdinalLink>(link =>
        {
            link.ToTable(nameof(OrdinalLink));

            // An extension of the store's events table, keyed as the store's data is, by what it
            // describes, as its class states: one stream's ordinals in order, so a read and a
            // stream's last ordinal are a seek from either end of the key, with the position in
            // the row it lands on. Unique by event within a type too, so a batch can never link an
            // event twice, whatever went wrong before it.
            link.HasKey(row => new { row.Type, row.TenantId, row.Key, row.Ordinal });
            link.HasIndex(row => new { row.Type, row.TenantId, row.SeqId }).IsUnique();

            // The type by number, a byte, as its enum states: it leads the key and the index of a
            // table that grows with the events.
            link.Property(row => row.Type).HasConversion<byte>();
            link.Property(row => row.TenantId).HasMaxLength(250).IsUnicode(false);
            link.Property(row => row.Key).HasMaxLength(250).IsUnicode(false);
        });

        modelBuilder.Entity<Tenant>(tenant =>
        {
            tenant.ToTable(nameof(Tenant));
            tenant.HasKey(row => row.Id);
            tenant.Property(row => row.Name).HasMaxLength(250);
            tenant.Property(row => row.StoreTenantId).HasMaxLength(250).IsUnicode(false);
            tenant.HasIndex(row => row.Name).IsUnique();
            tenant.HasIndex(row => row.StoreTenantId).IsUnique();
        });

        modelBuilder.Entity<Credential>(credential =>
        {
            credential.ToTable(nameof(Credential));
            credential.HasKey(row => row.Id);
            credential.Property(row => row.Name).HasMaxLength(250);
            credential.Property(row => row.PasswordHash).HasMaxLength(500).IsUnicode(false);
            credential.Property(row => row.Role).HasConversion<string>().HasMaxLength(20);
            credential.HasIndex(row => row.Name).IsUnique();
            credential.HasOne<Tenant>().WithMany().HasForeignKey(row => row.TenantId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<StoreProperty>(property =>
        {
            property.ToTable(nameof(StoreProperty));
            property.HasKey(row => row.Id);
            property.Property(row => row.Name).HasMaxLength(200);
            property.Property(row => row.Value).HasMaxLength(1000);
            property.HasIndex(row => row.Name).IsUnique();
        });
    }
}
