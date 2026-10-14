using System.Text;

using Dracocephalum.Nightingale.Server.Polecat.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests.Integration;

/// <summary>
/// The category the database computes for a stream name is the one the server computes in process:
/// the router matches categories from the name, and a read by category matches the column, so the
/// two rules must be one. The shared database has other tests' events in it; only these streams are read.
/// </summary>
[Collection(SharedSqlServer.Name)]
[Trait("Category", "Integration")]
public sealed class CategoryColumnTests(SqlServerTestDatabase database)
{
    [Theory]
    [InlineData("orders-")]
    [InlineData("orders-1-")]
    [InlineData("nohyphen")]
    [InlineData("-leading")]
    [InlineData("o-")]
    public async Task Category_AsTheDatabaseComputesIt_ShouldBeWhatTheServerComputes(string prefix)
    {
        // Arrange: a name of the shape, made unique so the shared database can hold it.
        var stream = prefix + Guid.NewGuid().ToString("N");
        var appended = await database.Store.AppendAsync(stream, StreamState.NoStream, [new EventData(Guid.NewGuid(), "order_placed", Encoding.UTF8.GetBytes("{}"))], TestContext.Current.CancellationToken);

        // Act
        await using var context = await database.Services.GetRequiredService<IDbContextFactory<EventsDbContext>>().CreateDbContextAsync(TestContext.Current.CancellationToken);
        var stored = await context.EventRows.AsNoTracking().Where(row => row.SeqId == appended.Position).Select(row => row.Category).SingleAsync(TestContext.Current.CancellationToken);

        // Assert
        stored.ShouldBe(StreamNames.CategoryOf(stream));
    }
}
