using System.Text.Encodings.Web;
using System.Text.Json;

namespace Dracocephalum.Nightingale;

/// <summary>
/// The one set of serializer options every Nightingale component uses when it writes JSON it does
/// not own: an event body or a metadata object on its way through the gateway. It is not for an
/// application's own JSON; it is what keeps the text the client sent and the text the client gets
/// back identical, wherever in the pipeline the bytes are re-serialized. Change it here and every
/// component agrees again at the next release.
/// </summary>
public static class NightingaleJson
{
    /// <summary>
    /// Gets the options. The relaxed encoder writes non-ASCII characters verbatim; the default one
    /// escapes every one of them as <c>\uXXXX</c>, which is equivalent JSON but not the same bytes.
    /// Everything JSON requires escaped is still escaped.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}
