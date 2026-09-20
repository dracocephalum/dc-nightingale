using System.Globalization;

namespace Dracocephalum.Nightingale;

/// <summary>
/// A read or subscription asked for ordinal numbering and the server's store was not initialized
/// with ordinals. The feature is fixed when the store is initialized; the server's features call
/// says whether it has it.
/// </summary>
public sealed class OrdinalsNotEnabledException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="OrdinalsNotEnabledException"/> class.</summary>
    /// <param name="stream">The virtual stream the read asked for.</param>
    public OrdinalsNotEnabledException(string stream)
        : base(string.Format(CultureInfo.InvariantCulture, "Stream '{0}' cannot be read by ordinal: the store was not initialized with ordinals.", stream))
    {
        Stream = stream;
    }

    /// <summary>Gets the stream name.</summary>
    public string Stream { get; }
}
