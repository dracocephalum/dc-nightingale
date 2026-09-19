# common

What every sample shares, so a sample is only its scenario. Not a sample
itself.

| | |
|---|---|
| Category | `examples/` |
| Kind | library |
| Solution | `Dracocephalum.Nightingale.Examples.Common.slnx` |
| Projects | `src/Dracocephalum.Nightingale.Examples.Common`, `test/Dracocephalum.Nightingale.Examples.Common.Tests` |

## What it does

Four things every sample program needs: the environment's `master`
connection string, from `NIGHTINGALE_SQLSERVER` or the local default; a
throwaway database name the server creates and the sample drops; the
Polecat-backed server hosted in the sample's own process on a loopback port,
with a client connected to it that bypasses any HTTP proxy the machine names;
and the program shell that runs a scenario, narrates it, prints one closing
line and turns success or failure into an exit code. A sample's `Scenario`
uses the first three and its `Program.cs` is one line over the fourth.

## Run it

    dotnet build Dracocephalum.Nightingale.Examples.Common.slnx --nologo -v:q
    dotnet test  Dracocephalum.Nightingale.Examples.Common.slnx

A library; nothing to run. The tests need no SQL Server: the program shell is
tested with a scenario delegate, and reserving a database name creates
nothing.

## Configuration

None of its own. The environment variable `NIGHTINGALE_SQLSERVER` names the
`master` connection string every sample uses; unset, the local default
applies.

## Notes

References the client and the Polecat components as projects, the recorded
workaround while no package feed exists; the samples reference this library
the same way. A sample that needs a server setting the default host does not
have adjusts the options when it starts one, as the deletion sample does.
