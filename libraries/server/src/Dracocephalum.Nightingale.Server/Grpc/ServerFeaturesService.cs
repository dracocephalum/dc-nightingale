using System.Reflection;

using Dracocephalum.Nightingale.Protocols.Grpc.V1;
using Grpc.Core;

namespace Dracocephalum.Nightingale.Server.Grpc;

/// <summary>
/// The server-features service: liveness, the server's version, and what the store was
/// initialized with, so a client can learn before its first read whether the virtual streams can
/// be read by ordinal.
/// </summary>
/// <param name="stores">The backend's stores, for what the store was initialized with and whether a read may span all tenants.</param>
/// <param name="options">The host's options, for whether calls are authenticated; none in a host that registered none.</param>
public sealed class ServerFeaturesService(ITenantStores stores, NightingaleOptionsBase? options = null) : ServerFeatures.ServerFeaturesBase
{
    private static readonly string Version =
        typeof(ServerFeaturesService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";

    /// <inheritdoc/>
    public override Task<PingResponse> Ping(PingRequest request, ServerCallContext context) =>
        Task.FromResult(new PingResponse
        {
            Version = Version,
            SupportsOrdinals = stores.GetStreams(Auth.TenantScope.Default).OrdinalsEnabled,
            AuthenticationRequired = options?.Auth.Enabled ?? false,
            SupportsAllTenants = stores.SupportsAllTenants,
        });
}
