using JasperFx.Descriptors;
using JasperFx.MultiTenancy;
using Polecat.Internal;
using Polecat.Storage;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// One database for every tenant, answered by the gateway's own database class. The store's
/// default tenancy is internal and always builds the plain database; this one is the seam through
/// which the schema additions reach the migration, because the store migrates whatever the tenancy
/// returns. The store still runs in conjoined mode with the default tenant, so enabling tenants
/// later is a matter of credentials, not schema.
/// </summary>
internal sealed class SingleTenancy(PolecatDatabase database, string connectionString) : ITenancy
{
    private readonly ConnectionFactory _factory = new(connectionString);

    /// <inheritdoc/>
    public DatabaseCardinality Cardinality => DatabaseCardinality.Single;

    /// <inheritdoc/>
    public string DefaultTenantId => JasperFx.StorageConstants.DefaultTenantId;

    /// <inheritdoc/>
    public ConnectionFactory GetConnectionFactory(string tenantId) => _factory;

    /// <inheritdoc/>
    public PolecatDatabase GetDatabase(string tenantId) => database;

    /// <inheritdoc/>
    public IReadOnlyList<PolecatDatabase> AllDatabases() => [database];

    /// <inheritdoc/>
    public Task<IReadOnlyList<PolecatDatabase>> BuildDatabasesAsync(CancellationToken token = default) =>
        Task.FromResult(AllDatabases());
}
