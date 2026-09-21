# cluster-redirect

Two server instances over one store, as a program that narrates each step and
as a test that asserts the same run.

| | |
|---|---|
| Category | `examples/` |
| Kind | tool |
| Solution | `Dracocephalum.Nightingale.Examples.ClusterRedirect.slnx` |
| Projects | `src/Dracocephalum.Nightingale.Examples.ClusterRedirect`, `test/Dracocephalum.Nightingale.Examples.ClusterRedirect.Tests` |

## What it does

Reserves a random database name on the local SQL Server, hosts two
Polecat-backed server instances in its own process, each on a loopback port of
its own and both over that one database, and drives them through two clients:
append two events and create a group through the second instance, which any
instance serves; connect a consumer to the first instance, which then runs the
group and writes its address on the group's lease; ask the second instance for
a second consumer and see the client sent to the first and refused there, as
the group's one consumer is taken; replay the parked message through the second
instance and see the client sent to the first, where the consumer receives it
at once. Then it disposes the consumer and drops the database. The test
project runs exactly the same code and asserts the report it returns.

## Run it

    dotnet build Dracocephalum.Nightingale.Examples.ClusterRedirect.slnx --nologo -v:q
    dotnet test  Dracocephalum.Nightingale.Examples.ClusterRedirect.slnx

    dotnet run --project src/Dracocephalum.Nightingale.Examples.ClusterRedirect

The program prints the seven steps and exits with a non-zero code if any fails.

## Configuration

One assumption: a SQL Server reachable with a connection string to its `master`
database, with permission to create and drop databases. The environment variable
`NIGHTINGALE_SQLSERVER` names it; when unset, the local default is used:

    Server=localhost;Database=master;Trusted_Connection=True;TrustServerCertificate=True;Command Timeout=300

Every run leaves the server as it found it: the database the first instance
creates is dropped at the end, whether the run passed or failed.

## Notes

Neither instance is configured with an address: each derives the one it
advertises from what it listens on, which here is a loopback port. An instance
behind address translation sets `Nightingale:Cluster:AdvertisedAddress`
instead. The client follows the address once, the way the reference client
follows a not-leader answer, and keeps the connection for the next time. The
sample references the shared example library as a project, which in turn
references the client and the Polecat components as projects, the recorded
workaround while no package feed exists; a real application would reference
the two packages.
