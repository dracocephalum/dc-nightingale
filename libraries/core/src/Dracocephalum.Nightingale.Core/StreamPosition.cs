using System.Globalization;

namespace Dracocephalum.Nightingale;

/// <summary>
/// A position within a stream: the zero-based revision of an event, or <see cref="End"/>,
/// which stands for "after the last event" and is where a backwards read begins.
/// </summary>
public readonly record struct StreamPosition
{
    /// <summary>The first position in any stream.</summary>
    public static readonly StreamPosition Start = new(0);

    /// <summary>The position after the last event, whatever the stream's length.</summary>
    public static readonly StreamPosition End = new(long.MaxValue);

    private StreamPosition(long value) => Value = value;

    /// <summary>Gets the zero-based revision, or <see cref="long.MaxValue"/> for <see cref="End"/>.</summary>
    public long Value { get; }

    /// <summary>Gets a value indicating whether this is <see cref="End"/>.</summary>
    public bool IsEnd => Value == long.MaxValue;

    /// <summary>Creates a position from a revision.</summary>
    /// <param name="value">The zero-based revision.</param>
    /// <returns>The position.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The revision is negative, or is the value reserved for <see cref="End"/>.</exception>
    public static StreamPosition From(long value) =>
        value < 0 || value == long.MaxValue
            ? throw new ArgumentOutOfRangeException(nameof(value), value, "A stream position is a non-negative revision.")
            : new(value);

    /// <summary>The position immediately after this one.</summary>
    /// <returns>The next position.</returns>
    /// <exception cref="InvalidOperationException">This is <see cref="End"/>.</exception>
    public StreamPosition Next() =>
        IsEnd ? throw new InvalidOperationException("There is no position after End.") : new(Value + 1);

    /// <inheritdoc/>
    public override string ToString() => IsEnd ? "End" : Value.ToString(CultureInfo.InvariantCulture);
}
