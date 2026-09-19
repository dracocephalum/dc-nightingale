namespace Dracocephalum.Nightingale;

/// <summary>Another server instance owns the group; connect there.</summary>
public sealed class GroupOwnedElsewhereException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="GroupOwnedElsewhereException"/> class.</summary>
    public GroupOwnedElsewhereException()
        : this(string.Empty, string.Empty, string.Empty)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="GroupOwnedElsewhereException"/> class.</summary>
    /// <param name="message">What happened.</param>
    public GroupOwnedElsewhereException(string message)
        : base(message)
    {
        Stream = string.Empty;
        Group = string.Empty;
    }

    /// <summary>Initializes a new instance of the <see cref="GroupOwnedElsewhereException"/> class.</summary>
    /// <param name="message">What happened.</param>
    /// <param name="innerException">The underlying failure.</param>
    public GroupOwnedElsewhereException(string message, Exception innerException)
        : base(message, innerException)
    {
        Stream = string.Empty;
        Group = string.Empty;
    }

    /// <summary>Initializes a new instance of the <see cref="GroupOwnedElsewhereException"/> class.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="group">The group.</param>
    /// <param name="owner">The id of the instance that owns the group.</param>
    public GroupOwnedElsewhereException(string stream, string group, string owner)
        : base($"Another server instance, {owner}, owns the group; connect there. Stream '{stream}', group '{group}'.")
    {
        Stream = stream;
        Group = group;
        Owner = owner;
    }

    /// <summary>Gets the id of the instance that owns the group, or empty when unknown.</summary>
    public string Owner { get; } = string.Empty;

    /// <summary>Gets the stream.</summary>
    public string Stream { get; }

    /// <summary>Gets the group.</summary>
    public string Group { get; }
}
