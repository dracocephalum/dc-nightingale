# Design

How the pieces fit and why the store is wrapped the way it is. Read this before
changing anything under `libraries/`; the type comments say what each class
does, this page says why the classes exist at all. What is deliberately
different from the reference client is in [`VARIANCES.md`](VARIANCES.md), what
is deferred with its approach decided is in [`PENDING.md`](PENDING.md), and
what is owed soon is in [`TODO.md`](TODO.md).

## What it is

Nightingale is a gRPC event-store gateway. Applications use a client shaped
like the reference event store's client: append to a stream with an expected
revision, read a stream or `$all` in either direction, subscribe. The server
stores the events in a document and event-sourcing framework on SQL Server
(Polecat), and later on PostgreSQL (Marten), without the applications knowing
which. The contract is Nightingale's own, written in the reference client's
shape because that shape is familiar and proven; the reference's own client
is not meant to connect to this server, and nothing is owed to its wire
format, only borrowed from it where useful. The sizing target is the deployment it replaces: three nodes, about
twenty terabytes, about a hundred services connected over gRPC.

## The shape

    application ──▶ NightingaleClient ──▶ gRPC (Protocols.Grpc.V1) ──▶ StreamsService ──▶ IStreamStore ──▶ PolecatStreamStore ──▶ Polecat ──▶ SQL Server
                    libraries/client       libraries/core                libraries/server                    libraries/server (Polecat backend)

| Component | Holds | Knows nothing about |
|---|---|---|
| `core` | two assemblies: `Core`, the domain types both sides share — positions, expected states, event data and records, exceptions — with no wire package at all; and `Protocols.Grpc`, the gRPC contract (`V1/*.proto`) and the conversions between its messages and the domain types | any store; `Core` knows no transport, `Protocols.Grpc` knows only its own |
| `client` | `NightingaleClient` over the generated stub, opened from one connection string in the reference client's shape that lists the instances and carries every setting, and the mapping from wire failure reasons to the shared exceptions | the server's internals |
| `server` | the application core — the port a backend implements, `IStreamStore`, the tail every subscription waits on, `IStoreTail`, the subscription runtime, `Authorizer` deciding on a principal, and the credential and tenant managers, and, in its `Sequencing` namespace, what every backend's sequences share, the tails that keep the sequence heads and the ordinal sequencer's loop — and, in its `Grpc` namespace, the one transport over it: the service implementations, the interceptor and the status mapping | any particular store; the core knows no transport |
| `server` (Polecat backend) | `PolecatStreamStore`, the SQL Server reads and horizon the server's tails keep the sequence heads by, the statement the ordinal sequencer numbers a batch with, the schema additions, the initializer that owns the database, and the default host | the wire |

The port is deliberately small: append with an expected state, read a bounded
slice. Everything the contract adds, paging, the head-first read order, error
mapping, lives in the service above it, once, for every backend.

## The seams

Every seam below follows one rule about who does what. The store does the
heavy lifting, and the write side above all: an append's version check, the
global sequence, partitions, tenants, archiving and deleting. Those are the parts
that are hard to get right under concurrency, and the store has already got
them right; the gateway calls them and does not rebuild them. Where the store
cannot serve something on par with the reference, which so far has always
been a read, the gateway serves it itself, straight from the store's tables
through its own context, as the mirror does for every read of a stream.

What the gateway adds must stay invisible to the store's own processes. An
added column is computed or nullable and the store's statements never name
it; an added index is one the store's migration is told about rather than one
it would drop; a value the gateway writes into a column the store owns is one
the store's own writes and the parts of the store the gateway runs read the
way the store means it, and where the store's own readers would read it
differently, the gateway does not call those readers, and says so where it
writes the value. The gateway reaches into the store only through public
seams: options, registrations, overridable classes, participants in its
transaction. It uses
no reflection into the store's internals unless the store's maintainers have
said the member is meant to be relied on, and what it would rather have as a
public seam is filed in [`UPSTREAM.md`](UPSTREAM.md). The same rule holds for
the second store when its backend is written.

### 1. Events carry no CLR type

The store, like every framework of its kind, wants an event to be a .NET class
it can serialize and name. The gateway has no event classes: the client's
type name and JSON body are the whole event. `JsonEvent` is the seam. It is the
store's event wrapper over a `JsonElement`, sealed, with the type name the
client sent held in a property whose setter does nothing, so the store's own
naming pass cannot overwrite it. The body is written and read as JSON text
with `NightingaleJson.Default`, the one set of serializer options every
component uses for JSON it does not own, so the client's casing, key order,
number formatting and unicode come back byte for byte, insignificant
whitespace aside.

Metadata is a JSON object, also sent as bytes. Two keys the server
understands, `$correlationId` and `$causationId`, move into the store's own
columns on the way in and are appended to the object on the way out, so they
come last. Everything else stays in the headers column as the client sent it.

An integration test guards this seam: if a store release stops honouring the
caller's type name, or changes what a body comes back as, it fails.

An event may carry a second body, binary data: any bytes, stored as they are
sent, beside the JSON or instead of it, with `$binaryContentType` among the
metadata keys saying what they are. The store has a binary column already,
`bdata`, and a binary path of its own, which is not used: it takes one body
or the other, never both, and wants a serializer per event type, which a
gateway without event classes does not have. So the store writes every event
as JSON, an event with binary data only as its own placeholder for one, `{}`,
since its JSON column takes no null, and leaves the binary column null.
`BinaryDataWriter` then sets the binary column of the rows the store just
inserted. It is a participant in the append's transaction, the store's public
seam for statements of a caller's own, which the store runs after all of its
own statements and before its commit: one statement for the whole append,
each row found by the position the store has read back and checked by its
id, tenant and stream, refusing, and so rolling the append back, when it finds
fewer rows than events. The store's queued commands would have been the
simpler seam, but they bind every value as text and refuse bytes. Only an
append that carries binary data has the participant, and its statement has
parameters of its own, so the limit on events in one append is unchanged.

