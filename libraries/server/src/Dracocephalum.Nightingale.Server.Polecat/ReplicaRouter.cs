using System.Data.Common;

using Microsoft.Extensions.Logging;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// Decides, page by page, how much of a read by position the read-only connection may serve, so
/// that the answer is the one the primary would give. A secondary applies the log after the
/// primary commits, so it holds a prefix of the events; the high-water mark is replicated with
/// them, and everything at or below the mark as the secondary has it is there. The secondary is
/// therefore asked for the part of the page at or below its own mark, read in the same round
/// trip as the mark, and the primary for whatever lies above it. A reader working through
/// history is served by the secondary alone; a reader at the head by the primary alone, because
/// a position above the mark last seen, seen a moment ago, is not asked of the secondary at all.
/// A secondary that fails is left alone for a while and the primary serves everything.
/// </summary>
/// <param name="timeProvider">The clock.</param>
/// <param name="logger">The logger.</param>
internal sealed partial class ReplicaRouter(TimeProvider timeProvider, ILogger logger)
{
    /// <summary>How long a mark seen on the secondary is trusted to say a position is above it.</summary>
    internal static readonly TimeSpan MarkFreshness = TimeSpan.FromSeconds(1);

    /// <summary>How long a secondary that failed is left alone.</summary>
    internal static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(10);

    private long _mark = -1;
    private long _markSeen;
    private long _downSince;
    private int _down;

    /// <summary>Reads what the secondary holds of a page: its mark, and the page's events at or below it.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The mark and the events.</returns>
    public delegate Task<(long Mark, IReadOnlyList<EventRecord> Events)> ReplicaRead(CancellationToken cancellationToken);

    /// <summary>Reads a page, or the rest of one, from the primary.</summary>
    /// <param name="from">Where to begin, inclusive.</param>
    /// <param name="count">The most events to return.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The events.</returns>
    public delegate Task<IReadOnlyList<EventRecord>> PrimaryRead(long from, int count, CancellationToken cancellationToken);

    /// <summary>Reads one page by position, from the secondary as far as it reaches and the primary beyond.</summary>
    /// <param name="direction">The direction of the read.</param>
    /// <param name="from">Where the page begins, inclusive, in the reading direction.</param>
    /// <param name="head">The highest position a forwards page may hold.</param>
    /// <param name="count">The most events to return.</param>
    /// <param name="replica">Reads from the secondary.</param>
    /// <param name="primary">Reads from the primary.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The page, as the primary alone would have returned it.</returns>
    public async Task<IReadOnlyList<EventRecord>> ReadAsync(Direction direction, long from, long head, int count, ReplicaRead replica, PrimaryRead primary, CancellationToken cancellationToken)
    {
        if (IsDown() || IsAboveFreshMark(from))
        {
            return await primary(from, count, cancellationToken).ConfigureAwait(false);
        }

        long mark;
        IReadOnlyList<EventRecord> events;
        try
        {
            (mark, events) = await replica(cancellationToken).ConfigureAwait(false);
        }
        catch (DbException exception) when (!cancellationToken.IsCancellationRequested)
        {
            MarkDown(exception);
            return await primary(from, count, cancellationToken).ConfigureAwait(false);
        }

        Volatile.Write(ref _mark, mark);
        Volatile.Write(ref _markSeen, timeProvider.GetTimestamp());

        if (direction == Direction.Backwards)
        {
            // Backwards from a position the secondary has reached, everything below is there too.
            return from <= mark ? events : await primary(from, count, cancellationToken).ConfigureAwait(false);
        }

        if (events.Count == count || mark >= head)
        {
            return events;
        }

        // The secondary gave everything it has of the page, and the page reaches above its mark.
        var rest = await primary(Math.Max(from, mark + 1), count - events.Count, cancellationToken).ConfigureAwait(false);
        return events.Count == 0 ? rest : [.. events, .. rest];
    }

    /// <summary>Runs a read on the secondary, or on the primary while the secondary is left alone or when it fails.</summary>
    /// <typeparam name="T">What the read returns.</typeparam>
    /// <param name="replica">Reads from the secondary.</param>
    /// <param name="primary">Reads from the primary.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result.</returns>
    public async Task<T> PreferReplicaAsync<T>(Func<CancellationToken, Task<T>> replica, Func<CancellationToken, Task<T>> primary, CancellationToken cancellationToken)
    {
        if (IsDown())
        {
            return await primary(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return await replica(cancellationToken).ConfigureAwait(false);
        }
        catch (DbException exception) when (!cancellationToken.IsCancellationRequested)
        {
            MarkDown(exception);
            return await primary(cancellationToken).ConfigureAwait(false);
        }
    }

    private bool IsAboveFreshMark(long from)
    {
        var mark = Volatile.Read(ref _mark);
        return mark >= 0 && from > mark && timeProvider.GetElapsedTime(Volatile.Read(ref _markSeen)) < MarkFreshness;
    }

    private bool IsDown()
    {
        if (Volatile.Read(ref _down) == 0)
        {
            return false;
        }

        if (timeProvider.GetElapsedTime(Volatile.Read(ref _downSince)) < FailureBackoff)
        {
            return true;
        }

        Volatile.Write(ref _down, 0);
        return false;
    }

    private void MarkDown(Exception exception)
    {
        Volatile.Write(ref _downSince, timeProvider.GetTimestamp());
        if (Interlocked.Exchange(ref _down, 1) == 0)
        {
            LogReplicaFailed(logger, FailureBackoff.TotalSeconds, exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A read on the read-only connection failed; reads go to the primary for {Seconds} seconds.")]
    private static partial void LogReplicaFailed(ILogger logger, double seconds, Exception exception);
}
