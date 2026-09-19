using Shouldly;

namespace Dracocephalum.Nightingale.Examples.CategoryStream.Tests;

/// <summary>
/// The sample as an integration test: the same scenario the program runs, against the local SQL
/// Server named by <c>NIGHTINGALE_SQLSERVER</c> or the local default, asserting the report.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ScenarioTests
{
    [Fact]
    public async Task RunAsync_ShouldReadAndFollowTheCategoryAcrossItsStreams()
    {
        // Arrange
        var master = Scenario.ResolveMasterConnectionString();

        // Act
        var report = await Scenario.RunAsync(master, TextWriter.Null, TestContext.Current.CancellationToken);

        // Assert: positions in a fresh database are 1 to 4; the category's own bounds are 1 and 4,
        // and the subscription confirmed at 4 sees only the event at 6, never shipments' 5.
        report.Category.ShouldBe(["orders-1:order_placed", "orders-1:order_paid", "orders-2:order_placed"]);
        report.CategoryHead.ShouldBe(new StreamHead(1, 4));
        report.ByType.ShouldBe(["orders-1", "orders-2"]);
        report.ConfirmedHead.ShouldBe(4);
        report.Live.ShouldBe(["orders-3:order_placed"]);
    }
}
