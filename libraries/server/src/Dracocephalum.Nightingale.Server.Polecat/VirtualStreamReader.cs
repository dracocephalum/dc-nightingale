using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Data.SqlClient;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// Reads the virtual streams straight from the events table. The store's own query surface knows
/// nothing of the persisted <c>category</c> column, and its type column is only reachable through
/// its event-type registry, so the predicates are written here as SQL that the filtered indexes
/// answer with a seek: tenant, key, then position, or tenant, key, then ordinal on a store with
/// ordinals. Rows are hydrated into event records the way the store adapter hydrates the store's
/// events, body re-serialized through the shared options and the two reserved metadata keys
/// appended last, so an event reads the same through either path; an integration test holds the
/// two paths to the same bytes.
/// </summary>
/// <param name="connectionString">The connection string the store uses.</param>
/// <param name="schemaName">The schema the events table lives in.</param>
/// <param name="tenantId">The tenant every read is scoped to.</param>
internal sealed class VirtualStreamReader(string connectionString, string schemaName, string tenantId)
{
    private const string Columns = "seq_id, id, stream_id, version, data, type, timestamp, correlation_id, causation_id, headers";

    // The schema name comes from the store's options, never from a request; it is bracketed as an
    // identifier all the same.
    private readonly string _schema = "[" + schemaName.Replace("]", "]]", StringComparison.Ordinal) + "]";

    /// <summary>The bounds of a virtual stream at or below a head.</summary>
    /// <param name="stream">The virtual stream.</param>
    /// <param name="head">The highest position to consider.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The first and last positions, or <see langword="null"/> when there are none.</returns>
    public async Task<StreamHead?> HeadAsync(VirtualStreamName stream, long head, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, stream, string.Format(CultureInfo.InvariantCulture, "SELECT MIN(seq_id), MAX(seq_id) FROM {0} WHERE {1} AND seq_id <= @head", Table, Predicate(stream)));
        command.Parameters.AddWithValue("@head", head);
        return await BoundsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One page of a virtual stream; see the port for the bounds.</summary>
    /// <param name="stream">The virtual stream.</param>
    /// <param name="direction">The direction to read in.</param>
    /// <param name="from">Where to begin, inclusive, in the reading direction.</param>
    /// <param name="head">The highest position a forwards page may hold.</param>
    /// <param name="count">The most events to return.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The events of the page.</returns>
    public async Task<IReadOnlyList<EventRecord>> ReadAsync(VirtualStreamName stream, Direction direction, long from, long head, int count, CancellationToken cancellationToken)
    {
        var range = direction == Direction.Forwards
            ? "seq_id >= @from AND seq_id <= @head ORDER BY seq_id"
            : "seq_id <= @from ORDER BY seq_id DESC";
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, stream, string.Format(CultureInfo.InvariantCulture, "SELECT TOP (@count) {0} FROM {1} WHERE {2} AND {3}", Columns, Table, Predicate(stream), range));
        command.Parameters.AddWithValue("@from", from);
        command.Parameters.AddWithValue("@head", head);
        command.Parameters.AddWithValue("@count", count);
        return await PageAsync(command, count, ordinal: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>How many live events of a virtual stream lie after a position, up to the head.</summary>
    /// <param name="stream">The virtual stream.</param>
    /// <param name="after">The position already delivered, exclusive.</param>
    /// <param name="head">The highest position to count, inclusive.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The count.</returns>
    public async Task<long> CountAsync(VirtualStreamName stream, long after, long head, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, stream, string.Format(CultureInfo.InvariantCulture, "SELECT COUNT_BIG(*) FROM {0} WHERE {1} AND seq_id > @after AND seq_id <= @head", Table, Predicate(stream)));
        command.Parameters.AddWithValue("@after", after);
        command.Parameters.AddWithValue("@head", head);
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>
    /// The bounds of a virtual stream under ordinal numbering: the lowest and highest ordinal
    /// assigned, archived rows included, because a hole is still a number in the sequence.
    /// </summary>
    /// <param name="stream">The virtual stream.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The bounds, or <see langword="null"/> when nothing is numbered.</returns>
    public async Task<StreamHead?> OrdinalHeadAsync(VirtualStreamName stream, CancellationToken cancellationToken)
    {
        var column = OrdinalColumn(stream);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, stream, string.Format(CultureInfo.InvariantCulture, "SELECT MIN({2}), MAX({2}) FROM {0} WHERE {1} AND {2} IS NOT NULL", Table, KeyPredicate(stream), column));
        return await BoundsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One page of a virtual stream by ordinal, live rows only, each record carrying its ordinal.</summary>
    /// <param name="stream">The virtual stream.</param>
    /// <param name="direction">The direction to read in.</param>
    /// <param name="from">The ordinal to begin at, inclusive, in the reading direction.</param>
    /// <param name="count">The most events to return.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The events of the page.</returns>
    public async Task<IReadOnlyList<EventRecord>> ReadByOrdinalAsync(VirtualStreamName stream, Direction direction, long from, int count, CancellationToken cancellationToken)
    {
        var column = OrdinalColumn(stream);
        var range = direction == Direction.Forwards
            ? column + " >= @from ORDER BY " + column
            : column + " <= @from ORDER BY " + column + " DESC";
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, stream, string.Format(CultureInfo.InvariantCulture, "SELECT TOP (@count) {0}, {4} FROM {1} WHERE {2} AND {3}", Columns, Table, Predicate(stream), range, column));
        command.Parameters.AddWithValue("@from", from);
        command.Parameters.AddWithValue("@count", count);
        return await PageAsync(command, count, ordinal: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The position every event up to which has its ordinals; 0 when none has.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The position.</returns>
    public async Task<long> NumberedThroughAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = string.Format(CultureInfo.InvariantCulture, "SELECT ISNULL((SELECT numbered_through FROM {0}.[{1}] WHERE id = 1), CAST(0 AS bigint))", _schema, NightingaleTablesFeature.OrdinalsTable);
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static string KeyPredicate(VirtualStreamName stream) =>
        stream.Kind == VirtualStreamKind.Category
            ? "tenant_id = @tenant AND category = @key"
            : "tenant_id = @tenant AND [type] = @key";

    private static string Predicate(VirtualStreamName stream) => KeyPredicate(stream) + " AND is_archived = 0";

    private static string OrdinalColumn(VirtualStreamName stream) =>
        stream.Kind == VirtualStreamKind.Category ? PatchedEventStoreFeature.CategoryOrdinalColumn : PatchedEventStoreFeature.TypeOrdinalColumn;

    private static async Task<StreamHead?> BoundsAsync(SqlCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.IsDBNull(0))
        {
            return null;
        }

        return new StreamHead(reader.GetInt64(0), reader.GetInt64(1));
    }

    private static async Task<IReadOnlyList<EventRecord>> PageAsync(SqlCommand command, int count, bool ordinal, CancellationToken cancellationToken)
    {
        var records = new List<EventRecord>(count);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(Hydrate(reader, ordinal));
        }

        return records;
    }

