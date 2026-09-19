using System.Globalization;

namespace Dracocephalum.Nightingale;

/// <summary>
/// An append or delete found the stream in a different state than the caller expected. Carries the
/// expectation and the stream's actual revision, <c>-1</c> when the stream does not exist, so the
/// caller can decide whether to reload and retry.
/// </summary>
public sealed class RevisionConflictException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="RevisionConflictException"/> class.</summary>
    /// <param name="stream">The stream name.</param>
    /// <param name="expected">What the caller asserted.</param>
    /// <param name="actualRevision">The stream's revision, or <c>-1</c> when it does not exist.</param>
    public RevisionConflictException(string stream, StreamState expected, long actualRevision)
        : base(string.Format(
            CultureInfo.InvariantCulture,
            "Stream '{0}' was expected at {1} but is at revision {2}.",
            stream,
            expected,
            actualRevision))
    {
        Stream = stream;
        Expected = expected;
        ActualRevision = actualRevision;
    }

    /// <summary>Gets the stream name.</summary>
    public string Stream { get; }

    /// <summary>Gets what the caller asserted.</summary>
    public StreamState Expected { get; }

    /// <summary>Gets the stream's actual revision, or <c>-1</c> when the stream does not exist.</summary>
    public long ActualRevision { get; }
}
