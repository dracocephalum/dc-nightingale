using System.Net;

using Dracocephalum.Nightingale.Server;
using Dracocephalum.Nightingale.Server.Polecat;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dracocephalum.Nightingale.Examples.CatchUpSubscription;

/// <summary>
/// A Nightingale server hosted in the sample's own process, on a loopback port the operating
/// system picks, speaking cleartext HTTP/2 so no certificate is needed. It is wired exactly as the
/// default host is: the server extensions plus the store registration, nothing else.
/// </summary>
public sealed class ExampleServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private ExampleServer(WebApplication app, Uri address)
    {
        _app = app;
        Address = address;
    }

    /// <summary>Gets the address a client connects to.</summary>
    public Uri Address { get; }

    /// <summary>Starts a server backed by the given database.</summary>
    /// <param name="connectionString">The database's connection string.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The running server; dispose it to stop it.</returns>
    public static async Task<ExampleServer> StartAsync(string connectionString, CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));
        builder.Services.AddNightingaleServer();
        builder.Services.AddNightingalePolecat(connectionString);

        var app = builder.Build();
        app.MapNightingaleServer();
        await app.StartAsync(cancellationToken).ConfigureAwait(false);

        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
        var address = addresses?.FirstOrDefault()
            ?? throw new InvalidOperationException("The server started without reporting an address.");
        return new ExampleServer(app, new Uri(address));
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }
}
