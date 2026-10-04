using System.Text.Json;

using FakeItEasy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Tests;

public sealed class NightingaleJsonExtensionsTests
{
    [Fact]
    public void AddNightingaleJson_ShouldGiveTheHostsJsonTheLibrarysSettings()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddNightingaleJson();

        // Assert: minimal endpoints and controllers both write as the library does.
        using var provider = services.BuildServiceProvider();
        var minimal = provider.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
        var controllers = provider.GetRequiredService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>().Value.JsonSerializerOptions;
        var expected = JsonSerializer.Serialize(new Sample("Café", PartitioningMode.ArchivedStream), NightingaleJson.Default);
        JsonSerializer.Serialize(new Sample("Café", PartitioningMode.ArchivedStream), minimal).ShouldBe(expected);
        JsonSerializer.Serialize(new Sample("Café", PartitioningMode.ArchivedStream), controllers).ShouldBe(expected);
        expected.ShouldBe("{\"displayName\":\"Café\",\"mode\":\"ArchivedStream\"}");
    }

    [Theory]
    [InlineData("Development", true)]
    [InlineData("Staging", true)]
    [InlineData("Production", false)]
    [InlineData(null, false)]
    public void AddNightingaleJson_ShouldIndentOnlyWhenGivenAnEnvironmentThatIsNotProduction(string? environmentName, bool indented)
    {
        // Arrange: no environment given means the host did not ask.
        var services = new ServiceCollection();
        IHostEnvironment? environment = null;
        if (environmentName is not null)
        {
            environment = A.Fake<IHostEnvironment>();
            A.CallTo(() => environment.EnvironmentName).Returns(environmentName);
        }

        // Act
        services.AddNightingaleJson(environment);

        // Assert
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions.WriteIndented.ShouldBe(indented);
        provider.GetRequiredService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>().Value.JsonSerializerOptions.WriteIndented.ShouldBe(indented);
        NightingaleJson.Default.WriteIndented.ShouldBeFalse();
    }

    private sealed record Sample(string DisplayName, PartitioningMode Mode);
}
