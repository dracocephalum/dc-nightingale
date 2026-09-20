// Runs the scenario against the local SQL Server and narrates it. Exit code 0 when
// every step completed, 1 otherwise; the test project asserts the same run.
using Dracocephalum.Nightingale.Examples.ClusterRedirect;
using Dracocephalum.Nightingale.Examples.Common;

return await ExampleProgram.RunAsync(Scenario.RunAsync, report => $"Done: the second consumer was refused as {report.SecondConsumerRefusal}; {report.Replayed} replayed through the other instance and the {report.ReplayedType} arrived at once.");
