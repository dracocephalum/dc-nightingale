namespace Dracocephalum.Nightingale;

/// <summary>A persistent-subscription group with this name already exists on the stream.</summary>
public sealed class GroupExistsException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="GroupExistsException"/> class.</summary>
    public GroupExistsException()
        : this(string.Empty, string.Empty)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="GroupExistsException"/> class.</summary>
    /// <param name="message">What happened.</param>
    public GroupExistsException(string message)
        : base(message)
    {
        Stream = string.Empty;
        Group = string.Empty;
    }

    /// <summary>Initializes a new instance of the <see cref="GroupExistsException"/> class.</summary>
    /// <param name="message">What happened.</param>
    /// <param name="innerException">The underlying failure.</param>
    public GroupExistsException(string message, Exception innerException)
        : base(message, innerException)
    {
        Stream = string.Empty;
        Group = string.Empty;
    }

    /// <summary>Initializes a new instance of the <see cref="GroupExistsException"/> class.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="group">The group.</param>
    public GroupExistsException(string stream, string group)
        : base($"A persistent-subscription group with this name already exists on the stream. Stream '{stream}', group '{group}'.")
    {
        Stream = stream;
        Group = group;
    }

    /// <summary>Gets the stream.</summary>
    public string Stream { get; }

    /// <summary>Gets the group.</summary>
    public string Group { get; }
}
