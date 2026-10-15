namespace Dracocephalum.Nightingale.Server.Sequencing;

/// <summary>
/// A journal's transaction horizon: the point before which the database reports no transaction
/// still open that has written to it. Positions are taken in order and only by a transaction that
/// is writing, so once the horizon has passed a moment, every position taken before that moment is
/// final: committed and visible, or never to be. The tail gives a missing position up by it, and
/// the ordinal audit settles a range by it; neither waits on a clock. A backend implements it for
/// its database with one small query, and reports whether that query is allowed at all.
/// </summary>
public interface ITransactionHorizon
{
    /// <summary>
    /// Gets how old a marker must be before it is judged: the moment, on a database where it
    /// exists, between a transaction taking a position and the database seeing it as writing. Zero
    /// where a transaction is seen writing before it can take one.
    /// </summary>
    TimeSpan MinimumAge { get; }

    /// <summary>Checks that the database answers the horizon's query for this login, before anything relies on it.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether it does, and what is missing when it does not.</returns>
    ValueTask<HorizonAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken);

    /// <summary>Marks the present moment on the database's own clock, to be judged later.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The marker.</returns>
    ValueTask<HorizonMarker> MarkAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Finds the oldest transaction still open that has written to the database since before a
    /// marker: the one the horizon waits on. None means the horizon has passed the marker. A start
    /// the database reports in a form that cannot be read counts as before the marker, so the
    /// answer errs towards waiting.
    /// </summary>
    /// <param name="marker">A marker this horizon issued.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The transaction holding the horizon back, or <see langword="null"/> when it has passed the marker.</returns>
    ValueTask<HorizonHolder?> FindHolderAsync(HorizonMarker marker, CancellationToken cancellationToken);
}
