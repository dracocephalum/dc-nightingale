namespace Dracocephalum.Nightingale;

/// <summary>
/// The append carries more events than the server takes in one append. Nothing was written. An
/// append is all of its events or none, in one short transaction, so its size is bounded; a
/// caller with more events sends them in several appends, each under the limit, and they commit
/// one after another.
/// </summary>
public sealed class AppendSizeExceededException : ArgumentException
{
    /// <summary>Initializes a new instance of the <see cref="AppendSizeExceededException"/> class.</summary>
    public AppendSizeExceededException()
        : this(string.Empty, 0)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AppendSizeExceededException"/> class.</summary>
    /// <param name="message">What happened.</param>
    public AppendSizeExceededException(string message)
        : base(message)
    {
        Stream = string.Empty;
    }

    /// <summary>Initializes a new instance of the <see cref="AppendSizeExceededException"/> class.</summary>
    /// <param name="message">What happened.</param>
    /// <param name="innerException">The underlying failure.</param>
    public AppendSizeExceededException(string message, Exception innerException)
        : base(message, innerException)
    {
        Stream = string.Empty;
    }

    /// <summary>Initializes a new instance of the <see cref="AppendSizeExceededException"/> class.</summary>
    /// <param name="stream">The stream the append was for.</param>
    /// <param name="limit">The most events one append may carry; 0 when the server did not say.</param>
    /// <param name="innerException">The underlying failure, when the store was the one to refuse.</param>
    public AppendSizeExceededException(string stream, int limit, Exception? innerException = null)
        : base(
            limit > 0
                ? $"The append to stream '{stream}' carries more events than the {limit} one append may; send them in several appends."
                : $"The append to stream '{stream}' carries more events than one append may; send them in several appends.",
            innerException)
    {
        Stream = stream;
        Limit = limit;
    }

    /// <summary>Gets the stream the append was for.</summary>
    public string Stream { get; }

    /// <summary>Gets the most events one append may carry; 0 when the server did not say.</summary>
    public int Limit { get; }
}
