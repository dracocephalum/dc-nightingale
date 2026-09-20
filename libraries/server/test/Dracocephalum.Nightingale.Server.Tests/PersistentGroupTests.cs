using System.Text;
using System.Text.Json.Nodes;

using FakeItEasy;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Tests;

/// <summary>
/// The group runtime against a strict fake of the stream store, a recording fake of the group
/// store, a tail moved by hand and a clock moved by hand: what is delivered, in what order, and
/// what each acknowledgement, refusal and timeout does to the checkpoint and the parked rows.
/// </summary>
public sealed class PersistentGroupTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private readonly IStreamStore _store = A.Fake<IStreamStore>(options => options.Strict());
    private readonly IGroupStore _groups = A.Fake<IGroupStore>();
    private readonly FakeTail _tail = new();
    private readonly FakeTimeProvider _time = new(Now);

    public PersistentGroupTests()
    {
        A.CallTo(() => _groups.ReplayableAsync(A<string>._, A<string>._, A<CancellationToken>._)).Returns(new List<ParkedMessage>());
    }

    [Fact]
    public async Task Delivery_ShouldStartAtTheCheckpointAndWriteItOnceEnoughIsAcknowledged()
    {
        // Arrange: three events, room for two; the checkpoint is written every two acknowledgements.
        var events = new[] { Record("orders-1", 0, 10), Record("orders-1", 1, 11), Record("orders-1", 2, 12) };
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Forwards, 0, 500, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 2), events));
        await using var sut = Group("orders-1", checkpoint: -1, consumerBuffer: 2, settings => settings with { Start = StreamPosition.Start, CheckpointUpperBound = 2, CheckpointLowerBound = 1 });

        // Act
        var first = await Next(sut);
        var second = await Next(sut);
        await sut.AcknowledgeAsync([first.Record.Id]);
        var third = await Next(sut);
        await sut.AcknowledgeAsync([second.Record.Id]);

        // Assert
        first.RetryCount.ShouldBe(0);
        new[] { first, second, third }.Select(message => message.Record.Revision).ShouldBe([0, 1, 2]);
        sut.Checkpoint.ShouldBe(1);
        A.CallTo(() => _groups.SaveCheckpointAsync("orders-1", "g", 1, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Acknowledging_OutOfOrder_ShouldAdvanceTheCheckpointOnlyOverWhatIsDone()
    {
        // Arrange
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Forwards, 0, 500, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 1), [Record("orders-1", 0, 10), Record("orders-1", 1, 11)]));
        await using var sut = Group("orders-1", -1, 2, settings => settings with { Start = StreamPosition.Start });
        var first = await Next(sut);
        var second = await Next(sut);

        // Act
        await sut.AcknowledgeAsync([second.Record.Id]);
        var afterSecond = sut.Checkpoint;
        await sut.AcknowledgeAsync([first.Record.Id]);

        // Assert
        afterSecond.ShouldBe(-1);
        sut.Checkpoint.ShouldBe(1);
    }

    [Fact]
    public async Task Refusing_WithRetry_ShouldRedeliverWithTheCountThenParkAtTheLimit()
    {
        // Arrange: one retry allowed.
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Forwards, 0, 500, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 0), [Record("orders-1", 0, 10)]));
        await using var sut = Group("orders-1", -1, 1, settings => settings with { Start = StreamPosition.Start, MaxRetryCount = 1 });
        var first = await Next(sut);

        // Act
        await sut.RefuseAsync([first.Record.Id], NackAction.Retry, "not yet");
        var again = await Next(sut);
        await sut.RefuseAsync([again.Record.Id], NackAction.Retry, "still not");

        // Assert
        again.RetryCount.ShouldBe(1);
        again.Record.Id.ShouldBe(first.Record.Id);
        A.CallTo(() => _groups.ParkAsync(A<ParkedMessage>.That.Matches(parked => parked.Position == 10 && parked.Revision == 0 && parked.Ordinal == null && parked.Attempts == 1 && parked.Reason == "still not"), A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        sut.Checkpoint.ShouldBe(0);
    }

    [Fact]
    public async Task Refusing_WithPark_ShouldParkNowAndCountAsDone()
    {
        // Arrange
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Forwards, 0, 500, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 0), [Record("orders-1", 0, 10)]));
        await using var sut = Group("orders-1", -1, 1, settings => settings with { Start = StreamPosition.Start });
        var first = await Next(sut);

        // Act
        await sut.RefuseAsync([first.Record.Id], NackAction.Park, "poison");

        // Assert
        A.CallTo(() => _groups.ParkAsync(A<ParkedMessage>.That.Matches(parked => parked.EventId == first.Record.Id && parked.Reason == "poison" && !parked.Replay), A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        sut.Checkpoint.ShouldBe(0);
    }

    [Fact]
    public async Task Expiring_AfterTheMessageTimeout_ShouldRedeliver()
    {
        // Arrange
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Forwards, 0, 500, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 0), [Record("orders-1", 0, 10)]));
        await using var sut = Group("orders-1", -1, 1, settings => settings with { Start = StreamPosition.Start, MessageTimeout = TimeSpan.FromSeconds(30) });
        var first = await Next(sut);

        // Act
        _time.Advance(TimeSpan.FromSeconds(31));
        await sut.ExpireAsync();
        var again = await Next(sut);

        // Assert
        again.Record.Id.ShouldBe(first.Record.Id);
        again.RetryCount.ShouldBe(1);
    }

    [Fact]
    public async Task Delivery_ShouldReplayAMarkedParkedMessageFirstAndUnparkItOnAcknowledgement()
    {
        // Arrange: a parked event at revision 5 marked for replay; the stream itself is followed from the checkpoint after it.
        var parked = Record("orders-1", 5, 50);
        A.CallTo(() => _groups.ReplayableAsync("orders-1", "g", A<CancellationToken>._))
            .Returns(new List<ParkedMessage> { new("orders-1", "g", 50, 5, null, parked.Id, "poison", 3, Now, true) });
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Forwards, 5, 1, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 8), [parked]));
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Forwards, 9, 500, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 9), [Record("orders-1", 9, 90)]));
        await using var sut = Group("orders-1", checkpoint: 8, consumerBuffer: 2);

        // Act
        var first = await Next(sut);
        var second = await Next(sut);
        await sut.AcknowledgeAsync([first.Record.Id]);

        // Assert
        first.Record.Revision.ShouldBe(5);
        first.RetryCount.ShouldBe(3);
        second.Record.Revision.ShouldBe(9);
        A.CallTo(() => _groups.UnparkAsync("orders-1", "g", 50, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        sut.Checkpoint.ShouldBe(8, "acknowledging a replayed message below the checkpoint must not move it back");
    }

    [Fact]
    public async Task Delivery_OfAnAllGroupFromTheEnd_ShouldWaitForTheTailThenDeliverWhatItBrings()
    {
        // Arrange: the head is 7 when the group starts, so the feed begins at 8.
        _tail.Advance(7);
        A.CallTo(() => _store.ReadAllAsync(Direction.Forwards, 8, 9, 500, A<CancellationToken>._))
            .Returns([Record("orders-3", 0, 9)]);
        await using var sut = Group("$all", -1, 1);

        // Act
        _tail.Advance(9);
        var first = await Next(sut);

        // Assert
        first.Record.Position.ShouldBe(9);
    }

    [Fact]
    public async Task Dispose_ShouldWriteTheCheckpointWhateverThePolicySays()
    {
        // Arrange
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Forwards, 0, 500, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 0), [Record("orders-1", 0, 10)]));
        var sut = Group("orders-1", -1, 1, settings => settings with { Start = StreamPosition.Start });
        var first = await Next(sut);
        await sut.AcknowledgeAsync([first.Record.Id]);

        // Act
        await sut.DisposeAsync();

        // Assert
        A.CallTo(() => _groups.SaveCheckpointAsync("orders-1", "g", 0, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Delivery_OfAnOrdinalGroup_ShouldKeyTheCheckpointAndTheParkedRowByOrdinal()
    {
        // Arrange: a category group created under ordinal numbering, from the start; two numbered
        // events at sparse positions, and the sequencer level with the tail so the feed waits on it.
        var orders = new VirtualStreamName(VirtualStreamKind.Category, "orders");
        _tail.Advance(20);
        A.CallTo(() => _store.ReadByOrdinalAsync(orders, Direction.Forwards, 0, 500, A<CancellationToken>._))
            .Returns([Record("orders-1", 0, 5) with { Ordinal = 0 }, Record("orders-2", 0, 17) with { Ordinal = 1 }]);
        A.CallTo(() => _store.NumberedThroughAsync(A<CancellationToken>._)).Returns(20L);
        await using var sut = Group("$ce-orders", -1, 2, settings => settings with { Start = StreamPosition.Start, Numbering = Numbering.Ordinal, CheckpointUpperBound = 1 });

        // Act
        var first = await Next(sut);
        var second = await Next(sut);
        await sut.AcknowledgeAsync([first.Record.Id]);
        await sut.RefuseAsync([second.Record.Id], NackAction.Park, "no");

        // Assert: every number the group keeps is an ordinal, never the position.
        first.Record.Ordinal.ShouldBe(0);
        second.Record.Ordinal.ShouldBe(1);
        sut.Checkpoint.ShouldBe(1);
        A.CallTo(() => _groups.SaveCheckpointAsync("$ce-orders", "g", 1, A<CancellationToken>._)).MustHaveHappened();
        A.CallTo(() => _groups.ParkAsync(A<ParkedMessage>.That.Matches(parked => parked.Position == 17 && parked.Ordinal == 1 && parked.Revision == 0 && parked.EventId == second.Record.Id), A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Delivery_OfAnOrdinalGroupFromTheEnd_ShouldStartAfterTheLastOrdinal()
    {
        // Arrange: the category's last ordinal is 4 when the group starts, so the feed begins at 5.
        var orders = new VirtualStreamName(VirtualStreamKind.Category, "orders");
        _tail.Advance(20);
        A.CallTo(() => _store.OrdinalHeadAsync(orders, A<CancellationToken>._)).Returns(new StreamHead(0, 4));
        A.CallTo(() => _store.ReadByOrdinalAsync(orders, Direction.Forwards, 5, 500, A<CancellationToken>._))
            .Returns([Record("orders-9", 0, 33) with { Ordinal = 5 }]);
        A.CallTo(() => _store.NumberedThroughAsync(A<CancellationToken>._)).Returns(20L);
        await using var sut = Group("$ce-orders", -1, 1, settings => settings with { Numbering = Numbering.Ordinal });

        // Act
        var first = await Next(sut);

        // Assert
        first.Record.Ordinal.ShouldBe(5);
    }

    private static EventRecord Record(string stream, long revision, long position) =>
        new(Guid.NewGuid(), stream, revision, position, "order_placed", Now, Encoding.UTF8.GetBytes("{}"), new JsonObject());

    private static async Task<PersistentSubscriptionMessage.Recorded> Next(PersistentGroup group) =>
        await group.Outgoing.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(Wait);

    private PersistentGroup Group(string stream, long checkpoint, int consumerBuffer, Func<GroupSettings, GroupSettings>? adjust = null)
    {
        var settings = (adjust ?? (defaults => defaults))(GroupSettings.Default);
        return new PersistentGroup(_store, _tail, _groups, _time, new GroupDefinition(stream, "g", settings, checkpoint), consumerBuffer);
    }
}
