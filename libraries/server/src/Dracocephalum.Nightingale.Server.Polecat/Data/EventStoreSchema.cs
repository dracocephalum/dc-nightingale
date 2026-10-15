using Dracocephalum.Nightingale.Server.Data;

namespace Dracocephalum.Nightingale.Server.Polecat.Data;

/// <summary>The schema the event store keeps its tables in, which the mirrors of them read, and the gateway's own, whose ordinal links they read beside them.</summary>
/// <param name="Name">The store's schema.</param>
/// <param name="GatewayName">The gateway's schema.</param>
internal sealed record EventStoreSchema(string Name, string GatewayName = NightingaleSchema.Default);
