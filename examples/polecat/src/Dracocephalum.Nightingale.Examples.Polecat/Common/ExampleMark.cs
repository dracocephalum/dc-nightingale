using Dracocephalum.Nightingale.Client;

namespace Dracocephalum.Nightingale.Examples.Polecat.Common;

/// <summary>
/// The store's high-water mark as a sample sees it: a bounded read of <c>$all</c> brings the mark
/// up to date and reports it as the head. A sample that reads <c>$all</c> or a virtual stream, or
/// subscribes to one from its end, right after appending waits for the mark to cover its own
/// appends first, because the scenarios share one store: a sequence number below them that
/// another scenario's refused append left abandoned holds the mark back until the store gives the
/// gap up, the exception <c>VARIANCES.md</c> records under the forward reads of <c>$all</c>, and
/// a read taken before then stops short of the appends.
/// </summary>
public static class ExampleMark
{
    /// <summary>Waits until the store's high-water mark has reached the position.</summary>
    /// <param name="client">The client, working in the scenario's tenant.</param>
    /// <param name="position">The position of the last event the scenario appended.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Completes once the mark covers the position; the store gives an abandoned gap up after its threshold, so it does.</returns>
    public static async Task WaitUntilCoversAsync(NightingaleClient client, long position, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        while (true)
        {
            // Consumed to its end, so the server completes the call itself.
            StreamHead? head;
            await using (var read = client.ReadAllAsync(Direction.Backwards, StreamPosition.End, maxCount: 1, cancellationToken))
            {
                head = await read.Head.ConfigureAwait(false);
                await foreach (var record in read.WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    _ = record;
                }
            }

            if (head is { } reached && reached.Last >= position)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }
    }
}
