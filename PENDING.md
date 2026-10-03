# Pending

Features that belong to the product but are deliberately not built yet, each
with the approach already decided so it can be picked up cold. An entry moves
to a ticket when work starts and is deleted when it ships. Items owed soon are
in [`TODO.md`](TODO.md); deliberate behavioural differences from the reference event store are
in [`VARIANCES.md`](VARIANCES.md).

## Authentication

Nothing yet, like the reference event store's insecure mode. Username and password per call,
carried in the standard `authorization` header with the Basic scheme, before
the first beta; Bearer later. The client already reads credentials from its
connection string and holds them unsent until then. A credential is bound to exactly one tenant or carries the admin
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
shared sequence and the wildcard. Ordinals are per tenant by construction,
the sequencer partitions by tenant and the ordinal indexes lead with the tenant
column, so they fit either partitioning; what tenant partitioning needs is a
sequencer that follows the mark and keeps its progress per tenant, which is why
`Nightingale:Store:AssignOrdinals` is refused with it until then. A wildcard
read spans tenants and so has no single ordinal sequence: it is served under
global numbering only, and ordinal numbering with the wildcard is refused.
Provisioning a tenant touches no schema: a new value in the tenant column, a
registry entry and credentials, all the gateway's own documents. The store
runs in conjoined mode
with the default tenant from day one, so enabling tenants later adds
credentials, not a schema migration; the category and type indexes lead with
the tenant column. Database-per-tenant is not planned: there is no coherent
cross-tenant position across databases.

## Ordinals on a store initialized without them

Ordinal numbering of the virtual streams (`DESIGN.md`, seam 6) is a store
setting fixed at initialization, and a store initialized without it refuses
the setting. Adopting it later is a backfill: the migrate command adds the two
columns and their indexes, then the sequencer numbers from position 0, which
on a large store takes as long as one pass over the table and is done while
the store is served, because a batch is atomic and a read under ordinal
numbering sees only what is numbered. The marker row is stamped with the
setting once the columns exist, before the backfill completes, so the server
knows the store has the feature and readers see the ordinals arrive.

## Server-computed lag counts

For consumers that alert on an absolute number of events behind rather than a
growing lag: a range count over the category or type index from the
subscriber's position to the head, reported on the fell-behind message or by a
small extra call. Under ordinal numbering the count is already a subtraction,
holes included; this entry is for global numbering.

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
accepts acknowledgements in any order. Group info and listing, and updating a
group's settings in place, are in this group too.

## A relay inside the cluster

A persistent-subscription group runs in exactly one instance at a time, the
one holding its lease, and the lease carries that instance's address: an
instance asked for a group another runs refuses with the address, and the
client goes there itself, as the reference client does. That needs clients
to reach each instance directly, which holds inside one network. A client
that can reach one load-balanced address only would need the instance it
reached to open the same call to the owner and relay both directions, one
hop, with a header that stops a second hop. It sits on the same lease-carried
address and adds nothing to the store; built only if such a client appears.

## Parked messages as rows, and the outbox beside them

The reference parks a message, after its retries are spent, into a stream per
group and can replay only all of them or the first so many, because a log
has no random-access removal. Ours are rows, one per group and event, with
the reason, the attempt count and when it was parked, and a replay moves a
row to the group's outbox, the shape of a service bus's dead-letter queue:
what is on the outbox is delivered ahead of the stream, a delivery that
fails again moves the row back. Shipped: parking, replay of all and of one,
delivery at once to a consumer connected to the serving instance. Pending:
listing parked and outbox rows and skipping one by deleting it, with group
info; replay of all before a number; and deferred delivery, a retry
with a delay or a nack that says later, which is an outbox row with a due
time in the future and a timer in the group's loop, the outbox already
having the column.

## Reads from a readable secondary

A switch, on by default, with `Nightingale:ReadOnlyConnectionStringName`
beside it. With the name set, that string is used for read-only connections
exactly as written; without it, the main string is used with
`ApplicationIntent=ReadOnly` set, which an availability-group listener with
read-only routing, or a managed database with read scale-out, sends to a
readable secondary, and which every other server ignores, so the default
costs a standalone server a second connection pool and nothing else. The
store has no read/write split of its own, so the split is the gateway's: its
raw readers take the read-only string, and what is read through the store's
sessions today needs a raw reader of its own first.

What may be read there is narrow, because a secondary applies the log after
the primary commits, under synchronous commit too: a range of `$all` or of a
virtual stream that lies wholly at or below the mark as the secondary has it,
the store's progression row read on the same connection. A read on the
secondary bounded by the primary's mark would skip, silently and inside a
subscription, the events the secondary has not applied yet. So catch-up over
history goes to the secondary and everything near the head stays on the
primary: live delivery, reads of a plain stream, since a client reads what it
has just appended, heads, every write, the leases and the sequencer.

The gain is isolation, not throughput. A from-start replay over a large
store pulls cold pages through the primary's memory and pushes out the hot
tail that appends and live subscribers depend on; on a secondary it does
neither. Consumers at the head read pages already in memory, and moving them
would gain nothing and cost freshness. None of it is measurable without an
availability group: built when one is at hand, with a replay's effect on
append latency measured both ways.

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
