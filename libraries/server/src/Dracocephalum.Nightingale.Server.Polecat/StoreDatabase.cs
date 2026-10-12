using System.Globalization;

using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>The database on SQL Server, reached through the store's connection string and, for what must not connect to it, the server's master database.</summary>
/// <param name="connectionString">The connection string the store uses.</param>
/// <param name="logger">The logger; silent when not given.</param>
internal sealed partial class StoreDatabase(string connectionString, ILogger<StoreDatabase>? logger = null) : IStoreDatabase
{
    /// <summary>The name of the schema lock, one per database: whoever holds it exclusively is the one instance changing the database.</summary>
    internal const string SchemaLockResource = "Nightingale.Schema";

    /// <summary>The error SQL Server raises for a <c>CREATE DATABASE</c> of a name that is taken.</summary>
    private const int DatabaseExistsError = 1801;

    private readonly string _master = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" }.ConnectionString;
    private readonly string _unpooled = new SqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;
    private readonly ILogger<StoreDatabase> _logger = logger ?? NullLogger<StoreDatabase>.Instance;

    /// <inheritdoc/>
    public string Name { get; } = new SqlConnectionStringBuilder(connectionString).InitialCatalog;

    /// <inheritdoc/>
    public async Task<bool> ExistsAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_master);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DB_ID(@name)";
        command.Parameters.AddWithValue("@name", Name);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is not null and not DBNull;
    }

    /// <inheritdoc/>
    public async Task<int?> ReadCodePageAsync(string collation, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_master);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COLLATIONPROPERTY(@collation, 'CodePage')";
        command.Parameters.AddWithValue("@collation", collation);
        var codePage = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return codePage is null or DBNull ? null : Convert.ToInt32(codePage, CultureInfo.InvariantCulture);
    }

    /// <inheritdoc/>
    public async Task CreateAsync(string? collation, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_master);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // The name is quoted as an identifier, and the collation is one the caller has checked
        // against the server's catalog before it is spliced in; both come from the host's own
        // configuration, never from a request.
        await using var command = connection.CreateCommand();
        command.CommandText = collation is null
            ? "DECLARE @sql nvarchar(400) = N'CREATE DATABASE ' + QUOTENAME(@name); EXEC(@sql);"
            : "DECLARE @sql nvarchar(600) = N'CREATE DATABASE ' + QUOTENAME(@name) + N' COLLATE ' + @collation; EXEC(@sql);";
        command.Parameters.AddWithValue("@name", Name);
        if (collation is not null)
        {
            command.Parameters.AddWithValue("@collation", collation);
        }

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException exception) when (exception.Number == DatabaseExistsError)
        {
            LogCreatedElsewhere(Name);
        }
    }

    /// <inheritdoc/>
    public async Task<IAsyncDisposable> LockSchemaAsync(bool exclusive, CancellationToken cancellationToken)
    {
        var mode = exclusive ? "Exclusive" : "Shared";
        var connection = new SqlConnection(_unpooled);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            // Asked for at once first, so that a wait behind another instance is said out loud
            // before it is waited out; the second request waits for as long as it takes.
            if (await RequestLockAsync(connection, mode, waitForever: false, cancellationToken).ConfigureAwait(false) < 0)
            {
                LogWaitingForLock(Name, mode);
                var result = await RequestLockAsync(connection, mode, waitForever: true, cancellationToken).ConfigureAwait(false);
                if (result < 0)
                {
                    throw new StoreInitializationException($"The schema lock on database {Name} could not be taken: sp_getapplock answered {result}.");
                }
            }

            return new SchemaLock(connection);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<DatabaseCollation> ReadCollationAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DECLARE @collation varchar(128) = CONVERT(varchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation'));"
            + " SELECT @collation, COLLATIONPROPERTY(@collation, 'CodePage'), COLLATIONPROPERTY(@collation, 'ComparisonStyle'), COLLATIONPROPERTY(@collation, 'LCID')";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new DatabaseCollation(reader.GetString(0), ReadInt(reader, 1), ReadInt(reader, 2), ReadInt(reader, 3));
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DatabaseTable>> ListTablesAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Whether a table holds a row is asked of the table, one statement each built from the
        // catalog with every name quoted; the row count the catalog keeps is an estimate. The
        // batch is empty, and answers nothing, when the database holds no table.
        await using var command = connection.CreateCommand();
        command.CommandText =
            "DECLARE @sql nvarchar(max) = (SELECT STRING_AGG(CONVERT(nvarchar(max),"
            + " N'SELECT ' + QUOTENAME(s.name, '''') + N', ' + QUOTENAME(t.name, '''')"
            + " + N', CASE WHEN EXISTS (SELECT 1 FROM ' + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name) + N') THEN 1 ELSE 0 END'), N' UNION ALL ')"
            + " FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE t.is_ms_shipped = 0);"
            + " IF @sql IS NOT NULL EXEC sp_executesql @sql;";
        var tables = new List<DatabaseTable>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tables.Add(new DatabaseTable(new TableName(reader.GetString(0), reader.GetString(1)), reader.GetInt32(2) == 1));
        }

        return tables.OrderBy(table => table.Name.Schema, StringComparer.Ordinal).ThenBy(table => table.Name.Name, StringComparer.Ordinal).ToList();
    }

    private static int? ReadInt(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    /// <summary>Asks for the lock on the session; what comes back is sp_getapplock's answer, 0 or 1 when granted and negative when not.</summary>
    private static async Task<int> RequestLockAsync(SqlConnection connection, string mode, bool waitForever, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "DECLARE @result int; EXEC @result = sp_getapplock @Resource = @resource, @LockMode = @mode, @LockOwner = 'Session', @LockTimeout = @timeout; SELECT @result;";
        command.CommandTimeout = 0;
        command.Parameters.AddWithValue("@resource", SchemaLockResource);
        command.Parameters.AddWithValue("@mode", mode);
        command.Parameters.AddWithValue("@timeout", waitForever ? -1 : 0);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Database {Database} was created by another instance in the meantime; taking it as made.")]
    private partial void LogCreatedElsewhere(string database);

    [LoggerMessage(Level = LogLevel.Information, Message = "Waiting for the schema lock on database {Database} ({Mode}): another instance is working on the schema.")]
    private partial void LogWaitingForLock(string database, string mode);

    /// <summary>The lock as held: releasing it closes the connection it lives on, which was nobody else's.</summary>
    private sealed class SchemaLock(SqlConnection connection) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "EXEC sp_releaseapplock @Resource = @resource, @LockOwner = 'Session';";
                command.Parameters.AddWithValue("@resource", SchemaLockResource);
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            catch (SqlException)
            {
                // The connection is gone, and the lock with it.
            }
            catch (InvalidOperationException)
            {
                // Same: a connection that is no longer open holds no lock.
            }
            finally
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
