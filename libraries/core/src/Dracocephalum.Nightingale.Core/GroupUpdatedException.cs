namespace Dracocephalum.Nightingale;

/// <summary>
/// The persistent-subscription group's settings were updated while this consumer was connected,
/// and its call ended so that it connects again under the new settings. Nothing is lost: what
/// the consumer had not acknowledged is delivered again after it reconnects.
/// </summary>
/// <remarks>
/// The name is the contract's, kept on purpose: the reference client says "persistent subscription" and
/// "group", and so do the wire and the client here. The server's own types say "subscription group".
/// </remarks>
public sealed class GroupUpdatedException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="GroupUpdatedException"/> class.</summary>
    public GroupUpdatedException()
        : this(string.Empty, string.Empty)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="GroupUpdatedException"/> class.</summary>
    /// <param name="message">What happened.</param>
    public GroupUpdatedException(string message)
        : base(message)
    {
        Stream = string.Empty;
        Group = string.Empty;
    }

    /// <summary>Initializes a new instance of the <see cref="GroupUpdatedException"/> class.</summary>
    /// <param name="message">What happened.</param>
    /// <param name="innerException">The underlying failure.</param>
    public GroupUpdatedException(string message, Exception innerException)
        : base(message, innerException)
    {
        Stream = string.Empty;
        Group = string.Empty;
    }

    /// <summary>Initializes a new instance of the <see cref="GroupUpdatedException"/> class.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="group">The group.</param>
    public GroupUpdatedException(string stream, string group)
        : base($"The group's settings were updated; connect again to consume under them. Stream '{stream}', group '{group}'.")
    {
        Stream = stream;
        Group = group;
    }

    /// <summary>Gets the stream name.</summary>
    public string Stream { get; }

    /// <summary>Gets the group name.</summary>
    public string Group { get; }
}