The value the participant writes is one the store's own readers read
differently: to them a row with binary data is a binary event, and without
a serializer for its type they refuse it. The gateway does not call them.
Every read of events goes through the mirror, which reads both columns, and
the check that recognises a retried append reads the ids through it too,
rather than through the store's fetch of a stream. An integration test holds
the store to the order the participant relies on: the participant finds the
row the append inserted, and a participant that refuses leaves nothing of the
append behind.

An append to several streams is the store's too: one session holding every
stream's append, each under its own expected state, which the store writes
in one transaction and positions stream by stream, in the order given,
writing each stream as a command of its own, so the parameter limit binds a
stream's events and not the total. What it adds is checks on streams the
append does not write, and those are a second participant,
`StreamStateChecker`: one statement reading the checked streams' rows under
an update lock held to the commit, a range lock where a stream has no row,
so no append to a checked stream, and no stream begun under a checked name,
can commit between the check and the append's commit. The store has a check
of a stream without events of its own, but it reads without a lock, which an
append landing in between would pass. Only an append with checks has the
participant. When the store refuses, the gateway reads every stream named
again, in one statement through the mirror, and reports every condition that
failed rather than the first the store met, the failure path paying for
what the success path never does. A metadata set in such an append is
decided as a set of it alone is, on its metadata stream as read just before,
and written as its events under that stream's expected revision; a reset is
not a set there, since the value it writes depends on its stream's last
revision, which in the same commit would include the commit's own events.

### 2. The schema is patched, not replaced

Category, event-type and correlation streams (`$ce-orders`,
`$et-OrderCreated`, `$bc-checkout-1`) and the listings of streams
(`$streams`, `$category-orders`) are predicates over the events table, not
link streams. For them to be seeks rather than scans the table needs a
persisted computed `category` column and filtered indexes: by category and
by type, over every live event; by correlation, over the events that carry a
correlation id only, so an append without one writes nothing to it; and two
over the first event of each stream only, one row a stream, for the
listings, so only an append that begins a stream writes to them. The store's
migration drops any column or index it does not know about, so they cannot be
applied by hand; they have to be part of the definition the store migrates
from.

The category is one rule, the same in the column and in process, where the
router matches live events by it (`StreamNames.CategoryOf`): the name up to
its first hyphen, when one follows at least one character, and the whole name
otherwise. A category is never empty, and a stream named just `orders` is in
category `orders`, so `$ce-orders` and the category filter `orders` hold it.
The prefix filter `orders-` does not: it is answered by the same index, the
category's equality, and takes the stream named just the category away again,
since that name does not begin with the prefix. The rule is not configurable:
a different rule would rebuild the column and every category index and
renumber every category ordinal.

`AugmentedPolecatDatabase` is that seam. It is the store's database class with one
override: when the store builds its feature schemas, the event-store feature
is wrapped in `PatchedEventStoreFeature`, which adds the column and indexes to
the events table, and on a store initialized with ordinals the two ordinal
columns and their filtered indexes (seam 6), and the gateway's own tables are
added as a feature beside it. `SingleTenancy` exists only to hand this database class
to the store, because the store's default tenancy always builds the plain one.

The virtual streams are read straight from the table, `VirtualStreamReader`,
because the store's query surface knows nothing of the added column and
reaches the type column only through its own type registry; the reader's SQL
is what the indexes were declared for, and an integration test holds its
rows to the same bytes the store's own path returns. Another asks the
database itself, through its count of seeks and scans on each index, that
the bounds and counts of a correlation and of the listings seek their
indexes and never scan them.

The store's automatic schema management is off. Its table ensurer rebuilds
the event-store definition from the options on first use, bypassing the
tenancy's database, and would drop the additions. The initializer applies
the schema instead, through the patched database. A second integration test
guards this: apply the schema twice and the second delta is empty.

The store's own `Database` property keeps the plain definition; it cannot be
replaced without forking. Two things read it. Tenant partition provisioning
only splits partitions through it, which the tenant-partitioning test shows
leaves the augmented schema without a delta. The store's schema export would
write the plain schema, so the export is the gateway's: the default host's
`--export-schema <file>` writes the script the server would apply, built
from the augmented definition without connecting to anything.

### 3. The server owns the database

A host never configures the store; it names a database. The initializer,
`StoreInitializer`, runs at startup before any request is served, under one
rule: the server initializes only an empty database, and never changes a
store while serving it unless told to. What a start may do is
`Nightingale:SchemaMode`: `Verify`, the default, checks and refuses, since a
server assumes its database is ready the way a service assumes its migrations
ran elsewhere; `Migrate` also applies what an initialized store is behind on;
`Initialize` also creates and initializes, and is idempotent, so a host may
leave it on. The default host's switches do the same work once for a
pipeline.

Two mechanisms keep the database's tables, and they are kept in one order.
The event store's tables, with the gateway's additions to them, are compared
by the store's own schema tooling, which works from the difference between
the real tables and what this version of the store expects: a comparison,
not a version number, so a change the store's library brings with an upgrade
is found whether or not anyone expected it. The gateway's own tables are
compared by its migrations (seam 7), whose history says which have run. The
store's go first, because the gateway's tables are about a store that
exists. Neither tool applies: each writes the statements it would run, and
the server runs them itself, one batch at a time, the store's per object as
the tooling would and the migrations' per `GO` of their idempotent script,
without the transaction the migrations' tooling wraps each migration in. So
what the report prints is exactly what the apply runs, each batch is logged
as it starts and when it ends with how long it took, a batch that creates or
alters an index runs under `Nightingale:IndexMigrationStatementTimeout`, an
hour by default, rather than the connection's command timeout, and a batch
that fails is named. Every batch waits for a table's lock no longer than
`Nightingale:SchemaLockWaitTimeout`, thirty seconds by default, then sleeps
as long and tries again, six times before giving up: a statement queued for
a schema lock behind a long reader has every later reader and writer queue
behind it, so a schema change on a system being served must never wait
unboundedly, and the sleep between tries lets the queue drain. When online
index builds arrive (`PENDING.md`), the same setting becomes the index
statement's own low-priority wait. A connection that fails under the apply, as the store's
tooling judges such failures, has the whole apply begun again, which is safe
because both tools apply only what is missing. `StoreSchema` is the one
place that reports what is pending in both and applies it; startup, the
default host's switches and a host's own code all go through it.

