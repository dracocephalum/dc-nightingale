using Microsoft.Data.SqlClient;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests;

public sealed class ReadOnlyConnectionTests
{
    private const string Main = "Server=sql.example;Database=nightingale;Integrated Security=true";

    [Fact]
    public void Options_ShouldUseTheReadOnlyConnectionForHistoryOnlyByDefault()
    {
        // Act
        var options = new NightingaleOptions();

        // Assert
        options.UseReadOnlyConnection.ShouldBeTrue();
        options.ReadOnlyConnectionStringName.ShouldBeNull();
        options.ReadStreamsFromReadOnlyConnection.ShouldBeFalse();
    }

    [Fact]
    public void Resolve_WhenNoNameIsConfigured_ShouldSetTheIntentOnTheMainString()
    {
        // Act
        var resolved = ReadOnlyConnection.Resolve(Main, new NightingaleOptions(), named: null);

        // Assert: the same server and database, asked for with a read-only intent.
        var builder = new SqlConnectionStringBuilder(resolved);
        builder.ApplicationIntent.ShouldBe(ApplicationIntent.ReadOnly);
        builder.DataSource.ShouldBe("sql.example");
        builder.InitialCatalog.ShouldBe("nightingale");
    }

    [Fact]
    public void Resolve_WhenTheMainStringStatesAnIntent_ShouldReplaceIt()
    {
        // Act
        var resolved = ReadOnlyConnection.Resolve(Main + ";ApplicationIntent=ReadWrite", new NightingaleOptions(), named: null);

        // Assert
        new SqlConnectionStringBuilder(resolved).ApplicationIntent.ShouldBe(ApplicationIntent.ReadOnly);
    }

    [Fact]
    public void Resolve_WhenANameIsConfigured_ShouldUseThatStringExactlyAsWritten()
    {
        // Arrange
        const string secondary = "Server=replica.example;Database=nightingale;Integrated Security=true";
        var options = new NightingaleOptions { ReadOnlyConnectionStringName = "NightingaleReadOnly" };

        // Act
        var resolved = ReadOnlyConnection.Resolve(Main, options, name => name == "NightingaleReadOnly" ? secondary : null);

        // Assert
        resolved.ShouldBe(secondary);
    }

    [Fact]
    public void Resolve_WhenTheNamedStringIsMissing_ShouldThrowNamingTheSetting()
    {
        // Arrange
        var options = new NightingaleOptions { ReadOnlyConnectionStringName = "NightingaleReadOnly" };

        // Act
        var exception = Should.Throw<InvalidOperationException>(() => ReadOnlyConnection.Resolve(Main, options, _ => null));

        // Assert
        exception.Message.ShouldContain("NightingaleReadOnly");
        exception.Message.ShouldContain(nameof(NightingaleOptions.ReadOnlyConnectionStringName));
    }

    [Fact]
    public void Resolve_WhenTheHostPassesAString_ShouldUseItOverTheName()
    {
        // Arrange
        const string passed = "Server=passed.example;Database=nightingale;Integrated Security=true";
        var options = new NightingaleOptions { ReadOnlyConnectionStringName = "NightingaleReadOnly" };

        // Act
        var resolved = ReadOnlyConnection.Resolve(Main, options, _ => "Server=named.example", passed);

        // Assert
        resolved.ShouldBe(passed);
    }

    [Fact]
    public void Resolve_WhenTurnedOff_ShouldReturnNothing()
    {
        // Arrange
        var options = new NightingaleOptions { UseReadOnlyConnection = false, ReadOnlyConnectionStringName = "NightingaleReadOnly" };

        // Act
        var resolved = ReadOnlyConnection.Resolve(Main, options, _ => "Server=named.example", "Server=passed.example");

        // Assert
        resolved.ShouldBeNull();
    }
}
