namespace Dracocephalum.Nightingale;

/// <summary>The group already has as many consumers as it allows.</summary>
public sealed class ConsumerLimitReachedException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="ConsumerLimitReachedException"/> class.</summary>
    public ConsumerLimitReachedException()
        : this(string.Empty, string.Empty)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ConsumerLimitReachedException"/> class.</summary>
    /// <param name="message">What happened.</param>
    public ConsumerLimitReachedException(string message)
        : base(message)
    {
        Stream = string.Empty;
        Group = string.Empty;
    }

    /// <summary>Initializes a new instance of the <see cref="ConsumerLimitReachedException"/> class.</summary>
    /// <param name="message">What happened.</param>
    /// <param name="innerException">The underlying failure.</param>
    public ConsumerLimitReachedException(string message, Exception innerException)
        : base(message, innerException)
    {
        Stream = string.Empty;
        Group = string.Empty;
    }

    /// <summary>Initializes a new instance of the <see cref="ConsumerLimitReachedException"/> class.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="group">The group.</param>
    public ConsumerLimitReachedException(string stream, string group)
        : base($"The group already has as many consumers as it allows. Stream '{stream}', group '{group}'.")
    {
        Stream = stream;
        Group = group;
    }

    /// <summary>Gets the stream.</summary>
    public string Stream { get; }

    /// <summary>Gets the group.</summary>
    public string Group { get; }
}
