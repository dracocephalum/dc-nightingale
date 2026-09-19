using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests.Integration;

/// <summary>
/// One database on the local SQL Server for the whole test project, created by the server itself
/// before the first test and dropped after the last, pass or fail. The fixture hosts the store
/// exactly as a production host would, through the same registration, so the initializer runs
/// and the tests exercise the real startup path: a database the server created and initialized.
/// </summary>
public sealed class SqlServerTestDatabase : IAsyncLifetime
{
    private IHost? _host;

    /// <summary>Gets the database name.</summary>
    public string Name { get; } = TestDatabases.NewName();

    /// <summary>Gets the connection string of the test database.</summary>
    public string ConnectionString => TestDatabases.ConnectionStringFor(Name);

    /// <summary>Gets the store, as the host registered it.</summary>
    public IStreamStore Store => Services.GetRequiredService<IStreamStore>();

    /// <summary>Gets the host's services.</summary>
    public IServiceProvider Services => _host?.Services ?? throw new InvalidOperationException("The fixture has not started.");

    public async ValueTask InitializeAsync()
    {
        _host = await TestDatabases.StartHostAsync(Name);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        await TestDatabases.DropAsync(Name);
    }
}
