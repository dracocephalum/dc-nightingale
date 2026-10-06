# competing-consumers

Two consumers on one persistent-subscription group against a Nightingale
server, as a program that narrates each step and as a test that asserts the
same run.

## What it does

Reserves a random database name on the local SQL Server, hosts the
Polecat-backed server in its own process on a loopback port, which creates and
initializes that database, and drives it through the client: create a group
over the `$ce-orders` category from its start, pinned, connect two consumers,
append two events to each of four streams and see every stream's events
arrive at one consumer, ask the server about the group and see both consumers
with what each holds, change the group to round robin and see both consumers'
calls end with `GroupUpdatedException`, connect a slow consumer and a quick one
holding one event each, append three events to one stream and see the quick
one take what the slow one cannot, have the slow one leave while it holds an
event and see the quick one receive that event at once with a retry count of
zero, and delete the group. Then it drops the database.
The scenario's test runs exactly the same code and asserts the report it
returns.

## Run it

From the component's folder:

    dotnet run --project src/Dracocephalum.Nightingale.Examples.Polecat -- competing-consumers

Its test is `CompetingConsumersTests` in the component's test project.

## Notes

Which consumer a pinned stream goes to is a hash of the stream name over the
consumers connected, so the split between the two is whatever the four names
hash to; what holds is that no stream is with both. A consumer that leaves
cleanly, which disposing the subscription does, tells the server, which hands
what the consumer held to the others at once rather than after the message
timeout, and without counting it against the event: a retry count says how
often a consumer refused or timed out on the event, not how often it changed
hands.
