using System.Data;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

using JasperFx.Events;
using Microsoft.Data.SqlClient;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// Writes the binary data of an append's events beside their JSON, in the append's own
/// transaction. The store writes every event as JSON, with its binary column null, because the
/// gateway registers no binary serializer: the store's binary path takes one body or the other
/// and needs a serializer per event type. This participant then sets the binary column of the
/// rows the store inserted, after all of the store's statements and before its commit, which is
/// the store's promise to a transaction participant, so a reader never sees the JSON without
/// its binary data and a failure here rolls the whole append back.
/// <para>
/// One statement whatever the number of events, its own command with its own parameters, so the
/// store's limit on events in one append is untouched. Each row is found by its position, which
/// the store has read back by now, and checked by its id, tenant and stream; the store's key
/// leads with the position, so it is a seek of the key, in the tenant's partition on a store
/// partitioned by tenant. Fewer rows than events refuses the append rather than store an event
/// without the data it was sent with.
/// </para>
/// <para>
/// The store's own readers take a row with both columns for a binary event and refuse it for
/// want of a serializer. The gateway never reads events through them; it reads the table
/// through its own mirror.
/// </para>
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
