using Dracocephalum.Nightingale.Protocol.V1;
using FakeItEasy;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Shouldly;

namespace Dracocephalum.Nightingale.Client.Tests;

/// <summary>
/// The consumer side of a persistent subscription over a scripted duplex call: what the pump
/// yields and what the acknowledgements send.
/// </summary>
public sealed class PersistentSubscriptionTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 21, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Subscribing_ShouldSendTheOptionsFirstThenYieldTheConfirmationAndEventsWithRetryCounts()
    {
        // Arrange
        var (invoker, sent) = DuplexRead(
        [
            new PersistentReadResponse { Confirmed = new PersistentSubscriptionConfirmed { SubscriptionId = "p-1", Checkpoint = 4 } },
            new PersistentReadResponse { Event = new PersistentEvent { Event = Recorded("orders-1", 5, 50), RetryCount = 0 } },
            new PersistentReadResponse { Event = new PersistentEvent { Event = Recorded("orders-1", 6, 60), RetryCount = 2 } },
        ]);
        await using var sut = new NightingaleClient(invoker);

        // Act
        await using var subscription = await sut.SubscribeToPersistentSubscriptionAsync("orders-1", "billing", 7, TestContext.Current.CancellationToken);
        var delivered = new List<PersistentSubscriptionMessage.Recorded>();
        await foreach (var message in subscription.WithCancellation(TestContext.Current.CancellationToken))
        {
            delivered.Add(message);
            await subscription.AckAsync(message.Record.Id);
        }

        // Assert
        (await subscription.Confirmed).ShouldBe(new PersistentSubscriptionMessage.Confirmed("p-1", 4));
        delivered.Select(message => (message.Record.Revision, message.RetryCount)).ShouldBe([(5, 0), (6, 2)]);
        sent[0].Options.ShouldSatisfyAllConditions(options => options.Stream.ShouldBe("orders-1"), options => options.Group.ShouldBe("billing"), options => options.BufferSize.ShouldBe(7));
        sent.Skip(1).Select(request => request.Ack.Ids.Single()).ShouldBe(delivered.Select(message => message.Record.Id.ToString("D")));
    }

    [Fact]
    public async Task DisposeAsync_ShouldCompleteTheRequestsAndWaitForTheServerToEndTheCallDroppingWhatIsStillOnTheWay()
    {
        // Arrange: the server has three events on the way; the consumer takes one and leaves.
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (invoker, sent, writer) = DuplexRead(
            Trickle(
            [
                new PersistentReadResponse { Confirmed = new PersistentSubscriptionConfirmed { SubscriptionId = "p-1", Checkpoint = -1 } },
                new PersistentReadResponse { Event = new PersistentEvent { Event = Recorded("orders-1", 5, 50) } },
                new PersistentReadResponse { Event = new PersistentEvent { Event = Recorded("orders-1", 6, 60) } },
                new PersistentReadResponse { Event = new PersistentEvent { Event = Recorded("orders-1", 7, 70) } },
            ],
            ended.Task));
        writer.Completed = ended.SetResult;
        await using var sut = new NightingaleClient(invoker);
        var subscription = await sut.SubscribeToPersistentSubscriptionAsync("orders-1", "billing", 7, TestContext.Current.CancellationToken);
        var first = await subscription.Messages.OfType<PersistentSubscriptionMessage.Recorded>().FirstAsync(TestContext.Current.CancellationToken);

        // Act
        await subscription.DisposeAsync();

        // Assert: the requests were completed, which is what ended the call; the two events still on the way went nowhere.
        first.Record.Revision.ShouldBe(5);
        sent.Count.ShouldBe(1, "only the options were sent");
        ended.Task.IsCompleted.ShouldBeTrue();
    }

    [Fact]
    public async Task NackAsync_ShouldSendTheActionTheReasonAndTheIds()
    {
        // Arrange
        var (invoker, sent) = DuplexRead([new PersistentReadResponse { Confirmed = new PersistentSubscriptionConfirmed { SubscriptionId = "p-1", Checkpoint = -1 } }]);
        await using var sut = new NightingaleClient(invoker);
        await using var subscription = await sut.SubscribeToPersistentSubscriptionAsync("orders-1", "billing", cancellationToken: TestContext.Current.CancellationToken);
        var id = Guid.NewGuid();

        // Act
        await subscription.NackAsync(NackAction.Park, "poison", id);

        // Assert
        var nack = sent.Last().Nack;
        nack.Action.ShouldBe(Protocol.V1.NackAction.Park);
        nack.Reason.ShouldBe("poison");
        nack.Ids.ShouldBe([id.ToString("D")]);
    }

    [Fact]
    public async Task Subscribe_WhenTheGroupIsOwnedElsewhereWithoutAnAddress_ShouldFailNamingTheOwner()
    {
        // Arrange: the owner advertised no address, so there is nowhere to go.
        var (invoker, _) = DuplexRead([], Failure(StatusCode.Unavailable, "GROUP_OWNED_ELSEWHERE", ("stream", "orders-1"), ("group", "billing"), ("owner", "node-2"), ("address", string.Empty)));
        await using var sut = new NightingaleClient(invoker);

        // Act
        var exception = await Should.ThrowAsync<GroupOwnedElsewhereException>(() => sut.SubscribeToPersistentSubscriptionAsync("orders-1", "billing", cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        exception.Owner.ShouldBe("node-2");
        exception.Group.ShouldBe("billing");
        exception.Address.ShouldBeNull();
    }

    [Fact]
    public async Task Subscribe_WhenTheGroupIsOwnedElsewhereWithAnAddress_ShouldGoThereOnce()
    {
        // Arrange: the first instance refuses and names the owner's address; the owner confirms.
        var (front, _) = DuplexRead([], Failure(StatusCode.Unavailable, "GROUP_OWNED_ELSEWHERE", ("stream", "orders-1"), ("group", "billing"), ("owner", "node-2"), ("address", "http://node-2:5000/")));
        var (owner, ownerRequests) = DuplexRead([new PersistentReadResponse { Confirmed = new PersistentSubscriptionConfirmed { SubscriptionId = "s-2", Checkpoint = 4 } }]);
        var redirectedTo = new List<Uri>();
        await using var sut = new NightingaleClient(front, address =>
        {
            redirectedTo.Add(address);
            return owner;
        });

        // Act
        await using var subscription = await sut.SubscribeToPersistentSubscriptionAsync("orders-1", "billing", cancellationToken: TestContext.Current.CancellationToken);
        var confirmed = await subscription.Confirmed;

        // Assert
        confirmed.SubscriptionId.ShouldBe("s-2");
        confirmed.Checkpoint.ShouldBe(4);
        redirectedTo.ShouldBe([new Uri("http://node-2:5000/")]);
        ownerRequests.ShouldHaveSingleItem().Options.Group.ShouldBe("billing");
    }

    private static RecordedEvent Recorded(string stream, long revision, long position) => new()
    {
        Id = Guid.NewGuid().ToString("D"),
        Stream = stream,
        Revision = revision,
        Position = position,
        EventType = "order_placed",
        Created = Timestamp.FromDateTimeOffset(At),
        Data = ByteString.CopyFromUtf8("{}"),
        Metadata = ByteString.Empty,
    };

    private static RpcException Failure(StatusCode code, string reason, params (string Key, string Value)[] metadata)
    {
        var info = new Google.Rpc.ErrorInfo { Domain = "nightingale", Reason = reason };
        foreach (var (key, value) in metadata)
        {
            info.Metadata[key] = value;
        }

        var status = new Google.Rpc.Status { Code = (int)code, Message = reason, Details = { Any.Pack(info) } };
        return status.ToRpcException();
    }

    /// <summary>A duplex call whose responses are scripted and whose requests are collected.</summary>
    private static (CallInvoker Invoker, List<PersistentReadRequest> Sent) DuplexRead(IEnumerable<PersistentReadResponse> responses, RpcException? failure = null)
    {
        var (invoker, sent, _) = DuplexRead(responses.ToAsyncEnumerable(), failure);
        return (invoker, sent);
    }

    /// <summary>A duplex call whose responses are scripted, and may wait, and whose requests are collected.</summary>
    private static (CallInvoker Invoker, List<PersistentReadRequest> Sent, CollectingWriter Writer) DuplexRead(IAsyncEnumerable<PersistentReadResponse> responses, RpcException? failure = null)
    {
        var sent = new List<PersistentReadRequest>();
        var writer = new CollectingWriter(sent);
        var invoker = A.Fake<CallInvoker>();
        A.CallTo(() => invoker.AsyncDuplexStreamingCall(A<Method<PersistentReadRequest, PersistentReadResponse>>._, A<string?>._, A<CallOptions>._))
            .ReturnsLazily(_ =>
            {
                var reader = new ScriptedReader(responses, failure);
                return new AsyncDuplexStreamingCall<PersistentReadRequest, PersistentReadResponse>(writer, reader, Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => [], () => { });
            });
        return (invoker, sent, writer);
    }

    /// <summary>The responses one by one, then the end of the call once the server is told the consumer has left.</summary>
    private static async IAsyncEnumerable<PersistentReadResponse> Trickle(IEnumerable<PersistentReadResponse> responses, Task ended)
    {
        foreach (var response in responses)
        {
            yield return response;
        }

        await ended;
    }

    private sealed class ScriptedReader(IAsyncEnumerable<PersistentReadResponse> responses, RpcException? failure) : IAsyncStreamReader<PersistentReadResponse>
    {
        private readonly IAsyncEnumerator<PersistentReadResponse> _responses = responses.GetAsyncEnumerator();

        public PersistentReadResponse Current => _responses.Current;

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await _responses.MoveNextAsync())
            {
                return true;
            }

            return failure is null ? false : throw failure;
        }
    }

    private sealed class CollectingWriter(List<PersistentReadRequest> sent) : IClientStreamWriter<PersistentReadRequest>
    {
        public WriteOptions? WriteOptions { get; set; }

        public Action? Completed { get; set; }

        public Task WriteAsync(PersistentReadRequest message)
        {
            sent.Add(message);
            return Task.CompletedTask;
        }

        public Task CompleteAsync()
        {
            Completed?.Invoke();
            return Task.CompletedTask;
        }
    }
}
