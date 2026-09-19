# delete-and-tombstone

Deletion against a Nightingale server, as a program that narrates each step
and as a test that asserts the same run.

| | |
|---|---|
| Category | `examples/` |
| Kind | tool |
| Solution | `Dracocephalum.Nightingale.Examples.DeleteAndTombstone.slnx` |
| Projects | `src/Dracocephalum.Nightingale.Examples.DeleteAndTombstone`, `test/Dracocephalum.Nightingale.Examples.DeleteAndTombstone.Tests` |

## What it does

Reserves a random database name on the local SQL Server, hosts the
Polecat-backed server in its own process on a loopback port with delete and
tombstone allowed, which the default host does not, and drives it through the
client: append to two streams, have a stale delete refused with the actual
revision, delete one stream and see its read and its append fail and the
category stream shrink, tombstone it and see the name read as never used,
append to the freed name as a new stream, then start a second server with the
default settings and see it refuse to delete at all. Then it drops the
database. The test project runs exactly the same code and asserts the report
it returns.

## Run it

    dotnet build Dracocephalum.Nightingale.Examples.DeleteAndTombstone.slnx --nologo -v:q
    dotnet test  Dracocephalum.Nightingale.Examples.DeleteAndTombstone.slnx

    dotnet run --project src/Dracocephalum.Nightingale.Examples.DeleteAndTombstone

The program prints the nine steps and exits with a non-zero code if any fails.

## Configuration

One assumption: a SQL Server reachable with a connection string to its `master`
database, with permission to create and drop databases. The environment variable
`NIGHTINGALE_SQLSERVER` names it; when unset, the local default is used:

    Server=localhost;Database=master;Trusted_Connection=True;TrustServerCertificate=True;Command Timeout=300

Every run leaves the server as it found it: the database the server creates
is dropped at the end, whether the run passed or failed.

## Notes

Deletion is a server-side decision: the host enables delete and tombstone
separately under `Nightingale:Deletion`, and both are off unless it does. The
sample's own server turns them on to show what they do; its second server
shows the default. The sample references the client and the Polecat components
as projects, the recorded workaround while no package feed exists; a real
application would reference the two packages.
