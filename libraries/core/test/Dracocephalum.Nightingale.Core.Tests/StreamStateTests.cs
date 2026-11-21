using Shouldly;

namespace Dracocephalum.Nightingale.Tests;

public sealed class StreamStateTests
{
    public static TheoryData<long, StreamState> Named => new()
    {
        { -1, StreamState.NoStream },
        { -2, StreamState.Any },
        { -4, StreamState.StreamExists },
    };

    [Fact]
    public void StreamRevision_WhenNegative_ShouldThrow()
    {
        // Act & Assert
        Should.Throw<ArgumentOutOfRangeException>(() => StreamState.StreamRevision(-1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(41)]
    public void StreamRevision_WhenZeroOrMore_ShouldHoldTheRevision(long revision)
    {
        // Act
        var sut = StreamState.StreamRevision(revision);

        // Assert
        sut.HasRevision.ShouldBeTrue();
        sut.ToInt64().ShouldBe(revision);
    }

    [Theory]
    [MemberData(nameof(Named))]
    public void FromInt64_WhenTheValueOfANamedState_ShouldBeThatState(long value, StreamState expected)
    {
        // Act
        var sut = StreamState.FromInt64(value);

        // Assert
        sut.ShouldBe(expected);
        sut.HasRevision.ShouldBeFalse();
        sut.ToInt64().ShouldBe(value);
    }

    [Fact]
    public void FromInt64_WhenZeroOrMore_ShouldBeThatRevision()
    {
        // Act
        var sut = StreamState.FromInt64(7);

        // Assert
        sut.ShouldBe(StreamState.StreamRevision(7));
    }

    [Theory]
    [InlineData(-3)]
    [InlineData(-5)]
    [InlineData(long.MinValue)]
    public void FromInt64_WhenNeitherARevisionNorANamedState_ShouldThrow(long value)
    {
        // Act & Assert
        Should.Throw<ArgumentOutOfRangeException>(() => StreamState.FromInt64(value));
    }

    [Theory]
    [InlineData(-1, "NoStream")]
    [InlineData(-2, "Any")]
    [InlineData(-4, "StreamExists")]
    [InlineData(7, "7")]
    public void ToString_ShouldNameTheStateOrGiveTheRevision(long value, string expected)
    {
        // Arrange
        var sut = StreamState.FromInt64(value);

        // Act
        var text = sut.ToString();

        // Assert
        text.ShouldBe(expected);
    }
}
