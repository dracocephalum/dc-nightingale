namespace Dracocephalum.Nightingale.Server.Sequencing;

/// <summary>A sequence the sequencer numbers: the tenant it belongs to, or none for a shared sequence, the tail that says how far it is committed, and the numbering its progress is published into for this process.</summary>
/// <param name="TenantId">The id the store keeps for the tenant, or <see langword="null"/> for the shared sequence.</param>
/// <param name="Tail">The tail of the sequence.</param>
/// <param name="Numbering">Where how far the sequence is numbered is published: by the sequencer when this process numbers, by the follower when another does.</param>
internal sealed record NumberedSequence(string? TenantId, IStoreTail Tail, FollowedHead Numbering);
