# category-ordinals

The virtual streams under ordinal numbering against a Nightingale server, as a
program that narrates each step and as a test that asserts the same run.

## What it does

Creates a tenant of its own in the run's database, whose store was
initialized with `Nightingale:Store:AssignOrdinals` on and so has the ordinal
columns, starts a server over it with deletion allowed, and drives that
server through the client working in the tenant: append to three plain
streams in two categories, read `$ce-orders` by ordinal and see the three
orders events numbered 0, 1 and 2, the tenant's own numbers, at their sparse
positions, with bounds that are a count; read `$et-order_placed` by ordinal
and see it numbered on its own; subscribe to `$ce-orders` by ordinal from its
end and be confirmed at ordinal 2; delete `orders-1`, append to `orders-3`,
and see the subscription deliver ordinal 3, never one the deleted stream
held; then read the category again and see ordinals 2 and 3 with the holes
skipped and the bounds still counting from 0. Then it creates a
persistent-subscription group over the category under ordinal numbering,
from the start, acknowledges the two events it delivers as ordinals 2 and 3,
reconnects and is confirmed at checkpoint 3, an ordinal, because the
numbering is the group's for life.
The scenario's test runs exactly the same code and asserts the report it
returns.

## Run it

From the component's folder:

    dotnet run --project src/Dracocephalum.Nightingale.Examples.Polecat -- category-ordinals

Its test is `CategoryOrdinalsTests` in the component's test project.

## Notes

Ordinals are assigned after commit by one sequencer per cluster, behind the
store's high-water mark, so a read by ordinal made right after an append may
not see the event yet; the sample waits for the bounds to reach what it
expects, which is what a consumer that needs the number would do. Ordinal
numbering is the caller's explicit choice per read or subscription and is
refused on a store initialized without it.
