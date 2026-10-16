using Dracocephalum.Nightingale.Server.Sequencing;
using Microsoft.Data.SqlClient;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// What the SQL Server horizons share: the marker, on the server's UTC clock, and how a
/// transaction's start, which the database reports on its local clock, is read in UTC. The local
/// clock goes back an hour where the server keeps daylight saving time; the start is converted by
/// the server's own time zone, which reads an hour that happens twice as its first, the earlier
/// instant, so an ambiguous hour can only make a horizon wait, never pass early.
/// </summary>
internal static class SqlServerHorizon
{
    /// <summary>
    /// How much later than a marker a start may read and still count as before it: the database
    /// keeps a start to its <c>datetime</c> type's three hundredth of a second, rounded either way.
    /// </summary>
    public static readonly TimeSpan Rounding = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// Refuses a server whose time zone it cannot convert by: every start would be unreadable,
    /// and every check would wait for good.
    /// </summary>
    public const string TimeZoneCheck =
        " IF NOT EXISTS (SELECT 1 FROM sys.time_zone_info WHERE name = CURRENT_TIMEZONE_ID())"
        + " THROW 50000, N'The server''s time zone is not one it can convert by (sys.time_zone_info), so the start of an open transaction cannot be read in UTC.', 1;";

    /// <summary>The errors of a login without the permission, or of a server without a usable time zone: DBCC refused for the database or at all, a permission refused, and the time zone's own.</summary>
    private static readonly int[] Unanswered = [7983, 2571, 297, 300, 50000];

    /// <summary>Opens connections to the journal a connection string names.</summary>
    /// <param name="connectionString">The connection string.</param>
    /// <returns>Opens a connection.</returns>
    public static Func<CancellationToken, Task<SqlConnection>> Connect(string connectionString)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        return async cancellationToken =>
        {
            var connection = new SqlConnection(connectionString);
            try
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                return connection;
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        };
    }

    /// <summary>A start on the server's local clock, as an expression, read in UTC.</summary>
    /// <param name="local">The expression of the local <c>datetime</c>.</param>
    /// <returns>The expression of the start in UTC.</returns>
    public static string ToUtc(string local) => "(CAST(" + local + " AS datetime2) AT TIME ZONE CURRENT_TIMEZONE_ID()) AT TIME ZONE 'UTC'";

    /// <summary>Marks the present moment on the server's UTC clock.</summary>
    /// <param name="connections">Opens a connection to the journal.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The marker.</returns>
    public static async ValueTask<HorizonMarker> MarkAsync(Func<CancellationToken, Task<SqlConnection>> connections, CancellationToken cancellationToken)
    {
        await using var connection = await connections(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT SYSUTCDATETIME()";
        var now = (DateTime)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        return new HorizonMarker(now.Ticks);
    }

    /// <summary>Whether a transaction that began at a start may have taken a position before a marker: an unreadable start counts as before.</summary>
    /// <param name="startedAt">When the transaction began, in UTC, when it could be read.</param>
    /// <param name="marker">The marker.</param>
    /// <returns>Whether it began before the marker, within the rounding.</returns>
    public static bool BeganBefore(DateTimeOffset? startedAt, HorizonMarker marker) => startedAt is not { } started || started.UtcTicks <= marker.Value + Rounding.Ticks;

    /// <summary>Whether an error says the login, or the server, cannot answer a horizon's query.</summary>
    /// <param name="exception">The error.</param>
    /// <returns>Whether it is one of those.</returns>
    public static bool IsUnanswered(SqlException exception) => Unanswered.Contains(exception.Number);

    /// <summary>Reads a holder from a session id, a login and a start, at an ordinal of a row.</summary>
    /// <param name="reader">The reader, on the row.</param>
    /// <param name="ordinal">The ordinal of the session id; the login and the start follow it.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The holder.</returns>
    public static async Task<HorizonHolder> ReadHolderAsync(SqlDataReader reader, int ordinal, CancellationToken cancellationToken) => new(
        await reader.IsDBNullAsync(ordinal, cancellationToken).ConfigureAwait(false) ? null : reader.GetInt32(ordinal),
        await reader.IsDBNullAsync(ordinal + 1, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(ordinal + 1),
        await reader.IsDBNullAsync(ordinal + 2, cancellationToken).ConfigureAwait(false) ? null : reader.GetDateTimeOffset(ordinal + 2));
}
