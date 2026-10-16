using System.Text.RegularExpressions;

using Dracocephalum.Nightingale.Server.Sequencing;

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
    /// Gets or sets what a start may do to the database. <see cref="SchemaMode.Verify"/>, the
    /// default, checks and refuses: the database is expected to be ready, as a service expects
    /// its migrations to have run elsewhere, and a start against one that is missing, not
    /// initialized, behind or newer says so and names the mode or the switch that would fix it.
    /// <see cref="SchemaMode.Migrate"/> also applies what an initialized store is behind on,
    /// which on a large store can take long. <see cref="SchemaMode.Initialize"/> also creates a
    /// missing database, with the configured collation, and initializes an empty one; it is
    /// idempotent, so a host may leave it on. Each mode takes the schema lock, shared to verify
    /// and exclusive to change, so instances starting together do the work once and never see
    /// it half-done. The default host's switches do the same work once: <c>--apply-schema</c>
    /// as <see cref="SchemaMode.Migrate"/> would, <c>--initialize</c> as
    /// <see cref="SchemaMode.Initialize"/> would.
    /// </summary>
    public SchemaMode SchemaMode { get; set; } = SchemaMode.Verify;

    /// <summary>
    /// Gets or sets how long a batch of the schema that creates or alters an index may run before
    /// it is stopped and the start fails, naming the batch. An hour by default: an index over a
    /// large events table takes long, and one that hangs, behind a lock another session holds
    /// say, must not hang silently for longer. Every other batch runs under the connection's own
    /// command timeout, since nothing else takes long. A change too large for even this is one to
    /// run by hand; the report prints it.
    /// </summary>
    public TimeSpan IndexMigrationStatementTimeout { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Gets or sets how long a batch of the schema may wait for a lock another session holds on
    /// a table before it gives the wait up, to be tried again after as long, six times in all
    /// before the start fails naming the batch. Thirty seconds by default. The wait is bounded
    /// for the sake of the system being served, not of the start: a statement queued for a
    /// table's schema lock behind a long reader has every reader and writer that comes after it
    /// queue behind it, so the table is frozen for as long as the statement waits, and the wait
    /// between tries lets that queue drain. Every batch of both halves runs under it.
    /// </summary>
    public TimeSpan SchemaLockWaitTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets a value indicating whether startup takes the event store's tables as current
    /// when the hash recorded with them is the hash of the creation script this server would
    /// apply, instead of comparing every table with what the store expects. The hash covers the
    /// store library, the schema library under it, the gateway's additions and the settings, so
    /// it changes whenever the expected tables do and the comparison then runs as usual. What it
    /// cannot see is a change made to the tables outside the server, a dropped index say, which
    /// the comparison would have refused at startup and which now shows as an error at the first
    /// statement that needs it. Off by default. The comparison costs seconds the first time the
    /// database server compiles it and a fraction of one after, so this is for a host that starts
    /// often against a database it trusts; the schema report and the schema apply always compare.
    /// </summary>
    public bool FastBoot { get; set; }

    /// <summary>
    /// Gets or sets how long the tails rest between looks at the store once they have caught up,
    /// which is how far behind an append made through another instance a subscriber on this one
    /// can be: an append made through this instance wakes its own tails at once, the others find
    /// it at their next look. A second, the store's own slow polling time, unless the host says
    /// otherwise; a look is one small query per instance, whatever the number of subscriptions,
    /// so a host that wants a quarter of a second across instances pays four such queries a
    /// second for it. While a look left a head short of positions already seen past it, a head
    /// catching up or a gap waiting on its checkpoint, the tails look again after the store's fast
    /// polling time, a quarter of a second, or after this interval when it is shorter. The rule is the same for the
    /// shared sequence and for a sequence per tenant.
    /// </summary>
    public TimeSpan TailPollingInterval { get; set; } = SequenceTails.SlowInterval;

    /// <summary>
    /// Gets or sets how long the ordinal sequencer's lease lasts without renewal, after which another
    /// instance takes over the numbering. The holder renews, and a bystander asks again, at a
    /// third of it. Not bound from configuration: it is here so that a test of one instance
    /// taking over from another does not wait out half a minute.
    /// </summary>
    internal TimeSpan OrdinalSequencerLeaseDuration { get; set; } = OrdinalSequencing.LeaseDuration;

    /// <summary>
    /// Gets or sets a value indicating whether the server opens a second, read-only connection and
    /// serves from it the reads a readable secondary can answer exactly: pages of <c>$all</c> and of
    /// the virtual streams, by position, at or below the high-water mark as that connection sees
    /// it. On by default, because where there is no secondary it costs a second connection pool
    /// and nothing else: the read-only connection reaches the same server. Appends, live delivery,
    /// heads, leases and the sequencer always use the main connection.
    /// </summary>
    public bool UseReadOnlyConnection { get; set; } = true;

    /// <summary>
    /// Gets or sets the name of the entry under <c>ConnectionStrings</c> used for the read-only
    /// connection, exactly as written. Unset, the main connection string is used with its
    /// application intent set to read-only, which a listener with read-only routing sends to a
    /// readable secondary and every other server ignores.
    /// </summary>
    public string? ReadOnlyConnectionStringName { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a bounded read of a plain stream is served from the
    /// read-only connection too. Off by default, because such a read is then only eventually
    /// consistent: a client that reads a stream it has just appended to may not see the append
    /// yet, and an append made on what it read is refused with a revision conflict more often.
    /// Nothing is ever lost or reordered, and an append's expected revision is always checked on
    /// the main connection. Subscriptions to a plain stream are not affected.
    /// </summary>
    public bool ReadStreamsFromReadOnlyConnection { get; set; }

    /// <summary>
    /// Gets or sets the settings fixed when the store is initialized and checked on every later
    /// start.
    /// </summary>
    public StoreOptions Store { get; set; } = new();

    /// <summary>
    /// The store's settings as a host of this backend configures them: the four every store is
    /// initialized with, from the base, and the check that they are values SQL Server and this
    /// backend can take. The schema is the store's default, <c>dbo</c>, unless said otherwise,
    /// and is created with the tables when it does not exist. The collation must be a UTF-8 one,
    /// whichever it is: the event store keeps names in columns that are not Unicode, and under
    /// any other collation a character outside the code page is stored as a question mark, so
    /// two names in another script become one. The server refuses to create a database with
    /// another, and refuses a database provisioned by hand that has another. A database
    /// provisioned by hand keeps the collation it has, and the setting must match it. Ordinals
    /// fit partitioning by tenant by construction: the sequencer then numbers each tenant's
    /// sequence behind that tenant's own sequence head.
    /// </summary>
    public sealed partial class StoreOptions : StoreSettings
    {
        /// <summary>
        /// The collation a database is created with unless the host names another: binary, so two
        /// names that differ in case are two names, as they are in the reference event store and
        /// in PostgreSQL, and UTF-8, so a name holds any character.
        /// </summary>
        public const string DefaultCollation = "Latin1_General_100_BIN2_UTF8";

        /// <summary>
        /// Gets or sets a value indicating whether the server works with a collation that is not
        /// UTF-8. Off, such a collation is refused. On, it is the host's own considered choice,
        /// for a database that already has another collation or a server too old to have a
        /// UTF-8 one, and its consequence is the host's too: a character of a stream name, an
        /// event type, a tenant or a correlation or causation id that is outside the collation's
        /// code page is stored as a question mark, so two such names can become one. Names that
        /// stay within the code page are unaffected. It is a choice of the host and not a
        /// setting of the store, so it is not recorded with the store's settings.
        /// </summary>
        public bool IgnoreCollationCompatibility { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the ordinal sequencer runs, on a store
        /// initialized with ordinals. Off, this instance numbers nothing, and refuses every read
        /// and subscription by ordinal with <c>SEQUENCER_DISABLED</c> rather than leaving it to
        /// wait; on again, the sequencer goes on from where it stopped. The operator's choice of
        /// the moment rather than a setting of the store, so it is not recorded with the store's
        /// settings, and each instance follows its own. On by default.
        /// </summary>
        public bool OrdinalSequencerEnabled { get; set; } = true;

        /// <summary>Initializes a new instance of the <see cref="StoreOptions"/> class with this backend's defaults.</summary>
        public StoreOptions()
        {
            Collation = DefaultCollation;
        }

        /// <summary>Checks the settings are consistent.</summary>
        /// <exception cref="InvalidOperationException">A setting is not a value it can take.</exception>
        public void Validate()
        {
            if (!Enum.IsDefined(Partitioning))
            {
                throw new InvalidOperationException(
                    $"Nightingale:Store:Partitioning {Partitioning} is not one of {string.Join(", ", Enum.GetNames<PartitioningMode>())}.");
            }

            if (Collation is not null && !CollationNameRegex().IsMatch(Collation))
            {
                throw new InvalidOperationException(
                    $"Nightingale:Store:Collation '{Collation}' is not a collation name; a name has letters, digits and underscores only.");
            }

            if (Schema is null || !SchemaNameRegex().IsMatch(Schema))
            {
                throw new InvalidOperationException(
                    $"Nightingale:Store:Schema '{Schema}' is not a schema name; a name starts with a letter or underscore and has letters, digits and underscores only.");
            }
        }

        [GeneratedRegex("^[A-Za-z0-9_]{1,128}$")]
        private static partial Regex CollationNameRegex();

        [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,127}$")]
        private static partial Regex SchemaNameRegex();
    }
}
