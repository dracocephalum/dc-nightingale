using System.Globalization;

namespace Dracocephalum.Nightingale;

/// <summary>
/// An append to several streams found streams in another state than the caller expected, and wrote
/// nothing. Carries every such stream, in the order the conditions were given, so the caller can
/// reload them all at once; a caller that wants one takes the first.
/// </summary>
public sealed class StreamConflictsException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="StreamConflictsException"/> class.</summary>
    /// <param name="conflicts">The streams not in the expected state, in the order the conditions were given; never empty.</param>
    public StreamConflictsException(IReadOnlyList<StreamConflict> conflicts)
        : base(Describe(conflicts))
    {
        Conflicts = conflicts;
    }

    /// <summary>Gets the streams not in the expected state, in the order the conditions were given.</summary>
    public IReadOnlyList<StreamConflict> Conflicts { get; }

    private static string Describe(IReadOnlyList<StreamConflict> conflicts)
    {
        ArgumentNullException.ThrowIfNull(conflicts);
        ArgumentOutOfRangeException.ThrowIfZero(conflicts.Count);
        return "Nothing was written: " + string.Join(
            "; ",
            conflicts.Select(conflict => string.Format(
                CultureInfo.InvariantCulture,
                "stream '{0}' was expected at {1} but is at revision {2}",
                conflict.Stream,
                conflict.Expected,
                conflict.ActualRevision))) + ".";
    }
}
