# Pending

Features that belong to the product but are deliberately not built yet, each
with the approach already decided so it can be picked up cold. An entry moves
to a ticket when work starts and is deleted when it ships. Items owed soon are
in [`TODO.md`](TODO.md); deliberate behavioural differences from the reference event store are
in [`VARIANCES.md`](VARIANCES.md).

## Authentication: what is left

Basic authentication is in: a user name and password on every call, the
built-in administrator from configuration, credentials in the gateway's
tables, roles, and the tenant header; see `VARIANCES.md`. What is left, in
order:

- **Managing credentials and tenants through the API.** A `Credentials`
  service: create, set a password, set the role and tenant, disable, delete,
  list, and change one's own password; and a `Tenants` service: create,
  rename, disable, enable, list. Admin only, a tenant-bound admin within its
  tenant. Until then the rows are written by hand, and the built-in
  administrator is the one credential.
- **The Bearer scheme.** External tokens first, validated against an identity
  provider's published keys with a claim mapping onto the same principal;
  tokens the gateway issues itself only if an application needs them without
  a provider, and then opaque and revocable, never a design of their own.
- **Rate limiting per peer** on refused credentials, beside the lockout per
  credential that is there.
- **A pepper that never leaves a key vault**, an implementation of the pepper
  step that asks the vault to HMAC, behind the same seam.
- **Stream ACLs**, with stream metadata below.

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
the tenant column. Database-per-tenant is not planned; what it would take is
under its own heading below.

The samples would use it. Each scenario under `examples/` creates and
initializes a database of its own, because several of them append to the same
stream names and read the same category. With tenants, one database
initialized once serves every scenario of a run, a tenant each: names, groups
and ordinals are per tenant, so the scenarios keep their names and their
dense numbering. Positions are the exception while the sequence is shared: a
scenario would assert them relative to where it started, or run on a
tenant-partitioned store.

## Database per tenant

Not planned, and recorded so that the cost is known if it is ever wanted.
The store supports it through a master table that maps each tenant to a
connection string, with tenants added, disabled and removed at runtime; each
tenant's database is then a single-tenant store of its own, migrated like
the one store is today. The store registers connection strings only: the
database behind one must already exist, which on a self-hosted server is a
`CREATE DATABASE` under a login that may, and on a managed cloud database is
a control-plane operation outside the gateway. There is no coherent position
across databases, so the wildcard tenant, cross-tenant ordinals and any
cross-tenant subscription are gone in this mode, and the server says so
through its features call.

What enabling it would mean, each a design of its own:

- **No default tenant**; a credential is bound to a registered tenant, and
  a tenant is registered with one or two connection strings, the read-only
  one the gateway's to keep beside the store's registry, resolved per tenant
  where today it is resolved once.
- **A tenant per composition root**: one database, one tail, one sequencer
  lease, one set of gateway tables and one registry of running groups per
  tenant, activated on first use and evicted when idle, so that startup,
  migration and the catalog comparison, seconds per fresh database, are not
  paid for every tenant on every instance at once.
- **A database that knows its tenant**: the tenant id stamped in the
  store's marker row at provisioning, and a registration refused when the
  database says another, since the registry row is otherwise the only link
  and removing it leaves the database behind.
- **Connection pools per tenant**, primary and replica, per instance, with a
  budget and eviction against the server's connection ceiling.
- **Registry changes seen by every instance**: a tenant disabled or
  re-pointed on one instance ends the consumers and stops the tail on the
  others, on a refresh cadence or a signal.
- **Placement of per-tenant leases**, so every tenant's sequencer does not
  land on the instance that woke first.
- **Operator tooling per database**: the schema report and apply, the
  collation rule and the initialization-fixed settings checked and reported
  per tenant; tenant partitioning refused as meaningless in this mode.
- **Management calls that name a tenant**, since groups are per database:
  a listing across tenants is a fan-out, and an admin credential sends the
  tenant header on management calls too.
- **Failure isolation**: one unreachable tenant database fails neither the
  instance's health nor the other tenants.
