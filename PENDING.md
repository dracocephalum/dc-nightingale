# Pending

Features that belong to the product but are deliberately not built yet, each
with the approach already decided so it can be picked up cold. An entry moves
to a ticket when work starts and is deleted when it ships. Items owed soon are
in [`TODO.md`](TODO.md); deliberate behavioural differences from the reference event store are
in [`VARIANCES.md`](VARIANCES.md).

## Authentication: what is left

Basic authentication is in: a user name and password on every call, the
built-in administrator from configuration, credentials and tenants in the
gateway's tables and managed through the `Credentials` and `Tenants`
services, roles, and the tenant header; see `VARIANCES.md`. What is left,
in order:

- **The Bearer scheme.** External tokens first, validated against an identity
  provider's published keys with a claim mapping onto the same principal;
  tokens the gateway issues itself only if an application needs them without
  a provider, and then opaque and revocable, never a design of their own.
- **Rate limiting per peer** on refused credentials, beside the lockout per
  credential that is there.
- **A pepper that never leaves a key vault**, an implementation of the pepper
  step that asks the vault to HMAC, behind the same seam.
- **Stream ACLs**, with stream metadata below.

## Protocols beyond gRPC

The server is an application core with gRPC as one adapter over it
(`DESIGN.md` §10), so a second protocol is a sibling, not a rewrite. The
shape, decided so nothing here is re-derived:

- **The ladder, by the reason a sibling exists.** Before any second stack,
  two things the gRPC contract gives away for the same proto and the same
  service classes: gRPC-Web, a middleware, carries unary and server-streaming
  calls through an HTTP/1.1 proxy and into a browser, and cannot carry
  client or duplex streaming, so under it a group's acknowledgements go as a
  unary call; JSON transcoding, `google.api.http` annotations on the proto,
  gives a REST shape for `curl`, scripts and an OpenAPI document. The second
  stack proper is **SignalR**: full duplex where HTTP/2 cannot go, a browser
  client with no proxy, and its own negotiation from WebSockets down to
  server-sent events and long polling. A hub method streams both ways as
  `IAsyncEnumerable<T>`, so reads, subscriptions and a group's duplex map
  onto the seam without polling; the simpler shape, a server stream plus an
  acknowledge call on the same connection, is as good because the connection
  already ties them. Set aside, with the reason: Thrift and the other
  request-response stacks (streaming becomes polling, the least of the
  contract carried); RSocket (the right spec on paper, request-stream,
  request-channel and resumption built in, and a thin .NET implementation);
  a WebSocket with framing of our own (SignalR without the library); brokers
  such as MQTT, AMQP or NATS (a bridge, not an adapter).
- **Assemblies.** A second stack is `Protocols.<Stack>` beside
  `Protocols.Grpc`, referencing `Core` and never the other stack; its server
  adapter is `Server.<Stack>` with `AddNightingale<Stack>()` and
  `MapNightingale<Stack>()`, and its client adapter is `Client.<Stack>`; for
  SignalR a browser speaks to the hub with the JavaScript client directly,
  and `Client.SignalR` is the .NET adapter. When the first sibling arrives,
  the `Server.Grpc` namespace becomes an assembly and the client's transport
  seam becomes an interface in `Client` that each stack implements. The seam
  is defined in the core's terms — `IAsyncEnumerable<EventRecord>` for reads,
  a duplex of messages and acknowledgements for groups, resumable positions —
  and each stack's messages are its own, mapped at the adapter as the gRPC
  ones are (`agentics/rules/coding/architecture.md`), so the hub's messages
  are not the proto's.
