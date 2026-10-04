namespace Dracocephalum.Nightingale;

/// <summary>No such persistent-subscription group.</summary>
/// <remarks>
/// The name is the contract's, kept on purpose: the reference client says "persistent subscription" and
/// "group", and so do the wire and the client here. The server's own types say "subscription group".
/// </remarks>
public sealed class GroupNotFoundException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="GroupNotFoundException"/> class.</summary>
    public GroupNotFoundException()
        : this(string.Empty, string.Empty)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="GroupNotFoundException"/> class.</summary>
    /// <param name="message">What happened.</param>
    public GroupNotFoundException(string message)
        : base(message)
    {
        Stream = string.Empty;
        Group = string.Empty;
    }

    /// <summary>Initializes a new instance of the <see cref="GroupNotFoundException"/> class.</summary>
    /// <param name="message">What happened.</param>
    /// <param name="innerException">The underlying failure.</param>
    public GroupNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
        Stream = string.Empty;
        Group = string.Empty;
    }

    /// <summary>Initializes a new instance of the <see cref="GroupNotFoundException"/> class.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="group">The group.</param>
    public GroupNotFoundException(string stream, string group)
        : base($"No such persistent-subscription group. Stream '{stream}', group '{group}'.")
    {
        Stream = stream;
        Group = group;
    }

    /// <summary>Gets the stream.</summary>
    public string Stream { get; }

    /// <summary>Gets the group.</summary>
    public string Group { get; }
}