- Every start takes the schema lock, an application lock named for the
  gateway in the database, on a connection of its own that is never pooled,
  so the lock lives exactly as long as the work and dies with the process:
  shared to verify, exclusive to change. Instances starting together
  therefore do the work once, the second finding it done under the lock, and
  a verifier never reads a database another instance is half-way through
  changing. Creating the database is the one step no lock can cover, and a
  database another instance created first is taken as made.
- A database that does not exist is created, with the configured collation,
  in `Initialize` mode; the other modes refuse, naming the mode and the
  switch.
- An empty database, created by the server or provisioned by hand, gets the
  store's tables and then the gateway's while every table is empty, so no
  index is ever built over data, and then a row per property of the store in
  `StoreProperty`, written in one transaction with the default tenant's row:
  the settings below that shaped it, with the actual collation, the version
  of the store's library and the hash of its creation script, then when and
  by which server version it was initialized. The last row is what the next
  start reads as "initialized", so a record is complete or absent, never
  partial, and a start that died anywhere before it leaves a database that
  `Initialize` finishes and the other modes refuse, saying so.
- An initialized database is refused when it has a migration this server
  does not know, because the store is then newer than the server; refused
  when the configuration contradicts a setting it was initialized with; and
  refused when either set of tables is behind what this server expects, with
  what would be applied in the message, in `Verify` mode; the other two apply
  it, then record the library and the hash again. A store whose tables are
  current under a record that is not this server's, after an upgrade that
  changed no table, or a start that died between applying and recording, is
  served by every mode and has its record rewritten by the two that may
  write.
  The comparison of the store's tables is the slow part of a start the first
  time the database server compiles it, about ten seconds for the eighty
  catalog statements the store's schema library sends, and a fraction of a
  second after. Every apply records the hash of the store's creation script,
  which covers the store library, the schema library, the gateway's additions
  and the settings; with `Nightingale:FastBoot` on, a start that finds its own
  hash recorded skips the comparison. It trades away noticing a change made
  to the tables outside the server, so it is off by default and the report
  and the apply never use it. The integration tests that are not about
  initialization make their store from the creation script and start with
  fast boot on; one test holds a store made that way equal to one the server
  initialized, catalog and recorded rows.
- A database with tables and no record of a store is refused outright, it is
  not ours, unless every table is one the server creates and every one is
  empty but for the migrations' history: that is what a start that died
  before its record leaves, and both halves apply only what is missing, so
  the next start in `Initialize` mode initializes it the rest of the way,
  with a warning. A table that is not the server's, or one with rows, still
  refuses.
- What a start applies is logged under `StoreSchema`'s category, both
  halves alike: an entry as each batch starts, with the batch, one as it
  ends, with how long it took, and an error naming the batch when one fails.

The same report and the same work are there without starting the server:
`--schema-report` on the default host prints what the database needs and
exits with 1 when it is not current, `--apply-schema` does what `Migrate`
does and `--initialize` what `Initialize` does, whatever the setting says,
and a host of its own calls `GetNightingaleSchemaReportAsync`,
`ApplyNightingaleSchemaAsync` and `InitializeNightingaleStoreAsync`.

The settings under `Nightingale:Store` are the ones fixed at initialization
because each shapes the store's tables or where they live: the schema they
are in, `dbo` unless said otherwise; the collation, which must be a UTF-8 one so
that a name holds any character, and which decides whether names that differ
in case are the same name, binary and so case-sensitive unless said otherwise; the partitioning mode, one of none, by
tenant, which gives each tenant its own sequence, or by the archived flag,
which keeps deleted streams' events out of every live read, one mode rather
than one switch per scheme because a table has one partition scheme; and
whether ordinals are assigned (seam 6). Changing any of them means a new
database, and the store's property rows record each so a changed
configuration is refused rather than served.

The table is called `StoreProperty` because not every row is a setting: some
are what the store was initialized with, `Partitioning`, `Collation`, and the
rest are facts about it, `CreatedAt`, `StoreSchemaHash`. Every row is a name
and a value, one level: a setting is a single value, never an object or a
list, so its row's name is its member's name with no path in front, and a
test holds the settings type to that. The setting rows
are read by the configuration binder into `StoreSettings`, the base of the
type the configuration binds into. Both are seen through one interface,
`IStoreSettings`, which every backend's settings carry, and the comparison
is written against it, property by property, and the table never changes shape: a store older than
a setting has no row for it and reads as the default, and a row a newer
server wrote is ignored by an older one.

### 4. Positions are the store's sequence

A stream revision is the store's version minus one, so revisions start at
zero as the reference client expects. A position is a number from its store
partition's sequence, one signed 64-bit value. `$all` and the virtual streams are
predicates over it, so their positions are sparse: monotonic, but with other
streams' events between. A virtual stream ends at its own last event, never at
the sequence head.