- **Both at once.** Kestrel hosts several endpoints with different protocols,
  the hub beside the gRPC services on the same port if the host likes, and a
  stack with its own listener is a hosted service on its own port; the core
  never learns which listener a call came in on. Credentials ride where each
  stack carries them — metadata for gRPC, a bearer token in the header or,
  over WebSockets, the `access_token` query string for SignalR — and end in
  the same `NightingalePrincipal` and the same `Authorizer`. gRPC is the full
  contract; a sibling may carry a subset, and refuses what it cannot carry
  rather than quietly degrading. SignalR's receive-size limit is raised for
  appends, since its default is sized for chat.
- **Fallback in the client.** Safe because appends are idempotent by event
  id, reads are pure, subscriptions resume from a position and seats are per
  connection: a failover is a reconnect over another transport from the
  checkpoint, never a mid-stream migration. SignalR's own reconnect drops the
  streams in flight and is treated the same way. The redirect to a group's
  owning instance maps one to one, the hub answers with the owner's address
  and the client connects there; a SignalR connection is sticky to one
  instance behind a load balancer, which the redirect rule assumes anyway.
  The server advertises its transports and their addresses through
  `ServerFeatures`; the client caches the table and falls back only on
  connection failure, never on a missing feature. On the very first connect
  the seed itself must answer.
- **The connection string.** The authority stays the seed list, as today;
  `transports=grpc,signalr` is the preference order, and a stack listed there
  is used only when listed; `signalr=:5000` or `signalr=h1:5000,h2:5000` is
  an explicit seed list for a stack, in the authority's own `host[:port]`
  comma idiom, for a network where discovery cannot work, the hub's path
  being the stack's own and fixed. One key, one value: a repeated key stays
  refused, and `@` never appears outside the userinfo.
  `nightingale+discover://` resolves per stack, by SRV name
  (`_nightingale-grpc._tcp`, `_nightingale-signalr._tcp`), when a cluster
  needs it.
- **gRPC-compatible derivatives, to look at when the contract is stable.**
  The same proto files under another generator or framing, each checked for
  a .NET server and client before it is counted on: Connect-RPC (gRPC,
  gRPC-Web and its own protocol over HTTP/1.1 and HTTP/2, JSON or protobuf,
  with browser, Go, Swift and Kotlin clients; no .NET server today, and its
  clients already reach this server through gRPC-Web); Twirp (JSON or
  protobuf over HTTP/1.1, no streaming at all, so no more than transcoding
  gives); protovalidate (the request checks as constraints in the proto
  instead of code in the services, if its runtime exists for .NET); Buf (the
  proto toolchain: lint and breaking-change detection against the published
  contract, which the repository has nothing for yet). Code-first stacks that
  drop the proto run the other way and are not candidates: the proto is the
  contract.

## KurrentDB's own protocol, as a second gRPC adapter

Our own proto in the same shape is settled; this is an add-on, not a change of
direction. KurrentDB's official clients would reach this server unmodified if
it also spoke their protocol, and the ports-and-adapters shape makes that a
sibling of `Protocols.Grpc` on the ladder above, not a rewrite: the proto
files, Apache-2.0 in the KurrentDB .NET client's repository under
`kurrentdb/protocol/v1` and `v2`, generate service bases that
`Protocols.Grpc.KurrentDB` and `Server.Grpc.KurrentDB` implement over the
same managers and the same `Authorizer`, and gRPC routes by full service
name, so both contracts sit on one Kestrel port. The copied proto files keep
their license header and copyright, `NOTICE` names them, and the adapter
says "compatible with", never more: the name is Kurrent, Inc.'s trademark,
and the server is theirs; only the client and its protocol are Apache-2.0,
and nothing of the server's source, which is under its own license, is read
into this repository.

When: once the gateway has most of what the reference's client calls, so
the adapter is a mapping rather than a list of `Unimplemented` answers. The
client discovers optional features through `ServerFeatures` and adapts, but
a core call it makes unconditionally fails in front of the application, so
parity comes first, in the order the client exposes it:

1. **Stream metadata**, `$maxAge`, `$maxCount` and `$tb`, read and set as the
   client's own calls do (*Stream metadata* below); `$acl` waits for the
   authentication that is left.
