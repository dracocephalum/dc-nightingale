using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests;

/// <summary>What a schema report says of itself: whether the database is current, and the text an operator reads.</summary>
public sealed class SchemaReportTests
{
    [Fact]
    public void Describe_WhenTheDatabaseDoesNotExist_ShouldSayOnlyThat()
    {
        // Arrange
        var sut = new SchemaReport(false, false, "CREATE TABLE ...", ["0001_Initial"], [], []);

        // Act
        var text = sut.Describe();

        // Assert
        text.ShouldBe("The database does not exist.");
        sut.IsCurrent.ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Describe_WhenTheDatabaseIsCurrent_ShouldSaySoAndMentionAStaleRecordOfTheStore(bool stale)
    {
        // Arrange
        var sut = new SchemaReport(true, true, null, [], [], []) { StoreRecordStale = stale };

        // Act
        var text = sut.Describe();

        // Assert
        sut.IsCurrent.ShouldBeTrue();
        text.ShouldStartWith("The database is current");
        text.Contains("is not this server's", StringComparison.Ordinal).ShouldBe(stale);
    }

    [Theory]
    [InlineData(false, "The database has not been initialized by Nightingale.")]
    [InlineData(true, "An initialization of the database by Nightingale did not finish: the gateway's migrations are applied, and the record of the store is not written. Initialize mode finishes it.")]
    public void Describe_WhenTheDatabaseIsNotInitialized_ShouldTellAnInitializationThatDiedFromOneNeverBegun(bool unfinished, string expected)
    {
        // Arrange
        var sut = new SchemaReport(true, false, null, [], [], []) { InitializationUnfinished = unfinished };

        // Act
        var text = sut.Describe();

        // Assert
        text.ShouldBe(expected);
        sut.IsCurrent.ShouldBeFalse();
    }

    [Fact]
    public void Describe_WhenSeveralPartsAreBehind_ShouldListEachUnderItsOwnHeadingWhatRefusesAStartFirst()
    {
        // Arrange
        var sut = new SchemaReport(
            true,
            true,
            "ALTER TABLE [dbo].[pc_events] ADD [bdata] varbinary(max) NULL;",
            ["0007_Leases", "0008_Tenants"],
            ["0099_FromTheFuture"],
            ["Ordinals: the store has them, the configuration does not."]);

        // Act
        var lines = sut.Describe().Split(Environment.NewLine);

        // Assert
        lines.ShouldBe(
        [
            "Migrations applied to the database that this server does not have; the store is newer than the server:",
            "  0099_FromTheFuture",
            "Settings the store was initialized with that the configuration no longer matches:",
            "  Ordinals: the store has them, the configuration does not.",
            "Migrations of the gateway's tables to apply:",
            "  0007_Leases",
            "  0008_Tenants",
            "Changes to the event store's tables to apply:",
            "ALTER TABLE [dbo].[pc_events] ADD [bdata] varbinary(max) NULL;",
        ]);
        sut.IsCurrent.ShouldBeFalse();
    }

    [Theory]
    [InlineData("store")]
    [InlineData("pending")]
    [InlineData("unknown")]
    [InlineData("settings")]
    public void IsCurrent_WhenAnyOnePartIsBehind_ShouldBeFalse(string part)
    {
        // Arrange
        var sut = new SchemaReport(
            true,
            true,
            part == "store" ? "ALTER TABLE ..." : null,
            part == "pending" ? ["0007_Leases"] : [],
            part == "unknown" ? ["0099_FromTheFuture"] : [],
            part == "settings" ? ["Ordinals"] : []);

        // Act
        var current = sut.IsCurrent;

        // Assert
        current.ShouldBeFalse();
        sut.Describe().ShouldNotStartWith("The database is current");
    }
}
