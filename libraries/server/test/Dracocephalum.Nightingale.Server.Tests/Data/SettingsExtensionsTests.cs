using Dracocephalum.Nightingale.Server.Data;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Tests.Data;

/// <summary>
/// The store's settings as rows and back: written one row per value under the path the setting
/// has in a settings file, read by the same binder that reads the configuration.
/// </summary>
public sealed class SettingsExtensionsTests
{
    private readonly DbContextOptions<NightingaleDbContext> _options = new DbContextOptionsBuilder<NightingaleDbContext>()
        .UseInMemoryDatabase("settings-" + Guid.NewGuid().ToString("N"))
        .Options;

    [Fact]
    public void Flatten_ShouldNameEachValueByItsConfigurationPath()
    {
        // Act
        var pairs = SettingsExtensions.Flatten(Settings()).ToDictionary(pair => pair.Key, pair => pair.Value);

        // Assert: named as in a settings file, an enum by its name, a null by no row at all.
        pairs.ShouldBe(new Dictionary<string, string?>
        {
            ["Store:Schema"] = "events",
            ["Store:Collation"] = "Latin1_General_100_BIN2",
            ["Store:Partitioning"] = "ArchivedStream",
            ["Store:AssignOrdinals"] = "true",
        });
        SettingsExtensions.Flatten(new StoreSettings()).Select(pair => pair.Key).ShouldNotContain("Store:Collation");
    }

    [Fact]
    public void Flatten_ShouldWriteOnlyWhatTheInterfaceDeclares()
    {
        // Arrange: a host's options carry more than the settings a store is initialized with.
        var settings = new WithMore { Schema = "events", OnlyForTheHost = "not a store setting" };

        // Act
        var names = SettingsExtensions.Flatten(settings).Select(pair => pair.Key);

        // Assert
        names.ShouldBe(["Store:Schema", "Store:Partitioning", "Store:AssignOrdinals"], ignoreOrder: true);
    }

    [Fact]
    public async Task WriteSettingsAsync_ThenReadSettingsAsync_ShouldReturnTheSameSettings()
    {
        // Arrange
        var settings = Settings();

        // Act
        await using (var context = Context())
        {
            await context.WriteSettingsAsync(settings, TestContext.Current.CancellationToken);
        }

        StoreSettings read;
        int rows;
        await using (var context = Context())
        {
            read = await context.ReadSettingsAsync<StoreSettings>(TestContext.Current.CancellationToken);
            rows = await context.Settings.CountAsync(TestContext.Current.CancellationToken);
        }

        // Assert
        read.Partitioning.ShouldBe(PartitioningMode.ArchivedStream);
        read.AssignOrdinals.ShouldBeTrue();
        read.Schema.ShouldBe("events");
        read.Collation.ShouldBe("Latin1_General_100_BIN2");
        read.DifferencesFrom(settings).ShouldBeEmpty();
        rows.ShouldBe(4);
    }

    [Fact]
    public async Task WriteSettingAsync_ShouldWriteOneRowReplaceItsValueAndLeaveTheOthers()
    {
        // Arrange
        await using (var context = Context())
        {
            await context.WriteSettingsAsync(Settings(), TestContext.Current.CancellationToken);
            await context.WriteSettingAsync("CreatedBy", "1.0.0", TestContext.Current.CancellationToken);
        }

        // Act: the same row written again, as an upgrade records what it applied.
        await using (var context = Context())
        {
            await context.WriteSettingAsync("CreatedBy", "2.0.0", TestContext.Current.CancellationToken);
        }

        // Assert
        await using var check = Context();
        (await check.ReadSettingAsync("CreatedBy", TestContext.Current.CancellationToken)).ShouldBe("2.0.0");
        (await check.ReadSettingAsync("NeverWritten", TestContext.Current.CancellationToken)).ShouldBeNull();
        (await check.ReadSettingsAsync<StoreSettings>(TestContext.Current.CancellationToken)).Schema.ShouldBe("events");
        (await check.Settings.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(5);
    }

    [Fact]
    public void Bind_WhenARowIsMissing_ShouldTakeTheDefault()
    {
        // Act: a store initialized before a setting existed has no row for it.
        var read = SettingsExtensions.Bind<StoreSettings>([new("Store:Partitioning", "Tenant")]);

        // Assert
        read.Partitioning.ShouldBe(PartitioningMode.Tenant);
        read.AssignOrdinals.ShouldBeFalse();
        read.Schema.ShouldBe("dbo");
    }

    [Fact]
    public void Bind_WhenARowIsUnknownOrNotASetting_ShouldIgnoreIt()
    {
        // Act: a row a newer server wrote, and a row that is no setting at all.
        var read = SettingsExtensions.Bind<StoreSettings>([new("Store:Schema", "events"), new("Store:SomethingNewer", "42"), new("CreatedBy", "1.0.0")]);

        // Assert
        read.Schema.ShouldBe("events");
    }

    private static StoreSettings Settings() =>
        new()
        {
            Partitioning = PartitioningMode.ArchivedStream,
            AssignOrdinals = true,
            Schema = "events",
            Collation = "Latin1_General_100_BIN2",
        };

    private NightingaleDbContext Context() => new(_options, new NightingaleSchema(NightingaleSchema.Default));

    private sealed class WithMore : StoreSettings
    {
        public string? OnlyForTheHost { get; set; }
    }
}
