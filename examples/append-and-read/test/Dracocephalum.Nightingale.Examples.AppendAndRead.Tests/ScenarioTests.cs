using Dracocephalum.Nightingale.Client;
using Dracocephalum.Nightingale.Examples.Common;
using Shouldly;

namespace Dracocephalum.Nightingale.Examples.AppendAndRead.Tests;

/// <summary>
/// The sample as an integration test: the same scenario the program runs, against the local SQL
/// Server named by <c>NIGHTINGALE_SQLSERVER</c> or the local default, asserting the report.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ScenarioTests
{
    [Fact]
    public async Task RunAsync_ShouldCompleteEveryStepEndToEnd()
    {
        // Arrange
        var master = ExampleEnvironment.ResolveMasterConnectionString();

        // Act
        var report = await Scenario.RunAsync(master, TextWriter.Null, TestContext.Current.CancellationToken);

        // Assert
        report.AppendedRevision.ShouldBe(2);
        report.Head.ShouldBe(new StreamHead(0, 2));
        report.ReadForwards.ShouldBe(["order_placed", "order_paid", "order_shipped"]);
        report.ReadBackwards.ShouldBe(["order_shipped", "order_paid"]);
        report.CorrelationId.ShouldBe("checkout-1");
        report.ConflictActualRevision.ShouldBe(2);
        report.RetriedRevision.ShouldBe(2);
        report.MissingStreamState.ShouldBe(ReadState.StreamNotFound);
    }
}
