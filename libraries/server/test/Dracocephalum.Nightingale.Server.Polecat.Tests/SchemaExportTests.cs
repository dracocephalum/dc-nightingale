using Microsoft.Extensions.DependencyInjection;
using Polecat;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests;

/// <summary>
/// The script is built from the definitions alone, so no database is needed to see that the
/// gateway's additions are in it.
/// </summary>
public sealed class SchemaExportTests
{
    [Fact]
    public void TryGetPath_WhenSwitchIsAbsent_ShouldBeFalse()
    {
        // Act
        var present = SchemaExport.TryGetPath(["--urls", "http://localhost:5000"], out var path);

        // Assert
        present.ShouldBeFalse();
        path.ShouldBeEmpty();
    }

    [Fact]
    public void TryGetPath_WhenSwitchNamesAFile_ShouldReturnIt()
    {
        // Act
        var present = SchemaExport.TryGetPath(["--export-schema", "schema.sql"], out var path);

        // Assert
        present.ShouldBeTrue();
        path.ShouldBe("schema.sql");
    }

    [Fact]
    public void TryGetPath_WhenSwitchHasNoFile_ShouldRefuse()
    {
        // Act & Assert
        Should.Throw<ArgumentException>(() => SchemaExport.TryGetPath(["--export-schema"], out _));
    }

    [Fact]
    public async Task ExportAsync_ShouldWriteTheAugmentedSchema()
    {
        // Arrange: the store is registered as a host registers it, against a server nobody connects to.
        var services = new ServiceCollection();
        services.AddNightingalePolecat("Server=example;Database=nightingale;Trusted_Connection=True", options => options.Store.Partitioning = NightingaleOptions.StoreSettings.PartitioningMode.ArchivedStream);
        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IDocumentStore>();
        var path = Path.Combine(Path.GetTempPath(), "nightingale-schema-" + Guid.NewGuid().ToString("N") + ".sql");

        try
        {
            // Act
            await SchemaExport.ExportAsync(store, path, TestContext.Current.CancellationToken);

            // Assert
            var script = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            script.ShouldSatisfyAllConditions(
                text => text.ShouldContain("pc_events"),
                text => text.ShouldContain("category"),
                text => text.ShouldContain("ix_pc_events_category_seq"),
                text => text.ShouldContain("ix_pc_events_type_seq"),
                text => text.ShouldContain("nightingale_store"),
                text => text.ShouldContain("PARTITION"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
