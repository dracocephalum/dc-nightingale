# category-ordinals

The virtual streams under ordinal numbering against a Nightingale server, as a
program that narrates each step and as a test that asserts the same run.

| | |
|---|---|
| Category | `examples/` |
| Kind | tool |
| Solution | `Dracocephalum.Nightingale.Examples.CategoryOrdinals.slnx` |
| Projects | `src/Dracocephalum.Nightingale.Examples.CategoryOrdinals`, `test/Dracocephalum.Nightingale.Examples.CategoryOrdinals.Tests` |

## What it does

Reserves a random database name on the local SQL Server, hosts the
Polecat-backed server in its own process on a loopback port with
`Nightingale:Store:AssignOrdinals` on, which creates and initializes that database
with the ordinal columns, and drives it through the client: append to three
plain streams in two categories, read `$ce-orders` by ordinal and see the three
orders events numbered 0, 1 and 2 at their sparse global positions, with bounds
that are a count; read `$et-order_placed` by ordinal and see it numbered on its
own; subscribe to `$ce-orders` by ordinal from its end and be confirmed at
ordinal 2; delete `orders-1`, append to `orders-3`, and see the subscription
deliver ordinal 3, never one the deleted stream held; then read the category
again and see ordinals 2 and 3 with the holes skipped and the bounds still
counting from 0. Then it creates a persistent-subscription group over the
category under ordinal numbering, from the start, acknowledges the two events
it delivers as ordinals 2 and 3, reconnects and is confirmed at checkpoint 3,
an ordinal, because the numbering is the group's for life. Then it drops the
database. The test project runs exactly the same code and asserts the report
it returns.

## Run it

    dotnet build Dracocephalum.Nightingale.Examples.CategoryOrdinals.slnx --nologo -v:q
    dotnet test  Dracocephalum.Nightingale.Examples.CategoryOrdinals.slnx

    dotnet run --project src/Dracocephalum.Nightingale.Examples.CategoryOrdinals

The program prints the twelve steps and exits with a non-zero code if any fails.

## Configuration

One assumption: a SQL Server reachable with a connection string to its `master`
database, with permission to create and drop databases. The environment variable
`NIGHTINGALE_SQLSERVER` names it; when unset, the local default is used:

    Server=localhost;Database=master;Trusted_Connection=True;TrustServerCertificate=True;Command Timeout=300

Every run leaves the server as it found it: the database the server creates
is dropped at the end, whether the run passed or failed.

## Notes

Ordinals are assigned after commit by one sequencer per cluster, behind the
store's high-water mark, so a read by ordinal made right after an append may
not see the event yet; the sample waits for the bounds to reach what it
expects, which is what a consumer that needs the number would do. Ordinal
numbering is the caller's explicit choice per read or subscription and is
refused on a store initialized without it. The sample references the shared
example library as a project, which in turn references the client and the
Polecat components as projects, the recorded workaround while no package feed
exists; a real application would reference the two packages.