2. **Binary event bodies** (below): every append from the client carries a
   content type, and one that is not JSON is refused today.
3. **The small ones together**: `$bc-` by correlation id, `$streams`, and
   the `Stop` action of a group's nack.
4. **Multi-stream and conditional appends** (below), the client's newest.

What the gateway has that the reference does not, deferred delivery, a
client that resubscribes on its own, server-computed lag counts, waits for a
consumer that asks and is not in the way.

What it takes, read from the client's source at its 1.3.0 tag, so nothing
here is re-derived:

- **Services.** `event_store.client.streams.Streams`,
  `...persistent_subscriptions.PersistentSubscriptions`, `...gossip.Gossip`,
  `...server_features.ServerFeatures`, `...operations.Operations`,
  `...users.Users`, `...projections.Projections`, and the v2
  `kurrentdb.protocol.v2.streams.StreamsService`. A connection string with
  no gossip seeds never calls `Gossip`. `ServerFeatures.GetSupportedMethods`
  is called at connect, and an `Unimplemented` answer means "an old server",
  every optional feature off; the adapter advertises exactly what it carries
  (batch append, persistent subscriptions to `$all`, get-info,
  replay-parked, list, multi-stream append) and the client adapts.
- **Errors.** Trailers, key `exception`, with `wrong-expected-version`,
  `stream-not-found`, `stream-deleted`, `access-denied`,
  `maximum-append-size-exceeded`, and `not-leader` with
  `leader-endpoint-host` and `-port`: a one-to-one map from
  `NightingaleErrors`, the redirect to a group's owner included. An append's
  wrong expected version is a `oneof` in the response, not a status.
- **Shapes.** UUIDs as two 64-bit halves or a string, the byte order to be
  matched exactly; positions as commit-and-prepare pairs, both our one
  sequence; revisions 0-based; a metadata map carrying `type`,
  `content-type` and `created` in ticks; Basic credentials in
  `authorization`, as ours; `connection-name` and `requires-leader` headers
  read and ignored.
- **Not carried, answered `Unimplemented` until the core has them:** filters
  on `$all`, stream metadata (the `$$` streams), multi-stream append,
  projections, operations such as scavenge, and user management beyond what
  the credentials table covers. The client refuses what the feature table
  does not advertise rather than degrading.
- **Tenancy.** The KurrentDB client knows no tenant: a host binds the adapter
  to one tenant, or a credential carries its tenant.
- **Testing.** The official client package against this server, the client's
  checkout as the reference for behaviour; its own test suite is the
  conformance suite where it can run against another server.
- **Size.** Streams, reads and catch-up subscriptions are a mapping, since
  the shapes were mirrored from the start; persistent subscriptions, a duplex
  of acknowledgements with KurrentDB's settings, are the largest part;
  features, gossip and errors are small.

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
  paid for all tenants on every instance at once.
- **A database that knows its tenant**: the tenant id stamped in the
  store's marker row at provisioning, and a registration refused when the
  database says another, since the registry row is otherwise the only link
  and removing it leaves the database behind.
- **Connection pools per tenant**, primary and replica, per instance, with a
  budget and eviction against the server's connection ceiling.
- **Registry changes seen by every instance**: a tenant disabled or
  re-pointed on one instance ends the consumers and stops the tail on the
  others, on a refresh cadence or a signal.
- **Placement of per-tenant leases**, so all tenants's sequencer does not
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

### A registry of stores

