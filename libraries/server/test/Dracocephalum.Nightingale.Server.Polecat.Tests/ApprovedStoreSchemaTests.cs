using System.Runtime.CompilerServices;

using Microsoft.Extensions.DependencyInjection;
using Polecat;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests;

/// <summary>
/// The event store's schema, as this version of its library and the gateway's additions define
/// it, against a copy kept in the repository. A server refuses at startup a database that is
/// behind what it expects, so nothing depends on this; what it adds is sight of the change at
/// review time. An upgrade of the store's library that changes its tables fails here, and
/// accepting it puts the difference in the pull request, statement by statement, before it
/// reaches any database. The gateway's own tables are not in the copy: their migrations are in
/// the repository already.
/// </summary>
public sealed class ApprovedStoreSchemaTests
{
    /// <summary>Set to 1 to accept the schema as it is now: the test rewrites the approved copy.</summary>
    private const string ApproveVariable = "NIGHTINGALE_APPROVE_SCHEMA";

    private const string ApprovedFile = "store-schema.sql";

    [Fact]
    public async Task StoreSchema_ShouldBeTheApprovedOne()
    {
        // Arrange: the store as a host registers it, with ordinals, so every column and index is
        // in the script; against a server nobody connects to.
        var services = new ServiceCollection();
        services.AddNightingalePolecat("Server=example;Database=nightingale;Trusted_Connection=True", TestAuth.Off(options => options.Store.AssignOrdinals = true));
        await using var provider = services.BuildServiceProvider();
        var databases = await provider.GetRequiredService<IDocumentStore>().Options.Tenancy!.BuildDatabasesAsync(TestContext.Current.CancellationToken);
        var written = Path.Combine(AppContext.BaseDirectory, "store-schema." + Guid.NewGuid().ToString("N") + ".sql");

        // Act
        string actual;
        try
        {
            await databases[0].WriteCreationScriptToFileAsync(written, TestContext.Current.CancellationToken);
            actual = Normalize(await File.ReadAllTextAsync(written, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(written);
        }

        if (Environment.GetEnvironmentVariable(ApproveVariable) == "1")
        {
            await File.WriteAllTextAsync(Path.Combine(SourceDirectory(), "Approved", ApprovedFile), actual, TestContext.Current.CancellationToken);
            return;
        }

        var approved = Normalize(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Approved", ApprovedFile), TestContext.Current.CancellationToken));

        // Assert
        actual.ShouldBe(
            approved,
            $"The event store's schema is not the approved one. If the change is intended, an upgrade of the store's library or of the gateway's additions, run this test once with {ApproveVariable}=1 and commit the rewritten Approved/{ApprovedFile}; its diff is the change.");
    }

    private static string Normalize(string script) => script.ReplaceLineEndings("\n").Trim() + "\n";

    private static string SourceDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}
