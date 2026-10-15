using System.Data;
using System.Globalization;
using System.Text;

using Microsoft.Data.SqlClient;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// Checks the streams an append to several streams names without writing them, in the append's
/// own transaction, after the store's statements and before its commit. The streams' rows are read
/// under an update lock held to the commit, a range lock where a stream has no row, so no append
/// to a checked stream, and no stream begun under a checked name, can commit between the check and
/// the append's commit: the check holds at the commit, not only when it was read. The store's own
/// check of a stream without events is a read without a lock, which an append landing in between
/// would pass, and is not used.
/// <para>
/// One statement for every check, its own command, over the streams' key, in the tenant's
/// partition on a store partitioned by tenant. A check that does not hold refuses the whole
/// append with every check that did not, numbered after the appends, as the caller gave them.
/// An append without checks adds no participant and pays nothing.
/// </para>
/// </summary>
/// <param name="schema">The schema of the store's tables.</param>
/// <param name="tenantId">The tenant the append writes in.</param>
/// <param name="partitionedByTenant">Whether the store is partitioned by tenant, which keys its streams by the tenant's partition key too.</param>
/// <param name="checks">The checks, in the order the caller gave them.</param>
/// <param name="firstIndex">The index of the first check among the append's conditions: the number of appends before it.</param>
internal sealed class StreamStateChecker(string schema, string tenantId, bool partitionedByTenant, IReadOnlyList<StreamCheck> checks, int firstIndex) : global::Polecat.ITransactionParticipant
{
    /// <inheritdoc/>
    public async Task BeforeCommitAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        await using var command = new SqlCommand(BuildSql(), connection, transaction);
        command.Parameters.Add("@tenant", SqlDbType.VarChar, 250).Value = tenantId;
        for (var i = 0; i < checks.Count; i++)
        {
            command.Parameters.Add($"@stream{i}", SqlDbType.VarChar, 250).Value = checks[i].Stream;
        }

        var found = new Dictionary<string, long>(StringComparer.Ordinal);
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                found[reader.GetString(0)] = reader.GetInt64(1) - 1;
            }
        }

        var conflicts = new List<StreamConflict>();
        for (var i = 0; i < checks.Count; i++)
        {
            var actual = found.TryGetValue(checks[i].Stream, out var last) ? last : -1;
            if (!StreamAppends.Holds(checks[i].Expected, actual))
            {
                conflicts.Add(new StreamConflict(firstIndex + i, checks[i].Stream, checks[i].Expected, actual));
            }
        }

        if (conflicts.Count > 0)
        {
            throw new StreamConflictsException(conflicts);
        }
    }

    private static string Quote(string identifier) => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    private string BuildSql()
    {
        var sql = new StringBuilder();
        sql.Append(CultureInfo.InvariantCulture, $"SELECT s.id, s.version FROM {Quote(schema)}.[pc_streams] AS s WITH (UPDLOCK, HOLDLOCK) WHERE s.tenant_id = @tenant AND s.id IN (");
        for (var i = 0; i < checks.Count; i++)
        {
            sql.Append(CultureInfo.InvariantCulture, $"{(i == 0 ? string.Empty : ", ")}@stream{i}");
        }

        sql.Append(')');
        if (partitionedByTenant)
        {
            sql.Append(CultureInfo.InvariantCulture, $" AND s.[{Data.EventsDbContext.TenantPartitionColumn}] = (SELECT p.ordinal FROM {Quote(schema)}.[pc_tenant_partitions] AS p WHERE p.tenant_id = @tenant)");
        }

        return sql.ToString();
    }
}
