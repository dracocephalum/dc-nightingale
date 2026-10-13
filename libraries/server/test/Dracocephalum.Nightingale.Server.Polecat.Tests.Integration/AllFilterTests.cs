using System.Text;

using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests.Integration;

/// <summary>
/// A filtered scan of <c>$all</c> against the real events table: a category prefix through the
/// category column, with the stream named just the category left out as the reference's prefix
/// filter leaves it; a prefix with a character SQL Server's <c>LIKE</c> would read as a wildcard,
/// escaped by the provider; an event-type prefix; a regular expression matched in process; and a
/// scan backwards. The shared database holds other tests' events, so every name here carries a
/// suffix of its own and the scans are bounded to the positions appended here.
/// </summary>
[Collection(SharedSqlServer.Name)]
[Trait("Category", "Integration")]
public sealed class AllFilterTests(SqlServerTestDatabase database)
{
    [Fact]
    public async Task ScanAll_WithACategoryPrefix_ShouldKeepTheCategorysStreamsButNotTheBareName()
    {
        // Arrange: two streams of the category, the bare name, and a stream whose name merely
        // begins with the category's letters.
        var category = "flt" + Suffix();
        var sut = database.Store;
        var first = await sut.AppendAsync(category + "-1", StreamState.NoStream, [Event("placed")], TestContext.Current.CancellationToken);
        await sut.AppendAsync(category, StreamState.NoStream, [Event("placed")], TestContext.Current.CancellationToken);
        await sut.AppendAsync(category + "x-1", StreamState.NoStream, [Event("placed")], TestContext.Current.CancellationToken);
        var last = await sut.AppendAsync(category + "-2", StreamState.NoStream, [Event("placed")], TestContext.Current.CancellationToken);
        var head = await ReachAsync(last.Position);

        // Act
        var scanned = await sut.ScanAllAsync(EventFilter.StreamName(category + "-"), Direction.Forwards, first.Position, head, 1000, 10, TestContext.Current.CancellationToken);

        // Assert
        scanned.Events.Select(record => record.Stream).ShouldBe([category + "-1", category + "-2"]);
        scanned.Through.ShouldBe(head);
    }

    [Fact]
    public async Task ScanAll_WithAPrefixThatLooksLikeAWildcard_ShouldMatchItLiterally()
    {
        // Arrange: an underscore in the prefix, which LIKE reads as any one character unless escaped.
        var stem = "flt" + Suffix();
        var sut = database.Store;
        var literal = await sut.AppendAsync(stem + "_a-1", StreamState.NoStream, [Event("placed")], TestContext.Current.CancellationToken);
        var lookalike = await sut.AppendAsync(stem + "xa-1", StreamState.NoStream, [Event("placed")], TestContext.Current.CancellationToken);
        var head = await ReachAsync(lookalike.Position);

        // Act
        var scanned = await sut.ScanAllAsync(EventFilter.StreamName(stem + "_"), Direction.Forwards, literal.Position, head, 1000, 10, TestContext.Current.CancellationToken);

        // Assert
        scanned.Events.ShouldHaveSingleItem().Stream.ShouldBe(stem + "_a-1");
    }

    [Fact]
    public async Task ScanAll_ByTypePrefixByRegularExpressionAndBackwards_ShouldKeepWhatMatchesInTheReadingDirection()
    {
        // Arrange
        var suffix = Suffix();
        var sut = database.Store;
        var a = await sut.AppendAsync("flt" + suffix + "-1", StreamState.NoStream, [Event("placed_" + suffix), Event("paid_" + suffix)], TestContext.Current.CancellationToken);
        var b = await sut.AppendAsync("flt" + suffix + "-2", StreamState.NoStream, [Event("placed_" + suffix)], TestContext.Current.CancellationToken);
        var head = await ReachAsync(b.Position);
        var from = a.Position - 1;

        // Act
        var byType = await sut.ScanAllAsync(EventFilter.EventType("placed_" + suffix), Direction.Forwards, from, head, 1000, 10, TestContext.Current.CancellationToken);
        var byRegex = await sut.ScanAllAsync(EventFilter.EventTypeMatches("^pa(id|ssed)_" + suffix + "$"), Direction.Forwards, from, head, 1000, 10, TestContext.Current.CancellationToken);
        var backwards = await sut.ScanAllAsync(EventFilter.StreamName("flt" + suffix + "-"), Direction.Backwards, head, head, (int)(head - from + 1), 10, TestContext.Current.CancellationToken);

        // Assert
        byType.Events.Select(record => record.Position).ShouldBe([a.Position - 1, b.Position]);
        byRegex.Events.ShouldHaveSingleItem().Position.ShouldBe(a.Position);
        backwards.Events.Select(record => record.Position).ShouldBe([b.Position, a.Position, a.Position - 1]);
        backwards.Through.ShouldBe(from);
    }

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private static EventData Event(string type) => new(Guid.NewGuid(), type, Encoding.UTF8.GetBytes("{\"orderId\":1}"));

    /// <summary>Waits for the tail to reach a position, so a scan bounded by the head sees the event.</summary>
    private async Task<long> ReachAsync(long position)
    {
        var tail = database.Services.GetRequiredService<IStoreTail>();
        var head = await tail.RefreshAsync(TestContext.Current.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (head < position)
        {
            head = await tail.WaitForAdvanceAsync(head, timeout.Token);
        }

        return head;
    }
}
