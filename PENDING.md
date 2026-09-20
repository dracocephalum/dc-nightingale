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

## Group ownership across instances, by lease and forwarding

A persistent-subscription group runs in exactly one instance at a time,
because its in-flight messages, retry counts and dispatch order are in
memory. Ownership is a lease row per group, taken by the instance that first
serves the group and renewed on a heartbeat; an expired lease is taken over,
and the previous owner's in-flight messages are redelivered from the
checkpoint. No leader, no gossip: the database is the only coordination
point, and the client never learns the topology. Today an instance that does
not own a group refuses the call with the owner named; the pending step is
in-cluster forwarding, where that instance opens the same call to the owner
and relays both directions, so any address serves any group through a dumb
load balancer, at the cost of one hop. An instance registry table, id,
address and last heartbeat, is what forwarding resolves the owner through.

## Parked messages as rows, replayable one at a time

The reference parks a message, after its retries are spent, into a stream per
group and can replay only all of them or the first so many, because a log
has no random-access removal. Ours are rows, one per group and event, with
the reason, the attempt count and when it was parked: replay one by key,
skip one by deleting it, replay all or all before a position, and list them.
The first slice ships parking and replay of all and of one; listing and
replay before a position follow with group info.

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
