namespace Dracocephalum.Nightingale;

/// <summary>
/// The server did not accept the call's credentials: none were sent where some are required,
/// the name or the password is wrong, the credential is disabled or locked out, or credentials
/// were sent over a transport the server does not accept them on. The server says no more than
/// that, on purpose.
/// </summary>
public sealed class AuthenticationFailedException : UnauthorizedAccessException
{
    /// <summary>Initializes a new instance of the <see cref="AuthenticationFailedException"/> class.</summary>
    public AuthenticationFailedException()
        : base("The server did not accept the call's credentials.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AuthenticationFailedException"/> class.</summary>
    /// <param name="message">The server's message.</param>
    public AuthenticationFailedException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AuthenticationFailedException"/> class.</summary>
    /// <param name="message">The server's message.</param>
    /// <param name="innerException">The cause.</param>
    public AuthenticationFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
