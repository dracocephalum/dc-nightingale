# catch-up-subscription

Two catch-up subscriptions against a Nightingale server, as a program that
narrates each step and as a test that asserts the same run.

## What it does

Reserves a random database name on the local SQL Server, hosts the
Polecat-backed server in its own process on a loopback port, which creates and
initializes that database, and drives it through the client: append two
events to a stream, subscribe to that stream from its start and watch it catch
up and say so, subscribe to `$all` from its end, append to two streams while
both subscriptions are live, and see the stream subscription deliver its one
new event and the `$all` subscription deliver both in position order. Then it
disposes the subscriptions and drops the database.
The scenario's test runs exactly the same code and asserts the report it
returns.

## Run it

From the component's folder:

    dotnet run --project src/Dracocephalum.Nightingale.Examples.Polecat -- catch-up-subscription

Its test is `CatchUpSubscriptionTests` in the component's test project.

## Notes

Live delivery waits on the server's tailer, which polls the store's high-water
mark on the store's own cadence, a quarter of a second while events flow, so a
live event arrives within that interval of its append.
