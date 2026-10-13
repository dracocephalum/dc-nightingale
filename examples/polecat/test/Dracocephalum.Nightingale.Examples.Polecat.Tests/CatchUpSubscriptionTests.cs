using Dracocephalum.Nightingale.Examples.Polecat.CatchUpSubscription;
using Dracocephalum.Nightingale.Examples.Polecat.Common;
using Shouldly;

namespace Dracocephalum.Nightingale.Examples.Polecat.Tests;

/// <summary>
/// The scenario as an integration test: the same code the program runs, against the local SQL
/// Server named by <c>NIGHTINGALE_SQLSERVER</c> or the local default, asserting the report.
/// </summary>
[Collection(SharedRun.Name)]
[Trait("Category", "Integration")]
public sealed class CatchUpSubscriptionTests(SharedRun.Fixture fixture)
{
    [Fact]
    public async Task RunAsync_ShouldCatchUpThenDeliverLiveOnBothSubscriptions()
    {
        // Arrange
        var run = fixture.Run;

        // Act
        var report = await Scenario.RunAsync(run, TextWriter.Null, TestContext.Current.CancellationToken);

        // Assert
        report.ConfirmedHead.ShouldBe(1);
        report.CaughtUp.ShouldBe(["order_placed", "order_paid"]);
        report.CaughtUpHead.ShouldBe(1);
        report.AllConfirmedHead.ShouldBe(report.AppendedPosition);
        report.Live.ShouldBe(["order_shipped"]);
        report.AllLive.ShouldBe([report.Stream + ":order_shipped", "shipments-" + report.Stream["orders-".Length..] + ":shipment_dispatched"]);

        // The filter keeps the three orders and not the shipment; a checkpoint carries the
        // shipment's position, the last examined, past the last order; the group's checkpoint
        // stands there too once the orders are acknowledged.
        report.FilteredCaughtUp.ShouldBe(["order_placed", "order_paid", "order_shipped"]);
        report.FilteredCheckpoint.ShouldNotBeNull().ShouldBe(report.AppendedPosition + 2);
        report.GroupDelivered.ShouldBe(["order_placed", "order_paid", "order_shipped"]);
        report.GroupCheckpoint.ShouldBe(report.FilteredCheckpoint);
    }
}
