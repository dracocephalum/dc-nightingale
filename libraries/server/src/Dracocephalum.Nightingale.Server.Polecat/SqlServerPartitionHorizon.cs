using System.Globalization;
using System.Text;
using System.Text.Json;

using Dracocephalum.Nightingale.Server.Sequencing;
using Microsoft.Data.SqlClient;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// The transaction horizon of each store partition of a SQL Server journal: a sequence is held
/// only by an open transaction that has written to the database since before its marker and holds
/// a lock in the sequence's partition of the store's tables. An append writes its stream's row
/// before it draws a position, and holds the row's lock to its end, so a transaction that may
/// still commit a position holds a lock in its partition of the streams table from before it took
/// it. A transaction open elsewhere in the database, in another tenant's partition or on tables
/// that are not the store's, holds nothing back. Both of its views need
/// <c>VIEW SERVER PERFORMANCE STATE</c>, on the whole server, which answers
/// <see cref="SqlServerTransactionHorizon"/>'s query too.
/// It asks in two stages. The database's writers first, one cheap read: when none began before a
/// marker, the marker has passed, as the transaction horizon would say. The locks only while a
/// writer older than a marker is open and the marker is past <see cref="LockReadDelay"/>: reading
/// them walks every lock on the server, and the writer is most often the append a moment from its
/// commit, which the next look finds gone. Until then the oldest writer holds the sequence. One
/// read of the locks answers every sequence asked. A sequence whose partition the store's registry
/// does not name, the shared sequence or a tenant not yet in it, is held by a lock anywhere in the
/// tables.
/// </summary>
internal sealed class SqlServerPartitionHorizon : ITransactionHorizon
{
    /// <summary>How old a marker is before the locks are read for it rather than the oldest writer taken to hold it.</summary>
    public static readonly TimeSpan LockReadDelay = TimeSpan.FromSeconds(1);

    // The database's writers: its transactions that have logged a change, with when they first did.
    private static readonly string WritersWithStart =
        "SELECT dt.transaction_id, st.session_id, s.login_name, " + SqlServerHorizon.ToUtc("dt.database_transaction_begin_time") + " AS started_at"
        + " FROM sys.dm_tran_database_transactions dt"
        + " LEFT JOIN sys.dm_tran_session_transactions st ON st.transaction_id = dt.transaction_id"
        + " LEFT JOIN sys.dm_exec_sessions s ON s.session_id = st.session_id"
        + " WHERE dt.database_id = DB_ID() AND dt.database_transaction_begin_time IS NOT NULL";

    // The server's clock and the oldest writer of the database, if any: one row whatever is open.
    private static readonly string OldestWriter =
        "SET NOCOUNT ON;"
        + SqlServerHorizon.TimeZoneCheck
        + " SELECT SYSUTCDATETIME(), CASE WHEN w.transaction_id IS NULL THEN 0 ELSE 1 END, w.session_id, w.login_name, w.started_at FROM (SELECT 1 AS one) o"
        + " OUTER APPLY (SELECT TOP (1) * FROM (" + WritersWithStart + ") x ORDER BY x.started_at) w;";

    private readonly Func<CancellationToken, Task<SqlConnection>> _connections;
    private readonly IReadOnlyList<WatchedTable> _tables;
    private readonly TimeSpan _lockReadDelay;
    private readonly string _lockHolders;

    /// <summary>Initializes a new instance of the <see cref="SqlServerPartitionHorizon"/> class.</summary>
    /// <param name="connections">Opens a connection to the journal.</param>
    /// <param name="tables">The tables whose locks hold a sequence: the store's streams and events tables.</param>
    /// <param name="registry">The store's registry of tenants' partition keys, <c>tenant_id</c> to <c>ordinal</c>; <see langword="null"/> where the tables are not partitioned.</param>
    /// <param name="lockReadDelay">How old a marker is before the locks are read for it; <see cref="LockReadDelay"/> when not given.</param>
    public SqlServerPartitionHorizon(Func<CancellationToken, Task<SqlConnection>> connections, IReadOnlyList<WatchedTable> tables, string? registry, TimeSpan? lockReadDelay = null)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(tables);
        if (tables.Count == 0 || (registry is null && tables.Any(table => table.PartitionFunction is not null)))
        {
            throw new ArgumentException("A horizon watches at least one table, and a partitioned one only with the registry that names each tenant's partition.", nameof(tables));
        }

