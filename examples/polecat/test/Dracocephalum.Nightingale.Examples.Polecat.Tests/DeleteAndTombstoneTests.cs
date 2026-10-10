using Dracocephalum.Nightingale.Client;
using Dracocephalum.Nightingale.Examples.Polecat.Common;
using Dracocephalum.Nightingale.Examples.Polecat.DeleteAndTombstone;
using Shouldly;

namespace Dracocephalum.Nightingale.Examples.Polecat.Tests;

/// <summary>
/// The scenario as an integration test: the same code the program runs, against the local SQL
/// Server named by <c>NIGHTINGALE_SQLSERVER</c> or the local default, asserting the report.
/// </summary>
[Collection(SharedRun.Name)]
[Trait("Category", "Integration")]
public sealed class DeleteAndTombstoneTests(SharedRun.Fixture fixture)
{
    [Fact]
    public async Task RunAsync_ShouldDeleteTombstoneReviveAndBeRefusedByTheDefaultServer()
    {
        // Arrange
        var run = fixture.Run;

        // Act
        var report = await Scenario.RunAsync(run, TextWriter.Null, TestContext.Current.CancellationToken);

        // Assert
        report.CategoryBefore.ShouldBe(3);
        report.ConflictActualRevision.ShouldBe(1);
        report.ReadAfterDelete.ShouldBe(nameof(StreamDeletedException));
        report.CategoryAfterDelete.ShouldBe(1);
        report.AppendAfterDelete.ShouldBe(nameof(StreamDeletedException));
        report.StateAfterTombstone.ShouldBe(ReadState.StreamNotFound);
        report.RevivedRevision.ShouldBe(0);
        report.DefaultServerRefusal.ShouldBe(nameof(DeletionDisabledException));
    }
}
