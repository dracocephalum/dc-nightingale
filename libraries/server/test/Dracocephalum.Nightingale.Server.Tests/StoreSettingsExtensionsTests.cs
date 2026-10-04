using Shouldly;

namespace Dracocephalum.Nightingale.Server.Tests;

/// <summary>
/// The comparison between what a store was initialized with and what a host is configured with.
/// It is written against the interface, so the two sides here are different types on purpose.
/// </summary>
public sealed class StoreSettingsExtensionsTests
{
    [Fact]
    public void DifferencesFrom_WhenSettingsMatch_ShouldBeEmpty()
    {
        // Arrange
        var sut = Marker();
        var settings = new Configured();

        // Act
        var differences = sut.DifferencesFrom(settings);

        // Assert
        differences.ShouldBeEmpty();
    }

    [Fact]
    public void DifferencesFrom_WhenCollationIsNotConfigured_ShouldAcceptWhateverTheDatabaseHas()
    {
        // Arrange
        var sut = Marker(collation: "Latin1_General_100_BIN2");
        var settings = new Configured { Collation = null };

        // Act
        var differences = sut.DifferencesFrom(settings);

        // Assert
        differences.ShouldBeEmpty();
    }

    [Fact]
    public void DifferencesFrom_WhenConfiguredCollationDiffersOnlyInCase_ShouldBeEmpty()
    {
        // Arrange
        var sut = Marker(collation: "Latin1_General_100_BIN2");
        var settings = new Configured { Collation = "latin1_general_100_bin2" };

        // Act
        var differences = sut.DifferencesFrom(settings);

        // Assert
        differences.ShouldBeEmpty();
    }

    [Fact]
    public void DifferencesFrom_WhenPartitioningChanged_ShouldNameBothModes()
    {
        // Arrange
        var sut = Marker(partitioning: PartitioningMode.Tenant);
        var settings = new Configured { Partitioning = PartitioningMode.ArchivedStream };

        // Act
        var differences = sut.DifferencesFrom(settings);

        // Assert
        differences.ShouldHaveSingleItem().ShouldContain("Partitioning is ArchivedStream but the store was initialized with Tenant");
    }

    [Fact]
    public void DifferencesFrom_WhenConfiguredCollationDiffers_ShouldSayWhatTheDatabaseHas()
    {
        // Arrange
        var sut = Marker(collation: "SQL_Latin1_General_CP1_CI_AS");
        var settings = new Configured { Collation = "Latin1_General_100_BIN2" };

        // Act
        var differences = sut.DifferencesFrom(settings);

        // Assert
        differences.ShouldHaveSingleItem().ShouldContain("the database's collation is SQL_Latin1_General_CP1_CI_AS");
    }

    [Fact]
    public void DifferencesFrom_WhenEverythingDiffers_ShouldListEveryDifference()
    {
        // Arrange
        var sut = Marker(partitioning: PartitioningMode.ArchivedStream, collation: "SQL_Latin1_General_CP1_CI_AS");
        var settings = new Configured { Partitioning = PartitioningMode.None, Collation = "Latin1_General_100_BIN2" };

        // Act
        var differences = sut.DifferencesFrom(settings);

        // Assert
        differences.Count.ShouldBe(2);
        differences[0].ShouldContain("Partitioning");
        differences[1].ShouldContain("Collation");
    }

    [Fact]
    public void DifferencesFrom_WhenOrdinalsWereTurnedOnSince_ShouldSayThereIsNoBackfill()
    {
        // Arrange
        var sut = Marker(assignOrdinals: false);
        var settings = new Configured { AssignOrdinals = true };

        // Act
        var differences = sut.DifferencesFrom(settings);

        // Assert
        differences.ShouldHaveSingleItem().ShouldContain("initialized without ordinals");
    }

    [Fact]
    public void DifferencesFrom_WhenOrdinalsWereTurnedOffSince_ShouldRefuse()
    {
        // Arrange
        var sut = Marker(assignOrdinals: true);
        var settings = new Configured { AssignOrdinals = false };

        // Act
        var differences = sut.DifferencesFrom(settings);

        // Assert
        differences.ShouldHaveSingleItem().ShouldContain("initialized with ordinals");
    }

    [Fact]
    public void DifferencesFrom_WhenSchemaChanged_ShouldNameBoth()
    {
        // Arrange
        var sut = Marker(schema: "events");
        var settings = new Configured { Schema = "dbo" };

        // Act
        var differences = sut.DifferencesFrom(settings);

        // Assert
        differences.ShouldHaveSingleItem().ShouldContain("Schema is dbo but the store was initialized in events");
    }

    private static StoreSettings Marker(PartitioningMode partitioning = PartitioningMode.None, string collation = "SQL_Latin1_General_CP1_CI_AS", bool assignOrdinals = false, string schema = "dbo") =>
        new() { Partitioning = partitioning, AssignOrdinals = assignOrdinals, Schema = schema, Collation = collation };

    /// <summary>What a host configures: another type that carries the settings, as a backend's options do.</summary>
    private sealed class Configured : IStoreSettings
    {
        public string Schema { get; init; } = "dbo";

        public string? Collation { get; init; }

        public PartitioningMode Partitioning { get; init; }

        public bool AssignOrdinals { get; init; }
    }
}
