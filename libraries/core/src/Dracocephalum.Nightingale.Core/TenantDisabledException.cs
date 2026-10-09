namespace Dracocephalum.Nightingale;

/// <summary>The tenant exists and is disabled: its calls are refused and its consumers are disconnected until it is enabled again.</summary>
public sealed class TenantDisabledException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="TenantDisabledException"/> class.</summary>
    public TenantDisabledException()
        : base("The tenant is disabled.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TenantDisabledException"/> class.</summary>
    /// <param name="message">The server's message.</param>
    public TenantDisabledException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TenantDisabledException"/> class.</summary>
    /// <param name="message">The server's message.</param>
    /// <param name="innerException">The cause.</param>
    public TenantDisabledException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TenantDisabledException"/> class.</summary>
    /// <param name="tenantId">The tenant.</param>
    public TenantDisabledException(Guid tenantId)
        : base($"Tenant '{tenantId}' is disabled.")
    {
        TenantId = tenantId;
    }

    /// <summary>Gets the tenant.</summary>
    public Guid TenantId { get; }
}
