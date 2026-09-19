using System.Globalization;

namespace Dracocephalum.Nightingale;

/// <summary>
/// The stream was soft-deleted: its events are hidden from every read and it accepts no appends.
/// </summary>
public sealed class StreamDeletedException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="StreamDeletedException"/> class.</summary>
    /// <param name="stream">The stream name.</param>
    public StreamDeletedException(string stream)
        : base(string.Format(CultureInfo.InvariantCulture, "Stream '{0}' has been deleted.", stream))
    {
        Stream = stream;
    }

    /// <summary>Gets the stream name.</summary>
    public string Stream { get; }
}
