using Dracocephalum.Nightingale.Examples.Polecat.ClusterRedirect;
using Dracocephalum.Nightingale.Examples.Polecat.Common;
using Shouldly;

namespace Dracocephalum.Nightingale.Examples.Polecat.Tests;

/// <summary>
/// The scenario as an integration test: the same code the program runs, against the local SQL
/// Server named by <c>NIGHTINGALE_SQLSERVER</c> or the local default, asserting the report.
/// </summary>
[Collection(OwnDatabase.Name)]
[Trait("Category", "Integration")]
public sealed class ClusterRedirectTests
{
    [Fact]
    public async Task RunAsync_ShouldSendTheClientToTheInstanceThatRunsTheGroup()
    {
        // Arrange
        var master = ExampleEnvironment.ResolveMasterConnectionString();

        // Act
        var report = await Scenario.RunAsync(master, TextWriter.Null, TestContext.Current.CancellationToken);

        // Assert: the second consumer was refused by the owner, not told to go elsewhere, and
        // the replay asked of the other instance reached the consumer.
        report.Delivered.ShouldBe(["order_placed", "order_paid"]);
        report.SecondConsumerRefusal.ShouldBe("ConsumerLimitReachedException");
        report.Replayed.ShouldBe(1);
        report.ReplayedType.ShouldBe("order_placed");
        report.ReadInRotation.ShouldBe(4);
    }
}
