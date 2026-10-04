using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests;

public sealed class ContiguousPrefixTests
{
    [Theory]
    [InlineData(10, new long[0], 10)]
    [InlineData(10, new long[] { 11, 12, 13 }, 13)]
    [InlineData(10, new long[] { 12, 13 }, 10)]
    [InlineData(10, new long[] { 11, 12, 14, 15 }, 12)]
    [InlineData(0, new long[] { 1 }, 1)]
    public void After_ShouldStopBeforeTheFirstGap(long mark, long[] following, long expected)
    {
        // Act: a missing position is a transaction still in flight, or one that never committed;
        // either way nothing above it is safe to read yet.
        var reached = ContiguousPrefix.After(mark, following);

        // Assert
        reached.ShouldBe(expected);
    }
}