Sequence numbers are assigned before a transaction commits, so a forward read
of `$all` or a subscription must stop at the sequence head, which advances by
polling; per-stream reads are unaffected because versions are dense under the
stream lock. The head is the gateway's own, for every store partition and
every backend: `SequenceTails`, in the server's `Sequencing` namespace, one
poller per process for each journal, keeps a head per store partition, the
shared sequence or each served tenant's, and every subscription waits on it rather than polling. The store's
own high-water agent is not run: its rule gives a gap up after 3 seconds,
whatever holds it, and where readers read past an open transaction an append
open longer than that commits below a mark that has passed it, never to be
delivered by position. The gateway's rule is a `HorizonCheckpoint`: when a gap
is first seen, the highest position seen at that look and a marker of the
journal's transaction horizon taken after it; once the horizon has passed the
marker, every position up to that ceiling is final, those seen committed and
the missing ones never to be, so the head moves to the ceiling at once, the
whole span with it, and extends from there; while a transaction that began
before the marker is open, it waits, however long, and names the transaction
in a warning after half a minute. A rolled-back append is given up at the next
look rather than after a threshold. How the checkpoint is judged is the
`HeadStrategy`, chosen once when the tails start and kept for the life of the
process, the first the login may read. On SQL Server, first the horizon of each
store partition, when the login has `VIEW SERVER PERFORMANCE STATE`: a gap is
held only by a transaction that has written to the database since before the
marker and holds a lock in the gap's partition of the events or streams table.
An append writes its stream's row, in its tenant's partition, before it draws a
position, and keeps the lock to its end, so nothing that could still fill the
gap is missed, and a transaction in another tenant's partition, or on tables
that are not the store's, holds nothing. It asks in two stages: the
database's writers, one cheap read, which settle most gaps, since the writer
is most often the append a moment from its commit; then the locks, a walk of
the server's lock manager, only for a marker older than a second while a writer
older than it is open, one read for every gap held. Next the transaction
horizon of the whole database, `DBCC OPENTRAN`, with that permission or
`VIEW DATABASE STATE`, where any open writer holds every head. Otherwise the
best-effort strategy, a horizon that has always passed with the store's 3
seconds as its minimum age, over reads that take the events table
`WITH (READCOMMITTEDLOCK)`, so they wait on an open append's row even under
read committed snapshot and the wait never starts for it; only a position whose
row is not yet in the table when the wait ends can be skipped. A look marks
once for every gap it first sees, and asks the horizon once for every gap it
holds. None is required: each step down is logged with the grant that would
avoid it, the last is the store's own rule made safer, and a grant takes effect
at the next start. The head is kept in the gateway's `SequenceHead` table, a row per
sequence raised by every instance and lowered by none, its position the row's
concurrency token: a tail begins there rather than at the start of the table,
a reader of a replica takes it as the bound of what the replica holds, and the
time checked moves at every look, so a head that has caught up is told from one
nobody keeps. An append made through this process does not wait for the poll:
every stream store pulses `AppendSignal` once its append has committed, and the
poller waits on that signal between looks. Only the appending instance is
woken; the others find the event at their next look, again after the store's
fast polling time, a quarter of a second, while a look left a head short of
what is already seen past it, a head catching up or a gap waiting on its
checkpoint, most often an append a moment from its commit; after
`Nightingale:TailPollingInterval` once every head has caught up, a second by
default. So a subscriber on another instance than
the writer is at most that interval behind, measured at a median of half of it
on a quiet store and the whole of it while events flow, since every look then
catches up; the setting buys it down, a quarter of a second for four small
queries a second per instance, and a signal across instances, Service Broker
or query notifications on SQL Server, was not worth its machinery for a
quarter of a second that a setting already gives. Under tenant partitioning
there is no global sequence, each tenant has its own and its own head, so the
wildcard read across tenants is refused there and the tail is per tenant:
`ITenantStores` hands a scope its tail beside its stores, the one tail while the
sequence is shared and the tenant's own under a sequence per tenant. One read
per look finds which followed sequences moved, and only those are read. A
backend supplies the reads, `ISequencePositions`, and the horizon; the rules
are tested in the server against positions and a horizon written by hand.

### 5. A group runs in one place, on a lease

A persistent-subscription group is a checkpoint the server keeps and a set of
events in flight to its consumers. The checkpoint, the group's settings and its
parked messages are rows in the gateway's own tables, through the `ISubscriptionGroupStore`
port and its one implementation, `SubscriptionGroupStore`, over the context of seam 7;
the in-flight events, their retry counts, their deadlines and which consumer
holds each are in memory, in `SubscriptionGroupRuntime`, in the one instance
that holds the group's lease. The runtime is one per group per instance, one
reader and one dispatcher feeding every consumer connected there by the
group's strategy, round robin, pinned by a hash of the stream name, or a
single consumer with the others standing by; `SubscriptionGroupRegistry`
seats the consumers, the first to arrive starting the runtime under the lease
and the last to leave stopping it, and `SubscriptionGroupHost` keeps the
lease renewed, redelivers what timed out and writes how the group stands for
as long as any consumer is there.
The lease is a row that only a free, expired or already owned lease lets an
instance write, two instances racing for it told apart by the row's
concurrency tokens, renewed while a consumer stays, released when the last
leaves. The same tokens fence everything a running group writes: its
checkpoint, a parked message, a message off the outbox, how it stands. Each
such write reads the lease, checks it is this instance's and unexpired, and
saves the lease row with it, unchanged but token-checked, in one transaction,
so an instance that stalled past its lease while another took the group over
writes nothing when it wakes; the refused write ends its consumers' calls
with who owns the group now, the same answer a refused renewal gives. The
checkpoint never moves back. A group over `$all` may carry a filter, fixed at
creation; its feed then comes from the store's scan a stretch at a time, and
a stretch the filter keeps nothing of travels through the feed as a marker,
after the events before it, so the runtime moves the checkpoint over it once
everything delivered before it is done, and a consumer coming back reads
nothing twice. The lease carries the owner's address, derived from what the owner
listens on unless configured, and an instance asked for a group another runs
refuses with that address; the client goes there itself, once, and keeps the
connection, the way the reference client follows a not-leader answer to the
leader. That is the whole of the cluster: no gossip, no registry, no relay,
the database the only coordination point. A replay is refused the same way
while a consumer is connected elsewhere, since only the instance running the
group can wake it. A relay inside the cluster, for a client that can reach
one load-balanced address only, would sit on the same lease-carried address
and is not built. The checkpoint is the last position every
delivered event up to which is done: acknowledged, skipped, or parked. Parked
messages are rows, one per event, so one can be replayed by itself; the
reference keeps them in a stream and can only replay them together. A parked
row carries every number its event has, the global position as its key, the
revision within its stream, and the ordinal when the group is numbered by
ordinal, so a replay addresses it by whichever number the group speaks. A
replay is a move, the shape of a service bus's dead-letter queue: the row
goes from parked to the group's outbox, a message is always in exactly one of
the two, and a delivery that fails again moves it back with its new reason
and count. Delivery has the reference's shape: one dispatcher counts each
consumer's slots and serves a list of retries before the live buffer, so
what is due on the outbox, queued as retries when the group starts and
whenever it is woken, which the replay call does through the registry
when a consumer is connected to the same instance, goes out ahead of the
next event the stream would have sent; the events already in flight complete
as they are. A message stays on the outbox while it is in flight, so a wake
in the meantime never delivers it twice. The outbox row has a due time, so a
retry with a delay is the same row with a later one.

