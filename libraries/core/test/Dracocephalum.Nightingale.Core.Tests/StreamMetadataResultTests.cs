using Shouldly;

namespace Dracocephalum.Nightingale.Tests;

public sealed class StreamMetadataResultTests
{
    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(3, true)]
    public void Exists_ShouldTellWhetherMetadataWasEverSet(long revision, bool expected)
    {
        // Arrange
        var sut = new StreamMetadataResult("orders-1", revision, StreamMetadata.Empty);

        // Act
        var exists = sut.Exists;

        // Assert
        exists.ShouldBe(expected);
    }
}
