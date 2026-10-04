using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
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

    private sealed record Sample(string DisplayName, PartitioningMode Mode);
}
