using Dracocephalum.Nightingale.Protocol.V1;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Shouldly;

namespace Dracocephalum.Nightingale.Client.Tests;

public sealed class StreamSubscriptionTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 20, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Messages_ShouldYieldEverythingTheServerSaidInOrder()
    {
        // Arrange
        var (invoker, _) = FakeStreamsCall.Read(
        [
            new ReadResponse { Confirmed = new SubscriptionConfirmed { SubscriptionId = "s-1", Head = 1 } },
            new ReadResponse { Event = Recorded("orders-1", 0, 10) },
            new ReadResponse { Event = Recorded("orders-1", 1, 11) },
            new ReadResponse { CaughtUp = new CaughtUp { Head = 1, At = Timestamp.FromDateTimeOffset(At) } },
            new ReadResponse { FellBehind = new FellBehind { Head = 9, EventsBehind = 7, At = Timestamp.FromDateTimeOffset(At) } },
        ]);
        await using var sut = new NightingaleClient(invoker);

        // Act
        await using var subscription = sut.SubscribeToStreamAsync("orders-1", StreamPosition.Start, TestContext.Current.CancellationToken);
        var messages = new List<SubscriptionMessage>();
        await foreach (var message in subscription.Messages.WithCancellation(TestContext.Current.CancellationToken))
        {
            messages.Add(message);
        }

        // Assert
        (await subscription.Confirmed).ShouldBe(new SubscriptionMessage.Confirmed("s-1", 1));
        messages.Count.ShouldBe(5);
        messages[0].ShouldBeOfType<SubscriptionMessage.Confirmed>().SubscriptionId.ShouldBe("s-1");
        messages[1].ShouldBeOfType<SubscriptionMessage.Recorded>().Record.Revision.ShouldBe(0);
        messages[2].ShouldBeOfType<SubscriptionMessage.Recorded>().Record.Revision.ShouldBe(1);
        messages[3].ShouldBe(new SubscriptionMessage.CaughtUp(1, At));
        messages[4].ShouldBe(new SubscriptionMessage.FellBehind(9, 7, At));
    }

    [Fact]
    public async Task Enumerating_ShouldYieldOnlyTheEvents()
    {
        // Arrange
        var (invoker, _) = FakeStreamsCall.Read(
        [
            new ReadResponse { Confirmed = new SubscriptionConfirmed { SubscriptionId = "s-1", Head = 0 } },
            new ReadResponse { Event = Recorded("orders-1", 0, 10) },
            new ReadResponse { CaughtUp = new CaughtUp { Head = 0, At = Timestamp.FromDateTimeOffset(At) } },
            new ReadResponse { Event = Recorded("orders-1", 1, 11) },
        ]);
        await using var sut = new NightingaleClient(invoker);

        // Act
        await using var subscription = sut.SubscribeToStreamAsync("orders-1", StreamPosition.Start, TestContext.Current.CancellationToken);
        var revisions = new List<long>();
        await foreach (var record in subscription.WithCancellation(TestContext.Current.CancellationToken))
        {
            revisions.Add(record.Revision);
        }

        // Assert
        revisions.ShouldBe([0, 1]);
    }

    [Fact]
    public async Task SubscribeToAllAsync_ShouldAskForAllFromTheEndAsASubscription()
    {
        // Arrange
        var (invoker, request) = FakeStreamsCall.Read([new ReadResponse { Confirmed = new SubscriptionConfirmed { SubscriptionId = "s-2", Head = 40 } }]);
        await using var sut = new NightingaleClient(invoker);

        // Act
        await using var subscription = sut.SubscribeToAllAsync(StreamPosition.End, TestContext.Current.CancellationToken);
        var confirmed = await subscription.Confirmed;

        // Assert
        confirmed.Head.ShouldBe(40);
        var sent = request().ShouldNotBeNull();
        sent.Stream.ShouldBe("$all");
        sent.ModeCase.ShouldBe(ReadRequest.ModeOneofCase.Subscription);
        sent.FromCase.ShouldBe(ReadRequest.FromOneofCase.End);
    }

    [Fact]
    public async Task SubscribeToStreamAsync_WhenFromAPosition_ShouldSendItInclusive()
    {
        // Arrange
        var (invoker, request) = FakeStreamsCall.Read([]);
        await using var sut = new NightingaleClient(invoker);

        // Act
        await using var subscription = sut.SubscribeToStreamAsync("orders-1", StreamPosition.From(7), TestContext.Current.CancellationToken);
        var delivered = 0;
        await foreach (var record in subscription.WithCancellation(TestContext.Current.CancellationToken))
        {
            delivered++;
        }

        // Assert
        delivered.ShouldBe(0);
        request().ShouldNotBeNull().Position.ShouldBe(7);
    }

    [Fact]
    public async Task Confirmed_WhenTheServerFailsTheCallWithoutAReason_ShouldFaultWithTheCallException()
    {
        // Arrange: a status with no error detail maps to nothing, so the call exception is what the
        // consumer gets, with its status code.
        var (invoker, _) = FakeStreamsCall.Read([], new Status(StatusCode.InvalidArgument, "A subscription reads forwards."));
        await using var sut = new NightingaleClient(invoker);

        // Act
        await using var subscription = sut.SubscribeToStreamAsync("orders-1", StreamPosition.Start, TestContext.Current.CancellationToken);

        // Assert
        var exception = await Should.ThrowAsync<RpcException>(() => subscription.Confirmed);
        exception.StatusCode.ShouldBe(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task ReadAllAsync_ShouldReadAllAndReportTheHead()
    {
        // Arrange
        var (invoker, request) = FakeStreamsCall.Read(
        [
            new ReadResponse { Head = new StreamBounds { First = 0, Last = 11 } },
            new ReadResponse { Event = Recorded("orders-1", 0, 10) },
            new ReadResponse { Event = Recorded("orders-2", 0, 11) },
        ]);
        await using var sut = new NightingaleClient(invoker);

        // Act
        await using var read = sut.ReadAllAsync(Direction.Forwards, StreamPosition.Start, 2, TestContext.Current.CancellationToken);
        var positions = new List<long>();
        await foreach (var record in read.WithCancellation(TestContext.Current.CancellationToken))
        {
            positions.Add(record.Position);
        }

        // Assert
        (await read.Head).ShouldBe(new StreamHead(0, 11));
        positions.ShouldBe([10, 11]);
        request().ShouldNotBeNull().Stream.ShouldBe("$all");
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
}
