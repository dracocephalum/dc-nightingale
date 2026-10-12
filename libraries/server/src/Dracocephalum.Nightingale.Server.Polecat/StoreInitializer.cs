using System.Reflection;

using Dracocephalum.Nightingale.Server.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// Owns the database, at startup and before any request is served, as far as the host's
/// <see cref="SchemaMode"/> lets it. The rule: the server initializes only a database that is
/// empty, and never changes a store while serving it unless told to. In <see cref="SchemaMode.Verify"/>
/// it changes nothing and refuses whatever is not what this server expects, naming the mode or
/// the switch that would fix it; in <see cref="SchemaMode.Migrate"/> it applies what an initialized
/// store is behind on; in <see cref="SchemaMode.Initialize"/> it creates a missing database, with
/// the configured collation, initializes an empty one, created here or provisioned by hand, and
/// finishes an initialization that died half-way. Initializing is the event store's tables and
/// then the gateway's own, while every table is empty, and then every row of the store's record
/// in one transaction, the row saying who initialized it last: the next start reads that row as
/// "initialized", so a record is either complete or absent. An initialized database is refused
/// when it is newer than this server, or when the configuration contradicts the settings it was
/// initialized with. A database with tables that the gateway never initialized is refused
/// outright, it is not ours, unless the tables are all the server's own and all empty but for the
/// migrations' history, which is what an initialization that died leaves: that one is initialized
/// the rest of the way. Every start takes the schema lock, shared to verify and exclusive to
/// change, after the database exists and before it reads anything of it, so instances starting
/// together do the work once and never see it half-done; creating the database is the one step no
/// lock can cover, and a database another instance created first is taken as made. The store's
/// own schema management stays off throughout, because its table ensurer would rebuild the events
/// table from the unpatched definition on first use. What is pending and how it is applied is
/// <see cref="IStoreSchema"/>'s, and the database itself is <see cref="IStoreDatabase"/>'s; this
/// decides when, and holds no connection of its own, so every decision in it is tested without a
/// database.
/// </summary>
/// <param name="database">The database: whether it is there, making it, its collation, its lock.</param>
/// <param name="schema">Reports what the database needs, and applies it.</param>
/// <param name="options">The host's options.</param>
/// <param name="names">Told the database's collation, so that names are matched in process as the database compares them.</param>
/// <param name="timeProvider">The clock the store's creation is stamped with.</param>
/// <param name="logger">The logger.</param>
internal sealed partial class StoreInitializer(IStoreDatabase database, IStoreSchema schema, NightingaleOptions options, NameComparison names, TimeProvider timeProvider, ILogger<StoreInitializer> logger) : IHostedService
{
    /// <summary>The code page SQL Server reports for a UTF-8 collation.</summary>
    private const int Utf8CodePage = 65001;

    private const string ModeOption = "Nightingale:SchemaMode";

    private const string IgnoreOption = "Nightingale:Store:IgnoreCollationCompatibility";

    private const string HowToIgnore = "To use this collation all the same, knowing that, set " + IgnoreOption + " to true.";

    private const string WhyUtf8 = "The event store keeps stream names, event types and tenants in columns that are not Unicode, and under any other collation a character outside the code page is stored as a question mark, so two names in another script become one.";

