# persistent-subscription

A persistent-subscription group against a Nightingale server, as a program
that narrates each step and as a test that asserts the same run.

## What it does

Reserves a random database name on the local SQL Server, hosts the
Polecat-backed server in its own process on a loopback port, which creates and
initializes that database, and drives it through the client: append three
events to a stream, create a group over it from its start with one retry
before parking, consume as the group's one consumer, acknowledge two events,
refuse the payment once and see it come back with its retry count, refuse it
again and see it parked, leave, come back to a checkpoint that covers all
three revisions, ask the server about the group and see where it runs, its
checkpoint against the stream's last revision and the one parked message,
replay that one parked message by itself while connected and
receive it at once, acknowledge it, and delete the group. Then it drops the
database.
The scenario's test runs exactly the same code and asserts the report it
returns.

## Run it

From the component's folder:

    dotnet run --project src/Dracocephalum.Nightingale.Examples.Polecat -- persistent-subscription

Its test is `PersistentSubscriptionTests` in the component's test project.

## Notes

A parked event counts as done for the group's checkpoint, which is why the
second consumer is confirmed at revision 2 although revision 1 was never
acknowledged; the parked row keeps it, and replaying it by position, which
moves it to the group's outbox and wakes the connected consumer, is what the
reference cannot do.
