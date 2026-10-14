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

    [Fact]
    public async Task Constructor_WhenAConnectionString_ShouldOpenWithoutConnecting()
    {
        // Act & Assert: nothing listens there, and nothing is asked of it yet.
        await using var sut = new NightingaleClient("nightingale://one.example:5001,two.example:5002?tls=false&defaultDeadline=5000");
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
        var writer = new EndingWriter(writesBeforeEnd: 2);
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

    /// <summary>A request stream whose call the server ends after so many writes, as grpc-dotnet's does: later writes fail.</summary>
    private sealed class EndingWriter(int writesBeforeEnd) : IClientStreamWriter<AppendRequest>
    {
        public int Writes { get; private set; }

        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(AppendRequest message) => WriteAsync(message, CancellationToken.None);

        public Task WriteAsync(AppendRequest message, CancellationToken cancellationToken)
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
