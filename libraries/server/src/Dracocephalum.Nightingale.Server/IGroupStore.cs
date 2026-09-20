using Dracocephalum.Nightingale;

namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// The port for what a persistent-subscription group keeps in the store: its definition and
/// checkpoint, its parked messages, and the lease that says which instance runs it. The runtime
/// of a group, its in-flight events and their retries, is in memory in the owning instance and
/// is not here. A backend reports expected failures as the domain exceptions and never lets its
/// own exception types cross this interface.
/// </summary>
public interface IGroupStore
{
    /// <summary>Creates a group with no checkpoint.</summary>
    /// <param name="group">The definition.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the group exists.</returns>
    /// <exception cref="GroupExistsException">A group with that name exists on the stream.</exception>
    Task CreateAsync(GroupDefinition group, CancellationToken cancellationToken);

    /// <summary>Reads a group.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="group">The group.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The definition, or <see langword="null"/> when there is none.</returns>
    Task<GroupDefinition?> GetAsync(string stream, string group, CancellationToken cancellationToken);

    /// <summary>Deletes a group, its checkpoint and its parked messages.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="group">The group.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True when a group was deleted.</returns>
    Task<bool> DeleteAsync(string stream, string group, CancellationToken cancellationToken);

    /// <summary>Writes the group's checkpoint: the last position every event up to which is done.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="group">The group.</param>
    /// <param name="checkpoint">The position.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the checkpoint is written.</returns>
    Task SaveCheckpointAsync(string stream, string group, long checkpoint, CancellationToken cancellationToken);

    /// <summary>Parks a message: it is done for the group's checkpoint and kept for replay.</summary>
    /// <param name="message">The parked message.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the row exists.</returns>
    Task ParkAsync(ParkedMessage message, CancellationToken cancellationToken);

    /// <summary>Removes a parked message: it was replayed and acknowledged, or skipped.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="group">The group.</param>
    /// <param name="position">The event's global position, the row's key.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the row is gone.</returns>
    Task UnparkAsync(string stream, string group, long position, CancellationToken cancellationToken);

    /// <summary>The parked messages of a group marked for replay, in the order they were parked.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="group">The group.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The messages.</returns>
    Task<IReadOnlyList<ParkedMessage>> ReplayableAsync(string stream, string group, CancellationToken cancellationToken);

    /// <summary>Marks parked messages for replay: all of the group's, or the one with a number.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="group">The group.</param>
    /// <param name="number">The message's number, or <see langword="null"/> for all.</param>
    /// <param name="by">Which of the message's numbers <paramref name="number"/> is: the one the group speaks.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>How many messages were marked.</returns>
    Task<int> MarkForReplayAsync(string stream, string group, long? number, ParkedNumber by, CancellationToken cancellationToken);

    /// <summary>
    /// Takes or renews a lease. A lease that is free, expired, or already the owner's is granted;
    /// one another owner holds is not.
    /// </summary>
    /// <param name="name">What is leased, a group's name.</param>
    /// <param name="owner">The instance asking.</param>
    /// <param name="duration">How long the lease lasts from now.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><see langword="null"/> when granted; otherwise the id of the owner that holds it.</returns>
    Task<string?> AcquireLeaseAsync(string name, string owner, TimeSpan duration, CancellationToken cancellationToken);

    /// <summary>Releases a lease the owner holds; a lease held by someone else is left alone.</summary>
    /// <param name="name">What is leased.</param>
    /// <param name="owner">The instance releasing.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the lease is released.</returns>
    Task ReleaseLeaseAsync(string name, string owner, CancellationToken cancellationToken);
}
