using Dracocephalum.Nightingale.Client;
using Dracocephalum.Nightingale.Examples.Polecat.Common;
using Dracocephalum.Nightingale.Examples.Polecat.StreamMetadata;
using Shouldly;

namespace Dracocephalum.Nightingale.Examples.Polecat.Tests;

/// <summary>
/// The scenario as an integration test: the same code the program runs, against the local SQL
/// Server named by <c>NIGHTINGALE_SQLSERVER</c> or the local default, asserting the report.
/// </summary>
[Collection(SharedRun.Name)]
[Trait("Category", "Integration")]
public sealed class StreamMetadataTests(SharedRun.Fixture fixture)
{
    [Fact]
    public async Task RunAsync_ShouldSetReadBackRaceAndApplyTheMetadataAndKeepItOutOfTheDataEvents()
    {
        // Arrange
        var run = fixture.Run;

        // Act
        var report = await Scenario.RunAsync(run, TextWriter.Null, TestContext.Current.CancellationToken);

        // Assert: the tenant holds two events of orders-1 and two metadata events.
        report.FirstSet.ShouldBe(0);
        report.ReadBeforeAppend.ShouldBe(ReadState.StreamNotFound);
        report.ConflictActualRevision.ShouldBe(0);
        report.Final.Revision.ShouldBe(1);
        report.Final.Metadata.MaxAge.ShouldBe(TimeSpan.FromDays(30));
        report.Final.Metadata.MaxCount.ShouldBe(10);
        report.Final.Metadata.Custom.ShouldNotBeNull()["owner"].ShouldNotBeNull().GetValue<string>().ShouldBe("billing");
        report.History.ShouldBe([StreamNames.MetadataEventType, StreamNames.MetadataEventType]);
        report.AclRefusal.ShouldBe(nameof(ArgumentException));
        report.All.ShouldBe(4);
        report.DataEvents.ShouldBe(2);
        report.MetadataEvents.ShouldBe(2);

        // Assert: orders-2's five events read as its last two; the category holds all seven.
        report.Kept.ShouldBe([3, 4]);
        report.Category.ShouldBe(7);
    }
}
