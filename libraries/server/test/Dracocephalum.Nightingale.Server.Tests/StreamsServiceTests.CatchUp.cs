using Dracocephalum.Nightingale.Protocols.Grpc.V1;
using Dracocephalum.Nightingale.Server.Grpc;
using FakeItEasy;
using Google.Rpc;
using Grpc.Core;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Tests;

/// <content>
/// A subscription's catch-up over more than a page: when it says it fell behind, and that it says
/// so once; then what ends or thins a subscription that is following.
/// </content>
public sealed partial class StreamsServiceTests
{
    [Fact]
    public async Task Read_WhenSubscribingToAStreamLongerThanAPage_ShouldSayOnceItFellBehindThenDeliverEverything()
    {
        // Arrange: 1101 events, which is two full pages and a short one.
        var head = new StreamHead(0, 1100);
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Forwards, 0, StreamsService.PageSize, A<CancellationToken>._))
            .Returns(new StreamSlice(head, Records("orders-1", 0, 512)));
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Forwards, 512, StreamsService.PageSize, A<CancellationToken>._))
            .Returns(new StreamSlice(head, Records("orders-1", 512, 512)));
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Forwards, 1024, StreamsService.PageSize, A<CancellationToken>._))
            .Returns(new StreamSlice(head, Records("orders-1", 1024, 77)));
        var client = new Streams.StreamsClient(_channel);
        using var call = client.Read(new ReadRequest { Stream = "orders-1", Start = new(), Subscription = new SubscriptionOptions() }, cancellationToken: TestContext.Current.CancellationToken);

        // Act
        var messages = await ReadThroughCaughtUpAsync(call);

        // Assert: the events to come are counted from the first one the subscription asked for.
        messages[0].Confirmed.Head.ShouldBe(1100);
        messages[1].FellBehind.ShouldSatisfyAllConditions(
            behind => behind.Head.ShouldBe(1100),
            behind => behind.EventsBehind.ShouldBe(1101));
        messages.Count(message => message.ContentCase == ReadResponse.ContentOneofCase.FellBehind).ShouldBe(1);
        messages.Skip(2).Take(1101).Select(message => message.Event.Revision).ShouldBe(Enumerable.Range(0, 1101).Select(revision => (long)revision));
        messages[^1].CaughtUp.Head.ShouldBe(1100);
    }

    [Fact]
    public async Task Read_WhenSubscribingToAStreamOfExactlyAPage_ShouldNotSayItFellBehind()
    {
        // Arrange: the page is full and is the whole stream, so the next read finds nothing.
        var head = new StreamHead(0, 511);
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Forwards, 0, StreamsService.PageSize, A<CancellationToken>._))
            .Returns(new StreamSlice(head, Records("orders-1", 0, 512)));
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Forwards, 512, StreamsService.PageSize, A<CancellationToken>._))
            .Returns(new StreamSlice(head, []));
        var client = new Streams.StreamsClient(_channel);
        using var call = client.Read(new ReadRequest { Stream = "orders-1", Start = new(), Subscription = new SubscriptionOptions() }, cancellationToken: TestContext.Current.CancellationToken);

        // Act
        var messages = await ReadThroughCaughtUpAsync(call);

        // Assert
        messages.ShouldNotContain(message => message.ContentCase == ReadResponse.ContentOneofCase.FellBehind);
        messages[1].Event.Revision.ShouldBe(0);
        messages[^1].CaughtUp.Head.ShouldBe(511);
    }

    [Theory]
    [InlineData(700, true)]
    [InlineData(512, false)]
    public async Task Read_WhenSubscribingToAllOverAFullPage_ShouldSayItFellBehindOnlyWhenThereIsMoreToRead(long head, bool expected)
    {
        // Arrange: a first page of 512 positions; with the head at 700 there are 188 more.
        _tail.Advance(head);
        A.CallTo(() => _store.ReadAllAsync(Direction.Forwards, 0, head, StreamsService.PageSize, A<CancellationToken>._))
            .Returns(Records("orders-1", 1, 512));
        A.CallTo(() => _store.ReadAllAsync(Direction.Forwards, 513, 700, StreamsService.PageSize, A<CancellationToken>._))
            .Returns(Records("orders-2", 513, 188));
        var client = new Streams.StreamsClient(_channel);
        using var call = client.Read(new ReadRequest { Stream = "$all", Start = new(), Subscription = new SubscriptionOptions() }, cancellationToken: TestContext.Current.CancellationToken);

        // Act
        var messages = await ReadThroughCaughtUpAsync(call);

        // Assert
        messages[0].Confirmed.Head.ShouldBe(head);
        messages.Count(message => message.ContentCase == ReadResponse.ContentOneofCase.FellBehind).ShouldBe(expected ? 1 : 0);
        if (expected)
        {
            messages[1].FellBehind.ShouldSatisfyAllConditions(
                behind => behind.Head.ShouldBe(700),
                behind => behind.EventsBehind.ShouldBe(188));
        }

        messages.Where(message => message.ContentCase == ReadResponse.ContentOneofCase.Event).Select(message => message.Event.Position)
            .ShouldBe(Enumerable.Range(1, (int)head).Select(position => (long)position));
        messages[^1].CaughtUp.Head.ShouldBe(head);
    }

    [Theory]
    [InlineData(40, true)]
    [InlineData(0, false)]
    public async Task Read_WhenSubscribingToACategoryOverAFullPage_ShouldSayItFellBehindOnlyWhenTheCategoryHasMore(int more, bool expected)
    {
        // Arrange: a first page of 512 of the category's events below a head of 700, and how many
        // more of its own lie between the page's last and the head, which only a count tells.
        var orders = new VirtualStreamName(VirtualStreamKind.Category, "orders");
        _tail.Advance(700);
        A.CallTo(() => _store.VirtualHeadAsync(orders, 700, A<CancellationToken>._)).Returns(new StreamHead(1, 512 + more));
        A.CallTo(() => _store.ReadVirtualAsync(orders, Direction.Forwards, 0, 700, StreamsService.PageSize, A<CancellationToken>._))
            .Returns(Records("orders-1", 1, 512));
        A.CallTo(() => _store.CountVirtualAsync(orders, 512, 700, A<CancellationToken>._)).Returns(more);
        A.CallTo(() => _store.ReadVirtualAsync(orders, Direction.Forwards, 513, 700, StreamsService.PageSize, A<CancellationToken>._))
            .Returns(Records("orders-2", 513, more));
        var client = new Streams.StreamsClient(_channel);
        using var call = client.Read(new ReadRequest { Stream = "$ce-orders", Start = new(), Subscription = new SubscriptionOptions() }, cancellationToken: TestContext.Current.CancellationToken);

        // Act
        var messages = await ReadThroughCaughtUpAsync(call);

        // Assert: the head it names is the category's own, and the count is of the category's events.
        messages[0].Confirmed.Head.ShouldBe(512 + more);
        messages.Count(message => message.ContentCase == ReadResponse.ContentOneofCase.FellBehind).ShouldBe(expected ? 1 : 0);
        if (expected)
        {
            messages[1].FellBehind.ShouldSatisfyAllConditions(
                behind => behind.Head.ShouldBe(552),
                behind => behind.EventsBehind.ShouldBe(552));
        }

        messages.Count(message => message.ContentCase == ReadResponse.ContentOneofCase.Event).ShouldBe(512 + more);
        messages[^1].CaughtUp.Head.ShouldBe(512 + more);
    }

    [Theory]
    [InlineData(599, true)]
    [InlineData(511, false)]
    public async Task Read_WhenSubscribingByOrdinalOverAFullPage_ShouldSayItFellBehindOnlyWhenOrdinalsGoOn(long last, bool expected)
    {
        // Arrange: a first page of ordinals 0 to 511; with the last ordinal at 599 there are 88 more.
        var orders = new VirtualStreamName(VirtualStreamKind.Category, "orders");
        _tail.Advance(700);
        _tail.Number(700);
        A.CallTo(() => _store.OrdinalsEnabled).Returns(true);
        A.CallTo(() => _store.OrdinalHeadAsync(orders, A<CancellationToken>._)).Returns(new StreamHead(0, last));
        A.CallTo(() => _store.ReadByOrdinalAsync(orders, Direction.Forwards, 0, StreamsService.PageSize, A<CancellationToken>._))
            .Returns(NumberedFrom(0, 512));
        A.CallTo(() => _store.ReadByOrdinalAsync(orders, Direction.Forwards, 512, StreamsService.PageSize, A<CancellationToken>._))
            .Returns(NumberedFrom(512, (int)last - 511));
        var client = new Streams.StreamsClient(_channel);
        using var call = client.Read(new ReadRequest { Stream = "$ce-orders", Start = new(), Subscription = new SubscriptionOptions(), Numbering = Protocols.Grpc.V1.Numbering.Ordinal }, cancellationToken: TestContext.Current.CancellationToken);

        // Act
        var messages = await ReadThroughCaughtUpAsync(call);

        // Assert: ordinals are dense, so the distance to the last one is the count.
        messages[0].Confirmed.Head.ShouldBe(last);
        messages.Count(message => message.ContentCase == ReadResponse.ContentOneofCase.FellBehind).ShouldBe(expected ? 1 : 0);
        if (expected)
        {
            messages[1].FellBehind.ShouldSatisfyAllConditions(
                behind => behind.Head.ShouldBe(599),
                behind => behind.EventsBehind.ShouldBe(600));
        }

        messages.Where(message => message.ContentCase == ReadResponse.ContentOneofCase.Event).Select(message => message.Event.Ordinal)
            .ShouldBe(Enumerable.Range(0, (int)last + 1).Select(ordinal => (long)ordinal));
        messages[^1].CaughtUp.Head.ShouldBe(last);
    }

    [Fact]
    public async Task Read_WhenSubscribingByOrdinalFromAnOrdinal_ShouldStartThere()
    {
        // Arrange
        var orders = new VirtualStreamName(VirtualStreamKind.Category, "orders");
        _tail.Advance(20);
        _tail.Number(20);
        A.CallTo(() => _store.OrdinalsEnabled).Returns(true);
        A.CallTo(() => _store.OrdinalHeadAsync(orders, A<CancellationToken>._)).Returns(new StreamHead(0, 6));
        A.CallTo(() => _store.ReadByOrdinalAsync(orders, Direction.Forwards, 5, StreamsService.PageSize, A<CancellationToken>._))
            .Returns([Numbered("orders-1", 3, 15, 5), Numbered("orders-2", 0, 17, 6)]);
        var client = new Streams.StreamsClient(_channel);
        using var call = client.Read(new ReadRequest { Stream = "$ce-orders", Position = 5, Subscription = new SubscriptionOptions(), Numbering = Protocols.Grpc.V1.Numbering.Ordinal }, cancellationToken: TestContext.Current.CancellationToken);

        // Act
        var messages = await ReadThroughCaughtUpAsync(call);

        // Assert
        messages[0].Confirmed.Head.ShouldBe(6);
        messages.Skip(1).Take(2).Select(message => message.Event.Ordinal).ShouldBe([5, 6]);
        messages[3].CaughtUp.Head.ShouldBe(6);
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(100, false)]
    public async Task Read_WhenSubscribingToAllWithAFilter_ShouldDeliverWhatTheCatchUpKeepsAndCheckpointTheQuietStretchAfterIt(uint interval, bool checkpointed)
    {
        // Arrange: of the first hundred positions the filter keeps the one at 40, and sixty quiet
        // positions follow it: a checkpoint under an interval of two, none under one of a hundred.
        _tail.Advance(100);
        A.CallTo(() => _store.ScanAllAsync(A<EventFilter>._, Direction.Forwards, 0, 100, StreamsService.ScanWindow, StreamsService.PageSize, A<CancellationToken>._))
            .Returns(new ScannedSlice([Record("orders-1", 0, 40, "order_placed")], 100));
        var client = new Streams.StreamsClient(_channel);
        using var call = client.Read(
            new ReadRequest { Stream = "$all", Start = new(), Subscription = new SubscriptionOptions(), Filter = new Filter { EventType = new Expression { Prefix = { "order" } }, CheckpointInterval = interval } },
            cancellationToken: TestContext.Current.CancellationToken);

        // Act
        var messages = await ReadThroughCaughtUpAsync(call);

        // Assert
        messages[1].Event.Position.ShouldBe(40);
        messages.Where(message => message.ContentCase == ReadResponse.ContentOneofCase.Checkpoint).Select(message => message.Checkpoint.Position).ShouldBe(checkpointed ? [100] : []);
        messages[^1].CaughtUp.Head.ShouldBe(100);
    }

    [Fact]
    public async Task Read_WhenTheRouterBringsAnEventTheCatchUpHasDelivered_ShouldNotDeliverItTwice()
    {
        // Arrange: revision 1 is appended while the catch-up reads, so the read returns it and the
        // router brings it too. The router may see the two advances as one, so that range is set up as well.
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Forwards, 0, StreamsService.PageSize, A<CancellationToken>._))
            .ReturnsLazily(_ =>
            {
                _tail.Advance(15);
                return Task.FromResult<StreamSlice?>(new StreamSlice(new StreamHead(0, 1), [Record("orders-1", 0, 10, "a"), Record("orders-1", 1, 15, "b")]));
            });
        A.CallTo(() => _store.ReadPointersAsync(1, 15, StreamRouter.PointerPage, A<CancellationToken>._))
            .Returns([new EventPointer("orders-1", 1, 15, "b")]);
        A.CallTo(() => _store.ReadPointersAsync(16, 20, StreamRouter.PointerPage, A<CancellationToken>._))
            .Returns([new EventPointer("orders-1", 2, 20, "c")]);
        A.CallTo(() => _store.ReadPointersAsync(1, 20, StreamRouter.PointerPage, A<CancellationToken>._))
            .Returns([new EventPointer("orders-1", 1, 15, "b"), new EventPointer("orders-1", 2, 20, "c")]);
        A.CallTo(() => _store.ReadAsStoredAsync("orders-1", 1, 1, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 1), [Record("orders-1", 1, 15, "b")]));
        A.CallTo(() => _store.ReadAsStoredAsync("orders-1", 2, 1, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 2), [Record("orders-1", 2, 20, "c")]));
        A.CallTo(() => _store.ReadAsStoredAsync("orders-1", 1, 2, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 2), [Record("orders-1", 1, 15, "b"), Record("orders-1", 2, 20, "c")]));
        var client = new Streams.StreamsClient(_channel);
        using var call = client.Read(new ReadRequest { Stream = "orders-1", Start = new(), Subscription = new SubscriptionOptions() }, cancellationToken: TestContext.Current.CancellationToken);

        // Act
        var catchUp = await ReadThroughCaughtUpAsync(call);
        _tail.Advance(20);
        var live = await ReadThroughCaughtUpAsync(call);

        // Assert
        catchUp.Skip(1).Take(2).Select(message => message.Event.Revision).ShouldBe([0, 1]);
        catchUp[3].CaughtUp.Head.ShouldBe(1);
        live[0].Event.Revision.ShouldBe(2);
        live[1].CaughtUp.Head.ShouldBe(2);
    }

    [Fact]
    public async Task Read_WhenTheStreamIsDeletedWhileItIsFollowed_ShouldEndTheSubscriptionSayingSo()
    {
        // Arrange: the router is pointed at a new event of the stream and finds the stream deleted.
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Forwards, 0, StreamsService.PageSize, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 0), [Record("orders-1", 0, 10, "a")]));
        A.CallTo(() => _store.ReadPointersAsync(1, 15, StreamRouter.PointerPage, A<CancellationToken>._))
            .Returns([new EventPointer("orders-1", 1, 15, "b")]);
        A.CallTo(() => _store.ReadAsStoredAsync("orders-1", 1, 1, A<CancellationToken>._)).ThrowsAsync(new StreamDeletedException("orders-1"));
        var client = new Streams.StreamsClient(_channel);
        using var call = client.Read(new ReadRequest { Stream = "orders-1", Start = new(), Subscription = new SubscriptionOptions() }, cancellationToken: TestContext.Current.CancellationToken);
        await ReadThroughCaughtUpAsync(call);

        // Act
        _tail.Advance(15);
        var refusal = await Should.ThrowAsync<RpcException>(() => Next(call, 1));

        // Assert
        ReasonOf(refusal).ShouldBe("STREAM_DELETED");
    }

    [Fact]
    public async Task Read_WhenTheSequencerStopsWhileAnOrdinalSubscriptionWaitsOnIt_ShouldEndTheSubscriptionWithTheReason()
    {
        // Arrange: caught up at ordinal 1 with everything numbered. The store then advances past
        // what is numbered, so the subscription waits on the sequencer, which has faulted.
        var orders = new VirtualStreamName(VirtualStreamKind.Category, "orders");
        _tail.Advance(20);
        _tail.Number(20);
        A.CallTo(() => _store.OrdinalsEnabled).Returns(true);
        A.CallTo(() => _store.OrdinalHeadAsync(orders, A<CancellationToken>._)).Returns(new StreamHead(0, 1));
        A.CallTo(() => _store.ReadByOrdinalAsync(orders, Direction.Forwards, 0, StreamsService.PageSize, A<CancellationToken>._))
            .Returns([Numbered("orders-1", 0, 5, 0), Numbered("orders-1", 1, 17, 1)]);
        A.CallTo(() => _store.ReadByOrdinalAsync(orders, Direction.Forwards, 2, StreamsService.PageSize, A<CancellationToken>._)).Returns([]);
        var client = new Streams.StreamsClient(_channel);
        using var call = client.Read(new ReadRequest { Stream = "$ce-orders", Start = new(), Subscription = new SubscriptionOptions(), Numbering = Protocols.Grpc.V1.Numbering.Ordinal }, cancellationToken: TestContext.Current.CancellationToken);
        await ReadThroughCaughtUpAsync(call);

        // Act
        _tail.NumberingStopped = new OrdinalSequencerFaultedException("the batch timed out");
        _tail.Advance(30);
        var refusal = await Should.ThrowAsync<RpcException>(() => Next(call, 1));

        // Assert: a fault is worth a retry after a back-off.
        refusal.StatusCode.ShouldBe(StatusCode.Unavailable);
        var info = refusal.GetRpcStatus()?.GetDetail<ErrorInfo>();
        info?.Reason.ShouldBe("SEQUENCER_FAULTED");
        info?.Metadata["cause"].ShouldBe("the batch timed out");
    }

    /// <summary>
    /// Reads a subscription through the next message saying it is caught up. A catch-up and a
    /// batch of live events both end with that, so a test reading this way fails at once on what
    /// it got when fewer messages come, where one waiting for a number of them waits out its patience.
    /// </summary>
    private static async Task<List<ReadResponse>> ReadThroughCaughtUpAsync(AsyncServerStreamingCall<ReadResponse> call)
    {
        var messages = new List<ReadResponse>();
        while (await call.ResponseStream.MoveNextOrFailAsync(messages))
        {
            messages.Add(call.ResponseStream.Current);
            if (call.ResponseStream.Current.ContentCase == ReadResponse.ContentOneofCase.CaughtUp)
            {
                break;
            }
        }

        return messages;
    }

    /// <summary>Makes a run of one stream's events, each at the position its revision has.</summary>
    private static List<EventRecord> Records(string stream, int from, int count) =>
        [.. Enumerable.Range(from, count).Select(number => Record(stream, number, number, "a"))];

    /// <summary>Makes a run of numbered events, each at the position its ordinal has plus one.</summary>
    private static List<EventRecord> NumberedFrom(int from, int count) =>
        [.. Enumerable.Range(from, count).Select(ordinal => Numbered("orders-1", ordinal, ordinal + 1, ordinal))];
}
