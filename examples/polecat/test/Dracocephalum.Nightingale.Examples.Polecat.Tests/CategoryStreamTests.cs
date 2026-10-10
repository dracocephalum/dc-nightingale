using Dracocephalum.Nightingale.Examples.Polecat.CategoryStream;
using Dracocephalum.Nightingale.Examples.Polecat.Common;
using Shouldly;

namespace Dracocephalum.Nightingale.Examples.Polecat.Tests;

/// <summary>
/// The scenario as an integration test: the same code the program runs, against the local SQL
/// Server named by <c>NIGHTINGALE_SQLSERVER</c> or the local default, asserting the report.
/// </summary>
[Collection(SharedRun.Name)]
[Trait("Category", "Integration")]
public sealed class CategoryStreamTests(SharedRun.Fixture fixture)
{
    [Fact]
    public async Task RunAsync_ShouldReadAndFollowTheCategoryAcrossItsStreams()
    {
        // Arrange
        var run = fixture.Run;

        // Act
        var report = await Scenario.RunAsync(run, TextWriter.Null, TestContext.Current.CancellationToken);

        // Assert: the four events take four positions in a row, and the category's own bounds are
        // the first and the fourth; positions are the store's, shared by every tenant of the run,
        // so they are asserted from one another. The subscription confirmed at the category's
        // head sees only orders-3's event, never the one shipments-1 took before it.
        report.Category.ShouldBe(["orders-1:order_placed", "orders-1:order_paid", "orders-2:order_placed"]);
        (report.CategoryHead.Last - report.CategoryHead.First).ShouldBe(3);
        report.ByType.ShouldBe(["orders-1", "orders-2"]);
        report.ConfirmedHead.ShouldBe(report.CategoryHead.Last);
        report.Live.ShouldBe(["orders-3:order_placed"]);
    }
}
