using System.Text;
using System.Text.Json.Nodes;

using FakeItEasy;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Tests;

/// <summary>
/// The router against a strict fake of the store and a tail moved by hand: what one advance costs
/// in reads, who gets what, how names are matched, what a follower that falls too far behind
/// gets, what a deleted stream's followers get, and what a range that cannot be read does.
/// </summary>
public sealed class StreamRouterTests : IAsyncDisposable
{
    private static readonly DateTimeOffset Created = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private readonly IStreamStore _store = A.Fake<IStreamStore>(options => options.Strict());
    private readonly FakeTail _tail = new();
    private readonly StreamRouter _sut;

    public StreamRouterTests()
    {
        _sut = new StreamRouter(_store, _tail, StringComparer.Ordinal);
    }

    public ValueTask DisposeAsync() => _sut.DisposeAsync();

    [Fact]
    public async Task Route_ShouldReadTheRangeOnceAndEachStreamThatMatchedOnceAndHandEveryFollowerWhatMatchesIt()
    {
        // Arrange: four streams moved. One is followed by two, one is in a followed category,
        // one has an event of a followed type, and one is nobody's. Each matched stream is read
        // once, from the first revision pointed at, and each event goes to whoever it matches.
        A.CallTo(() => _store.ReadPointersAsync(1, 7, StreamRouter.PointerPage, A<CancellationToken>._))
            .Returns([Pointer("orders-1", 0, 1), Pointer("shipments-1", 0, 2), Pointer("orders-1", 1, 3), Pointer("audit-1", 0, 4), Pointer("invoices-1", 0, 5, "invoice_paid"), Pointer("orders-1", 2, 7, "order_paid")]);
        A.CallTo(() => _store.ReadAsStoredAsync("orders-1", 0, 3, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 2), [Record("orders-1", 0, 1), Record("orders-1", 1, 3), Record("orders-1", 2, 7, "order_paid")]));
        A.CallTo(() => _store.ReadAsStoredAsync("shipments-1", 0, 1, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 0), [Record("shipments-1", 0, 2)]));
        A.CallTo(() => _store.ReadAsStoredAsync("invoices-1", 0, 1, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 0), [Record("invoices-1", 0, 5, "invoice_paid")]));
        using var first = _sut.Follow("orders-1");
        using var second = _sut.Follow("orders-1");
        using var shipments = _sut.Follow("$ce-shipments");
        using var paid = _sut.Follow("$et-invoice_paid");
        using var idle = _sut.Follow("returns-1");

        // Act
        _tail.Advance(7);
        var toFirst = await NextAsync(first, 3);
        var toSecond = await NextAsync(second, 3);
        var toShipments = await NextAsync(shipments, 1);
        var toPaid = await NextAsync(paid, 1);

        // Assert
        toFirst.Select(record => record.Revision).ShouldBe([0, 1, 2]);
        toSecond.Select(record => record.Revision).ShouldBe([0, 1, 2]);
        toShipments.Single().Stream.ShouldBe("shipments-1");
        toPaid.Single().Type.ShouldBe("invoice_paid");
        idle.Events.TryRead(out _).ShouldBeFalse();
        _sut.Routed.ShouldBe(7);
        _sut.FollowerCount.ShouldBe(5);
        A.CallTo(() => _store.ReadPointersAsync(A<long>._, A<long>._, A<int>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _store.ReadAsStoredAsync("audit-1", A<long>._, A<int>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Route_WhenAllIsFollowed_ShouldReadTheRangeWithItsBodiesOnceAndServeEveryoneFromIt()
    {
        // Arrange: two followers of $all, one of a category, one of a stream; no pointers read.
        A.CallTo(() => _store.ReadAllAsync(Direction.Forwards, 1, 3, StreamRouter.PointerPage, A<CancellationToken>._))
            .Returns([Record("orders-1", 0, 1), Record("audit-1", 0, 2), Record("orders-2", 0, 3)]);
        using var everything = _sut.Follow(StreamNames.All);
        using var everythingToo = _sut.Follow(StreamNames.All);
        using var orders = _sut.Follow("$ce-orders");
        using var audit = _sut.Follow("audit-1");

        // Act
        _tail.Advance(3);
        var toEverything = await NextAsync(everything, 3);
        var toEverythingToo = await NextAsync(everythingToo, 3);
        var toOrders = await NextAsync(orders, 2);
        var toAudit = await NextAsync(audit, 1);

        // Assert
        toEverything.Select(record => record.Position).ShouldBe([1, 2, 3]);
        toEverythingToo.Select(record => record.Position).ShouldBe([1, 2, 3]);
        toOrders.Select(record => record.Position).ShouldBe([1, 3]);
        toAudit.Single().Position.ShouldBe(2);
        A.CallTo(() => _store.ReadAllAsync(A<Direction>._, A<long>._, A<long>._, A<int>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _store.ReadPointersAsync(A<long>._, A<long>._, A<int>._, A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => _store.ReadAsStoredAsync(A<string>._, A<long>._, A<int>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Route_WhenTheDataEventsAreFollowed_ShouldReadTheRangeOnceAndLeaveThemTheSystemEventsOut()
    {
        // Arrange: the data events are nearly all of $all, so they are served as $all is; a
        // metadata event is $all's and $mde's, never theirs.
        A.CallTo(() => _store.ReadAllAsync(Direction.Forwards, 1, 3, StreamRouter.PointerPage, A<CancellationToken>._))
            .Returns([Record("orders-1", 0, 1), Record("$$orders-1", 0, 2, StreamNames.MetadataEventType), Record("orders-1", 1, 3)]);
        using var data = _sut.Follow(StreamNames.DataEvents);
        using var metadata = _sut.Follow(new VirtualStreamName(VirtualStreamKind.EventType, StreamNames.MetadataEventType).Name);

        // Act
        _tail.Advance(3);
        var toData = await NextAsync(data, 2);
        var toMetadata = await NextAsync(metadata, 1);

        // Assert
        toData.Select(record => record.Position).ShouldBe([1, 3]);
        toMetadata.Single().Stream.ShouldBe("$$orders-1");
        data.Events.TryRead(out _).ShouldBeFalse();
        A.CallTo(() => _store.ReadPointersAsync(A<long>._, A<long>._, A<int>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Route_ShouldMatchNamesWithTheBackendsComparer()
    {
        // Arrange: a backend that compares names without regard to case, as a case-insensitive
        // collation does; the followers spell the names otherwise than the store does.
        await using var sut = new StreamRouter(_store, _tail, StringComparer.OrdinalIgnoreCase);
        A.CallTo(() => _store.ReadPointersAsync(1, 2, StreamRouter.PointerPage, A<CancellationToken>._))
            .Returns([Pointer("orders-1", 0, 1, "Order_Placed"), Pointer("Orders-2", 0, 2)]);
        A.CallTo(() => _store.ReadAsStoredAsync("orders-1", 0, 1, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 0), [Record("orders-1", 0, 1, "Order_Placed")]));
        A.CallTo(() => _store.ReadAsStoredAsync("Orders-2", 0, 1, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 0), [Record("Orders-2", 0, 2)]));
        using var stream = sut.Follow("ORDERS-1");
        using var category = sut.Follow("$ce-Orders");
        using var type = sut.Follow("$et-order_placed");

        // Act
        _tail.Advance(2);
        var toStream = await NextAsync(stream, 1);
        var toCategory = await NextAsync(category, 2);
        var toType = await NextAsync(type, 2);

        // Assert
        toStream.Single().Stream.ShouldBe("orders-1");
        toCategory.Select(record => record.Stream).ShouldBe(["orders-1", "Orders-2"]);
        toType.Select(record => record.Position).ShouldBe([1, 2]);
    }

    [Fact]
    public async Task Route_WhenNobodyFollows_ShouldReadNothingAndStillPassTheRange()
    {
        // Act
        _tail.Advance(9);
        await WaitUntilAsync(() => _sut.Routed == 9);

        // Assert: a follower who arrives now catches up from its own position; the range is not re-read.
        A.CallTo(() => _store.ReadPointersAsync(A<long>._, A<long>._, A<int>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Route_WhenAFollowerLeavesTooMuchUnread_ShouldLapseItAndGoOnForTheOthers()
    {
        // Arrange: one more event of a stream than its follower, who reads nothing, may leave
        // unread, spread over two pages of pointers; then another stream's event, pointed at last.
        var count = StreamRouter.FollowerBuffer + 1;
        var records = Enumerable.Range(0, count).Select(revision => Record("orders-1", revision, revision + 1)).ToList();
        var last = Record("orders-2", 0, count + 1);
        A.CallTo(() => _store.ReadPointersAsync(1, count + 1, StreamRouter.PointerPage, A<CancellationToken>._))
            .Returns(records.Take(StreamRouter.PointerPage).Select(record => Pointer("orders-1", record.Revision, record.Position)).ToList());
        A.CallTo(() => _store.ReadPointersAsync(StreamRouter.PointerPage + 1, count + 1, StreamRouter.PointerPage, A<CancellationToken>._))
            .Returns([.. records.Skip(StreamRouter.PointerPage).Select(record => Pointer("orders-1", record.Revision, record.Position)), Pointer("orders-2", 0, count + 1)]);
        A.CallTo(() => _store.ReadAsStoredAsync("orders-1", 0, StreamRouter.PointerPage, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, count - 1), records.Take(StreamRouter.PointerPage).ToList()));
        A.CallTo(() => _store.ReadAsStoredAsync("orders-1", StreamRouter.PointerPage, 1, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, count - 1), records.Skip(StreamRouter.PointerPage).ToList()));
        A.CallTo(() => _store.ReadAsStoredAsync("orders-2", 0, 1, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 0), [last]));
        using var slow = _sut.Follow("orders-1");
        using var other = _sut.Follow("orders-2");

        // Act
        _tail.Advance(count + 1);
        var toOther = await NextAsync(other, 1);
        var slowGot = 0;
        while (slow.Events.TryRead(out _))
        {
            slowGot++;
        }

        var slowOver = await slow.Events.WaitToReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(Wait, TestContext.Current.CancellationToken);

        // Assert: the slow one keeps what fit, then its feed ends as a lapse; the other is unaffected.
        toOther.Single().Stream.ShouldBe("orders-2");
        slowGot.ShouldBe(StreamRouter.FollowerBuffer);
        slowOver.ShouldBeFalse();
        slow.Lapsed.ShouldBeTrue();
        other.Lapsed.ShouldBeFalse();
        _sut.FollowerCount.ShouldBe(1);
    }

    [Fact]
    public async Task Route_WhenAFollowedStreamWasDeleted_ShouldTellItsFollowersAndNobodyElse()
    {
        // Arrange
        A.CallTo(() => _store.ReadPointersAsync(1, 2, StreamRouter.PointerPage, A<CancellationToken>._))
            .Returns([Pointer("orders-1", 3, 1), Pointer("orders-2", 0, 2)]);
        A.CallTo(() => _store.ReadAsStoredAsync("orders-1", 3, 1, A<CancellationToken>._))
            .Throws(new StreamDeletedException("orders-1"));
        A.CallTo(() => _store.ReadAsStoredAsync("orders-2", 0, 1, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 0), [Record("orders-2", 0, 2)]));
        using var deleted = _sut.Follow("orders-1");
        using var other = _sut.Follow("orders-2");

        // Act
        _tail.Advance(2);
        var toOther = await NextAsync(other, 1);

        // Assert
        await Should.ThrowAsync<StreamDeletedException>(() => deleted.Events.WaitToReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(Wait, TestContext.Current.CancellationToken));
        toOther.Single().Revision.ShouldBe(0);
        _sut.FollowerCount.ShouldBe(1);
    }

    [Fact]
    public async Task Route_WhenARangeCannotBeRead_ShouldFailEveryFollowerPresentAndRouteTheNextAdvance()
    {
        // Arrange: the first range fails as each follower's own read would have; the next is read.
        A.CallTo(() => _store.ReadPointersAsync(1, 5, StreamRouter.PointerPage, A<CancellationToken>._))
            .Throws(new InvalidOperationException("The database went away."));
        A.CallTo(() => _store.ReadPointersAsync(6, 8, StreamRouter.PointerPage, A<CancellationToken>._))
            .Returns([Pointer("orders-1", 4, 8)]);
        A.CallTo(() => _store.ReadAsStoredAsync("orders-1", 4, 1, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 4), [Record("orders-1", 4, 8)]));
        using var failed = _sut.Follow("orders-1");

        // Act
        _tail.Advance(5);
        var failure = await Should.ThrowAsync<InvalidOperationException>(() => failed.Events.WaitToReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(Wait, TestContext.Current.CancellationToken));
        await WaitUntilAsync(() => _sut.Routed == 5);
        using var later = _sut.Follow("orders-1");
        _tail.Advance(8);
        var toLater = await NextAsync(later, 1);

        // Assert
        failure.Message.ShouldBe("The database went away.");
        failed.Lapsed.ShouldBeFalse();
        toLater.Single().Revision.ShouldBe(4);
    }

    [Fact]
    public async Task Follow_AfterTheTailStoppedFollowing_ShouldRefuseWithTheCause()
    {
        // Arrange: a tail that fails its waiters for good, the way the real one does.
        var tail = A.Fake<IStoreTail>();
        var cause = new InvalidOperationException("The server has stopped following the store's head.");
        A.CallTo(() => tail.Head).Returns(0L);
        A.CallTo(() => tail.WaitForAdvanceAsync(A<long>._, A<CancellationToken>._)).Throws(cause);
        await using var sut = new StreamRouter(_store, tail, StringComparer.Ordinal);
        using var present = sut.Follow("orders-1");

        // Act
        var toPresent = await Should.ThrowAsync<InvalidOperationException>(() => present.Events.WaitToReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(Wait, TestContext.Current.CancellationToken));
        var later = Should.Throw<InvalidOperationException>(() => sut.Follow("orders-1"));

        // Assert
        toPresent.ShouldBeSameAs(cause);
        later.ShouldBeSameAs(cause);
    }

    private static EventPointer Pointer(string stream, long revision, long position, string type = "order_placed") => new(stream, revision, position, type);

    private static EventRecord Record(string stream, long revision, long position, string type = "order_placed") =>
        new(Guid.NewGuid(), stream, revision, position, type, Created, Encoding.UTF8.GetBytes("{}"), new JsonObject());

    private static async Task<List<EventRecord>> NextAsync(StreamFollowing following, int count)
    {
        var records = new List<EventRecord>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(Wait);
        while (records.Count < count)
        {
            records.Add(await following.Events.ReadAsync(timeout.Token));
        }

        return records;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(Wait);
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
