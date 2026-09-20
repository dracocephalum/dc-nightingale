using Dracocephalum.Nightingale.Protocol.V1;
using FakeItEasy;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Tests;

public sealed class ServerFeaturesServiceTests : IAsyncLifetime
{
    private WebApplication? _app;

    public async ValueTask InitializeAsync()
    {
        // Arrange: a minimal host, exactly as a real host would mount the server, over a store
        // that was initialized with ordinals.
        var store = A.Fake<IStreamStore>();
        A.CallTo(() => store.OrdinalsEnabled).Returns(true);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddNightingaleServer();
        builder.Services.AddSingleton(store);
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
    public async Task Ping_ShouldReturnTheServerVersionAndWhatTheStoreHas()
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
        response.SupportsOrdinals.ShouldBeTrue();
    }
}
