# persistent-subscription

A persistent-subscription group against a Nightingale server, as a program
that narrates each step and as a test that asserts the same run.

| | |
|---|---|
| Category | `examples/` |
| Kind | tool |
| Solution | `Dracocephalum.Nightingale.Examples.PersistentSubscription.slnx` |
| Projects | `src/Dracocephalum.Nightingale.Examples.PersistentSubscription`, `test/Dracocephalum.Nightingale.Examples.PersistentSubscription.Tests` |

## What it does

Reserves a random database name on the local SQL Server, hosts the
Polecat-backed server in its own process on a loopback port, which creates and
initializes that database, and drives it through the client: append three
events to a stream, create a group over it from its start with one retry
before parking, consume as the group's one consumer, acknowledge two events,
refuse the payment once and see it come back with its retry count, refuse it
again and see it parked, leave, replay that one parked message by itself, come
back to a checkpoint that covers all three revisions and receive the replayed
event first, acknowledge it, and delete the group. Then it drops the database.
The test project runs exactly the same code and asserts the report it returns.

## Run it

    dotnet build Dracocephalum.Nightingale.Examples.PersistentSubscription.slnx --nologo -v:q
    dotnet test  Dracocephalum.Nightingale.Examples.PersistentSubscription.slnx

    dotnet run --project src/Dracocephalum.Nightingale.Examples.PersistentSubscription

The program prints the nine steps and exits with a non-zero code if any fails.

## Configuration

One assumption: a SQL Server reachable with a connection string to its `master`
database, with permission to create and drop databases. The environment variable
`NIGHTINGALE_SQLSERVER` names it; when unset, the local default is used:

    Server=localhost;Database=master;Trusted_Connection=True;TrustServerCertificate=True;Command Timeout=300

Every run leaves the server as it found it: the database the server creates
is dropped at the end, whether the run passed or failed.

## Notes

A parked event counts as done for the group's checkpoint, which is why the
second consumer is confirmed at revision 2 although revision 1 was never
acknowledged; the parked row keeps it, and replaying it by position is what
the reference cannot do. The sample references the shared example library as
a project, which in turn references the client and the Polecat components as
projects, the recorded workaround while no package feed exists; a real
application would reference the two packages.
