using System.Globalization;
using System.Security.Cryptography;

using Microsoft.Data.SqlClient;

namespace Dracocephalum.Nightingale.Examples.DeleteAndTombstone;

/// <summary>
/// A throwaway database name on the local server. The server creates and initializes the database
/// itself when it starts, so the sample only picks a name no other run uses and drops the
/// database on disposal, leaving the server as it was found. The sample assumes nothing about the
/// server beyond the master connection string it was given.
/// </summary>
public sealed class ExampleDatabase : IAsyncDisposable
{
    private readonly string _master;

    private ExampleDatabase(string master, string name)
    {
        _master = master;
        Name = name;
        ConnectionString = new SqlConnectionStringBuilder(master) { InitialCatalog = name }.ConnectionString;
    }

    /// <summary>Gets the connection string of the database.</summary>
    public string ConnectionString { get; }

    /// <summary>Gets the database name.</summary>
    public string Name { get; }

    /// <summary>Reserves a fresh random name; nothing is created until the server starts.</summary>
    /// <param name="masterConnectionString">A connection string to the server's master database.</param>
    /// <returns>The database; dispose it to drop it.</returns>
    public static ExampleDatabase Reserve(string masterConnectionString) =>
        new(masterConnectionString, "nightingale_example_" + RandomNumberGenerator.GetHexString(8, lowercase: true));

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        await using var connection = new SqlConnection(_master);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = string.Format(
            CultureInfo.InvariantCulture,
            "IF DB_ID('{0}') IS NOT NULL BEGIN ALTER DATABASE [{0}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{0}]; END",
            Name);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}
