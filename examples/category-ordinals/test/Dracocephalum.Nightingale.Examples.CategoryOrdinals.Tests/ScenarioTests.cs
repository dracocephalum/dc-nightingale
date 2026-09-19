using Dracocephalum.Nightingale.Examples.Common;
using Shouldly;

namespace Dracocephalum.Nightingale.Examples.CategoryOrdinals.Tests;

/// <summary>
/// The sample as an integration test: the same scenario the program runs, against the local SQL
/// Server named by <c>NIGHTINGALE_SQLSERVER</c> or the local default, asserting the report.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ScenarioTests
{
    [Fact]
    public async Task RunAsync_ShouldNumberTheCategoryDenselyAndSkipTheHolesADeleteLeaves()
    {
        // Arrange
        var master = ExampleEnvironment.ResolveMasterConnectionString();

        // Act
        var report = await Scenario.RunAsync(master, TextWriter.Null, TestContext.Current.CancellationToken);

        // Assert: positions in a fresh database are 1 to 4, the orders at 1, 2 and 4; their
        // ordinals are 0, 1 and 2. Deleting orders-1 vacates 0 and 1, orders-3 takes 3, and the
        // bounds keep counting from 0 because the archived rows keep their numbers.
        report.Ordinals.ShouldBe([(0L, 1L), (1L, 2L), (2L, 4L)]);
        report.OrdinalHead.ShouldBe(new StreamHead(0, 2));
        report.ByType.ShouldBe([0L, 1L]);
        report.ConfirmedHead.ShouldBe(2);
        report.LiveOrdinal.ShouldBe(3);
        report.AfterDelete.ShouldBe([2L, 3L]);
        report.AfterDeleteHead.ShouldBe(new StreamHead(0, 3));
    }
}