        _connections = connections;
        _tables = tables;
        _lockReadDelay = lockReadDelay ?? LockReadDelay;
        _lockHolders = ComposeLockHolders(tables, registry);
    }

    /// <inheritdoc/>
    public TimeSpan MinimumAge => TimeSpan.Zero;

    /// <summary>The horizon of a store's tables in a schema, by tenant partition when the store is partitioned by tenant.</summary>
    /// <param name="connectionString">The journal's connection string.</param>
    /// <param name="schema">The store's schema.</param>
    /// <param name="partitionedByTenant">Whether the store is partitioned by tenant.</param>
    /// <returns>The horizon.</returns>
    public static SqlServerPartitionHorizon OverStore(string connectionString, string schema, bool partitionedByTenant)
    {
        ArgumentException.ThrowIfNullOrEmpty(schema);
        WatchedTable Watch(string table) => new(Quote(schema) + "." + Quote(table), partitionedByTenant ? "pf_" + table + "_tenant_ordinal" : null);
        return new SqlServerPartitionHorizon(
            SqlServerHorizon.Connect(connectionString),
            [Watch("pc_streams"), Watch("pc_events")],
            partitionedByTenant ? Quote(schema) + ".[pc_tenant_partitions]" : null);
    }

    /// <inheritdoc/>
    public async ValueTask<HorizonAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ReadOldestWriterAsync(cancellationToken).ConfigureAwait(false);
            await ReadLockHoldersAsync([SequenceKey.Shared], cancellationToken).ConfigureAwait(false);
            foreach (var table in _tables)
            {
                if (!await SeesPartitionsAsync(table, cancellationToken).ConfigureAwait(false))
                {
                    // Locks on a table whose partitions cannot be seen would match none, and a
                    // sequence held there would read as clear.
                    return HorizonAvailability.Unavailable($"The login cannot see the partitions of {table.Name}, so its locks cannot be told apart. Grant it VIEW DEFINITION on the table.");
                }
            }

            return HorizonAvailability.Available;
        }
        catch (SqlException exception) when (SqlServerHorizon.IsUnanswered(exception))
        {
            return HorizonAvailability.Unavailable(
                exception.Number == 50000
                    ? exception.Message
                    : $"The login cannot read the database's open transactions and locks ({exception.Message}). Grant it VIEW SERVER PERFORMANCE STATE, on the server: GRANT VIEW SERVER PERFORMANCE STATE TO [<the gateway's login>];");
        }
    }

    /// <inheritdoc/>
    public ValueTask<HorizonMarker> MarkAsync(CancellationToken cancellationToken) => SqlServerHorizon.MarkAsync(_connections, cancellationToken);

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyDictionary<SequenceKey, HorizonHolder>> FindHoldersAsync(IReadOnlyDictionary<SequenceKey, HorizonMarker> markers, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(markers);
        var holders = new Dictionary<SequenceKey, HorizonHolder>();
        var (now, oldest) = await ReadOldestWriterAsync(cancellationToken).ConfigureAwait(false);
        if (oldest is null)
        {
            return holders;
        }

        var locked = new List<SequenceKey>();
        foreach (var (sequence, marker) in markers)
        {
            if (!SqlServerHorizon.BeganBefore(oldest.StartedAt, marker))
            {
                continue;
            }

            if (now.Ticks - marker.Value < _lockReadDelay.Ticks)
            {
                holders[sequence] = oldest;
            }
            else
            {
                locked.Add(sequence);
            }
        }

        if (locked.Count == 0)
        {
            return holders;
        }

        // The oldest holder of each sequence's partition that began before its marker.
        var byLocks = await ReadLockHoldersAsync(locked, cancellationToken).ConfigureAwait(false);
        foreach (var (sequence, candidates) in byLocks)
        {
            var marker = markers[sequence];
            if (candidates.Where(holder => SqlServerHorizon.BeganBefore(holder.StartedAt, marker)).OrderBy(holder => holder.StartedAt ?? DateTimeOffset.MinValue).FirstOrDefault() is { } holder)
            {
                holders[sequence] = holder;
            }
        }

        return holders;
    }

    /// <summary>
    /// Composes the read of the locks: every lock a transaction holds on the tables, its partition
    /// when it holds a part of one, none when it holds the whole table; then, for each sequence
    /// asked, the writers holding one in its partition. Each sequence's partition is found from
    /// the registry by its tenant, through the table's partition function.
    /// </summary>
    private static string ComposeLockHolders(IReadOnlyList<WatchedTable> tables, string? registry)
    {
        var sql = new StringBuilder("SET NOCOUNT ON;").Append(SqlServerHorizon.TimeZoneCheck);
        for (var i = 0; i < tables.Count; i++)
        {
            sql.Append(CultureInfo.InvariantCulture, $" DECLARE @o{i} int = OBJECT_ID(N'{tables[i].Name.Replace("'", "''", StringComparison.Ordinal)}');");
        }

        var objects = string.Join(", ", tables.Select((_, i) => "@o" + i.ToString(CultureInfo.InvariantCulture)));
        sql.Append(" DECLARE @held TABLE (owner bigint NOT NULL, object_id int NOT NULL, partition_number int NULL);")
            .Append(" INSERT INTO @held SELECT DISTINCT l.request_owner_id, COALESCE(p.object_id, CAST(l.resource_associated_entity_id AS int)), p.partition_number")
            .Append(" FROM sys.dm_tran_locks l")
            .Append(" LEFT JOIN sys.partitions p ON p.hobt_id = l.resource_associated_entity_id AND l.resource_type IN (N'KEY', N'PAGE', N'RID', N'HOBT') AND p.index_id IN (0, 1)")
            .Append(" WHERE l.resource_database_id = DB_ID() AND l.request_owner_type = N'TRANSACTION'")
            .Append(CultureInfo.InvariantCulture, $" AND (p.object_id IN ({objects}) OR (l.resource_type = N'OBJECT' AND l.resource_associated_entity_id IN ({objects}) AND l.request_mode IN (N'X', N'SIX')));");

        sql.Append(" DECLARE @sequences TABLE (idx int NOT NULL PRIMARY KEY");
        for (var i = 0; i < tables.Count; i++)
        {
            sql.Append(CultureInfo.InvariantCulture, $", p{i} int NULL");
        }

        sql.Append("); INSERT INTO @sequences SELECT CAST(a.[key] AS int)");
        for (var i = 0; i < tables.Count; i++)
        {
            sql.Append(tables[i].PartitionFunction is { } function
                ? $", CASE WHEN r.ordinal IS NULL THEN NULL ELSE $PARTITION.{Quote(function)}(r.ordinal) END"
                : ", NULL");
        }

        sql.Append(" FROM OPENJSON(@asked) a");
        if (registry is not null)
        {
            sql.Append(CultureInfo.InvariantCulture, $" LEFT JOIN {registry} r ON r.tenant_id = CAST(a.[value] AS varchar(200)) COLLATE DATABASE_DEFAULT");
        }

        var matches = string.Join(" OR ", tables.Select((_, i) => string.Create(CultureInfo.InvariantCulture, $"(h.object_id = @o{i} AND (h.partition_number IS NULL OR a.p{i} IS NULL OR h.partition_number = a.p{i}))")));
        sql.Append("; SELECT DISTINCT a.idx, w.session_id, w.login_name, w.started_at FROM @sequences a")
            .Append(CultureInfo.InvariantCulture, $" JOIN @held h ON {matches}")
            .Append(" JOIN (").Append(WritersWithStart).Append(") w ON w.transaction_id = h.owner;");
        return sql.ToString();
    }

    private static string Quote(string identifier) => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    private async Task<(DateTime Now, HorizonHolder? Oldest)> ReadOldestWriterAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _connections(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = OldestWriter;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return (reader.GetDateTime(0), reader.GetInt32(1) == 0 ? null : await SqlServerHorizon.ReadHolderAsync(reader, 2, cancellationToken).ConfigureAwait(false));
    }

    private async Task<Dictionary<SequenceKey, List<HorizonHolder>>> ReadLockHoldersAsync(List<SequenceKey> sequences, CancellationToken cancellationToken)
    {
        await using var connection = await _connections(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = _lockHolders;
        command.Parameters.Add(new SqlParameter("@asked", System.Data.SqlDbType.NVarChar, -1) { Value = JsonSerializer.Serialize(sequences.Select(sequence => sequence.TenantId)) });
        var holders = new Dictionary<SequenceKey, List<HorizonHolder>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var sequence = sequences[reader.GetInt32(0)];
            if (!holders.TryGetValue(sequence, out var list))
            {
                holders[sequence] = list = [];
            }

            list.Add(await SqlServerHorizon.ReadHolderAsync(reader, 1, cancellationToken).ConfigureAwait(false));
        }

        return holders;
    }

    private async Task<bool> SeesPartitionsAsync(WatchedTable table, CancellationToken cancellationToken)
    {
        await using var connection = await _connections(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CASE WHEN EXISTS (SELECT 1 FROM sys.partitions WHERE object_id = OBJECT_ID(@table) AND index_id IN (0, 1)) THEN 1 ELSE 0 END";
        command.Parameters.Add(new SqlParameter("@table", System.Data.SqlDbType.NVarChar, 512) { Value = table.Name });
        return (int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! == 1;
    }

    /// <summary>A table whose locks hold a sequence, and the partition function it is partitioned by, if any.</summary>
    /// <param name="Name">The table's name, schema-qualified and quoted.</param>
    /// <param name="PartitionFunction">The partition function the table is partitioned by, through the registry's keys; <see langword="null"/> when it is not partitioned.</param>
    internal sealed record WatchedTable(string Name, string? PartitionFunction);
}
