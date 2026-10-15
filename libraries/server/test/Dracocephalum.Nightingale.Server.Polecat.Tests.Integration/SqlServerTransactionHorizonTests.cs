using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;

using Dracocephalum.Nightingale.Server.Contracts;
using Dracocephalum.Nightingale.Server.Sequencing;
using Microsoft.Data.SqlClient;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests.Integration;

/// <summary>
/// The SQL Server horizon against the contract every backend's horizon keeps, and what is its own:
/// the permission it needs, and a start reported on the server's local clock read in UTC.
/// </summary>
/// <param name="databases">The journal and the other database, made once for the class.</param>
public sealed class SqlServerTransactionHorizonTests(SqlServerTransactionHorizonTests.Databases databases) : TransactionHorizonContract, IClassFixture<SqlServerTransactionHorizonTests.Databases>
{
    /// <inheritdoc/>
    protected override ITransactionHorizon Horizon { get; } = SqlServerTransactionHorizon.Over(TestDatabases.ConnectionStringFor(databases.Journal));

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FindHolder_WithAWriterOpen_ShouldReportItsStartInUtc()
    {
        await using var connection = (SqlConnection)await OpenJournalAsync(Cancellation);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(Cancellation);
        await using var insert = new SqlCommand($"INSERT INTO {ProbeTable} (id) VALUES (1)", connection, transaction);
        await insert.ExecuteNonQueryAsync(Cancellation);
        var marker = await Horizon.MarkAsync(Cancellation);

        var holder = await Horizon.FindHolderAsync(marker, Cancellation);

        // Read on a server away from UTC, a start left in local time would be hours off.
        holder.ShouldNotBeNull();
        holder.StartedAt.ShouldNotBeNull();
        holder.StartedAt.Value.ShouldBe(TimeProvider.System.GetUtcNow(), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task CheckAvailability_WithoutViewDatabaseState_ShouldSayWhatToGrant()
    {
        await using var login = await TestLogin.CreateAsync(databases.Journal);

        var availability = await login.Horizon.CheckAvailabilityAsync(Cancellation);

        availability.IsAvailable.ShouldBeFalse();
        availability.Reason.ShouldNotBeNull().ShouldContain("VIEW DATABASE STATE");
    }

    [Fact]
    public async Task FindHolder_WithViewDatabaseStateAlone_ShouldFindAnotherSessionsWriter()
    {
        await using var login = await TestLogin.CreateAsync(databases.Journal);
        await TestDatabases.ExecuteAsync(databases.Journal, string.Format(CultureInfo.InvariantCulture, "GRANT VIEW DATABASE STATE TO [{0}]", login.Name));
        await using var connection = (SqlConnection)await OpenJournalAsync(Cancellation);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(Cancellation);
        await using var insert = new SqlCommand($"INSERT INTO {ProbeTable} (id) VALUES (1)", connection, transaction);
        await insert.ExecuteNonQueryAsync(Cancellation);

        var availability = await login.Horizon.CheckAvailabilityAsync(Cancellation);
        var holder = await login.Horizon.FindHolderAsync(await login.Horizon.MarkAsync(Cancellation), Cancellation);

        availability.ShouldBe(HorizonAvailability.Available);
        holder.ShouldNotBeNull();
    }

    /// <inheritdoc/>
    protected override Task<DbConnection> OpenJournalAsync(CancellationToken cancellationToken) => OpenAsync(databases.Journal, cancellationToken);

    /// <inheritdoc/>
    protected override Task<DbConnection> OpenOtherDatabaseAsync(CancellationToken cancellationToken) => OpenAsync(databases.Other, cancellationToken);

    private static async Task<DbConnection> OpenAsync(string database, CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(TestDatabases.ConnectionStringFor(database));
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    /// <summary>The journal and another database on the same server, each with the probe table, read once; dropped after the class.</summary>
    public sealed class Databases : IAsyncLifetime
    {
        /// <summary>Gets the name of the journal.</summary>
        public string Journal { get; } = TestDatabases.NewName();

        /// <summary>Gets the name of the other database.</summary>
        public string Other { get; } = TestDatabases.NewName();

        public async ValueTask InitializeAsync()
        {
            foreach (var name in new[] { Journal, Other })
            {
                await TestDatabases.CreateEmptyAsync(name);
                await TestDatabases.ExecuteAsync(name, $"CREATE TABLE {ProbeTable} (id int NOT NULL); SELECT COUNT(*) FROM {ProbeTable};");
            }
        }

        public async ValueTask DisposeAsync()
        {
            await TestDatabases.DropAsync(Journal);
            await TestDatabases.DropAsync(Other);
        }
    }

    /// <summary>
    /// A login of the journal's with no permission of its own, for as long as a test runs, and a
    /// horizon reading as it: the server takes Windows logins only, so the horizon's connection
    /// is opened as the tests' and impersonates the login, unpooled, so nothing else inherits it.
    /// </summary>
    private sealed class TestLogin : IAsyncDisposable
    {
        private TestLogin(string name, string journal)
        {
            Name = name;
            var connectionString = new SqlConnectionStringBuilder(TestDatabases.ConnectionStringFor(journal)) { Pooling = false }.ConnectionString;
            Horizon = new SqlServerTransactionHorizon(async cancellationToken =>
            {
                var connection = new SqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                await using var impersonate = new SqlCommand(string.Format(CultureInfo.InvariantCulture, "EXECUTE AS LOGIN = '{0}'", name), connection);
                await impersonate.ExecuteNonQueryAsync(cancellationToken);
                return connection;
            });
        }

        public string Name { get; }

        public SqlServerTransactionHorizon Horizon { get; }

        public static async Task<TestLogin> CreateAsync(string journal)
        {
            var name = "nightingale_test_login_" + RandomNumberGenerator.GetHexString(8, lowercase: true);
            var password = RandomNumberGenerator.GetHexString(32) + "aA1!";
            await using (var master = new SqlConnection(TestDatabases.Master))
            {
                await master.OpenAsync();
                await using var create = new SqlCommand(string.Format(CultureInfo.InvariantCulture, "CREATE LOGIN [{0}] WITH PASSWORD = N'{1}', CHECK_POLICY = OFF", name, password), master);
                await create.ExecuteNonQueryAsync();
            }

            await TestDatabases.ExecuteAsync(journal, string.Format(CultureInfo.InvariantCulture, "CREATE USER [{0}] FOR LOGIN [{0}]", name));
            return new TestLogin(name, journal);
        }

        public async ValueTask DisposeAsync()
        {
            await using var master = new SqlConnection(TestDatabases.Master);
            await master.OpenAsync();
            await using var drop = new SqlCommand(string.Format(CultureInfo.InvariantCulture, "IF SUSER_ID('{0}') IS NOT NULL DROP LOGIN [{0}]", Name), master);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
