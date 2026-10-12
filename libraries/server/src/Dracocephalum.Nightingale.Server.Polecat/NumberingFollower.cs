using Dracocephalum.Nightingale.Server.Data;
using Microsoft.Extensions.Logging;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// Keeps the numbering of the sequences this process follows current while another instance
/// numbers them: the one poller of the process for the sequencer's progress, in place of a poll
/// per subscription. It waits on the heads, as the sequencer does, and only while a followed
/// sequence's head is past its numbering does it read the progress rows, once per interval, and
/// publish what they say; a sequence numbered up to its head is waited on again, so a quiet
/// store costs nothing. The instance that numbers publishes its own progress as it commits and
/// does not run this. A read that fails is logged and taken again at the next interval.
/// </summary>
/// <param name="sequences">The sequences, of which those followed here are kept current.</param>
/// <param name="progress">Where the sequencer's progress rows are read.</param>
/// <param name="timeProvider">The clock.</param>
/// <param name="logger">The logger.</param>
/// <param name="interval">How long the follower waits between reads while a sequence is behind; <see cref="Interval"/> when not given.</param>
internal sealed partial class NumberingFollower(INumberedSequences sequences, ISequencerProgressReader progress, TimeProvider timeProvider, ILogger<NumberingFollower> logger, TimeSpan? interval = null)
{
    /// <summary>How long the follower waits between reads while a sequence is behind: the tails' fast interval, the sequencer's own cadence.</summary>
    public static readonly TimeSpan Interval = PolecatTenantTails.FastInterval;

    private readonly TimeSpan _interval = interval ?? Interval;

    /// <summary>Follows the numbering for a time, then returns; the caller asks for the lease in between.</summary>
    /// <param name="atMost">How long to follow before returning.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the time is up.</returns>
    public async Task FollowAsync(TimeSpan atMost, CancellationToken cancellationToken)
    {
        var started = timeProvider.GetTimestamp();
        while (true)
        {
            var remaining = atMost - timeProvider.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            // The count is read before the heads are, so a head that moves during the look is
            // not waited on as if it had not.
            var seen = sequences.Advances;
            var behind = sequences.ListFollowed().Where(sequence => sequence.Numbering.Head < sequence.Tail.Head).ToList();
            try
            {
                if (behind.Count == 0)
                {
                    await sequences.WaitForAnyAdvanceAsync(seen, cancellationToken).WaitAsync(remaining, timeProvider, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await LookAsync(behind, cancellationToken).ConfigureAwait(false);
                await Task.Delay(_interval < remaining ? _interval : remaining, timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return;
            }
        }
    }

    /// <summary>One read of the progress rows of the sequences behind, each published to its numbering.</summary>
    /// <param name="behind">The sequences whose head is past their numbering.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when what the rows say is published.</returns>
    internal async Task LookAsync(IReadOnlyList<NumberedSequence> behind, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, long> positions;
        try
        {
            positions = await progress.ReadAsync(behind.Select(sequence => SequencerProgress.OrdinalsOf(sequence.TenantId)).ToList(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogLookFailed(exception);
            return;
        }

        foreach (var sequence in behind)
        {
            if (positions.TryGetValue(SequencerProgress.OrdinalsOf(sequence.TenantId), out var position))
            {
                sequence.Numbering.Publish(position);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Reading the sequencer's progress failed; reading again after the interval.")]
    private partial void LogLookFailed(Exception exception);
}
