using Dracocephalum.Nightingale.Protocols.Grpc.V1;
using FakeItEasy;
using Google.Protobuf;
using Google.Rpc;
using Grpc.Core;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Tests;

/// <content>What the streams service refuses before the store is asked, what it makes of the store's own refusals, and where a bounded read starts and stops.</content>
public sealed partial class StreamsServiceTests
{
    [Fact]
    public async Task Read_WhenThePositionIsNegative_ShouldRefuseItBeforeTheStore()
    {
        // Arrange
        var client = new Streams.StreamsClient(_channel);

        // Act
        var refusal = await Should.ThrowAsync<RpcException>(() => ReadAll(client, new ReadRequest { Stream = "orders-1", Position = -1, Count = 1 }));

        // Assert
        ReasonOf(refusal).ShouldBe("INVALID_ARGUMENT");
        refusal.Status.Detail.ShouldContain("never negative");
    }

    [Fact]
    public async Task Read_WhenItIsNeitherACountNorASubscription_ShouldRefuseIt()
    {
        // Arrange
        var client = new Streams.StreamsClient(_channel);

        // Act
        var refusal = await Should.ThrowAsync<RpcException>(() => ReadAll(client, new ReadRequest { Stream = "orders-1", Start = new(), Count = 0 }));

        // Assert
        ReasonOf(refusal).ShouldBe("INVALID_ARGUMENT");
        refusal.Status.Detail.ShouldContain("positive count");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(251)]
    public async Task Read_WhenTheStreamNameIsEmptyOrTooLong_ShouldRejectTheName(int length)
    {
        // Arrange
        var client = new Streams.StreamsClient(_channel);

        // Act
        var refusal = await Should.ThrowAsync<RpcException>(() => ReadAll(client, new ReadRequest { Stream = new string('a', length), Start = new(), Count = 1 }));

        // Assert
        ReasonOf(refusal).ShouldBe("INVALID_STREAM_NAME");
    }

    [Fact]
    public async Task Read_WhenTheStoreSaysTheStreamIsDeleted_ShouldAnswerThatItIs()
    {
        // Arrange
        A.CallTo(() => _store.ReadEventualAsync("orders-1", Direction.Forwards, null, 5, A<CancellationToken>._)).ThrowsAsync(new StreamDeletedException("orders-1"));
        var client = new Streams.StreamsClient(_channel);

        // Act
        var refusal = await Should.ThrowAsync<RpcException>(() => ReadAll(client, new ReadRequest { Stream = "orders-1", Start = new(), Count = 5 }));

        // Assert
        refusal.StatusCode.ShouldBe(StatusCode.FailedPrecondition);
        ReasonOf(refusal).ShouldBe("STREAM_DELETED");
    }

    [Fact]
    public async Task Read_WhenFromARevision_ShouldAskTheStoreForItAndStopWhenThePageComesShort()
    {
        // Arrange
        A.CallTo(() => _store.ReadEventualAsync("orders-1", Direction.Forwards, 3, 5, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 4), [Record("orders-1", 3, 13, "a"), Record("orders-1", 4, 14, "b")]));
        var client = new Streams.StreamsClient(_channel);

        // Act
        var messages = await ReadAll(client, new ReadRequest { Stream = "orders-1", Position = 3, Count = 5 });

