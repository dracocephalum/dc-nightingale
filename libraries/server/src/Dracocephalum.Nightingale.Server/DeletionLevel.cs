namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// How far a stream may be taken away, in rising order: each level allows what the levels below
/// it allow. A reset is allowed at every level, since it hides and removes nothing.
/// </summary>
public enum DeletionLevel
{
    /// <summary>Neither archive nor delete: a stream can be reset, never taken away.</summary>
    None = 0,

    /// <summary>Archive: a stream leaves every read and refuses appends, its rows kept in the store.</summary>
    Archive = 1,

    /// <summary>Delete, and archive with it: a stream and its events are removed for good.</summary>
    Delete = 2,
}
