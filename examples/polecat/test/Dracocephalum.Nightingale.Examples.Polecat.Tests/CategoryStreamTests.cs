using Dracocephalum.Nightingale.Examples.Polecat.CategoryStream;
using Dracocephalum.Nightingale.Examples.Polecat.Common;
using Shouldly;

namespace Dracocephalum.Nightingale.Examples.Polecat.Tests;

/// <summary>
/// The scenario as an integration test: the same code the program runs, against the local SQL
/// Server named by <c>NIGHTINGALE_SQLSERVER</c> or the local default, asserting the report.
/// </summary>
[Collection(OwnDatabase.Name)]
[Trait("Category", "Integration")]
public sealed class CategoryStreamTests
{
    [Fact]
    public async Task RunAsync_ShouldReadAndFollowTheCategoryAcrossItsStreams()
    {
        // Arrange
        var master = ExampleEnvironment.ResolveMasterConnectionString();

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
