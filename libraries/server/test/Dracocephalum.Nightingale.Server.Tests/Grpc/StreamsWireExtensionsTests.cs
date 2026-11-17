using Dracocephalum.Nightingale.Protocols.Grpc.V1;
using Dracocephalum.Nightingale.Server.Grpc;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Tests.Grpc;

/// <summary>Where a subscription counted in positions starts, by what the request asks.</summary>
public sealed class StreamsWireExtensionsTests
{
    [Fact]
    public void GetStartPosition_WhenAPositionIsAsked_ShouldGiveIt()
    {
        // Arrange
        var request = new ReadRequest { Position = 42 };

        // Act
        var start = request.GetStartPosition(head: 100);

        // Assert
        start.ShouldBe(42);
    }

    [Fact]
    public void GetStartPosition_WhenAskedFromTheEnd_ShouldGiveThePositionAfterTheHead()
    {
        // Arrange
        var request = new ReadRequest { End = new() };

        // Act
        var start = request.GetStartPosition(head: 100);

        // Assert
        start.ShouldBe(101);
    }

    [Fact]
    public void GetStartPosition_WhenNothingIsAsked_ShouldGiveTheBeginning()
    {
        // Arrange
        var request = new ReadRequest();

        // Act
        var start = request.GetStartPosition(head: 100);

        // Assert
        start.ShouldBe(0);
    }
}
