using System.Text;
using System.Text.Json.Nodes;

using FakeItEasy;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Tests;

/// <summary>
/// The group runtime against a strict fake of the stream store, a recording fake of the group
/// store, a tail moved by hand and a clock moved by hand: what is delivered, in what order, and
/// what each acknowledgement, refusal and timeout does to the checkpoint and the parked rows;
/// then how several consumers share the events under each strategy, and what a consumer's
/// leaving does to what it held.
/// </summary>
public sealed class SubscriptionGroupRuntimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private readonly IStreamStore _store = A.Fake<IStreamStore>(options => options.Strict());
    private readonly ISubscriptionGroupStore _groups = A.Fake<ISubscriptionGroupStore>();
    private readonly FakeTail _tail = new();
    private readonly FakeTimeProvider _time = new(Now);
    private SubscriptionConsumer? _consumer;

    public SubscriptionGroupRuntimeTests()
    {
        A.CallTo(() => _groups.DueAsync(A<Guid>._, A<DateTimeOffset>._, A<CancellationToken>._)).Returns(new List<SubscriptionOutboxMessage>());
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
        A.CallTo(() => _groups.SaveCheckpointAsync(A<Guid>._, 1, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
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
        A.CallTo(() => _groups.ParkAsync(A<SubscriptionParkedMessage>.That.Matches(parked => parked.Position == 10 && parked.Revision == 0 && parked.Ordinal == null && parked.Attempts == 1 && parked.Reason == "still not"), A<CancellationToken>._)).MustHaveHappenedOnceExactly();
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
        A.CallTo(() => _groups.ParkAsync(A<SubscriptionParkedMessage>.That.Matches(parked => parked.EventId == first.Record.Id && parked.Reason == "poison"), A<CancellationToken>._)).MustHaveHappenedOnceExactly();
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
    public async Task Delivery_ShouldDeliverTheOutboxFirstAndDequeueOnAcknowledgement()
    {
        // Arrange: an event at revision 5 on the outbox; the stream itself is followed from the checkpoint after it.
        var replayed = Record("orders-1", 5, 50);
        A.CallTo(() => _groups.DueAsync(A<Guid>._, A<DateTimeOffset>._, A<CancellationToken>._))
            .Returns(new List<SubscriptionOutboxMessage> { new(Guid.Empty, 50, 5, null, replayed.Id, "poison", 3, Now) });
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Forwards, 5, 1, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 8), [replayed]));
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
        A.CallTo(() => _groups.DequeueAsync(A<Guid>._, 50, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        sut.Checkpoint.ShouldBe(8, "acknowledging a replayed message below the checkpoint must not move it back");
    }

    [Fact]
    public async Task Wake_ShouldDeliverTheOutboxAheadOfTheNextLiveEvent()
    {
        // Arrange: room for one; the stream has two events after the checkpoint and the consumer
        // holds the first. A replay arrives meanwhile; once the first is acknowledged the replayed
        // message goes out before the second live event, the reference's order.
        var replayed = Record("orders-1", 3, 30);
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Forwards, 9, 500, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 10), [Record("orders-1", 9, 90), Record("orders-1", 10, 100)]));
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Forwards, 3, 1, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 10), [replayed]));
        A.CallTo(() => _groups.DueAsync(A<Guid>._, A<DateTimeOffset>._, A<CancellationToken>._))
            .ReturnsNextFromSequence(
                new List<SubscriptionOutboxMessage>(),
                new List<SubscriptionOutboxMessage> { new(Guid.Empty, 30, 3, null, replayed.Id, "poison", 2, Now) });
        await using var sut = Group("orders-1", checkpoint: 8, consumerBuffer: 1);
        var first = await Next(sut);

        // Act
        sut.Wake();
        await WaitUntilAsync(() => Fake.GetCalls(_store).Any(call => call.Method.Name == nameof(IStreamStore.ReadAsync) && Equals(call.Arguments[2], 3L)));
        await sut.AcknowledgeAsync([first.Record.Id]);
        var second = await Next(sut);
        await sut.AcknowledgeAsync([second.Record.Id]);
        var third = await Next(sut);

        // Assert
        first.Record.Revision.ShouldBe(9);
        second.Record.Revision.ShouldBe(3);
        second.RetryCount.ShouldBe(2);
        third.Record.Revision.ShouldBe(10);
    }

    [Fact]
    public async Task Wake_ShouldDeliverWhatArrivedOnTheOutboxWhileTheConsumerIsConnectedAndNeverTwice()
    {
        // Arrange: nothing on the outbox when the consumer connects; a replay puts one message
        // there and wakes the group; a second wake before the message is acknowledged finds it
        // still on the outbox and must not deliver it again.
        var replayed = Record("orders-1", 3, 30);
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Forwards, 9, 500, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 8), []));
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Forwards, 3, 1, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 8), [replayed]));
        A.CallTo(() => _groups.DueAsync(A<Guid>._, A<DateTimeOffset>._, A<CancellationToken>._))
            .ReturnsNextFromSequence(
                new List<SubscriptionOutboxMessage>(),
                new List<SubscriptionOutboxMessage> { new(Guid.Empty, 30, 3, null, replayed.Id, "poison", 2, Now) },
                new List<SubscriptionOutboxMessage> { new(Guid.Empty, 30, 3, null, replayed.Id, "poison", 2, Now) });
        await using var sut = Group("orders-1", checkpoint: 8, consumerBuffer: 2);

        // Act
        sut.Wake();
        var first = await Next(sut);
        sut.Wake();
        var deliveredAgain = false;
        try
        {
            deliveredAgain = await _consumer!.Outgoing.Reader.WaitToReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);
        }
        catch (TimeoutException)
        {
            // Nothing arrived, which is the point.
        }

        // Assert
        first.Record.Revision.ShouldBe(3);
        first.RetryCount.ShouldBe(2);
        deliveredAgain.ShouldBeFalse("the message is in flight and stays on the outbox until it is done");
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
        A.CallTo(() => _groups.SaveCheckpointAsync(A<Guid>._, 0, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
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
        A.CallTo(() => _groups.SaveCheckpointAsync(A<Guid>._, 1, A<CancellationToken>._)).MustHaveHappened();
        A.CallTo(() => _groups.ParkAsync(A<SubscriptionParkedMessage>.That.Matches(parked => parked.Position == 17 && parked.Ordinal == 1 && parked.Revision == 0 && parked.EventId == second.Record.Id), A<CancellationToken>._)).MustHaveHappenedOnceExactly();
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

    [Fact]
    public async Task RoundRobin_ShouldHandEachEventToTheNextConsumerWithRoom()
    {
        // Arrange
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Forwards, 0, 500, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 3), [Record("orders-1", 0, 10), Record("orders-1", 1, 11), Record("orders-1", 2, 12), Record("orders-1", 3, 13)]));
        await using var sut = Group("orders-1", -1, 1, settings => settings with { Start = StreamPosition.Start });
        var second = sut.Join(1, "two");

        // Act
        var toFirst = await Next(sut);
        var toSecond = await Next(second);
        await sut.AcknowledgeAsync([toSecond.Record.Id]);
        var toSecondAgain = await Next(second);

        // Assert
        toFirst.Record.Revision.ShouldBe(0);
        toSecond.Record.Revision.ShouldBe(1);
        toSecondAgain.Record.Revision.ShouldBe(2, "the first consumer is full, so the next event skips it");
        var live = sut.Describe();
        live.ConsumerCount.ShouldBe(2);
        live.InFlightCount.ShouldBe(2);
        live.ConsumerBufferSize.ShouldBe(2);
        live.Consumers.Select(consumer => consumer.InFlightCount).ShouldBe([1, 1]);
    }

    [Fact]
    public async Task Pinned_ShouldKeepAStreamWithOneConsumer()
    {
        // Arrange
        var records = Enumerable.Range(0, 12).Select(index => Record("orders-" + (index % 3), index, 10 + index)).ToList();
        _tail.Advance(21);
        A.CallTo(() => _store.ReadAllAsync(Direction.Forwards, 0, 21, 500, A<CancellationToken>._)).Returns(records);
        await using var sut = Group(StreamNames.All, -1, 12, settings => settings with { Start = StreamPosition.Start, ConsumerStrategy = ConsumerStrategy.Pinned });
        var second = sut.Join(12, "two");
        var third = sut.Join(12, "three");
        var consumers = new[] { _consumer!, second, third };
        await WaitUntilAsync(() => sut.Describe().InFlightCount == 12);

        // Act
        var streamsPerConsumer = consumers.Select(consumer => Drain(consumer).Select(message => message.Record.Stream).Distinct().ToList()).ToList();

        // Assert
        streamsPerConsumer.SelectMany(streams => streams).Count().ShouldBe(3, "each stream is with exactly one consumer");
        streamsPerConsumer.SelectMany(streams => streams).Distinct().Count().ShouldBe(3);
    }

    [Fact]
    public async Task PinnedByCorrelation_ShouldKeepAWorkflowWithOneConsumerAcrossStreamsAndFallBackToTheStream()
    {
        // Arrange: twelve events over three streams, four workflows crossing them, and three
        // events with no correlation id at all, on a stream of their own.
        var records = Enumerable.Range(0, 12).Select(index => Record("orders-" + (index % 3), index, 10 + index, "checkout-" + (index % 4)))
            .Concat(Enumerable.Range(12, 3).Select(index => Record("audit-1", index, 10 + index)))
            .ToList();
        _tail.Advance(24);
        A.CallTo(() => _store.ReadAllAsync(Direction.Forwards, 0, 24, 500, A<CancellationToken>._)).Returns(records);
        await using var sut = Group(StreamNames.All, -1, 15, settings => settings with { Start = StreamPosition.Start, ConsumerStrategy = ConsumerStrategy.PinnedByCorrelation });
        var second = sut.Join(15, "two");
        var third = sut.Join(15, "three");
        var consumers = new[] { _consumer!, second, third };
        await WaitUntilAsync(() => sut.Describe().InFlightCount == 15);

        // Act
        var delivered = consumers.Select(Drain).ToList();

        // Assert: a workflow is with exactly one consumer whatever streams it spans, and so is
        // the uncorrelated stream.
        var workflowsPerConsumer = delivered.Select(messages => messages.Select(message => message.Record.Metadata[MetadataKeys.CorrelationId]?.GetValue<string>()).Where(id => id is not null).Distinct().ToList()).ToList();
        workflowsPerConsumer.SelectMany(ids => ids).Count().ShouldBe(4, "each workflow is with exactly one consumer");
        workflowsPerConsumer.SelectMany(ids => ids).Distinct().Count().ShouldBe(4);
        delivered.Count(messages => messages.Any(message => message.Record.Stream == "audit-1")).ShouldBe(1, "events without a correlation id are pinned by their stream");
        delivered.Sum(messages => messages.Count).ShouldBe(15);
    }

    [Fact]
    public async Task DispatchToSingle_ShouldSendEverythingToTheFirstConsumerUntilItLeaves()
    {
        // Arrange
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Forwards, 0, 500, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 2), [Record("orders-1", 0, 10), Record("orders-1", 1, 11), Record("orders-1", 2, 12)]));
        await using var sut = Group("orders-1", -1, 2, settings => settings with { Start = StreamPosition.Start, ConsumerStrategy = ConsumerStrategy.DispatchToSingle });
        var second = sut.Join(2, "two");

        // Act
        var first = await Next(sut);
        var next = await Next(sut);
        sut.Leave(_consumer!);
        var afterLeaving = new[] { await Next(second), await Next(second) };

        // Assert
        first.Record.Revision.ShouldBe(0);
        next.Record.Revision.ShouldBe(1);
        afterLeaving.Select(message => message.Record.Revision).ShouldBe([0, 1], "what the first consumer held goes to the next, ahead of the stream");
        afterLeaving.Select(message => message.RetryCount).ShouldBe([0, 0], "a consumer leaving is not the event's fault");
        sut.Describe().ConsumerCount.ShouldBe(1);
    }

    [Fact]
    public async Task Leave_ShouldCompleteTheConsumersChannelAndKeepWhatItAcknowledges()
    {
        // Arrange
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Forwards, 0, 500, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 1), [Record("orders-1", 0, 10), Record("orders-1", 1, 11)]));
        await using var sut = Group("orders-1", -1, 2, settings => settings with { Start = StreamPosition.Start, CheckpointUpperBound = 1 });
        var first = await Next(sut);
        await Next(sut);

        // Act
        sut.Leave(_consumer!);
        await sut.AcknowledgeAsync([first.Record.Id]);
        await _consumer!.Outgoing.Reader.Completion.WaitAsync(Wait, TestContext.Current.CancellationToken);

        // Assert
        _consumer.Outgoing.Reader.Completion.IsCompletedSuccessfully.ShouldBeTrue();
        sut.Checkpoint.ShouldBe(-1, "the acknowledgement arrived after the event went back to the group, so it is not done");
        sut.Describe().AwaitingRetryCount.ShouldBe(2);
    }

    [Fact]
    public async Task Fail_ShouldEndEveryConsumerWithTheCauseAndRefuseTheNext()
    {
        // Arrange
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Forwards, 0, 500, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 0), [Record("orders-1", 0, 10)]));
        await using var sut = Group("orders-1", -1, 1, settings => settings with { Start = StreamPosition.Start });
        var second = sut.Join(1, "two");
        var cause = new InvalidOperationException("updated");

        // Act
        sut.Fail(cause);
        var late = sut.Join(1, "three");

        // Assert
        foreach (var consumer in new[] { _consumer!, second, late })
        {
            var ended = await Should.ThrowAsync<InvalidOperationException>(async () =>
            {
                await foreach (var message in consumer.Outgoing.Reader.ReadAllAsync(TestContext.Current.CancellationToken))
                {
                    // What was delivered before the failure still comes through; the end is what matters.
                    message.ShouldNotBeNull();
                }
            });
            ended.ShouldBeSameAs(cause);
        }
    }

    private static EventRecord Record(string stream, long revision, long position, string? correlationId = null) =>
        new(Guid.NewGuid(), stream, revision, position, "order_placed", Now, Encoding.UTF8.GetBytes("{}"), correlationId is null ? new JsonObject() : new JsonObject { [MetadataKeys.CorrelationId] = correlationId });

    private static async Task<PersistentSubscriptionMessage.Recorded> Next(SubscriptionConsumer consumer) =>
        await consumer.Outgoing.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(Wait);

    private static List<PersistentSubscriptionMessage.Recorded> Drain(SubscriptionConsumer consumer)
    {
        var messages = new List<PersistentSubscriptionMessage.Recorded>();
        while (consumer.Outgoing.Reader.TryRead(out var message))
        {
            messages.Add(message);
        }

        return messages;
    }

    private async Task<PersistentSubscriptionMessage.Recorded> Next(SubscriptionGroupRuntime group) => await Next(_consumer!);

    /// <summary>Polls for something the group does on its own thread, within the usual wait.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + Wait;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException("The condition was not met in time.");
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>A running group with one consumer joined, the one <see cref="Next(SubscriptionGroupRuntime)"/> reads for.</summary>
    private SubscriptionGroupRuntime Group(string stream, long checkpoint, int consumerBuffer, Func<GroupSettings, GroupSettings>? adjust = null)
    {
        var settings = (adjust ?? (defaults => defaults))(GroupSettings.Default);
        var runtime = new SubscriptionGroupRuntime(_store, _tail, _groups, _time, new SubscriptionGroupDefinition(stream, "g", settings, checkpoint));
        _consumer = runtime.Join(consumerBuffer, "one");
        return runtime;
    }
}
