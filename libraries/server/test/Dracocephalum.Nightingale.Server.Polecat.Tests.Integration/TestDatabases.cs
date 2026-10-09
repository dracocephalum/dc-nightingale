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
    /// <param name="collation">The collation to create it with; the server's default collation when null.</param>
    /// <returns>A task that completes when the database exists.</returns>
    public static Task CreateEmptyAsync(string name, string? collation = NightingaleOptions.StoreOptions.DefaultCollation) =>
        ExecuteOnMasterAsync(collation is null
            ? string.Format(CultureInfo.InvariantCulture, "CREATE DATABASE [{0}]", name)
            : string.Format(CultureInfo.InvariantCulture, "CREATE DATABASE [{0}] COLLATE {1}", name, collation));

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
        builder.Services.AddNightingalePolecat(ConnectionStringFor(name), TestAuth.Off(configure));
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

    /// <summary>
    /// Makes an initialized store without the server: the event store's tables from its creation
    /// script, run as one command, then what the server itself does after applying them, the
    /// gateway's migrations and the rows that record what the store was made from. The server's
    /// own way compares the database with what the store expects, and the first comparison in a
    /// database costs many seconds of query compilation; a test that is not about initialization
    /// has no use for it. <c>StoreCreationTests</c> holds a store made this way equal to one the
    /// server initialized.
    /// </summary>
    /// <param name="name">The database name; the database must not exist.</param>
    /// <param name="configure">Adjusts the options, as for the host that will serve the store; the database is created with the collation they name.</param>
    /// <returns>A task that completes when the store is there.</returns>
    public static async Task ProvisionAsync(string name, Action<NightingaleOptions>? configure = null)
    {
        var options = new NightingaleOptions();
        configure?.Invoke(options);
        var collation = options.Store.Collation ?? NightingaleOptions.StoreOptions.DefaultCollation;
        await CreateEmptyAsync(name, collation);

        var services = new ServiceCollection();
        services.AddNightingalePolecat(ConnectionStringFor(name), TestAuth.Off(configure));
        await using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<global::Polecat.IDocumentStore>();
        foreach (var database in await store.Options.Tenancy!.BuildDatabasesAsync())
        {
            await ExecuteAsync(name, database.ToDatabaseScript());
        }

        var schema = provider.GetRequiredService<StoreSchema>();
        await schema.MigrateAsync(CancellationToken.None);
        await schema.WriteInitializationAsync(
            new StoreSettings
            {
                Schema = options.Store.Schema,
                Collation = collation,
                Partitioning = options.Store.Partitioning,
                AssignOrdinals = options.Store.AssignOrdinals,
            },
            TimeProvider.System.GetUtcNow(),
            "a test",
            CancellationToken.None);
    }

    /// <summary>
    /// Hosts the backend against a store made by <see cref="ProvisionAsync"/>, with fast boot on,
    /// so the start takes the recorded hash for the store's tables and compares nothing.
    /// </summary>
    /// <param name="name">The database name; the database must not exist.</param>
    /// <param name="configure">Adjusts the options.</param>
    /// <returns>The started host; stop and dispose it when done.</returns>
    public static async Task<IHost> StartProvisionedHostAsync(string name, Action<NightingaleOptions>? configure = null)
    {
        await ProvisionAsync(name, configure);
        return await StartHostAsync(name, FastBoot(configure));
    }

    /// <summary>The same options, with fast boot on.</summary>
    /// <param name="configure">Adjusts the options.</param>
    /// <returns>The adjustment, followed by turning fast boot on.</returns>
    public static Action<NightingaleOptions> FastBoot(Action<NightingaleOptions>? configure = null) => options =>
    {
        configure?.Invoke(options);
        options.FastBoot = true;
    };

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
