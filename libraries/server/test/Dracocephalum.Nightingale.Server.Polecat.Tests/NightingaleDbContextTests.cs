using Dracocephalum.Nightingale.Server.Data;
using Dracocephalum.Nightingale.Server.Polecat.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests;

/// <summary>
/// The gateway's own context as this backend configures it. The context owns its tables, so it
/// is held to the conventions, and to its migrations: a change to the model without a migration
/// fails here rather than at a server's first start. Building the model and writing a migration
/// script need the provider's type mappings, not a connection, so a placeholder string is enough.
/// </summary>
public sealed class NightingaleDbContextTests
{
    private const string DesignTime = "Server=localhost;Database=design-time;Integrated Security=true";

    /// <summary>The columns that refer to an event in the store's table, which is the store's and takes no foreign key of ours.</summary>
    private static readonly string[] ExternalReferences = ["SubscriptionParkedEvent.EventId", "SubscriptionOutboxEntry.EventId"];

    [Fact]
    public void Model_WhenBuilt_ShouldFollowConventions()
    {
        // Arrange
        using var context = Context(NightingaleSchema.Default);

        // Act & Assert: throws with every violation listed.
        ModelConventions.Check(context, ExternalReferences);
    }

    [Fact]
    public void OrdinalLinkType_ShouldKeepTheNumbersItStores()
    {
        // Act & Assert: the links store these numbers; a member renumbered, reused or added in
        // their place reads every link already written as another stream's.
        Enum.GetUnderlyingType(typeof(OrdinalLinkType)).ShouldBe(typeof(byte));
        Enum.GetValues<OrdinalLinkType>().ToDictionary(type => type.ToString(), type => (byte)type).ShouldBe(new Dictionary<string, byte>
        {
            ["Correlation"] = 1,
            ["Streams"] = 2,
            ["CategoryStreams"] = 3,
        });
    }

    [Fact]
    public void Model_ShouldHaveNoChangeTheMigrationsDoNotCarry()
    {
        // Arrange
        using var context = Context(NightingaleSchema.Default);

        // Act & Assert: a model changed without `dotnet ef migrations add` is caught here.
        context.Database.HasPendingModelChanges().ShouldBeFalse();
        context.Database.GetMigrations().ShouldNotBeEmpty();
    }

    [Fact]
    public void Model_ShouldBeBuiltPerSchema()
    {
        // Arrange: two contexts of one type in one process, over two schemas.
        using var first = Context(NightingaleSchema.Default);
        using var second = Context("elsewhere");

        // Act & Assert: each has a model of its own, or the second would use the first's tables.
        first.Model.GetDefaultSchema().ShouldBe(NightingaleSchema.Default);
        second.Model.GetDefaultSchema().ShouldBe("elsewhere");
    }

    [Fact]
    public void Migrations_WhenTheHostNamesAnotherSchema_ShouldRunInThatSchema()
    {
        // Arrange: the migrations were generated under the default schema and are not edited.
        using var context = Context("elsewhere");

        // Act
        var script = context.GetService<IMigrator>().GenerateScript();

        // Assert: every table and the schema itself moved; nothing is left in the default one.
        script.ShouldSatisfyAllConditions(
            text => text.ShouldContain("CREATE SCHEMA [elsewhere]"),
            text => text.ShouldContain("CREATE TABLE [elsewhere].[SubscriptionGroup]"),
            text => text.ShouldContain("CREATE TABLE [elsewhere].[SubscriptionParkedEvent]"),
            text => text.ShouldContain("CREATE TABLE [elsewhere].[SubscriptionOutboxEntry]"),
            text => text.ShouldContain("CREATE TABLE [elsewhere].[Lease]"),
            text => text.ShouldContain("CREATE TABLE [elsewhere].[OrdinalSequencingProgress]"),
            text => text.ShouldContain("CREATE TABLE [elsewhere].[StoreProperty]"),
            text => text.ShouldContain("[elsewhere].[__EFMigrationsHistory]"),
            text => text.ShouldNotContain("[" + NightingaleSchema.Default + "]"));
    }

    [Fact]
    public void Migrations_ShouldKeepOneOrdinalSequencingRowPerTenantAndOneForTheSharedSequence()
    {
        // Arrange: the shared sequence's row is the one whose tenant is null, and the provider's
        // default filter on a unique index over a nullable column would admit any number of them.
        using var context = Context(NightingaleSchema.Default);

        // Act
        var script = context.GetService<IMigrator>().GenerateScript();
        var index = script.Split('\n').Single(line => line.Contains("IX_OrdinalSequencingProgress_TenantId", StringComparison.Ordinal));

        // Assert
        index.ShouldContain("CREATE UNIQUE INDEX");
        index.ShouldNotContain("WHERE");
    }

    [Fact]
    public void Migrations_UnderTheDefaultSchema_ShouldRunAsGenerated()
    {
        // Arrange
        using var context = Context(NightingaleSchema.Default);

        // Act
        var script = context.GetService<IMigrator>().GenerateScript();

        // Assert
        script.ShouldContain("CREATE TABLE [nightingale].[StoreProperty]");
        script.ShouldContain("[nightingale].[__EFMigrationsHistory]");
    }

    private static NightingaleDbContext Context(string schema)
    {
        var builder = new DbContextOptionsBuilder<NightingaleDbContext>();
        NightingaleDbContextFactory.Configure(builder, DesignTime, schema);
        return new NightingaleDbContext(builder.Options, new NightingaleSchema(schema));
    }
}
