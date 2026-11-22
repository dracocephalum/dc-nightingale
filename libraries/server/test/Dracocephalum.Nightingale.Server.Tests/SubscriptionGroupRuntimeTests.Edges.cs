using FakeItEasy;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Tests;

/// <content>
/// The runtime's edges: answers for events it does not hold, a replayed event read back by
/// whichever number its group speaks, and a choice of consumer that has to
/// be made again because the consumers changed.
/// </content>
public sealed partial class SubscriptionGroupRuntimeTests
{
    [Fact]
    public async Task Leave_WhenTheConsumerHasLeftAlready_ShouldChangeNothing()
    {
        // Arrange
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Forwards, 0, 500, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 0), [Record("orders-1", 0, 10)]));
        await using var sut = Group("orders-1", -1, 1, settings => settings with { Start = StreamPosition.Start });
        var second = sut.Join(1, "two");
        var before = sut.ConsumerCount;
        sut.Leave(_consumer!);
        var first = await Next(second);

        // Act
        sut.Leave(_consumer!);

        // Assert: the event the second consumer holds is not taken from it by the first one leaving again.
        before.ShouldBe(2);
        sut.ConsumerCount.ShouldBe(1);
        first.Record.Revision.ShouldBe(0);
        sut.Describe().InFlightCount.ShouldBe(1);
    }

    [Fact]
    public async Task Refusing_AnEventTheGroupDoesNotHold_ShouldChangeNothing()
    {
        // Arrange
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Forwards, 0, 500, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 0), [Record("orders-1", 0, 10)]));
        await using var sut = Group("orders-1", -1, 1, settings => settings with { Start = StreamPosition.Start });
        await Next(sut);

        // Act
        await sut.RefuseAsync([Guid.NewGuid()], NackAction.Park, "unknown");

        // Assert
        sut.Checkpoint.ShouldBe(-1);
        sut.Describe().InFlightCount.ShouldBe(1);
        A.CallTo(() => _groups.ParkAsync(A<SubscriptionParkedMessage>._, A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Refusing_AReplayedEventWithSkip_ShouldTakeItOffTheOutboxAndParkNothing()
    {
        // Arrange: an event at revision 5 on the outbox, below the checkpoint the stream is followed from.
        var replayed = Record("orders-1", 5, 50);
        A.CallTo(() => _groups.DueAsync(A<Guid>._, A<DateTimeOffset>._, A<CancellationToken>._))
            .Returns(new List<SubscriptionOutboxMessage> { new(Guid.Empty, 50, 5, null, replayed.Id, "poison", 3, Now) });
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Forwards, 5, 1, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 8), [replayed]));
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Forwards, 9, 500, A<CancellationToken>._))
            .Returns((StreamSlice?)null);
        await using var sut = Group("orders-1", checkpoint: 8, consumerBuffer: 1);
        var first = await Next(sut);

        // Act
        await sut.RefuseAsync([first.Record.Id], NackAction.Skip, "not again");

        // Assert
        first.Record.Revision.ShouldBe(5);
        A.CallTo(() => _groups.DequeueAsync(A<Guid>._, 50, A<string>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _groups.ParkAsync(A<SubscriptionParkedMessage>._, A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Delivery_WhenAReplayedEventIsGone_ShouldTakeItOffTheOutboxAndGoOnWithTheStream()
    {
        // Arrange: the outbox names revision 5, which the stream no longer holds.
        A.CallTo(() => _groups.DueAsync(A<Guid>._, A<DateTimeOffset>._, A<CancellationToken>._))
            .Returns(new List<SubscriptionOutboxMessage> { new(Guid.Empty, 50, 5, null, Guid.NewGuid(), "poison", 3, Now) });
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Forwards, 5, 1, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(6, 9), [Record("orders-1", 6, 60)]));
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Forwards, 9, 500, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(6, 9), [Record("orders-1", 9, 90)]));
        await using var sut = Group("orders-1", checkpoint: 8, consumerBuffer: 2);

        // Act
        var first = await Next(sut);

        // Assert
        first.Record.Revision.ShouldBe(9);
        first.RetryCount.ShouldBe(0);
        A.CallTo(() => _groups.DequeueAsync(A<Guid>._, 50, A<string>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Delivery_OfAnAllGroup_ShouldReadAReplayedEventBackByItsPosition()
    {
        // Arrange: the group stands at the head, so only the outbox has anything for it.
        var replayed = Record("orders-1", 5, 50);
        _tail.Advance(60);
        A.CallTo(() => _groups.DueAsync(A<Guid>._, A<DateTimeOffset>._, A<CancellationToken>._))
            .Returns(new List<SubscriptionOutboxMessage> { new(Guid.Empty, 50, 5, null, replayed.Id, "poison", 2, Now) });
        A.CallTo(() => _store.ReadAllAsync(Direction.Forwards, 50, 50, 1, A<CancellationToken>._)).Returns([replayed]);
        await using var sut = Group(StreamNames.All, checkpoint: 60, consumerBuffer: 1);

        // Act
        var first = await Next(sut);

        // Assert
        first.Record.Id.ShouldBe(replayed.Id);
        first.RetryCount.ShouldBe(2);
    }

    [Fact]
    public async Task Delivery_OfACategoryGroup_ShouldReadAReplayedEventBackByItsPosition()
    {
        // Arrange: the group stands at the head, so only the outbox has anything for it.
        var orders = new VirtualStreamName(VirtualStreamKind.Category, "orders");
        var replayed = Record("orders-1", 5, 50);
        _tail.Advance(60);
        A.CallTo(() => _groups.DueAsync(A<Guid>._, A<DateTimeOffset>._, A<CancellationToken>._))
            .Returns(new List<SubscriptionOutboxMessage> { new(Guid.Empty, 50, 5, null, replayed.Id, "poison", 2, Now) });
        A.CallTo(() => _store.ReadVirtualAsync(orders, Direction.Forwards, 50, 50, 1, A<CancellationToken>._)).Returns([replayed]);
        await using var sut = Group("$ce-orders", checkpoint: 60, consumerBuffer: 1);

        // Act
        var first = await Next(sut);

        // Assert
        first.Record.Id.ShouldBe(replayed.Id);
        first.RetryCount.ShouldBe(2);
    }

    [Fact]
    public async Task Delivery_OfAnOrdinalGroup_ShouldReadAReplayedEventBackByItsOrdinalAndDropOneWithout()
    {
        // Arrange: two messages on the outbox, one with the ordinal its event has and one from
        // before the group spoke ordinals, with none; the feed itself has nothing past ordinal 7.
        var orders = new VirtualStreamName(VirtualStreamKind.Category, "orders");
        var replayed = Record("orders-1", 5, 50) with { Ordinal = 3 };
        _tail.Advance(60);
        _tail.Number(60);
        A.CallTo(() => _groups.DueAsync(A<Guid>._, A<DateTimeOffset>._, A<CancellationToken>._))
            .Returns(new List<SubscriptionOutboxMessage>
            {
                new(Guid.Empty, 40, 4, null, Guid.NewGuid(), "poison", 1, Now),
                new(Guid.Empty, 50, 5, 3, replayed.Id, "poison", 2, Now),
            });
        A.CallTo(() => _store.ReadByOrdinalAsync(orders, Direction.Forwards, 3, 1, A<CancellationToken>._)).Returns([replayed]);
        A.CallTo(() => _store.ReadByOrdinalAsync(orders, Direction.Forwards, 8, 500, A<CancellationToken>._)).Returns([]);
        await using var sut = Group("$ce-orders", checkpoint: 7, consumerBuffer: 1, settings => settings with { Numbering = Numbering.Ordinal });

        // Act
        var first = await Next(sut);

        // Assert
        first.Record.Ordinal.ShouldBe(3);
        first.RetryCount.ShouldBe(2);
        A.CallTo(() => _groups.DequeueAsync(A<Guid>._, 40, A<string>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Delivery_OfAStreamGroupFromTheEnd_ShouldStartAfterTheLastRevision()
    {
        // Arrange: the stream's last revision is 4 when the group starts, so the feed begins at 5.
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Forwards, long.MaxValue, 1, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 4), []));
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Forwards, 5, 500, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 5), [Record("orders-1", 5, 50)]));
        await using var sut = Group("orders-1", -1, 1);

        // Act
        var first = await Next(sut);

        // Assert
        first.Record.Revision.ShouldBe(5);
    }

    [Fact]
    public async Task Pinned_WhenTheConsumersChangeWhileAnEventWaitsForItsOwn_ShouldChooseAgainAndDeliverItOnce()
    {
        // Arrange: two events of one stream, read once both consumers have joined, each with
        // room for one. The stream's consumer takes the first event and is full, so the second
        // event waits for it while the other consumer has room.
        var joined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slice = new StreamSlice(new StreamHead(0, 1), [Record("orders-1", 0, 10), Record("orders-1", 1, 11)]);
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Forwards, 0, 500, A<CancellationToken>._))
            .ReturnsLazily(call => ReturnSliceWhenAsync(joined.Task, slice, call.GetArgument<CancellationToken>(4)));
        await using var sut = Group("orders-1", -1, 1, settings => settings with { Start = StreamPosition.Start, ConsumerStrategy = ConsumerStrategy.Pinned });
        var consumers = new List<SubscriptionConsumer> { _consumer!, sut.Join(1, "two") };
        joined.SetResult();
        var held = new List<PersistentSubscriptionMessage.Recorded>();
        await WaitUntilAsync(() =>
        {
            held.AddRange(consumers.SelectMany(Drain));
            return held.Count > 0;
        });
        var first = held.ShouldHaveSingleItem();

        // Act: a third consumer joins, so the stream may be another consumer's now; then the
        // first event is done with, so whichever consumer it is has room.
        consumers.Add(sut.Join(1, "three"));
        await sut.AcknowledgeAsync([first.Record.Id]);
        var after = new List<PersistentSubscriptionMessage.Recorded>();
        await WaitUntilAsync(() =>
        {
            after.AddRange(consumers.SelectMany(Drain));
            return after.Count > 0;
        });

        // Assert: the event that waited is delivered once, with nothing held against it.
        first.Record.Revision.ShouldBe(0);
        var second = after.ShouldHaveSingleItem();
        second.Record.Revision.ShouldBe(1);
        second.RetryCount.ShouldBe(0);
        sut.Describe().InFlightCount.ShouldBe(1);
    }

    [Fact]
    public async Task Pinned_WhenTheConsumersChangeWhileARetryWaitsForItsOwn_ShouldChooseAgainAndDeliverItOnce()
    {
        // Arrange: three events of one stream, read once both consumers have joined, each with
        // room for two. The stream's consumer holds the first two; the first is refused for a
        // retry, which frees the room the third event was waiting for, so the retry waits for
        // its consumer while the other has room.
        var joined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slice = new StreamSlice(new StreamHead(0, 2), [Record("orders-1", 0, 10), Record("orders-1", 1, 11), Record("orders-1", 2, 12)]);
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Forwards, 0, 500, A<CancellationToken>._))
            .ReturnsLazily(call => ReturnSliceWhenAsync(joined.Task, slice, call.GetArgument<CancellationToken>(4)));
        await using var sut = Group("orders-1", -1, 2, settings => settings with { Start = StreamPosition.Start, ConsumerStrategy = ConsumerStrategy.Pinned });
        var consumers = new List<SubscriptionConsumer> { _consumer!, sut.Join(2, "two") };
        joined.SetResult();
        var held = new List<PersistentSubscriptionMessage.Recorded>();
        await WaitUntilAsync(() =>
        {
            held.AddRange(consumers.SelectMany(Drain));
            return held.Count == 2;
        });
        var refused = held.Single(message => message.Record.Revision == 0);
        await sut.RefuseAsync([refused.Record.Id], NackAction.Retry, "busy");
        await WaitUntilAsync(() =>
        {
            held.AddRange(consumers.SelectMany(Drain));
            return held.Count == 3;
        });

        // Act: a third consumer joins, so the stream may be another consumer's now; then the
        // two events in flight are done with, so whichever consumer it is has room.
        consumers.Add(sut.Join(2, "three"));
        await sut.AcknowledgeAsync(held.Where(message => message.Record.Revision != 0).Select(message => message.Record.Id).ToList());
        var after = new List<PersistentSubscriptionMessage.Recorded>();
        await WaitUntilAsync(() =>
        {
            after.AddRange(consumers.SelectMany(Drain));
            return after.Count > 0;
        });

        // Assert: the retry is delivered once, counted as the one retry it is.
        held.Select(message => message.Record.Revision).ShouldBe([0, 1, 2], ignoreOrder: true);
        var again = after.ShouldHaveSingleItem();
        again.Record.Id.ShouldBe(refused.Record.Id);
        again.RetryCount.ShouldBe(1);
        sut.Describe().ShouldSatisfyAllConditions(
            live => live.InFlightCount.ShouldBe(1),
            live => live.AwaitingRetryCount.ShouldBe(0));
    }

    /// <summary>A stream read that answers once the test lets it, and ends with the group if the test never does.</summary>
    private static async Task<StreamSlice?> ReturnSliceWhenAsync(Task gate, StreamSlice slice, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        return slice;
    }
}
