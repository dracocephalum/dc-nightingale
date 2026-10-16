using System.Data.Common;

using Dracocephalum.Nightingale.Server.Contracts;
using Dracocephalum.Nightingale.Server.Sequencing;
using Microsoft.Data.SqlClient;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests.Integration;

/// <summary>
/// The SQL Server horizon by partition, watching the probe table alone, against the contract every
/// horizon keeps, and what is its own: a writer to a table it does not watch holds nothing, it
/// names the writer once it reads the locks, and the one permission it needs.
/// </summary>
/// <param name="databases">The journal and the other database, made once for the class.</param>
public sealed class SqlServerPartitionHorizonTests(SqlServerTransactionHorizonTests.Databases databases) : TransactionHorizonContract, IClassFixture<SqlServerTransactionHorizonTests.Databases>
{
    /// <summary>A delay before the locks are read short enough that the contract's tests cross it.</summary>
    private static readonly TimeSpan LockReadDelay = TimeSpan.FromMilliseconds(200);

    /// <inheritdoc/>
    protected override ITransactionHorizon Horizon { get; } = Watching(SqlServerHorizon.Connect(TestDatabases.ConnectionStringFor(databases.Journal)));

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FindHolder_WithAWriterOnATableItDoesNotWatch_ShouldFindNoneOnceItReadsTheLocks()
    {
        await using var connection = (SqlConnection)await OpenJournalAsync(Cancellation);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(Cancellation);
        await ExecuteAsync(connection, transaction, $"INSERT INTO {SqlServerTransactionHorizonTests.Databases.OtherTable} (id) VALUES (1)");
        var marker = await Horizon.MarkAsync(Cancellation);

        var early = await Horizon.FindHolderAsync(marker, Cancellation);
        await Task.Delay(LockReadDelay * 2, Cancellation);
        var late = await Horizon.FindHolderAsync(marker, Cancellation);

        // Before the delay the oldest writer of the database is taken to hold; after it, the locks say it holds nothing watched.
        early.ShouldNotBeNull();
        late.ShouldBeNull();
    }

    [Fact]
    public async Task FindHolder_OnceItReadsTheLocks_ShouldNameTheWritersSession()
    {
        await using var connection = (SqlConnection)await OpenJournalAsync(Cancellation);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(Cancellation);
        var session = await ExecuteAsync(connection, transaction, $"INSERT INTO {ProbeTable} (id) VALUES (1); SELECT @@SPID");
        var marker = await Horizon.MarkAsync(Cancellation);

        await Task.Delay(LockReadDelay * 2, Cancellation);
        var holder = await Horizon.FindHolderAsync(marker, Cancellation);

        holder.ShouldNotBeNull();
        holder.SessionId.ShouldBe(session);
        holder.LoginName.ShouldNotBeNullOrEmpty();
        holder.StartedAt.ShouldNotBeNull().ShouldBe(TimeProvider.System.GetUtcNow(), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task CheckAvailability_WithoutViewServerPerformanceState_ShouldSayWhatToGrant()
    {
        await using var login = await TestLogin.CreateAsync(databases.Journal);

        var availability = await Watching(login.OpenAsync).CheckAvailabilityAsync(Cancellation);

        availability.IsAvailable.ShouldBeFalse();
        availability.Reason.ShouldNotBeNull().ShouldContain("VIEW SERVER PERFORMANCE STATE");
    }

    [Fact]
    public async Task CheckAvailability_WhenTheLoginCannotSeeTheTable_ShouldRefuseRatherThanReadNoLocks()
    {
        await using var login = await TestLogin.CreateAsync(databases.Journal);
        await login.GrantOnServerAsync("VIEW SERVER PERFORMANCE STATE");

        var availability = await Watching(login.OpenAsync).CheckAvailabilityAsync(Cancellation);

        // Its locks would match no partition it can see, and a held sequence would read as clear.
        availability.IsAvailable.ShouldBeFalse();
        availability.Reason.ShouldNotBeNull().ShouldContain("VIEW DEFINITION");
    }

    [Fact]
    public async Task FindHolder_WithViewServerPerformanceStateAlone_ShouldFindAnotherSessionsWriterByItsLocks()
    {
        await using var login = await TestLogin.CreateAsync(databases.Journal);
        await login.GrantOnServerAsync("VIEW SERVER PERFORMANCE STATE");
        await TestDatabases.ExecuteAsync(databases.Journal, $"GRANT SELECT ON {ProbeTable} TO [{login.Name}]");
        await using var connection = (SqlConnection)await OpenJournalAsync(Cancellation);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(Cancellation);
        await ExecuteAsync(connection, transaction, $"INSERT INTO {ProbeTable} (id) VALUES (1)");
        var horizon = Watching(login.OpenAsync);

        var availability = await horizon.CheckAvailabilityAsync(Cancellation);
        var marker = await horizon.MarkAsync(Cancellation);
        await Task.Delay(LockReadDelay * 2, Cancellation);
        var holder = await horizon.FindHolderAsync(marker, Cancellation);

        availability.ShouldBe(HorizonAvailability.Available);
        holder.ShouldNotBeNull();
    }

    /// <inheritdoc/>
    protected override Task<DbConnection> OpenJournalAsync(CancellationToken cancellationToken) => OpenAsync(databases.Journal, cancellationToken);

    /// <inheritdoc/>
    protected override Task<DbConnection> OpenOtherDatabaseAsync(CancellationToken cancellationToken) => OpenAsync(databases.Other, cancellationToken);

    private static SqlServerPartitionHorizon Watching(Func<CancellationToken, Task<SqlConnection>> connections) =>
        new(connections, [new SqlServerPartitionHorizon.WatchedTable($"[dbo].[{ProbeTable}]", null)], null, LockReadDelay);

    private static async Task<DbConnection> OpenAsync(string database, CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(TestDatabases.ConnectionStringFor(database));
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task<int?> ExecuteAsync(SqlConnection connection, SqlTransaction transaction, string sql)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        return await command.ExecuteScalarAsync(Cancellation) is short session ? session : null;
    }
}
