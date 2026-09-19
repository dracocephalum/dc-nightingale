using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests;

public sealed class StoreMarkerTests
{
    private static readonly DateTimeOffset Created = new(2026, 9, 19, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DifferencesFrom_WhenSettingsMatch_ShouldBeEmpty()
    {
        // Arrange
        var sut = Marker();
        var settings = new NightingaleOptions.StoreSettings();

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
        var settings = new NightingaleOptions.StoreSettings { Collation = null };

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
        var settings = new NightingaleOptions.StoreSettings { Collation = "latin1_general_100_bin2" };

        // Act
        var differences = sut.DifferencesFrom(settings);

        // Assert
        differences.ShouldBeEmpty();
    }

    [Fact]
    public void DifferencesFrom_WhenPartitioningChanged_ShouldNameBothModes()
    {
        // Arrange
        var sut = Marker(partitioning: NightingaleOptions.StoreSettings.PartitioningMode.Tenant);
        var settings = new NightingaleOptions.StoreSettings { Partitioning = NightingaleOptions.StoreSettings.PartitioningMode.ArchivedStream };

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
        var settings = new NightingaleOptions.StoreSettings { Collation = "Latin1_General_100_BIN2" };

        // Act
        var differences = sut.DifferencesFrom(settings);

        // Assert
        differences.ShouldHaveSingleItem().ShouldContain("the database's collation is SQL_Latin1_General_CP1_CI_AS");
    }

    [Fact]
    public void DifferencesFrom_WhenEverythingDiffers_ShouldListEveryDifference()
    {
        // Arrange
        var sut = Marker(schemaVersion: StoreMarker.CurrentSchemaVersion + 1, partitioning: NightingaleOptions.StoreSettings.PartitioningMode.ArchivedStream, collation: "SQL_Latin1_General_CP1_CI_AS");
        var settings = new NightingaleOptions.StoreSettings { Partitioning = NightingaleOptions.StoreSettings.PartitioningMode.None, Collation = "Latin1_General_100_BIN2" };

        // Act
        var differences = sut.DifferencesFrom(settings);

        // Assert
        differences.Count.ShouldBe(3);
        differences[0].ShouldContain("newer than");
        differences[1].ShouldContain("Partitioning");
        differences[2].ShouldContain("Collation");
    }

    [Fact]
    public void DifferencesFrom_WhenStoreIsOlderThanTheServer_ShouldNotBeADifference()
    {
        // Arrange: an older schema is the migration's business, not a settings conflict.
        var sut = Marker(schemaVersion: 0);

        // Act
        var differences = sut.DifferencesFrom(new NightingaleOptions.StoreSettings());

        // Assert
        differences.ShouldBeEmpty();
    }

    [Fact]
    public void DifferencesFrom_WhenOrdinalsWereTurnedOnSince_ShouldSayThereIsNoBackfill()
    {
        // Arrange
        var sut = Marker(ordinals: false);
        var settings = new NightingaleOptions.StoreSettings { Ordinals = true };

        // Act
        var differences = sut.DifferencesFrom(settings);

        // Assert
        differences.ShouldHaveSingleItem().ShouldContain("initialized without ordinals");
    }

    [Fact]
    public void DifferencesFrom_WhenOrdinalsWereTurnedOffSince_ShouldRefuse()
    {
        // Arrange
        var sut = Marker(ordinals: true);
        var settings = new NightingaleOptions.StoreSettings { Ordinals = false };

        // Act
        var differences = sut.DifferencesFrom(settings);

        // Assert
        differences.ShouldHaveSingleItem().ShouldContain("initialized with ordinals");
    }

    private static StoreMarker Marker(int schemaVersion = StoreMarker.CurrentSchemaVersion, NightingaleOptions.StoreSettings.PartitioningMode partitioning = NightingaleOptions.StoreSettings.PartitioningMode.None, string collation = "SQL_Latin1_General_CP1_CI_AS", bool ordinals = false) =>
        new(schemaVersion, partitioning, ordinals, collation, Created, "1.0.0+abcdef12");
}
