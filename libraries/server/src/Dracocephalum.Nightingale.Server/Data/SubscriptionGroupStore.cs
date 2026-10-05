using Dracocephalum.Nightingale;
using Microsoft.EntityFrameworkCore;

namespace Dracocephalum.Nightingale.Server.Data;

/// <summary>
/// The group store over the gateway's own tables through the context: groups with their settings
/// as columns and their checkpoint, parked messages one row each, the outbox a replay
/// moves them to, and leases. Every operation is plain reads and writes, so it runs on any
/// provider and the in-memory one stands in for a test; the one race that matters, two
/// instances taking one lease, is settled by the lease row's concurrency tokens rather than by a
/// statement only one database has.
/// </summary>
/// <param name="contexts">Where a context comes from, one per operation.</param>
/// <param name="tenantId">The tenant every group belongs to.</param>
/// <param name="timeProvider">The clock leases are measured against.</param>
public sealed class SubscriptionGroupStore(IDbContextFactory<NightingaleDbContext> contexts, string tenantId, TimeProvider timeProvider) : ISubscriptionGroupStore
{
    /// <summary>How many times a delete is tried while the group's rows change under it.</summary>
    private const int DeleteAttempts = 5;

    /// <summary>The longest consumer address the row holds; a longer one is cut, being for reading only.</summary>
    private const int MaxConsumerAddressLength = 200;

