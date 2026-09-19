// Runs the scenario against the local SQL Server and narrates it. Exit code 0 when
// every step completed, 1 otherwise; the test project asserts the same run.
using Dracocephalum.Nightingale.Examples.CategoryOrdinals;
using Dracocephalum.Nightingale.Examples.Common;

return await ExampleProgram.RunAsync(Scenario.RunAsync, report => $"Done: ordinals {string.Join(", ", report.Ordinals)} read, {string.Join(", ", report.AfterDelete)} after the delete; live ordinal {report.LiveOrdinal}.");
