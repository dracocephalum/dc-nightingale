using System.Text;

using Dracocephalum.Nightingale.Server.Data;
using Dracocephalum.Nightingale.Server.Sequencing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests.Integration;

/// <summary>
/// The tails follow the shared sequence's head by the transaction horizon, which the test login
/// may read, keep it in the gateway's table, and <c>$all</c> reads page in position order within
/// it. The shared database has other tests' events in it, so assertions are relative to what this
/// test appends.
/// </summary>
[Collection(SharedSqlServer.Name)]
[Trait("Category", "Integration")]
public sealed class SequenceTailsTests(SqlServerTestDatabase database)
{
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("{\"orderId\":1}");

    [Fact]
    public async Task Head_AfterAnAppend_ShouldReachTheAppendedPositionWithinThePollingInterval()
    {
        // Arrange
        var tail = database.Services.GetRequiredService<IStoreTail>();
        var before = tail.Head;

        // Act
        var appended = await database.Store.AppendAsync(NewStream(), StreamState.NoStream, [Event(), Event()], TestContext.Current.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var head = tail.Head;
        while (head < appended.Position)
        {
            head = await tail.WaitForAdvanceAsync(head, timeout.Token);
        }

        // Assert
        appended.Position.ShouldBeGreaterThan(before);
        head.ShouldBeGreaterThanOrEqualTo(appended.Position);
    }

    [Fact]
    public async Task Head_ShouldBeKeptByTheTransactionHorizonAndWrittenToTheGatewaysTable()
    {
        // Arrange
        var tails = database.Services.GetRequiredService<SequenceTails>();
        var contexts = database.Services.GetRequiredService<IDbContextFactory<NightingaleDbContext>>();
        var appended = await database.Store.AppendAsync(NewStream(), StreamState.NoStream, [Event()], TestContext.Current.CancellationToken);

        // Act: the row is written at the look after the one that reached the append, or at it.
        var kept = 0L;
        for (var attempt = 0; attempt < 100 && kept < appended.Position; attempt++)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
            await using var context = await contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);
            kept = await context.SequenceHeads.Where(row => row.TenantId == null).Select(row => row.Position).SingleOrDefaultAsync(TestContext.Current.CancellationToken);
        }

        // Assert
        tails.Strategy.ShouldNotBeNull().Kind.ShouldBe(HeadStrategyKind.PartitionHorizon);
        kept.ShouldBeGreaterThanOrEqualTo(appended.Position);
    }

    [Fact]
    public async Task ReadAllAsync_ShouldPageForwardsInPositionOrderUpToTheHeadAndBackwardsFromIt()
    {
        // Arrange
        var stream = NewStream();
        var sut = database.Store;
        var first = await sut.AppendAsync(stream, StreamState.NoStream, [Event()], TestContext.Current.CancellationToken);
        var last = await sut.AppendAsync(stream, StreamState.StreamRevision(0), [Event(), Event()], TestContext.Current.CancellationToken);

        // Act
        var forwards = await sut.ReadAllAsync(Direction.Forwards, first.Position, last.Position, 2, TestContext.Current.CancellationToken);
        var rest = await sut.ReadAllAsync(Direction.Forwards, forwards[^1].Position + 1, last.Position, 2, TestContext.Current.CancellationToken);
        var backwards = await sut.ReadAllAsync(Direction.Backwards, last.Position, last.Position, 3, TestContext.Current.CancellationToken);
        var bounded = await sut.ReadAllAsync(Direction.Forwards, first.Position, first.Position, 10, TestContext.Current.CancellationToken);

        // Assert
        forwards.Select(record => record.Position).ShouldBe([first.Position, first.Position + 1]);
        rest.ShouldHaveSingleItem().Position.ShouldBe(last.Position);
        backwards.Select(record => record.Revision).ShouldBe([2, 1, 0]);
        bounded.ShouldHaveSingleItem().Stream.ShouldBe(stream);
    }

    private static string NewStream() => "tail-" + Guid.NewGuid().ToString("N");

    private static EventData Event() => new(Guid.NewGuid(), "order_placed", Body);
}
