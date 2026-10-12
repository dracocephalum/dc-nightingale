using System.Text.RegularExpressions;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// The batches a schema script runs as, and what is known of each from its text: the server
/// executes the statements the two schema tools write, one batch at a time, and this is where a
/// script becomes batches and a batch says whether it builds an index.
/// </summary>
internal static partial class SchemaBatches
{
    /// <summary>
    /// Splits a script on its <c>GO</c> lines, the batch separator the migrations' script is
    /// written with, which is the tooling's and not SQL Server's; a batch that is blank is dropped.
    /// </summary>
    /// <param name="script">The script.</param>
    /// <returns>The batches, in order, each trimmed.</returns>
    public static IReadOnlyList<string> SplitOnGo(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        return GoLineRegex().Split(script)
            .Select(batch => batch.Trim())
            .Where(batch => batch.Length > 0)
            .ToList();
    }

    /// <summary>
    /// Whether a batch creates or alters an index: the statements that can take hours on a large
    /// table, and so the ones a host's index timeout is for. A batch that creates a table and its
    /// indexes counts too, which costs nothing, since the table is empty.
    /// </summary>
    /// <param name="batch">The batch.</param>
    /// <returns>True when a statement in it is <c>CREATE INDEX</c> or <c>ALTER INDEX</c>.</returns>
    public static bool BuildsAnIndex(string batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return IndexStatementRegex().IsMatch(batch);
    }

    /// <summary>The first line of a batch that says anything, for a log entry that names it.</summary>
    /// <param name="batch">The batch.</param>
    /// <returns>The first non-blank line, trimmed.</returns>
    public static string FirstLineOf(string batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        foreach (var line in batch.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                return trimmed;
            }
        }

        return string.Empty;
    }

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoLineRegex();

    [GeneratedRegex(@"\b(CREATE\s+(UNIQUE\s+)?((NON)?CLUSTERED\s+)?INDEX|ALTER\s+INDEX)\b", RegexOptions.IgnoreCase)]
    private static partial Regex IndexStatementRegex();
}
