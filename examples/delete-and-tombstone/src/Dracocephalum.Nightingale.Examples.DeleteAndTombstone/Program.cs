// Runs the scenario against the local SQL Server and narrates it. Exit code 0 when
// every step completed, 1 otherwise; the test project asserts the same run.
using Dracocephalum.Nightingale.Examples.Common;
using Dracocephalum.Nightingale.Examples.DeleteAndTombstone;

return await ExampleProgram.RunAsync(Scenario.RunAsync, report => $"Done: deleted, tombstoned and revived {report.Stream}; the default server refused with {report.DefaultServerRefusal}.");
