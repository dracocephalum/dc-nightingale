# Pending

Features that belong to the product but are deliberately not built yet, each
with the approach already decided so it can be picked up cold. An entry moves
to a ticket when work starts and is deleted when it ships. Items owed soon are
in [`TODO.md`](TODO.md); deliberate behavioural differences from the reference event store are
in [`VARIANCES.md`](VARIANCES.md); what was weighed and set aside is in
[`UNSUPPORTED.md`](UNSUPPORTED.md).

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
   client's own calls do, and the reference's delete as the gateway's reset:
   done. `$acl` waits for the authentication that is left (*Stream metadata*
   below).
2. **Binary event bodies**: done, as a second body beside the JSON one
   (`VARIANCES.md`, *An event has two bodies*); the mapping to the
   reference's one body is the adapter's, under *Bodies* below.
3. **The small ones together**: `$bc-` by correlation id, `$streams` and
   `$category-`, by position, and by ordinal through links the sequencer
   writes: done. The `Stop` action of a group's nack is a
   variance, mapped by the adapter under *Group nacks* below.
4. **Multi-stream and conditional appends**: done, as `AppendToStreams`,
   the reference's append session and its consistency checks on streams
   not written in one call; what is left of them is under *Multi-stream and
   conditional appends* below, and the mapping under *Multi-stream appends*.

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
- **Event numbers.** A reference record's `EventNumber` is its revision in
  its own stream, never a position; positions are the log's. A `$ce-` or
  `$et-` stream holds link events with revisions of their own, dense, and a
  client reading one is on the link: `OriginalEventNumber`, the number a
  stream subscription checkpoints with, is the link's revision, the
  gateway's ordinal, while the resolved event's `EventNumber` is its
  revision in its own stream. So the adapter serves `$ce-` and `$et-` under
  ordinal numbering, the ordinal as the link's revision. On a store without
  ordinals it serves positions in its place: they only increase, so a
  checkpoint still resumes, but two of them subtracted are no count, a
  variance to write when the adapter ships. The core's own client gets its
  one number per record differently, as *A record's locus* below.
- **Deletes and metadata.** The reference's delete is the gateway's `Reset`
  and its tombstone the gateway's `Delete`, one call each; the gateway's
  `Archive` has no reference call to map. That client reads metadata as the
  last event of `$$<stream>` and its parser throws on a negative `$tb`, so
  the adapter translates the stored reset mark on every `$$` page it serves:
  on the last metadata event, `$tb` at its largest while the mark is beyond
  the stream's last revision and the mark itself once an append has reached
  it, what the reference's delete and un-delete events hold; on an earlier
  event, always the largest, what the reference held there. The stream's last
  revision comes from the first statement of the `$$` read, the one that
  finds the head, so the page and the revision are one moment's view; the
  store then needs a read of a metadata stream that returns it, built with
  the adapter. A reset of a stream with metadata but no events is served as
  the cleared metadata it is here.
- **Bodies.** A reference event has one body, and its `content-type` says
  which: `application/json` appends JSON data, `application/octet-stream`
  binary data alone, stored with the `{}` placeholder. A read serves one
  body by the store's own rule for telling a binary event from a JSON one:
  the binary data, as `application/octet-stream`, when the JSON is the `{}`
  placeholder, and the JSON otherwise, so an event of the gateway's own with
  both bodies shows a reference client its JSON. That rule needs nothing
  stored. A writer that wants a reference client to see the binary data of
  an event whose JSON is real would need a marker, booked under the name
  `$primaryDataSlot`, a metadata key of `json` or `binary`, `json` when
  absent, and written only when someone needs it.
- **Listings and correlations.** `$bc-`, `$streams` and `$category-` are
  served as they are; a reference client reads links, so `$streams` serves
  each stream's first event as the resolved link, and `$category-` builds a
  `$@` event per stream from the first event, its body the stream's name.
  The category rule is the gateway's, so a stream named just the category is
  in it, which a reference client sees too (`VARIANCES.md`). Their numbers
  are the link revisions a reference client counts on a store with
  ordinals, and positions on one without, the same variance as `$ce-` there.
