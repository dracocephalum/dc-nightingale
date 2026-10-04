namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// The default host's switches for looking at a database's schema and bringing it up to date
/// without serving: an operator's dry run and a deployment's migrate step. A host of its own
/// calls <see cref="NightingaleSchemaExtensions"/> instead.
/// </summary>
public static class SchemaSwitches
{
    /// <summary>Prints what the database needs and exits; the exit code is 0 when it is current, 1 when it is not.</summary>
    public const string Report = "--schema-report";

    /// <summary>Brings the database up to date and exits.</summary>
    public const string Apply = "--apply-schema";
}
