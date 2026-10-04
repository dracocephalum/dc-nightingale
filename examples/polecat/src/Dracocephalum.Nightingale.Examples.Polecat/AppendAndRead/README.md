# append-and-read

The whole round trip a client makes against a Nightingale server, as a program
that narrates each step and as a test that asserts the same run.

## What it does

Reserves a random database name on the local SQL Server, hosts the
Polecat-backed server in its own process on a loopback port, which creates and
initializes that database, and drives it through the client: append three
events to a new stream, read them forwards with the head and the metadata,
read the last two backwards, have a stale append refused with the stream's
actual revision, retry the first append with the same ids and see it succeed
without writing again, read a stream that never existed, then drop the
database.
The scenario's test runs exactly the same code and asserts the report it
returns.

## Run it

From the component's folder:

    dotnet run --project src/Dracocephalum.Nightingale.Examples.Polecat -- append-and-read

Its test is `AppendAndReadTests` in the component's test project.
