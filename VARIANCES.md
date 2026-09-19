# Variances

Where Nightingale behaves differently from the reference event store on
purpose. The client API keeps the reference client's shape; these are the places where the store underneath
(Polecat on SQL Server) makes the same call mean something slightly different.
Each entry says what the reference does, what Nightingale does, and why. A variance
is a decision, not a defect: remove an entry only when the behaviour is made
identical, and add one whenever a deliberate difference is introduced.

Deferred features that will close a gap later are in [`PENDING.md`](PENDING.md).

## Positions of virtual streams are the global sequence

**Reference:** `$ce-orders` and `$et-OrderCreated` are link streams built by
system projections, with dense revisions of their own (0, 1, 2, ...).

**Nightingale:** `$ce-`, `$et-` and `$all` are predicates over the event table.
Their positions are the store's global sequence number: monotonic within the
stream but sparse, because other streams' events occupy the numbers between.
The head of each virtual stream is its own last event, never the global head,
so a quiet category reports a stable head and zero lag.

**Why:** no link events, no projection lag, no extra writes. A consumer that
alerts on an absolute count of events behind, rather than on a growing lag,
needs a server-computed count; see `PENDING.md`.

## A position is one number

**Reference:** a position in `$all` is a pair, commit and prepare.

**Nightingale:** a single signed 64-bit number, the store's global sequence.
The client type keeps the name `Position` but holds one value. The head of
`$all` is the high-water mark, the position up to which every event is
committed; 0 means no events yet.

**Why:** the store has one sequence; a pair would carry the same number twice.

## Subscriptions report the head and a count when behind

**Reference:** caught-up and fell-behind notifications carry a timestamp and
a position.

**Nightingale:** the subscription confirmation carries the head at that
moment, caught-up carries the head, and fell-behind carries the head and the
number of events between the client and it, computed by the server.

**Why:** positions of virtual streams are sparse, so a difference of positions
is not a count; consumers that alert on a count need the server to supply it.

## Metadata is a JSON object, understood by the server

**Reference:** custom metadata is an opaque byte array the server never reads;
clients JSON-serialise a dictionary into it. Its newer protocol makes it a
typed map of values instead.

**Nightingale:** metadata travels as bytes exactly like the body, and must be
a JSON object. It comes back as sent, insignificant whitespace aside, with one
exception: the server understands `$correlationId` and `$causationId`, stores
them in their own columns, and returns them last in the object. Other
`$`-prefixed keys are reserved. Event ids follow the reference rule exactly:
not enforced unique, compared from the expected revision on a retry, no
guarantee with `ANY`. That part is not a variance.

**Why:** bytes keep the client's exact text, which a typed map of doubles
cannot, and the store keeps headers as a JSON object already.

## Subscription start positions are inclusive

**Reference:** a subscription starts after a position: `FromStream.After(x)`
and `FromAll.After(p)` are exclusive, `Start` and `End` are the two ends.

**Nightingale:** a subscription starts at a position, inclusive, exactly as a
read does; `End` means after now. A client that wants everything after `x`
subscribes from `x + 1`.

**Why:** one rule for reads and subscriptions; the client type is the same
`StreamPosition` for both.

## The client hands back events, not resolved links

**Reference:** a read yields a `ResolvedEvent` wrapping the event and, when
the stream is a link stream, the link it was reached through.

**Nightingale:** a read yields the event record itself, with its stream,
revision, position, type, creation time, body and metadata, and the read
result exposes the stream's state and head before any event. There are no
link events to resolve.

**Why:** virtual streams are predicates, so an event is only ever itself.

## A tombstoned stream reads as one that never existed

**Reference:** a read of a tombstoned stream fails with a stream-deleted
error, and the name can never be used again.

**Nightingale:** tombstone removes the stream and its events permanently, and
afterwards the name reads as not found and may be appended to again.

**Why:** the store's hard delete leaves no marker behind.

