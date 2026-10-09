namespace Dracocephalum.Nightingale;

/// <summary>The credential was accepted but may not do what the call asked: its role is too low, or the tenant is not its own.</summary>
public sealed class AccessDeniedException : UnauthorizedAccessException
{
    /// <summary>Initializes a new instance of the <see cref="AccessDeniedException"/> class.</summary>
    public AccessDeniedException()
        : base("The credential may not do this.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AccessDeniedException"/> class.</summary>
    /// <param name="message">The server's message.</param>
    public AccessDeniedException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AccessDeniedException"/> class.</summary>
    /// <param name="message">The server's message.</param>
    /// <param name="innerException">The cause.</param>
    public AccessDeniedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AccessDeniedException"/> class.</summary>
    /// <param name="message">The server's message.</param>
    /// <param name="required">The role the call needs.</param>
    public AccessDeniedException(string message, CredentialRole required)
        : base(message)
    {
        Required = required;
    }

    /// <summary>Gets the role the call needs, when the server said.</summary>
    public CredentialRole? Required { get; }
}
