namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// Finds how far the committed events reach without a gap. Sequence numbers are assigned before
/// a transaction commits, so a higher number can be visible while a lower one is still in
/// flight; the positions after a mark are safe to read only as far as they follow it one by one.
/// </summary>
internal static class ContiguousPrefix
{
    /// <summary>The last position of the unbroken run that follows a mark.</summary>
    /// <param name="mark">The position every event up to which is known to be committed.</param>
    /// <param name="following">The positions visible after the mark, ascending.</param>
    /// <returns>The mark when the next position is missing; otherwise the last position before the first gap.</returns>
    public static long Extend(long mark, IReadOnlyList<long> following)
    {
        ArgumentNullException.ThrowIfNull(following);
        var reached = mark;
        foreach (var position in following)
        {
            if (position != reached + 1)
            {
                break;
            }

            reached = position;
        }

        return reached;
    }
}