## Forward reads of `$all` stop at the high-water mark

**Reference:** a read of `$all` sees an event the moment its append returns.

**Nightingale:** sequence numbers are assigned before a transaction commits, so
a lower number can become visible after a higher one. Every forward read and
subscription over `$all` and the virtual streams is therefore bounded by the
store's high-water mark: the position up to which every number is committed.
A bounded read, and a subscription from the end, bring the mark up to date
first, so an append that has returned is in what they see, with one exception:
while a lower number's transaction is still open, the mark stops before it and
every higher event waits, until that transaction commits or the poller gives
the gap up as abandoned after the store's threshold. A subscription's live
phase follows the poller, at most its interval behind an append. Per-stream
reads are unaffected: versions are dense under the stream lock.

**Why:** reading past the high-water mark can skip an event forever.

## Stream name case sensitivity follows the database collation

**Reference:** stream names are case-sensitive; `Orders-1` and `orders-1` are
different streams.

**Nightingale:** the stream identifier column uses the database's collation.
SQL Server's default collations are case-insensitive, so those two names
resolve to the same stream. A binary collation, chosen through
`Nightingale:Store:Collation` when the server creates the database, makes them
different streams as the reference does. A caller that uses one consistent
spelling is unaffected either way.

**Why:** it is the database's behaviour, and a collation is fixed when the
database is created, so it is an initialization choice rather than an API one.

## `$all` holds only application events

**Reference:** `$all` includes system events (`$`-prefixed), and a client
filters them out with `ExcludeSystemEvents`.

**Nightingale:** there are no system events. The filter is accepted and does
nothing. Events of archived (soft-deleted) streams are excluded from `$all`
and from the virtual streams. `resolveLinkTos` is accepted and ignored because
no link events exist.

**Why:** the store has no system streams, and hiding archived events is its
convention on every read.

## Deleted streams

**Reference:** a soft-deleted stream can be recreated by appending to it, its
events remain in `$all` until scavenged, and a tombstoned stream is permanent.

**Nightingale:** delete archives the stream: its events disappear from every
read, and an append to it fails with a stream-deleted error rather than
recreating it. Tombstone removes the stream and its events permanently. The
gateway exposes no un-archive.

**Why:** archive and tombstone are the store's two primitives; recreating a
soft-deleted stream with continuing revision numbers has no counterpart.

## Stream metadata is not enforced

**Reference:** `$maxAge`, `$maxCount`, `$tb` (truncate before) and `$acl` on
a stream's metadata change what a read returns and who may read it.

**Nightingale:** stream metadata can be stored and read back but none of it is
enforced yet. See `PENDING.md` for the intended read-time enforcement.

## Event bodies are JSON

**Reference:** `application/json` or `application/octet-stream`.

**Nightingale:** JSON only. A binary content type is rejected on append.

**Why:** the store keeps the body in a JSON column; its binary path needs a
serializer per type on the server, which defeats a type-free gateway.

## Event type names are stored verbatim

**Reference:** the type string is opaque.

**Nightingale:** the same, but note the store's own convention for CLR event
types is a snake-cased class name. A server-side projection written against
CLR types would have to use the names the clients send.

## Regular-expression filters are applied after the read

**Reference:** prefix and regular-expression filters on `$all` run in the
server's index.

**Nightingale:** prefix filters on stream name and event type are index
predicates. Regular-expression filters are applied to each page after it is
read, so a selective regex over a large store reads more rows than it returns.

## Multi-tenancy exists

**Reference:** no tenancy.

**Nightingale:** the store supports tenants in one database, and the gateway
reserves a per-call tenant header. Single-tenant until the design in
`PENDING.md` is done. Partitioning the events table by tenant is an
initialization choice, `Nightingale:Store:Partitioning` set to `Tenant`; under it each
tenant numbers its events from its own sequence, so positions are per tenant
and the wildcard read across tenants is refused.
