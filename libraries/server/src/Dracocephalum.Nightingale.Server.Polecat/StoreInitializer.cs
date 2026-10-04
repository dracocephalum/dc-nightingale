using System.Globalization;
using System.Reflection;

using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// Owns the database, at startup and before any request is served. The rule: the server
/// initializes only a database that is empty, and never changes a store while serving it unless
/// told to. A missing database is created, with the configured collation, when the host allows
/// it. An empty database, created here or provisioned by hand, gets the event store's tables and
/// then the gateway's own, while every table is empty, and rows recording the settings that
/// shaped it. An initialized database is refused when it is newer than this server, or when the
/// configuration contradicts the settings it was initialized with; when its tables are behind
/// what this server expects it is refused too, naming what would be applied, unless the host
/// said to apply it. A database with tables that the gateway never initialized is refused
/// outright: it is not ours. The store's own schema management stays off throughout, because its
/// table ensurer would rebuild the events table from the unpatched definition on first use.
/// What is pending and how it is applied is <see cref="StoreSchema"/>'s; this decides when.
/// </summary>
/// <param name="schema">Reports what the database needs, and applies it.</param>
/// <param name="connectionString">The connection string the store uses.</param>
/// <param name="options">The host's options.</param>
/// <param name="timeProvider">The clock the store's creation is stamped with.</param>
/// <param name="logger">The logger.</param>
internal sealed partial class StoreInitializer(StoreSchema schema, string connectionString, NightingaleOptions options, TimeProvider timeProvider, ILogger<StoreInitializer> logger) : IHostedService
{
    /// <summary>The code page SQL Server reports for a UTF-8 collation.</summary>
    private const int Utf8CodePage = 65001;

    private const string IgnoreOption = "Nightingale:Store:IgnoreCollationCompatibility";

    private const string HowToIgnore = "To use this collation all the same, knowing that, set " + IgnoreOption + " to true.";