    /// <inheritdoc/>
    public async Task CreateAsync(SubscriptionGroupDefinition group, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(group);
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (await context.SubscriptionGroups.AnyAsync(row => row.TenantId == tenantId && row.Stream == group.Stream && row.Name == group.Group, cancellationToken).ConfigureAwait(false))
        {
            throw new GroupExistsException(group.Stream, group.Group);
        }

        var settings = group.Settings;
        context.SubscriptionGroups.Add(new SubscriptionGroup
        {
            Id = group.Id,
            TenantId = tenantId,
            Stream = group.Stream,
            Name = group.Group,
            StartPosition = settings.Start.IsEnd ? null : settings.Start.Value,
            MessageTimeoutMs = (long)settings.MessageTimeout.TotalMilliseconds,
            MaxRetryCount = settings.MaxRetryCount,
            CheckpointUpperBound = settings.CheckpointUpperBound,
            CheckpointAfterMs = (long)settings.CheckpointAfter.TotalMilliseconds,
            CheckpointLowerBound = settings.CheckpointLowerBound,
            BufferSize = settings.BufferSize,
            MaxSubscriberCount = settings.MaxSubscriberCount,
            Numbering = settings.Numbering,
            CheckpointPosition = group.Checkpoint,
            CreatedAt = timeProvider.GetUtcNow(),
        });
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception)
        {
            // Created by someone else between the check and the write: the group exists. Anything
            // else the database refused is not that, and the usual cause is a name longer than
            // its column, which the database measures and the store does not second-guess.
            await using var check = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            if (await check.SubscriptionGroups.AnyAsync(row => row.TenantId == tenantId && row.Stream == group.Stream && row.Name == group.Group, cancellationToken).ConfigureAwait(false))
            {
                throw new GroupExistsException(group.Stream, group.Group);
            }

            throw new ValueTooLongException("stream or group name", exception);
        }
    }

    /// <inheritdoc/>
    public async Task<SubscriptionGroupDefinition?> GetAsync(string stream, string group, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await context.SubscriptionGroups.AsNoTracking()
            .SingleOrDefaultAsync(row => row.TenantId == tenantId && row.Stream == stream && row.Name == group, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        return Definition(row);
    }

    /// <inheritdoc/>
    public async Task SaveLiveAsync(Guid groupId, SubscriptionGroupLive? live, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await context.SubscriptionGroups
            .SingleOrDefaultAsync(row => row.Id == groupId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            // The group was deleted under its consumer; there is nothing to write this on.
            return;
        }

        row.LiveSnapshotAt = live?.AsOf;
        row.LiveConnectedAt = live?.ConnectedAt;
        row.LiveConsumerAddress = live?.ConsumerAddress is { Length: > MaxConsumerAddressLength } address ? address[..MaxConsumerAddressLength] : live?.ConsumerAddress;
        row.LiveConsumerBufferSize = live?.ConsumerBufferSize;
        row.LiveInFlightCount = live?.InFlightCount;
        row.LiveAwaitingRetryCount = live?.AwaitingRetryCount;
        row.LiveCheckpointPosition = live?.Checkpoint;
        row.LiveOldestInFlightAt = live?.OldestInFlightAt;
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Deleted between the read and the write; the same as not being there.
        }
    }

    /// <inheritdoc/>
    public async Task<SubscriptionGroupSummary?> DescribeAsync(string stream, string group, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await context.SubscriptionGroups.AsNoTracking()
            .SingleOrDefaultAsync(row => row.TenantId == tenantId && row.Stream == stream && row.Name == group, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var lease = SubscriptionGroupRegistry.LeaseName(row.Id);
        var now = timeProvider.GetUtcNow();
        var held = await context.Leases.AsNoTracking().SingleOrDefaultAsync(held => held.Name == lease, cancellationToken).ConfigureAwait(false);
        var holder = held is null || held.ExpiresAt <= now ? null : Holder(held);
        return new SubscriptionGroupSummary(
            Definition(row),
            row.CreatedAt,
            await ParkedOf(context, row.Id).LongCountAsync(cancellationToken).ConfigureAwait(false),
            await OutboxOf(context, row.Id).LongCountAsync(cancellationToken).ConfigureAwait(false),
            holder,
            Live(row, holder));
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SubscriptionGroupSummary>> ListAsync(string? stream, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = context.SubscriptionGroups.AsNoTracking().Where(row => row.TenantId == tenantId);
        if (stream is not null)
        {
            query = query.Where(row => row.Stream == stream);
        }

        var rows = await query.OrderBy(row => row.Stream).ThenBy(row => row.Name).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return [];
        }

        // Three reads for the whole listing, whatever its size: the counts of each kind grouped
        // by group, and the leases that are groups'. A count per group would be two reads each.
        var ids = rows.Select(row => row.Id).ToList();
        var parked = await context.SubscriptionParkedEvents.AsNoTracking()
            .Where(row => ids.Contains(row.SubscriptionGroupId))
            .GroupBy(row => row.SubscriptionGroupId)
            .Select(counted => new { counted.Key, Count = counted.LongCount() })
            .ToDictionaryAsync(counted => counted.Key, counted => counted.Count, cancellationToken)
            .ConfigureAwait(false);
        var outbox = await context.SubscriptionOutboxEntries.AsNoTracking()
            .Where(row => ids.Contains(row.SubscriptionGroupId))
            .GroupBy(row => row.SubscriptionGroupId)
            .Select(counted => new { counted.Key, Count = counted.LongCount() })
            .ToDictionaryAsync(counted => counted.Key, counted => counted.Count, cancellationToken)
            .ConfigureAwait(false);
        var names = ids.Select(SubscriptionGroupRegistry.LeaseName).ToList();
        var now = timeProvider.GetUtcNow();
        var leases = (await context.Leases.AsNoTracking()
                .Where(held => names.Contains(held.Name))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .Where(held => held.ExpiresAt > now)
            .ToDictionary(held => held.Name, Holder, StringComparer.Ordinal);

        return rows
            .Select(row =>
            {
                var holder = leases.GetValueOrDefault(SubscriptionGroupRegistry.LeaseName(row.Id));
                return new SubscriptionGroupSummary(Definition(row), row.CreatedAt, parked.GetValueOrDefault(row.Id), outbox.GetValueOrDefault(row.Id), holder, Live(row, holder));
            })
            .ToList();
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteAsync(string stream, string group, CancellationToken cancellationToken)
    {
        // A group is deleted with its parked messages and its outbox, read first and removed
        // together. Its consumer, connected or just gone, may still be writing: an outbox row
        // delivered and taken off, a refusal that parks a message. A row gone under the delete,
        // or one that arrived after the read, fails the write; what is there is then read again
        // and the delete repeated, a few times at most, since the consumer's writes end.
        for (var attempt = 1; ; attempt++)
        {
            await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var row = await context.SubscriptionGroups
                .SingleOrDefaultAsync(row => row.TenantId == tenantId && row.Stream == stream && row.Name == group, cancellationToken)
                .ConfigureAwait(false);
            if (row is null)
            {
                // Gone on a later attempt means another delete finished the same work.
                return attempt > 1;
            }

            context.SubscriptionParkedEvents.RemoveRange(await ParkedOf(context, row.Id).ToListAsync(cancellationToken).ConfigureAwait(false));
            context.SubscriptionOutboxEntries.RemoveRange(await OutboxOf(context, row.Id).ToListAsync(cancellationToken).ConfigureAwait(false));
            context.SubscriptionGroups.Remove(row);
            try
            {
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (DbUpdateException) when (attempt < DeleteAttempts)
            {
                // Read again; see above.
            }
        }
    }

    /// <inheritdoc/>
    public async Task SaveCheckpointAsync(Guid groupId, long checkpoint, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await context.SubscriptionGroups
            .SingleOrDefaultAsync(row => row.Id == groupId, cancellationToken)
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
    public async Task ParkAsync(SubscriptionParkedMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await context.SubscriptionParkedEvents
            .SingleOrDefaultAsync(row => row.SubscriptionGroupId == message.GroupId && row.Position == message.Position, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            row = new SubscriptionParkedEvent
            {
                SubscriptionGroupId = message.GroupId,
                Position = message.Position,
                Revision = message.Revision,
                Ordinal = message.Ordinal,
                EventId = message.EventId,
                Reason = message.Reason,
            };
            context.SubscriptionParkedEvents.Add(row);
        }

        row.Reason = message.Reason;
        row.Attempts = message.Attempts;
        row.ParkedAt = message.ParkedAt;

        // Moved back: a message that was on the outbox is parked again, not in both places.
        var queued = await context.SubscriptionOutboxEntries
            .SingleOrDefaultAsync(row => row.SubscriptionGroupId == message.GroupId && row.Position == message.Position, cancellationToken)
            .ConfigureAwait(false);
        if (queued is not null)
        {
            context.SubscriptionOutboxEntries.Remove(queued);
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // The group was deleted under its consumer, and its parked events with it: the foreign
            // key refuses a row for a group that is gone, and there is nothing to park it for.
            // Anything else the database refused is not that, and is the caller's to see.
            if (await GroupExistsAsync(message.GroupId, cancellationToken).ConfigureAwait(false))
            {
                throw;
            }
        }
    }

    /// <inheritdoc/>
    public Task<int> ReplayAsync(Guid groupId, long? number, SubscriptionParkedNumber by, DateTimeOffset dueAt, CancellationToken cancellationToken) =>
        MoveToOutboxAsync(groupId, number, null, by, dueAt, cancellationToken);

    /// <inheritdoc/>
    public Task<int> ReplayBeforeAsync(Guid groupId, long before, SubscriptionParkedNumber by, DateTimeOffset dueAt, CancellationToken cancellationToken) =>
        MoveToOutboxAsync(groupId, null, before, by, dueAt, cancellationToken);

    /// <inheritdoc/>
    public async Task<int> SkipAsync(Guid groupId, long? number, long? before, SubscriptionParkedNumber by, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var toSkip = await Selected(ParkedOf(context, groupId), number, before, by).ToListAsync(cancellationToken).ConfigureAwait(false);
        context.SubscriptionParkedEvents.RemoveRange(toSkip);
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A replay or another skip took some of them first; what is left to count is what
            // this call can still remove, and the caller's rows are gone either way.
            await using var again = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var left = await Selected(ParkedOf(again, groupId), number, before, by).ToListAsync(cancellationToken).ConfigureAwait(false);
            again.SubscriptionParkedEvents.RemoveRange(left);
            await again.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return left.Count;
        }

        return toSkip.Count;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SubscriptionParkedMessage>> ListParkedAsync(Guid groupId, SubscriptionParkedNumber by, long? after, int limit, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = ParkedOf(context, groupId).AsNoTracking();
        rows = by switch
        {
            SubscriptionParkedNumber.Revision => rows.Where(row => after == null || row.Revision > after).OrderBy(row => row.Revision),
            SubscriptionParkedNumber.Ordinal => rows.Where(row => after == null || row.Ordinal > after).OrderBy(row => row.Ordinal),
            _ => rows.Where(row => after == null || row.Position > after).OrderBy(row => row.Position),
        };
        var page = await rows.Take(limit).ToListAsync(cancellationToken).ConfigureAwait(false);
        return page.Select(row => new SubscriptionParkedMessage(groupId, row.Position, row.Revision, row.Ordinal, row.EventId, row.Reason, row.Attempts, row.ParkedAt)).ToList();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SubscriptionOutboxMessage>> ListOutboxAsync(Guid groupId, SubscriptionParkedNumber by, long? after, int limit, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = OutboxOf(context, groupId).AsNoTracking();
        rows = by switch
        {
            SubscriptionParkedNumber.Revision => rows.Where(row => after == null || row.Revision > after).OrderBy(row => row.Revision),
            SubscriptionParkedNumber.Ordinal => rows.Where(row => after == null || row.Ordinal > after).OrderBy(row => row.Ordinal),
            _ => rows.Where(row => after == null || row.Position > after).OrderBy(row => row.Position),
        };
        var page = await rows.Take(limit).ToListAsync(cancellationToken).ConfigureAwait(false);
        return page.Select(row => new SubscriptionOutboxMessage(groupId, row.Position, row.Revision, row.Ordinal, row.EventId, row.Reason, row.Attempts, row.DueAt)).ToList();
    }

    private static IQueryable<SubscriptionParkedEvent> Selected(IQueryable<SubscriptionParkedEvent> parked, long? number, long? before, SubscriptionParkedNumber by)
    {
        if (number is { } wanted)
        {
            parked = by switch
            {
                SubscriptionParkedNumber.Revision => parked.Where(row => row.Revision == wanted),
                SubscriptionParkedNumber.Ordinal => parked.Where(row => row.Ordinal == wanted),
                _ => parked.Where(row => row.Position == wanted),
            };
        }

        if (before is { } below)
        {
            parked = by switch
            {
                SubscriptionParkedNumber.Revision => parked.Where(row => row.Revision < below),
                SubscriptionParkedNumber.Ordinal => parked.Where(row => row.Ordinal < below),
                _ => parked.Where(row => row.Position < below),
            };
        }

        return parked;
    }

    private static IQueryable<SubscriptionOutboxEntry> Selected(IQueryable<SubscriptionOutboxEntry> queued, long? number, long? before, SubscriptionParkedNumber by)
    {
        if (number is { } wanted)
        {
            queued = by switch
            {
                SubscriptionParkedNumber.Revision => queued.Where(row => row.Revision == wanted),
                SubscriptionParkedNumber.Ordinal => queued.Where(row => row.Ordinal == wanted),
                _ => queued.Where(row => row.Position == wanted),
            };
        }

        if (before is { } below)
        {
            queued = by switch
            {
                SubscriptionParkedNumber.Revision => queued.Where(row => row.Revision < below),
                SubscriptionParkedNumber.Ordinal => queued.Where(row => row.Ordinal < below),
                _ => queued.Where(row => row.Position < below),
            };
        }

        return queued;
    }

    private async Task<int> MoveToOutboxAsync(Guid groupId, long? number, long? before, SubscriptionParkedNumber by, DateTimeOffset dueAt, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var parked = Selected(ParkedOf(context, groupId), number, before, by);
        var queued = Selected(OutboxOf(context, groupId), number, before, by);

        var toMove = await parked.ToListAsync(cancellationToken).ConfigureAwait(false);
        var already = await queued.CountAsync(cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        foreach (var row in toMove)
        {
            context.SubscriptionOutboxEntries.Add(new SubscriptionOutboxEntry
            {
                SubscriptionGroupId = row.SubscriptionGroupId,
                Position = row.Position,
                Revision = row.Revision,
                Ordinal = row.Ordinal,
                EventId = row.EventId,
                Reason = row.Reason,
                Attempts = row.Attempts,
                DueAt = dueAt,
                QueuedAt = now,
            });
            context.SubscriptionParkedEvents.Remove(row);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return toMove.Count + already;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SubscriptionOutboxMessage>> DueAsync(Guid groupId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await OutboxOf(context, groupId).AsNoTracking()
            .Where(row => row.DueAt <= now)
            .OrderBy(row => row.DueAt).ThenBy(row => row.Position)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(row => new SubscriptionOutboxMessage(groupId, row.Position, row.Revision, row.Ordinal, row.EventId, row.Reason, row.Attempts, row.DueAt)).ToList();
    }

    /// <inheritdoc/>
    public async Task DequeueAsync(Guid groupId, long position, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await context.SubscriptionOutboxEntries
            .SingleOrDefaultAsync(row => row.SubscriptionGroupId == groupId && row.Position == position, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return;
        }

        context.SubscriptionOutboxEntries.Remove(row);
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
                context.Leases.Add(new Lease { Name = name, Owner = owner, OwnerAddress = address, ExpiresAt = now + duration });
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

    private static SubscriptionGroupDefinition Definition(SubscriptionGroup row)
    {
        var settings = new GroupSettings(
            row.StartPosition is { } start ? StreamPosition.From(start) : StreamPosition.End,
            TimeSpan.FromMilliseconds(row.MessageTimeoutMs),
            row.MaxRetryCount,
            row.CheckpointUpperBound,
            TimeSpan.FromMilliseconds(row.CheckpointAfterMs),
            row.CheckpointLowerBound,
            row.BufferSize,
            row.MaxSubscriberCount,
            row.Numbering);

        // The names are the row's own: a case-insensitive database finds the row under another
        // spelling, and the group still goes by the names it was created with.
        return new SubscriptionGroupDefinition(row.Stream, row.Name, settings, row.CheckpointPosition) { Id = row.Id };
    }

    /// <summary>
    /// The row's snapshot, while the group's lease is held. Without a holder the columns are
    /// what an instance that stopped without clearing them left behind, and say nothing.
    /// </summary>
    private static SubscriptionGroupLive? Live(SubscriptionGroup row, LeaseHolder? holder) =>
        holder is null || row.LiveSnapshotAt is not { } asOf
            ? null
            : new SubscriptionGroupLive(
                row.LiveConnectedAt ?? asOf,
                row.LiveInFlightCount ?? 0,
                row.LiveAwaitingRetryCount ?? 0,
                row.LiveConsumerBufferSize ?? 0,
                row.LiveCheckpointPosition,
                row.LiveOldestInFlightAt,
                row.LiveConsumerAddress,
                asOf);

    private static LeaseHolder Holder(Lease lease) =>
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

    private async Task<bool> GroupExistsAsync(Guid groupId, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.SubscriptionGroups.AnyAsync(row => row.Id == groupId, cancellationToken).ConfigureAwait(false);
    }

    private static IQueryable<SubscriptionParkedEvent> ParkedOf(NightingaleDbContext context, Guid groupId) =>
        context.SubscriptionParkedEvents.Where(row => row.SubscriptionGroupId == groupId);

    private static IQueryable<SubscriptionOutboxEntry> OutboxOf(NightingaleDbContext context, Guid groupId) =>
        context.SubscriptionOutboxEntries.Where(row => row.SubscriptionGroupId == groupId);
}
