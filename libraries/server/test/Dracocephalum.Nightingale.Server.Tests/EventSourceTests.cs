using System.Text;
using System.Text.Json.Nodes;

using FakeItEasy;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Tests;

/// <summary>
/// The feed a persistent-subscription group reads from: on a plain stream, what it does with what
/// the router brings out of order; under ordinal numbering, what it does when the sequencer
/// numbers an event at the very moment the feed is looking.
/// </summary>
public sealed class EventSourceTests
{
    private static readonly DateTimeOffset Created = new(2026, 9, 21, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FollowStream_WhenTheRouterBringsAnEventAheadOfTheNext_ShouldReadTheStreamAgainFromTheNextAndDeliverInOrder()
    {
        // Arrange: the catch-up finds revision 0; the router then brings revision 2, whose number
        // landed below the mark while revision 1's landed above it; the second catch-up finds both.
        var store = A.Fake<IStreamStore>(options => options.Strict());
        var tail = new FakeTail();
        await using var router = new StreamRouter(store, tail);
        A.CallTo(() => store.ReadAsync("orders-1", Direction.Forwards, 0, 10, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 0), [Record(0, 10)]));
        A.CallTo(() => store.ReadPointersAsync(1, 20, StreamRouter.PointerPage, A<CancellationToken>._))
            .Returns([new EventPointer("orders-1", 2, 20)]);
        A.CallTo(() => store.ReadAsync("orders-1", Direction.Forwards, 2, 1, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 2), [Record(2, 20)]));
        A.CallTo(() => store.ReadAsync("orders-1", Direction.Forwards, 1, 10, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 2), [Record(1, 22), Record(2, 20)]));
        var feed = EventSource.FollowAsync(store, tail, router, "orders-1", 0, 10, Numbering.Global, TimeProvider.System, TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);

        // Act
        var delivered = new List<long>();
        await feed.MoveNextAsync();
        delivered.Add(feed.Current.Revision);
        tail.Advance(20);
        while (delivered.Count < 3 && await feed.MoveNextAsync())
        {
            delivered.Add(feed.Current.Revision);
        }

        await feed.DisposeAsync();

        // Assert
        delivered.ShouldBe([0, 1, 2]);
    }

    [Fact]
    public async Task FollowByOrdinal_WhenAnEventIsNumberedBetweenTheReadAndTheWait_ShouldStillDeliverIt()
    {
        // Arrange: the head is at 20 and stays there, since nothing more is appended. The event
        // at 20 is committed and not numbered when the feed reads, and numbered a moment later:
        // asked before that read the sequencer says "through 19", asked after it "through 20".
        var orders = new VirtualStreamName(VirtualStreamKind.Category, "orders");
        var store = A.Fake<IStreamStore>();
        var tail = new FakeTail();
        await using var router = new StreamRouter(store, tail);
        tail.Advance(20);
        var reads = 0;
        A.CallTo(() => store.NumberedThroughAsync(A<CancellationToken>._)).ReturnsLazily(_ => Task.FromResult(Volatile.Read(ref reads) == 0 ? 19L : 20L));
        A.CallTo(() => store.ReadByOrdinalAsync(orders, Direction.Forwards, 8, 10, A<CancellationToken>._))
            .ReturnsLazily(_ => Task.FromResult<IReadOnlyList<EventRecord>>(
                Interlocked.Increment(ref reads) == 1
                    ? []
                    : [new EventRecord(Guid.NewGuid(), "orders-3", 0, 20, "order_placed", Created, Encoding.UTF8.GetBytes("{}"), new JsonObject()) { Ordinal = 8 }]));
        var time = new FakeTimeProvider(Created);
        using var abandon = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        // Act: the first event the feed yields. The feed's pause between looks is on the clock
        // the test holds, so the test moves the clock and waits for nothing. Six intervals are
        // five more than the feed needs; a feed that still has not delivered is waiting on
        // something no clock will bring, and is abandoned.
        var feed = EventSource.FollowAsync(store, tail, router, "$ce-orders", 8, 10, Numbering.Ordinal, time, abandon.Token).GetAsyncEnumerator(abandon.Token);
        var next = feed.MoveNextAsync().AsTask();
        for (var interval = 0; interval < 6 && !next.IsCompleted; interval++)
        {
            await Task.WhenAny(next, Task.Delay(50, TestContext.Current.CancellationToken));
            time.Advance(EventSource.NumberingPollInterval);
        }

        await Task.WhenAny(next, Task.Delay(500, TestContext.Current.CancellationToken));
        var delivered = next.IsCompletedSuccessfully && await next ? feed.Current : null;
        await abandon.CancelAsync();

        // Assert: found on the next look, not waited for behind an append that never comes.
        delivered.ShouldNotBeNull().Ordinal.ShouldBe(8);
        delivered.Position.ShouldBe(20);
    }

    private static EventRecord Record(long revision, long position) =>
        new(Guid.NewGuid(), "orders-1", revision, position, "order_placed", Created, Encoding.UTF8.GetBytes("{}"), new JsonObject());
}
