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
- **Stream ACLs**, the `$acl` key of stream metadata, which is refused until
  then (*Stream metadata* below).

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

Parity is not bought with the core's speed. The gateway is not a clone, and
some of the reference's behaviour is there for its history or its single
writer rather than for an application. Features come in three tiers. The
first tier is appends, reads and subscriptions, which stay as efficient as
they are. A behaviour that would add a read, a write or a lock to all of
them goes to the second tier, served on a path of its own at best effort and
paid for only by the callers who use it, or to the third tier, written down
as a variance with the client-side way around it. *Stream metadata* below is
the worked example.

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
- **Index reads.** The reference's secondary indexes, `$idx-all`,
  `$idx-ce-<category>` and `$idx-et-<type>`, are not streams a client reads
  by name: a read or a catch-up subscription of `$all` whose filter is a
  stream prefix naming exactly one index is served from the index instead of
  the log, in `$all` positions, and the reference's groups have no such path,
  so a group filtered that way matches nothing there. `$idx-all` holds the
  events of non-system streams whose type is not a system one. The adapter
  maps the filter to a stream: `$idx-all` to `$de`, `$idx-ce-<category>` to
  `$ce-<category>` and `$idx-et-<type>` to `$et-<type>`, read by position, a
  group's filter included, so the reference's gap is not reproduced. The
  `$idx-` filter stays the adapter's: the gateway's own contract names the
  streams.
- **Not carried, answered `Unimplemented` until the core has them:** stream
  metadata (the `$$` streams), multi-stream append, projections, operations
  such as scavenge, and user management beyond what the credentials table
  covers. The client refuses what the feature table does not advertise rather
  than degrading.
- **Tenancy.** The KurrentDB client knows no tenant: a host binds the adapter
  to one tenant, or the credential carries it, which the gateway already
  does: a credential bound to a tenant that names none works in its own.
  `$all` is then the tenant's, which on a store keeping many tenants on the
  shared sequence wants *One tenant's `$all` by index* below, or partitioning
  by tenant.
- **Testing.** The official client package against this server, the client's
  checkout as the reference for behaviour; its own test suite is the
  conformance suite where it can run against another server.
- **Size.** Streams, reads and catch-up subscriptions are a mapping, since
  the shapes were mirrored from the start; persistent subscriptions, a duplex
  of acknowledgements with KurrentDB's settings, are the largest part;
  features, gossip and errors are small.

## Stream metadata

