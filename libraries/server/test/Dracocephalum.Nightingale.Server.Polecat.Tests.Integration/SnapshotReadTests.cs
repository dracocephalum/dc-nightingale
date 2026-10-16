using System.Diagnostics;
using System.Text;

using Dracocephalum.Nightingale.Server.Data;
using Dracocephalum.Nightingale.Server.Polecat.Data;
using Dracocephalum.Nightingale.Server.Sequencing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests.Integration;

/// <summary>
/// A store whose database reads past an open transaction, under read committed snapshot, as Azure
/// SQL Database does by default: an append still open is a gap the tail sees, with committed
/// appends above it. That is where the store's own rule, a gap given up after 3 seconds, skips an
/// append that commits after; here neither strategy does. The transaction horizon holds the head
/// for as long as the append is open and gives a rolled-back one up at once; the best-effort
/// strategy's reads wait on the open row, so its wait never starts. And the sequencer, which numbers
/// only up to the head, numbers the late append in position order, never after what was appended
/// above it. The open append is a row inserted by hand in a transaction the test holds.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SnapshotReadTests : IAsyncLifetime
{
    /// <summary>Longer than the store's stale threshold, so the store's own rule would have given the open append up.</summary>
    private static readonly TimeSpan HeldFor = StaleThresholdHorizon.DefaultThreshold + TimeSpan.FromSeconds(1);

    private readonly string _name = TestDatabases.NewName();
    private IHost? _host;

    private IHost Host => _host ?? throw new InvalidOperationException("The host starts in InitializeAsync.");

    public async ValueTask InitializeAsync()
    {
        Action<NightingaleOptions> ordinals = options => options.Store.AssignOrdinals = true;
        await TestDatabases.ProvisionAsync(_name, ordinals);
        await TestDatabases.ExecuteAsync("master", $"ALTER DATABASE [{_name}] SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE");
        _host = await TestDatabases.StartHostAsync(_name, TestDatabases.FastBoot(ordinals));
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync(CancellationToken.None);
            _host.Dispose();
        }

        await TestDatabases.DropAsync(_name);
    }

    [Fact]
    public async Task Head_UnderTheTransactionHorizon_ShouldHoldBeforeAnOpenAppendPastTheThresholdAndPassItOnceCommitted()
    {
        // Arrange: an append held open, then one committed above it.
        var tail = Host.Tail();
        await using var held = await HoldAppendAsync("held-1");
        var above = await Host.Store().AppendAsync("held-2", StreamState.NoStream, [Event()], TestContext.Current.CancellationToken);

        // Act
        await Task.Delay(HeldFor, TestContext.Current.CancellationToken);
        var whileOpen = await tail.RefreshAsync(TestContext.Current.CancellationToken);
        await held.Transaction.CommitAsync(TestContext.Current.CancellationToken);
        var after = await ReachAsync(tail, above.Position);

        // Assert
        Host.Services.GetRequiredService<SequenceTails>().Strategy.ShouldNotBeNull().Kind.ShouldBe(HeadStrategyKind.PartitionHorizon);
        whileOpen.ShouldBeLessThan(held.Position, "the open append is a gap the head must not pass");
        after.ShouldBeGreaterThanOrEqualTo(above.Position);
    }

    [Fact]
    public async Task Head_UnderTheTransactionHorizon_ShouldGiveARolledBackAppendUpWithoutWaitingOutTheThreshold()
    {
        // Arrange
        var tail = Host.Tail();
        var held = await HoldAppendAsync("held-1");
        var above = await Host.Store().AppendAsync("held-2", StreamState.NoStream, [Event()], TestContext.Current.CancellationToken);

        // Act
        var clock = Stopwatch.StartNew();
        await held.DisposeAsync();
        var after = await ReachAsync(tail, above.Position);
        clock.Stop();

        // Assert: given up at the next look, the slow interval at most, not after the threshold.
        after.ShouldBeGreaterThanOrEqualTo(above.Position);
        clock.Elapsed.ShouldBeLessThan(StaleThresholdHorizon.DefaultThreshold);
    }

    [Fact]
    public async Task Head_UnderTheBestEffortStrategy_ShouldWaitOnAnOpenAppendPastTheThresholdAndPassItOnceCommitted()
    {
        // Arrange: tails of the test's own over the same store, kept by the best-effort strategy.
        var events = Host.Services.GetRequiredService<IDbContextFactory<EventsDbContext>>();
        await using var tails = new SequenceTails(
            _ => ValueTask.FromResult(new HeadStrategy(HeadStrategyKind.BestEffort, new StaleThresholdHorizon(StaleThresholdHorizon.DefaultThreshold), new SqlServerSequencePositions(events, lockingReads: true))),
            Host.Services.GetRequiredService<IDbContextFactory<NightingaleDbContext>>(),
            new AppendSignal(TimeProvider.System),
            TimeProvider.System,
            NullLogger<SequenceTails>.Instance);
        var tail = tails.For(SequenceKey.Shared);
        await tails.StartAsync(TestContext.Current.CancellationToken);
        await using var held = await HoldAppendAsync("held-1");
        var above = await Host.Store().AppendAsync("held-2", StreamState.NoStream, [Event()], TestContext.Current.CancellationToken);

        // Act
        await Task.Delay(HeldFor, TestContext.Current.CancellationToken);
        var whileOpen = tail.Head;
        await held.Transaction.CommitAsync(TestContext.Current.CancellationToken);
        var after = await ReachAsync(tail, above.Position);
        await tails.StopAsync(TestContext.Current.CancellationToken);

        // Assert
        whileOpen.ShouldBeLessThan(held.Position, "the reads wait on the open row, so the threshold never runs");
        after.ShouldBeGreaterThanOrEqualTo(above.Position);
    }

    [Fact]
    public async Task Sequencer_ShouldNumberAnAppendThatCommitsLateInPositionOrder()
    {
        // Arrange: in one category, an append held open past the threshold, one committed above it.
        var tail = Host.Tail();
        var held = await HoldAppendAsync("late-1");
        var above = await Host.Store().AppendAsync("late-2", StreamState.NoStream, [Event()], TestContext.Current.CancellationToken);
        await Task.Delay(HeldFor, TestContext.Current.CancellationToken);

        // Act
        await held.Transaction.CommitAsync(TestContext.Current.CancellationToken);
        await held.DisposeAsync();
        await ReachAsync(tail, above.Position);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await tail.WaitForNumberedAsync(above.Position, timeout.Token);
        var ordinals = await ReadCategoryOrdinalsAsync("late");

        // Assert: the late append first, as its position says; under a head that had given it up
        // it would have been numbered after the append above it.
        ordinals.ShouldBe([("late-1", 0L), ("late-2", 1L)]);
    }

    private static async Task<long> ReachAsync(IStoreTail tail, long position)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var head = tail.Head;
        while (head < position)
        {
            head = await tail.WaitForAdvanceAsync(head, timeout.Token);
        }

        return head;
    }

    private static EventData Event() => new(Guid.NewGuid(), "order_placed", Encoding.UTF8.GetBytes("{\"orderId\":1}"));

    /// <summary>Inserts an event row in a transaction left open: a position taken, not committed.</summary>
    private async Task<HeldAppend> HoldAppendAsync(string stream)
    {
        var connection = new SqlConnection(TestDatabases.ConnectionStringFor(_name));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var transaction = (SqlTransaction)await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO dbo.pc_events (id, stream_id, version, data, type) OUTPUT inserted.seq_id VALUES (NEWID(), @stream, 1, '{}', 'order_placed');";
        command.Parameters.Add(new SqlParameter("@stream", System.Data.SqlDbType.VarChar, 250) { Value = stream });
        var position = (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        return new HeldAppend(connection, transaction, position);
    }

    private async Task<IReadOnlyList<(string Stream, long Ordinal)>> ReadCategoryOrdinalsAsync(string category)
    {
        await using var connection = new SqlConnection(TestDatabases.ConnectionStringFor(_name));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT e.stream_id, l.[Ordinal] FROM nightingale.OrdinalLink l JOIN dbo.pc_events e ON e.seq_id = l.[SeqId] AND e.tenant_id = l.[TenantId] WHERE l.[Type] = 2 AND l.[Key] = @category ORDER BY e.seq_id;";
        command.Parameters.Add(new SqlParameter("@category", System.Data.SqlDbType.VarChar, 250) { Value = category });
        var ordinals = new List<(string, long)>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            ordinals.Add((reader.GetString(0), reader.GetInt64(1)));
        }

        return ordinals;
    }

    /// <summary>An append held open; disposing it without a commit rolls it back.</summary>
    private sealed class HeldAppend(SqlConnection connection, SqlTransaction transaction, long position) : IAsyncDisposable
    {
        public SqlTransaction Transaction => transaction;

        public long Position => position;

        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
