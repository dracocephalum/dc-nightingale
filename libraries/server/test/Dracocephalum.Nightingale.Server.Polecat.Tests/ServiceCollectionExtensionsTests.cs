using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests;

/// <summary>
/// The registration reads configuration only; nothing here opens a connection.
/// </summary>
public sealed class ServiceCollectionExtensionsTests
{
    private const string ConnectionString = "Server=example;Database=nightingale;Trusted_Connection=True";

    [Fact]
    public void AddNightingalePolecat_WhenConnectionStringIsMissing_ShouldNameTheKeyItLookedFor()
    {
        // Arrange
        var configuration = Configuration(("Nightingale:ConnectionStringName", "Store"));
        var services = new ServiceCollection();

        // Act
        var exception = Should.Throw<InvalidOperationException>(() => services.AddNightingalePolecat(configuration));

        // Assert
        exception.Message.ShouldContain("ConnectionStrings:Store");
    }

    [Fact]
    public void AddNightingalePolecat_ShouldBindTheSectionAndDefaultTheConnectionStringName()
    {
        // Arrange
        var configuration = Configuration(
            ("ConnectionStrings:Nightingale", ConnectionString),
            ("Nightingale:CreateDatabase", "false"),
            ("Nightingale:ApplySchemaChanges", "true"),
            ("Nightingale:FastBoot", "true"),
            ("Nightingale:Store:Collation", "Latin1_General_100_BIN2"),
            ("Nightingale:Store:Partitioning", "ArchivedStream"),
            ("Nightingale:Deletion:AllowTombstone", "true"));
        var services = new ServiceCollection();

        // Act
        services.AddNightingalePolecat(configuration);

        // Assert
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<NightingaleOptions>();
        options.ShouldSatisfyAllConditions(
            bound => bound.ConnectionStringName.ShouldBe("Nightingale"),
            bound => bound.CreateDatabase.ShouldBeFalse(),
            bound => bound.ApplySchemaChanges.ShouldBeTrue(),
            bound => bound.FastBoot.ShouldBeTrue(),
            bound => bound.Store.Collation.ShouldBe("Latin1_General_100_BIN2"),
            bound => bound.Store.Partitioning.ShouldBe(PartitioningMode.ArchivedStream),
            bound => bound.Deletion.AllowDelete.ShouldBeFalse(),
            bound => bound.Deletion.AllowTombstone.ShouldBeTrue());
        provider.GetRequiredService<NightingaleOptionsBase>().ShouldBeSameAs(options);
        provider.GetRequiredService<IStreamStore>().ShouldBeOfType<PolecatStreamStore>();
    }

    [Fact]
    public void AddNightingalePolecat_WhenPartitioningIsNotAKnownMode_ShouldRefuseAtRegistration()
    {
        // Arrange
        var configuration = Configuration(
            ("ConnectionStrings:Nightingale", ConnectionString),
            ("Nightingale:Store:Partitioning", "Sideways"));
        var services = new ServiceCollection();

        // Act
        var exception = Should.Throw<InvalidOperationException>(() => services.AddNightingalePolecat(configuration));

        // Assert
        exception.Message.ShouldContain("Sideways");
    }

    [Theory]
    [InlineData("0", "must be at least 1")]
    [InlineData("191", "at most 190 events in one append")]
    public void AddNightingalePolecat_WhenAnAppendMayCarryNoEventsOrMoreThanTheStoreTakes_ShouldRefuseAtRegistration(string configured, string expected)
    {
        // Arrange
        var configuration = Configuration(
            ("ConnectionStrings:Nightingale", ConnectionString),
            ("Nightingale:MaxEventsPerAppend", configured));
        var services = new ServiceCollection();

        // Act
        var exception = Should.Throw<InvalidOperationException>(() => services.AddNightingalePolecat(configuration));

        // Assert
        exception.Message.ShouldContain("Nightingale:MaxEventsPerAppend");
        exception.Message.ShouldContain(expected);
    }

    [Fact]
    public void AddNightingalePolecat_ByDefault_ShouldAllowFewerEventsInOneAppendThanTheStoreTakes()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddNightingalePolecat(Configuration(("ConnectionStrings:Nightingale", ConnectionString), ("Nightingale:MaxEventsPerAppend", "190")));
        using var provider = services.BuildServiceProvider();

        // Assert: the default leaves room below the store's ceiling, and the ceiling itself is accepted.
        NightingaleOptionsBase.DefaultMaxEventsPerAppend.ShouldBeLessThan(PolecatStreamStore.MaxEventsPerAppend);
        provider.GetRequiredService<NightingaleOptions>().MaxEventsPerAppend.ShouldBe(190);
    }

    private static IConfiguration Configuration(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(pair => new KeyValuePair<string, string?>(pair.Key, pair.Value)))
            .Build();
}
