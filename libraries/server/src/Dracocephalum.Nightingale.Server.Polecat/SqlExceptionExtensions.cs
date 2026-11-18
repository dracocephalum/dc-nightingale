using System.Text.RegularExpressions;

using Microsoft.Data.SqlClient;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>What the backend reads off an error the database raised.</summary>
internal static partial class SqlExceptionExtensions
{
    /// <summary>What the column the database named holds, as a caller would call it.</summary>
    public static string DescribeColumn(this SqlException exception)
    {
        var column = ColumnRegex().Match(exception.Message) is { Success: true } match ? match.Groups[1].Value : string.Empty;
        return column switch
        {
            "id" or "stream_id" => ValueTooLongException.StreamName,
            "type" => "event type",
            "correlation_id" => "correlation id",
            "causation_id" => "causation id",
            _ => "value",
        };
    }

    [GeneratedRegex("column '([^']+)'")]
    private static partial Regex ColumnRegex();
}
