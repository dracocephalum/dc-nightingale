using Shouldly;

namespace Dracocephalum.Nightingale.Examples.Common.Tests;

/// <summary>
/// The program shell and the environment, with no server involved: the scenario is a delegate.
/// </summary>
public sealed class ExampleProgramTests
{
    [Fact]
    public async Task RunAsync_WhenTheScenarioCompletes_ShouldPrintTheClosingLineAndExitZero()
    {
        // Arrange
        var output = new StringWriter();
        var error = new StringWriter();

        // Act
        var exit = await ExampleProgram.RunAsync(
            (master, writer, _) =>
            {
                master.ShouldNotBeNullOrWhiteSpace();
                return writer.WriteLineAsync("1. A step.").ContinueWith(_ => 42, TaskScheduler.Default);
            },
            report => $"Done: {report}.",
            output,
            error);

        // Assert
        exit.ShouldBe(0);
        output.ToString().ShouldBe($"1. A step.{Environment.NewLine}Done: 42.{Environment.NewLine}");
        error.ToString().ShouldBeEmpty();
    }

    [Fact]
    public async Task RunAsync_WhenTheScenarioFails_ShouldReportTheFailureAndExitOne()
    {
        // Arrange
        var output = new StringWriter();
        var error = new StringWriter();

        // Act
        var exit = await ExampleProgram.RunAsync<int>(
            (_, _, _) => throw new InvalidOperationException("step 3 broke"),
            report => $"Done: {report}.",
            output,
            error);

        // Assert
        exit.ShouldBe(1);
        error.ToString().ShouldBe($"Failed: InvalidOperationException: step 3 broke{Environment.NewLine}");
        output.ToString().ShouldBeEmpty();
    }

    [Fact]
    public void ResolveMasterConnectionString_ShouldNameTheMasterDatabase()
    {
        // Act
        var master = ExampleEnvironment.ResolveMasterConnectionString();

        // Assert: the environment's string or the default; either way it points at master.
        master.ShouldContain("master", Case.Insensitive);
    }

    [Fact]
    public void Reserve_ShouldNameADatabaseNoOtherRunUsesAndPointTheConnectionStringAtIt()
    {
        // Arrange & Act
        var first = ExampleDatabase.Reserve(ExampleEnvironment.DefaultMasterConnectionString);
        var second = ExampleDatabase.Reserve(ExampleEnvironment.DefaultMasterConnectionString);

        // Assert: nothing was created, so nothing needs dropping; the names are what the sweep looks for.
        first.Name.ShouldStartWith("nightingale_example_");
        first.Name.ShouldNotBe(second.Name);
        first.ConnectionString.ShouldContain($"Initial Catalog={first.Name}");
    }
}
