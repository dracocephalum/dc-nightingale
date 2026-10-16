using System.Globalization;

using Dracocephalum.Nightingale.Server.Data;
using Dracocephalum.Nightingale.Server.Sequencing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// The ordinal sequencer on SQL Server, a batch: one statement over the events table's range from
/// the progress to the batch's end, read by the table's key (in the tenant's partition under a
/// sequence per tenant) into a table variable, every link of the batch made from it, and the
/// progress row moved in the same transaction, read under an update lock. The host around it, the
/// lease and the loop, and the follower, are the server's own.
/// </summary>
/// <param name="connectionString">The connection string the store uses.</param>
/// <param name="schemaName">The schema the event store's tables live in.</param>
/// <param name="progressSchemaName">The schema the gateway's own tables live in, the progress rows and the ordinal links among them.</param>
/// <param name="logger">The logger.</param>
internal sealed partial class SqlServerOrdinalSequencer(string connectionString, string schemaName, string progressSchemaName, ILogger<SqlServerOrdinalSequencer> logger) : IOrdinalSequencer
{
    /// <summary>How many positions one batch numbers at most.</summary>
    public const int BatchSize = 5_000;

    /// <inheritdoc/>
    public async Task<long> SequenceBatchAsync(string? tenantId, long head, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // The shared sequence's row is the one whose tenant is null.
        var row = tenantId is null ? "[TenantId] IS NULL" : "[TenantId] = @tenant";
        long from;
        await using (var progress = connection.CreateCommand())
        {
            progress.Transaction = transaction;
            progress.CommandText = string.Format(CultureInfo.InvariantCulture, "SELECT [Position] FROM {0} WITH (UPDLOCK, HOLDLOCK) WHERE {1}", ProgressTable, row);
            AddTenant(progress, tenantId);
            var value = await progress.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is long known)
            {
                from = known;
            }
            else
            {
                from = 0;
                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = string.Format(CultureInfo.InvariantCulture, "INSERT INTO {0} ([Id], [TenantId], [Position]) VALUES (@id, {1}, 0)", ProgressTable, tenantId is null ? "NULL" : "@tenant");
                insert.Parameters.AddWithValue("@id", Guid.NewGuid());
                AddTenant(insert, tenantId);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        // The progress row is written only when it moves; an empty range costs one seek.
        var to = Math.Max(from, Math.Min(head, from + BatchSize));
        await using (var number = connection.CreateCommand())
        {
            // The batch is the range from the progress to its end, read by the table's key: every
            // row there is committed, since the head passes a missing position only once it is
            // final. Under a sequence per tenant a position is only unique with its tenant, so the
            // batch is the tenant's rows alone, read in the tenant's partition. It is read once,
            // into a table variable, so every link is made from the same rows.
            //
            // Each key's next ordinal is the last one assigned to it plus one, found with one
            // backward seek in the links' key; the rows of the batch are then numbered in position
            // order per key. The previous ordinal is -1 when a key is new, so the first ordinal is
            // 0. Every event links into its type and its category, into its correlation when it
            // has one, and into the listing of every stream and of its category's streams when it
            // begins its stream; the type's link is the witness that the event is numbered.
            number.Transaction = transaction;
            number.CommandText = string.Format(
                CultureInfo.InvariantCulture,
                "DECLARE @numbered int;{2}"
                + " DECLARE @batch TABLE (seq_id bigint NOT NULL, tenant_id varchar(250) NOT NULL, category varchar(250) NOT NULL, [type] varchar(500) NOT NULL, correlation_id varchar(250) NULL, starts bit NOT NULL, PRIMARY KEY (tenant_id, seq_id));"
                + " INSERT INTO @batch (seq_id, tenant_id, category, [type], correlation_id, starts)"
                + " SELECT seq_id, tenant_id, category, [type], correlation_id, CASE WHEN version = 1 THEN 1 ELSE 0 END"
                + " FROM {0} WHERE seq_id > @from AND seq_id <= @to{3};"
                + " SET @numbered = @@ROWCOUNT;"
                + " WITH keyed AS ("
                + " SELECT CAST({4} AS tinyint) AS link_type, tenant_id, [type] AS link_key, seq_id FROM @batch"
                + " UNION ALL SELECT CAST({5} AS tinyint), tenant_id, category, seq_id FROM @batch"
                + " UNION ALL SELECT CAST({6} AS tinyint), tenant_id, correlation_id, seq_id FROM @batch WHERE correlation_id IS NOT NULL"
                + " UNION ALL SELECT CAST({7} AS tinyint), tenant_id, '', seq_id FROM @batch WHERE starts = 1"
                + " UNION ALL SELECT CAST({8} AS tinyint), tenant_id, category, seq_id FROM @batch WHERE starts = 1),"
                + " numbered AS ("
                + " SELECT link_type, tenant_id, link_key, seq_id, ROW_NUMBER() OVER (PARTITION BY link_type, tenant_id, link_key ORDER BY seq_id) AS n FROM keyed),"
                + " lasts AS ("
                + " SELECT k.link_type, k.tenant_id, k.link_key, ISNULL(last.[Ordinal], -1) AS last_ordinal"
                + " FROM (SELECT DISTINCT link_type, tenant_id, link_key FROM keyed) k"
                + " OUTER APPLY (SELECT TOP (1) l.[Ordinal] FROM {9} l WHERE l.[Type] = k.link_type AND l.[TenantId] = k.tenant_id AND l.[Key] = k.link_key ORDER BY l.[Ordinal] DESC) last)"
                + " INSERT INTO {9} ([Type], [TenantId], [Key], [Ordinal], [SeqId])"
                + " SELECT n.link_type, n.tenant_id, n.link_key, a.last_ordinal + n.n, n.seq_id"
                + " FROM numbered n JOIN lasts a ON a.link_type = n.link_type AND a.tenant_id = n.tenant_id AND a.link_key = n.link_key;"
                + "{1}"
                + " SELECT @numbered;",
                EventsTable,
                to > from ? string.Format(CultureInfo.InvariantCulture, " UPDATE {0} SET [Position] = @to WHERE {1};", ProgressTable, row) : string.Empty,
                tenantId is null ? string.Empty : string.Format(CultureInfo.InvariantCulture, " DECLARE @partition int = (SELECT [ordinal] FROM {0} WHERE [tenant_id] = @tenant);", PartitionsTable),
                tenantId is null ? string.Empty : " AND tenant_ordinal = @partition AND tenant_id = @tenant",
                (int)OrdinalLinkType.EventType,
                (int)OrdinalLinkType.Category,
                (int)OrdinalLinkType.Correlation,
                (int)OrdinalLinkType.Streams,
                (int)OrdinalLinkType.CategoryStreams,
                LinksTable);
            number.Parameters.AddWithValue("@from", from);
            number.Parameters.AddWithValue("@to", to);
            AddTenant(number, tenantId);

            var rows = (int)(await number.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            LogNumbered(tenantId ?? "the shared sequence", to, rows);
        }

        return to;
    }

    // The schema name comes from the store's options, never from a request; it is bracketed as an
    // identifier all the same.
    private string Schema => "[" + schemaName.Replace("]", "]]", StringComparison.Ordinal) + "]";

    private string EventsTable => Schema + ".[pc_events]";

    private string PartitionsTable => Schema + ".[pc_tenant_partitions]";

    private string ProgressTable => GatewayTable(nameof(OrdinalSequencingProgress));

    private string LinksTable => GatewayTable(nameof(OrdinalLink));

    private string GatewayTable(string name) => "[" + progressSchemaName.Replace("]", "]]", StringComparison.Ordinal) + "].[" + name + "]";

    /// <summary>
    /// The tenant as a parameter, typed as both its columns are: a name sent as Unicode to a
    /// column that is not makes the database convert the column and scan the index it should seek.
    /// </summary>
    private static void AddTenant(SqlCommand command, string? tenantId)
    {
        if (tenantId is not null)
        {
            command.Parameters.Add("@tenant", System.Data.SqlDbType.VarChar, 250).Value = tenantId;
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Numbered {Sequence} through position {Through}: {Rows} rows.")]
    private partial void LogNumbered(string sequence, long through, int rows);
}
