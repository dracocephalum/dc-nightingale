using System.Data;
using System.Globalization;
using System.Reflection;

using JasperFx;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Polecat;
using Weasel.Core.Migrations;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// Owns the database, at startup and before any request is served. The rule: the server
/// initializes only a database that is empty, and never migrates a store while serving it.
/// A missing database is created, with the configured collation, when the host allows it. An empty
/// database, created here or provisioned by hand, gets the whole schema in one pass while every
/// table is empty, and a marker row recording the settings that shaped it. A database with a
/// marker is compared with the configuration and refused on any difference, then its schema is
/// asserted to match what this server expects. A database with tables and no marker is refused
/// outright: it is not ours. The store's own schema management stays off throughout, because its
/// table ensurer would rebuild the events table from the unpatched definition on first use.
/// </summary>
/// <param name="store">The store whose tenancy answers the patched database.</param>
/// <param name="connectionString">The connection string the store uses.</param>
/// <param name="options">The host's options.</param>
/// <param name="timeProvider">The clock the marker is stamped with.</param>
/// <param name="logger">The logger.</param>
internal sealed partial class StoreInitializer(IDocumentStore store, string connectionString, NightingaleOptions options, TimeProvider timeProvider, ILogger<StoreInitializer> logger) : IHostedService
{
    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        options.Store.Validate();
        var target = new SqlConnectionStringBuilder(connectionString);
        var name = target.InitialCatalog;
        if (string.IsNullOrEmpty(name) || string.Equals(name, "master", StringComparison.OrdinalIgnoreCase))
        {
            throw new StoreInitializationException(
                $"The connection string ConnectionStrings:{options.ConnectionStringName} must name the database to use; master is not it.");
        }

        var master = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" }.ConnectionString;
        if (!await DatabaseExistsAsync(master, name, cancellationToken).ConfigureAwait(false))
        {
            if (!options.CreateDatabase)
            {
                throw new StoreInitializationException(
                    $"Database {name} does not exist and Nightingale:CreateDatabase is false. Create it empty, or set Nightingale:CreateDatabase to true.");
            }

            await CreateDatabaseAsync(master, name, options.Store.Collation, cancellationToken).ConfigureAwait(false);
            LogCreatedDatabase(name, options.Store.Collation ?? "the server default");
        }

        var databases = await store.Options.Tenancy!.BuildDatabasesAsync(cancellationToken).ConfigureAwait(false);
        var marker = await ReadMarkerAsync(cancellationToken).ConfigureAwait(false);
        if (marker is null)
        {
            await InitializeEmptyDatabaseAsync(name, databases, cancellationToken).ConfigureAwait(false);
            return;
        }

        var differences = marker.DifferencesFrom(options.Store);
        if (differences.Count > 0)
        {
            throw new StoreInitializationException(
                $"Database {name} was initialized by Nightingale with settings the configuration no longer matches: {string.Join("; ", differences)}. These settings are fixed at initialization; change the configuration back, or initialize a new database.");
        }

        foreach (var database in databases)
        {
            if (options.ApplySchemaChanges)
            {
                await database.ApplyAllConfiguredChangesToDatabaseAsync(AutoCreate.CreateOrUpdate, ct: cancellationToken).ConfigureAwait(false);
                await StampSchemaVersionAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                await database.AssertDatabaseMatchesConfigurationAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DatabaseValidationException exception)
            {
                throw new StoreInitializationException(
                    $"The schema of database {name} differs from what this server expects, and Nightingale:ApplySchemaChanges is false. The change it would apply follows; set the option to true to let this server apply it at startup.{Environment.NewLine}{exception.Message}",
                    exception);
            }
        }

        LogServingStore(name, marker.SchemaVersion, marker.CreatedBy);
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static async Task<bool> DatabaseExistsAsync(string master, string name, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(master);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DB_ID(@name)";
        command.Parameters.AddWithValue("@name", name);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is not null and not DBNull;
    }