- **Credentials in the control plane**, since a caller is authenticated
  before its tenant is known; which is the model the authentication design
  takes from the start, so that it does not move later.

## Ordinals on a store initialized without them

Ordinal numbering of the virtual streams (`DESIGN.md`, seam 6) is a store
setting fixed at initialization, and a store initialized without it refuses
the setting. Adopting it later is a backfill: the migrate commadds the two
columns and their indexes, then the sequencer numbers from position 0, which
on a large store takes as long as one pass over the table and is done while
the store is served, because a batch is atomic and a read under ordinal
numbering sees only what is numbered. The setting's row is written once the columns exist, before the backfill
completes, so the server knows the store has the feature and readers see the
ordinals arrive.

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

## Moving a group's position

The reference lets an update change where a group starts, which resets it.
Here a group's start and its numbering are fixed when it is created, an
update changes its other settings only, and a consumer that wants another
position deletes the group and creates it again.
Moving a group in place, back to replay a stretch or forward to skip one, is
wanted and parked: it has to say what becomes of the parked messages and the
outbox on either side of the new position, of events in flight, and of a
checkpoint that would then move backwards, and whether it may happen while a
consumer is connected.

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

## Larger appends, at the host's choice

An append carries at most so many events (`VARIANCES.md`), and the default
stays that. Two things a host could be allowed to turn on, both only if the
store offers the means, since the gateway never writes the store's event
tables itself to get round it:

- **Paging on the server.** The append is still received whole, then written
  in as many commands as it needs inside its one transaction, so one atomic
  append can carry more than one command holds. It needs the store to write
  an append that way; that is an upstream request in `TODO.md`. Memory still
  grows with the append, and the transaction with it, so a limit in bytes
  would come with it.
- **Streaming.** Written as it arrives, so memory stays flat. The transaction
  is then open for as long as the client takes, and the head, live delivery
  and numbering wait behind it; only for a host that can vouch for the link
  between client and server, and with an answer for a client that stops
  half-way, which has to be given up on promptly for everyone else's sake.

## Parked messages as rows, and the outbox beside them

The reference parks a message, after its retries are spent, into a stream per
group and can replay only all of them or the first so many, because a log
has no random-access removal. Ours are rows, one per group and event, with
the reason, the attempt count and when it was parked, and a replay moves a
row to the group's outbox, the shape of a service bus's dead-letter queue:
what is on the outbox is delivered ahead of the stream, a delivery that
fails again moves the row back. Shipped: parking, replay of all, of one and
of all before a number, delivery at once to a consumer connected to the
serving instance, listing parked and outbox rows, and skipping parked ones.
Pending: deferred delivery, a retry
with a delay or a nack that says later, which is an outbox row with a due
time in the future and a timer in the group's loop, the outbox already
having the column. Group info carries the parked and outbox
counts.

## More reads from the read-only connection

Shipped (`DESIGN.md`, seam 8): pages of `$all` and of the virtual streams by
position are served from the read-only connection as far as its own
high-water mark reaches, exactly, and bounded reads of plain streams when the
host asks, eventually consistent. Pending:

- **Reads by ordinal.** The numbered rows a secondary holds are a prefix of
  the primary's, so a full page from it is the primary's page; what is
  missing is the cheap way to know a reader is at the head and skip the
  secondary, which the position reads get from the mark. The sequencer's
  progress row, replicated like the mark, is that signal.
- **Heads and counts.** A virtual stream's bounds and the fell-behind count
  are index seeks and stay on the main connection; each could move when its
  head is at or below the secondary's mark.
- **A subscription that follows the secondary.** Live delivery follows the
  primary's tail. A host with a secondary far stronger than its primary may
  prefer subscriptions that never touch the primary, at the price of the
  secondary's lag on every event: a tail over the secondary's mark, chosen
  per host.
- **A measurement.** None of it has run against an availability group. The
  gain to measure is a from-start replay's effect on append latency with the
  read-only connection on and off, and what a stronger secondary does for
  read throughput.

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
