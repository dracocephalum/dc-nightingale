using Dracocephalum.Nightingale.Server.Data;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Tests.Data;

/// <summary>
/// The store's settings as rows and back: written one row per value under the value's own name,
/// one level and nothing nested, and read by the same binder that reads the configuration.
/// </summary>
public sealed class StorePropertyExtensionsTests
{
    private readonly DbContextOptions<NightingaleDbContext> _options = new DbContextOptionsBuilder<NightingaleDbContext>()
        .UseInMemoryDatabase("settings-" + Guid.NewGuid().ToString("N"))
        .Options;

    [Fact]
    public void Flatten_ShouldNameEachValueAsItsMemberIsWithNoPrefix()
    {
        // Act
        var pairs = StorePropertyExtensions.Flatten(Settings()).ToDictionary(pair => pair.Key, pair => pair.Value);

        // Assert: named as the member is, an enum by its name, a null by no row at all.
        pairs.ShouldBe(new Dictionary<string, string?>
        {
            ["Schema"] = "events",
            ["Collation"] = "Latin1_General_100_BIN2",
            ["Partitioning"] = "ArchivedStream",
            ["AssignOrdinals"] = "true",
        });
        StorePropertyExtensions.Flatten(new StoreSettings()).Select(pair => pair.Key).ShouldNotContain("Collation");
    }

    [Fact]
    public void StoreSettings_ShouldBeSingleValuesSoThatAPropertyIsANameAndAValue()
    {
        // Act: what the store's rows can hold is a text, a number, a flag or one of a few names.
        var notSingleValues = typeof(IStoreSettings).GetProperties()
            .Where(property => (Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType) is var type && !(type == typeof(string) || type.IsPrimitive || type.IsEnum))
            .Select(property => property.Name);

        // Assert: an object or a list would need a row per member under a path; give it a member per value instead.
        notSingleValues.ShouldBeEmpty();
    }

    [Fact]
    public void Flatten_ShouldWriteOnlyWhatTheInterfaceDeclares()
    {
        // Arrange: a host's options carry more than the settings a store is initialized with.
        var settings = new WithMore { Schema = "events", OnlyForTheHost = "not a store setting" };

        // Act
        var names = StorePropertyExtensions.Flatten(settings).Select(pair => pair.Key);

        // Assert
        names.ShouldBe(["Schema", "Partitioning", "AssignOrdinals"], ignoreOrder: true);
    }

    [Fact]
    public async Task WriteStoreSettingsAsync_ThenReadStoreSettingsAsync_ShouldReturnTheSameSettings()
    {
        // Arrange
        var settings = Settings();

        // Act
        await using (var context = Context())
        {
            await context.WriteStoreSettingsAsync(settings, TestContext.Current.CancellationToken);
        }

        StoreSettings read;
        int rows;
        await using (var context = Context())
        {
            read = await context.ReadStoreSettingsAsync<StoreSettings>(TestContext.Current.CancellationToken);
            rows = await context.StoreProperties.CountAsync(TestContext.Current.CancellationToken);
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
    public async Task WriteStorePropertyAsync_ShouldWriteOneRowReplaceItsValueAndLeaveTheOthers()
    {
        // Arrange
        await using (var context = Context())
        {
            await context.WriteStoreSettingsAsync(Settings(), TestContext.Current.CancellationToken);
            await context.WriteStorePropertyAsync("CreatedBy", "1.0.0", TestContext.Current.CancellationToken);
        }

        // Act: the same row written again, as an upgrade records what it applied.
        await using (var context = Context())
        {
            await context.WriteStorePropertyAsync("CreatedBy", "2.0.0", TestContext.Current.CancellationToken);
        }

        // Assert
        await using var check = Context();
        (await check.ReadStorePropertyAsync("CreatedBy", TestContext.Current.CancellationToken)).ShouldBe("2.0.0");
        (await check.ReadStorePropertyAsync("NeverWritten", TestContext.Current.CancellationToken)).ShouldBeNull();
        (await check.ReadStoreSettingsAsync<StoreSettings>(TestContext.Current.CancellationToken)).Schema.ShouldBe("events");
        (await check.StoreProperties.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(5);
    }

    [Fact]
    public void Bind_WhenARowIsMissing_ShouldTakeTheDefault()
    {
        // Act: a store initialized before a setting existed has no row for it.
        var read = StorePropertyExtensions.Bind<StoreSettings>([new("Partitioning", "Tenant")]);

        // Assert
        read.Partitioning.ShouldBe(PartitioningMode.Tenant);
        read.AssignOrdinals.ShouldBeFalse();
        read.Schema.ShouldBe("dbo");
    }

    [Fact]
    public void Bind_WhenARowIsUnknownOrNotASetting_ShouldIgnoreIt()
    {
        // Act: a row a newer server wrote, and a row that is no setting at all.
        var read = StorePropertyExtensions.Bind<StoreSettings>([new("Schema", "events"), new("SomethingNewer", "42"), new("CreatedBy", "1.0.0")]);

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
