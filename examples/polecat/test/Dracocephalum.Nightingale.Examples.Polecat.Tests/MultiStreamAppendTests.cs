using Dracocephalum.Nightingale.Examples.Polecat.Common;
using Dracocephalum.Nightingale.Examples.Polecat.MultiStreamAppend;
using Shouldly;

namespace Dracocephalum.Nightingale.Examples.Polecat.Tests;

/// <summary>
/// The scenario as an integration test: the same code the program runs, against the local SQL
/// Server named by <c>NIGHTINGALE_SQLSERVER</c> or the local default, asserting the report.
/// </summary>
[Collection(SharedRun.Name)]
[Trait("Category", "Integration")]
public sealed class MultiStreamAppendTests(SharedRun.Fixture fixture)
{
    [Fact]
    public async Task RunAsync_ShouldCommitTheTransferWholeAndReportEveryConditionThatFails()
    {
        // Arrange
        var run = fixture.Run;

        // Act
        var report = await Scenario.RunAsync(run, TextWriter.Null, TestContext.Current.CancellationToken);

        // Assert: one commit for both accounts, positioned in the order given.
        report.Opened.ShouldBe(["account-alice:0", "account-bob:0"]);
        report.TransferInOrder.ShouldBeTrue();

        // Assert: every stream that moved on, then the check, numbered after the appends.
        report.StaleConflicts.ShouldBe(["0:account-alice", "1:account-bob"]);
        report.FrozenConflict.ShouldBe("2:freeze-alice");

        // Assert: a stream and its own metadata in one commit; a reset refused.
        report.WithMetadata.ShouldBe(["account-carol:0", "$$account-carol:0"]);
        report.MaxCount.ShouldBe(1000);
        report.ResetRefusal.ShouldContain("a reset is its own call");
    }
}
