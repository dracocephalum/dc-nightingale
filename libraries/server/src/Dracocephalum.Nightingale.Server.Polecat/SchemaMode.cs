namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// What a start may do to the database, each value allowing what the one before it allows and
/// more. The convention a server follows is that its database is ready when it starts, the way
/// a service assumes its migrations ran somewhere else; the two other modes are for a host that
/// would rather the server did that work itself, a development box or a small deployment, and
/// the default host's switches do the same work once, for a pipeline. Whatever the mode, every
/// start takes the schema lock, so instances starting together never read or change a database
/// half-way through another's work.
/// </summary>
public enum SchemaMode
{
    /// <summary>
    /// Check the database and refuse when it is not what this server expects: missing, not
    /// initialized, behind, newer, or initialized with other settings. Never changes it. The
    /// default.
    /// </summary>
    Verify,

    /// <summary>
    /// Apply what an initialized store is behind on: changes to the event store's tables, then
    /// the gateway's pending migrations. Never creates or initializes a database.
    /// </summary>
    Migrate,

    /// <summary>
    /// Create a missing database, initialize an empty one, finish an initialization that died
    /// half-way, and apply what an initialized store is behind on. Idempotent: an initialized,
    /// current store is served as it is.
    /// </summary>
    Initialize,
}
