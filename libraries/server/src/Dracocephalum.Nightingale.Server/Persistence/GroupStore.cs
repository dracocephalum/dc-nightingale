using System.Text.Json;

using Dracocephalum.Nightingale;
using Microsoft.EntityFrameworkCore;

namespace Dracocephalum.Nightingale.Server.Persistence;

/// <summary>
/// The group store over the gateway's own tables through the context: groups with their settings
/// as a JSON document and their checkpoint, parked messages one row each, the outbox a replay
/// moves them to, and leases. Every operation is plain reads and writes, so it runs on any
/// provider and the in-memory one stands in for a test; the one race that matters, two
/// instances taking one lease, is settled by the lease row's concurrency tokens rather than by a
/// statement only one database has.
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
        context.Outbox.RemoveRange(await OutboxOf(context, stream, group).ToListAsync(cancellationToken).ConfigureAwait(false));
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

        // Moved back: a message that was on the outbox is parked again, not in both places.
        var queued = await context.Outbox
            .SingleOrDefaultAsync(row => row.TenantId == tenantId && row.Stream == message.Stream && row.GroupName == message.Group && row.Position == message.Position, cancellationToken)
            .ConfigureAwait(false);
        if (queued is not null)
        {
            context.Outbox.Remove(queued);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<int> ReplayAsync(string stream, string group, long? number, ParkedNumber by, DateTimeOffset dueAt, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var parked = ParkedOf(context, stream, group);
        var queued = OutboxOf(context, stream, group);
        if (number is { } wanted)
        {
            parked = by switch
            {
                ParkedNumber.Revision => parked.Where(row => row.Revision == wanted),
                ParkedNumber.Ordinal => parked.Where(row => row.Ordinal == wanted),
                _ => parked.Where(row => row.Position == wanted),
            };
            queued = by switch
            {
                ParkedNumber.Revision => queued.Where(row => row.Revision == wanted),
                ParkedNumber.Ordinal => queued.Where(row => row.Ordinal == wanted),
                _ => queued.Where(row => row.Position == wanted),
            };
        }

        var toMove = await parked.ToListAsync(cancellationToken).ConfigureAwait(false);
        var already = await queued.CountAsync(cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        foreach (var row in toMove)
        {
            context.Outbox.Add(new OutboxRow
            {
                TenantId = row.TenantId,
                Stream = row.Stream,
                GroupName = row.GroupName,
                Position = row.Position,
                Revision = row.Revision,
                Ordinal = row.Ordinal,
                EventId = row.EventId,
                Reason = row.Reason,
                Attempts = row.Attempts,
                DueAt = dueAt,
                QueuedAt = now,
            });
            context.Parked.Remove(row);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return toMove.Count + already;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<OutboxMessage>> DueAsync(string stream, string group, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await OutboxOf(context, stream, group).AsNoTracking()
            .Where(row => row.DueAt <= now)
            .OrderBy(row => row.DueAt).ThenBy(row => row.Position)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(row => new OutboxMessage(stream, group, row.Position, row.Revision, row.Ordinal, row.EventId, row.Reason, row.Attempts, row.DueAt)).ToList();
    }

    /// <inheritdoc/>
    public async Task DequeueAsync(string stream, string group, long position, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await context.Outbox
            .SingleOrDefaultAsync(row => row.TenantId == tenantId && row.Stream == stream && row.GroupName == group && row.Position == position, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return;
        }

        context.Outbox.Remove(row);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<LeaseHolder?> AcquireLeaseAsync(string name, string owner, Uri? ownerAddress, TimeSpan duration, CancellationToken cancellationToken)
    {
        // Free, expired, or ours: taken or renewed. Held by another: left alone, and its holder
        // returned. Two instances racing for the same row are told apart by the concurrency
        // tokens: the loser's write fails, and it reads who won. A lost race is tried once more,
        // because the winner may have been releasing rather than taking.
        var address = ownerAddress?.ToString();
        for (var attempt = 0; ; attempt++)
        {
            var now = timeProvider.GetUtcNow();
            await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var lease = await context.Leases.SingleOrDefaultAsync(row => row.Name == name, cancellationToken).ConfigureAwait(false);
            if (lease is null)
            {
                context.Leases.Add(new LeaseRow { Name = name, Owner = owner, OwnerAddress = address, ExpiresAt = now + duration });
            }
            else if (lease.Owner != owner && lease.ExpiresAt > now)
            {
                return Holder(lease);
            }
            else
            {
                lease.Owner = owner;
                lease.OwnerAddress = address;
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
                return holder is null || holder.Owner == owner ? null : Holder(holder);
            }
        }
    }

    /// <inheritdoc/>
    public async Task<LeaseHolder?> LeaseHolderAsync(string name, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var lease = await context.Leases.AsNoTracking().SingleOrDefaultAsync(row => row.Name == name, cancellationToken).ConfigureAwait(false);
        return lease is null || lease.ExpiresAt <= timeProvider.GetUtcNow() ? null : Holder(lease);
    }

    private static LeaseHolder Holder(LeaseRow lease) =>
        new(lease.Owner, lease.OwnerAddress is null ? null : new Uri(lease.OwnerAddress, UriKind.Absolute));

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

    private IQueryable<OutboxRow> OutboxOf(NightingaleDbContext context, string stream, string group) =>
        context.Outbox.Where(row => row.TenantId == tenantId && row.Stream == stream && row.GroupName == group);

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
