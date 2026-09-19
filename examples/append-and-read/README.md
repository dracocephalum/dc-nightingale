# append-and-read

The whole round trip a client makes against a Nightingale server, as a program
that narrates each step and as a test that asserts the same run.

| | |
|---|---|
| Category | `examples/` |
| Kind | tool |
| Solution | `Dracocephalum.Nightingale.Examples.AppendAndRead.slnx` |
| Projects | `src/Dracocephalum.Nightingale.Examples.AppendAndRead`, `test/Dracocephalum.Nightingale.Examples.AppendAndRead.Tests` |

## What it does

Reserves a random database name on the local SQL Server, hosts the
Polecat-backed server in its own process on a loopback port, which creates and
initializes that database, and drives it through the client: append three
events to a new stream, read them forwards with the head and the metadata,
read the last two backwards, have a stale append refused with the stream's
actual revision, retry the first append with the same ids and see it succeed
without writing again, read a stream that never existed, then drop the
database. The test project runs exactly the same code and asserts the report
it returns.

## Run it

    dotnet build Dracocephalum.Nightingale.Examples.AppendAndRead.slnx --nologo -v:q
    dotnet test  Dracocephalum.Nightingale.Examples.AppendAndRead.slnx

    dotnet run --project src/Dracocephalum.Nightingale.Examples.AppendAndRead

The program prints the nine steps and exits with a non-zero code if any fails.

## Configuration

One assumption: a SQL Server reachable with a connection string to its `master`
database, with permission to create and drop databases. The environment variable
`NIGHTINGALE_SQLSERVER` names it; when unset, the local default is used:

    Server=localhost;Database=master;Trusted_Connection=True;TrustServerCertificate=True;Command Timeout=300

Every run leaves the server as it found it: the database the server creates
is dropped at the end, whether the run passed or failed.

## Notes

The sample references the client and the Polecat components as projects, the
recorded workaround while no package feed exists; a real application would
reference the two packages.
