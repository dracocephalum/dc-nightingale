using Shouldly;

namespace Dracocephalum.Nightingale.Client.Tests;

public sealed class NightingaleClientOptionsTests
{
    [Theory]
    [InlineData("http://localhost:5001")]
    [InlineData("https://nightingale.example")]
    public void Validate_WhenAbsoluteHttp_ShouldPass(string address)
    {
        // Arrange
        var sut = new NightingaleClientOptions { Address = new Uri(address) };

        // Act & Assert
        Should.NotThrow(sut.Validate);
    }

    [Fact]
    public void Validate_WhenRelative_ShouldThrow()
    {
        // Arrange
        var sut = new NightingaleClientOptions { Address = new Uri("/nightingale", UriKind.Relative) };

        // Act & Assert
        Should.Throw<ArgumentException>(sut.Validate);
    }

    [Fact]
    public void Validate_WhenNotHttp_ShouldThrow()
    {
        // Arrange
        var sut = new NightingaleClientOptions { Address = new Uri("ftp://nightingale.example") };

        // Act & Assert
        Should.Throw<ArgumentException>(sut.Validate);
    }
}
