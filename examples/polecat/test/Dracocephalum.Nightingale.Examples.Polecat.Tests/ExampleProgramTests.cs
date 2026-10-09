using Dracocephalum.Nightingale.Examples.Polecat.Common;
using Shouldly;

namespace Dracocephalum.Nightingale.Examples.Polecat.Tests;

/// <summary>
/// The program around the scenarios and the environment, with no server involved: the scenarios
/// here are delegates.
/// </summary>
public sealed class ExampleProgramTests
{
    [Fact]
    public async Task RunAsync_WithoutArguments_ShouldListTheScenariosAndRunNone()
    {
        // Arrange
        var output = new StringWriter();
        var ran = new List<string>();

        // Act
        var exit = await ExampleProgram.RunAsync([], [Completing("first", ran), Completing("second-one", ran)], output, new StringWriter());

        // Assert
        exit.ShouldBe(0);
        ran.ShouldBeEmpty();
        output.ToString().ShouldBe(
            $"Name a scenario to run, several, or 'all':{Environment.NewLine}" +
            $"  first       shows first{Environment.NewLine}" +
            $"  second-one  shows second-one{Environment.NewLine}");
    }

    [Fact]
    public async Task RunAsync_WithOneName_ShouldRunThatScenarioPrintItsClosingLineAndExitZero()
    {
        // Arrange
        var output = new StringWriter();
        var error = new StringWriter();
        var ran = new List<string>();

        // Act
        var exit = await ExampleProgram.RunAsync(["second"], [Completing("first", ran), Completing("second", ran)], output, error);

        // Assert
        exit.ShouldBe(0);
        ran.ShouldBe(["second"]);
        output.ToString().ShouldBe($"1. A step of second.{Environment.NewLine}Done: second.{Environment.NewLine}");
        error.ToString().ShouldBeEmpty();
    }

    [Fact]
    public async Task RunAsync_WithAll_ShouldRunEveryScenarioUnderItsNameAndGoOnAfterAFailure()
    {
        // Arrange
        var output = new StringWriter();
        var error = new StringWriter();
        var ran = new List<string>();
        var failing = new ExampleScenario("broken", "breaks", (_, _, _) => throw new InvalidOperationException("step 3 broke"));

        // Act
        var exit = await ExampleProgram.RunAsync(["all"], [Completing("first", ran), failing, Completing("last", ran)], output, error);

        // Assert
        exit.ShouldBe(1);
        ran.ShouldBe(["first", "last"]);
        output.ToString().ShouldBe(
            $"== first =={Environment.NewLine}1. A step of first.{Environment.NewLine}Done: first.{Environment.NewLine}" +
            $"== broken =={Environment.NewLine}" +
            $"== last =={Environment.NewLine}1. A step of last.{Environment.NewLine}Done: last.{Environment.NewLine}" +
            $"2 of 3 scenarios completed.{Environment.NewLine}");
        error.ToString().ShouldBe($"Failed: broken: InvalidOperationException: step 3 broke{Environment.NewLine}");
    }

    [Fact]
    public async Task RunAsync_WithANameThatIsNotAScenarios_ShouldSaySoListThemAndRunNone()
    {
        // Arrange
        var output = new StringWriter();
        var error = new StringWriter();
        var ran = new List<string>();

        // Act
        var exit = await ExampleProgram.RunAsync(["first", "frist"], [Completing("first", ran)], output, error);

        // Assert
        exit.ShouldBe(2);
        ran.ShouldBeEmpty();
        output.ToString().ShouldBeEmpty();
        error.ToString().ShouldBe($"'frist' is not a scenario. The scenarios are:{Environment.NewLine}  first  shows first{Environment.NewLine}");
    }

    [Fact]
    public void All_ShouldNameEachScenarioOnceInKebabCaseAndSayWhatItShows()
    {
        // Act
        var names = Scenarios.All.Select(scenario => scenario.Name).ToList();

        // Assert: the names the READMEs and the tests use.
        names.ShouldBe(["append-and-read", "catch-up-subscription", "category-stream", "category-ordinals", "cluster-redirect", "competing-consumers", "credentials-and-tenants", "delete-and-tombstone", "persistent-subscription"]);
        names.ShouldAllBe(name => name.All(character => char.IsAsciiLetterLower(character) || character == '-') && name != ExampleProgram.All);
        Scenarios.All.ShouldAllBe(scenario => !string.IsNullOrWhiteSpace(scenario.Summary));
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

    private static ExampleScenario Completing(string name, List<string> ran) => ExampleScenario.Create(
        name,
        $"shows {name}",
        async (master, output, _) =>
        {
            master.ShouldNotBeNullOrWhiteSpace();
            ran.Add(name);
            await output.WriteLineAsync($"1. A step of {name}.");
            return name;
        },
        report => $"Done: {report}.");
}
