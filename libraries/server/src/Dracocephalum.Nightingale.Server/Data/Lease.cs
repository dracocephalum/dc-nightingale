namespace Dracocephalum.Nightingale.Server.Data;

/// <summary>
/// A lease: who runs something that must run in one place, until when, and where they are
/// reached. The owner and the expiry are concurrency tokens, so two instances writing the same
/// lease are told apart by the row itself, on any provider.
/// </summary>
public sealed class Lease
{
    /// <summary>Gets or sets the row's id, its key.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets what is leased; unique.</summary>
    public required string Name { get; set; }

    /// <summary>Gets or sets the instance holding the lease.</summary>
    public required string Owner { get; set; }

    /// <summary>Gets or sets where the holder is reached, when it advertises an address.</summary>
    public string? OwnerAddress { get; set; }

    /// <summary>Gets or sets when the lease lapses unless renewed.</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}
