using Dracocephalum.Nightingale.Server.Sequencing;
using Microsoft.Data.SqlClient;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// The transaction horizon of a SQL Server journal, read through <c>DBCC OPENTRAN</c>: the oldest
/// transaction still open in the database that has written to it, with its session and when it
/// began. Its login is read from the security id the command reports, which it cuts to 46
/// characters, too short for a Windows login's, so for those it is read from the session list,
/// where the permission lets the gateway see it. The transaction views say more, every open transaction rather than the oldest,
/// but from SQL Server 2022 they need <c>VIEW SERVER PERFORMANCE STATE</c>, a permission on the
/// whole server; this needs <c>VIEW DATABASE STATE</c>, on the journal alone, and the oldest is all
/// the horizon asks. A transaction that has only read is not among them; one that has written is,
/// from its first write, and a position is taken only by the insert of an event, which the
/// store's append makes after writing the stream's row: no transaction holds a position unseen,
/// so the minimum age is zero.
/// The database reports a transaction's start on its local clock, which goes back an hour where
/// the server keeps daylight saving time. The start is converted to UTC by the server's own time
/// zone, which reads an hour that happens twice as its first, the earlier instant; the marker is
/// the server's UTC clock. A start that may be either instant is therefore taken as the earlier,
/// and an ambiguous hour can only make the horizon wait, never pass early.
/// </summary>
/// <param name="connections">Opens a connection to the journal.</param>
internal sealed class SqlServerTransactionHorizon(Func<CancellationToken, Task<SqlConnection>> connections) : ITransactionHorizon
{
    /// <summary>
    /// How much later than the marker a start may read and still count as before it: the database
    /// keeps a start to its <c>datetime</c> type's three hundredth of a second, rounded either way.
    /// </summary>
    internal static readonly TimeSpan Rounding = TimeSpan.FromMilliseconds(10);

    // The oldest open transaction that has written, read into a table variable, so the start is
    // read here in the same session language it was written in. The time zone is checked first:
    // a zone the server cannot convert by would leave every start unreadable, and every check
    // waiting for good, so it is refused instead.
    private const string OldestOpenTransaction =
        "SET NOCOUNT ON;"
        + " IF NOT EXISTS (SELECT 1 FROM sys.time_zone_info WHERE name = CURRENT_TIMEZONE_ID())"
        + " THROW 50000, N'The server''s time zone is not one it can convert by (sys.time_zone_info), so the start of an open transaction cannot be read in UTC.', 1;"
        + " DECLARE @open TABLE (k nvarchar(128) NOT NULL, v sql_variant NULL);"
        + " INSERT INTO @open (k, v) EXEC (N'DBCC OPENTRAN WITH TABLERESULTS, NO_INFOMSGS');"
        + " IF EXISTS (SELECT 1 FROM @open WHERE k = N'OLDACT_SPID')"
        + " SELECT TRY_CAST(CAST((SELECT v FROM @open WHERE k = N'OLDACT_SPID') AS nvarchar(32)) AS int),"
        + " COALESCE(SUSER_SNAME(TRY_CONVERT(varbinary(85), CAST((SELECT v FROM @open WHERE k = N'OLDACT_SID') AS nvarchar(200)), 1)), (SELECT login_name FROM sys.dm_exec_sessions WHERE session_id = TRY_CAST(CAST((SELECT v FROM @open WHERE k = N'OLDACT_SPID') AS nvarchar(32)) AS int))),"
        + " (CAST(TRY_CONVERT(datetime, CAST((SELECT v FROM @open WHERE k = N'OLDACT_STARTTIME') AS nvarchar(64)), 109) AS datetime2) AT TIME ZONE CURRENT_TIMEZONE_ID()) AT TIME ZONE 'UTC';";

    /// <summary>The errors a login without the permission, or a server without a usable time zone, gets: DBCC refused for the database or at all, a permission refused, and the time zone's own.</summary>
    private static readonly int[] Unanswered = [7983, 2571, 297, 300, 50000];

    /// <inheritdoc/>
    public TimeSpan MinimumAge => TimeSpan.Zero;

    /// <summary>A horizon over the journal a connection string names.</summary>
    /// <param name="connectionString">The connection string.</param>
    /// <returns>The horizon.</returns>
    public static SqlServerTransactionHorizon Over(string connectionString)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        return new SqlServerTransactionHorizon(async cancellationToken =>
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
        });
    }

    /// <inheritdoc/>
    public async ValueTask<HorizonAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ReadOldestAsync(cancellationToken).ConfigureAwait(false);
            return HorizonAvailability.Available;
        }
        catch (SqlException exception) when (Unanswered.Contains(exception.Number))
        {
            return HorizonAvailability.Unavailable(
                exception.Number == 50000
                    ? exception.Message
                    : $"The login cannot read the journal's open transactions ({exception.Message}). Grant it VIEW DATABASE STATE on the journal: GRANT VIEW DATABASE STATE TO [<the gateway's user>];");
        }
    }

    /// <inheritdoc/>
    public async ValueTask<HorizonMarker> MarkAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT SYSUTCDATETIME()";
        var now = (DateTime)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        return new HorizonMarker(now.Ticks);
    }

    /// <inheritdoc/>
    public async ValueTask<HorizonHolder?> FindHolderAsync(HorizonMarker marker, CancellationToken cancellationToken)
    {
        var oldest = await ReadOldestAsync(cancellationToken).ConfigureAwait(false);

        // The oldest began after the marker: so did every other, and none can hold a position
        // taken before it. A start that could not be read counts as before.
        return oldest is { StartedAt: { } started } && started.UtcTicks > marker.Value + Rounding.Ticks ? null : oldest;
    }

    private async Task<HorizonHolder?> ReadOldestAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = OldestOpenTransaction;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new HorizonHolder(
            await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false) ? null : reader.GetInt32(0),
            await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(1),
            await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? null : reader.GetDateTimeOffset(2));
    }
}
