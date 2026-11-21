using System.Net;

using FakeItEasy;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Tests;

/// <summary>Where an instance says it is reached: the configured address first, then what the server listens on, and nothing for a server that listens on nothing.</summary>
public sealed class InstanceAddressTests
{
    private readonly TestOptions _options = new();
    private readonly IServer _server = A.Fake<IServer>();
    private readonly ServerAddressesFeature _listening = new();

    public InstanceAddressTests()
    {
        var features = new FeatureCollection();
        features.Set<IServerAddressesFeature>(_listening);
        A.CallTo(() => _server.Features).Returns(features);
    }

    [Fact]
    public void Current_WhenAnAddressIsConfigured_ShouldBeThatWhateverTheServerListensOn()
    {
        // Arrange
        _options.Cluster.AdvertisedAddress = "https://node-1.example:5001";
        _listening.Addresses.Add("http://localhost:5000");
        var sut = new InstanceAddress(_options, _server);

        // Act
        var address = sut.Current;

        // Assert
        address.ShouldBe(new Uri("https://node-1.example:5001"));
    }

    [Fact]
    public void Current_WhenTheServerListensOnANamedHost_ShouldBeItsFirstHttpAddress()
    {
        // Arrange
        _listening.Addresses.Add("net.pipe://localhost/nightingale");
        _listening.Addresses.Add("http://node-2.example:5000");
        _listening.Addresses.Add("https://node-2.example:5001");
        var sut = new InstanceAddress(_options, _server);

        // Act
        var address = sut.Current;

        // Assert
        address.ShouldBe(new Uri("http://node-2.example:5000"));
    }

    [Theory]
    [InlineData("http://0.0.0.0:5000")]
    [InlineData("http://[::]:5000")]
    public void Current_WhenTheServerListensOnEveryInterface_ShouldNameThisMachine(string listening)
    {
        // Arrange
        _listening.Addresses.Add(listening);
        var sut = new InstanceAddress(_options, _server);

        // Act
        var address = sut.Current;

        // Assert
        var derived = address.ShouldNotBeNull();
        derived.Host.ShouldBe(Dns.GetHostName(), StringCompareShould.IgnoreCase);
        (derived.Scheme, derived.Port).ShouldBe(("http", 5000));
    }

    [Fact]
    public void Current_WhenTheServerListensOnNothing_ShouldBeNothing()
    {
        // Arrange: a test server, which has no addresses.
        var sut = new InstanceAddress(_options, _server);

        // Act
        var address = sut.Current;

        // Assert
        address.ShouldBeNull();
    }

    [Fact]
    public void Current_ShouldBeDerivedOnce()
    {
        // Arrange
        _listening.Addresses.Add("http://node-2.example:5000");
        var sut = new InstanceAddress(_options, _server);
        var first = sut.Current;
        _listening.Addresses.Clear();
        _listening.Addresses.Add("http://node-3.example:5000");

        // Act
        var second = sut.Current;

        // Assert
        second.ShouldBe(first);
    }

    private sealed class TestOptions : NightingaleOptionsBase
    {
    }
}
