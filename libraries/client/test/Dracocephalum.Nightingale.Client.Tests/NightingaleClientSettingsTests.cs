using Shouldly;

namespace Dracocephalum.Nightingale.Client.Tests;

public sealed class NightingaleClientSettingsTests
{
    [Fact]
    public void Parse_WhenOnlyAHost_ShouldUseTheDefaults()
    {
        // Act
        var sut = NightingaleClientSettings.Parse("nightingale://events.example");

        // Assert
        var endpoint = sut.Endpoints.ShouldHaveSingleItem();
        endpoint.Host.ShouldBe("events.example");
        endpoint.Port.ShouldBe(NightingaleClientSettings.DefaultPort);
        sut.Discover.ShouldBeFalse();
        sut.Tls.ShouldBeTrue();
        sut.TlsVerifyCertificate.ShouldBeTrue();
        sut.KeepAliveInterval.ShouldBe(TimeSpan.FromSeconds(10));
        sut.KeepAliveTimeout.ShouldBe(TimeSpan.FromSeconds(10));
        sut.DefaultDeadline.ShouldBeNull();
        sut.UserName.ShouldBeNull();
        sut.Password.ShouldBeNull();
    }

    [Fact]
    public void Parse_WhenSeveralHostsAndSettings_ShouldReadThemAllWhateverTheCaseOfTheKeys()
    {
        // Act
        var sut = NightingaleClientSettings.Parse("Nightingale://one.example:5001,two.example,[::1]:5003/?TLS=false&tlsverifycert=False&keepAliveInterval=-1&KeepAliveTimeout=2500&defaultDeadline=30000");

        // Assert
        sut.Endpoints.Select(endpoint => $"{endpoint.Host}:{endpoint.Port}").ShouldBe(["one.example:5001", "two.example:2113", "[::1]:5003"]);
        sut.Tls.ShouldBeFalse();
        sut.TlsVerifyCertificate.ShouldBeFalse();
        sut.KeepAliveInterval.ShouldBeNull();
        sut.KeepAliveTimeout.ShouldBe(TimeSpan.FromMilliseconds(2500));
        sut.DefaultDeadline.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Parse_WhenDiscover_ShouldNameTheOneHostToResolve()
    {
        // Act
        var sut = NightingaleClientSettings.Parse("nightingale+discover://cluster.example:5001");

        // Assert
        sut.Discover.ShouldBeTrue();
        sut.Endpoints.ShouldHaveSingleItem().Host.ShouldBe("cluster.example");
    }

    [Fact]
    public void Parse_WhenCredentials_ShouldUnescapeAndHoldThem()
    {
        // Act: the password holds an escaped '@' and ':'.
        var sut = NightingaleClientSettings.Parse("nightingale://reader:p%40ss%3Aword@events.example:5001?tls=false");

        // Assert
        sut.UserName.ShouldBe("reader");
        sut.Password.ShouldBe("p@ss:word");
        sut.Endpoints.ShouldHaveSingleItem().Host.ShouldBe("events.example");
    }

    [Theory]
    [InlineData("events.example:5001")]
    [InlineData("https://events.example:5001")]
    [InlineData("nightingale://")]
    [InlineData("nightingale://one.example,,two.example")]
    [InlineData("nightingale://events.example:port")]
    [InlineData("nightingale://events.example:0")]
    [InlineData("nightingale://events.example:70000")]
    [InlineData("nightingale://[::1:5001")]
    [InlineData("nightingale://events.example/streams")]
    [InlineData("nightingale://:secret@events.example")]
    [InlineData("nightingale+discover://one.example,two.example")]
    [InlineData("nightingale://events.example?tls")]
    [InlineData("nightingale://events.example?tls=yes")]
    [InlineData("nightingale://events.example?tls=true&TLS=false")]
    [InlineData("nightingale://events.example?keepAliveInterval=0")]
    [InlineData("nightingale://events.example?keepAliveTimeout=soon")]
    [InlineData("nightingale://events.example?defaultDeadline=-1")]
    [InlineData("nightingale://events.example?nodePreference=leader")]
    public void Parse_WhenNotAConnectionString_ShouldThrow(string connectionString)
    {
        // Act & Assert
        Should.Throw<FormatException>(() => NightingaleClientSettings.Parse(connectionString));
    }

    [Fact]
    public void Parse_WhenTheStringIsRefused_ShouldNotRepeatTheCredentials()
    {
        // Act
        var exception = Should.Throw<FormatException>(() => NightingaleClientSettings.Parse("nightingale://some-user:some-password@events.example?tls=perhaps"));

        // Assert
        exception.Message.ShouldNotContain("some-password");
        exception.Message.ShouldNotContain("some-user");
    }

    [Fact]
    public void Parse_WhenBlank_ShouldThrow()
    {
        // Act & Assert
        Should.Throw<ArgumentException>(() => NightingaleClientSettings.Parse(" "));
    }
}