`IStoreTail` is the seam for liveness. One poller per process for each journal,
`SequenceTails`, keeps the sequence heads and publishes every advance; each
subscription waits on it instead of polling the store, so a thousand
subscribers cost the store one poll per interval. A
subscription confirms with the head, catches up in pages, says so, then
delivers whatever each advance brings, through `StreamRouter`, one per
tenant's store, which reads each advance once for every subscription that
follows it. It reads where the advanced range's events are once, their
stream, revision and type without their bodies, matches them in process
against every followed stream, category and type, and reads the events of
each stream that matched something; when `$all` is followed, one read of the
range with its bodies serves everyone instead. So an advance costs reads in
the number of events, not of subscriptions nor of names followed. A
subscription follows the router before its catch-up reads, so an event the
reads do not see yet is one the router brings. A plain stream's subscription
delivers only in revision order, reading its stream again when the router
brings something ahead of its next revision; one by position skips what it
delivered already. A follower that leaves too much unread lapses and catches
up itself from where it stands, the reference server's own answer to a full
live buffer. A subscription to `$all` under a filter catches up through the
store's scan, a stretch of positions at a time, the prefixes the database's
predicate through the category and type indexes and a regular expression
matched in process, and then keeps, of what the router brings, what the
filter keeps; positions examined without an event to deliver are counted,
and at the filter's interval a checkpoint carries the last of them, so a
consumer's position advances over a quiet stretch (`VARIANCES.md`,
*Regular-expression filters*). One router per tenant is the one shape that fits both tenant
modes: a stream name is per tenant, and under a sequence per tenant so is
the tail.

Matching in process means the server compares names, which the database
does everywhere else, so the two must agree. The category of a stream is one
rule in two places, the store's persisted column and `StreamNames.CategoryOf`,
held equal by an integration test. The comparison itself comes from the
database: at every start the initializer reads the database's collation and
what it ignores, `COLLATIONPROPERTY`'s comparison style and locale, and
`NameComparison` builds the nearest comparer, which the router matches with.
Under a binary collation, the default, that is ordinal and exact. Under a
linguistic one it is the same flags, case, accents, kana, width, on .NET's
engine rather than SQL Server's, whose weights differ in corners, so there a
name could match on a read and not live or the reverse; `VARIANCES.md`
records it. Should that corner ever matter, the exact alternative keeps its
shape here: hand the store every followed name in one query, a table-valued
parameter or an `OPENJSON` list joined to the range on stream, category and
type, and let it return each event with the followed name it matched, spelled
as the follower spelled it, so the server's part is a lookup and never a
comparison. One range read per advance, exact under any collation, at the cost
of a parameter list the in-process match does not need; both could coexist,
chosen per host, if a collation ever demands it.
Where now matters, a subscription from the end or a bounded read of `$all`,
the tail refreshes itself first with one query through the committed rows that
follow the head without a gap, so an append that has returned is never on the
wrong side of "now". It never passes a gap; giving one up is the poller's
call, by the head strategy (seam 4).

### 6. Ordinals are assigned after commit, by one sequencer

A virtual stream's positions are sparse, so two of them subtracted are not a
count. The reference gets dense numbers from link events with their own
revisions; ours are rows of `OrdinalLink`, on a store initialized with
`Nightingale:Store:AssignOrdinals`: one for each event in each numbered
virtual stream it is in, its type's and its category's always, its
correlation's when it carries one, and the listings' when it begins a
stream. They are not columns of the events table: a store provider's
migrations drop the columns they do not know, which rules columns out on
Marten, and on PostgreSQL every update of an event's row writes the whole row
again, in every index; one design serves both backends, and the events table
is the same with ordinals and without. The setting is recorded in the marker
row all the same: a store initialized without it refuses it until a backfill
exists, and one initialized with it refuses to run without.

The table is an extension of the events table, not an entity of the
gateway's: what it holds describes the event's rows, and it sits in a table
of its own, in the gateway's schema, so that the store's migrations never
touch it. So it is keyed as the store's data is, by what it describes: the
type, stored as a byte and numbered by how many links each makes, the event
type first; the tenant; the key, as wide as an event type; and the ordinal,
with the event's position beside them. A read by ordinal seeks that key in
order and fetches each event by its own key, in the tenant's partition on a
store partitioned by tenant, an index seek and a lookup a row; a unique index
by type, tenant and position means no batch can link an event twice. Rows are
never removed: a link whose event was archived or deleted is a hole the read
skips, and the last ordinal of a key stays, so none is reused. Every event has
its type's link, written in the same batch as its others, so the type's link
is the witness that an event is numbered.

`OrdinalSequencerHost`, in the server's `Sequencing` namespace, hosts the
sequencer for every backend, its lease and its loop, with only the batch the
backend's own (`IOrdinalSequencer`, `SqlServerOrdinalSequencer` on SQL Server):
one per journal in the cluster, on the same lease rows the persistent-subscription
groups use, walking the events table behind the sequence head in batches, by its
key, and giving each event its links, each
batch continuing from the last ordinal of each key found with one backward
seek in the links' key. A batch is the range from the progress to its end,
read once, into a table variable, so every link is made from the same rows.
The append path is untouched, because numbering at append time would
serialize every append to a category. A batch and the progress row, the
position numbered through, are one transaction, and the progress row is read
under an update lock, so a sequencer whose lease overlapped a takeover
continues from what the other committed. A range numbered is never looked at
again: the tails pass a missing position only once it is final (seam 4), so
nothing commits below the head once it has passed, and nothing below the
sequencer's progress is unnumbered. The price is lag: ordinals trail the
sequence head by the sequencer's cadence, which is the tails', and a removed
event leaves a numbered hole the reader skips and the sequencer never reuses,
which is what a link whose event is gone does in the reference.

