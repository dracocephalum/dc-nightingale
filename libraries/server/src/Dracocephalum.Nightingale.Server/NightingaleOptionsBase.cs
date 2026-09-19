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
}
