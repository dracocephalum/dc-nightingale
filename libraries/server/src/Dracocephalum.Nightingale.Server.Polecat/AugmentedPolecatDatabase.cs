using Polecat;
using Polecat.Storage;
using Weasel.Core.Migrations;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// The store's database, augmented with the gateway's additions to the schema: the patched event-store
/// feature and the marker table. The store rebuilds its feature definitions on every migration
/// call, so the additions are applied to the definition each time it is built rather than to a
/// cached instance. This class only exists for the schema; every other behaviour is the store's own.
/// The schema tooling's own apply is never called on it, the server runs the statements it writes;
/// so its logger, which the store's own would send to the console, stays the default and unused.
/// </summary>
/// <param name="options">The store options this database belongs to.</param>
/// <param name="ordinals">Whether the events table carries the ordinal columns.</param>
internal sealed class AugmentedPolecatDatabase(StoreOptions options, bool ordinals) : PolecatDatabase(options)
{
    /// <inheritdoc/>
    public override IFeatureSchema[] BuildFeatureSchemas() =>
        base.BuildFeatureSchemas()
            .Select(feature => feature.Identifier == PatchedEventStoreFeature.EventStoreIdentifier ? new PatchedEventStoreFeature(feature, ordinals) : feature)
            .ToArray();
}
