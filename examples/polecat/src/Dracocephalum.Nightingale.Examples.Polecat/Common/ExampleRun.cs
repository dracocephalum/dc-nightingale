using Dracocephalum.Nightingale.Server.Polecat;

namespace Dracocephalum.Nightingale.Examples.Polecat.Common;

/// <summary>
/// One run of the program or of the test project: one database on the local server, created and
/// initialized by the run's own server, and a tenant for each scenario, so that the scenarios
/// keep their stream names, their group names and their ordinals without seeing one another.
/// A scenario that needs a server setting the run's server does not have starts another server
/// over the same database. Disposing the run stops the server and drops the database, leaving
/// the SQL Server as it was found.
/// </summary>
public sealed class ExampleRun : IAsyncDisposable
{
    private readonly ExampleConnection _administrator;
    private int _tenants;

    private ExampleRun(ExampleDatabase database, ExampleServer server)
    {
        Database = database;
        Server = server;
        _administrator = server.ConnectAsAdministrator();
    }

    /// <summary>Gets the run's database.</summary>
    public ExampleDatabase Database { get; }

    /// <summary>Gets the run's server, with the default settings, which created and initialized the database.</summary>
    public ExampleServer Server { get; }

    /// <summary>Reserves a database name and starts the run's server, which creates and initializes the database.</summary>
    /// <param name="masterConnectionString">A connection string to the server's master database.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The run; dispose it to stop the server and drop the database.</returns>
    public static async Task<ExampleRun> StartAsync(string masterConnectionString, CancellationToken cancellationToken)
    {
        var database = ExampleDatabase.Reserve(masterConnectionString);
        try
        {
            var server = await ExampleServer.StartAsync(database.ConnectionString, cancellationToken).ConfigureAwait(false);
            return new ExampleRun(database, server);
        }
        catch
        {
            await database.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Creates a tenant for a scenario, under a name the run gives out once, so that a scenario
    /// run twice in one run collides with nothing. Every server over the run's database serves
    /// the tenant from its first call.
    /// </summary>
    /// <param name="name">The scenario's name, or what the tenant is for.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The tenant.</returns>
    public Task<TenantInfo> CreateTenantAsync(string name, CancellationToken cancellationToken) =>
        _administrator.Client.Management.CreateTenantAsync(ReserveTenantName(name), cancellationToken);

    /// <summary>Gives out a tenant name the run gives out once: the name and a number.</summary>
    /// <param name="name">The scenario's name, or what the tenant is for.</param>
    /// <returns>The name to create the tenant under.</returns>
    public string ReserveTenantName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return $"{name}-{Interlocked.Increment(ref _tenants)}";
    }

    /// <summary>Starts another server over the run's database, for a scenario that needs a setting the run's server does not have.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="configure">Adjusts the options; the defaults are the default host's.</param>
    /// <returns>The running server; dispose it to stop it.</returns>
    public Task<ExampleServer> StartServerAsync(CancellationToken cancellationToken, Action<NightingaleOptions>? configure = null) =>
        ExampleServer.StartAsync(Database.ConnectionString, cancellationToken, configure);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _administrator.DisposeAsync().ConfigureAwait(false);
        await Server.DisposeAsync().ConfigureAwait(false);
        await Database.DisposeAsync().ConfigureAwait(false);
    }
}
