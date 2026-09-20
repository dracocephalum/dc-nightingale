using System.Text.Json;

using Dracocephalum.Nightingale;
using Microsoft.EntityFrameworkCore;

namespace Dracocephalum.Nightingale.Server.Persistence;

/// <summary>
/// The group store over the gateway's own tables through the context: groups with their settings
/// as a JSON document and their checkpoint, parked messages one row each with a replay flag, and
/// leases. Every operation is plain reads and writes, so it runs on any provider and the
/// in-memory one stands in for a test; the one race that matters, two instances taking one
/// lease, is settled by the lease row's concurrency tokens rather than by a statement only one
/// database has.
/// </summary>
/// <param name="contexts">Where a context comes from, one per operation.</param>
/// <param name="tenantId">The tenant every group belongs to.</param>
/// <param name="timeProvider">The clock leases are measured against.</param>
public sealed class GroupStore(IDbContextFactory<NightingaleDbContext> contexts, string tenantId, TimeProvider timeProvider) : IGroupStore
{
    /// <inheritdoc/>
    public async Task CreateAsync(GroupDefinition group, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(group);
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (await context.Groups.AnyAsync(row => row.TenantId == tenantId && row.Stream == group.Stream && row.GroupName == group.Group, cancellationToken).ConfigureAwait(false))
        {
            throw new GroupExistsException(group.Stream, group.Group);
        }

        context.Groups.Add(new GroupRow
        {
            TenantId = tenantId,
            Stream = group.Stream,
            GroupName = group.Group,
            Settings = JsonSerializer.Serialize(StoredSettings.From(group.Settings)),
            CheckpointPosition = group.Checkpoint,
            CreatedAt = timeProvider.GetUtcNow(),
        });
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Created by someone else between the check and the write: the group exists.
            throw new GroupExistsException(group.Stream, group.Group);
        }
    }

    /// <inheritdoc/>
    public async Task<GroupDefinition?> GetAsync(string stream, string group, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await context.Groups.AsNoTracking()
            .SingleOrDefaultAsync(row => row.TenantId == tenantId && row.Stream == stream && row.GroupName == group, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var settings = JsonSerializer.Deserialize<StoredSettings>(row.Settings) ?? throw new InvalidOperationException("The group's settings are not readable.");
        return new GroupDefinition(stream, group, settings.ToSettings(), row.CheckpointPosition);
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteAsync(string stream, string group, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await context.Groups
            .SingleOrDefaultAsync(row => row.TenantId == tenantId && row.Stream == stream && row.GroupName == group, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return false;
        }

        context.Parked.RemoveRange(await ParkedOf(context, stream, group).ToListAsync(cancellationToken).ConfigureAwait(false));
        context.Groups.Remove(row);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc/>
    public async Task SaveCheckpointAsync(string stream, string group, long checkpoint, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await context.Groups
            .SingleOrDefaultAsync(row => row.TenantId == tenantId && row.Stream == stream && row.GroupName == group, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            // The group was deleted under its consumer; there is nothing to record the checkpoint on.
            return;
        }

        row.CheckpointPosition = checkpoint;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task ParkAsync(ParkedMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await context.Parked
            .SingleOrDefaultAsync(row => row.TenantId == tenantId && row.Stream == message.Stream && row.GroupName == message.Group && row.Position == message.Position, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            row = new ParkedRow
            {
                TenantId = tenantId,
                Stream = message.Stream,
                GroupName = message.Group,
                Position = message.Position,
                Revision = message.Revision,
                Ordinal = message.Ordinal,
                EventId = message.EventId,
                Reason = message.Reason,
            };
            context.Parked.Add(row);
        }

        row.Reason = message.Reason;
        row.Attempts = message.Attempts;
        row.ParkedAt = message.ParkedAt;
        row.Replay = message.Replay;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task UnparkAsync(string stream, string group, long position, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await context.Parked
            .SingleOrDefaultAsync(row => row.TenantId == tenantId && row.Stream == stream && row.GroupName == group && row.Position == position, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return;
        }

        context.Parked.Remove(row);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ParkedMessage>> ReplayableAsync(string stream, string group, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await ParkedOf(context, stream, group).AsNoTracking()
            .Where(row => row.Replay)
            .OrderBy(row => row.ParkedAt).ThenBy(row => row.Position)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(row => new ParkedMessage(stream, group, row.Position, row.Revision, row.Ordinal, row.EventId, row.Reason, row.Attempts, row.ParkedAt, true)).ToList();
    }

    /// <inheritdoc/>
    public async Task<int> MarkForReplayAsync(string stream, string group, long? number, ParkedNumber by, CancellationToken cancellationToken)
    {
        // Marking is idempotent: a row already marked is matched and counted again, so replaying
        // the same message twice is a no-op rather than a message not found.
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = ParkedOf(context, stream, group);
        if (number is { } wanted)
        {
            query = by switch
            {
                ParkedNumber.Revision => query.Where(row => row.Revision == wanted),
                ParkedNumber.Ordinal => query.Where(row => row.Ordinal == wanted),
                _ => query.Where(row => row.Position == wanted),
            };
        }

        var rows = await query.ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var row in rows)
        {
            row.Replay = true;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return rows.Count;
    }

    /// <inheritdoc/>
    public async Task<string?> AcquireLeaseAsync(string name, string owner, TimeSpan duration, CancellationToken cancellationToken)
    {
        // Free, expired, or ours: taken or renewed. Held by another: left alone, and its owner
        // returned. Two instances racing for the same row are told apart by the concurrency
        // tokens: the loser's write fails, and it reads who won. A lost race is tried once more,
        // because the winner may have been releasing rather than taking.
        for (var attempt = 0; ; attempt++)
        {
            var now = timeProvider.GetUtcNow();
            await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var lease = await context.Leases.SingleOrDefaultAsync(row => row.Name == name, cancellationToken).ConfigureAwait(false);
            if (lease is null)
            {
                context.Leases.Add(new LeaseRow { Name = name, Owner = owner, ExpiresAt = now + duration });
            }
            else if (lease.Owner != owner && lease.ExpiresAt > now)
            {
                return lease.Owner;
            }
            else
            {
                lease.Owner = owner;
                lease.ExpiresAt = now + duration;
            }

            try
            {
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                // Someone else inserted or changed the row first; look again.
            }
            catch (DbUpdateException)
            {
                context.ChangeTracker.Clear();
                var holder = await context.Leases.AsNoTracking().SingleOrDefaultAsync(row => row.Name == name, cancellationToken).ConfigureAwait(false);
                return holder is null || holder.Owner == owner ? null : holder.Owner;
            }
        }
    }

    /// <inheritdoc/>
    public async Task ReleaseLeaseAsync(string name, string owner, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var lease = await context.Leases.SingleOrDefaultAsync(row => row.Name == name && row.Owner == owner, cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            return;
        }

        context.Leases.Remove(lease);
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Taken over between the read and the delete; it is no longer ours to release.
        }
    }

    private IQueryable<ParkedRow> ParkedOf(NightingaleDbContext context, string stream, string group) =>
        context.Parked.Where(row => row.TenantId == tenantId && row.Stream == stream && row.GroupName == group);

    /// <summary>The settings as stored: primitives only, so the document outlives the domain type's shape.</summary>
    private sealed record StoredSettings(long Start, long MessageTimeoutMs, int MaxRetryCount, int CheckpointUpperBound, long CheckpointAfterMs, int CheckpointLowerBound, int BufferSize, int MaxSubscriberCount, int Numbering = 0)
    {
        public static StoredSettings From(GroupSettings settings) => new(
            settings.Start.IsEnd ? -1 : settings.Start.Value,
            (long)settings.MessageTimeout.TotalMilliseconds,
            settings.MaxRetryCount,
            settings.CheckpointUpperBound,
            (long)settings.CheckpointAfter.TotalMilliseconds,
            settings.CheckpointLowerBound,
            settings.BufferSize,
            settings.MaxSubscriberCount,
            settings.Numbering == Nightingale.Numbering.Ordinal ? 1 : 0);

        public GroupSettings ToSettings() => new(
            Start < 0 ? StreamPosition.End : StreamPosition.From(Start),
            TimeSpan.FromMilliseconds(MessageTimeoutMs),
            MaxRetryCount,
            CheckpointUpperBound,
            TimeSpan.FromMilliseconds(CheckpointAfterMs),
            CheckpointLowerBound,
            BufferSize,
            MaxSubscriberCount,
            Numbering == 1 ? Nightingale.Numbering.Ordinal : Nightingale.Numbering.Global);
    }
}
