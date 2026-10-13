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
        await using var router = new StreamRouter(store, tail, StringComparer.Ordinal);
        A.CallTo(() => store.ReadAsync("orders-1", Direction.Forwards, 0, 10, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 0), [Record(0, 10)]));
        A.CallTo(() => store.ReadPointersAsync(1, 20, StreamRouter.PointerPage, A<CancellationToken>._))
            .Returns([new EventPointer("orders-1", 2, 20, "order_placed")]);
        A.CallTo(() => store.ReadAsync("orders-1", Direction.Forwards, 2, 1, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 2), [Record(2, 20)]));
        A.CallTo(() => store.ReadAsync("orders-1", Direction.Forwards, 1, 10, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 2), [Record(1, 22), Record(2, 20)]));
        var feed = EventSource.FollowAsync(store, tail, router, "orders-1", 0, 10, Numbering.Global, null, TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);

        // Act
        var delivered = new List<long>();
        await feed.MoveNextAsync();
        delivered.Add(feed.Current.Record!.Revision);
        tail.Advance(20);
        while (delivered.Count < 3 && await feed.MoveNextAsync())
        {
            delivered.Add(feed.Current.Record!.Revision);
        }

        await feed.DisposeAsync();

        // Assert
        delivered.ShouldBe([0, 1, 2]);
    }

    [Fact]
    public async Task FollowByOrdinal_WhenAnEventIsNumberedBetweenTheReadAndTheWait_ShouldStillDeliverIt()
    {
        // Arrange: the head is at 20 and stays there, since nothing more is appended. The event
        // at 20 is committed and not numbered when the feed reads, and numbered the moment the
        // read returns: taken before that read the numbering says "through 19", after it
        // "through 20".
        var orders = new VirtualStreamName(VirtualStreamKind.Category, "orders");
        var store = A.Fake<IStreamStore>();
        var tail = new FakeTail();
        await using var router = new StreamRouter(store, tail, StringComparer.Ordinal);
        tail.Advance(20);
        tail.Number(19);
        var reads = 0;
        A.CallTo(() => store.ReadByOrdinalAsync(orders, Direction.Forwards, 8, 10, A<CancellationToken>._))
            .ReturnsLazily(_ =>
            {
                if (Interlocked.Increment(ref reads) == 1)
                {
                    tail.Number(20);
                    return Task.FromResult<IReadOnlyList<EventRecord>>([]);
                }

                return Task.FromResult<IReadOnlyList<EventRecord>>([new EventRecord(Guid.NewGuid(), "orders-3", 0, 20, "order_placed", Created, Encoding.UTF8.GetBytes("{}"), new JsonObject()) { Ordinal = 8 }]);
            });
        using var abandon = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        abandon.CancelAfter(TimeSpan.FromSeconds(10));

        // Act: the first event the feed yields. A feed that waits on an advance instead is
        // waiting on something that never comes, and is abandoned.
        var feed = EventSource.FollowAsync(store, tail, router, "$ce-orders", 8, 10, Numbering.Ordinal, null, abandon.Token).GetAsyncEnumerator(abandon.Token);
        var delivered = await feed.MoveNextAsync() ? feed.Current.Record : null;
        await feed.DisposeAsync();

        // Assert: found on the look the numbering woke, not waited for behind an append that never comes.
        delivered.ShouldNotBeNull().Ordinal.ShouldBe(8);
        delivered.Position.ShouldBe(20);
        reads.ShouldBe(2);
    }

    private static EventRecord Record(long revision, long position) =>
        new(Guid.NewGuid(), "orders-1", revision, position, "order_placed", Created, Encoding.UTF8.GetBytes("{}"), new JsonObject());
}
