namespace Dracocephalum.Nightingale.Server.Data;

/// <summary>
/// A persistent-subscription group as its row: found by tenant, stream and name, which are
/// unique together, and known by its id from then on. Its settings are columns, a value each, so they
/// are read, compared and changed in the database like any other column; the checkpoint is in
/// the group's numbering.
/// </summary>
public sealed class SubscriptionGroup
{
    /// <summary>Gets or sets the group's id, its key.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the tenant.</summary>
    public required string TenantId { get; set; }

    /// <summary>Gets or sets the stream the group reads.</summary>
    public required string Stream { get; set; }

    /// <summary>Gets or sets the group's name, unique per tenant and stream.</summary>
    public required string Name { get; set; }

    /// <summary>Gets or sets where the group starts, inclusive, in its numbering; <see langword="null"/> for the end of its stream.</summary>
    public long? StartPosition { get; set; }

    /// <summary>Gets or sets how long a delivered event may stay unacknowledged before it is redelivered, in milliseconds.</summary>
    public long MessageTimeoutMs { get; set; }

    /// <summary>Gets or sets how many times an event is redelivered before it is parked.</summary>
    public int MaxRetryCount { get; set; }

    /// <summary>Gets or sets how many acknowledgements force a checkpoint write.</summary>
    public int CheckpointUpperBound { get; set; }

    /// <summary>Gets or sets the time after which a checkpoint is written, given the lower bound, in milliseconds.</summary>
    public long CheckpointAfterMs { get; set; }

    /// <summary>Gets or sets the fewest acknowledgements a timed checkpoint write needs.</summary>
    public int CheckpointLowerBound { get; set; }

    /// <summary>Gets or sets how many events the server reads ahead of the consumer.</summary>
    public int BufferSize { get; set; }

    /// <summary>Gets or sets how many consumers may connect at once.</summary>
    public int MaxSubscriberCount { get; set; }

    /// <summary>Gets or sets how the group's numbers are meant, fixed when it is created; stored by name.</summary>
    public Numbering Numbering { get; set; }

    /// <summary>Gets or sets how the group shares its events among its consumers; stored by name.</summary>
    public ConsumerStrategy ConsumerStrategy { get; set; }

    /// <summary>Gets or sets the checkpoint: the last number every event up to which is done, or -1.</summary>
    public long CheckpointPosition { get; set; }

    /// <summary>Gets or sets when the group was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Gets or sets when the running group last wrote how it stands, which it does with each
    /// renewal of its lease; <see langword="null"/> when no consumer is connected. The columns
    /// named <c>Live</c> are that snapshot. They are for reading only, and mean something only
    /// while the group's lease is held: an instance that stopped without clearing them leaves
    /// its last numbers behind.
    /// </summary>
    public DateTimeOffset? LiveSnapshotAt { get; set; }

    /// <summary>Gets or sets when the consumer connected.</summary>
    public DateTimeOffset? LiveConnectedAt { get; set; }

    /// <summary>Gets or sets how many consumers are connected.</summary>
    public int? LiveConsumerCount { get; set; }

    /// <summary>Gets or sets where the first consumer connected from.</summary>
    public string? LiveConsumerAddress { get; set; }

    /// <summary>Gets or sets how many delivered, unacknowledged events the consumers hold at once, over all of them.</summary>
    public int? LiveConsumerBufferSize { get; set; }

    /// <summary>Gets or sets how many events are delivered and neither acknowledged nor refused yet.</summary>
    public int? LiveInFlightCount { get; set; }

    /// <summary>Gets or sets how many events are queued to be delivered again.</summary>
    public int? LiveAwaitingRetryCount { get; set; }

    /// <summary>Gets or sets the checkpoint as the running group has it, which may be ahead of <see cref="CheckpointPosition"/>.</summary>
    public long? LiveCheckpointPosition { get; set; }

    /// <summary>Gets or sets when the oldest event still in flight was delivered.</summary>
    public DateTimeOffset? LiveOldestInFlightAt { get; set; }
}