    private static EventRecord Hydrate(SqlDataReader reader, bool ordinal)
    {
        // The body is re-serialized through the shared options rather than copied from the column,
        // so it comes back as the store adapter returns it, whatever escaping the store wrote.
        using var body = JsonDocument.Parse(reader.GetString(4));
        var metadata = reader.IsDBNull(9) ? [] : JsonNode.Parse(reader.GetString(9)) as JsonObject ?? [];
        if (!reader.IsDBNull(7))
        {
            metadata["$correlationId"] = reader.GetString(7);
        }

        if (!reader.IsDBNull(8))
        {
            metadata["$causationId"] = reader.GetString(8);
        }

        return new EventRecord(
            reader.GetGuid(1),
            reader.GetString(2),
            reader.GetInt64(3) - 1,
            reader.GetInt64(0),
            reader.GetString(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            JsonSerializer.SerializeToUtf8Bytes(body.RootElement, NightingaleJson.Options),
            metadata,
            ordinal ? reader.GetInt64(10) : null);
    }

    private string Table => _schema + ".[pc_events]";

    private SqlCommand Command(SqlConnection connection, VirtualStreamName stream, string text)
    {
        var command = connection.CreateCommand();
        command.CommandText = text;
        command.Parameters.Add("@tenant", SqlDbType.VarChar, 250).Value = tenantId;
        command.Parameters.Add("@key", SqlDbType.VarChar, 500).Value = stream.Key;
        return command;
    }
}