The sequencer is running, disabled or faulted, and a client never waits on
one that is not running. Disabled is the operator's switch,
`Nightingale:Store:OrdinalSequencerEnabled`, each instance's own: off, the
instance runs no host and stops the numbering of every sequence it follows
with the reason, so every read and subscription by ordinal is refused with
`SEQUENCER_DISABLED`, not worth a retry, and a subscription already waiting on
the numbering ends with it; on again, the sequencer goes on from its progress.
Faulted is a sequence's own: a batch that fails is tried again at the next
pass while the other sequences go on, and after three failures in a row the
host stops that sequence's numbering with the cause and records it on the
sequence's progress row, which the follower on every other instance reads
with the progress, so every instance refuses with `SEQUENCER_FAULTED`, worth a
retry after a back-off. The first batch that succeeds lifts the stop and
clears the row, and a host that takes the lease over clears what an earlier
holder recorded as it numbers again. One sequencer numbers every ordinal, so
the system's virtual streams stop and start together; KurrentDB stops its
system projections one by one (`UNSUPPORTED.md`). The server features call
reports the state for the caller's sequence.

The numbering is the caller's explicit choice per read or subscription,
global by default, and the service refuses an ordinal read of `$all` and
`$de`, and with `ORDINALS_NOT_ENABLED` on a store without the
feature, before anything is delivered. Under ordinal numbering every number in
the conversation is an ordinal and each event carries its ordinal beside the
position it always carries; a consumer keeps the numbering with its checkpoint
and never resumes under the other. A bounded read is a snapshot of what is
numbered and needs no head, because a batch is atomic. A subscription waits
on the tail like every other, then looks for newly numbered rows; while the
sequencer is behind the head it woke for, it waits for the numbering to reach
that head rather than for an advance a quiet store would never bring. The
tail carries the numbering beside the head, as the process knows it: the
instance that numbers publishes each batch as it commits, and every other
instance follows the progress rows with one reader per process, on the
tail's cadence and only while a followed sequence is behind, so no
subscription polls the store for the sequencer. A
persistent-subscription group chooses its numbering once, when it is created,
and keeps it for life: its start, its checkpoint and the key of a parked
message are all in that numbering, so a consumer resuming the group can never
mix the two, and one that wants the other numbering creates another group.
Ordinals are per tenant by construction, the sequencer partitions by tenant and
the indexes lead with the tenant column, so a read across all tenants has no
single ordinal sequence and is served under global numbering only. Under a
sequence per tenant the sequencer walks each tenant's sequence in turn, behind
that tenant's own mark, with a progress row per tenant, and a batch is one
tenant's rows, since a position is only unique with its tenant there.

