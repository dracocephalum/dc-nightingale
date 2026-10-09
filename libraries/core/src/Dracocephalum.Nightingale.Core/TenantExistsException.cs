namespace Dracocephalum.Nightingale;

/// <summary>A tenant of that name exists.</summary>
public sealed class TenantExistsException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="TenantExistsException"/> class.</summary>
    public TenantExistsException()
        : base("A tenant of that name exists.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TenantExistsException"/> class.</summary>
    /// <param name="message">The message.</param>
    public TenantExistsException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TenantExistsException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public TenantExistsException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
