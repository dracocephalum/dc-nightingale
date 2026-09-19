using Polecat;
using Polecat.Storage;
using Weasel.Core.Migrations;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// The store's database, augmented with the gateway's additions to the schema: the patched event-store
/// feature and the marker table. The store rebuilds its feature definitions on every migration
/// call, so the additions are applied to the definition each time it is built rather than to a
/// cached instance. This class only exists for the schema; every other behaviour is the store's own.
/// </summary>
internal sealed class AugmentedPolecatDatabase : PolecatDatabase
{
    private readonly StoreOptions _options;

    /// <summary>Initializes a new instance of the <see cref="AugmentedPolecatDatabase"/> class.</summary>
    /// <param name="options">The store options this database belongs to.</param>
    public AugmentedPolecatDatabase(StoreOptions options)
        : base(options)
    {
        _options = options;

        // The store's default logger writes every DDL statement it runs to the console, which a
        // host's output is not the place for. Failures still surface as exceptions.
        MigrationLogger = new DefaultMigrationLogger(TextWriter.Null);
    }

    /// <inheritdoc/>
    public override IFeatureSchema[] BuildFeatureSchemas() =>
        base.BuildFeatureSchemas()
            .Select(feature => feature.Identifier == PatchedEventStoreFeature.EventStoreIdentifier ? new PatchedEventStoreFeature(feature) : feature)
            .Append(new StoreMarkerFeature(_options.DatabaseSchemaName))
            .ToArray();
}
