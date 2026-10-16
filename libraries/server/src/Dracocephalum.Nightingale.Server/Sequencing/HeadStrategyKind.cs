namespace Dracocephalum.Nightingale.Server.Sequencing;

/// <summary>Which strategy keeps a journal's sequence heads.</summary>
public enum HeadStrategyKind
{
    /// <summary>
    /// The database's transaction horizon: a missing position is given up once no transaction that
    /// could still commit it is open, and never while one is. No position is skipped that commits later.
    /// </summary>
    TransactionHorizon,

    /// <summary>
    /// A wait, where the database does not answer the horizon's query for this login: a missing
    /// position is given up once a checkpoint over it is as old as the stale threshold, whatever
    /// holds it. How near that comes to the horizon is the backend's reads: on SQL Server they wait
    /// on an open append's row, so only a position whose row is not yet there when the wait ends can
    /// be skipped and commit after.
    /// </summary>
    BestEffort,
}
