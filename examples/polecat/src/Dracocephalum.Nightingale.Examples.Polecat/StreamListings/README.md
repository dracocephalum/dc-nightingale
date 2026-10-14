# stream-listings

The virtual streams over a workflow and over the streams themselves, as a
program that narrates each step and as a test that asserts the same run.

## What it does

Creates a tenant of its own in the run's database and drives the run's server
through the client working in the tenant: append to `orders-1`, `payments-1`
and a stream named just `orders`, three of the events carrying the
correlation id `checkout-1`; read `$bc-checkout-1` and see the workflow
across the streams it touched; read `$streams` and `$category-orders`, one
event a stream, its first; read `$ce-orders` and see the stream named just
`orders` in it; read `$all` with the category filter `orders`, the prefix
filter `orders-` and the type filter `order_placed`, and see what each keeps;
then subscribe to `$streams` from the end and see a new stream handed over,
and a stream that only moved on not.
The scenario's test runs exactly the same code and asserts the report it
returns.

## Run it

From the component's folder:

    dotnet run --project src/Dracocephalum.Nightingale.Examples.Polecat -- stream-listings

Its test is `StreamListingsTests` in the component's test project.

## Notes

A stream's category is its name up to the first hyphen, when one follows at
least one character, and the whole name otherwise, so the stream named just
`orders` is in category `orders`. The category filter keeps it, as
`$ce-orders` does; the prefix filter `orders-` does not, because its name
does not begin with the prefix. `$bc-`, `$streams` and `$category-` are read
by position, each through a filtered index that holds only the rows it
reads: the events that carry a correlation id, and the first event of each
stream.
