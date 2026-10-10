using Dracocephalum.Nightingale.Examples.Polecat.CategoryOrdinals;
using Dracocephalum.Nightingale.Examples.Polecat.Common;
using Shouldly;

namespace Dracocephalum.Nightingale.Examples.Polecat.Tests;

/// <summary>
/// The scenario as an integration test: the same code the program runs, against the local SQL
/// Server named by <c>NIGHTINGALE_SQLSERVER</c> or the local default, asserting the report.
/// </summary>
[Collection(SharedRun.Name)]
[Trait("Category", "Integration")]
public sealed class CategoryOrdinalsTests(SharedRun.Fixture fixture)
{
    [Fact]
    public async Task RunAsync_ShouldNumberTheCategoryDenselyAndSkipTheHolesADeleteLeaves()
    {
        // Arrange
        var run = fixture.Run;

        // Act
        var report = await Scenario.RunAsync(run, TextWriter.Null, TestContext.Current.CancellationToken);

        // Assert: the four events take four positions in a row, the orders the first, second and
        // fourth; positions are the store's, shared by every tenant of the run, so they are
        // asserted from the first one. The ordinals are the tenant's own: 0, 1 and 2. Deleting
        // orders-1 vacates 0 and 1, orders-3 takes 3, and the bounds keep counting from 0 because
        // the archived rows keep their numbers.
        report.Ordinals.Select(pair => pair.Ordinal).ShouldBe([0L, 1L, 2L]);
        report.Ordinals.Select(pair => pair.Position - report.Ordinals[0].Position).ShouldBe([0L, 1L, 3L]);
        report.OrdinalHead.ShouldBe(new StreamHead(0, 2));
        report.ByType.ShouldBe([0L, 1L]);
        report.ConfirmedHead.ShouldBe(2);
        report.LiveOrdinal.ShouldBe(3);
        report.AfterDelete.ShouldBe([2L, 3L]);
        report.AfterDeleteHead.ShouldBe(new StreamHead(0, 3));
        report.GroupOrdinals.ShouldBe([2L, 3L]);
        report.GroupCheckpoint.ShouldBe(3);
    }
}
