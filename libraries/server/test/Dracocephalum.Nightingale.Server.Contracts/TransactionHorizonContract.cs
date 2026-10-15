using System.Data.Common;
using Dracocephalum.Nightingale.Server.Sequencing;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Contracts;

/// <summary>
/// What every backend's <see cref="ITransactionHorizon"/> must do. Correctness: a transaction
/// that has written and is still open holds the horizon back from every marker taken after its
/// write, until it commits or rolls back. Liveness: nothing that cannot hold a position holds it
/// back, neither a reader, nor a writer in another database, nor a writer that began after the
/// marker. A derived class supplies the horizon of a journal and connections to it and to another
/// database on the same server, each with the table <see cref="ProbeTable"/>, already read once:
/// a database may log the first read of a fresh table as a write. Where the horizon is expected
/// to clear, the tests wait for it a moment: the database's own short transactions may hold it
/// while they run.
/// </summary>
public abstract class TransactionHorizonContract
{
    /// <summary>The table the tests write to, in both databases: one integer column, <c>id</c>.</summary>
    public const string ProbeTable = "horizon_probe";

    /// <summary>How long a horizon expected to clear is given to clear: the database's own short transactions may hold it meanwhile.</summary>
    private static readonly TimeSpan Settling = TimeSpan.FromSeconds(5);

    /// <summary>Gets the horizon of the journal under test.</summary>
    protected abstract ITransactionHorizon Horizon { get; }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CheckAvailability_ForTheTestsLogin_ShouldBeAvailable()
    {
        var availability = await Horizon.CheckAvailabilityAsync(Cancellation);

        availability.ShouldBe(HorizonAvailability.Available);
    }

    [Fact]
    public async Task FindHolder_WithNothingOpen_ShouldFindNone()
    {
        var marker = await Horizon.MarkAsync(Cancellation);

        await ShouldClearAsync(marker);
    }

    [Fact]
    public async Task FindHolder_WithAWriterOpenSinceBeforeTheMarker_ShouldFindIt()
    {
        await using var writer = await BeginWriterAsync(OpenJournalAsync);
        var marker = await Horizon.MarkAsync(Cancellation);

        var holder = await Horizon.FindHolderAsync(marker, Cancellation);

        holder.ShouldNotBeNull();
    }

    [Fact]
    public async Task FindHolder_WithAWriterOpen_ShouldNameItsSession()
    {
        await using var writer = await BeginWriterAsync(OpenJournalAsync);
        var marker = await Horizon.MarkAsync(Cancellation);

        var holder = await Horizon.FindHolderAsync(marker, Cancellation);

        holder.ShouldNotBeNull();
        holder.SessionId.ShouldNotBeNull();
        holder.StartedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task FindHolder_WhileTheWriterStaysOpen_ShouldKeepFindingIt()
    {
        await using var writer = await BeginWriterAsync(OpenJournalAsync);
        var marker = await Horizon.MarkAsync(Cancellation);

        for (var look = 0; look < 5; look++)
        {
            (await Horizon.FindHolderAsync(marker, Cancellation)).ShouldNotBeNull();
            await Task.Delay(TimeSpan.FromMilliseconds(100), Cancellation);
        }
    }

    [Fact]
    public async Task FindHolder_AfterTheWriterCommits_ShouldFindNone()
    {
        await using var writer = await BeginWriterAsync(OpenJournalAsync);
        var marker = await Horizon.MarkAsync(Cancellation);

        await writer.Transaction.CommitAsync(Cancellation);

        await ShouldClearAsync(marker);
    }

    [Fact]
    public async Task FindHolder_AfterTheWriterRollsBack_ShouldFindNone()
    {
        await using var writer = await BeginWriterAsync(OpenJournalAsync);
        var marker = await Horizon.MarkAsync(Cancellation);

        await writer.Transaction.RollbackAsync(Cancellation);

        await ShouldClearAsync(marker);
    }

    [Fact]
    public async Task FindHolder_WithAWriterThatBeganAfterTheMarker_ShouldFindNone()
    {
        var marker = await Horizon.MarkAsync(Cancellation);

        // Well past the marker on any clock's rounding.
        await Task.Delay(TimeSpan.FromMilliseconds(200), Cancellation);
        await using var writer = await BeginWriterAsync(OpenJournalAsync);

        await ShouldClearAsync(marker);
    }

    [Fact]
    public async Task FindHolder_WithOnlyAReaderOpen_ShouldFindNone()
    {
        await using var connection = await OpenJournalAsync(Cancellation);
        await using var transaction = await connection.BeginTransactionAsync(Cancellation);
        await ExecuteAsync(connection, transaction, $"SELECT COUNT(*) FROM {ProbeTable}");
        var marker = await Horizon.MarkAsync(Cancellation);

        await ShouldClearAsync(marker);
    }

    [Fact]
    public async Task FindHolder_WithAWriterInAnotherDatabase_ShouldFindNone()
    {
        await using var writer = await BeginWriterAsync(OpenOtherDatabaseAsync);
        var marker = await Horizon.MarkAsync(Cancellation);

        await ShouldClearAsync(marker);
    }

    /// <summary>Opens a connection to the journal the horizon reads.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The open connection.</returns>
    protected abstract Task<DbConnection> OpenJournalAsync(CancellationToken cancellationToken);

    /// <summary>Opens a connection to another database on the same server.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The open connection.</returns>
    protected abstract Task<DbConnection> OpenOtherDatabaseAsync(CancellationToken cancellationToken);

    private static async Task<OpenWriter> BeginWriterAsync(Func<CancellationToken, Task<DbConnection>> connections)
    {
        var connection = await connections(Cancellation);
        var transaction = await connection.BeginTransactionAsync(Cancellation);
        await ExecuteAsync(connection, transaction, $"INSERT INTO {ProbeTable} (id) VALUES (1)");
        return new OpenWriter(connection, transaction);
    }

    private static async Task ExecuteAsync(DbConnection connection, DbTransaction transaction, string sql)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Cancellation);
    }

    private async Task ShouldClearAsync(HorizonMarker marker)
    {
        var until = TimeProvider.System.GetTimestamp() + (long)(Settling.TotalSeconds * TimeProvider.System.TimestampFrequency);
        HorizonHolder? holder;
        while ((holder = await Horizon.FindHolderAsync(marker, Cancellation)) is not null && TimeProvider.System.GetTimestamp() < until)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), Cancellation);
        }

        holder.ShouldBeNull($"the horizon was still held after {Settling.TotalSeconds} s");
    }

    /// <summary>A transaction that has written to the probe table and is left open.</summary>
    private sealed class OpenWriter(DbConnection connection, DbTransaction transaction) : IAsyncDisposable
    {
        public DbTransaction Transaction => transaction;

        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
