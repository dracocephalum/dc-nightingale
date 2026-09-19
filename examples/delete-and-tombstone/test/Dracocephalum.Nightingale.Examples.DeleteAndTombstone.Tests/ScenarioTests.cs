using Dracocephalum.Nightingale.Client;
using Shouldly;

namespace Dracocephalum.Nightingale.Examples.DeleteAndTombstone.Tests;

/// <summary>
/// The sample as an integration test: the same scenario the program runs, against the local SQL
/// Server named by <c>NIGHTINGALE_SQLSERVER</c> or the local default, asserting the report.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ScenarioTests
{
    [Fact]
    public async Task RunAsync_ShouldDeleteTombstoneReviveAndBeRefusedByTheDefaultServer()
    {
        // Arrange
        var master = Scenario.ResolveMasterConnectionString();

        // Act
        var report = await Scenario.RunAsync(master, TextWriter.Null, TestContext.Current.CancellationToken);

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
