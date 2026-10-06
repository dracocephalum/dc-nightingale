namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// A consumer's place at a group in this instance, from <see cref="SubscriptionGroupRegistry.JoinAsync"/>
/// until <see cref="SubscriptionGroupRegistry.Leave"/>. The first seat's holder starts the group
/// and tells the others through <see cref="Started"/> or <see cref="Failed"/>; every holder reads
/// the running group from <see cref="Host"/>.
/// </summary>
internal sealed class SubscriptionGroupSeat
{
    internal SubscriptionGroupSeat(SubscriptionGroupRegistry.Entry entry, bool first)
    {
        Entry = entry;
        First = first;
    }

    /// <summary>Gets a value indicating whether this seat's holder is to start the group.</summary>
    public bool First { get; }

    /// <summary>Gets the running group, once the first seat's holder has started it; faulted with the cause when it could not.</summary>
    public Task<SubscriptionGroupHost> Host => Entry.Ready.Task;

    internal SubscriptionGroupRegistry.Entry Entry { get; }

    /// <summary>The group runs.</summary>
    /// <param name="host">The running group.</param>
    public void Started(SubscriptionGroupHost host) => Entry.Ready.TrySetResult(host);

    /// <summary>The group could not be started; every seat's holder gets the cause.</summary>
    /// <param name="cause">Why not.</param>
    public void Failed(Exception cause) => Entry.Ready.TrySetException(cause);
}
