using System.Globalization;

namespace Dracocephalum.Nightingale;

/// <summary>
/// What a writer asserts about a stream before appending: a specific revision, or one of the
/// named states. On the wire the named states are the negative constants of the contract's
/// <c>ExpectedRevision</c> enum, so <see cref="ToInt64"/> is the exact value sent.
/// </summary>
public readonly record struct StreamState
{
    /// <summary>No check: append whatever the stream's revision is.</summary>
    public static readonly StreamState Any = new(-2);

    /// <summary>The stream must not exist yet.</summary>
    public static readonly StreamState NoStream = new(-1);

    /// <summary>The stream must exist, at any revision.</summary>
    public static readonly StreamState StreamExists = new(-4);

    private readonly long _value;

    private StreamState(long value) => _value = value;

    /// <summary>Gets a value indicating whether this state names a specific revision.</summary>
    public bool HasRevision => _value >= 0;

    /// <summary>The stream's last event must be at exactly this revision.</summary>
    /// <param name="revision">The zero-based revision.</param>
    /// <returns>The state.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The revision is negative.</exception>
    public static StreamState StreamRevision(long revision) =>
        revision < 0
            ? throw new ArgumentOutOfRangeException(nameof(revision), revision, "A revision is never negative.")
            : new(revision);

    /// <summary>Reads a state back from its wire value.</summary>
    /// <param name="value">A revision, or one of the named constants.</param>
    /// <returns>The state.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative and not a named state.</exception>
    public static StreamState FromInt64(long value) =>
        value >= 0 || value is -1 or -2 or -4
            ? new(value)
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Not a revision or a named stream state.");

    /// <summary>The wire value: the revision, or the named state's constant.</summary>
    /// <returns>The value the contract's <c>expected_revision</c> field carries.</returns>
    public long ToInt64() => _value;

    /// <inheritdoc/>
    public override string ToString() => _value switch
    {
        -1 => "NoStream",
        -2 => "Any",
        -4 => "StreamExists",
        _ => _value.ToString(CultureInfo.InvariantCulture),
    };
}