        // Assert
        messages.Skip(1).Select(message => message.Event.Revision).ShouldBe([3, 4]);
    }

    [Fact]
    public async Task Read_WhenReadingAllFromAPosition_ShouldStartThereAndStopWhenThePageComesShort()
    {
        // Arrange
        _tail.Advance(20);
        A.CallTo(() => _store.ReadAllAsync(Direction.Forwards, 6, 20, 5, A<CancellationToken>._))
            .Returns([Record("orders-2", 0, 7, "b"), Record("orders-1", 1, 9, "c")]);
        var client = new Streams.StreamsClient(_channel);

        // Act
        var messages = await ReadAll(client, new ReadRequest { Stream = "$all", Position = 6, Count = 5 });

        // Assert
        messages.Skip(1).Select(message => message.Event.Position).ShouldBe([7, 9]);
    }

    [Fact]
    public async Task Read_WhenReadingACategoryBackwardsFromTheEnd_ShouldStartAtItsLastEvent()
    {
        // Arrange
        var orders = new VirtualStreamName(VirtualStreamKind.Category, "orders");
        _tail.Advance(20);
        A.CallTo(() => _store.VirtualHeadAsync(orders, 20, A<CancellationToken>._)).Returns(new StreamHead(5, 17));
        A.CallTo(() => _store.ReadVirtualAsync(orders, Direction.Backwards, 17, 20, 10, A<CancellationToken>._))
            .Returns([Record("orders-1", 1, 17, "c"), Record("orders-2", 0, 9, "b")]);
        var client = new Streams.StreamsClient(_channel);

        // Act
        var messages = await ReadAll(client, new ReadRequest { Stream = "$ce-orders", Direction = ReadDirection.Backwards, End = new(), Count = 10 });

        // Assert
        messages.Skip(1).Select(message => message.Event.Position).ShouldBe([17, 9]);
    }

    [Fact]
    public async Task Read_WhenReadingACategoryFromBeyondItsLastEvent_ShouldSendItsBoundsAndReadNothing()
    {
        // Arrange: the strict store proves no page is asked for.
        var orders = new VirtualStreamName(VirtualStreamKind.Category, "orders");
        _tail.Advance(20);
        A.CallTo(() => _store.VirtualHeadAsync(orders, 20, A<CancellationToken>._)).Returns(new StreamHead(5, 17));
        var client = new Streams.StreamsClient(_channel);

        // Act
        var messages = await ReadAll(client, new ReadRequest { Stream = "$ce-orders", Position = 18, Count = 10 });

        // Assert
        messages.ShouldHaveSingleItem().Head.Last.ShouldBe(17);
    }

    [Fact]
    public async Task Read_WhenReadingByOrdinalAStreamNothingIsNumberedIn_ShouldSendZeroBoundsOnly()
    {
        // Arrange
        var orders = new VirtualStreamName(VirtualStreamKind.Category, "orders");
        A.CallTo(() => _store.OrdinalsEnabled).Returns(true);
        A.CallTo(() => _store.OrdinalHeadAsync(orders, A<CancellationToken>._)).Returns((StreamHead?)null);
        var client = new Streams.StreamsClient(_channel);

        // Act
        var messages = await ReadAll(client, new ReadRequest { Stream = "$ce-orders", Start = new(), Count = 10, Numbering = Protocols.Grpc.V1.Numbering.Ordinal });

        // Assert
        var head = messages.ShouldHaveSingleItem().Head;
        (head.First, head.Last).ShouldBe((0L, 0L));
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(3, 0)]
    public async Task Read_WhenReadingByOrdinalFromANumber_ShouldStartThereOrReadNothingBeyondTheLast(long from, int delivered)
    {
        // Arrange: ordinals 0 to 2 are assigned; a read from beyond them asks the store for nothing.
        var orders = new VirtualStreamName(VirtualStreamKind.Category, "orders");
        A.CallTo(() => _store.OrdinalsEnabled).Returns(true);
        A.CallTo(() => _store.OrdinalHeadAsync(orders, A<CancellationToken>._)).Returns(new StreamHead(0, 2));
        A.CallTo(() => _store.ReadByOrdinalAsync(orders, Direction.Forwards, 1, 10, A<CancellationToken>._))
            .Returns([Numbered("orders-2", 0, 9, 1), Numbered("orders-1", 1, 17, 2)]);
        var client = new Streams.StreamsClient(_channel);

        // Act
        var messages = await ReadAll(client, new ReadRequest { Stream = "$ce-orders", Position = from, Count = 10, Numbering = Protocols.Grpc.V1.Numbering.Ordinal });

        // Assert
        messages.Count.ShouldBe(1 + delivered);
        messages.Skip(1).Select(message => message.Event.Ordinal).ShouldBe(delivered == 0 ? [] : [1L, 2L]);
    }

    [Fact]
    public async Task Append_WhenAMessageAfterTheOptionsIsNotAnEvent_ShouldRefuseItBeforeTheStore()
    {
        // Arrange
        var client = new Streams.StreamsClient(_channel);
        using var call = client.Append(cancellationToken: TestContext.Current.CancellationToken);
        await call.RequestStream.WriteAsync(new AppendRequest { Options = new AppendOptions { Stream = "orders-1", ExpectedRevision = -2 } }, TestContext.Current.CancellationToken);

        // Act
        var refusal = await Should.ThrowAsync<RpcException>(async () =>
        {
            await WriteLastAsync(call.RequestStream, new AppendRequest { Options = new AppendOptions { Stream = "orders-2", ExpectedRevision = -2 } });
            await call.ResponseAsync;
        });

        // Assert
        ReasonOf(refusal).ShouldBe("INVALID_ARGUMENT");
        refusal.Status.Detail.ShouldContain("carries one event");
    }

    [Fact]
    public async Task Append_WhenItCarriesNoEvent_ShouldRefuseItBeforeTheStore()
    {
        // Arrange
        var client = new Streams.StreamsClient(_channel);

        // Act
        var refusal = await Should.ThrowAsync<RpcException>(() => AppendAsync(client, "orders-1", -2));

        // Assert
        ReasonOf(refusal).ShouldBe("INVALID_ARGUMENT");
        refusal.Status.Detail.ShouldContain("at least one event");
    }

    [Fact]
    public async Task Append_WhenTheStoreSaysTheStreamIsDeleted_ShouldAnswerThatItIs()
    {
        // Arrange
        A.CallTo(() => _store.AppendAsync("orders-1", StreamState.Any, A<IReadOnlyList<EventData>>._, A<CancellationToken>._)).ThrowsAsync(new StreamDeletedException("orders-1"));
        var client = new Streams.StreamsClient(_channel);

        // Act
        var refusal = await Should.ThrowAsync<RpcException>(() => AppendAsync(client, "orders-1", -2, Proposed(Guid.NewGuid(), "order_paid", "{}")));

        // Assert
        ReasonOf(refusal).ShouldBe("STREAM_DELETED");
    }

    [Theory]
    [InlineData("0198f3a2-0000-7000-8000-000000000042", "", "{}", "needs a type")]
    [InlineData("not-an-id", "order_paid", "{}", "a UUID in its canonical form")]
    [InlineData("0198f3a2-0000-7000-8000-000000000042", "order_paid", "[1]", "Event metadata is a JSON object")]
    [InlineData("0198f3a2-0000-7000-8000-000000000042", "order_paid", "{", "Event metadata is a JSON object")]
    public async Task Append_WhenAnEventIsMalformed_ShouldRefuseItBeforeTheStore(string id, string type, string metadata, string why)
    {
        // Arrange
        var client = new Streams.StreamsClient(_channel);
        var proposed = new ProposedEvent { Id = id, EventType = type, Data = ByteString.CopyFromUtf8("{}"), Metadata = ByteString.CopyFromUtf8(metadata) };

        // Act
        var refusal = await Should.ThrowAsync<RpcException>(() => AppendAsync(client, "orders-1", -2, proposed));

        // Assert
        ReasonOf(refusal).ShouldBe("INVALID_ARGUMENT");
        refusal.Status.Detail.ShouldContain(why);
    }

    [Fact]
    public async Task Append_WhenTheExpectedRevisionIsNeitherARevisionNorANamedState_ShouldRefuseIt()
    {
        // Arrange
        var client = new Streams.StreamsClient(_channel);

        // Act
        var refusal = await Should.ThrowAsync<RpcException>(() => AppendAsync(client, "orders-1", -9, Proposed(Guid.NewGuid(), "order_paid", "{}")));

        // Assert
        ReasonOf(refusal).ShouldBe("INVALID_ARGUMENT");
        refusal.Status.Detail.ShouldContain("neither a revision nor a named state");
    }

    [Theory]
    [InlineData("deleted", "STREAM_DELETED")]
    [InlineData("too many", "APPEND_SIZE_EXCEEDED")]
    [InlineData("stream name", "INVALID_ARGUMENT")]
    [InlineData("event type", "INVALID_ARGUMENT")]
    public async Task AppendToStreams_WhenTheStoreRefuses_ShouldAnswerTheContractsError(string why, string reason)
    {
        // Arrange
        Exception refused = why switch
        {
            "deleted" => new StreamDeletedException("orders-1"),
            "too many" => new AppendSizeExceededException("orders-1", 1),
            _ => new ValueTooLongException(why),
        };
        A.CallTo(() => _store.AppendToStreamsAsync(A<IReadOnlyList<StreamAppend>>._, A<IReadOnlyList<StreamCheck>>._, A<CancellationToken>._)).ThrowsAsync(refused);
        var client = new Streams.StreamsClient(_channel);

        // Act
        var refusal = await Should.ThrowAsync<RpcException>(() => AppendToStreamsAsync(
            client,
            new AppendToStreamsRequest { Stream = new AppendStreamHeader { Stream = "orders-1", ExpectedRevision = -2 } },
            new AppendToStreamsRequest { Event = Proposed(Guid.NewGuid(), "order_paid", "{}") }));

        // Assert
        ReasonOf(refusal).ShouldBe(reason);
    }

    [Fact]
    public async Task AppendToStreams_WhenAMessageCarriesNothing_ShouldRefuseItBeforeTheStore()
    {
        // Arrange
        var client = new Streams.StreamsClient(_channel);

        // Act
        var refusal = await Should.ThrowAsync<RpcException>(() => AppendToStreamsAsync(client, new AppendToStreamsRequest()));

        // Assert
        ReasonOf(refusal).ShouldBe("INVALID_ARGUMENT");
        refusal.Status.Detail.ShouldContain("a stream's header, an event, a metadata set or a check");
    }

    [Fact]
    public async Task AppendToStreams_WithMoreChecksThanOneAppendMayCarry_ShouldRefuseThemBeforeTheStore()
    {
        // Arrange
        _serverOptions.MaxEventsPerAppend = 1;
        var client = new Streams.StreamsClient(_channel);

        // Act
        var refusal = await Should.ThrowAsync<RpcException>(() => AppendToStreamsAsync(
            client,
            new AppendToStreamsRequest { Stream = new AppendStreamHeader { Stream = "orders-1", ExpectedRevision = -2 } },
            new AppendToStreamsRequest { Event = Proposed(Guid.NewGuid(), "order_paid", "{}") },
            new AppendToStreamsRequest { Check = new AppendCheck { Stream = "ledger-1", ExpectedRevision = -1 } },
            new AppendToStreamsRequest { Check = new AppendCheck { Stream = "ledger-2", ExpectedRevision = -1 } }));

        // Assert
        ReasonOf(refusal).ShouldBe("INVALID_ARGUMENT");
        refusal.Status.Detail.ShouldContain("at most 1 checks");
    }

    [Fact]
    public async Task Delete_WhenAllowed_ShouldPassTheExpectedStateToTheStore()
    {
        // Arrange
        _serverOptions.Deletion.Allowed = DeletionLevel.Delete;
        A.CallTo(() => _store.DeleteAsync("orders-1", StreamState.StreamExists, A<CancellationToken>._)).Returns(Task.CompletedTask);
        var client = new Streams.StreamsClient(_channel);

        // Act
        await client.DeleteAsync(new DeleteRequest { Stream = "orders-1", ExpectedRevision = StreamState.StreamExists.ToInt64() }, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        A.CallTo(() => _store.DeleteAsync("orders-1", StreamState.StreamExists, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task SetStreamMetadata_WhenTheCustomKeysAreNotJson_ShouldAnswerInvalidArgumentWithoutTouchingTheStore()
    {
        // Arrange
        var client = new Streams.StreamsClient(_channel);
        var request = new SetStreamMetadataRequest { Stream = "orders-1", ExpectedRevision = -1, Metadata = new Protocols.Grpc.V1.StreamMetadata { Custom = ByteString.CopyFromUtf8("{") } };

        // Act
        var refusal = await Should.ThrowAsync<RpcException>(async () => await client.SetStreamMetadataAsync(request, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        ReasonOf(refusal).ShouldBe("INVALID_ARGUMENT");
        refusal.Status.Detail.ShouldContain("Custom stream metadata is a JSON object");
    }

    private static string? ReasonOf(RpcException refusal) => refusal.GetRpcStatus()?.GetDetail<ErrorInfo>()?.Reason;

    /// <summary>
    /// Writes the last request of a call the server is expected to refuse, and completes the requests.
    /// A server that has ended the call by then fails the write or the completion too, with a status
    /// and without the details, so that failure is left for the response to give in full.
    /// </summary>
    private static async Task WriteLastAsync<TRequest>(IClientStreamWriter<TRequest> requests, TRequest last)
    {
        try
        {
            await requests.WriteAsync(last, TestContext.Current.CancellationToken);
            await requests.CompleteAsync();
        }
        catch (RpcException)
        {
            // The response says why.
        }
    }
}
