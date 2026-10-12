using System.Text;

using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests.Integration;

/// <summary>
/// The collation a store is initialized under, against a real database: the default tells names
/// apart by case and by script, another UTF-8 collation follows its own rule, one that is not
/// UTF-8 is refused before anything is created unless the host chooses it knowingly, a database
/// provisioned by hand under such a collation is refused, and an unknown one is refused too.
/// Every test uses a database of its own, dropped at the end whatever happened; hosts start in
/// Initialize mode unless a test says otherwise.
/// </summary>
[Trait("Category", "Integration")]
public sealed class StoreCollationTests : IAsyncLifetime
{
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("{\"orderId\":1}");
    private readonly string _name = TestDatabases.NewName();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await TestDatabases.DropAsync(_name);

    [Fact]
    public async Task Start_ByDefault_ShouldTellStreamNamesApartByCaseAndByScript()
    {
        // Arrange & Act: the default collation is binary and UTF-8.
        using var host = await TestDatabases.StartProvisionedHostAsync(_name);
        var store = host.Store();
        await store.AppendAsync("Orders-1", StreamState.NoStream, [Event()], TestContext.Current.CancellationToken);
        var lower = await store.AppendAsync("orders-1", StreamState.NoStream, [Event()], TestContext.Current.CancellationToken);
        await store.AppendAsync("订单-1", StreamState.NoStream, [Event()], TestContext.Current.CancellationToken);
        var otherScript = await store.AppendAsync("账单-1", StreamState.NoStream, [Event()], TestContext.Current.CancellationToken);
        var read = await store.ReadAsync("账单-1", Direction.Forwards, null, 10, TestContext.Current.CancellationToken);

        // Assert: four streams. Under a collation that is not UTF-8 the last two would be one,
        // both stored as question marks.
        lower.Revision.ShouldBe(0);
        otherScript.Revision.ShouldBe(0);
        read.ShouldNotBeNull().Events.ShouldHaveSingleItem().Stream.ShouldBe("账单-1");
        (await TestDatabases.ScalarAsync<string>(_name, "SELECT [Value] FROM nightingale.StoreProperty WHERE [Name] = 'Collation'")).ShouldBe(NightingaleOptions.StoreOptions.DefaultCollation);
        (await TestDatabases.ScalarAsync<int>(_name, "SELECT COUNT(*) FROM dbo.pc_streams")).ShouldBe(4);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Start_WithAnotherUtf8Collation_ShouldFollowItsRuleForCase()
    {
        // Arrange: a host may still choose that case does not tell names apart. That the server
        // creates a database with the collation it is given is StoreDatabaseTests'.
        const string collation = "Latin1_General_100_CI_AS_SC_UTF8";

        // Act
        using var host = await TestDatabases.StartProvisionedHostAsync(_name, options => options.Store.Collation = collation);
        var store = host.Store();
        await store.AppendAsync("Orders-1", StreamState.NoStream, [Event()], TestContext.Current.CancellationToken);
        var lower = await store.AppendAsync("orders-1", StreamState.Any, [Event()], TestContext.Current.CancellationToken);

        // Assert: one stream, and the record says which collation the database has.
        lower.Revision.ShouldBe(1);
        (await TestDatabases.ScalarAsync<string>(_name, "SELECT [Value] FROM nightingale.StoreProperty WHERE [Name] = 'Collation'")).ShouldBe(collation);
        (await TestDatabases.ScalarAsync<int>(_name, "SELECT COUNT(*) FROM dbo.pc_streams")).ShouldBe(1);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("SQL_Latin1_General_CP1_CI_AS")]
    [InlineData("Latin1_General_100_BIN2")]
    public async Task Start_WithACollationThatIsNotUtf8_ShouldRefuseBeforeCreatingAnything(string collation)
    {
        // Act
        var exception = await Should.ThrowAsync<StoreInitializationException>(
            () => TestDatabases.StartHostAsync(_name, options => options.Store.Collation = collation));

        // Assert
        exception.Message.ShouldContain("is not a UTF-8 collation");
        exception.Message.ShouldContain(NightingaleOptions.StoreOptions.DefaultCollation);
        exception.Message.ShouldContain("Nightingale:Store:IgnoreCollationCompatibility");
        (await TestDatabases.ScalarAsync<object>("master", $"SELECT ISNULL(DB_ID('{_name}'), -1)")).ShouldBe(-1);
    }

    [Fact]
    public async Task Start_WhenTheHostIgnoresCollationCompatibility_ShouldServeADatabaseThatIsNotUtf8()
    {
        // Arrange: the host's own choice, for a collation the server would otherwise refuse.
        const string collation = "SQL_Latin1_General_CP1_CI_AS";
        static void Ignoring(NightingaleOptions options)
        {
            options.Store.Collation = collation;
            options.Store.IgnoreCollationCompatibility = true;
        }

        // Act: served with it, and a second start finds it as it was left.
        using (var first = await TestDatabases.StartProvisionedHostAsync(_name, Ignoring))
        {
            var appended = await first.Store().AppendAsync("orders-1", StreamState.NoStream, [Event()], TestContext.Current.CancellationToken);
            appended.Revision.ShouldBe(0);
            await first.StopAsync(TestContext.Current.CancellationToken);
        }

        using (var second = await TestDatabases.StartHostAsync(_name, TestDatabases.FastBoot(Ignoring)))
        {
            await second.StopAsync(TestContext.Current.CancellationToken);
        }

        var withoutTheChoice = await Should.ThrowAsync<StoreInitializationException>(() => TestDatabases.StartHostAsync(_name, TestDatabases.FastBoot(options => options.Store.Collation = collation)));

        // Assert: the choice is the host's, made each time, and not a property of the store.
        (await TestDatabases.ScalarAsync<string>(_name, "SELECT [Value] FROM nightingale.StoreProperty WHERE [Name] = 'Collation'")).ShouldBe(collation);
        (await TestDatabases.ScalarAsync<int>(_name, "SELECT COUNT(*) FROM nightingale.StoreProperty WHERE [Name] LIKE '%IgnoreCollation%'")).ShouldBe(0);
        withoutTheChoice.Message.ShouldContain("which is not a UTF-8 collation");
    }

    [Fact]
    public async Task Start_WhenAProvisionedDatabaseIsNotUtf8_ShouldRefuseAndInitializeNothing()
    {
        // Arrange: a database created by hand with the server's own default collation.
        await TestDatabases.CreateEmptyAsync(_name, collation: null);

        // Act: whatever the host configures, the database's own collation decides.
        var asConfigured = await Should.ThrowAsync<StoreInitializationException>(() => TestDatabases.StartHostAsync(_name));
        var whateverItHas = await Should.ThrowAsync<StoreInitializationException>(() => TestDatabases.StartHostAsync(_name, options => options.Store.Collation = null));

        // Assert
        asConfigured.Message.ShouldContain("which is not a UTF-8 collation");
        asConfigured.Message.ShouldContain("Nightingale:Store:IgnoreCollationCompatibility");
        whateverItHas.Message.ShouldContain("which is not a UTF-8 collation");
        (await TestDatabases.ScalarAsync<int>(_name, "SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped = 0")).ShouldBe(0);
    }

    [Fact]
    public async Task Start_WithAnUnknownCollation_ShouldRefuseBeforeCreatingAnything()
    {
        // Act
        var exception = await Should.ThrowAsync<StoreInitializationException>(
            () => TestDatabases.StartHostAsync(_name, options => options.Store.Collation = "No_Such_Collation"));

        // Assert
        exception.Message.ShouldContain("fn_helpcollations");
        (await TestDatabases.ScalarAsync<object>("master", $"SELECT ISNULL(DB_ID('{_name}'), -1)")).ShouldBe(-1);
    }

    private static EventData Event() => new(Guid.NewGuid(), "order_placed", Body);
}
