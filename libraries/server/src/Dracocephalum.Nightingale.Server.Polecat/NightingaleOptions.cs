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
    /// provisioned by hand keeps the collation it has, and the setting must match it. Ordinals fit partitioning by tenant by
    /// construction, and are refused with it only until the sequencer follows a high-water mark
    /// per tenant, which the multi-tenancy work brings.
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
