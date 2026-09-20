namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// The settings every Nightingale server has, whichever store backs it, bound from the
/// <c>Nightingale</c> configuration section. A backend's options derive from this class and add
/// what is theirs, so a host binds one tree and the server reads the part it owns through the
/// base. The type is also the constraint a registration accepts: whatever options a backend binds,
/// they are a Nightingale options tree. A sub-section's type is nested under the property that
/// binds it.
/// </summary>
public abstract class NightingaleOptionsBase
{
    /// <summary>The configuration section the options are bound from.</summary>
    public const string SectionName = "Nightingale";

    /// <summary>Gets or sets what the server allows to be deleted.</summary>
    public DeletionSettings Deletion { get; set; } = new();

    /// <summary>Gets or sets how this instance takes part in a cluster of instances over one store.</summary>
    public ClusterSettings Cluster { get; set; } = new();

    /// <summary>
    /// Which deletions the server accepts. Both are off by default: a store that never deletes
    /// keeps every event a consumer may still need and makes retention a decision rather than a
    /// habit, and a caller cannot turn deletion on; only the operator can, here.
    /// </summary>
    public sealed class DeletionSettings
    {
        /// <summary>
        /// Gets or sets a value indicating whether a stream may be deleted: its events leave every
        /// read and it cannot be appended to again, but the rows stay in the store.
        /// </summary>
        public bool AllowDelete { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether a stream may be tombstoned: it and its events are
        /// removed for good, and the name reads as one that never existed. Irreversible, which is
        /// why it is a switch of its own.
        /// </summary>
        public bool AllowTombstone { get; set; }
    }

    /// <summary>
    /// How clients find the instance that runs a group. A persistent-subscription group runs in one
    /// instance at a time, the one holding its lease, and the lease carries that instance's
    /// address; an instance asked for a group another runs refuses with that address, and the
    /// client goes there itself, the way the reference client follows a not-leader answer. The
    /// address is derived from what the server listens on unless one is configured here.
    /// </summary>
    public sealed class ClusterSettings
    {
        /// <summary>
        /// Gets or sets the address clients reach this instance at, an absolute http or https URI
        /// such as <c>http://node-1:5000</c>, or null to derive it: the address the server listens
        /// on, with a wildcard host replaced by this machine's name, which in a container or a pod
        /// is the routable one. Set it when that derivation is wrong for the network, such as
        /// behind address translation.
        /// </summary>
        public string? AdvertisedAddress { get; set; }

        /// <summary>Gets the advertised address as a URI, or null when there is none.</summary>
        public Uri? AdvertisedUri =>
            AdvertisedAddress is null ? null : new Uri(AdvertisedAddress, UriKind.Absolute);

        /// <summary>Checks the settings are usable.</summary>
        /// <exception cref="InvalidOperationException">The address is not an absolute http or https URI.</exception>
        public void Validate()
        {
            if (AdvertisedAddress is null)
            {
                return;
            }

            if (!Uri.TryCreate(AdvertisedAddress, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException(
                    $"Nightingale:Cluster:AdvertisedAddress '{AdvertisedAddress}' is not an absolute http or https URI.");
            }
        }
    }
}
