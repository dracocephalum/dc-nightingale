namespace Dracocephalum.Nightingale;

/// <summary>No parked message at that position in the group.</summary>
public sealed class ParkedMessageNotFoundException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="ParkedMessageNotFoundException"/> class.</summary>
    public ParkedMessageNotFoundException()
        : this(string.Empty, string.Empty)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ParkedMessageNotFoundException"/> class.</summary>
    /// <param name="message">What happened.</param>
    public ParkedMessageNotFoundException(string message)
        : base(message)
    {
        Stream = string.Empty;
        Group = string.Empty;
    }

    /// <summary>Initializes a new instance of the <see cref="ParkedMessageNotFoundException"/> class.</summary>
    /// <param name="message">What happened.</param>
    /// <param name="innerException">The underlying failure.</param>
    public ParkedMessageNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
        Stream = string.Empty;
        Group = string.Empty;
    }

    /// <summary>Initializes a new instance of the <see cref="ParkedMessageNotFoundException"/> class.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="group">The group.</param>
    public ParkedMessageNotFoundException(string stream, string group)
        : base($"No parked message at that position in the group. Stream '{stream}', group '{group}'.")
    {
        Stream = stream;
        Group = group;
    }

    /// <summary>Gets the stream.</summary>
    public string Stream { get; }

    /// <summary>Gets the group.</summary>
    public string Group { get; }
}
