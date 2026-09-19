using Dracocephalum.Nightingale.Protocol.V1;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests.Integration;

/// <summary>
/// The default host boots against the test database, which the fixture's host initialized with the
/// same defaults, and serves the server-features service.
/// </summary>
[Collection(SharedSqlServer.Name)]
[Trait("Category", "Integration")]
public sealed class ProgramTests(SqlServerTestDatabase database)
{
    [Fact]
    public async Task DefaultHost_ShouldServeServerFeatures()
    {
        // Arrange
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:Nightingale", database.ConnectionString));

        // Act
        var response = await Ping(factory);

        // Assert
        response.Version.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task DefaultHost_WhenTheConnectionStringNameIsChanged_ShouldUseThatConnectionString()
    {
        // Arrange
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder
                .UseSetting("Nightingale:ConnectionStringName", "Store")
                .UseSetting("ConnectionStrings:Store", database.ConnectionString));

        // Act
        var response = await Ping(factory);

        // Assert
        response.Version.ShouldNotBeNullOrWhiteSpace();
    }

    private static async Task<PingResponse> Ping(WebApplicationFactory<Program> factory)
    {
        using var channel = GrpcChannel.ForAddress(
            factory.Server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = factory.Server.CreateHandler() });
        var client = new ServerFeatures.ServerFeaturesClient(channel);
        return await client.PingAsync(new PingRequest(), cancellationToken: TestContext.Current.CancellationToken);
    }
}