The shape that holds database-per-tenant and today's layout as its two
corners is a registry of **stores**, with tenants placed into them: one store
with many conjoined tenants is what runs today, one store per tenant is the
heading above, and several stores with several tenants each is the mix. A
store is exactly the unit the composition root serves now — one database and
its read-only secondary, one tail, one sequencer lease, one set of gateway
tables, one registry of running groups, one marker row — so the work is to
instantiate that unit per store, keyed by a store id and activated on first
use, and to give the tenant registry a store column. Positions, `$all`, the
wildcard, ordinals and persistent groups are coherent within a store and
meaningless across stores, which the features call says per tenant. A store
carries a backend kind, so stores on SQL Server and on PostgreSQL mix behind
one gateway once the Marten backend exists; a backend is admitted by what the
store port demands — an append-only total order per store, atomic
multi-event appends with the per-stream revision check, a high-water mark the
tail can follow, the category and type projections, and a lease — and a
database without a sequence of its own brings one, as the ordinal sequencer
already does.

Three decisions are settled now so that they do not move later. The registry
is **driven by `appsettings` first**: stores are declared in configuration,
not registered at runtime, until the dynamic design above is wanted and
mature; the registry row then exists already, with the same columns. A store
row holds a **reference to its connection strings, never the secret**: a name
the host resolves through its configuration or a vault provider at
activation, as the pepper is resolved, so the table holds nothing worth
stealing and rotation is a configuration change. And the gateway's own tables
are a **control plane with a connection string of their own, defaulting to
the store's**, so a single-store deployment keeps today's layout and a
multi-store one points the control plane elsewhere without a change of shape;
the tenant and store refresh is the cadence `TenantRefresher` already runs. A
tenant moving between stores is an export and an import with positions
renumbered, never a transparent feature.

## Range partitioning of the events table

The store partitions events by tenant or by the archived flag only, and adds
the partition column to its unique stream-and-version index when it does. A
sliding window by sequence, for an events table too large to manage whole,
is not a store feature, so it would be ours to add through the patched
schema: a partition function on `seq_id` with a new range switched in ahead
of the head and old ranges switched out to archive. It forces the sequence
into the stream-and-version unique index, as every aligned unique index must
carry the partition column, which leaves per-stream version uniqueness to
the store's own commit-time check rather than the index. It would be a fourth
value of `Nightingale:Store:Partitioning`, fixed at initialization and
exclusive with the others, since a table has one partition scheme. Built when
sizing demands it.

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
  an append that way; that is a request in `UPSTREAM.md`. Memory still
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

## Schema work off the start path

Today the schema is dealt with before the server serves: `Nightingale:SchemaMode`
says what a start may do, and a start that cannot bring the database to what
the server expects fails. The shape wanted later: schema work that does not
block the server. A `SchemaErrorAction` with two values, fail the start as now,
or disable, where disable puts the store in a *disabled* state, a state of the
store unit `DESIGN.md` describes under *A registry of stores*: the management
calls answer, the health endpoint says why, the operations that need nothing
of what is missing still work, and the store is enabled once the schema is
brought up to date, by a pipeline or by an operator. Migrations themselves
then run beside the server rather than ahead of it. One more piece belongs
with it: `--schema-script <file>`, the delta the server would apply as one
reviewable file, the event store's statements and the gateway's idempotent
migration script in apply order, for the change too large to run at startup
at all, an index over a very large events table say, which a database
administrator runs by hand, the server accepting whatever matches its
definition. How such an index is built without blocking the table is the
next entry's.

## Online and resumable index builds

An index built over a large events table holds the table's schema lock for
the whole build unless it is built online, and an online build that is
stopped starts over unless it is resumable. The schema library writes plain
index statements on SQL Server, where its PostgreSQL side has a concurrent
option (`UPSTREAM.md`); since the server runs the statements the library
writes itself (`DESIGN.md`, seam 3), it can add the options until the library
does. The shape, decided so nothing here is re-derived:

