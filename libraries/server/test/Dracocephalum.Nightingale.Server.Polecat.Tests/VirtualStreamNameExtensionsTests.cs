using Dracocephalum.Nightingale.Server.Data;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests;

/// <summary>Which links hold a virtual stream's ordinals, and that a stream without ordinals has none.</summary>
public sealed class VirtualStreamNameExtensionsTests
{
    [Theory]
    [InlineData(VirtualStreamKind.Category, nameof(OrdinalLinkType.Category))]
    [InlineData(VirtualStreamKind.EventType, nameof(OrdinalLinkType.EventType))]
    [InlineData(VirtualStreamKind.Correlation, nameof(OrdinalLinkType.Correlation))]
    [InlineData(VirtualStreamKind.Streams, nameof(OrdinalLinkType.Streams))]
    [InlineData(VirtualStreamKind.CategoryStreams, nameof(OrdinalLinkType.CategoryStreams))]
    public void ToOrdinalLinkType_ShouldNameTheLinksOfTheStreamsKind(VirtualStreamKind kind, string expected)
    {
        // Arrange
        var sut = new VirtualStreamName(kind, "orders");

        // Act
        var links = sut.ToOrdinalLinkType();

        // Assert
        links.ToString().ShouldBe(expected);
    }

    [Fact]
    public void ToOrdinalLinkType_WhenTheStreamHasNoOrdinals_ShouldRefuseNamingIt()
    {
        // Arrange: the stream of all data events is read by position only.
        var sut = new VirtualStreamName(VirtualStreamKind.DataEvents, string.Empty);

        // Act
        var refusal = Should.Throw<ArgumentException>(() => sut.ToOrdinalLinkType());

        // Assert
        refusal.Message.ShouldContain("$de has no ordinals");
    }
}
