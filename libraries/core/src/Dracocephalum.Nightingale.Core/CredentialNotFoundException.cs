namespace Dracocephalum.Nightingale;

/// <summary>No credential of that name, among those the caller may manage.</summary>
public sealed class CredentialNotFoundException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="CredentialNotFoundException"/> class.</summary>
    public CredentialNotFoundException()
        : base("The credential was not found.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="CredentialNotFoundException"/> class.</summary>
    /// <param name="message">The message.</param>
    public CredentialNotFoundException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="CredentialNotFoundException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public CredentialNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
