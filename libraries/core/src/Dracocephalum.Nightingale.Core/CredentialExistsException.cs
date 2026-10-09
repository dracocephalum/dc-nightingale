namespace Dracocephalum.Nightingale;

/// <summary>A credential of that name exists.</summary>
public sealed class CredentialExistsException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="CredentialExistsException"/> class.</summary>
    public CredentialExistsException()
        : base("A credential of that name exists.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="CredentialExistsException"/> class.</summary>
    /// <param name="message">The message.</param>
    public CredentialExistsException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="CredentialExistsException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public CredentialExistsException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
