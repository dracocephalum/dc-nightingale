using Dracocephalum.Nightingale.Examples.Common;
using Shouldly;

namespace Dracocephalum.Nightingale.Examples.CatchUpSubscription.Tests;

/// <summary>
/// The sample as an integration test: the same scenario the program runs, against the local SQL
/// Server named by <c>NIGHTINGALE_SQLSERVER</c> or the local default, asserting the report.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ScenarioTests
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
