namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// SQL Server identifiers in the statements the backend writes as text. The names come from the
/// host's options, never from a request, and are bracketed all the same.
/// </summary>
internal static class SqlIdentifier
{
    /// <summary>Brackets an identifier, doubling a closing bracket inside it, so that it can only ever be read as a name.</summary>
    /// <param name="identifier">The name of a schema, a table or a column.</param>
    /// <returns>The identifier in brackets.</returns>
    public static string Quote(string identifier) => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";
}
