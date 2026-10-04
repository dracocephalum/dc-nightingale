// Lists the scenarios, or runs the ones named on the command line against the local SQL Server
// and narrates them: `-- append-and-read`, several names, or `-- all`. Exit code 0 when every
// scenario completed, 1 when one failed; the test project asserts the same runs.
using Dracocephalum.Nightingale.Examples.Polecat;
using Dracocephalum.Nightingale.Examples.Polecat.Common;

return await ExampleProgram.RunAsync(args, Scenarios.All);
