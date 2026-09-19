using Shouldly;

namespace Dracocephalum.Nightingale.Tests;

public sealed class StreamPositionTests
{
    [Fact]
    public void From_WhenNegative_ShouldThrow()
    {
        // Act & Assert
        Should.Throw<ArgumentOutOfRangeException>(() => StreamPosition.From(-1));
    }

    [Fact]
    public void From_WhenZero_ShouldEqualStart()
    {
        // Act
        var position = StreamPosition.From(0);

        // Assert
        position.ShouldBe(StreamPosition.Start);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(41, 42)]
    public void Next_ShouldAdvanceByOne(long from, long expected)
    {
        // Arrange
        var sut = StreamPosition.From(from);

        // Act
        var next = sut.Next();

        // Assert
        next.Value.ShouldBe(expected);
    }

    [Fact]
    public void ToString_ShouldBeInvariantDigits()
    {
        // Arrange
        var sut = StreamPosition.From(1234567);

        // Act & Assert
        sut.ToString().ShouldBe("1234567");
    }
}
