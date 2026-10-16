namespace Dracocephalum.Nightingale.Server.Sequencing;

/// <summary>
/// How the tails of a journal keep their sequence heads, chosen once when they start: the horizon
/// that says when a missing position is final, and the reads of the events table that go with it.
/// The choice holds for the life of the process, so the head behaves one way from start to stop.
/// </summary>
/// <param name="Kind">Which strategy it is.</param>
/// <param name="Horizon">Judges when every position up to a checkpoint's ceiling is final.</param>
/// <param name="Positions">Reads the events table's positions.</param>
public sealed record HeadStrategy(HeadStrategyKind Kind, ITransactionHorizon Horizon, ISequencePositions Positions);
