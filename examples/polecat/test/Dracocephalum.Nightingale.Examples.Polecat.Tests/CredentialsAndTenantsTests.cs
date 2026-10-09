using Dracocephalum.Nightingale.Examples.Polecat.Common;
using Dracocephalum.Nightingale.Examples.Polecat.CredentialsAndTenants;
using Shouldly;

namespace Dracocephalum.Nightingale.Examples.Polecat.Tests;

/// <summary>
/// The scenario as an integration test: the same code the program runs, against the local SQL
/// Server named by <c>NIGHTINGALE_SQLSERVER</c> or the local default, asserting the report.
/// </summary>
[Collection(OwnDatabase.Name)]
[Trait("Category", "Integration")]
public sealed class CredentialsAndTenantsTests
{
    [Fact]
    public async Task RunAsync_ShouldMakeCredentialsKeepThemToTheirRolesAndTenantsAndDisableATenant()
    {
        // Arrange
        var master = ExampleEnvironment.ResolveMasterConnectionString();

        // Act
        var report = await Scenario.RunAsync(master, TextWriter.Null, TestContext.Current.CancellationToken);

        // Assert
        report.Made.ShouldBe(["operator", "reader"]);
        report.ReaderRefusal.ShouldBe("AccessDeniedException (Ops)");
        report.OperatorCreated.ShouldBeTrue();
        report.ReaderReadAfterPasswordChange.ShouldBeTrue();
        report.Tenant.Name.ShouldBe("acme");
        report.BoundRefusal.ShouldBe("TenantNotFoundException");
        report.DisabledRefusal.ShouldBe("TenantDisabledException");
        report.Remaining.ShouldBeEmpty();
    }
}
