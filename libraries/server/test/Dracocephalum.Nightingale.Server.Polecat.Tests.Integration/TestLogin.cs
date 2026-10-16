using System.Globalization;
using System.Security.Cryptography;

using Microsoft.Data.SqlClient;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests.Integration;

/// <summary>
/// A login with a user in a database and no permission of its own, for as long as a test runs,
/// and connections that act as it: the server takes Windows logins only, so a connection is opened
/// as the tests' and impersonates the login, unpooled, so nothing else inherits it.
/// </summary>
internal sealed class TestLogin : IAsyncDisposable
{
    private readonly string _connectionString;

    private TestLogin(string name, string database)
    {
        Name = name;
        _connectionString = new SqlConnectionStringBuilder(TestDatabases.ConnectionStringFor(database)) { Pooling = false }.ConnectionString;
    }

    /// <summary>Gets the login's name.</summary>
    public string Name { get; }

    /// <summary>Creates a login and its user in a database.</summary>
    /// <param name="database">The database.</param>
    /// <returns>The login.</returns>
    public static async Task<TestLogin> CreateAsync(string database)
    {
        var name = "nightingale_test_login_" + RandomNumberGenerator.GetHexString(8, lowercase: true);
        var password = RandomNumberGenerator.GetHexString(32) + "aA1!";
        await ExecuteOnMasterAsync(string.Format(CultureInfo.InvariantCulture, "CREATE LOGIN [{0}] WITH PASSWORD = N'{1}', CHECK_POLICY = OFF", name, password));
        await TestDatabases.ExecuteAsync(database, string.Format(CultureInfo.InvariantCulture, "CREATE USER [{0}] FOR LOGIN [{0}]", name));
        return new TestLogin(name, database);
    }

    /// <summary>Grants the login a permission on the server.</summary>
    /// <param name="permission">The permission.</param>
    /// <returns>A task that completes when it is granted.</returns>
    public Task GrantOnServerAsync(string permission) => ExecuteOnMasterAsync(string.Format(CultureInfo.InvariantCulture, "GRANT {0} TO [{1}]", permission, Name));

    /// <summary>Opens a connection to the database that acts as the login.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The open connection.</returns>
    public async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var impersonate = new SqlCommand(string.Format(CultureInfo.InvariantCulture, "EXECUTE AS LOGIN = '{0}'", Name), connection);
        await impersonate.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => new(ExecuteOnMasterAsync(string.Format(CultureInfo.InvariantCulture, "IF SUSER_ID('{0}') IS NOT NULL DROP LOGIN [{0}]", Name)));

    private static async Task ExecuteOnMasterAsync(string sql)
    {
        await using var master = new SqlConnection(TestDatabases.Master);
        await master.OpenAsync();
        await using var command = new SqlCommand(sql, master);
        await command.ExecuteNonQueryAsync();
    }
}
