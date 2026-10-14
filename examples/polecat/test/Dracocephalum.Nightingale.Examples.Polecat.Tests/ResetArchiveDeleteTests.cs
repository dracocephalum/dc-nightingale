using Dracocephalum.Nightingale.Client;
using Dracocephalum.Nightingale.Examples.Polecat.Common;
using Dracocephalum.Nightingale.Examples.Polecat.ResetArchiveDelete;
using Shouldly;

namespace Dracocephalum.Nightingale.Examples.Polecat.Tests;

/// <summary>
/// The scenario as an integration test: the same code the program runs, against the local SQL
/// Server named by <c>NIGHTINGALE_SQLSERVER</c> or the local default, asserting the report.
/// </summary>
[Collection(SharedRun.Name)]
[Trait("Category", "Integration")]
public sealed class ResetArchiveDeleteTests(SharedRun.Fixture fixture)
{
    [Fact]
    public async Task RunAsync_ShouldResetArchiveDeleteReuseAndHaveTheDefaultServerRefuseOnlyTheArchive()
    {
        // Arrange
        var run = fixture.Run;

        // Act
        var report = await Scenario.RunAsync(run, TextWriter.Null, TestContext.Current.CancellationToken);

        // Assert: a reset hides the stream from its reads alone, and the next append continues it.
        report.CategoryBefore.ShouldBe(3);
        report.StateAfterReset.ShouldBe(ReadState.StreamNotFound);
        report.CategoryAfterReset.ShouldBe(3);
        report.ResetReported.ShouldBeTrue();
        report.RecreatedRevision.ShouldBe(2);
        report.ReadAfterRecreation.ShouldBe([2]);

        // Assert: an archive hides it from every read; a delete frees the name.
        report.ConflictActualRevision.ShouldBe(2);
        report.ReadAfterArchive.ShouldBe(nameof(StreamDeletedException));
        report.CategoryAfterArchive.ShouldBe(1);
        report.AppendAfterArchive.ShouldBe(nameof(StreamDeletedException));
        report.StateAfterDelete.ShouldBe(ReadState.StreamNotFound);
        report.ReusedRevision.ShouldBe(0);
        report.DefaultServerRefusal.ShouldBe(nameof(DeletionDisabledException));
        report.DefaultServerReset.ShouldBeTrue();
    }
}