    private static readonly string HowToInitialize = $"Set {ModeOption} to {SchemaMode.Initialize} to let this server do that at startup, or run the host with {SchemaSwitches.Initialize} once.";

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken) => InitializeAsync(options.SchemaMode, options.FastBoot, cancellationToken);

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Brings the database to a state the server can serve, as far as the mode allows, or refuses, saying why.</summary>
    /// <param name="mode">What may be done to the database.</param>
    /// <param name="fastBoot">Whether a recorded hash equal to this server's stands in for comparing the event store's tables.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the store can be served.</returns>
    /// <exception cref="StoreInitializationException">The store cannot be served; the message says why.</exception>
    public async Task InitializeAsync(SchemaMode mode, bool fastBoot, CancellationToken cancellationToken)
    {
        var name = database.Name;
        if (string.IsNullOrEmpty(name) || string.Equals(name, "master", StringComparison.OrdinalIgnoreCase))
        {
            throw new StoreInitializationException(
                $"The connection string ConnectionStrings:{options.ConnectionStringName} must name the database to use; master is not it.");
        }

        if (!await database.ExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (mode != SchemaMode.Initialize)
            {
                throw new StoreInitializationException(
                    $"Database {name} does not exist and {ModeOption} is {mode}. {HowToInitialize}");
            }

            await CreateDatabaseAsync(cancellationToken).ConfigureAwait(false);
            LogCreatedDatabase(name, options.Store.Collation ?? "the server default");
        }

        // From here on nothing is read or changed but under the lock: another instance that is
        // changing the database holds it exclusively until it is done.
        await using var held = await database.LockSchemaAsync(exclusive: mode != SchemaMode.Verify, cancellationToken).ConfigureAwait(false);
        var report = await schema.ReportAsync(fastBoot, cancellationToken).ConfigureAwait(false);
        if (!report.Initialized)
        {
            if (mode != SchemaMode.Initialize)
            {
                throw new StoreInitializationException(report.InitializationUnfinished
                    ? $"An initialization of database {name} by Nightingale did not finish: the gateway's migrations are applied, and the record of the store is not written. {ModeOption} is {mode}, which does not finish it. {HowToInitialize}"
                    : $"Database {name} has not been initialized by Nightingale and {ModeOption} is {mode}. {HowToInitialize}");
            }

            await InitializeEmptyDatabaseAsync(name, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (report.UnknownMigrations.Count > 0)
        {
            throw new StoreInitializationException(
                $"Database {name} is newer than this server: it has migrations this server does not know, {string.Join(", ", report.UnknownMigrations)}. Run a server at least as new as the one that applied them.");
        }

        if (report.SettingConflicts.Count > 0)
        {
            throw new StoreInitializationException(
                $"Database {name} was initialized by Nightingale with settings the configuration no longer matches: {string.Join("; ", report.SettingConflicts)}. These settings are fixed at initialization; change the configuration back, or initialize a new database.");
        }

        await RequireUtf8CollationAsync(name, cancellationToken).ConfigureAwait(false);
        if (report.StoreChanges is not null || report.PendingMigrations.Count > 0)
        {
            if (mode == SchemaMode.Verify)
            {
                throw new StoreInitializationException(
                    $"The schema of database {name} differs from what this server expects, and {ModeOption} is {mode}. What it would apply follows; set the mode to {SchemaMode.Migrate} or {SchemaMode.Initialize} to let this server apply it at startup, or run the host with {SchemaSwitches.Apply} once.{Environment.NewLine}{report.Describe()}");
            }

            await schema.ApplyAsync(cancellationToken).ConfigureAwait(false);
            await schema.RecordStoreAsync(cancellationToken).ConfigureAwait(false);
            LogAppliedChanges(name, report.PendingMigrations.Count, report.StoreChanges is not null);
        }
        else if (report.StoreRecordStale && mode != SchemaMode.Verify)
        {
            // Current tables under a record that is not this server's: a library upgraded without
            // a change to the tables, or a start that died between applying and recording.
            await schema.RecordStoreAsync(cancellationToken).ConfigureAwait(false);
            LogRecordedStore(name);
        }

        if (report.StoreComparisonSkipped)
        {
            LogStoreComparisonSkipped(name);
        }

        var createdBy = await schema.ReadCreatedByAsync(cancellationToken).ConfigureAwait(false);
        LogServingStore(name, createdBy);
    }

    /// <summary>Creates the database with the configured collation, which is checked against the server's catalog first.</summary>
    private async Task CreateDatabaseAsync(CancellationToken cancellationToken)
    {
        var collation = options.Store.Collation;
        if (collation is not null)
        {
            var codePage = await database.ReadCodePageAsync(collation, cancellationToken).ConfigureAwait(false);
            if (codePage is null)
            {
                throw new StoreInitializationException(
                    $"Nightingale:Store:Collation {collation} is not a collation this SQL Server knows; fn_helpcollations() lists the ones it does. A server older than SQL Server 2019 has no UTF-8 collation: name one it has, and see {IgnoreOption}.");
            }

            if (!options.Store.IgnoreCollationCompatibility && codePage != Utf8CodePage)
            {
                throw new StoreInitializationException(
                    $"Nightingale:Store:Collation {collation} is not a UTF-8 collation. {WhyUtf8} Name one that ends in _UTF8; unset, the server uses {NightingaleOptions.StoreOptions.DefaultCollation}. {HowToIgnore}");
            }
        }

        await database.CreateAsync(collation, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The database's collation, which must be a UTF-8 one whoever created the database; whatever it is, names are compared by it from here on.</summary>
    private async Task<string> RequireUtf8CollationAsync(string name, CancellationToken cancellationToken)
    {
        var described = await database.ReadCollationAsync(cancellationToken).ConfigureAwait(false);
        names.Adopt(described);
        var (collation, codePage) = (described.Name, described.CodePage);
        if (codePage != Utf8CodePage)
        {
            if (options.Store.IgnoreCollationCompatibility)
            {
                LogCollationNotUtf8(name, collation);
                return collation;
            }

            throw new StoreInitializationException(
                $"Database {name} has collation {collation}, which is not a UTF-8 collation. {WhyUtf8} The collation is set when the database is created; let the server create it, or create it with a collation that ends in _UTF8, such as {NightingaleOptions.StoreOptions.DefaultCollation}. {HowToIgnore}");
        }

        return collation;
    }

    private async Task InitializeEmptyDatabaseAsync(string name, CancellationToken cancellationToken)
    {
        var tables = await database.ListTablesAsync(cancellationToken).ConfigureAwait(false);
        if (tables.Count > 0)
        {
            await RequireEmptyTablesOfOursAsync(name, tables, cancellationToken).ConfigureAwait(false);
            LogResumingInitialization(name, tables.Count);
        }

        var collation = await RequireUtf8CollationAsync(name, cancellationToken).ConfigureAwait(false);

        if (options.Store.Collation is not null && !string.Equals(options.Store.Collation, collation, StringComparison.OrdinalIgnoreCase))
        {
            throw new StoreInitializationException(
                $"Database {name} has collation {collation} but Nightingale:Store:Collation is {options.Store.Collation}. The collation is set when the database is created; drop the database and let the server create it, or configure the collation it has.");
        }

        await schema.ApplyAsync(cancellationToken).ConfigureAwait(false);

        // What is recorded is what is configured, with the collation the database actually has.
        await schema.WriteInitializationAsync(
            new StoreSettings
            {
                Schema = options.Store.Schema,
                Collation = collation,
                Partitioning = options.Store.Partitioning,
                AssignOrdinals = options.Store.AssignOrdinals,
            },
            timeProvider.GetUtcNow(),
            typeof(StoreInitializer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
            cancellationToken).ConfigureAwait(false);

        LogInitializedStore(name, options.Store.Schema, options.Schema, collation, options.Store.Partitioning, options.Store.AssignOrdinals);
    }

    /// <summary>
    /// A database with tables and no record of the store is not ours, unless every table is one
    /// this server creates and every one is empty but for the migrations' history: that is what
    /// an initialization that died before its record leaves, before or after the gateway's
    /// migrations, and it is carried on from there, since both halves apply what is missing and
    /// the record is written whole. Anything else is refused.
    /// </summary>
    private async Task RequireEmptyTablesOfOursAsync(string name, IReadOnlyList<DatabaseTable> tables, CancellationToken cancellationToken)
    {
        const string onlyEmpty = "The server only initializes an empty database; point it at one, or at a database it initialized.";
        var ours = await schema.ListOwnTablesAsync(cancellationToken).ConfigureAwait(false);
        var foreign = tables.Where(table => !ours.Contains(table.Name)).Select(table => table.Name.ToString()).ToList();
        if (foreign.Count > 0)
        {
            throw new StoreInitializationException(
                $"Database {name} holds {tables.Count} tables and no record of a store initialized by Nightingale, so it was not initialized by Nightingale; {string.Join(", ", foreign)} are not tables this server creates. {onlyEmpty}");
        }

        var history = new TableName(options.Schema, NightingaleSchema.HistoryTable);
        var holdingRows = tables.Where(table => table.HasRows && table.Name != history).Select(table => table.Name.ToString()).ToList();
        if (holdingRows.Count > 0)
        {
            throw new StoreInitializationException(
                $"Database {name} holds only tables this server creates and no record of a store initialized by Nightingale, as an initialization that did not finish leaves it, but {string.Join(", ", holdingRows)} hold rows, which such an initialization never writes. {onlyEmpty}");
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Database {Database} holds {Tables} tables this server creates, empty but for its migrations' history, and no record of a store, as an initialization that did not finish leaves it; initializing it the rest of the way.")]
    private partial void LogResumingInitialization(string database, int tables);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Database {Database} has collation {Collation}, which is not UTF-8, and the host chose to use it: a character of a name outside its code page is stored as a question mark, so two such names can become one.")]
    private partial void LogCollationNotUtf8(string database, string collation);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created database {Database} with collation {Collation}.")]
    private partial void LogCreatedDatabase(string database, string collation);

    [LoggerMessage(Level = LogLevel.Information, Message = "Initialized the store in database {Database}: the event store in schema {StoreSchema}, the gateway's tables in schema {Schema}, collation {Collation}, partitioning {Partitioning}, ordinals {Ordinals}.")]
    private partial void LogInitializedStore(string database, string storeSchema, string schema, string collation, PartitioningMode partitioning, bool ordinals);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Applied schema changes to database {Database}: {Migrations} of the gateway's migrations, and changes to the event store's tables: {StoreChanged}.")]
    private partial void LogAppliedChanges(string database, int migrations, bool storeChanged);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recorded this server's store library and creation script against database {Database}, whose tables were current under another record.")]
    private partial void LogRecordedStore(string database);

    [LoggerMessage(Level = LogLevel.Information, Message = "Fast boot: the event store's tables in database {Database} were not compared, because the recorded hash of their creation script is this server's. A change made to them outside the server goes unnoticed.")]
    private partial void LogStoreComparisonSkipped(string database);

    [LoggerMessage(Level = LogLevel.Information, Message = "Serving the store in database {Database}, initialized by {CreatedBy}.")]
    private partial void LogServingStore(string database, string createdBy);
}