    private static async Task CreateDatabaseAsync(string master, string name, string? collation, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(master);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // The name is quoted as an identifier and the collation is checked against the server's
        // catalog before it is spliced in; both come from the host's own configuration, never from
        // a request.
        if (collation is not null)
        {
            await using var check = connection.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM fn_helpcollations() WHERE name = @collation";
            check.Parameters.AddWithValue("@collation", collation);
            var known = (int)(await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
            if (known == 0)
            {
                throw new StoreInitializationException(
                    $"Nightingale:Store:Collation {collation} is not a collation this SQL Server knows; fn_helpcollations() lists the ones it does.");
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

    private async Task InitializeEmptyDatabaseAsync(string name, IReadOnlyList<global::Polecat.Storage.PolecatDatabase> databases, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using (var count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped = 0";
            var tables = (int)(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
            if (tables > 0)
            {
                throw new StoreInitializationException(
                    $"Database {name} holds {tables} tables and no Nightingale marker, so it was not initialized by Nightingale. The server only initializes an empty database; point it at one, or at a database it initialized.");
            }
        }

        string collation;
        await using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT CONVERT(varchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation'))";
            collation = (string)(await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        if (options.Store.Collation is not null && !string.Equals(options.Store.Collation, collation, StringComparison.OrdinalIgnoreCase))
        {
            throw new StoreInitializationException(
                $"Database {name} has collation {collation} but Nightingale:Store:Collation is {options.Store.Collation}. The collation is set when the database is created; drop the database and let the server create it, or configure the collation it has.");
        }

        foreach (var database in databases)
        {
            await database.ApplyAllConfiguredChangesToDatabaseAsync(AutoCreate.CreateOrUpdate, ct: cancellationToken).ConfigureAwait(false);
        }

        var marker = new StoreMarker(
            StoreMarker.CurrentSchemaVersion,
            options.Store.Partitioning,
            collation,
            timeProvider.GetUtcNow(),
            typeof(StoreInitializer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown");
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = string.Format(
                CultureInfo.InvariantCulture,
                "INSERT INTO {0} (id, schema_version, partitioning, collation, created_at, created_by) VALUES (1, @version, @partitioning, @collation, @created_at, @created_by)",
                MarkerTable);
            insert.Parameters.AddWithValue("@version", marker.SchemaVersion);
            insert.Parameters.AddWithValue("@partitioning", marker.Partitioning.ToString());
            insert.Parameters.AddWithValue("@collation", marker.Collation);
            insert.Parameters.AddWithValue("@created_at", marker.CreatedAt);
            insert.Parameters.AddWithValue("@created_by", marker.CreatedBy);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        LogInitializedStore(name, collation, marker.Partitioning);
    }

    /// <summary>After a change is applied, the marker says which version of the schema the store now has.</summary>
    private async Task StampSchemaVersionAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = string.Format(CultureInfo.InvariantCulture, "UPDATE {0} SET schema_version = @version WHERE id = 1 AND schema_version < @version", MarkerTable);
        command.Parameters.AddWithValue("@version", StoreMarker.CurrentSchemaVersion);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<StoreMarker?> ReadMarkerAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using (var exists = connection.CreateCommand())
        {
            exists.CommandText = "SELECT OBJECT_ID(@table, 'U')";
            exists.Parameters.AddWithValue("@table", MarkerTable);
            var id = await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (id is null or DBNull)
            {
                return null;
            }
        }

        await using var select = connection.CreateCommand();
        select.CommandText = string.Format(
            CultureInfo.InvariantCulture,
            "SELECT schema_version, partitioning, collation, created_at, created_by FROM {0} WHERE id = 1",
            MarkerTable);
        await using var reader = await select.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new StoreMarker(
            reader.GetInt32(0),
            Enum.Parse<NightingaleOptions.StoreSettings.PartitioningMode>(reader.GetString(1)),
            reader.GetString(2),
            reader.GetFieldValue<DateTimeOffset>(3),
            reader.GetString(4));
    }

    // The schema name comes from the store's options, never from a request; it is bracketed as an
    // identifier all the same.
    private string MarkerTable =>
        $"[{store.Options.DatabaseSchemaName.Replace("]", "]]", StringComparison.Ordinal)}].[{NightingaleTablesFeature.StoreTable}]";

    [LoggerMessage(Level = LogLevel.Information, Message = "Created database {Database} with collation {Collation}.")]
    private partial void LogCreatedDatabase(string database, string collation);

    [LoggerMessage(Level = LogLevel.Information, Message = "Initialized the store in database {Database}: collation {Collation}, partitioning {Partitioning}.")]
    private partial void LogInitializedStore(string database, string collation, NightingaleOptions.StoreSettings.PartitioningMode partitioning);

    [LoggerMessage(Level = LogLevel.Information, Message = "Serving the store in database {Database}, schema version {SchemaVersion}, initialized by {CreatedBy}.")]
    private partial void LogServingStore(string database, int schemaVersion, string createdBy);
}