"Ordinal" here means these numbers and nothing else. The store partitioned by
tenant has a number of its own by the same name, the `tenant_ordinal` column
and the `ordinal` of its `pc_tenant_partitions` table: the value of the
partitioning column, which the store gives each tenant, or each bucket of
tenants, and from which SQL Server's partition function finds the partition.
The gateway calls it the tenant's partition key, not its partition number,
which in SQL Server is the slot the function maps a value to (value `k`
lands in partition number `k + 2` under the store's boundaries). The store's
column names appear only where the mirror maps them, under a property named
`PartitionKey`, never in a name, message or document of the gateway's own as
an ordinal. A sequencer is named for what it sequences: `IOrdinalSequencer`
numbers every ordinal in one batch, and its progress rows are named for the
ordinals they track. A sequencer of another kind, for a virtual stream added
after a store is numbered far along, would need a type on its progress rows
and a lease of its own.

### 7. The gateway's own tables are one model, and its own schema

A group's settings are columns of its row, a value each: its start, its
timeouts in milliseconds, its bounds, and its numbering by name. A group is
one of many rows of one shape, which is what columns are for; they are read
and compared in the database, and changing one is a column update. The
store's properties are the other case, one object with a few facts, and are
a row each.

A group's row has three kinds of column: its settings, its state, the
checkpoint and when it was created, and a snapshot of how it stands while
it runs, the columns named `Live`, written with each renewal of its lease
and cleared when its last consumer leaves. The snapshot is what lets any
instance list every group with its live numbers. It is read only under a
held lease and nothing decides by it.

The tables the gateway keeps, subscription groups, their parked events and
outbox, leases, the sequencer's progress and the store's properties, are one EF
Core context, `NightingaleDbContext`, in the server library. The context owns
them, under the repository's EF rules for a context that does: a `Guid`
key named `Id` on each, singular table names, natural keys as unique
indexes, a check that fails a test when a line is forgotten. A column that
refers to a row of another table is named for that table, `SubscriptionGroupId`;
when the table is the gateway's own the column is a foreign key to it, never
cascading, and the check holds the name and the key to each other. When the
table is the event store's, the column keeps the name and takes no
constraint: the store's tables are the store's, an event's id has no unique
index to refer to there, and the store deletes events of a tombstoned stream
whatever refers to them. Such a column is listed by name where the check is
run, and anything that joins to the store's events joins on the position,
which is their key. Two exceptions are stated where they apply, both on the
ordinal links (seam 6), which extend the store's events table rather than
hold anything of the gateway's own. The table is keyed by what it describes,
as the store's data is, and carries no id: an id would be one more thing
the database invents for every row, since the sequencer writes a batch of
links in one statement no code sees row by row, on a table that grows with
the events. Its class carries `[StoreExtension]`, and the check exempts such
a table from the rule of one `Guid` key, and from that rule only. And its
type is stored as a byte, not a name, because it leads the key and the index
of every link: its enum carries `[StoredAsNumber]`, the check accepts a
numeric column only for such an enum and only as its own width, and a test
pins the numbers, which may never change or be reused. It names no
provider and no column type, only lengths and whether a string is Unicode,
so each backend generates its own migrations from the one model, with
`dotnet ef migrations add`, into its own project: SQL Server's are in the
Polecat backend, and the Marten backend brings PostgreSQL's for the same
context, not a second model. A second test fails when the model has changed
and no migration carries the change.

The tables live in a schema of their own, `nightingale` unless
`Nightingale:Schema` names another, with the migrations history beside
them, so the gateway's tables and the event store's never share a
mechanism or a history; the schema may be the store's own, the names do
not collide. Generated migrations carry the schema as a literal, and a host
may name another, so the operations are moved to the host's schema as they
run, by the backend's migrations SQL generator; the generated code is never
edited. A model is cached per context type with its schema in it, so the
schema is part of the cache key: one process serving two schemas would
otherwise read the first one's tables through the second context. The
mirror reads the links from the gateway's schema beside the store's, so
both are in its key.

A group's key is an id made with the group, and its tenant, stream and
group name sit under a unique index: the names find a group, by whatever
rule the database's collation has for which names are the same, and the id
is the group. Its parked events, its outbox and its lease go by the id, so
nothing downstream of the lookup depends on how a name is spelt or compared.
A park that arrives for a group deleted meanwhile is refused by the foreign
key and taken for what it is: the group is gone.

The backend has a second, read-only context beside it, a mirror of the
tables the store owns that the gateway reads: its events, with the columns
the gateway adds, its streams, and the registry of tenants' partition keys;
and, from the gateway's own schema, the ordinal links, which a read joins to
the events, and the sequence heads, which bound a read of a replica. The reads
by category, by event type and by position are LINQ over that mirror, and so
is the tails' look for the unbroken run of positions after a head, whose gap
rule is plain code; the best-effort strategy's locking reads are the same
LINQ composed over the table taken with a hint. A query names the columns it
needs, never a whole row, because the partition key exists only on a store
partitioned by tenant; and the
mirror declares the store's column types exactly, since a name sent as
Unicode to a column that is not makes the database convert the column and
scan the index it should seek. One test holds every mapped column to the
store's own definition, another asks the database for its count of seeks
and scans on the index. The mirror refuses to save, and exists as two
types over one mapping, `EventsDbContext` on the main connection and
`ReadOnlyEventsDbContext` on the read-only one (seam 8), each with a
registration of its own, so a reader asks for the connection it means by
type.

A plain stream's read is the same LINQ, with its metadata applied in the
statements the read already ran: the first finds the stream's row and, in
the same statement, its last metadata event, a `TOP 1` seek backwards on
the store's unique index of a stream's versions, so a stream without
metadata costs a few index pages and no round trip; the second reads the
page from the bound `max(last − $maxCount + 1, $tb)` as the start of its
seek on that index, with `$maxAge` checked against the database's clock,
`SYSUTCDATETIME()`, the clock that stamped the rows, before the page is
counted, so a short page still means the end. The head's first revision is
that bound. Nothing is cached: every instance appends, and a cache would
learn of a change only through the tail, a polling interval late, serving
meanwhile events a new `$maxCount` has cut; if the probe ever costs too
much, the way out is a store setting fixed at initialization that refuses
metadata and skips it, not a cache. On a store partitioned by tenant every
statement of the read names the tenant's partition key, through a subquery
of the store's registry, since the unique index leads with it and a
predicate on the tenant alone eliminates no partition; the tenant stays in
the predicate because tenants can share a partition, and no second index is
added, since it would cost every append a second write. An integration test
forces each statement onto the index by the key and the stream, and holds
that the same statements without the key are refused (error 8622).

What stays SQL text, because its shape is the point or there is no table
to map: the sequencer's batch, which numbers thousands of events in one
set-based statement under a lock where row-by-row writes would be an order
of magnitude slower; and the initializer's questions of the catalog and
its creation of the database.

The group store's plain reads and writes, the reader's queries and the
store's property rows run on the in-memory provider in unit tests. That provider
has no transactions, no unique indexes, no collation and no filtered
indexes, so it stands in for exactly those reads and writes and for nothing
else; the lease is written so that it needs nothing else, an optimistic
write settled by the row's concurrency tokens, and the integration suite
still runs the same tests on SQL Server.

### 8. History is read from a read-only connection

The backend opens a second connection, read-only, and by default it is the
main connection string with a read-only application intent: a listener with
read-only routing sends it to a readable secondary, and any other server
ignores the intent, so a standalone server pays a second connection pool and
nothing else. The store has no read and write split of its own; this one is
the backend's.

A secondary applies the log after the primary commits, under synchronous
commit too, so it holds a prefix of the events. Reading it bounded by the
primary's sequence head would skip, silently and inside a subscription,
whatever it has not applied yet. The head, kept in the gateway's table, is the
mark: it is replicated with the events, and it is written after the events it
covers, so everything at or below the mark
*as the secondary reports it* is on that secondary. A page of `$all` or of a
virtual stream by position is therefore asked of the read-only connection
together with its mark, in one round trip so both come from the same server:
the part of the page at or below that mark comes back, and the main
connection supplies whatever lies above. The page is the one the main
connection alone would have returned. A reader working through history is
served by the secondary; a reader at the head is not sent there at all, a
position above a mark seen within the last second going straight to the main
connection; a secondary that fails is left alone for ten seconds.

What a deletion changes is the one thing that can differ: an event archived
on the primary a moment ago may still be read from the secondary, as it
would have been by a reader a moment earlier.

Bounded reads of a plain stream stay on the main connection, because a
client reads what it has just appended and appends on what it read. A host
may move them, `Nightingale:ReadStreamsFromReadOnlyConnection`: they are
then eventually consistent, nothing lost or reordered, and the cost is a
revision conflict where the read was behind, since an append's expected
revision is always checked on the main connection. Subscriptions, heads,
ordinal reads, every write, the leases and the sequencer use the main
connection whatever is configured.

### 9. A call is authenticated by scheme and authorized by role and tenant

`AuthenticationInterceptor` runs before every service method, reads the
`authorization` header and hands its parameter to the handler for the
scheme: `BasicAuthenticator` today, a token validator when the Bearer
scheme comes. Whatever the handler, the result is one
`NightingalePrincipal`, a name, a role and the tenant the credential is
bound to or none, left on the call; the services never learn how the caller
was authenticated. Each service method then asks `Authorization`, the gRPC
face of `Authorizer`, for a role and a kind of access, and gets back the
`TenantScope` the call works in: the credential's own tenant, or the one a
global credential named in `nightingale-tenant`, resolved through
`TenantDirectory`. `Authorizer` decides on the principal and on the tenant
the transport read, and refuses with the shared exceptions;
`NightingaleErrors.TryTranslate` turns those into the statuses the contract
promises, once, for every service. Credentials and tenants are managed the
same way: `CredentialManager` and `TenantManager` hold every rule and know
no transport, and `CredentialsService` and `TenantsService` parse the wire,
hand them the caller, and write the answer back. The directory
maps a tenant's id to the id the store keeps on its rows, which never
changes and is kept on first use, and whether it is disabled, which is read
again on a cadence by `TenantRefresher`; a tenant found disabled has its
running groups failed here. Credentials are rows with a salted, peppered
PBKDF2 hash in PHC form (`PasswordHasher`), the built-in administrator is
configuration, and a verified name and password are remembered per
instance for a short while, keyed by a digest of what was presented. The
scope is what a call resolves its stores through: `ITenantStores` hands out
the stream store, the group store and the tail of the tenant, one of each per
tenant served, made on first use over the one document store and the one set
of gateway tables — the Polecat stream store opens every session for its
tenant, the group store reads and writes its tenant's rows, the tail is the
process's one tail while the sequence is shared and the tenant's own under a
sequence per tenant (seam 4), and a running group resolves its own tenant's
stores and tail from the tenant its row carries, so a disabled tenant stops
its groups alone. The wildcard resolves
to a stream store with no tenant, which reads `$all` and the virtual streams
across tenants through the mirror of the events table and refuses a stream
name, which is per tenant; it exists while the sequence is shared, which
`ServerFeatures` says. A host without a backend registers `TenantStores`,
which hands every scope the one store it has.

### 10. The transport is an adapter over the core

Ports and adapters, with the arrows pointing at the domain and nowhere
else. `Dracocephalum.Nightingale.Core` is the domain both sides share and
references no wire package; the compiler holds it to that. The gRPC
contract is its own assembly beside it, `Protocols.Grpc`: the `.proto`
files, the generated code, and the conversions between messages and domain
types. The server's application core — stores, runtime, `Authorizer`, the
managers — takes a `NightingalePrincipal` and core requests and throws the
shared exceptions; everything that touches `Grpc.Core` or the generated
types lives in the `Server.Grpc` namespace, one file per service, each a
bridge that parses the request, calls the core, and maps the answer or the
refusal. The client's public surface is the connection string and the
domain types; its gRPC channel, stubs and status mapping are internal.

The rule for what crosses the seam: small scalars are copied into core
records, payloads are `ReadOnlyMemory<byte>` views over the bytes the
transport received, and collections are streamed, never materialized at
the boundary. A second stack — another IDL with its own RPC — is a sibling
`Protocols.<Stack>` assembly and a sibling `Server.<Stack>` adapter over
the same core, and the `Server.Grpc` namespace becomes an assembly of its
own when that sibling exists; `PENDING.md` keeps the shape of running two
transports at once and of a client that can fall back between them.

## Conventions the code relies on

- **Expected states** are the reference client's: any, no stream, stream
  exists, or an exact revision. The store's expected version is the version
  after the append, one-based, and the conversion happens once, in
  `PolecatStreamStore`.
- **Idempotent retries** follow the reference rule: on a conflict with an
  explicit expectation, the stored events from that revision on are compared
  with the batch by id, and an exact match returns the original result. Event
  ids are not enforced unique.
- **Names** come in two vocabularies, on purpose. The contract, the wire and
  the client say "persistent subscription" and "group", as the reference
  does, so they read as the contract their callers already know:
  `GroupSettings`, `GroupExistsException`, `GROUP_NOT_FOUND`. The server's
  own types and tables say "subscription group" for the same thing:
  `SubscriptionGroupStore`, `SubscriptionParkedEvent`. The difference stops
  at the wire, and each contract type says so where it is declared.
- **Data access** lives in a `Data` folder and namespace: the contexts, their
  entities, and the migrations under `Data/Migrations`. A context exposes
  every table it maps through a set named for its entity, the class name or
  its plural, which the conventions check holds.
- **Errors** cross the wire as a `google.rpc.Status` with an `ErrorInfo`
  detail whose reason is one of `errors.proto`'s; the client maps reasons to
  the shared exceptions, and unknown reasons stay `RpcException`.
- **Options** are one tree bound from the `Nightingale` section: the server
  library's `NightingaleOptionsBase` holds what every backend shares, such as
  which deletions the host allows, a backend's options derive from it and add
  their own, and the registration accepts any type that derives from the base.
- **Tenancy** is conjoined from day one, with the default tenant, so enabling
  tenants later is a matter of credentials and a header, not a migration.

## How it is tested

- `*.Tests` projects are unit tests and need nothing running: the service
  against a strict fake of the port and a tail a test moves by hand, the
  router against the same, the per-tenant poller against positions a test
  writes, the append signal on a clock a test moves, the client against a
  scripted call, the conversions, the marker comparison, the group store and
  the mirror's queries on the in-memory provider, and the model against the
  schema feature.
- `*.Tests.Integration` projects need the local SQL Server and run only with
  `-p:RunIntegrationTests=true`: the store adapter, the initializer's every
  path, the two guards above, the wake, proven by a host whose polling
  interval is minutes long reaching its own append in seconds, and the
  category rule, the database's column against the server's. Each creates
  its databases and drops them.
- `examples/` holds one program per backend that narrates each of its scenarios
  end to end, and a test per scenario that asserts the same run, against one
  database per run that the run's server creates and the run drops, with a
  tenant per scenario.
