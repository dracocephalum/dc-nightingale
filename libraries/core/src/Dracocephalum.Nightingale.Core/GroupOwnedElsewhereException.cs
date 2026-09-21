using System.Globalization;

namespace Dracocephalum.Nightingale;

/// <summary>
/// The persistent-subscription group runs in another server instance, the one holding its lease,
/// and this instance does not serve it. The owner's address, when the owner advertised one, is
/// where the same call succeeds; the client follows it on its own, the way the reference client
/// follows a not-leader answer to the leader.
/// </summary>
public sealed class GroupOwnedElsewhereException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="GroupOwnedElsewhereException"/> class.</summary>
    /// <param name="stream">The stream name.</param>
    /// <param name="group">The group name.</param>
    /// <param name="owner">The id of the instance that owns the group.</param>
    /// <param name="address">Where that instance is reached, or <see langword="null"/> when it advertised none.</param>
    public GroupOwnedElsewhereException(string stream, string group, string owner, Uri? address = null)
        : base(string.Format(
            CultureInfo.InvariantCulture,
            address is null
                ? "Group '{1}' on stream '{0}' is owned by instance {2}, which advertises no address."
                : "Group '{1}' on stream '{0}' is owned by instance {2} at {3}.",
            stream,
            group,
            owner,
            address))
    {
        Stream = stream;
        Group = group;
        Owner = owner;
        Address = address;
    }

    /// <summary>Gets the stream name.</summary>
    public string Stream { get; }

    /// <summary>Gets the group name.</summary>
    public string Group { get; }

    /// <summary>Gets the id of the instance that owns the group.</summary>
    public string Owner { get; }

    /// <summary>Gets where the owner is reached, or <see langword="null"/> when it advertised no address.</summary>
    public Uri? Address { get; }
}
