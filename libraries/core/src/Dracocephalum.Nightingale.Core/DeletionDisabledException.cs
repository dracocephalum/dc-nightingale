namespace Dracocephalum.Nightingale;

/// <summary>
/// The server refused a delete or a tombstone because its host has not enabled that kind of
/// deletion. Off by default: a store that never deletes keeps every event a consumer may still
/// need, and enabling deletion is a deliberate decision of the operator, not of the caller.
/// </summary>
public sealed class DeletionDisabledException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="DeletionDisabledException"/> class.</summary>
    public DeletionDisabledException()
        : this("Deletion is disabled on this server.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="DeletionDisabledException"/> class.</summary>
    /// <param name="message">What was refused.</param>
    public DeletionDisabledException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="DeletionDisabledException"/> class.</summary>
    /// <param name="message">What was refused.</param>
    /// <param name="innerException">The underlying failure.</param>
    public DeletionDisabledException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
