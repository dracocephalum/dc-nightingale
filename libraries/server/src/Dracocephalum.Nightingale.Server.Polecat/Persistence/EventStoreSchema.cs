namespace Dracocephalum.Nightingale.Server.Polecat.Persistence;

/// <summary>The schema the event store keeps its tables in, which the mirrors of them read.</summary>
/// <param name="Name">The schema's name.</param>
internal sealed record EventStoreSchema(string Name);
