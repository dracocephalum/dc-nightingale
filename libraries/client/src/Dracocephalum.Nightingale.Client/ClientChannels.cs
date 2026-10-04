using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.Client.Balancer;
using Grpc.Net.Client.Configuration;

namespace Dracocephalum.Nightingale.Client;

/// <summary>
/// Opens the channels a client's settings describe: one to the instances the connection string
/// names, and one to any single instance a server sends the client to, under the same transport
/// settings. A cleartext connection never goes through the machine's HTTP proxy, which cannot
/// carry cleartext HTTP/2.
/// </summary>
internal static class ClientChannels
{
    /// <summary>The name a channel over several instances goes by; no instance is addressed by it.</summary>
    private const string ClusterAuthority = "nightingale";

    /// <summary>Opens the channel to the instances in the settings.</summary>
    /// <param name="settings">The settings.</param>
    /// <returns>The channel, which owns its handler.</returns>
    public static GrpcChannel Open(NightingaleClientSettings settings)
    {
        if (settings.Endpoints.Count == 0)
        {
            throw new ArgumentException("The settings name no host.", nameof(settings));
        }

        var first = settings.Endpoints[0];
        if (!settings.Discover && settings.Endpoints.Count == 1)
        {
            var scheme = settings.Tls ? Uri.UriSchemeHttps : Uri.UriSchemeHttp;
            return OpenTo(settings, new UriBuilder(scheme, first.Host, first.Port).Uri);
        }

        // Every instance serves every call, so the instances are used in rotation and one that is
        // down is passed over; a group that runs elsewhere is the server's to point at.
        var options = OptionsFor(settings, settings.Tls);
        options.Credentials = settings.Tls ? ChannelCredentials.SecureSsl : ChannelCredentials.Insecure;
        options.ServiceConfig = new ServiceConfig { LoadBalancingConfigs = { new RoundRobinConfig() } };
        if (settings.Discover)
        {
            return GrpcChannel.ForAddress($"dns:///{first.Host}:{first.Port}", options);
        }

        var addresses = settings.Endpoints.Select(endpoint => new BalancerAddress(endpoint.Host, endpoint.Port)).ToList();
        options.ServiceProvider = new ResolverProvider(new StaticResolverFactory(_ => addresses));
        return GrpcChannel.ForAddress($"static:///{ClusterAuthority}", options);
    }

    /// <summary>Opens a channel to one instance, such as the one a server said runs a group.</summary>
    /// <param name="settings">The settings.</param>
    /// <param name="address">The instance's address.</param>
    /// <returns>The channel, which owns its handler.</returns>
    public static GrpcChannel OpenTo(NightingaleClientSettings settings, Uri address) =>
        GrpcChannel.ForAddress(address, OptionsFor(settings, address.Scheme == Uri.UriSchemeHttps));

    private static GrpcChannelOptions OptionsFor(NightingaleClientSettings settings, bool encrypted)
    {
        // Many subscriptions share a client, and one HTTP/2 connection carries a limited number of
        // streams at once, so the handler opens another connection when one is full.
        var handler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            KeepAlivePingDelay = settings.KeepAliveInterval ?? Timeout.InfiniteTimeSpan,
            KeepAlivePingTimeout = settings.KeepAliveTimeout ?? Timeout.InfiniteTimeSpan,
            UseProxy = encrypted,
        };

        if (!settings.TlsVerifyCertificate)
        {
#pragma warning disable CA5359 // The connection string asked for it, tlsVerifyCert=false, for a development certificate.
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
#pragma warning restore CA5359
        }

        return new GrpcChannelOptions { HttpHandler = handler, DisposeHttpClient = true };
    }

    /// <summary>The one service the channel asks for: the resolver that answers with the listed instances.</summary>
    private sealed class ResolverProvider(ResolverFactory factory) : IServiceProvider
    {
        private readonly ResolverFactory[] _factories = [factory];

        public object? GetService(Type serviceType) =>
            serviceType == typeof(IEnumerable<ResolverFactory>) ? _factories : null;
    }
}