    private const string WhyUtf8 = "The event store keeps stream names, event types and tenants in columns that are not Unicode, and under any other collation a character outside the code page is stored as a question mark, so two names in another script become one.";

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken) => InitializeAsync(options.ApplySchemaChanges, cancellationToken);

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Brings the database to a state the server can serve, or refuses, saying why.</summary>
    /// <param name="applyChanges">Whether changes an initialized store needs are applied, or refused.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the store can be served.</returns>
    /// <exception cref="StoreInitializationException">The store cannot be served; the message says why.</exception>
    public async Task InitializeAsync(bool applyChanges, CancellationToken cancellationToken)
    {
        var target = new SqlConnectionStringBuilder(connectionString);
        var name = target.InitialCatalog;
        if (string.IsNullOrEmpty(name) || string.Equals(name, "master", StringComparison.OrdinalIgnoreCase))
        {
            throw new StoreInitializationException(
                $"The connection string ConnectionStrings:{options.ConnectionStringName} must name the database to use; master is not it.");
        }

        var master = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" }.ConnectionString;
        if (!await schema.DatabaseExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!options.CreateDatabase)
            {
                throw new StoreInitializationException(
                    $"Database {name} does not exist and Nightingale:CreateDatabase is false. Create it empty, or set Nightingale:CreateDatabase to true.");
            }

            await CreateDatabaseAsync(master, name, options.Store.Collation, !options.Store.IgnoreCollationCompatibility, cancellationToken).ConfigureAwait(false);
            LogCreatedDatabase(name, options.Store.Collation ?? "the server default");
        }

        var report = await schema.ReportAsync(cancellationToken).ConfigureAwait(false);
        if (!report.Initialized)
        {
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
            if (!applyChanges)
            {
                throw new StoreInitializationException(
                    $"The schema of database {name} differs from what this server expects, and Nightingale:ApplySchemaChanges is false. What it would apply follows; set the option to true to let this server apply it at startup, or run the host with {SchemaSwitches.Apply}.{Environment.NewLine}{report.Describe()}");
            }

            await schema.ApplyAsync(cancellationToken).ConfigureAwait(false);
            LogAppliedChanges(name, report.PendingMigrations.Count, report.StoreChanges is not null);
        }

        var createdBy = await schema.ReadCreatedByAsync(cancellationToken).ConfigureAwait(false);
        LogServingStore(name, createdBy);
    }

    private static async Task CreateDatabaseAsync(string master, string name, string? collation, bool requireUtf8, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(master);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // The name is quoted as an identifier and the collation is checked against the server's
        // catalog before it is spliced in; both come from the host's own configuration, never from
        // a request.
        if (collation is not null)
        {
            await using var check = connection.CreateCommand();
            check.CommandText = "SELECT COLLATIONPROPERTY(@collation, 'CodePage')";
            check.Parameters.AddWithValue("@collation", collation);
            var codePage = await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (codePage is null or DBNull)
            {
                throw new StoreInitializationException(
                    $"Nightingale:Store:Collation {collation} is not a collation this SQL Server knows; fn_helpcollations() lists the ones it does. A server older than SQL Server 2019 has no UTF-8 collation: name one it has, and see {IgnoreOption}.");
            }

            if (requireUtf8 && Convert.ToInt32(codePage, CultureInfo.InvariantCulture) != Utf8CodePage)
            {
                throw new StoreInitializationException(
                    $"Nightingale:Store:Collation {collation} is not a UTF-8 collation. {WhyUtf8} Name one that ends in _UTF8; unset, the server uses {NightingaleOptions.StoreOptions.DefaultCollation}. {HowToIgnore}");
            }
        }

        await using var command = connection.CreateCommand();
        command.CommandText = collation is null
            ? "DECLARE @sql nvarchar(400) = N'CREATE DATABASE ' + QUOTENAME(@name); EXEC(@sql);"
            : "DECLARE @sql nvarchar(600) = N'CREATE DATABASE ' + QUOTENAME(@name) + N' COLLATE ' + @collation; EXEC(@sql);";
        command.Parameters.AddWithValue("@name", name);
        if (collation is not null)
        {
            command.Parameters.AddWithValue("@collation", collation);
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The database's collation, which must be a UTF-8 one whoever created the database.</summary>
    private async Task<string> RequireUtf8CollationAsync(string name, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var read = connection.CreateCommand();
        read.CommandText = "SELECT CONVERT(varchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation')), COLLATIONPROPERTY(CONVERT(varchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation')), 'CodePage')";
        await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var collation = reader.GetString(0);
        if (reader.IsDBNull(1) || Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture) != Utf8CodePage)
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
        await using (var connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (var count = connection.CreateCommand())
            {
                count.CommandText = "SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped = 0";
                var tables = (int)(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
                if (tables > 0)
                {
                    throw new StoreInitializationException(
                        $"Database {name} holds {tables} tables and none of Nightingale's migrations, so it was not initialized by Nightingale. The server only initializes an empty database; point it at one, or at a database it initialized.");
                }
            }
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Database {Database} has collation {Collation}, which is not UTF-8, and the host chose to use it: a character of a name outside its code page is stored as a question mark, so two such names can become one.")]
    private partial void LogCollationNotUtf8(string database, string collation);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created database {Database} with collation {Collation}.")]
    private partial void LogCreatedDatabase(string database, string collation);

    [LoggerMessage(Level = LogLevel.Information, Message = "Initialized the store in database {Database}: the event store in schema {StoreSchema}, the gateway's tables in schema {Schema}, collation {Collation}, partitioning {Partitioning}, ordinals {Ordinals}.")]
    private partial void LogInitializedStore(string database, string storeSchema, string schema, string collation, PartitioningMode partitioning, bool ordinals);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Applied schema changes to database {Database}: {Migrations} of the gateway's migrations, and changes to the event store's tables: {StoreChanged}.")]
    private partial void LogAppliedChanges(string database, int migrations, bool storeChanged);

    [LoggerMessage(Level = LogLevel.Information, Message = "Serving the store in database {Database}, initialized by {CreatedBy}.")]
    private partial void LogServingStore(string database, string createdBy);
}
