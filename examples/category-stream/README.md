# category-stream

The virtual streams against a Nightingale server, as a program that narrates
each step and as a test that asserts the same run.

| | |
|---|---|
| Category | `examples/` |
| Kind | tool |
| Solution | `Dracocephalum.Nightingale.Examples.CategoryStream.slnx` |
| Projects | `src/Dracocephalum.Nightingale.Examples.CategoryStream`, `test/Dracocephalum.Nightingale.Examples.CategoryStream.Tests` |

## What it does

Reserves a random database name on the local SQL Server, hosts the
Polecat-backed server in its own process on a loopback port, which creates and
initializes that database, and drives it through the client: append to three
plain streams in two categories, read `$ce-orders` and see the events of both
`orders-*` streams in position order with the category's own bounds, read
`$et-order_placed` and see one event from each stream that has one, subscribe
to `$ce-orders` from its end, append to a shipments stream and then to a new
orders stream, and see the subscription deliver the orders event and nothing
else. Then it disposes the subscription and drops the database. The test
project runs exactly the same code and asserts the report it returns.

## Run it

    dotnet build Dracocephalum.Nightingale.Examples.CategoryStream.slnx --nologo -v:q
    dotnet test  Dracocephalum.Nightingale.Examples.CategoryStream.slnx

    dotnet run --project src/Dracocephalum.Nightingale.Examples.CategoryStream

The program prints the nine steps and exits with a non-zero code if any fails.

## Configuration

One assumption: a SQL Server reachable with a connection string to its `master`
database, with permission to create and drop databases. The environment variable
`NIGHTINGALE_SQLSERVER` names it; when unset, the local default is used:

    Server=localhost;Database=master;Trusted_Connection=True;TrustServerCertificate=True;Command Timeout=300

Every run leaves the server as it found it: the database the server creates
is dropped at the end, whether the run passed or failed.

## Notes

A virtual stream's positions are the store's global positions, sparse, and its
head is its own last event: the category read reports bounds 1 to 4 while the
store's head is also 4 only by coincidence of order, and after the shipments
append the subscription's head stays where the category's last event is. The
sample references the client and the Polecat components as projects, the
recorded workaround while no package feed exists; a real application would
reference the two packages.
