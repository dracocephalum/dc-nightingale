using Dracocephalum.Nightingale.Examples.Polecat.CatchUpSubscription;
using Dracocephalum.Nightingale.Examples.Polecat.Common;
using Shouldly;

namespace Dracocephalum.Nightingale.Examples.Polecat.Tests;

/// <summary>
/// The scenario as an integration test: the same code the program runs, against the local SQL
/// Server named by <c>NIGHTINGALE_SQLSERVER</c> or the local default, asserting the report.
/// </summary>
[Collection(OwnDatabase.Name)]
[Trait("Category", "Integration")]
public sealed class CatchUpSubscriptionTests
{
    [Fact]
    public async Task RunAsync_ShouldCatchUpThenDeliverLiveOnBothSubscriptions()
    {
        // Arrange
        var master = ExampleEnvironment.ResolveMasterConnectionString();

        // Act
        var report = await Scenario.RunAsync(master, TextWriter.Null, TestContext.Current.CancellationToken);

        // Assert
        report.ConfirmedHead.ShouldBe(1);
        report.CaughtUp.ShouldBe(["order_placed", "order_paid"]);
        report.CaughtUpHead.ShouldBe(1);
        report.AllConfirmedHead.ShouldBe(2);
        report.Live.ShouldBe(["order_shipped"]);
        report.AllLive.ShouldBe([report.Stream + ":order_shipped", "shipments-" + report.Stream["orders-".Length..] + ":shipment_dispatched"]);
    }
}
