# category-stream

The virtual streams against a Nightingale server, as a program that narrates
each step and as a test that asserts the same run.

## What it does

Creates a tenant of its own in the run's database and drives the run's
server, hosted in the program's own process on a loopback port, through the
client working in that tenant: append to three plain streams in two
categories, read `$ce-orders` and see the events of both `orders-*` streams in
position order with the category's own bounds, read `$et-order_placed` and
see one event from each stream that has one, subscribe to `$ce-orders` from
its end, append to a shipments stream and then to a new orders stream, and
see the subscription deliver the orders event and nothing else. Then it
disposes the subscription.
The scenario's test runs exactly the same code and asserts the report it
returns.

## Run it

From the component's folder:

    dotnet run --project src/Dracocephalum.Nightingale.Examples.Polecat -- category-stream

Its test is `CategoryStreamTests` in the component's test project.

## Notes

A virtual stream's positions are the store's global positions, sparse, and its
head is its own last event: the category read reports bounds 1 to 4 while the
store's head is also 4 only by coincidence of order, and after the shipments
append the subscription's head stays where the category's last event is.
