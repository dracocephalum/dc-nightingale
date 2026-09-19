# Pending

Features that belong to the product but are deliberately not built yet, each
with the approach already decided so it can be picked up cold. An entry moves
to a ticket when work starts and is deleted when it ships. Items owed soon are
in [`TODO.md`](TODO.md); deliberate behavioural differences from the reference event store are
in [`VARIANCES.md`](VARIANCES.md).

## Authentication

Nothing yet, like the reference event store's insecure mode. Username and password per call,
carried in the standard `authorization` header with the Basic scheme, before
the first beta; Bearer later. A credential is bound to exactly one tenant or carries the admin
role; admin is a role, not a tenant. Management operations — create and list
tenants, manage credentials, administer persistent-subscription groups —
form a fourth service area and take no tenant. Stream ACLs after that,
together with stream metadata below.

## Stream metadata

`$maxAge`, `$maxCount` and `$tb` (truncate before) stored as one document per
stream and enforced at read time: a read filters versions below the truncate
point, outside the age window, or beyond the last N. Physical removal of the
filtered events is a separate maintenance job, the analogue of a scavenge.
`$acl` arrives with authentication.

## Multi-stream and conditional appends

The reference protocol's second version has two: an append session, several streams written
atomically each with its own expected revision, and an append with consistency
checks on streams that are not written. Both map onto the store directly: one
session with several appends is already one transaction, and a stream-state
check is the store's version assertion without an append. The store's own
consistency model is richer still, tag-based conditions of the form "no event
matching this query since sequence N", and can be exposed once the single-stream
contract is stable.

## Multi-tenancy

Tenants share one database and one global sequence, so positions stay coherent
across tenants. A tenant-bound credential needs no tenant header: every read
and append happens within its tenant, `$all` and the virtual streams
included, and a persistent-subscription group is identified by tenant, stream
and group name; a header sent anyway must equal the credential's tenant. An
admin credential must send the tenant header on every data operation, so
nothing lands in a tenant or spans tenants by omission; the wildcard value
spans all tenants and is accepted on reads only. The wildcard exists because
the sequence is shared, so it is only offered while it is: with the store's
per-tenant partitioning (`Nightingale:Store:Partitioning` = `Tenant`, fixed when the
store is initialized and guarded by its marker row) or a database per tenant
there is no global position,
the server says so through its features call, and the wildcard is refused.
Range partitioning by sequence, the gateway's own if ever added, keeps the
shared sequence and the wildcard. Provisioning a tenant touches no schema: a new value in the tenant
column, a registry entry and credentials, all the gateway's own documents. The store runs in conjoined mode
with the default tenant from day one, so enabling tenants later adds
credentials, not a schema migration; the category and type indexes lead with
the tenant column. Database-per-tenant is not planned: there is no coherent
cross-tenant position across databases.

## Dense positions for category and event-type streams

The reference model: an internal projection walks `$all` in order and writes
one link row per event into a table per virtual stream kind, `(key, position,
sequence)`, with `position` the running count per key. Catch-up and persistent
subscriptions read whichever feed the stream name resolves to: the event table
by stream and version, a link table by position, or the event table by
sequence for `$all`. Costs: a second write per event, and a category's head
then lags the linker, so the linker needs its own lag alert. Built only if a
consumer needs dense numbering; the sparse global sequence covers lag
monitoring as long as the head is reported per stream.

## Server-computed lag counts

For consumers that alert on an absolute number of events behind rather than a
growing lag: a range count over the category or type index from the
subscriber's position to the head, reported on the fell-behind message or by a
small extra call.

## Resubscription after a dropped connection

A subscription ends with an exception when its connection drops, and the
consumer subscribes again from the position after the last event it handled;
that is what the reference client leaves to the application too. A client
option that resubscribes on its own, with backoff and from the last delivered
position, so a consumer sees one unbroken enumeration across reconnects.

## Competing consumers

Persistent subscriptions ship with dispatch to a single consumer and a
subscriber limit of one. Round-robin and pinned strategies add one dispatcher
per group fanning out to several connections; the in-flight tracker already
accepts acknowledgements in any order. Also in this group: replay of parked
messages, group info and listing, and group ownership across several gateway
instances by an application lock, with a cross-instance wake-up or accepted
polling latency.

## Reads from a readable secondary

From-start replays over a large store can run against a readable secondary of
an availability group and stay consistent as long as both the events and the
high-water mark are read from the same secondary. Two connection strings in the
gateway; the store has no read/write split of its own.

## Binary event bodies

`application/octet-stream` through the store's binary event path, which needs
a serializer per event type on the server. Deferred until a consumer needs it.

## Regular-expression filters in the database

If post-filtering pages by regular expression proves too slow, SQL Server 2025's
regular-expression functions can move the filter into the query.

## Marten backend

The same gateway over Marten on PostgreSQL. The event wrapper, the feed
abstraction and the tailer are backend-neutral by design; the schema patch and
the wake-up (PostgreSQL has notifications) are the backend-specific parts.
