namespace Dracocephalum.Nightingale.Client;

/// <summary>
/// How a client reaches a Nightingale server.
/// </summary>
public sealed record NightingaleClientOptions
{
    /// <summary>Gets the server address, such as <c>https://nightingale.example:5001</c>.</summary>
    public required Uri Address { get; init; }

    /// <summary>Validates the options.</summary>
    /// <exception cref="ArgumentException">The address is not absolute, or its scheme is not HTTP or HTTPS.</exception>
    public void Validate()
    {
        if (!Address.IsAbsoluteUri)
        {
            throw new ArgumentException("The server address must be absolute.", nameof(Address));
        }

        if (Address.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("The server address must use http or https.", nameof(Address));
        }
    }
}
