namespace Dracocephalum.Nightingale.Server.Persistence;

/// <summary>
/// A lease as its row: what is leased, which instance holds it, and until when. Both the owner
/// and the expiry are concurrency tokens, so two instances racing for the same lease serialize on
/// the row: the second write sees the first one's values changed and loses.
/// </summary>
public sealed class LeaseRow
{
    /// <summary>Gets or sets what is leased.</summary>
    public required string Name { get; set; }

    /// <summary>Gets or sets the instance holding it.</summary>
    public required string Owner { get; set; }

    /// <summary>Gets or sets when it lapses unless renewed.</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}
