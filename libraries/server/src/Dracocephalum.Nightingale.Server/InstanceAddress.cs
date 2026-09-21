using System.Net;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// Where this instance is reached by others: the address it writes with every lease it takes, so
/// a caller refused because a group runs here can be sent here. The configured advertised address
/// when there is one; otherwise derived from what the server listens on, with a wildcard host
/// replaced by this machine's name, which in a container or a pod is the routable one. Derived
/// once, after the server has started listening; a host that listens on nothing, a test server,
/// has no address and its leases carry none.
/// </summary>
/// <param name="options">The cluster settings.</param>
/// <param name="server">The server, for what it listens on.</param>
public sealed class InstanceAddress(NightingaleOptionsBase options, IServer server)
{
    private static readonly string[] Wildcards = ["0.0.0.0", "[::]", "::", "+", "*"];
    private readonly Lock _gate = new();
    private Uri? _derived;
    private bool _tried;

    /// <summary>Gets the address, or <see langword="null"/> when this instance has none.</summary>
    public Uri? Current
    {
        get
        {
            if (options.Cluster.AdvertisedUri is { } configured)
            {
                return configured;
            }

            lock (_gate)
            {
                if (!_tried)
                {
                    _tried = true;
                    _derived = Derive();
                }

                return _derived;
            }
        }
    }

    private Uri? Derive()
    {
        var listening = server.Features.Get<IServerAddressesFeature>()?.Addresses
            .FirstOrDefault(address => address.StartsWith("http", StringComparison.OrdinalIgnoreCase));
        if (listening is null || !Uri.TryCreate(listening, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (!Wildcards.Contains(uri.Host, StringComparer.Ordinal))
        {
            return uri;
        }

        return new UriBuilder(uri) { Host = Dns.GetHostName() }.Uri;
    }
}
