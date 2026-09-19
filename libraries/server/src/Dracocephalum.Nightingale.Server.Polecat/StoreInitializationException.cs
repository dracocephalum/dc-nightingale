namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// The server cannot serve the database it was pointed at: it does not exist and may not be
/// created, it is not empty and was not initialized by Nightingale, its record disagrees with the
/// configuration, or its schema differs from what this server expects. The message says which and
/// what would fix it. Thrown from startup, so the host does not start.
/// </summary>
public sealed class StoreInitializationException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="StoreInitializationException"/> class.</summary>
    public StoreInitializationException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="StoreInitializationException"/> class.</summary>
    /// <param name="message">What is wrong and what would fix it.</param>
    public StoreInitializationException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="StoreInitializationException"/> class.</summary>
    /// <param name="message">What is wrong and what would fix it.</param>
    /// <param name="innerException">The underlying failure.</param>
    public StoreInitializationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
