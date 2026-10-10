# polecat

The scenarios of the Polecat-backed server, in one program: each runs end to
end against a local SQL Server and narrates its steps, and each has a test
that runs the same code and asserts what it reports.

| | |
|---|---|
| Category | `examples/` |
| Kind | tool |
| Solution | `Dracocephalum.Nightingale.Examples.Polecat.slnx` |
| Projects | `src/Dracocephalum.Nightingale.Examples.Polecat`, `test/Dracocephalum.Nightingale.Examples.Polecat.Tests` |

## What it does

Every scenario reserves a random database name on the local SQL Server, hosts
the server in the program's own process on a loopback port, which creates and
initializes that database, drives it through the client, and drops the
database on the way out. A scenario is a folder under the source project with
its code and a README that walks through it.

| Scenario | Shows |
|---|---|
| [`append-and-read`](src/Dracocephalum.Nightingale.Examples.Polecat/AppendAndRead/README.md) | the client round trip: append, read both ways, conflict, idempotent retry |
| [`catch-up-subscription`](src/Dracocephalum.Nightingale.Examples.Polecat/CatchUpSubscription/README.md) | two catch-up subscriptions, to a stream from its start and to `$all` from its end: catch up, caught-up note, live delivery in order |
| [`category-stream`](src/Dracocephalum.Nightingale.Examples.Polecat/CategoryStream/README.md) | the virtual streams: read `$ce-orders` and `$et-order_placed` across plain streams, subscribe to the category and see only its events |
| [`category-ordinals`](src/Dracocephalum.Nightingale.Examples.Polecat/CategoryOrdinals/README.md) | the virtual streams by ordinal, on a store initialized with ordinals: dense numbers whose bounds are a count, a subscription confirmed at an ordinal, and the holes a delete leaves |
| [`cluster-redirect`](src/Dracocephalum.Nightingale.Examples.Polecat/ClusterRedirect/README.md) | two instances over one store: a group runs where its consumer connected, the other instance refuses with that address, and the client goes there |
| [`competing-consumers`](src/Dracocephalum.Nightingale.Examples.Polecat/CompetingConsumers/README.md) | two consumers on one group: a pinned group shares its streams out between them, the owner describes both, an update ends both calls, and a consumer that leaves while holding an event has it handed to the other at once, not counted as a retry |
| [`credentials-and-tenants`](src/Dracocephalum.Nightingale.Examples.Polecat/CredentialsAndTenants/README.md) | credentials and tenants: an ops credential and a user bound to the default tenant, what each may do, a password changed by its owner, a second tenant with a credential kept to it, and the tenant disabled |
| [`delete-and-tombstone`](src/Dracocephalum.Nightingale.Examples.Polecat/DeleteAndTombstone/README.md) | delete hides a stream, tombstone removes it and frees the name, a stale revision is refused, and a server with the default settings refuses to delete at all |
| [`persistent-subscription`](src/Dracocephalum.Nightingale.Examples.Polecat/PersistentSubscription/README.md) | a persistent-subscription group: create, consume with acknowledgements, retry and park, replay the parked message, resume from the checkpoint, delete |

What the scenarios share is in the `Common` folder: the environment's `master`
connection string, the throwaway database, the in-process server with a client
opened from its connection string, and the program around the scenarios.

## Run it

    dotnet build Dracocephalum.Nightingale.Examples.Polecat.slnx --nologo -v:q
    dotnet test  Dracocephalum.Nightingale.Examples.Polecat.slnx

    dotnet run --project src/Dracocephalum.Nightingale.Examples.Polecat
    dotnet run --project src/Dracocephalum.Nightingale.Examples.Polecat -- append-and-read
    dotnet run --project src/Dracocephalum.Nightingale.Examples.Polecat -- all

Without an argument the program lists the scenarios. With names it runs those
in the order given, and with `all` every one, going on after a failure. The
exit code is 0 when every scenario asked for completed, 1 when one failed and
2 when a name is not a scenario's.

The tests run the scenarios one at a time, a test per scenario; the tests of
the program itself need no SQL Server.

## Configuration

One assumption: a SQL Server reachable with a connection string to its `master`
database, with permission to create and drop databases. The environment variable
`NIGHTINGALE_SQLSERVER` names it; when unset, the local default is used:

    Server=localhost;Database=master;Trusted_Connection=True;TrustServerCertificate=True;Command Timeout=300

Every run leaves the server as it found it: the database a scenario's server
creates is dropped at the end, whether the scenario passed or failed.

## Notes

To add a scenario: a folder with a `Scenario` and a `README.md`, a line in
`Scenarios.cs`, a test class, and a row in the table above. A scenario that
needs a server setting the default host does not have adjusts the options
when it starts its server, as the deletion scenario does.

References the client and the Polecat components as projects, the recorded
workaround while no package feed exists; a real application would reference
the two packages.
