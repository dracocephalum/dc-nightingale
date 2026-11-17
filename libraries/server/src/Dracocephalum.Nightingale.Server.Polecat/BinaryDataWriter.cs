using System.Data;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

using JasperFx.Events;
using Microsoft.Data.SqlClient;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// Writes the binary data of an append's events beside their JSON, in the append's own transaction:
/// a participant the store runs after all of its statements and before its commit, so a reader
/// never sees the JSON without its binary data, and a failure here rolls the whole append back.
/// Fewer rows than events refuses the append rather than store an event without the data it was
/// sent with. Why the store's own binary path is not used, and what its readers make of such a row,
/// are <c>DESIGN.md</c>, seam 1.
/// </summary>
/// <param name="schema">The schema of the store's tables.</param>
/// <param name="tenantId">The tenant the append writes in.</param>
/// <param name="stream">The stream appended to.</param>
/// <param name="partitionedByTenant">Whether the store is partitioned by tenant, which keys its rows by the tenant's partition key too.</param>
/// <param name="events">The appended events with binary data, each with that data.</param>
internal sealed class BinaryDataWriter(string schema, string tenantId, string stream, bool partitionedByTenant, IReadOnlyList<(IEvent Event, ReadOnlyMemory<byte> BinaryData)> events) : global::Polecat.ITransactionParticipant
{
    /// <summary>The error the statement raises when it finds fewer rows than it was given.</summary>
    public const int RowsNotFound = 50_001;

    /// <inheritdoc/>
    public async Task BeforeCommitAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        await using var command = new SqlCommand(BuildSql(), connection, transaction);
        command.Parameters.Add("@tenant", SqlDbType.VarChar, 250).Value = tenantId;
        command.Parameters.Add("@stream", SqlDbType.VarChar, 250).Value = stream;
        for (var i = 0; i < events.Count; i++)
        {
            var (appended, binaryData) = events[i];
            command.Parameters.Add($"@seq{i}", SqlDbType.BigInt).Value = appended.Sequence;
            command.Parameters.Add($"@id{i}", SqlDbType.UniqueIdentifier).Value = appended.Id;
            command.Parameters.Add($"@bdata{i}", SqlDbType.VarBinary, -1).Value = ToArray(binaryData);
        }

        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    /// <summary>The bytes as an array, the one the transport filled when it is exactly theirs, so a large body is not copied again.</summary>
    private static byte[] ToArray(ReadOnlyMemory<byte> bytes) =>
        MemoryMarshal.TryGetArray(bytes, out var segment) && segment.Offset == 0 && segment.Count == segment.Array!.Length
            ? segment.Array
            : bytes.ToArray();

    private static string Quote(string identifier) => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    private string BuildSql()
    {
        // One statement whatever the number of events. Each row is found by its position, which
        // the store has read back by now and which leads the store's key, so it is a seek of the
        // key, in the tenant's partition on a store partitioned by tenant; its id, tenant and
        // stream are checked on the row found.
        var sql = new StringBuilder();
        sql.Append(CultureInfo.InvariantCulture, $"UPDATE e SET bdata = v.bdata FROM {Quote(schema)}.[pc_events] AS e JOIN (VALUES ");
        for (var i = 0; i < events.Count; i++)
        {
            sql.Append(CultureInfo.InvariantCulture, $"{(i == 0 ? string.Empty : ", ")}(@seq{i}, @id{i}, @bdata{i})");
        }

        sql.Append(") AS v (seq_id, id, bdata) ON e.seq_id = v.seq_id AND e.id = v.id WHERE e.tenant_id = @tenant AND e.stream_id = @stream AND e.is_archived = 0");
        if (partitionedByTenant)
        {
            sql.Append(CultureInfo.InvariantCulture, $" AND e.[{Data.EventsDbContext.TenantPartitionColumn}] = (SELECT p.ordinal FROM {Quote(schema)}.[pc_tenant_partitions] AS p WHERE p.tenant_id = @tenant)");
        }

        sql.Append(CultureInfo.InvariantCulture, $"; IF @@ROWCOUNT <> {events.Count} THROW {RowsNotFound}, 'The binary data of an append found fewer of its rows than it has events; the append is rolled back.', 1;");
        return sql.ToString();
    }
}
