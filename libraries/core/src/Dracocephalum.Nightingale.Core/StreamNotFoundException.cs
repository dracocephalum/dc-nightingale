using System.Globalization;

namespace Dracocephalum.Nightingale;

/// <summary>
/// A read was enumerated as events and the stream has none. The non-throwing way to learn the same
/// thing is the read result's state, which the client exposes before any event is enumerated.
/// </summary>
public sealed class StreamNotFoundException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="StreamNotFoundException"/> class.</summary>
    /// <param name="stream">The stream name.</param>
    public StreamNotFoundException(string stream)
        : base(string.Format(CultureInfo.InvariantCulture, "Stream '{0}' was not found.", stream))
    {
        Stream = stream;
    }

    /// <summary>Gets the stream name.</summary>
    public string Stream { get; }
}
