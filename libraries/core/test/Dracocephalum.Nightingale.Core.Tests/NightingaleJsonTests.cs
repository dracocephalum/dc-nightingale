using System.Text.Json;
using System.Text.Json.Nodes;

using Shouldly;

namespace Dracocephalum.Nightingale.Core.Tests;

public sealed class NightingaleJsonTests
{
    private enum Colour
    {
        DeepBlue,
    }

    [Fact]
    public void Default_ShouldWriteATypesOwnPropertiesInCamelCaseAndEnumsByName()
    {
        // Act
        var json = JsonSerializer.Serialize(new Paint("Café", Colour.DeepBlue), NightingaleJson.Default);

        // Assert: non-ASCII characters as themselves, not escaped.
        json.ShouldBe("{\"displayName\":\"Café\",\"colour\":\"DeepBlue\"}");
    }

    [Fact]
    public void Default_ShouldReadWithoutRegardToCaseAndEnumsByName()
    {
        // Act
        var paint = JsonSerializer.Deserialize<Paint>("{\"DISPLAYNAME\":\"Sky\",\"Colour\":\"DeepBlue\"}", NightingaleJson.Default);

        // Assert
        paint.ShouldBe(new Paint("Sky", Colour.DeepBlue));
    }

    [Fact]
    public void Default_ShouldPassADocumentThroughWithItsNamesAndTextUnchanged()
    {
        // Arrange: a body the gateway does not own; its names are data, not properties.
        const string body = "{\"OrderId\":1,\"Total\":42.50,\"Note\":\"Café ☕\",\"snake_case\":true}";
        using var parsed = JsonDocument.Parse(body);
        var node = JsonNode.Parse(body)!;

        // Act
        var viaElement = JsonSerializer.Serialize(parsed.RootElement, NightingaleJson.Default);
        var viaNode = node.ToJsonString(NightingaleJson.Default);

        // Assert: byte for byte what came in, whatever the naming policy says of a type's properties.
        viaElement.ShouldBe(body);
        viaNode.ShouldBe(body);
    }

    [Fact]
    public void PascalCase_ShouldWriteNamesAsDeclaredAndKeepEverythingElse()
    {
        // Act
        var json = JsonSerializer.Serialize(new Paint("Café", Colour.DeepBlue), NightingaleJson.PascalCase);

        // Assert
        json.ShouldBe("{\"DisplayName\":\"Café\",\"Colour\":\"DeepBlue\"}");
    }

    [Fact]
    public void Configure_ShouldGiveAnotherSetOfOptionsTheSameSettingsOnce()
    {
        // Arrange: a host's own options, configured twice as two registrations might.
        var options = new JsonSerializerOptions();

        // Act
        NightingaleJson.Configure(NightingaleJson.Configure(options));

        // Assert
        JsonSerializer.Serialize(new Paint("Café", Colour.DeepBlue), options).ShouldBe(JsonSerializer.Serialize(new Paint("Café", Colour.DeepBlue), NightingaleJson.Default));
        options.Converters.Count.ShouldBe(1);
    }

    [Fact]
    public void Configure_WhenAskedToIndent_ShouldIndentAndTheDefaultsShouldNot()
    {
        // Act
        var indented = JsonSerializer.Serialize(new Paint("Sky", Colour.DeepBlue), NightingaleJson.Configure(new JsonSerializerOptions(), writeIndented: true));
        var compact = JsonSerializer.Serialize(new Paint("Sky", Colour.DeepBlue), NightingaleJson.Default);

        // Assert: indentation is part of the bytes, so what passes a document through never has it.
        indented.ShouldContain("\n");
        indented.ReplaceLineEndings(string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal).ShouldBe(compact);
        NightingaleJson.Default.WriteIndented.ShouldBeFalse();
        NightingaleJson.PascalCase.WriteIndented.ShouldBeFalse();
    }

    private sealed record Paint(string DisplayName, Colour Colour);
}
