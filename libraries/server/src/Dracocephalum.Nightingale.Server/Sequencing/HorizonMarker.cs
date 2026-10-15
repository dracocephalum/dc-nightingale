namespace Dracocephalum.Nightingale.Server.Sequencing;

/// <summary>
/// A moment on a journal's own clock, as its <see cref="ITransactionHorizon"/> marked it. The value
/// means something only to the horizon that issued it: a time on one database, a transaction id on
/// another. Markers from different journals never compare.
/// </summary>
/// <param name="Value">The value, in the issuing horizon's terms.</param>
public readonly record struct HorizonMarker(long Value);
