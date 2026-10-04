using System.Globalization;

using Microsoft.Data.SqlClient;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>The database on SQL Server, reached through the store's connection string and, for what must not connect to it, the server's master database.</summary>
/// <param name="connectionString">The connection string the store uses.</param>
internal sealed class StoreDatabase(string connectionString) : IStoreDatabase
{
    private readonly string _master = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" }.ConnectionString;

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

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<(string Collation, int? CodePage)> ReadCollationAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CONVERT(varchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation')), COLLATIONPROPERTY(CONVERT(varchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation')), 'CodePage')";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return (reader.GetString(0), reader.IsDBNull(1) ? null : Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture));
    }

    /// <inheritdoc/>
    public async Task<int> CountTablesAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped = 0";
        return (int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }
}
