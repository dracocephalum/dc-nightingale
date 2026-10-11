using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests;

/// <summary>
/// What the store's schema tooling is told to write ends up in the host's log: a script at the
/// level the gateway's own migrations are logged at, a failure at error with the statement, and
/// then thrown on, as the tooling's own logger throws it.
/// </summary>
public sealed class StoreMigrationLoggerTests
{
    private readonly FakeLogger<StoreSchema> _logger = new();

    [Fact]
    public void SchemaChange_ShouldLogTheScriptAtInformationNamingTheDatabase()
    {
        // Arrange
        var sut = new StoreMigrationLogger(_logger, "nightingale");

        // Act
        sut.SchemaChange("CREATE TABLE dbo.pc_events (seq_id bigint);");

        // Assert
        var entry = _logger.Collector.LatestRecord;
        entry.Level.ShouldBe(LogLevel.Information);
        entry.Message.ShouldContain("database nightingale");
        entry.Message.ShouldContain("CREATE TABLE dbo.pc_events (seq_id bigint);");
    }

    [Fact]
    public void OnFailure_ShouldLogTheStatementAtErrorAndThrowTheFailureOn()
    {
        // Arrange
        var sut = new StoreMigrationLogger(_logger, "nightingale");
        using var command = new SqlCommand("DROP INDEX ix_pc_events_category_seq ON dbo.pc_events");
        var failure = new InvalidOperationException("the index is not there");

        // Act
        var thrown = Should.Throw<InvalidOperationException>(() => sut.OnFailure(command, failure));

        // Assert
        thrown.ShouldBeSameAs(failure);
        var entry = _logger.Collector.LatestRecord;
        entry.Level.ShouldBe(LogLevel.Error);
        entry.Exception.ShouldBeSameAs(failure);
        entry.Message.ShouldContain("DROP INDEX ix_pc_events_category_seq ON dbo.pc_events");
    }
}
