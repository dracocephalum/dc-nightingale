using Dracocephalum.Nightingale.Protocols.Grpc.V1;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Shouldly;

namespace Dracocephalum.Nightingale.Client.Tests;

/// <summary>A stream read over a scripted call: what the first message decides, what is enumerated after it, and what a failure or a missing stream becomes.</summary>
public sealed class ReadStreamResultTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 20, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReadStreamAsync_ShouldAskForTheStreamInTheDirectionAndYieldTheEventsAfterTheHead()
    {
        // Arrange
        var (invoker, request) = FakeStreamsCall.Read(
        [
            new ReadResponse { Head = new StreamBounds { First = 0, Last = 1 } },
            new ReadResponse { Event = Recorded("orders-1", 1, 11) },
            new ReadResponse { Event = Recorded("orders-1", 0, 10) },
        ]);
        await using var client = new NightingaleClient(invoker);

        // Act
        await using var sut = client.ReadStreamAsync(Direction.Backwards, "orders-1", StreamPosition.End, 2, cancellationToken: TestContext.Current.CancellationToken);
        var revisions = new List<long>();
        await foreach (var record in sut.WithCancellation(TestContext.Current.CancellationToken))
        {
            revisions.Add(record.Revision);
        }

        // Assert
        var asked = request().ShouldNotBeNull();
        (asked.Stream, asked.Direction, asked.Count).ShouldBe(("orders-1", ReadDirection.Backwards, 2UL));
        sut.Stream.ShouldBe("orders-1");
        (await sut.ReadState).ShouldBe(ReadState.Ok);
        (await sut.Head).ShouldBe(new StreamHead(0, 1));
        revisions.ShouldBe([1, 0]);
    }

    [Fact]
    public async Task ReadStreamAsync_WhenTheStreamIsNotFound_ShouldSaySoAndThrowOnlyWhenEnumerated()
    {
        // Arrange
        var (invoker, _) = FakeStreamsCall.Read([new ReadResponse { StreamNotFound = new StreamNotFound() }]);
        await using var client = new NightingaleClient(invoker);

        // Act
        await using var sut = client.ReadStreamAsync(Direction.Forwards, "orders-1", StreamPosition.Start, cancellationToken: TestContext.Current.CancellationToken);
        var state = await sut.ReadState;
        var head = await sut.Head;
        var thrown = await Should.ThrowAsync<StreamNotFoundException>(async () => await sut.FirstAsync(TestContext.Current.CancellationToken));

        // Assert
        state.ShouldBe(ReadState.StreamNotFound);
        head.ShouldBeNull();
        thrown.Stream.ShouldBe("orders-1");
    }

    [Fact]
    public async Task ReadStreamAsync_WhenTheServerRefusesWithAReason_ShouldFaultTheStateTheHeadAndTheEnumeration()
    {
        // Arrange
        var (invoker, _) = FakeStreamsCall.Read([], FakeUnaryCall.Failure(StatusCode.FailedPrecondition, "STREAM_DELETED", ("stream", "orders-1")));
        await using var client = new NightingaleClient(invoker);

        // Act
        await using var sut = client.ReadStreamAsync(Direction.Forwards, "orders-1", StreamPosition.Start, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        (await Should.ThrowAsync<StreamDeletedException>(() => sut.ReadState)).Stream.ShouldBe("orders-1");
        await Should.ThrowAsync<StreamDeletedException>(() => sut.Head);
        await Should.ThrowAsync<StreamDeletedException>(async () => await sut.FirstAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadStreamAsync_WhenTheServerFailsPartWay_ShouldYieldWhatCameAndThenThrowTheReason()
    {
        // Arrange
        var (invoker, _) = FakeStreamsCall.Read(
            [
                new ReadResponse { Head = new StreamBounds { First = 0, Last = 5 } },
                new ReadResponse { Event = Recorded("orders-1", 0, 10) },
            ],
            FakeUnaryCall.Failure(StatusCode.FailedPrecondition, "STREAM_DELETED", ("stream", "orders-1")));
        await using var client = new NightingaleClient(invoker);
        await using var sut = client.ReadStreamAsync(Direction.Forwards, "orders-1", StreamPosition.Start, cancellationToken: TestContext.Current.CancellationToken);
        var revisions = new List<long>();

        // Act
        await Should.ThrowAsync<StreamDeletedException>(async () =>
        {
            await foreach (var record in sut.WithCancellation(TestContext.Current.CancellationToken))
            {
                revisions.Add(record.Revision);
            }
        });

        // Assert: the head was known before the failure, and stays known.
        revisions.ShouldBe([0]);
        (await sut.Head).ShouldBe(new StreamHead(0, 5));
    }

    [Fact]
    public async Task ReadStreamAsync_WhenTheReadEndsBeforeAFirstMessage_ShouldFaultAndNotLeaveTheConsumerWaiting()
    {
        // Arrange: a server that breaks the contract, ending the call having said nothing.
        var (invoker, _) = FakeStreamsCall.Read([]);
        await using var client = new NightingaleClient(invoker);

        // Act
        await using var sut = client.ReadStreamAsync(Direction.Forwards, "orders-1", StreamPosition.Start, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        await Should.ThrowAsync<InvalidOperationException>(() => sut.ReadState);
        await Should.ThrowAsync<InvalidOperationException>(() => sut.Head);
    }

    [Fact]
    public async Task ReadStreamAsync_WhenTheServerSendsSomethingAReadDoesNotCarry_ShouldPassOverIt()
    {
        // Arrange: a message with no content this build knows, as a newer server might send.
        var (invoker, _) = FakeStreamsCall.Read(
        [
            new ReadResponse { Head = new StreamBounds { First = 0, Last = 0 } },
            new ReadResponse(),
            new ReadResponse { Event = Recorded("orders-1", 0, 10) },
        ]);
        await using var client = new NightingaleClient(invoker);

        // Act
        await using var sut = client.ReadStreamAsync(Direction.Forwards, "orders-1", StreamPosition.Start, cancellationToken: TestContext.Current.CancellationToken);
        var records = await sut.ToListAsync(TestContext.Current.CancellationToken);

        // Assert
        records.ShouldHaveSingleItem().Position.ShouldBe(10);
    }

    [Fact]
    public async Task DisposeAsync_WhenCalledAgain_ShouldDoNothing()
    {
        // Arrange
        var (invoker, _) = FakeStreamsCall.Read([new ReadResponse { Head = new StreamBounds { First = 0, Last = 0 } }]);
        await using var client = new NightingaleClient(invoker);
        var sut = client.ReadStreamAsync(Direction.Forwards, "orders-1", StreamPosition.Start, cancellationToken: TestContext.Current.CancellationToken);
        await sut.DisposeAsync();

        // Act & Assert
        await Should.NotThrowAsync(async () => await sut.DisposeAsync());
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
