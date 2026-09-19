using System.Globalization;
using System.Security.Cryptography;

using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests.Integration;

/// <summary>
/// The local SQL Server the integration tests use, reached through the master connection string
/// in the <c>NIGHTINGALE_SQLSERVER</c> environment variable or the generic local default, and the
/// few things a test does to a database around the server: name one, provision one by hand, host
/// the backend against it, and drop it afterwards, pass or fail.
/// </summary>
internal static class TestDatabases
{
    private const string EnvironmentVariable = "NIGHTINGALE_SQLSERVER";
    private const string DefaultMaster = "Server=localhost;Database=master;Trusted_Connection=True;TrustServerCertificate=True;Command Timeout=300";

    /// <summary>Gets the master connection string.</summary>
    public static string Master { get; } = Environment.GetEnvironmentVariable(EnvironmentVariable) ?? DefaultMaster;

    /// <summary>A database name no other run uses.</summary>
    /// <returns>The name.</returns>
    public static string NewName() => "nightingale_test_" + RandomNumberGenerator.GetHexString(8, lowercase: true);

    /// <summary>The connection string of a database on the test server.</summary>
    /// <param name="name">The database name.</param>
    /// <returns>The connection string.</returns>
    public static string ConnectionStringFor(string name) => new SqlConnectionStringBuilder(Master) { InitialCatalog = name }.ConnectionString;

    /// <summary>Creates an empty database by hand, as a database administrator would provision one.</summary>
    /// <param name="name">The database name.</param>
    /// <returns>A task that completes when the database exists.</returns>
    public static Task CreateEmptyAsync(string name) => ExecuteOnMasterAsync(string.Format(CultureInfo.InvariantCulture, "CREATE DATABASE [{0}]", name));

    /// <summary>Runs a statement in a database.</summary>
    /// <param name="name">The database name.</param>
    /// <param name="sql">The statement.</param>
    /// <returns>A task that completes when the statement has run.</returns>
    public static async Task ExecuteAsync(string name, string sql)
    {
        await using var connection = new SqlConnection(ConnectionStringFor(name));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Runs a query in a database and returns the first column of the first row.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="name">The database name.</param>
    /// <param name="sql">The query.</param>
    /// <returns>The value.</returns>
    public static async Task<T> ScalarAsync<T>(string name, string sql)
    {
        await using var connection = new SqlConnection(ConnectionStringFor(name));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Drops a database if it exists, closing every connection to it first.</summary>
    /// <param name="name">The database name.</param>
    /// <returns>A task that completes when the database is gone.</returns>
    public static Task DropAsync(string name)
    {
        SqlConnection.ClearAllPools();
        return ExecuteOnMasterAsync(string.Format(
            CultureInfo.InvariantCulture,
            "IF DB_ID('{0}') IS NOT NULL BEGIN ALTER DATABASE [{0}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{0}]; END",
            name));
    }

    /// <summary>Hosts the backend against a database exactly as a production host would, through the same registration.</summary>
    /// <param name="name">The database name.</param>
    /// <param name="configure">Adjusts the options.</param>
    /// <returns>The started host; stop and dispose it when done.</returns>
    public static async Task<IHost> StartHostAsync(string name, Action<NightingaleOptions>? configure = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddNightingalePolecat(ConnectionStringFor(name), configure);
        var host = builder.Build();
        try
        {
            await host.StartAsync();
        }
        catch
        {
            host.Dispose();
            throw;
        }

        return host;
    }

    /// <summary>The store a host registered.</summary>
    /// <param name="host">The host.</param>
    /// <returns>The store.</returns>
    public static IStreamStore Store(this IHost host) => host.Services.GetRequiredService<IStreamStore>();

    private static async Task ExecuteOnMasterAsync(string sql)
    {
        await using var connection = new SqlConnection(Master);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
