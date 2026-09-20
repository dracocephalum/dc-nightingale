namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// Who holds a lease and where they are reached: the instance id the lease names, and the
/// address the instance wrote when it took the lease, when it has one. The address is what a
/// caller is told when a group runs elsewhere, so it can go there itself, the way the reference
/// client follows a not-leader answer to the leader.
/// </summary>
/// <param name="Owner">The instance id.</param>
/// <param name="Address">Where the instance is reached, or <see langword="null"/> when it advertised none.</param>
public sealed record LeaseHolder(string Owner, Uri? Address);
