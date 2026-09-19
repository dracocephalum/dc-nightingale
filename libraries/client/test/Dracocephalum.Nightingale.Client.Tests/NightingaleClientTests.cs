using FakeItEasy;
using Grpc.Core;
using Shouldly;

namespace Dracocephalum.Nightingale.Client.Tests;

public sealed class NightingaleClientTests
{
    [Fact]
    public void Constructor_WhenAddressSchemeIsNotHttp_ShouldThrow()
    {
        // Arrange
        var options = new NightingaleClientOptions { Address = new Uri("ftp://nightingale.example") };

        // Act & Assert
        Should.Throw<ArgumentException>(() => new NightingaleClient(options));
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
