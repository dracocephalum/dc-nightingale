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
}
