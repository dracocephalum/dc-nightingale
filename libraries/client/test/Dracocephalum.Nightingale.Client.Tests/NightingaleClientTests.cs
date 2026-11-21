using Dracocephalum.Nightingale.Protocols.Grpc.V1;
using FakeItEasy;
using Grpc.Core;
using Shouldly;

namespace Dracocephalum.Nightingale.Client.Tests;

public sealed class NightingaleClientTests
{
    [Fact]
    public void Constructor_WhenNotAConnectionString_ShouldThrow()
    {
        // Act & Assert
        Should.Throw<FormatException>(() => new NightingaleClient("ftp://nightingale.example"));
    }

    [Theory]
    [InlineData("nightingale://one.example:5001,two.example:5002?tls=false&defaultDeadline=5000")]
    [InlineData("nightingale://reader:a-long-password@one.example:5001?tls=false")]
    [InlineData("nightingale://one.example:5001?tls=false&tenant=*")]
    public async Task Constructor_WhenAConnectionString_ShouldOpenWithoutConnecting(string connectionString)
    {
        // Act & Assert: nothing listens there, and nothing is asked of it yet.
        await using var sut = new NightingaleClient(connectionString);
    }

    [Fact]
    public async Task ReadStreamAsync_WhenCountIsZero_ShouldThrowBeforeCalling()
    {
        // Arrange: a strict invoker proves no call is made.
        var invoker = A.Fake<CallInvoker>(options => options.Strict());
        await using var sut = new NightingaleClient(invoker);

        // Act & Assert
        Should.Throw<ArgumentOutOfRangeException>(() => sut.ReadStreamAsync(Direction.Forwards, "orders-1", StreamPosition.Start, 0, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AppendToStreamAsync_WhenStreamIsEmpty_ShouldThrowBeforeCalling()
    {
        // Arrange
        var invoker = A.Fake<CallInvoker>(options => options.Strict());
        await using var sut = new NightingaleClient(invoker);

        // Act & Assert
        await Should.ThrowAsync<ArgumentException>(() => sut.AppendToStreamAsync(string.Empty, StreamState.Any, [], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AppendToStreamAsync_WhenTheServerEndsTheCallPartWay_ShouldThrowItsAnswerRatherThanTheWriteFailure()
    {
        // Arrange: the server refused the first event and ended the call, so the next write fails
        // without a reason, and the reason is in the response.
        var refusal = new RpcException(new Status(StatusCode.InvalidArgument, "An event carries JSON data, binary data, or both."));
        var writer = new EndingWriter<AppendRequest>(writesBeforeEnd: 2);
        var invoker = A.Fake<CallInvoker>();
        A.CallTo(() => invoker.AsyncClientStreamingCall(A<Method<AppendRequest, AppendResponse>>._, A<string>._, A<CallOptions>._))
            .Returns(new AsyncClientStreamingCall<AppendRequest, AppendResponse>(writer, Task.FromException<AppendResponse>(refusal), Task.FromResult(new Metadata()), () => refusal.Status, () => [], () => { }));
        await using var sut = new NightingaleClient(invoker);
        EventData[] events = [new(Guid.NewGuid(), "a", "{}"u8.ToArray()), new(Guid.NewGuid(), "b", default), new(Guid.NewGuid(), "c", "{}"u8.ToArray())];

        // Act
        var exception = await Should.ThrowAsync<RpcException>(() => sut.AppendToStreamAsync("orders-1", StreamState.Any, events, TestContext.Current.CancellationToken));

        // Assert
        exception.Status.Detail.ShouldBe(refusal.Status.Detail);
        writer.Writes.ShouldBe(2);
    }

    [Fact]
    public async Task AppendToStreamsAsync_ShouldSendEachStreamsHeaderAndEventsThenTheMetadataThenTheChecks()
    {
        // Arrange
        var writer = new RecordingWriter<AppendToStreamsRequest>();
        var response = new AppendToStreamsResponse { Appended = { new AppendedStream { Stream = "orders-1", Revision = 4, Position = 40 }, new AppendedStream { Stream = "$$payments-1", Revision = 0, Position = 41 } } };
        var invoker = A.Fake<CallInvoker>();
        A.CallTo(() => invoker.AsyncClientStreamingCall(A<Method<AppendToStreamsRequest, AppendToStreamsResponse>>._, A<string>._, A<CallOptions>._))
            .Returns(new AsyncClientStreamingCall<AppendToStreamsRequest, AppendToStreamsResponse>(writer, Task.FromResult(response), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => [], () => { }));
        await using var sut = new NightingaleClient(invoker);

        // Act
        var results = await sut.AppendToStreamsAsync(
            [
                new StreamAppend.OfEvents("orders-1", StreamState.StreamRevision(2), [new EventData(Guid.NewGuid(), "a", "{}"u8.ToArray()), new EventData(Guid.NewGuid(), "b", "{}"u8.ToArray())]),
                new StreamAppend.OfMetadata("payments-1", StreamState.Any, new StreamMetadata { MaxCount = 7 }),
            ],
            [new StreamCheck("ledger-1", StreamState.NoStream)],
            TestContext.Current.CancellationToken);

        // Assert
        writer.Written.Select(part => part.ContentCase).ShouldBe(
        [
            AppendToStreamsRequest.ContentOneofCase.Stream,
            AppendToStreamsRequest.ContentOneofCase.Event,
            AppendToStreamsRequest.ContentOneofCase.Event,
            AppendToStreamsRequest.ContentOneofCase.Metadata,
            AppendToStreamsRequest.ContentOneofCase.Check,
        ]);
        (writer.Written[0].Stream.Stream, writer.Written[0].Stream.ExpectedRevision).ShouldBe(("orders-1", 2L));
        (writer.Written[3].Metadata.Stream, writer.Written[3].Metadata.Metadata.MaxCount).ShouldBe(("payments-1", 7L));
        (writer.Written[4].Check.Stream, writer.Written[4].Check.ExpectedRevision).ShouldBe(("ledger-1", StreamState.NoStream.ToInt64()));
        writer.Completed.ShouldBeTrue();
        results.ShouldBe([new StreamAppendResult("orders-1", 4, 40), new StreamAppendResult("$$payments-1", 0, 41)]);
    }

    [Fact]
    public async Task AppendToStreamAsync_ShouldSendTheOptionsThenEachEventAndReturnWhereTheLastLanded()
    {
        // Arrange
        var writer = new RecordingWriter<AppendRequest>();
        var invoker = A.Fake<CallInvoker>();
        A.CallTo(() => invoker.AsyncClientStreamingCall(A<Method<AppendRequest, AppendResponse>>._, A<string>._, A<CallOptions>._))
            .Returns(new AsyncClientStreamingCall<AppendRequest, AppendResponse>(writer, Task.FromResult(new AppendResponse { Revision = 4, Position = 40 }), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => [], () => { }));
        await using var sut = new NightingaleClient(invoker);
        EventData[] events = [new(Guid.NewGuid(), "a", "{}"u8.ToArray()), new(Guid.NewGuid(), "b", "{}"u8.ToArray())];

        // Act
        var result = await sut.AppendToStreamAsync("orders-1", StreamState.StreamRevision(2), events, TestContext.Current.CancellationToken);

        // Assert
        result.ShouldBe(new AppendResult(4, 40));
        writer.Written.Select(part => part.ContentCase).ShouldBe([AppendRequest.ContentOneofCase.Options, AppendRequest.ContentOneofCase.Event, AppendRequest.ContentOneofCase.Event]);
        (writer.Written[0].Options.Stream, writer.Written[0].Options.ExpectedRevision).ShouldBe(("orders-1", 2L));
        writer.Written.Skip(1).Select(part => part.Event.EventType).ShouldBe(["a", "b"]);
        writer.Completed.ShouldBeTrue();
    }

    [Fact]
    public async Task AppendToStreamAsync_WhenTheStreamIsNotInTheExpectedState_ShouldThrowTheConflict()
    {
        // Arrange
        var refusal = FakeUnaryCall.Failure(StatusCode.FailedPrecondition, "REVISION_CONFLICT", ("stream", "orders-1"), ("expected", "2"), ("actual", "5"));
        var invoker = A.Fake<CallInvoker>();
        A.CallTo(() => invoker.AsyncClientStreamingCall(A<Method<AppendRequest, AppendResponse>>._, A<string>._, A<CallOptions>._))
            .Returns(new AsyncClientStreamingCall<AppendRequest, AppendResponse>(new RecordingWriter<AppendRequest>(), Task.FromException<AppendResponse>(refusal), Task.FromResult(new Metadata()), () => refusal.Status, () => [], () => { }));
        await using var sut = new NightingaleClient(invoker);

        // Act
        var conflict = await Should.ThrowAsync<RevisionConflictException>(() => sut.AppendToStreamAsync("orders-1", StreamState.StreamRevision(2), [new EventData(Guid.NewGuid(), "a", "{}"u8.ToArray())], TestContext.Current.CancellationToken));

        // Assert
        (conflict.Stream, conflict.Expected, conflict.ActualRevision).ShouldBe(("orders-1", StreamState.StreamRevision(2), 5L));
    }

    [Fact]
    public async Task AppendToStreamsAsync_WhenTheServerEndsTheCallPartWay_ShouldThrowItsReasonRatherThanTheWriteFailure()
    {
        // Arrange: the server refused at the first stream's header and ended the call.
        var refusal = FakeUnaryCall.Failure(StatusCode.FailedPrecondition, "STREAM_DELETED", ("stream", "orders-1"));
        var writer = new EndingWriter<AppendToStreamsRequest>(writesBeforeEnd: 1);
        var invoker = A.Fake<CallInvoker>();
        A.CallTo(() => invoker.AsyncClientStreamingCall(A<Method<AppendToStreamsRequest, AppendToStreamsResponse>>._, A<string>._, A<CallOptions>._))
            .Returns(new AsyncClientStreamingCall<AppendToStreamsRequest, AppendToStreamsResponse>(writer, Task.FromException<AppendToStreamsResponse>(refusal), Task.FromResult(new Metadata()), () => refusal.Status, () => [], () => { }));
        await using var sut = new NightingaleClient(invoker);
        StreamAppend[] appends = [new StreamAppend.OfEvents("orders-1", StreamState.Any, [new EventData(Guid.NewGuid(), "a", "{}"u8.ToArray())])];

        // Act
        var deleted = await Should.ThrowAsync<StreamDeletedException>(() => sut.AppendToStreamsAsync(appends, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        deleted.Stream.ShouldBe("orders-1");
        writer.Writes.ShouldBe(1);
    }

    [Fact]
    public async Task ArchiveStreamAsync_ShouldSendTheStreamAndItsExpectation()
    {
        // Arrange
        var asked = new List<ArchiveRequest>();
        await using var sut = new NightingaleClient(FakeUnaryCall.Answering(asked, new ArchiveResponse()));

        // Act
        await sut.ArchiveStreamAsync("orders-1", StreamState.StreamRevision(5), TestContext.Current.CancellationToken);

        // Assert
        asked.ShouldHaveSingleItem().ShouldBe(new ArchiveRequest { Stream = "orders-1", ExpectedRevision = 5 });
    }

    [Fact]
    public async Task ArchiveStreamAsync_WhenTheServerDoesNotAllowIt_ShouldThrowThatDeletionIsDisabled()
    {
        // Arrange
        await using var sut = new NightingaleClient(FakeUnaryCall.Failing<ArchiveRequest, ArchiveResponse>(FakeUnaryCall.Failure(StatusCode.PermissionDenied, "DELETION_DISABLED", ("stream", "orders-1"))));

        // Act & Assert
        await Should.ThrowAsync<DeletionDisabledException>(() => sut.ArchiveStreamAsync("orders-1", StreamState.Any, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteStreamAsync_ShouldSendTheStreamAndItsExpectation()
    {
        // Arrange
        var asked = new List<DeleteRequest>();
        await using var sut = new NightingaleClient(FakeUnaryCall.Answering(asked, new DeleteResponse()));

        // Act
        await sut.DeleteStreamAsync("orders-1", StreamState.StreamExists, TestContext.Current.CancellationToken);

        // Assert
        asked.ShouldHaveSingleItem().ShouldBe(new DeleteRequest { Stream = "orders-1", ExpectedRevision = StreamState.StreamExists.ToInt64() });
    }

    [Fact]
    public async Task DeleteStreamAsync_WhenTheStreamHasNoEvents_ShouldThrowThatItIsNotFound()
    {
        // Arrange
        await using var sut = new NightingaleClient(FakeUnaryCall.Failing<DeleteRequest, DeleteResponse>(FakeUnaryCall.Failure(StatusCode.NotFound, "STREAM_NOT_FOUND", ("stream", "orders-1"))));

        // Act
        var missing = await Should.ThrowAsync<StreamNotFoundException>(() => sut.DeleteStreamAsync("orders-1", StreamState.Any, TestContext.Current.CancellationToken));

        // Assert
        missing.Stream.ShouldBe("orders-1");
    }

    /// <summary>A request stream that keeps what was written to it.</summary>
    private sealed class RecordingWriter<T> : IClientStreamWriter<T>
    {
        public List<T> Written { get; } = [];

        public bool Completed { get; private set; }

        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(T message) => WriteAsync(message, CancellationToken.None);

        public Task WriteAsync(T message, CancellationToken cancellationToken)
        {
            Written.Add(message);
            return Task.CompletedTask;
        }

        public Task CompleteAsync()
        {
            Completed = true;
            return Task.CompletedTask;
        }
    }

    /// <summary>A request stream whose call the server ends after so many writes, as grpc-dotnet's does: later writes fail.</summary>
    private sealed class EndingWriter<T>(int writesBeforeEnd) : IClientStreamWriter<T>
    {
        public int Writes { get; private set; }

        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(T message) => WriteAsync(message, CancellationToken.None);

        public Task WriteAsync(T message, CancellationToken cancellationToken)
        {
            if (Writes == writesBeforeEnd)
            {
                return Task.FromException(new InvalidOperationException("Can't write the message because the call is complete."));
            }

            Writes++;
            return Task.CompletedTask;
        }

        public Task CompleteAsync() => Task.FromException(new InvalidOperationException("Can't complete the request stream because the call is complete."));
    }
}
