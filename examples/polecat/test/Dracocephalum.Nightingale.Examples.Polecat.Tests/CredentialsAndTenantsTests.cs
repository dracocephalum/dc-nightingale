using Dracocephalum.Nightingale.Examples.Polecat.Common;
using Dracocephalum.Nightingale.Examples.Polecat.CredentialsAndTenants;
using Shouldly;

namespace Dracocephalum.Nightingale.Examples.Polecat.Tests;

/// <summary>
/// The scenario as an integration test: the same code the program runs, against the local SQL
/// Server named by <c>NIGHTINGALE_SQLSERVER</c> or the local default, asserting the report.
/// </summary>
[Collection(SharedRun.Name)]
[Trait("Category", "Integration")]
public sealed class CredentialsAndTenantsTests(SharedRun.Fixture fixture)
{
    [Fact]
    public async Task RunAsync_ShouldMakeCredentialsKeepThemToTheirRolesAndTenantsAndDisableATenant()
    {
        // Arrange
        var run = fixture.Run;

        // Act
        var report = await Scenario.RunAsync(run, TextWriter.Null, TestContext.Current.CancellationToken);

        // Assert
        report.Made.ShouldBe(["operator", "reader"]);
        report.ReaderRefusal.ShouldBe("AccessDeniedException (Ops)");
        report.OperatorCreated.ShouldBeTrue();
        report.ReaderReadAfterPasswordChange.ShouldBeTrue();
        report.Tenant.Name.ShouldStartWith("acme-");
        report.BoundRefusal.ShouldBe("TenantNotFoundException");
        report.TenantRevision.ShouldBe(1);
        report.TenantEvents.ShouldBe(2);
        report.ReaderEvents.ShouldBe(0);
        report.DisabledRefusal.ShouldBe("TenantDisabledException");
        report.Remaining.ShouldBeEmpty();
    }
}
