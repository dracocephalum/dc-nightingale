using Dracocephalum.Nightingale.Examples.Polecat.Common;
using Dracocephalum.Nightingale.Examples.Polecat.PersistentSubscription;
using Shouldly;

namespace Dracocephalum.Nightingale.Examples.Polecat.Tests;

/// <summary>
/// The scenario as an integration test: the same code the program runs, against the local SQL
/// Server named by <c>NIGHTINGALE_SQLSERVER</c> or the local default, asserting the report.
/// </summary>
[Collection(OwnDatabase.Name)]
[Trait("Category", "Integration")]
public sealed class PersistentSubscriptionTests
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

        // While the second consumer waited: the group ran, at the address the client used, with
        // its checkpoint at the stream's last revision, one message parked and nothing in flight.
        report.Info.Running.ShouldBeTrue();
        report.Info.OwnerAddress.ShouldNotBeNull();
        report.Info.Checkpoint.ShouldBe(2);
        report.Info.LastKnownPosition.ShouldBe(2);
        report.Info.ParkedCount.ShouldBe(1);
        report.Info.OutboxCount.ShouldBe(0);
        report.Info.Settings.MaxRetryCount.ShouldBe(1);
        report.Info.Live.ShouldNotBeNull().InFlightCount.ShouldBe(0);
        report.Listed.ShouldBe(1);
    }
}
