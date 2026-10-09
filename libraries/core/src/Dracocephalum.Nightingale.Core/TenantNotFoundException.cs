namespace Dracocephalum.Nightingale;

/// <summary>No tenant with that id, or the call named none where its credential needs one, or named one its credential is not bound to.</summary>
public sealed class TenantNotFoundException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="TenantNotFoundException"/> class.</summary>
    public TenantNotFoundException()
        : base("The tenant was not found.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TenantNotFoundException"/> class.</summary>
    /// <param name="message">The server's message.</param>
    public TenantNotFoundException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TenantNotFoundException"/> class.</summary>
    /// <param name="message">The server's message.</param>
    /// <param name="innerException">The cause.</param>
    public TenantNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TenantNotFoundException"/> class.</summary>
    /// <param name="tenantId">The tenant that was named, or <see langword="null"/> when none was.</param>
    public TenantNotFoundException(Guid? tenantId)
        : base(tenantId is null ? "The call named no tenant, and its credential is not bound to one." : $"Tenant '{tenantId}' was not found.")
    {
        TenantId = tenantId;
    }

    /// <summary>Gets the tenant that was named, or <see langword="null"/> when none was.</summary>
    public Guid? TenantId { get; }
}
