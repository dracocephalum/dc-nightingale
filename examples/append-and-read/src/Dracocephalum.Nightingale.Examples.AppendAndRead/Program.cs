// Runs the scenario against the local SQL Server and narrates it. Exit code 0 when
// every step completed, 1 otherwise; the test project asserts the same run.
using Dracocephalum.Nightingale.Examples.AppendAndRead;
using Dracocephalum.Nightingale.Examples.Common;

return await ExampleProgram.RunAsync(Scenario.RunAsync, report => $"Done: {report.ReadForwards.Count} events round-tripped on {report.Stream}.");
