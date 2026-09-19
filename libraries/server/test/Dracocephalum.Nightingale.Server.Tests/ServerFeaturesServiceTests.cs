using Dracocephalum.Nightingale.Protocol.V1;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Tests;

public sealed class ServerFeaturesServiceTests : IAsyncLifetime
{
    private WebApplication? _app;

    public async ValueTask InitializeAsync()
    {
        // Arrange: a minimal host, exactly as a real host would mount the server.
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddNightingaleServer();
        _app = builder.Build();
        _app.MapNightingaleServer();
        await _app.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Ping_ShouldReturnTheServerVersion()
    {
        // Arrange
        using var channel = GrpcChannel.ForAddress(
            "http://localhost",
            new GrpcChannelOptions { HttpHandler = _app!.GetTestServer().CreateHandler() });
        var client = new ServerFeatures.ServerFeaturesClient(channel);

        // Act
        var response = await client.PingAsync(new PingRequest(), cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        response.Version.ShouldNotBeNullOrWhiteSpace();
    }
}
