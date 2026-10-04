using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dracocephalum.Nightingale;

/// <summary>
/// How Nightingale serializes JSON, in one place: the web defaults, enums by name, and non-ASCII
/// characters written as themselves. Every component uses these options, for the JSON it passes
/// through, an event body or a metadata object on its way through the gateway, and for the JSON
/// of its own types alike. The first is why the encoder is what it is: it keeps the text the
/// client sent and the text the client gets back identical, wherever in the pipeline the bytes
/// are re-serialized. The naming policy and the enum converter apply only to a type's own
/// properties and values, so they change nothing of a document that is passed through. A host
/// that serves JSON of its own takes the same settings through <see cref="Configure"/>.
/// </summary>
public static class NightingaleJson
{
    /// <summary>
    /// Gets the options every component serializes with: camel-case property names read without
    /// regard to case, numbers readable from strings, enums as their names, and the relaxed
    /// encoder.
    /// </summary>
    public static JsonSerializerOptions Default { get; } = Configure(new JsonSerializerOptions());

    /// <summary>
    /// Gets the same options with property names written as they are declared, for JSON whose
    /// names are read as configuration paths and should look like the ones in a settings file.
    /// </summary>
    public static JsonSerializerOptions PascalCase { get; } = new(Default) { PropertyNamingPolicy = null };

    /// <summary>
    /// Applies Nightingale's settings to a set of options, the one initialization the defaults
    /// above and a host's own JSON options share. The relaxed encoder writes non-ASCII characters
    /// verbatim; the default one escapes every one of them as <c>\uXXXX</c>, which is equivalent
    /// JSON but not the same bytes. Everything JSON requires escaped is still escaped; what is no
    /// longer escaped is what only matters to text embedded in HTML, which JSON served as JSON is
    /// not.
    /// </summary>
    /// <param name="options">The options to configure.</param>
    /// <returns>The same options, for chaining.</returns>
    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.PropertyNameCaseInsensitive = true;
        options.NumberHandling = JsonNumberHandling.AllowReadingFromString;
        options.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        if (!options.Converters.OfType<JsonStringEnumConverter>().Any())
        {
            options.Converters.Add(new JsonStringEnumConverter());
        }

        return options;
    }
}
