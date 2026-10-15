using System.Text;

using Shouldly;

namespace Dracocephalum.Nightingale.Server.Tests;

public sealed class StreamAppendsTests
{
    [Theory]
    [InlineData(-2, -1, true)]
    [InlineData(-2, 5, true)]
    [InlineData(-1, -1, true)]
    [InlineData(-1, 0, false)]
    [InlineData(-4, 0, true)]
    [InlineData(-4, -1, false)]
    [InlineData(3, 3, true)]
    [InlineData(3, 4, false)]
    public void Holds_ShouldMatchTheExpectedStateAgainstTheLastRevision(long expected, long actual, bool holds)
    {
        // Arrange: the contract's numbers, any -2, no stream -1, exists -4, else a revision.
        var state = expected switch
        {
            -2 => StreamState.Any,
            -1 => StreamState.NoStream,
            -4 => StreamState.StreamExists,
            _ => StreamState.StreamRevision(expected),
        };

        // Act & Assert
        StreamAppends.Holds(state, actual).ShouldBe(holds);
    }

    [Fact]
    public void FindProblems_OfAValidRequest_ShouldFindNone()
    {
        // Arrange: a stream and its own metadata together are fine; only a reset is not.
        StreamAppend[] appends = [Events("orders-1"), new StreamAppend.OfMetadata("orders-1", StreamState.Any, new StreamMetadata { MaxCount = 3 })];

        // Act & Assert
        StreamAppends.FindProblems(appends, [new StreamCheck("ledger-1", StreamState.NoStream)]).ShouldBeEmpty();
    }

    [Fact]
    public void FindProblems_ShouldFindEveryProblemInTheOrderOfTheParts()
    {
        // Arrange
        StreamAppend[] appends =
        [
            Events("orders-1"),
            new StreamAppend.OfEvents("payments-1", StreamState.Any, []),
            Events("orders-1"),
            new StreamAppend.OfMetadata("orders-2", StreamState.Any, new StreamMetadata { TruncateBefore = long.MaxValue }),
            new StreamAppend.OfMetadata("orders-3", StreamState.Any, new StreamMetadata { MaxCount = 0 }),
        ];
        StreamCheck[] checks = [new("ledger-1", StreamState.Any), new("orders-1", StreamState.NoStream), new("ledger-2", StreamState.NoStream), new("ledger-2", StreamState.StreamExists)];

        // Act
        var problems = StreamAppends.FindProblems(appends, checks);

        // Assert
        problems.Count.ShouldBe(7);
        problems[0].ShouldContain("Append 1 to payments-1 carries no events");
        problems[1].ShouldContain("Append 2 writes orders-1 again");
        problems[2].ShouldContain("Append 3 sets the metadata of orders-2 with $tb at its largest value");
        problems[3].ShouldContain("Append 4 sets the metadata of orders-3:");
        problems[4].ShouldContain("Check 0 on ledger-1 expects any state");
        problems[5].ShouldContain("Check 1 names orders-1, which is written");
        problems[6].ShouldContain("Check 3 checks ledger-2 again");
    }

    [Fact]
    public void FindProblems_WithNoAppends_ShouldSaySo()
    {
        // Act & Assert
        StreamAppends.FindProblems([], [new StreamCheck("ledger-1", StreamState.NoStream)]).ShouldHaveSingleItem().ShouldContain("writes at least one");
    }

    private static StreamAppend.OfEvents Events(string stream) =>
        new(stream, StreamState.Any, [new EventData(Guid.NewGuid(), "order_placed", Encoding.UTF8.GetBytes("{}"))]);
}
