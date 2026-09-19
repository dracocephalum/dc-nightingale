using System.Data;
using System.Globalization;
using System.Text.Json;

using Microsoft.Data.SqlClient;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// The group store over the gateway's own tables, in SQL: groups with their settings as a JSON
/// document and their checkpoint, parked messages one row each with a replay flag, and leases
/// taken with a merge that only an expired or the owner's own lease lets through.
/// </summary>
/// <param name="connectionString">The connection string the store uses.</param>
/// <param name="schemaName">The schema the tables live in.</param>
/// <param name="tenantId">The tenant every group belongs to.</param>
/// <param name="timeProvider">The clock leases are measured against.</param>
internal sealed class PolecatGroupStore(string connectionString, string schemaName, string tenantId, TimeProvider timeProvider) : IGroupStore
{
    private const int DuplicateKey = 2627;

    private readonly string _groups = Qualified(schemaName, NightingaleTablesFeature.GroupsTable);
    private readonly string _parked = Qualified(schemaName, NightingaleTablesFeature.ParkedTable);
    private readonly string _leases = Qualified(schemaName, NightingaleTablesFeature.LeasesTable);

    /// <inheritdoc/>
    public async Task CreateAsync(GroupDefinition group, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(group);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Keyed(connection, group.Stream, group.Group, string.Format(
            CultureInfo.InvariantCulture,
            "INSERT INTO {0} (tenant_id, stream, group_name, settings, checkpoint_position, created_at) VALUES (@tenant, @stream, @group, @settings, @checkpoint, @now)",
            _groups));
        command.Parameters.AddWithValue("@settings", JsonSerializer.Serialize(StoredSettings.From(group.Settings)));
        command.Parameters.AddWithValue("@checkpoint", group.Checkpoint);
        command.Parameters.AddWithValue("@now", timeProvider.GetUtcNow());
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException exception) when (exception.Number == DuplicateKey)
        {
            throw new GroupExistsException(group.Stream, group.Group);
        }
    }

    /// <inheritdoc/>
    public async Task<GroupDefinition?> GetAsync(string stream, string group, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Keyed(connection, stream, group, string.Format(
            CultureInfo.InvariantCulture,
            "SELECT settings, checkpoint_position FROM {0} WHERE tenant_id = @tenant AND stream = @stream AND group_name = @group",
            _groups));
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var settings = JsonSerializer.Deserialize<StoredSettings>(reader.GetString(0)) ?? throw new InvalidOperationException("The group's settings are not readable.");
        return new GroupDefinition(stream, group, settings.ToSettings(), reader.GetInt64(1));
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteAsync(string stream, string group, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Keyed(connection, stream, group, string.Format(
            CultureInfo.InvariantCulture,
            "DELETE FROM {1} WHERE tenant_id = @tenant AND stream = @stream AND group_name = @group; DELETE FROM {0} WHERE tenant_id = @tenant AND stream = @stream AND group_name = @group",
            _groups,
            _parked));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc/>
    public async Task SaveCheckpointAsync(string stream, string group, long checkpoint, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Keyed(connection, stream, group, string.Format(
            CultureInfo.InvariantCulture,
            "UPDATE {0} SET checkpoint_position = @checkpoint WHERE tenant_id = @tenant AND stream = @stream AND group_name = @group",
            _groups));
        command.Parameters.AddWithValue("@checkpoint", checkpoint);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task ParkAsync(ParkedMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Keyed(connection, message.Stream, message.Group, string.Format(
            CultureInfo.InvariantCulture,
            "MERGE {0} WITH (HOLDLOCK) AS t USING (SELECT @tenant AS tenant_id, @stream AS stream, @group AS group_name, @position AS position) AS s"
            + " ON t.tenant_id = s.tenant_id AND t.stream = s.stream AND t.group_name = s.group_name AND t.position = s.position"
            + " WHEN MATCHED THEN UPDATE SET reason = @reason, attempts = @attempts, parked_at = @parked_at, replay = @replay"
            + " WHEN NOT MATCHED THEN INSERT (tenant_id, stream, group_name, position, event_id, reason, attempts, parked_at, replay) VALUES (@tenant, @stream, @group, @position, @event_id, @reason, @attempts, @parked_at, @replay);",
            _parked));
        command.Parameters.AddWithValue("@position", message.Position);
        command.Parameters.AddWithValue("@event_id", message.EventId);
        command.Parameters.AddWithValue("@reason", message.Reason);
        command.Parameters.AddWithValue("@attempts", message.Attempts);
        command.Parameters.AddWithValue("@parked_at", message.ParkedAt);
        command.Parameters.AddWithValue("@replay", message.Replay);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task UnparkAsync(string stream, string group, long position, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Keyed(connection, stream, group, string.Format(
            CultureInfo.InvariantCulture,
            "DELETE FROM {0} WHERE tenant_id = @tenant AND stream = @stream AND group_name = @group AND position = @position",
            _parked));
        command.Parameters.AddWithValue("@position", position);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ParkedMessage>> ReplayableAsync(string stream, string group, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Keyed(connection, stream, group, string.Format(
            CultureInfo.InvariantCulture,
            "SELECT position, event_id, reason, attempts, parked_at FROM {0} WHERE tenant_id = @tenant AND stream = @stream AND group_name = @group AND replay = 1 ORDER BY parked_at, position",
            _parked));
        var messages = new List<ParkedMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            messages.Add(new ParkedMessage(stream, group, reader.GetInt64(0), reader.GetGuid(1), reader.GetString(2), reader.GetInt32(3), reader.GetFieldValue<DateTimeOffset>(4), true));
        }

        return messages;
    }

    /// <inheritdoc/>
    public async Task<int> MarkForReplayAsync(string stream, string group, long? position, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Keyed(connection, stream, group, string.Format(
            CultureInfo.InvariantCulture,
            "UPDATE {0} SET replay = 1 WHERE tenant_id = @tenant AND stream = @stream AND group_name = @group AND replay = 0{1}",
            _parked,
            position is null ? string.Empty : " AND position = @position"));
        if (position is { } wanted)
        {
            command.Parameters.AddWithValue("@position", wanted);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<string?> AcquireLeaseAsync(string name, string owner, TimeSpan duration, CancellationToken cancellationToken)
    {
        // Free, expired, or ours: taken or renewed. Held by another: left alone, and its owner
        // returned. The merge holds its range lock to the end of the statement, so two instances
        // racing for a free lease serialize on it and exactly one wins.
        var now = timeProvider.GetUtcNow();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = string.Format(
            CultureInfo.InvariantCulture,
            "MERGE {0} WITH (HOLDLOCK) AS t USING (SELECT @name AS name) AS s ON t.name = s.name"
            + " WHEN MATCHED AND (t.owner = @owner OR t.expires_at <= @now) THEN UPDATE SET owner = @owner, expires_at = @expires"
            + " WHEN NOT MATCHED THEN INSERT (name, owner, expires_at) VALUES (@name, @owner, @expires);"
            + " SELECT owner FROM {0} WHERE name = @name",
            _leases);
        command.Parameters.AddWithValue("@name", name);
        command.Parameters.AddWithValue("@owner", owner);
        command.Parameters.AddWithValue("@now", now);
        command.Parameters.AddWithValue("@expires", now + duration);
        var holder = (string)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        return string.Equals(holder, owner, StringComparison.Ordinal) ? null : holder;
    }

    /// <inheritdoc/>
    public async Task ReleaseLeaseAsync(string name, string owner, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = string.Format(CultureInfo.InvariantCulture, "DELETE FROM {0} WHERE name = @name AND owner = @owner", _leases);
        command.Parameters.AddWithValue("@name", name);
        command.Parameters.AddWithValue("@owner", owner);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // The schema name comes from the store's options, never from a request; it is bracketed as an
    // identifier all the same.
    private static string Qualified(string schema, string table) =>
        $"[{schema.Replace("]", "]]", StringComparison.Ordinal)}].[{table}]";

    private SqlCommand Keyed(SqlConnection connection, string stream, string group, string text)
    {
        var command = connection.CreateCommand();
        command.CommandText = text;
        command.Parameters.Add("@tenant", SqlDbType.VarChar, 250).Value = tenantId;
        command.Parameters.Add("@stream", SqlDbType.VarChar, 250).Value = stream;
        command.Parameters.Add("@group", SqlDbType.VarChar, 250).Value = group;
        return command;
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    /// <summary>The settings as stored: primitives only, so the document outlives the domain type's shape.</summary>
    private sealed record StoredSettings(long Start, long MessageTimeoutMs, int MaxRetryCount, int CheckpointUpperBound, long CheckpointAfterMs, int CheckpointLowerBound, int BufferSize, int MaxSubscriberCount)
    {
        public static StoredSettings From(GroupSettings settings) => new(
            settings.Start.IsEnd ? -1 : settings.Start.Value,
            (long)settings.MessageTimeout.TotalMilliseconds,
            settings.MaxRetryCount,
            settings.CheckpointUpperBound,
            (long)settings.CheckpointAfter.TotalMilliseconds,
            settings.CheckpointLowerBound,
            settings.BufferSize,
            settings.MaxSubscriberCount);

        public GroupSettings ToSettings() => new(
            Start < 0 ? StreamPosition.End : StreamPosition.From(Start),
            TimeSpan.FromMilliseconds(MessageTimeoutMs),
            MaxRetryCount,
            CheckpointUpperBound,
            TimeSpan.FromMilliseconds(CheckpointAfterMs),
            CheckpointLowerBound,
            BufferSize,
            MaxSubscriberCount);
    }
}