- **A rewrite of the index statements in the batches the server already
  runs**, not a parser of SQL: a `CREATE [UNIQUE] [NONCLUSTERED] INDEX`
  statement gets `WITH (ONLINE = ON, RESUMABLE = ON)` placed after its
  column list, its `INCLUDE` and its `WHERE`, and before any `ON` that names
  a filegroup or a partition scheme, where T-SQL wants it. A statement that
  already carries a `WITH` is left alone, so the rewrite retires itself the
  day the library writes the option. The library rebuilds an index it cannot
  alter as a drop and a create; that pair becomes one create with
  `DROP_EXISTING = ON`, so the old index serves until the new one is built.
- **Where the edition has it.** Online and resumable builds are Enterprise,
  Developer and Azure SQL only, `SERVERPROPERTY('EngineEdition')` 3, 5 or 8;
  elsewhere the statements run as the library wrote them. The script the
  previous entry exports carries the choice into the statement itself, built
  in dynamic SQL with the option appended when the edition reports one of
  those, since it runs on a server the gateway has not seen.
- **The lock wait inside the statement.** An online build still takes the
  table's schema lock briefly at its start and its end, and a build queued for
  it behind a long reader queues everything after it, the reason
  `Nightingale:SchemaLockWaitTimeout` exists. An online statement carries its
  own low-priority wait from the same setting, `WAIT_AT_LOW_PRIORITY
  (MAX_DURATION = …, ABORT_AFTER_WAIT = SELF)`, which waits without holding
  anyone up; its duration counts whole minutes, and which versions accept it
  on a create rather than a rebuild is to be checked against the servers the
  gateway supports when the slice starts.
- **A paused build is not a missing index.** A resumable build that was
  stopped leaves its index in the catalog, disabled, with a row in
  `sys.index_resumable_operations`; the schema comparison would read it as an
  index to create and try to create it over the paused one. While such a row
  exists for one of the store's tables, an apply refuses and names it, and
  the operator resumes or aborts it.
- **Nothing the comparison sees.** Online and resumable are how an index is
  built, not what it is: nothing of them is in the catalog afterwards, so a
  store whose indexes were built online compares as current.
- **Held by name.** A test asserts that every index the gateway adds to the
  events table comes out of the rewrite with the options, so a change in how
  the library writes them fails the build rather than silently building
  offline; the store's own indexes get the options where the rewrite finds
  them.

What it does not cover: adding a column to a large table, the persisted
category column of a store taking ordinals later say, is a change to every
row, online or not, and stays a change to run by hand.

## Binary event bodies, beside the JSON one

A body that is not JSON: a Protobuf or Avro payload, or personal data
encrypted with a key per subject, so that deleting the key forgets the
subject in a store that never deletes, which is the case most likely to make
this real. The shape, decided so the lazy route is not taken: never Base64
inside the JSON body, which the events table's native JSON column would parse
on every write on top of the size and the encoding; the table already has a
binary column, `bdata`, beside `data`, the store's own binary path, and a
binary body goes there raw. A content type on the wire says which; JSON stays
the default, and nothing else changes, since filters, the virtual streams and
the ordinals work on names and index columns, never on a body. The store's
typed binary path, which wants a serializer per event type, is not used; the
gateway's own readers hydrate rows themselves and read either column, and the
plain-stream read through the store's session needs the sticky event seam to
take a row whose JSON body is null, which is the one thing to probe. An event
may carry both at once, a JSON body and a binary one, which is the better
shape for encrypted personal data: what stays readable is JSON, the personal
part is the encrypted binary beside it, and shredding the key leaves an event
that still says what happened rather than a dead row. Only the gateway's own
protocol carries two bodies; the reference protocol has one per event, and the
compatibility adapter would carry the JSON one. No schema change: the column
exists. Deferred until a consumer needs it.

## Regular-expression filters in the database

If post-filtering pages by regular expression proves too slow, SQL Server 2025's
regular-expression functions can move the filter into the query.

## Marten backend

The same gateway over Marten on PostgreSQL. The event wrapper, the feed
abstraction and the tailer are backend-neutral by design; the schema patch and
the wake-up (PostgreSQL has notifications) are the backend-specific parts.
