using Dracocephalum.Nightingale.Examples.Polecat.BinaryData;
using Dracocephalum.Nightingale.Examples.Polecat.Common;
using Shouldly;

namespace Dracocephalum.Nightingale.Examples.Polecat.Tests;

/// <summary>
/// The scenario as an integration test: the same code the program runs, against the local SQL
/// Server named by <c>NIGHTINGALE_SQLSERVER</c> or the local default, asserting the report.
/// </summary>
[Collection(SharedRun.Name)]
[Trait("Category", "Integration")]
public sealed class BinaryDataTests(SharedRun.Fixture fixture)
{
    [Fact]
    public async Task RunAsync_ShouldCarryBinaryDataBesideTheJsonAndKeepTheJsonOnceTheKeyIsGone()
    {
        // Arrange
        var run = fixture.Run;

        // Act
        var report = await Scenario.RunAsync(run, TextWriter.Null, TestContext.Current.CancellationToken);

        // Assert: both bodies read back, the binary data decrypting while its key exists.
        report.ContentType.ShouldBe(Scenario.EncryptedContentType);
        report.Country.ShouldBe("NZ");
        report.DecryptedName.ShouldBe("Sample Person");
        report.BinaryOnlyJson.ShouldBe("{}");
        report.BinaryOnlyLength.ShouldBe(6);

        // Assert: the key destroyed, the JSON still says what happened.
        report.CountryAfterShredding.ShouldBe("NZ");
        report.DecryptableAfterShredding.ShouldBeFalse();
        report.CategoryWithBinaryData.ShouldBe(2);
        report.ContentTypeAloneRefusal.ShouldBe(nameof(ArgumentException));
    }
}
