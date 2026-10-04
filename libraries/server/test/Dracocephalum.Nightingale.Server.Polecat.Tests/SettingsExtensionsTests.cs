using Dracocephalum.Nightingale.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests;

/// <summary>
/// What a store was initialized with, as rows and back: written one row per value, read by the
/// same binder that reads the configuration, into the same settings type.
/// </summary>
public sealed class SettingsExtensionsTests
{
    private static readonly DateTimeOffset Created = new(2026, 9, 19, 8, 0, 0, TimeSpan.Zero);

    private readonly DbContextOptions<NightingaleDbContext> _options = new DbContextOptionsBuilder<NightingaleDbContext>()
        .UseInMemoryDatabase("settings-" + Guid.NewGuid().ToString("N"))
        .Options;

    [Fact]
    public void Flatten_ShouldNameEachValueByItsConfigurationPath()
    {
        // Arrange
        var settings = Settings();

        // Act
        var pairs = SettingsExtensions.Flatten(settings).ToDictionary(pair => pair.Key, pair => pair.Value);

        // Assert: an enum by its name, a null by no row at all.
        pairs["Store:Partitioning"].ShouldBe("ArchivedStream");
        pairs["Store:AssignOrdinals"].ShouldBe("true");
        pairs["Store:Schema"].ShouldBe("events");
        pairs["Store:Collation"].ShouldBe("Latin1_General_100_BIN2");
        pairs["CreatedBy"].ShouldBe("1.0.0+abcdef12");
        pairs.ShouldContainKey("CreatedAt");
        SettingsExtensions.Flatten(new StoredSettings()).Select(pair => pair.Key).ShouldNotContain("Store:Collation");
    }

    [Fact]
    public async Task WriteAsync_ThenReadAsync_ShouldReturnTheSameSettings()
    {
        // Arrange
        var settings = Settings();

        // Act
        await using (var context = Context())
        {
            await context.WriteSettingsAsync(settings, TestContext.Current.CancellationToken);
        }

        StoredSettings read;
        int rows;
        await using (var context = Context())
        {
            read = await context.ReadSettingsAsync<StoredSettings>(TestContext.Current.CancellationToken);
            rows = await context.Settings.CountAsync(TestContext.Current.CancellationToken);
        }

        // Assert
        read.Store.Partitioning.ShouldBe(NightingaleOptions.StoreSettings.PartitioningMode.ArchivedStream);
        read.Store.AssignOrdinals.ShouldBeTrue();
        read.Store.Schema.ShouldBe("events");
        read.Store.Collation.ShouldBe("Latin1_General_100_BIN2");
        read.CreatedAt.ShouldBe(Created);
        read.CreatedBy.ShouldBe("1.0.0+abcdef12");
        read.DifferencesFrom(settings.Store).ShouldBeEmpty();
        rows.ShouldBe(7);
    }

    [Fact]
    public async Task WriteAsync_WhenARowExists_ShouldReplaceItsValueAndLeaveTheOthers()
    {
        // Arrange
        await using (var context = Context())
        {
            await context.WriteSettingsAsync(Settings(), TestContext.Current.CancellationToken);
        }

        // Act: one value written on its own, as an upgrade records the library it applied.
        await using (var context = Context())
        {
            await context.WriteSettingAsync(nameof(StoredSettings.StoreLibrary), "9.9.9", TestContext.Current.CancellationToken);
        }

        // Assert
        await using var check = Context();
        var read = await check.ReadSettingsAsync<StoredSettings>(TestContext.Current.CancellationToken);
        read.StoreLibrary.ShouldBe("9.9.9");
        read.Store.Schema.ShouldBe("events");
        (await check.Settings.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(7);
    }

    [Fact]
    public void Bind_WhenARowIsMissing_ShouldTakeTheDefault()
    {
        // Act: a store initialized before a setting existed has no row for it.
        var read = SettingsExtensions.Bind<StoredSettings>([new("Store:Partitioning", "Tenant")]);

        // Assert
        read.Store.Partitioning.ShouldBe(NightingaleOptions.StoreSettings.PartitioningMode.Tenant);
        read.Store.AssignOrdinals.ShouldBeFalse();
        read.Store.Schema.ShouldBe("dbo");
    }

    [Fact]
    public void Bind_WhenARowIsUnknown_ShouldIgnoreIt()
    {
        // Act: a store initialized by a newer server has a row this one has no setting for.
        var read = SettingsExtensions.Bind<StoredSettings>([new("Store:Schema", "events"), new("Store:SomethingNewer", "42")]);

        // Assert
        read.Store.Schema.ShouldBe("events");
    }

    private static StoredSettings Settings() =>
        new()
        {
            Store = new NightingaleOptions.StoreSettings
            {
                Partitioning = NightingaleOptions.StoreSettings.PartitioningMode.ArchivedStream,
                AssignOrdinals = true,
                Schema = "events",
                Collation = "Latin1_General_100_BIN2",
            },
            CreatedAt = Created,
            CreatedBy = "1.0.0+abcdef12",
            StoreLibrary = "5.29.0",
        };

    private NightingaleDbContext Context() => new(_options, new NightingaleSchema(NightingaleSchema.Default));
}
