namespace Dracocephalum.Nightingale.Server.Data;

/// <summary>
/// A tenant as its row: the id the wire and the credentials speak, the name people use, and the
/// id the store keeps on every event and stream row, which only the gateway ever sees. The
/// default tenant is written when the schema is applied, mapped to the store's own default id,
/// so the single-tenant store needs nothing else.
/// </summary>
public sealed class Tenant
{
    /// <summary>The default tenant's id, the same in every store, seeded with the schema.</summary>
    public static readonly Guid DefaultId = new("a1c6e3d2-5b7f-4e8a-9c0d-1f2a3b4c5d6e");

    /// <summary>The default tenant's name.</summary>
    public const string DefaultName = "default";

    /// <summary>The id the store keeps for its default tenant, which the gateway maps the default tenant to.</summary>
    public const string DefaultStoreTenantId = "*DEFAULT*";

    /// <summary>Gets or sets the id, the one every call and credential names the tenant by.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the name, for people; unique, and free to change.</summary>
    public required string Name { get; set; }

    /// <summary>Gets or sets the id the store keeps on the tenant's rows; fixed for the tenant's life and never on the wire.</summary>
    public required string StoreTenantId { get; set; }

    /// <summary>Gets or sets a value indicating whether the tenant's calls are refused and its consumers disconnected.</summary>
    public bool IsDisabled { get; set; }

    /// <summary>Gets or sets when the tenant was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Gets or sets when the row last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
