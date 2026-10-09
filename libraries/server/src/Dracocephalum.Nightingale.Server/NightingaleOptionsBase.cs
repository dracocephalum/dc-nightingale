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

    /// <summary>The most events one append may carry unless the host says otherwise.</summary>
    public const int DefaultMaxEventsPerAppend = 150;

    /// <summary>
    /// Gets or sets the schema the gateway keeps its own tables in: the groups, their parked events
    /// and outbox, the leases and the settings the store was initialized with. They are created
    /// and changed by the gateway's own migrations, whose history is kept in the same schema. It
    /// may be the schema the event store uses; the names do not collide.
    /// </summary>
    public string Schema { get; set; } = Data.NightingaleSchema.Default;

    /// <summary>
    /// Gets or sets the most events one append may carry. An append with more is refused before
    /// anything is written, and its caller sends several. An append is received in full and
    /// written in one transaction, which holds back live delivery until it commits, so its size
    /// is bounded; and a backend can take only so many events in one write, which is the most
    /// this may be set to.
    /// </summary>
    public int MaxEventsPerAppend { get; set; } = DefaultMaxEventsPerAppend;

    /// <summary>Gets or sets what the server allows to be deleted.</summary>
    public DeletionSettings Deletion { get; set; } = new();

    /// <summary>Gets or sets how this instance takes part in a cluster of instances over one store.</summary>
    public ClusterSettings Cluster { get; set; } = new();

    /// <summary>Gets or sets how callers are authenticated; the <c>Nightingale:Auth</c> section.</summary>
    public AuthSettings Auth { get; set; } = new();

    /// <summary>Gets or sets how tenants are kept; the <c>Nightingale:Tenants</c> section.</summary>
    public TenantSettings Tenants { get; set; } = new();

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

    /// <summary>
    /// How callers are authenticated. On by default: every call but the features call carries a
    /// user name and password in the <c>authorization</c> header, verified against the built-in
    /// administrator, whose password is here so that nothing done through the API can lock
    /// everyone out, or against the credentials table. Off, every caller is the administrator.
    /// </summary>
    public sealed class AuthSettings
    {
        /// <summary>The default number of key-derivation iterations: OWASP's floor for PBKDF2-HMAC-SHA256.</summary>
        public const int DefaultIterations = 600_000;

        /// <summary>Gets or sets a value indicating whether calls are authenticated at all.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Gets or sets the built-in administrator's password; required while authentication is on and the administrator is not disabled.</summary>
        public string? AdminPassword { get; set; }

        /// <summary>Gets or sets a value indicating whether the built-in administrator is refused, once other administrators exist.</summary>
        public bool AdminDisabled { get; set; }

        /// <summary>Gets or sets the pepper: a secret mixed into every password before it is hashed, so a copy of the table alone verifies nothing. Empty for none.</summary>
        public string? Pepper { get; set; }

        /// <summary>Gets or sets the pepper's id, kept with each hash so the pepper can be rotated; the hash says which it was made under.</summary>
        public int PepperId { get; set; } = 1;

        /// <summary>Gets or sets how many key-derivation iterations a new hash uses; a hash made with fewer is remade on the next successful login.</summary>
        public int Iterations { get; set; } = DefaultIterations;

        /// <summary>Gets or sets a value indicating whether credentials are accepted over a connection that is not TLS; off, they are refused there, since Basic sends the password as it is.</summary>
        public bool AllowInsecureTransport { get; set; }

        /// <summary>Gets or sets for how long a verified name and password are remembered, so a slow hash is not recomputed on every call.</summary>
        public TimeSpan VerificationCacheDuration { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>Gets or sets how many wrong passwords in a row lock a credential out; 0 for never.</summary>
        public int LockoutThreshold { get; set; } = 5;

        /// <summary>Gets or sets for how long a locked-out credential is refused.</summary>
        public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(15);

        /// <summary>Gets or sets the shortest password accepted when one is set.</summary>
        public int MinimumPasswordLength { get; set; } = 12;

        /// <summary>Checks the settings against each other.</summary>
        /// <exception cref="InvalidOperationException">A setting is out of range, or the administrator has no password while needed.</exception>
        public void Validate()
        {
            if (Enabled && !AdminDisabled && string.IsNullOrEmpty(AdminPassword))
            {
                throw new InvalidOperationException($"{SectionName}:Auth:{nameof(AdminPassword)} is required while authentication is on and the built-in administrator is not disabled; set it, set {nameof(AdminDisabled)} once other administrators exist, or set {nameof(Enabled)} to false.");
            }

            if (Iterations < 10_000)
            {
                throw new InvalidOperationException($"{SectionName}:Auth:{nameof(Iterations)} must be at least 10000. It was {Iterations}.");
            }

            if (MinimumPasswordLength < 1 || LockoutThreshold < 0 || LockoutDuration < TimeSpan.Zero || VerificationCacheDuration < TimeSpan.Zero)
            {
                throw new InvalidOperationException($"{SectionName}:Auth: {nameof(MinimumPasswordLength)} must be at least 1, and {nameof(LockoutThreshold)}, {nameof(LockoutDuration)} and {nameof(VerificationCacheDuration)} must not be negative.");
            }
        }
    }

    /// <summary>How tenants are kept in memory on each instance.</summary>
    public sealed class TenantSettings
    {
        /// <summary>Gets or sets how often an instance reads the tenants again, which is how a tenant disabled or enabled on another instance is noticed.</summary>
        public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>Checks the settings.</summary>
        /// <exception cref="InvalidOperationException">The interval is not positive.</exception>
        public void Validate()
        {
            if (RefreshInterval <= TimeSpan.Zero)
            {
                throw new InvalidOperationException($"{SectionName}:Tenants:{nameof(RefreshInterval)} must be positive. It was {RefreshInterval}.");
            }
        }
    }
}
