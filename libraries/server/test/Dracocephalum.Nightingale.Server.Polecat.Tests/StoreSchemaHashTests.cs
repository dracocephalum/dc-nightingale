using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests;

/// <summary>
/// The hash a fast boot trusts is built from the definitions alone, so no database is needed to
/// see that it is the same for the same store and another for a store shaped differently.
/// </summary>
public sealed class StoreSchemaHashTests
{
    private const string ConnectionString = "Server=example;Database=nightingale;Trusted_Connection=True";

    [Fact]
    public async Task ComputeStoreHashAsync_ForTheSameConfiguration_ShouldBeTheSameFromOneHostToTheNext()
    {
        // Act
        var first = await HashAsync();
        var second = await HashAsync();

        // Assert
        first.ShouldNotBeNullOrWhiteSpace();
        second.ShouldBe(first);
    }

    [Theory]
    [InlineData(PartitioningMode.Tenant)]
    [InlineData(PartitioningMode.ArchivedStream)]
    public async Task ComputeStoreHashAsync_WhenASettingShapesTheTablesDifferently_ShouldDiffer(PartitioningMode partitioning)
    {
        // Act
        var plain = await HashAsync();
        var partitioned = await HashAsync(options => options.Store.Partitioning = partitioning);

        // Assert
        partitioned.ShouldNotBe(plain);
    }

    [Fact]
    public async Task ComputeStoreHashAsync_WhenTheStoreIsInAnotherSchema_ShouldDiffer()
    {
        // Act
        var plain = await HashAsync();
        var elsewhere = await HashAsync(options => options.Store.Schema = "events");

        // Assert
        elsewhere.ShouldNotBe(plain);
    }

    private static async Task<string> HashAsync(Action<NightingaleOptions>? configure = null)
    {
        // The store is registered as a host registers it, against a server nobody connects to.
        var services = new ServiceCollection();
        services.AddNightingalePolecat(ConnectionString, configure);
        await using var provider = services.BuildServiceProvider();
        return await provider.GetRequiredService<StoreSchema>().ComputeStoreHashAsync(TestContext.Current.CancellationToken);
    }
}