- **Multi-stream appends.** The v2 `AppendSession` maps onto
  `AppendToStreams` one stream a request, reporting the first conflict of
  the list as the reference does, and `STREAM_ALREADY_IN_APPEND_SESSION` for
  a stream named twice. `AppendRecords` maps by grouping its records per
  stream, in the order the streams first appear, each stream expecting any
  revision; a check on a stream written becomes its expected state, any
  other a check, and every violation is reported by its check's index. A
  record's typed properties become the event's JSON metadata, and a record
  of `$$<stream>` a metadata set, parsed as the reference's metadata. The
  reference's deleted and tombstoned check states map onto a reset and a
  deleted stream; its byte limits on a record and a transaction have no
  counterpart beside the event count.
- **Group nacks.** The reference client's `Stop` is served as the consumer
  leaving: its call ends, and its events in flight go back to the group,
  not counted as a retry. The reference server skips them instead
  (`VARIANCES.md`, *A group's nack has no Stop*).
- **Not carried, answered `Unimplemented` until the core has them:**
  projections, operations such as scavenge, and user management beyond what
  the credentials table covers. The client refuses what the feature table
  does not advertise rather than degrading.
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
`$maxAge`, `$maxCount`, `$tb` (truncate before), `$cacheControl`, `$tmp` and
`$acl`, and whatever else the application puts there. It changes what a read of the
stream returns and, for a soft delete alone, how an append to it is checked;
a read of `$all` ignores it. Parity here is weighed against cost, as
everywhere (*KurrentDB's own protocol* above): reads and appends stay as
cheap as they are, and what of the reference's behaviour would add a read, a
write or a lock to every append is served on a path of its own at best
effort, or written down as a variance. The shape, decided so nothing here is
re-derived:

- **Landed: stored, set, read back, and `$all` the whole log.**
  `SetStreamMetadata` appends a `$metadata` event to `$$<stream>`, in the
  reference's JSON, and `GetStreamMetadata` reads the last one, with the
  metadata stream's revision a set expects; no table of the gateway's, so a
  change keeps its place in the one sequence for a replay of `$all` and the
  history is the audit of who changed a stream's retention. Concurrency is
  the store's unique index, as for any append; a set needs no stream, so
  metadata can precede the first event; delete removes the metadata stream
  in the same transaction and archive leaves it. `$$<stream>` is read
  and followed as a stream, written only by the metadata calls, and refused
  for a group. Reserved keys are checked, a wrong type or a value out of
  range refusing the set; `$acl` is refused as an invalid argument; `$tb` at
  its largest value resets the stream (below); `$cacheControl`
  and `$tmp` are kept in `StreamMetadataKeys.CarriedOver`, stored and
  returned without effect, `$tmp` to get its meaning with physical removal.
  `$all` carries the metadata events; `$de`, the data events, is `$all`
  without the types that begin with `$`, at the cost of `$all`, by position
  only and followed as `$all` is; `$mde` is `$et-$metadata` under another
  name, ordinals included. `VARIANCES.md` says what differs.
- **Landed: enforced on reads, in the statements a read already ran.** A read
  of a plain stream goes through the gateway's mirror of the store's tables,
  as the pages of `$all` did, in the two statements the store's own fetch
  took. The first finds the stream's row and, in the same statement, the last
  metadata event with a `TOP 1` seek backwards on the store's unique index of
  a stream's versions, so the probe costs no round trip and a stream without
  metadata finds nothing in a few index pages. The second reads the page with
  the lower bound `max(last − $maxCount + 1, $tb)` as the start of its seek on
  that index and `$maxAge` as a check of each row's timestamp against the
  database's clock, `SYSUTCDATETIME()`, the clock that stamped it; the check
  comes before the page is counted, so a short page still means the end. The
  head's first revision is that bound, beyond the last revision when the
  metadata hides every event, and such a stream reads empty and exists, as in
  the reference. A catch-up subscription or a group on the stream starts from
  the bound because its reads do, and goes on from the head after a short
  page, whatever the metadata hid. Live delivery is not checked, as in the
  reference: the router reads what an append brought as it is stored, for the
  stream's followers and its category's and type's alike. Neither `$all` nor
  a virtual stream applies metadata, and a metadata stream reads its whole
  history (`VARIANCES.md`). Nothing is cached: every instance appends, and a
  cache would learn of a change only through the tail, a polling interval
  late, serving meanwhile events a new `$maxCount` has cut. If a measurement
  ever asks for less than the probe, the way out is a store setting fixed at
  initialization that refuses metadata and skips it, not a cache.
- **Landed: the partition key in a stream's read.** On a store partitioned by
  tenant the store's unique index of a stream's versions leads with the
  tenant's partition key, `tenant_ordinal` in the store's names (not an
  ordinal of the gateway's, `DESIGN.md` seam 6), which the read and its probe
  did not name, any more than the store's own fetch had, so they could not
  seek it, and a predicate on the tenant alone eliminates no partition. Every
  statement of a stream's read now names it, through a subquery of the
  store's registry, `pc_tenant_partitions`, so nothing about a tenant is
  cached; the tenant stays in the predicate because tenants can share a
  partition. No index is added: a second index over the same columns would
  cost every append a second write. The key is selected nullable, since the
  first of an `int` is wrapped in `COALESCE(..., 0)`, which the database does
  not seek by. An integration test forces each statement onto the index by
  the key and the stream: the statements naming the key run, and the same
  ones without it are refused with error 8622.
- **`$maxAge` reads its expired rows, booked.** A stream long past its
  `$maxAge` reads its expired rows on every forwards read from the start,
  each looked up in the table for its timestamp, which the unique index does
  not hold. Physical removal (below) is the answer, as the reference's
  scavenge is. Booked and not planned, since `$maxAge` is no first-tier path:
  an index for it is one a host adds as it adds any optional index, never one
  provisioned by default, because an index on the stream and the timestamp
  costs every append to every stream a write, and the timestamp included in
  the unique index is a change to the store's index; and a binary search by
  version over the existing index, as the reference's read does, which
  costs appends nothing and relies on a stream's timestamps rising with its
  versions, which they do while the database's clock does not step back.
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
- **Landed: reset, archive and delete, under one deletion level.** Three
  calls where the reference has two, named for what each does: `Reset` is
  the reference's delete, its soft delete, which hides and comes back;
  `Archive` is the store's archive, which the gateway called delete before,
  flagging the stream and its events out of every read, `$all` included, and
  refusing appends, the rows kept; `Delete` is the reference's tombstone,
  removing the stream and freeing its name. `Nightingale:Deletion:Allowed`,
  `None`, `Archive` or `Delete`, each allowing what is below it, replaces the
  two switches; a reset is allowed at every level, authorized as a metadata
  set, since it removes nothing and a set undoes it. `VARIANCES.md`, *Reset,
  archive and delete*, has what differs from the reference.
- **Reset is a read rule, stored as a negative `$tb`.** One metadata event
  replacing the metadata as the reference's does, `{"$tb": -6}` for a stream
  reset at revision 5, so one number says both that the stream was reset and
  where it is truncated. A read finds it not found while the mark is beyond
  the last revision; once an append reaches it, it is a plain truncation,
  which is what the reference's un-delete writes, so recreation needs nothing
  of its own and no write follows an append. No interleaving of an append
  and a reset on two instances strands an event: an append committed before
  the reset reads the last revision is hidden by it, one committed after is
  the recreation, the two orders the reference's writer allows. A caller can
  never set a negative `$tb`, and the metadata call reports the truncation
  and the reset apart. A stream with no events but with metadata has it
  cleared, which is what a reset comes to with nothing to hide. A set at the
  largest `$tb` resets keeping the other settings; a set on a reset stream
  brings it back in the reference's two events, the metadata as given, then
  the same with the reset's truncation, in one append, and a second set
  lowers the truncation. An append that expects no stream and finds a reset
  one is retried once on the failure path, expecting the last revision, and
  the same events sent again after a lost answer succeed with what was
  written; an append that succeeds never reads metadata.
- **No row lock.** The design held the update lock on the stream's row for a
  reset and a set on a reset stream. Neither needs it: each decision is
  written under an expected revision of the metadata stream, so a change of
  metadata between its read and its write is caught by the store's unique
  index and decided again, and an append landing between them is ordered
  after the decision, which is one of the two orders the reference's single
  writer could have given.
- **Physical removal** of the events metadata hides is a separate maintenance
  job, the analogue of a scavenge, and a slice of its own; whether the store
  can delete a stream's events below a version, or that is a request in
  `UPSTREAM.md`, is the first thing it finds out.

## Multi-stream and conditional appends

`AppendToStreams` carries both of the reference's: the append session,
several streams each under its own expected revision, and the consistency
checks on streams not written. What is left of them:

- **Records in one order across streams.** The reference's `AppendRecords`
  keeps the order the records were given in, a1, b1, a2; the store positions
  a commit stream by stream (`VARIANCES.md`). Only a reader of several
  streams at once sees the difference, `$all`, a correlation or a category,
  within one commit. Not without the store writing a commit's events in the
  order given, which no public seam offers.
- **Deleted and tombstoned as check states.** The reference's checks can
  assert a stream is deleted or tombstoned; the gateway's assert a revision,
  no stream or that it exists. The adapter maps the two onto a reset and a
  deleted stream when it comes.
- **A total beside the per-append limit.** The store writes each stream of a
  commit as its own command, so the parameter limit binds one stream's
  events; the total is held to the same count only so that a request held in
  full stays bounded. One of the tunables in `TODO.md`.

A reset inside an append to several streams, and the store's own tag-based
conditions, were weighed and set aside (`UNSUPPORTED.md`).

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
multi-event appends with the per-stream revision check, positions the tails
can follow and a horizon to judge their gaps by, and a lease — and a
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

## The PostgreSQL horizon

The Marten backend's sequence heads, and so its ordinals, which are links on
both backends (`DESIGN.md`, seam 6), need a horizon of its own: no
transaction of this database still in progress whose id is below the
snapshot's `xmax` at the marker, from `pg_snapshot_xip(pg_current_snapshot())`,
each id mapped to its database through the lock every id holder takes on its
own id in `pg_locks` and that session's database in `pg_stat_activity`, and no
prepared transaction of this database, from `pg_prepared_xacts`; the gateway's
own sessions left out. Every role may read all of it, so the backend needs no
grant. The backend appends in Marten's quick mode, which writes or locks the
stream's row before it takes a position for a new stream or an append that
checks the version, so its transaction has an id first; a plain append to an
existing stream takes its position a statement before its first write, inside
one function call, which a minimum age of a second covers, or none at all if
the backend's append locks the stream's row first. Marten's rich append mode
is not supported (`UNSUPPORTED.md`). What Marten's own detector learned, a
cluster's transaction ids counted for one database, the daemon's own lock
session holding a gap for good, applies to this query as written.

## Adopting ordinals on a store initialized without them

The events table is the same with ordinals and without, so adopting them is
the sequencer numbering from position 0 into the links while the store is
served, and saying how far behind it is until it catches up. Refused today,
since `Nightingale:Store:AssignOrdinals` is fixed when the store is
initialized; built when a store needs it.

## Pruning links whose events are gone

A link outlives its event: a stream deleted, the store's hard delete, leaves
its events' links behind as holes a read skips, and they count in a virtual
stream's bounds, as link events to deleted events do in the reference until
a scavenge. A maintenance pass can remove them, an anti-join of the links
against the events, a range of positions at a time and throttled, with one
rule: the highest link of every key stays, because the sequencer finds a
key's next ordinal from it, and removing it would hand that ordinal out again.
Everything below it may go, and the stream's first ordinal then moves up, as
the reference's does after a scavenge. An archived event keeps its row, so
its link stays. Not until a store's links are worth the space.

## Scrubs

A re-check of a range for events without a type link, an anti-join of the
events against the links, for where the head can be wrong: the best-effort
strategy, whose wait can skip a position whose row is not yet in the table when
it ends, and a writer no horizon sees, as Marten's rich append mode. Three, by
window and meaning, each numbering what it finds with the next ordinal of its
keys, so by ordinal a late event follows what was appended after it:

- a light guardrail every few minutes over the last quarter of an hour, a
  finding logged and counted; opt-in;
- a daily scrub over the last day, throttled, a finding an alert; opt-in;
- a manual scrub from the host, `--scrub-ordinals` over any range of positions
  or from a date, found by a binary search over positions reading each probe's
  timestamp, for an operator who wants the whole store proven.

What they cost, measured in a prototype on both databases over a million
events, on a held connection: the check of a range at some 2 to 4 µs a
position on SQL Server and 8 on PostgreSQL, so the guardrail over a quarter of
an hour at a thousand events a second is seconds, and the daily scrub
minutes. Under the transaction horizon they find nothing by construction, so
none is built yet.

## Virtual streams in `$streams`

The reference lists its link streams in `$streams`, since each has an event
0. The gateway's virtual streams have no first event of their own, but the
sequencer knows when a key gets its ordinal 0: that event begins `$ce-x`,
`$et-x` or `$bc-x`, and a link with the virtual stream's name could list it,
the same event then appearing once as its stream's start and once as the
virtual stream's, as the reference's resolved links show. Under ordinals
only, since finding a key's first event without the sequencer is a lookup a
row.

## A listing of categories

The reference has no stream that lists its categories: a client that wants
them writes a projection of its own. The gateway could serve one,
`$categories`, holding each category once, as the first event of its first
stream, which is what a reader of the categories wants: the name, and when
the category began. It is the gateway's own, with nothing in the reference
for the adapter to map to it.

By position it needs no new index. `ix_pc_events_category_starts` is ordered
by tenant, category and position, so each category's first row is one seek,
the next category the next seek past it, a skip scan written as a recursive
query; the categories are few, tens or hundreds where streams are millions,
so a page, its bounds and its count are a seek a category, sorted by
position in the statement. Live, the router asks only of an event that
begins a stream, and only while `$categories` is followed, whether its
category had a stream before it: one more seek of the same index.

Under ordinals it is cheaper still. A category's first stream is the event
the sequencer numbers 0 in `$category-x`, so `$categories` is the links of
that type at ordinal 0, an index on them filtered to that ordinal, and the
numbers of `$categories` itself are one more type of link, the next number,
6, filled in the same batch: the way to build it on a store with ordinals,
by position the way on one without. Not until someone asks for it.

## Separator characters, chosen at initialization

The category rule is one, a hyphen. A store could choose its separator
character, or the last separator rather than the first, as the reference's
projection is configured, fixed at initialization like the other store
settings, the column's expression and the rule in process changed together
and held to each other by a test. Not until someone names streams another
way and asks.

## Recategorizing a store

A store's category rule changed after the fact: rebuild the computed column
and every category index, clear the category ordinals and let the sequencer
number them again, as the ordinal backfill does; checkpoints by position
survive, those by category ordinal do not. An offline host command, if a
store ever needs it.

## An append statement of our own

The store writes an append in two round trips, a read of the stream's
version and then its batch, and the binary data adds a third. One statement
of the gateway's own, the stream's version checked and the events inserted
from one table-valued parameter, would be one round trip, carry the binary
data in the insert, fill the category from any rule in process rather than a
computed column, and take more than the store's limit of events in one
append. The price is owning what the store does best and what the gateway's
rule leaves to it (`DESIGN.md`, *The seams*): the version check and the
race for a new stream, every column the store expects with its meaning, the
per-tenant sequences of a store partitioned by tenant, and a second statement
for the second store. Taken only on evidence: an append's round trip
measured to matter, or appends larger than the limit asked for.

## Server-computed lag counts

For consumers that alert on an absolute number of events behind rather than a
growing lag: a range count over the category or type index from the
subscriber's position to the head, reported on the fell-behind message or by a
small extra call. Under ordinal numbering the count is already a subtraction,
holes included; this entry is for global numbering.

## A record's locus

Booked, not planned. A record carries three numbers: its revision in its own
stream, its position in the log, and its ordinal under ordinal numbering. A
consumer has to know which of them its read speaks: the revision for a plain
stream, the ordinal under ordinal numbering, the position for `$all` and for a
virtual stream numbered globally. The locus would be that number, set on
every record the client delivers, so a consumer reads, stores and resumes
after one property whatever it reads, in reads and subscriptions alike:

> **Locus**: a record's place in what you read. Its revision when you read a
> plain stream; its ordinal under ordinal numbering; its position otherwise.
> Compare it, store it, and resume after it, in the same read and the same
> numbering.

The client sets it from the request it made, the stream and the numbering;
it is never on the wire, where it would repeat what the caller said, and a
record the server's store returns leaves it unset. The name was chosen over
`Checkpoint`, which in the reference names a point announced or saved now and
then, never a property of every event; `Cursor`, which suggests an opaque
token; and the reference's `OriginalEventNumber`, which on `$all` is a
revision rather than the position a reader of `$all` resumes from. The
KurrentDB adapter computes the reference's own numbers for its clients, and
is not served by this property.

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
position are served from the read-only connection as far as the sequence
head it holds reaches, exactly, and bounded reads of plain streams when the
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

## Regular-expression filters in the database

If post-filtering pages by regular expression proves too slow, SQL Server 2025's
regular-expression functions can move the filter into the query.

## Marten backend

The same gateway over Marten on PostgreSQL. The event wrapper, the feed
abstraction and the tailer are backend-neutral by design; the wake-up
(PostgreSQL has notifications) is a backend's own. Read against Marten's
source and tried on PostgreSQL, nothing stops it, and the store is taken as it
ships:

- **No change to Marten's tables or tenancy.** Its migrations drop columns they
  do not know, and supplying a database of our own means a tenancy of our own,
  under which Marten refuses its managed tenant partitions. The category is an
  expression index on an immutable function of the stream name, which needs no
  column and is served by a seek in position order on the live partition;
  Marten's documented `IgnoreIndex` keeps its migrations off it and off the
  other indexes. The ordinals are links (`DESIGN.md`, seam 6).
- **Its placeholder rows.** After a save that fails once positions were taken,
  Marten writes a `tombstone` event in the stream `mt_tombstone` at each, so
  its high-water mark need not wait. Every read and the sequencer leave that
  stream out.
- **Checks before Marten writes.** A multi-stream append's checks run in a
  transaction of the gateway's own, which Marten's session joins
  (`SessionOptions.ForTransaction`), so a refused check never reaches its
  write. PostgreSQL has no lock on a row that does not exist: a check that a
  stream does not exist holds the name with an uncommitted placeholder row in
  `mt_streams`, inserted and deleted before the commit, which no other session
  ever sees and which makes a concurrent creation of the stream wait.
- **Bodies.** `jsonb` orders an object's keys, keeps the last of duplicate
  keys and refuses `\u0000`, where SQL Server's `json` keeps the order and the
  first duplicate. Each store's JSON rules are its own, a variance to record;
  the refusal maps to `INVALID_ARGUMENT`, and binary data keeps exact bytes on
  either.
- **Its own types.** Marten's "stream already exists" is not the shared
  JasperFx exception, and an append to an archived stream throws its own; it
  has no hard delete or un-archive, so `Delete` is the gateway's statement.
