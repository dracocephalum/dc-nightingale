namespace Dracocephalum.Nightingale;

/// <summary>
/// A persistent-subscription group as the server describes it: what it was created with, how far
/// it has come, what is set aside, and where it runs. Everything but <see cref="Live"/> and
/// <see cref="LastKnownPosition"/> is what the store holds, so a listing has it for every group;
/// those two are only in the answer about one group.
/// </summary>
/// <remarks>
/// The name is the contract's, kept on purpose: the reference client says "persistent subscription" and
/// "group", and so do the wire and the client here. The server's own types say "subscription group".
/// </remarks>
/// <param name="Stream">The stream, <c>$all</c>, or a virtual stream, as the group was created with it.</param>
/// <param name="Group">The group name, as the group was created with it.</param>
/// <param name="Settings">The group's settings, its numbering among them.</param>
/// <param name="CreatedAt">When the group was created.</param>
/// <param name="Checkpoint">The last number every event up to which is done, in the group's numbering; <see langword="null"/> when none is yet.</param>
/// <param name="ParkedCount">How many messages are parked.</param>
/// <param name="OutboxCount">How many messages wait on the outbox, to be delivered ahead of the stream.</param>
/// <param name="Running">Whether an instance runs the group now, which it does while a consumer is connected.</param>
/// <param name="OwnerAddress">Where the instance that runs the group is reached; <see langword="null"/> when none runs it or it advertises no address.</param>
/// <param name="LastKnownPosition">The last number of the group's stream, in the group's numbering; <see langword="null"/> in a listing, and when the stream holds nothing.</param>
/// <param name="Live">What only the instance running the group knows; <see langword="null"/> in a listing, and when the group is not running.</param>
public sealed record PersistentSubscriptionInfo(
    string Stream,
    string Group,
    GroupSettings Settings,
    DateTimeOffset CreatedAt,
    long? Checkpoint,
    long ParkedCount,
    long OutboxCount,
    bool Running,
    Uri? OwnerAddress,
    long? LastKnownPosition = null,
    PersistentSubscriptionLiveInfo? Live = null);
