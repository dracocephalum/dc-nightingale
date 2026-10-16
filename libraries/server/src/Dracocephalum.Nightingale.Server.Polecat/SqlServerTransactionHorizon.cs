using Dracocephalum.Nightingale.Server.Sequencing;
using Microsoft.Data.SqlClient;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// The transaction horizon of a SQL Server journal, read through <c>DBCC OPENTRAN</c>: the oldest
/// transaction still open in the database that has written to it, with its session and when it
/// began, which holds every sequence alike. Its login is read from the security id the command
/// reports, which it cuts to 46 characters, too short for a Windows login's, so for those it is
/// read from the session list, where the permission lets the gateway see it. It needs
/// <c>VIEW DATABASE STATE</c> on the journal alone; <see cref="SqlServerPartitionHorizon"/> reads
/// more, by store partition, and needs a permission on the whole server. A transaction that has
/// only read is not among them; one that has written is, from its first write, and a position is
/// taken only by the insert of an event, which the store's append makes after writing the
/// stream's row: no transaction holds a position unseen, so the minimum age is zero.
/// </summary>
/// <param name="connections">Opens a connection to the journal.</param>
internal sealed class SqlServerTransactionHorizon(Func<CancellationToken, Task<SqlConnection>> connections) : ITransactionHorizon
{
    // The oldest open transaction that has written, read into a table variable, so the start is
    // read here in the same session language it was written in.
    private static readonly string OldestOpenTransaction =
        "SET NOCOUNT ON;"
        + SqlServerHorizon.TimeZoneCheck
        + " DECLARE @open TABLE (k nvarchar(128) NOT NULL, v sql_variant NULL);"
        + " INSERT INTO @open (k, v) EXEC (N'DBCC OPENTRAN WITH TABLERESULTS, NO_INFOMSGS');"
        + " IF EXISTS (SELECT 1 FROM @open WHERE k = N'OLDACT_SPID')"
        + " SELECT TRY_CAST(CAST((SELECT v FROM @open WHERE k = N'OLDACT_SPID') AS nvarchar(32)) AS int),"
        + " COALESCE(SUSER_SNAME(TRY_CONVERT(varbinary(85), CAST((SELECT v FROM @open WHERE k = N'OLDACT_SID') AS nvarchar(200)), 1)), (SELECT login_name FROM sys.dm_exec_sessions WHERE session_id = TRY_CAST(CAST((SELECT v FROM @open WHERE k = N'OLDACT_SPID') AS nvarchar(32)) AS int))),"
        + " " + SqlServerHorizon.ToUtc("TRY_CONVERT(datetime, CAST((SELECT v FROM @open WHERE k = N'OLDACT_STARTTIME') AS nvarchar(64)), 109)") + ";";

    /// <inheritdoc/>
    public TimeSpan MinimumAge => TimeSpan.Zero;

    /// <summary>A horizon over the journal a connection string names.</summary>
    /// <param name="connectionString">The connection string.</param>
    /// <returns>The horizon.</returns>
    public static SqlServerTransactionHorizon Over(string connectionString) => new(SqlServerHorizon.Connect(connectionString));

    /// <inheritdoc/>
    public async ValueTask<HorizonAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ReadOldestAsync(cancellationToken).ConfigureAwait(false);
            return HorizonAvailability.Available;
        }
        catch (SqlException exception) when (SqlServerHorizon.IsUnanswered(exception))
        {
            return HorizonAvailability.Unavailable(
                exception.Number == 50000
                    ? exception.Message
                    : $"The login cannot read the journal's open transactions ({exception.Message}). Grant it VIEW DATABASE STATE on the journal: GRANT VIEW DATABASE STATE TO [<the gateway's user>];");
        }
    }

    /// <inheritdoc/>
    public ValueTask<HorizonMarker> MarkAsync(CancellationToken cancellationToken) => SqlServerHorizon.MarkAsync(connections, cancellationToken);

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyDictionary<SequenceKey, HorizonHolder>> FindHoldersAsync(IReadOnlyDictionary<SequenceKey, HorizonMarker> markers, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(markers);
        var oldest = await ReadOldestAsync(cancellationToken).ConfigureAwait(false);

        // A sequence whose marker the oldest began after is clear: so did every other, and none
        // can hold a position taken before it.
        return oldest is null
            ? new Dictionary<SequenceKey, HorizonHolder>()
            : markers.Where(pair => SqlServerHorizon.BeganBefore(oldest.StartedAt, pair.Value)).ToDictionary(pair => pair.Key, _ => oldest);
    }

    private async Task<HorizonHolder?> ReadOldestAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = OldestOpenTransaction;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? await SqlServerHorizon.ReadHolderAsync(reader, 0, cancellationToken).ConfigureAwait(false) : null;
    }
}
