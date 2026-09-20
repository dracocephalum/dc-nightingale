using System.Text.RegularExpressions;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// What a host configures for the Polecat backend, bound from the <c>Nightingale</c> configuration
/// section: the settings every Nightingale server has, from the base, plus this backend's own. The
/// whole settings tree is in this file and the base's: a sub-section's type is nested under the
/// property that binds it, so a reader finds every setting, its default and its reason in one
/// place. The connection string itself is not here: it lives under <c>ConnectionStrings</c> like every other
/// connection string in a .NET host, and <see cref="ConnectionStringName"/> says which one.
/// </summary>
public sealed partial class NightingaleOptions : NightingaleOptionsBase
{
    /// <summary>
    /// Gets or sets the name of the entry under <c>ConnectionStrings</c> that names the database.
    /// The connection string must name a database other than <c>master</c>.
    /// </summary>
    public string ConnectionStringName { get; set; } = "Nightingale";

    /// <summary>
    /// Gets or sets a value indicating whether the server creates the database when it does not
    /// exist. Off, a missing database is an error, which is the setting for a production host whose
    /// login is not allowed to create databases and whose database was provisioned empty by hand.
    /// </summary>
    public bool CreateDatabase { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the server applies pending schema changes at startup
    /// to a store it initialized earlier. Off, the server refuses to start when the schema differs
    /// from what this version expects, and names the difference; a newer server against an older
    /// store is the case. On, it applies the change, which on a large store can take long. Nothing
    /// in this setting changes what happens to a store that has not been initialized: that is
    /// always either initialized from empty or refused.
    /// </summary>
    public bool ApplySchemaChanges { get; set; }

    /// <summary>
    /// Gets or sets the settings fixed when the store is initialized and checked on every later
    /// start.
    /// </summary>
    public StoreSettings Store { get; set; } = new();

    /// <summary>
    /// The choices made once, when the store is initialized on an empty database, because each one
    /// shapes the events table and changing it afterwards means rebuilding the table. The server
    /// records them in the store and refuses to start against a store whose record disagrees with
    /// the configuration, so a setting cannot drift under a running system.
    /// </summary>
    public sealed partial class StoreSettings
    {
        /// <summary>
        /// How the events table is partitioned. One value rather than one switch per scheme because
        /// a SQL Server table has exactly one partition scheme, and because the choice is made once,
        /// when the store is initialized: changing it means rebuilding the table, so the server
        /// records it and refuses a configuration that disagrees.
        /// </summary>
        public enum PartitioningMode
        {
            /// <summary>One partition; one global sequence numbers every event.</summary>
            None = 0,

            /// <summary>
            /// A partition per tenant, each tenant numbering its events from its own sequence.
            /// Positions are then per tenant, and a read across all tenants has no coherent
            /// position, so the server refuses the wildcard tenant.
            /// </summary>
            Tenant = 1,

            /// <summary>
            /// Two partitions, live and archived, so the events of deleted streams sit apart and
            /// every read of live events touches only the other. The sequence stays global.
            /// </summary>
            ArchivedStream = 2,
        }

        /// <summary>
        /// Gets or sets the collation the database is created with, or null for the server's
        /// default. It decides whether two stream names that differ only in case are the same
        /// stream: SQL Server's default collations are case-insensitive, a binary collation such as
        /// <c>Latin1_General_100_BIN2</c> is not. Only used when the server creates the database; a
        /// database provisioned by hand keeps its own, and this setting, when given, must match it.
        /// </summary>
        public string? Collation { get; set; }

        /// <summary>Gets or sets how the events table is partitioned.</summary>
        public PartitioningMode Partitioning { get; set; } = PartitioningMode.None;

        /// <summary>
        /// Gets or sets the schema every table lives in, the store's and the gateway's alike, so the
        /// gateway's indexes and the sequencer's batch stay within one schema. The store's default,
        /// <c>dbo</c>. Created by the server with the tables when it does not exist. Fixed at
        /// initialization: a store initialized in one schema is found nowhere else.
        /// </summary>
        public string Schema { get; set; } = "dbo";

        /// <summary>
        /// Gets or sets a value indicating whether the virtual streams are numbered: two columns on
        /// the events table hold each event's dense, zero-based place within its category stream and
        /// its event-type stream, assigned after commit by one sequencer per cluster, so a consumer
        /// can read <c>$ce-</c> and <c>$et-</c> by ordinal and count with a subtraction. Off, an
        /// ordinal read is refused. Fixed at initialization because the columns and their indexes
        /// shape the table; a store that adopts it later needs a backfill, which is pending. Ordinals
        /// are per tenant by construction, so the feature fits partitioning by tenant; it is refused
        /// with it only until the sequencer follows a high-water mark per tenant, which the
        /// multi-tenancy work brings.
        /// </summary>
        public bool AssignOrdinals { get; set; }

        /// <summary>Checks the settings are consistent.</summary>
        /// <exception cref="InvalidOperationException">A setting is not a value it can take.</exception>
        public void Validate()
        {
            if (!Enum.IsDefined(Partitioning))
            {
                throw new InvalidOperationException(
                    $"Nightingale:Store:Partitioning {Partitioning} is not one of {string.Join(", ", Enum.GetNames<PartitioningMode>())}.");
            }

            if (AssignOrdinals && Partitioning == PartitioningMode.Tenant)
            {
                throw new InvalidOperationException(
                    "Nightingale:Store:AssignOrdinals cannot be combined with Nightingale:Store:Partitioning Tenant yet: the sequencer follows one high-water mark, and a sequence per tenant has one per tenant.");
            }

            if (Collation is not null && !CollationName().IsMatch(Collation))
            {
                throw new InvalidOperationException(
                    $"Nightingale:Store:Collation '{Collation}' is not a collation name; a name has letters, digits and underscores only.");
            }

            if (Schema is null || !SchemaName().IsMatch(Schema))
            {
                throw new InvalidOperationException(
                    $"Nightingale:Store:Schema '{Schema}' is not a schema name; a name starts with a letter or underscore and has letters, digits and underscores only.");
            }
        }

        [GeneratedRegex("^[A-Za-z0-9_]{1,128}$")]
        private static partial Regex CollationName();

        [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,127}$")]
        private static partial Regex SchemaName();
    }
}