The reference keeps a stream's metadata as events of type `$metadata` in a
stream of its own, `$$<stream>`, the last one in force: the reserved keys
`$maxAge`, `$maxCount`, `$tb` (truncate before), `$cacheControl` and `$acl`,
and whatever else the application puts there. It changes what a read of the
stream returns and, for a soft delete alone, how an append to it is checked;
a read of `$all` ignores it. Parity here is weighed against cost, as
everywhere (*KurrentDB's own protocol* above): reads and appends stay as
cheap as they are, and what of the reference's behaviour would add a read, a
write or a lock to every append is served on a path of its own at best
effort, or written down as a variance. The shape, decided so nothing here is
re-derived:

- **Stored as events, in the events table.** `SetStreamMetadata` appends a
  `$metadata` event to `$$<stream>` and `GetStreamMetadata` reads the last
  one; a read of `$$<stream>` returns the history as real events. The gateway
  keeps no table of its own for it, because a table would take the changes
  out of the one sequence: a replication or a migration that reads `$all`
  from the start needs a change of metadata in its place among the events
  around it, or it applies a soft delete and the stream's recreation in the
  wrong order, and the history is the audit of who changed a stream's
  retention, and when. Every metadata event is kept; they are small and rare.
- **Concurrency is the store's.** A set carries an expected revision of the
  metadata stream, and two sets from two instances race on the store's unique
  index of tenant, stream and version, as any two appends do. An append to the
  stream and a change of its metadata touch different streams and neither
  depends on the other, since metadata acts only when the stream is read.
  Tombstone removes the stream and its metadata stream in one transaction;
  delete, the archive, leaves the metadata stream as it is.
- **Written by the metadata call alone.** `Append` goes on refusing every `$`
  name, so a `$$` stream is written only by `SetStreamMetadata`, which
  validates the reserved keys' types and keeps the rest as JSON. `$acl` is
  refused, with a reason of its own, until authentication carries stream ACLs
  (*Authentication: what is left*).
- **Enforced inside the read's own statement.** A read of a plain stream finds
  the last metadata event with a `TOP 1` seek on the same unique index, joined
  to the page by an `OUTER APPLY`, so it costs no round trip: the lower bound
  `max(last − $maxCount + 1, |$tb|)` becomes a seek bound on that index, and
  `$maxAge` a check of each row's timestamp. A stream without metadata finds
  nothing in that one probe and reads as it does today, so a store that never
  sets metadata pays a probe of a few index pages per read and nothing else.
  Reads of plain streams move from the store's fetch to the gateway's mirror
  of the events table for it, as the pages of `$all` did. A subscription or a
  group on the stream starts from the same bound. Neither `$all` nor a virtual
  stream applies metadata, as in the reference. Nothing is cached: every
  instance appends, and a cache would learn of a change only through the tail,
  a polling interval late, serving meanwhile events a new `$maxCount` has cut.
  If a measurement ever asks for less than the probe, the way out is a store
  setting fixed at initialization that refuses metadata and skips it, not a
  cache.
- **`$all` becomes the whole log, and two streams select from it.** With
  metadata stored as events, `$all` carries them as the reference's does, and
  the variance *`$all` holds only application events* goes. `$de`, the data
  events, is `$all` without the events whose type begins with `$`, the
  reference's `ExcludeSystemEvents`, at the cost of `$all`: the same range of
  the clustered position, with the type checked on rows that are read anyway.
  It is read by position and has no ordinals, since numbering it would write
  every row a second time. `$mde`, the metadata events, is `$et-$metadata`
  under another name, one seek on `ix_pc_events_type_seq`, since `$metadata`
  is the only system type the table holds. Both are streams, so reads,
  catch-up subscriptions and groups all take them. Category ordinals are
  unaffected: `$$orders-1` is in the category `$$orders`, never `orders`.
- **Appends never read metadata.** The reference checks an append against
  the soft-delete value of `$tb` and writes an un-delete after it, which is
  safe there because one writer handles every write in turn. Here every
  instance appends, and a check of metadata before an append would go stale
  between the two, or cost a lock on every append for a feature most stores
  never use. So an append is checked against the stream's revisions alone,
  by the store and atomically, and whatever metadata decides is decided when
  the stream is read. The one sequence already places a change of metadata
  among the events around it for a reader of `$all`, which is all the
  reference's single writer gives a reader.
- **Delete and soft delete are different features.** Delete, the store's
  archive, flags the stream and every one of its events, hides them from
  `$all` too and refuses appends; undoing it, which the gateway does not
  offer, would bring every event back. The reference's soft delete hides the
  events from reads of the stream alone, leaves them in `$all`, and is undone
  by the next append, which recreates the stream: its revisions go on from
  the last one, since the old events keep theirs until a scavenge, and the old
  events stay hidden. Delete cannot stand in for it, so soft delete is a call
  of its own, named when the slice starts, and a `$tb` set to its largest
  value through `SetStreamMetadata` on a stream that is not soft-deleted is
  the same request.
- **Soft delete is a read rule, stored as a negative `$tb`.** The reference
  writes `$tb` at its largest value, replacing the other keys, and that value
  is its only mark of a soft delete; its un-delete then rewrites `$tb` as the
  last revision plus one. The gateway writes, in one metadata event, the
  negative of the last revision plus one, `-6` for a stream whose last
  revision is 5, and nothing else, so one number says both that the stream
  was soft-deleted and where it is truncated. A read finds the stream deleted
  while `$tb` is negative and its absolute value is beyond the last revision:
  not found. Otherwise the absolute value is the truncation, so recreation
  needs nothing of its own: the next append lands at 6, the stream reads
  again from there, and the same `-6` now means what the reference's
  un-delete writes. The value never leaves the gateway as it is stored: the
  core's metadata call reports a soft delete as a field of its own, and the
  KurrentDB adapter serves a read of `$$<stream>`, which is how that client
  reads metadata, with `$tb` at its largest value while the stream is deleted
  and at 6 after, the two values the reference's history holds; that
  client's parser throws on a negative `$tb`. A negative `$tb` from a caller
  is refused, where the reference ignores it; its client cannot send one. A
  stream with no events cannot be soft-deleted, as in the reference, so the
  value is never 0. Since no write has to follow the append, no interleaving
  of an append and a soft delete on two instances strands an event: an
  append committed before the soft delete reads the last revision is hidden
  by it, one committed after is the recreation, the two orders the
  reference's writer allows. The soft delete reads the last revision and then
  writes, and so does a metadata set on a soft-deleted stream (next), so both
  take the update lock on the stream's row in the store's streams table
  first; every append takes that lock anyway to move the version, so the
  lock is paid on those two paths and never by an append.
- **A metadata set on a soft-deleted stream un-deletes it.** In the
  reference, any write to `$$<stream>` while the stream is soft-deleted is
  followed by its un-delete, which rewrites `$tb` in what was just written:
  a set of `$tb` at 3 ends at 6, the old events stay hidden, and so does a
  set of `$tb` at its largest value, which un-deletes rather than deletes
  again; the other keys of the set are kept. The gateway writes that end
  state in one event: the caller's keys with `$tb` at the absolute value of
  the stored one, under the lock above. Once the stream is recreated, a set
  of `$tb` at 3 is a plain set and the events from 3 read again, in both,
  until physical removal (below) takes them.
- **"No stream" after a soft delete, by a retry on its failure.** The
  reference accepts an append that expects no stream on a soft-deleted stream
  and numbers its events from the last revision plus one, exactly as an
  append that expects the last revision; a recreated stream never starts
  again at 0. Here such an append fails on the store's check, and only then,
  on the failure path, the gateway reads the metadata: when the stream is
  soft-deleted with `$tb` at the negative of the revision the failure
  reports plus one, the
  append is retried once expecting that revision, which the store checks as
  atomically as any. An append that succeeds never reads metadata.
- **What stays a variance**, written into `VARIANCES.md` when the slice
  ships. An append that expects the stream to exist is accepted on a
  soft-deleted stream, where the reference refuses it: refusing it means
  reading metadata when the append would succeed, which is acceptable only
  if that expected state alone pays for it, as a probe before such appends,
  and even then a soft delete landing between the probe and the append gets
  through; it waits for a consumer that asks, and is then built that way,
  never as a check on every append. The metadata stream's history has no
  un-delete event, stores the negative `$tb` the adapter translates, and
  has one event where a set on a soft-deleted stream gives the reference
  two, so the reference's moment between them, with the caller's `$tb` in
  force, does not exist here. And a change of metadata that un-deletes the stream
  between a failed "no stream" append and its retry lets the retry through
  where the reference would refuse it; that window is a variance, not a
  lock.
- **Physical removal** of the events metadata hides is a separate maintenance
  job, the analogue of a scavenge, and a slice of its own; whether the store
  can delete a stream's events below a version, or that is a request in
  `UPSTREAM.md`, is the first thing it finds out.

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

## One tenant's `$all` by index

On a store that keeps its tenants on the shared sequence, a tenant's `$all`
is a range of the clustered position with the tenant checked on every row
read, since the store has no index of tenant and position: a catch-up reads
every tenant's rows to return one tenant's, a hundred for one on a store of a
hundred tenants of a size. The live tail is short and does not notice;
replaying history does. Partitioning by tenant avoids it, a sequence per
tenant clustered on its own, at the price of the wildcard read across
tenants. The other way, decided so nothing here is re-derived:

- **The index.** `ix_pc_events_tenant_seq` on tenant and position, filtered
  on the archived flag like the gateway's other indexes, with `is_system`
  included: a computed column, whether the type begins with `$`, which `$de`
  brings (*Stream metadata*). The column is not persisted, so no row is
  rewritten to add it, only the index built; that a column computed and not
  persisted can be included in a filtered index is checked on a real server
  first, the category column of `ix_pc_events_category_seq` being persisted.
- **One lookup per event, as in the reference.** A tenant's `$all` is a seek
  on tenant and position, in position order so a page stops early, with a
  lookup of each row's body in the clustered index. The reference's own index
  reads cost the same, positions from the index and then a read of each
  record. A tenant's `$de` checks the included flag on the index entry before
  the lookup, so a system event costs a skipped entry, never a row.
- **The reader chooses the path, not the optimizer.** For a tenant that is
  the whole store the clustered range is the cheaper, and a plan cached for a
  small tenant and reused for a large one, or the reverse, picks wrong. So
  the index serves a tenant's scope, and the clustered range the default
  scope and the read across all tenants.
- **Only where it pays.** A store that keeps tenants on the shared sequence;
  partitioned by tenant, the clustered key already leads with the tenant, and
  a store of one tenant needs neither. Added to an existing store, it is an
  index build over the whole table, which *Online and resumable index builds*
  takes care of.
- **Later, if a store asks.** Where system events are many, the flag can be a
  key column instead, tenant, flag and position, so `$de` seeks past them. It
  would be an index of its own beside this one, since `$all` needs one range
  in position order and the flag in the key splits it in two, and so a store
  setting the reader reads, since the reader chooses its path itself. Until a
  store asks, the included flag serves both streams.

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
option (`UPSTREAM.md`). Rather than wait for it, or rewrite the text it
writes, the gateway takes the events table's indexes out of the library's
hands and applies them itself. The shape, decided so nothing here is
re-derived:

- **Every index of `pc_events`, the store's and the gateway's.** The
  definitions stay where they are made: the store's come from its own feature
  and the gateway's from the patched one, rebuilt on every build, so an
  upgrade of the store that changes an index still shows as a difference.
  The patched feature moves them off the table definition and names each one
  ignored (`IgnoreIndex`, which the schema library honours on SQL Server from
  9.27, and which refuses a name the table still defines, so it is a move, not
  a mark); the library then neither creates, compares nor drops them, and
  manages the columns, the key and the partitioning as before.
- **The comparison is the gateway's, on the library's own terms.** The table
  as it is comes from the library's `FetchExistingAsync`, and each definition
  is held against the index of its name with the library's `Matches`, the
  canonical comparison the library itself uses, so it is exactly as strict: a
  definition with no index is one to create, one whose index differs is one to
  rebuild, and an index no definition names is one to drop, as the library
  drops it today. Nothing is parsed: the statements are written from the
  definitions, which carry the columns, uniqueness, included columns and
  filter.
- **The order is the whole difficulty.** A column an index uses cannot be
  dropped or altered while the index stands, and an index cannot be built on a
  column that is not there yet. So an apply runs in three steps around the
  library's batches for the table: first, the indexes to drop, and every index
  that uses a column the library is about to drop or alter, read from the
  difference between the table's columns as defined and as fetched; then the
  library's batches; then the indexes to create, and the ones to rebuild, as a
  create with `DROP_EXISTING = ON` where the old index still stands, so it
  serves until the new one is built, and a plain create where the first step
  dropped it. Every combination is held by an integration test against a real
  server: an index on a column added in the same apply, one on a column
  dropped, one on a column whose type changes, a changed filter, a changed
  uniqueness, an index renamed, an extra index, and each of those on a table
  partitioned by tenant and by the archived flag.
- **Online and resumable where the edition has them.** A create carries
  `WITH (ONLINE = ON, RESUMABLE = ON)` on Enterprise, Developer and Azure SQL,
  `SERVERPROPERTY('EngineEdition')` 3, 5 or 8, and runs offline elsewhere. On
  a partitioned table SQL Server aligns the index with the table's scheme by
  itself, so no `ON` clause is written. The script `--schema-script` exports
  makes the choice in the statement itself, as dynamic SQL, since it runs on a
  server the gateway has not seen.
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
  `sys.index_resumable_operations`; read as missing, it would be built again
  over the paused one. While such a row exists for the table, an apply refuses
  and names it, and the operator resumes or aborts it.
- **What the gateway then owns.** The library's creation script no longer
  holds the indexes, so initialization creates them after the table,
  `--export-schema` writes them, the approved schema snapshot holds them, the
  hash of the creation script that fast boot trusts covers the definitions,
  and the schema report lists the indexes to create, rebuild or drop beside
  the library's difference. Online and resumable are how an index is built,
  not what it is, so nothing of them is in the catalog afterwards and a store
  built that way compares as current.

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
