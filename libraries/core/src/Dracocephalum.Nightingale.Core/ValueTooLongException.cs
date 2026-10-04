using System.Globalization;

namespace Dracocephalum.Nightingale;

/// <summary>
/// A value the caller sent is longer than the store holds: a stream name, an event type, a
/// correlation or causation id. The store decides, when it is asked to write the value, and what
/// it holds is measured its own way, in bytes of the encoded text rather than in characters; the
/// server does not keep a copy of the store's limits to check against first.
/// </summary>
public sealed class ValueTooLongException : ArgumentException
{
    /// <summary>Initializes a new instance of the <see cref="ValueTooLongException"/> class.</summary>
    /// <param name="what">What was too long, as a caller would call it: "stream name", "event type".</param>
    /// <param name="innerException">What the store said.</param>
    public ValueTooLongException(string what, Exception? innerException = null)
        : base(string.Format(CultureInfo.InvariantCulture, "The {0} is longer than the store holds.", what), innerException)
    {
        What = what;
    }

    /// <summary>Gets what was too long.</summary>
    public string What { get; }

    /// <summary>What a too-long stream name is called.</summary>
    public const string StreamName = "stream name";
}
