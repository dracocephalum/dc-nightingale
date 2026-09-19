# catch-up-subscription

Two catch-up subscriptions against a Nightingale server, as a program that
narrates each step and as a test that asserts the same run.

| | |
|---|---|
| Category | `examples/` |
| Kind | tool |
| Solution | `Dracocephalum.Nightingale.Examples.CatchUpSubscription.slnx` |
| Projects | `src/Dracocephalum.Nightingale.Examples.CatchUpSubscription`, `test/Dracocephalum.Nightingale.Examples.CatchUpSubscription.Tests` |

## What it does

Reserves a random database name on the local SQL Server, hosts the
Polecat-backed server in its own process on a loopback port, which creates and
initializes that database, and drives it through the client: append two
events to a stream, subscribe to that stream from its start and watch it catch
up and say so, subscribe to `$all` from its end, append to two streams while
both subscriptions are live, and see the stream subscription deliver its one
new event and the `$all` subscription deliver both in position order. Then it
disposes the subscriptions and drops the database. The test project runs
exactly the same code and asserts the report it returns.

## Run it

    dotnet build Dracocephalum.Nightingale.Examples.CatchUpSubscription.slnx --nologo -v:q
    dotnet test  Dracocephalum.Nightingale.Examples.CatchUpSubscription.slnx

    dotnet run --project src/Dracocephalum.Nightingale.Examples.CatchUpSubscription

The program prints the nine steps and exits with a non-zero code if any fails.

## Configuration

One assumption: a SQL Server reachable with a connection string to its `master`
database, with permission to create and drop databases. The environment variable
`NIGHTINGALE_SQLSERVER` names it; when unset, the local default is used:

    Server=localhost;Database=master;Trusted_Connection=True;TrustServerCertificate=True;Command Timeout=300

Every run leaves the server as it found it: the database the server creates
is dropped at the end, whether the run passed or failed.

## Notes

Live delivery waits on the server's tailer, which polls the store's high-water
mark on the store's own cadence, a quarter of a second while events flow, so a
live event arrives within that interval of its append. The sample references
the shared example library as a project, which in turn references the client
and the Polecat components as projects, the recorded workaround while no
package feed exists; a real application would reference the two packages.
