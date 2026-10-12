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

    /// <summary>Applies what an initialized database is behind on and exits, as <see cref="SchemaMode.Migrate"/> would at startup; a database that is missing or not initialized is refused.</summary>
    public const string Apply = "--apply-schema";

    /// <summary>Creates the database when it is missing, initializes it when it is empty, applies what it is behind on, and exits, as <see cref="SchemaMode.Initialize"/> would at startup.</summary>
    public const string Initialize = "--initialize";
}
