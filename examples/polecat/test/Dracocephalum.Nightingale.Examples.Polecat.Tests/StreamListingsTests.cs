using Dracocephalum.Nightingale.Examples.Polecat.Common;
using Dracocephalum.Nightingale.Examples.Polecat.StreamListings;
using Shouldly;

namespace Dracocephalum.Nightingale.Examples.Polecat.Tests;

/// <summary>
/// The scenario as an integration test: the same code the program runs, against the local SQL
/// Server named by <c>NIGHTINGALE_SQLSERVER</c> or the local default, asserting the report.
/// </summary>
[Collection(SharedRun.Name)]
[Trait("Category", "Integration")]
public sealed class StreamListingsTests(SharedRun.Fixture fixture)
{
    [Fact]
    public async Task RunAsync_ShouldReadTheWorkflowTheListingsTheCategoryAndTheExactFilters()
    {
        // Arrange
        var run = fixture.Run;

        // Act
        var report = await Scenario.RunAsync(run, TextWriter.Null, TestContext.Current.CancellationToken);

        // Assert: the workflow across its streams, and each stream once, by its first event.
        report.Workflow.ShouldBe(["orders-1:order_placed", "payments-1:payment_taken", "orders-1:order_shipped"]);
        report.Streams.ShouldBe(["orders-1", "payments-1", "orders"]);
        report.OrdersStreams.ShouldBe(["orders-1", "orders"]);

        // Assert: a name without a hyphen is its own category; the prefix is a prefix.
        report.OrdersCategory.ShouldBe(["orders-1", "orders-1", "orders", "orders-1"]);
        report.ByCategoryFilter.ShouldBe(["orders-1", "orders-1", "orders", "orders-1"]);
        report.ByPrefixFilter.ShouldBe(["orders-1", "orders-1", "orders-1"]);
        report.ByTypeFilter.ShouldBe(["order_placed"]);
        report.LiveListed.ShouldBe("refunds-1");
    }
}
