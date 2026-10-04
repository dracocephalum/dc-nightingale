using Grpc.Core;
using Shouldly;

namespace Dracocephalum.Nightingale.Client.Tests;

/// <summary>
/// Opening a channel connects to nothing until a call is made, so these hold what the channel was
/// pointed at; a call through a channel to a running server is the samples' part.
/// </summary>
public sealed class ClientChannelsTests
{
    [Theory]
    [InlineData("nightingale://events.example:5001", "events.example:5001")]
    [InlineData("nightingale://events.example:5001?tls=false&tlsVerifyCert=false&keepAliveInterval=-1", "events.example:5001")]
    public void Open_ShouldPointTheChannelAtWhatTheStringNames(string connectionString, string target)
    {
        // Arrange
        var settings = NightingaleClientSettings.Parse(connectionString);

        // Act
        using var channel = ClientChannels.Open(settings);

        // Assert
        channel.Target.ShouldBe(target);
    }

    [Theory]
    [InlineData("nightingale://one.example:5001,two.example:5002?tls=false")]
    [InlineData("nightingale://one.example:5001,two.example:5002")]
    [InlineData("nightingale+discover://cluster.example:5001?tls=false")]
    [InlineData("nightingale+discover://cluster.example")]
    public void Open_WhenSeveralInstances_ShouldFindAResolverAndWaitForACall(string connectionString)
    {
        // Arrange
        var settings = NightingaleClientSettings.Parse(connectionString);

        // Act
        using var channel = ClientChannels.Open(settings);

        // Assert: the channel knows how to resolve its address, or opening it would have thrown.
        channel.State.ShouldBe(ConnectivityState.Idle);
    }

    [Fact]
    public void Open_WhenNoHost_ShouldThrow()
    {
        // Arrange
        var settings = new NightingaleClientSettings { Endpoints = [] };

        // Act & Assert
        Should.Throw<ArgumentException>(() => ClientChannels.Open(settings));
    }

    [Fact]
    public void OpenTo_ShouldPointTheChannelAtTheInstance()
    {
        // Arrange
        var settings = NightingaleClientSettings.Parse("nightingale://one.example:5001,two.example:5002?tls=false");

        // Act
        using var channel = ClientChannels.OpenTo(settings, new Uri("http://two.example:5002"));

        // Assert
        channel.Target.ShouldBe("two.example:5002");
    }
}
