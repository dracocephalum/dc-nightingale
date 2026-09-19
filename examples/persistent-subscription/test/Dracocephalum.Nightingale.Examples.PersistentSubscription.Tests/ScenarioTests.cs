using Dracocephalum.Nightingale.Examples.Common;
using Shouldly;

namespace Dracocephalum.Nightingale.Examples.PersistentSubscription.Tests;

/// <summary>
/// The sample as an integration test: the same scenario the program runs, against the local SQL
/// Server named by <c>NIGHTINGALE_SQLSERVER</c> or the local default, asserting the report.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ScenarioTests
{
    [Fact]
    public async Task RunAsync_ShouldConsumeRetryParkReplayAndResume()
    {
        // Arrange
        var master = ExampleEnvironment.ResolveMasterConnectionString();

        // Act
        var report = await Scenario.RunAsync(master, TextWriter.Null, TestContext.Current.CancellationToken);

        // Assert: all three were in flight before the payment was refused, so its redelivery follows the
        // shipment; the payment event was delivered twice, then parked; the checkpoint covers all three
        // revisions since a parked event counts as done; the replayed one comes back first.
        report.Delivered.ShouldBe(["order_placed", "order_paid", "order_shipped", "order_paid"]);
        report.RetryCountOnRedelivery.ShouldBe(1);
        report.Replayed.ShouldBe(1);
        report.CheckpointOnReturn.ShouldBe(2);
        report.ReplayedType.ShouldBe("order_paid");
    }
}
