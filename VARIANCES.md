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

**Nightingale:** `$ce-`, `$et-`, `$all`, and `$de` and `$mde` (the data and
the metadata events) are predicates over the event table.
Their positions are the store's global sequence number: monotonic within the
stream but sparse, because other streams' events occupy the numbers between.
The head of each virtual stream is its own last event, never the global head,
so a quiet category reports a stable head and zero lag, and a subscription to
it says nothing while the store moves on without it. On a store initialized
with `Nightingale:Store:AssignOrdinals`, a read or subscription of `$ce-`, `$et-`
or `$mde`, never `$de`, may ask for ordinal numbering instead: dense, zero-based numbers assigned
after commit by one sequencer per cluster, so they trail the global head by
its cadence, a deleted event leaves a hole that is skipped and never reused,
and an event that committed late, after the head had passed its position,
gets the next ordinal when the sequencer finds it, so by ordinal it follows
what was appended after it. The choice is explicit per call, global by default, and refused with
`ORDINALS_NOT_ENABLED` on a store without the feature; under it every number
in the conversation is an ordinal and each event carries its ordinal beside
its position.

**Why:** no link events, no projection lag, no extra writes by default; and
where a dense number is worth a numbering pass, it is two columns and one
seek rather than a second copy of every event.

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

## Parked messages are rows: listed, replayed and skipped by number

**Reference:** a parked message goes to a stream per group, and replay puts
back all of them, or the first so many; one cannot be picked out. There is
no call that lists them or removes one: the parked stream is read like any
stream, and truncated.

**Nightingale:** parked messages are rows, one per group and event, and
replay moves all of them or the one at a number in the group's numbering,
a revision for a group over a plain stream, a position over `$all` or a
virtual stream, an ordinal for a group created under ordinal numbering, to
the group's outbox, from which they are delivered ahead of the stream, at
once to a consumer connected to the serving instance; the row keeps all
three numbers, so the same message is addressable whichever way the group
counts. A replayed message keeps its retry count, and one that fails again
goes back to parked. Replaying the same message twice is a no-op.

Three things follow from their being rows. A replay can take every message
below a number, where the reference's `stopAt` counts how many to take.
The parked messages and the outbox are each listed, a page at a time in the
order of the group's numbering: which event, why, at which retry
count; the event itself is read from its stream by its number. And a
parked message can be skipped, removed for good so the group never
delivers it, one, all, or every one below a number; the event stays in its
stream. Skipping and listing are answered by any instance, since no running
group holds parked messages in memory; a replay still goes to the instance
that runs the group, which has a consumer to wake.

The client's names are `ListParkedMessagesAsync`, `ListOutboxMessagesAsync`,
`SkipParkedMessagesAsync`, `SkipParkedMessagesBeforeAsync` and
`ReplayParkedMessagesBeforeAsync`, beside `ReplayParkedMessagesAsync`.

**Why:** rows have keys; a log does not. Nothing is lost by the difference.

## Updating a group changes its tunables, not where it stands

**Reference:** an update replaces a group's settings, its start position
among them, and drops its connections so they reconnect under the new ones.
Changing the start resets the group.

**Nightingale:** an update changes the timeouts, the retry limit, the
checkpoint bounds, the buffer size, the consumer limit and the consumer
strategy, and ends every connected consumer's call with `GROUP_UPDATED`,
which the client raises as `GroupUpdatedException`; the consumers connect
again and what they had not acknowledged is delivered again. Three things
differ.

- **The start and the numbering are not changed.** They say what the
  group's checkpoint means. Naming another value for either is refused, and
  a consumer that wants one deletes the group and creates it again. The
  client's `UpdatePersistentSubscriptionAsync` takes a settings object as
  the reference's does and sends neither of the two.
- **On the wire, a field left unset keeps its value**, so a caller changes
  one setting without restating the others. The answer carries all of them
  as they then stand.
- **The change is made by the instance that runs the group**, which is the
  one that can end its consumers' calls: another refuses with that
  instance's address and the client repeats the call there, as for a
  replay. A group nobody runs is changed by whichever instance was asked.

**Why:** moving a group's position in place has to say what becomes of its
parked messages, its outbox and what is in flight; that is parked in
`PENDING.md` rather than done by a side door of the update.

## Competing consumers share a group where it runs

**Reference:** a group dispatches to several consumers at once, by a
strategy: round robin, pinned, where a hash of the stream keeps each stream
with one consumer, pinned by correlation, where the hash is of the event's
`$correlationId`, or dispatch to a single consumer with the others standing
by. A limit on consumers, zero for none, is part of the settings.

**Nightingale:** the same four strategies under the same names,
`RoundRobin` the default, settable when the group is created and changed by
an update, and the same limit, zero for none and the default. The group runs
once per instance, in the instance that holds its lease, and its consumers
there share that one running copy: the first to connect starts it, the last
to leave stops it, and a consumer sent to the owner by another instance
joins it there. Every consumer connected to the same group is on that one
instance, which is what the lease says. Four things are worth knowing.

- **A consumer that leaves hands over what it held at once.** Disposing the
  client's subscription completes the request stream, the server applies
  whatever the consumer last sent, acknowledgements and refusals, hands the
  events the consumer still held to the remaining consumers ahead of the
  stream, and ends the call; the client waits for that end. Those events
  carry the retry count they had: changing hands is not a retry. A consumer
  whose connection drops instead is left in flight until the message timeout,
  which does count.
- **Pinned goes by a hash of the stream name over the consumers connected**,
  the same in every process, and streams are shared out again whenever a
  consumer joins or leaves; pinned by correlation hashes the event's
  `$correlationId` instead, and the stream name when the event has none.
  The reference's pinning is by its own hash over its own buckets, so which
  consumer a key lands on differs, and nothing depends on it. A pinned
  consumer that is full holds up its keys rather
  than lending an event to another consumer; so does the single consumer
  under dispatch to single, whose stand-ins take over only when it leaves.
- **A retry goes before the stream, whoever is to receive it**, and no event
  is taken from the stream until a consumer has room for it, so what a
  leaver held, a refused event and a timed-out one all go out ahead of
  anything new.
- **Group info lists each consumer** where the reference lists each
  connection: when it connected, its address, its buffer and what it holds,
  from the instance that runs the group; the stored numbers, which any
  instance answers with, keep the count and the totals only.

## A group's info comes from its own instance, and a listing from the store

**Reference:** every persistent subscription runs on the leader, so one node
has all of them in memory. Info about one group and the listing of all are
both answered there, a follower sending the client to the leader, and both
carry the live picture: a status, per-connection statistics, rates, buffer
counts, in-flight and parked counts.

**Nightingale:** a group runs in whichever instance its consumer reached,
under a lease of its own, so different groups run in different instances and
no instance has them all.

- **Info about one group** is answered by the instance that runs it, with
  how the group stands at that moment: when the consumer connected and from
  where, how many events are in flight, since when the oldest of them, how
  many await a retry, the consumer's buffer size, and the checkpoint as the
  running group has it. An instance asked for a group another runs refuses
  with that instance's address and the client asks there, once, as it does
  for a read. A group nobody runs is described by whichever instance was
  asked, from the store.
- **A listing** is answered by any instance from the store, and never
  redirects: settings, checkpoint, parked and outbox counts, whether the
  group runs and where, and the same live numbers as they were a few seconds
  ago. A running group writes how it stands to its row each time it renews
  its lease, every ten seconds at most, and each answer says when its
  numbers were taken. The groups the answering instance runs itself are
  asked directly and are as of now; every group's numbers say which they
  are, the running group's own or the stored ones. The same row answers for one group when its owner
  advertises no address to send the client to.
- **The stored numbers are for reading.** Nothing decides by them: who runs
  a group is its lease's to say, and its progress its checkpoint's. They
  count only while the lease is held, so an instance that stopped without
  clearing them leaves nothing that is believed.
- **What is reported differs.** There is no status string: a group either
  runs, which it does while a consumer is connected, or does not. There are
  no rates, totals or per-connection statistics. There is an outbox count beside the parked
  count, the group's numbering, and the address of the instance that runs
  it. The last number of the group's stream is in the info and not in the
  listing, since it is a read per group.
- **The client's names** are `GetPersistentSubscriptionInfoAsync` and
  `ListPersistentSubscriptionsAsync`, the second taking a stream or none,
  where the reference client has `GetInfoToStreamAsync`, `GetInfoToAllAsync`,
  `ListToStreamAsync`, `ListToAllAsync` and `ListAllAsync`: here `$all` is a
  stream name like any other, as it is for creating and subscribing.
- **There is no call to restart the subsystem.** Nothing here is a subsystem
  that one node holds; a group stops when its consumer leaves.

**Why:** a lease per group lets groups spread over instances instead of
loading one; the price is that the live numbers of all groups are in no one
memory. Writing them to the store with the lease renewal puts them in one
place at the cost of a few seconds' age, which the reference's periodic
statistics have too. The alternative, the answering instance asking every
owner, needs instances to call each other, which nothing else here does;
it is the relay in `PENDING.md`.

## A connection string lists instances, not gossip seeds

The client is opened from one connection string in the reference's shape,
with our scheme: hosts, optional credentials, settings as a query. The
reference's hosts are seeds from which the client gossips its way to the
leader; ours has no leader and no gossip, every instance serves every call,
so several hosts are used in rotation and the `+discover` form resolves one
DNS name to the instances. The settings that steer gossip, the node
preference and the discovery attempts, interval and timeout, do not exist
here and are refused like any key the client does not know, rather than
accepted and ignored: a string carried over from the reference is edited
once, and a misspelt key never passes for a default. Credentials in the
string are read and held, and not sent while the server has no
authentication.

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

## A deleted stream reads as one that never existed

**Reference:** a tombstone, its hard delete, leaves a marker: a read of the
stream fails with a stream-deleted error, and the name can never be used
again.

**Nightingale:** `Delete` removes the stream and its events permanently, its
metadata stream with them, and afterwards the name reads as not found and may
be appended to again; metadata set on it afterwards is a new stream's, where
the reference refuses every write to the name. The KurrentDB adapter maps the
reference's tombstone to it.

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
phase follows the poller: an append made through the same instance wakes it,
so the event is delivered within milliseconds; one made through another
instance is found at that instance's next look, at most
`Nightingale:TailPollingInterval` later, a second by default, where the
reference delivers to every node once its replication has the event. A host
that wants less sets the interval shorter and pays a small query per interval
per instance, whatever the number of subscriptions. Per-stream reads are
unaffected: versions are dense under the stream lock.

**Why:** reading past the high-water mark can skip an event forever.

## Reading from a replica is the host's choice, not the call's

**Reference:** a client chooses a node preference, and a call that must not
be served by a follower says so; a read from a follower may lag.

**Nightingale:** there is no leader to prefer, and a client sets nothing. The
host has a read-only connection, on by default, and what is served from it by
default is exact: a page of `$all` or of a virtual stream is taken from it
only as far as its own high-water mark reaches, and completed from the main
connection, so a client cannot tell the difference. Only when the host sets
`Nightingale:ReadStreamsFromReadOnlyConnection` does a bounded read of a
plain stream lag as a follower read does, and then for every client of that
host.

**Why:** the common case, a replay of history, should not need a decision
from each client; and the one read that can go stale is the one an append is
usually based on, so it is opt-in and decided where the database is known.

## Names are compared by the database, under a UTF-8 collation

**Reference:** stream names are case-sensitive; `Orders-1` and `orders-1` are
different streams. A name is any text.

**Nightingale:** the same, by default: the server creates its database with a
binary UTF-8 collation, under which case tells names apart and a name holds
any character. What decides is the database's collation, not the server,
which never folds case itself: a host may name another UTF-8 collation
through `Nightingale:Store:Collation`, a case-insensitive one for instance,
and then two names that differ only in case are one stream, one category,
one event type, one persistent-subscription group. A collation that is not
UTF-8 is refused, for a database the server would create and for one
provisioned by hand alike, unless the host sets
`Nightingale:Store:IgnoreCollationCompatibility`: its own choice, for a
database that already has another collation or a server too old to have a
UTF-8 one, with the loss described below as its consequence.

One place compares names in the server rather than in the database: the
router, which matches each new event's stream, category and type against the
names live subscriptions follow, in process, with a comparer built from the
database's own collation as the server reads it at every start. Under a
binary collation, the default, that comparer is ordinal and agrees with the
database exactly. Under a linguistic collation it is the same flags, case,
accents, kana, width, applied by .NET's comparison rather than SQL Server's,
whose weights differ in corners: a pair of names that one engine holds equal
and the other does not would match on a read and not live, or the reverse.
Names in one script and one case never meet the corner; a host that has
chosen a linguistic collation and names that only such a collation folds
together should know it is there. `DESIGN.md` keeps the exact alternative.

A name has a longest length, which the reference does not have in the same
way: the event store's columns hold 250 bytes of a stream name and 500 of an
event type, in UTF-8, so 250 characters of ASCII and fewer of a script that
takes more bytes per character. The database measures; a name that does not
fit is refused as an invalid argument and nothing is stored cut short.

**Why:** the event store keeps names in columns that are not Unicode. Under
a collation that is not UTF-8 a character outside the collation's code page
is stored as a question mark, so two names in another script, of the same
length, would silently become one stream. Requiring UTF-8 removes the case
rather than guarding it, and a collation is fixed when the database is
created, which makes it an initialization choice rather than an API one.

## `$all` holds no system events but stream metadata

**Reference:** `$all` includes system events, those whose type begins with
`$`: stream metadata, and the link events, statistics, settings, users,
projections and subscriptions the reference keeps in its own log. A client
filters them out with `ExcludeSystemEvents`, or reads the secondary index
`$idx-all`.

**Nightingale:** the only system events are the `$metadata` events of the
metadata streams, which `$all` carries in their place in the sequence. A
filter that excludes system events does what the reference's does, with less
to exclude; `$de`, the data events, is `$all` without them, read as a
stream, and `$mde` is the metadata events alone. Events of archived
(deleted) streams are excluded from `$all` and from the virtual streams.
`resolveLinkTos` is accepted and ignored because no link events exist.

**Why:** what the reference keeps as events, the store keeps in tables of
its own; metadata stays an event because it changes what a stream reads, and
a replay of `$all` needs a change of it in its place among the events around
it. Hiding archived events is the store's convention on every read.

## Archive and delete are off unless the host allows them

**Reference:** any caller with write access may delete (soft) or tombstone a
stream.

**Nightingale:** `Reset`, the reference's soft delete, is allowed to any
caller with write access, as a metadata set is: it removes nothing. `Archive`
and `Delete`, the reference's tombstone, are refused unless the host raises
`Nightingale:Deletion:Allowed` from `None` to `Archive`, or to `Delete`,
which allows both, and they need the operations role; a caller cannot raise
the level. The refusal is the `DELETION_DISABLED` reason, naming the
operation and the level.

**Why:** a store that never removes keeps every event a consumer may still
need and makes retention a decision of the operator rather than a habit of
the code; the levels rise with what is lost, and a reset loses nothing.

## Reset, archive and delete

**Reference:** two calls. A delete, the soft delete, makes the stream read as
not found until an append recreates it; the append continues its revisions
from the last plus one, the older events stay hidden from its reads, and they
remain in `$all` until a scavenge. It writes `$tb` at its largest value into
the metadata, replacing every other key, and an un-delete with `$tb` at the
last revision plus one after the recreating append or a set of the metadata.
An append that expects the stream to exist is refused while it is
soft-deleted; one that expects no stream is accepted. A tombstone is
permanent.

**Nightingale:** three calls. `Reset` is the reference's delete, read the
same way. Its metadata event stores `$tb` as the negative of the revision the
stream continues from, `-6` for a stream reset at revision 5, which one number
says both that it is reset and where it is truncated; no un-delete event is
written when an append recreates the stream, since the same number is then a
plain truncation, and the KurrentDB adapter translates the value for that
client, `$tb` at its largest while the stream is reset and the revision
after. A set of the metadata on a reset stream writes the reference's two
events in one append. An append that expects the stream to exist is accepted
on a reset stream, and one that expects no stream is accepted by a retry
after the store refuses it, a change of metadata between the two letting it
through. A reset of a stream with metadata but no events clears the metadata,
where the reference marks it deleted until its first append; the stream
reads as not found either way. `Archive` has no counterpart: the stream and
its events leave every read, `$all` and the virtual streams included, appends
fail with a stream-deleted error, and the rows stay; the gateway exposes no
un-archive. `Delete` is the reference's tombstone, except for the name, above.

**Why:** an append never reads metadata, so no check of it lands on the
first-tier path, and the store's unique index on a stream's versions is what
orders an append and a reset on any number of instances. Archive is the
store's own primitive, kept for a stream that must leave every consumer's
view while its rows are kept.

## Stream metadata keeps its history and refuses what it cannot honour

**Reference:** a stream's metadata is the last `$metadata` event of
`$$<stream>`. `$maxAge`, `$maxCount` and `$tb` (truncate before) change what
a read of the stream returns, and so what a catch-up subscription or a
persistent subscription on it starts from; events delivered live are not
checked. `$acl` decides who may read the stream, `$cacheControl` how its HTTP
interface lets a response be cached, and `$tmp` whether its scavenge removes a
deleted stream's metadata too. A metadata stream reads as its last event only,
under a `$maxCount` of one the server gives every metadata stream. A reserved
key of the wrong type is stored, and the stream's metadata then reads as
empty, every key dropped. A persistent subscription can be made on any
stream, a metadata stream included.

**Nightingale:** `SetStreamMetadata` and `GetStreamMetadata` store the
metadata as the same events in the same stream, in the reference's JSON, and
read it back. A read of the stream applies `$maxCount`, `$tb` and `$maxAge`
as the reference does, a subscription or a group starts from what the read
leaves, and live delivery is not checked; `$maxAge` is measured by the
database's clock, which stamped the events. The read reports the bound of
`$maxCount` and `$tb` as the head's first revision. `$$<stream>` is read and
followed as a stream with its whole history, written only by the metadata
call, and refused for a persistent-subscription group. `$tb` at its largest
value resets the stream (*Reset, archive and delete*). `$acl` is refused as an invalid argument until authentication carries
stream ACLs. `$cacheControl` and `$tmp` are stored and returned without
effect, keys carried over from the reference. A reserved key out of range or
of the wrong type refuses the set rather than emptying the metadata.

**Why:** metadata stored as events keeps its place in the one sequence and
its history, and costs a stream that has none a probe of a few index pages;
the history is the record of who changed a stream's retention, kept until a
maintenance job removes what metadata hides (`PENDING.md`, *Stream
metadata*). An access list stored and not enforced would read as a protection
the stream does not have, and metadata silently dropped as a whole is a
retention the operator thinks is in force and is not.

## An append is limited by its number of events

**Reference:** an append is limited in bytes: the whole append by a maximum
append size the server is configured with, a megabyte unless told otherwise,
and newer versions also limit a single record. There is no limit on how many
events one append carries, and no way to commit several appends together to
get past the size: the answer to an append too large is to split it.

**Nightingale:** an append is limited in events: 150 in one append unless the
host sets `Nightingale:MaxEventsPerAppend`, and never more than the backend
takes in one write, 190 on SQL Server. One event too many is refused with
`APPEND_SIZE_EXCEEDED` before anything is written, the limit in the answer,
and the client raises `AppendSizeExceededException`. Splitting is the
caller's to do, as it is with the reference: several appends, each under the
limit, each committing by itself. Bytes are not limited beyond what one
message of the transport may hold.

**Why:** the store writes an append as one command with a fixed number of
parameters an event, and the database takes 2100 parameters in a command, so
the true ceiling is a count and not a size. A limit in bytes would promise
what a few hundred small events then break. The limit is also wanted for its
own sake: an append is held in memory whole and written in one transaction,
and live delivery waits behind that transaction until it commits.

## An event has two bodies

**Reference:** one body, with a content type of `application/json` or
`application/octet-stream`.

**Nightingale:** JSON data, binary data, or both, in two fields of the event.
The JSON is always there when the event is read: an event appended with
binary data only reads back with `{}`. The binary data's media type is the
reserved metadata key `$binaryContentType`, `application/octet-stream` when
absent, and any type may be named, not only the reference's two.

**Why:** the case that makes binary data worth having is personal data
encrypted under a key that can be destroyed, and there the event is better
as both: the JSON says what happened and stays readable after the key is
gone, and the binary data is the part that is forgotten. A Protobuf or other
binary event is the binary data alone. The `{}` is the store's own
placeholder for a binary event, whose JSON column takes no null. A client in
the reference's shape, through the compatibility adapter, sees one body: the
binary data when the JSON is that placeholder, the JSON otherwise, as the
store itself tells a binary event from a JSON one.

## Event type names are stored verbatim

**Reference:** the type string is opaque.

**Nightingale:** the same, but note the store's own convention for CLR event
types is a snake-cased class name. A server-side projection written against
CLR types would have to use the names the clients send.

## Regular-expression filters are applied after the read

**Reference:** prefix and regular-expression filters on `$all` run in the
server's index.

**Nightingale:** prefix filters on stream name and event type are the
database's predicates: a stream prefix that is a category and its hyphen,
`orders-`, is the category column's equality, which the category index seeks,
less the stream named just `orders`, which the reference's prefix filter
leaves out too; any other prefix is a `LIKE`. A regular expression is matched
in process over the names of each stretch the scan examines, read without
their bodies, and the bodies of what it kept are read after; it runs without
backtracking, so a pattern from a client takes time linear in what it reads,
and the constructs that need backtracking, lookarounds and backreferences, are
refused. A scan examines at most a window of positions at a time, 4096, so a
selective filter over a large store answers in bounded time and says how far
it looked. A filtered subscription sends a checkpoint after the filter's
`checkpoint_interval` positions examined without an event to deliver, 1024
unless the filter says otherwise, as the reference does after its window
times its multiplier; it reports no fell-behind count, since how many events
a stretch holds for the filter is not known until the stretch is scanned.
Filters are on `$all` only, as in the reference; a virtual stream is already
a selection. A persistent-subscription group over `$all` takes a filter too,
fixed when the group is created, since its checkpoint means what the filter
kept; the reference's `CreateToAll` takes one the same way. The group's
checkpoint advances over a stretch the filter keeps nothing of, once
everything delivered before it is acknowledged, so a consumer that comes
back reads nothing twice.

## Authentication is Basic, roles are three, and a credential has a tenant

**Reference:** a user name and password per call in the `authorization`
header, users kept by the server, the groups `$admins` and `$ops`, a default
administrator with a well-known password, and client certificates.

**Nightingale:** the same header and scheme, so the reference's clients and
proxies carry it unchanged; what is behind it differs.

- **The built-in administrator's password is configuration**,
  `Nightingale:Auth:AdminPassword`, not a row: nothing done through the API
  can delete, demote or lock everyone out, and `Nightingale:Auth:AdminDisabled`
  retires it once other administrators exist. Authentication is on by
  default and the server refuses to start without that password unless told
  `Nightingale:Auth:Enabled` is false; there is no well-known password.
- **Credentials are rows** in the gateway's schema, the password a salted,
  peppered PBKDF2-HMAC-SHA256 hash in PHC form that carries its parameters, so
  the cost can be raised and the pepper rotated and a hash made under older
  ones is remade on the next login. A verified name and password are
  remembered on the instance for a few minutes, so the slow hash is not
  recomputed per call; wrong passwords are counted on the row and the
  credential is locked out past a threshold; an unknown name costs the same
  time as a wrong password. Which part was wrong is never said.
- **Roles are `user`, `ops` and `admin`**, each including the ones below:
  reads, appends and subscriptions; plus management of groups and streams;
  plus management of credentials and, globally, tenants. Where the reference
  has groups a user may be in, a credential here has one role.
- **A credential is bound to one tenant or is global.** A tenant-bound one
  works in its tenant and needs no header; a global one, whatever its role,
  sends `nightingale-tenant` on every data call, a tenant id or `*` on reads
  and subscriptions, so nothing lands in a tenant by omission and a
  cross-tenant consumer needs no more than the `user` role. The client sends
  it from the `tenant` setting of its connection string.
- **Basic over cleartext is refused** unless `Nightingale:Auth:AllowInsecureTransport`
  says the network is private; the reference leaves that to the operator.
- **Credentials and tenants are managed through two services of their own**,
  `Credentials` and `Tenants`, admin only, a tenant-bound admin within its
  tenant and never granting above its own role; the reference manages users
  through its users service and has no tenants. A disabled tenant is told
  apart from an unknown one (`TENANT_DISABLED` against `TENANT_NOT_FOUND`):
  only an authenticated caller naming a random 128-bit id can tell, and the
  tenant's own consumers need the difference to back off rather than fail.
- **The features call needs no credentials** and says whether the others do.
- **No client certificates, and no tokens yet**; the Bearer scheme is
  reserved, see `PENDING.md`.

## Multi-tenancy exists

**Reference:** no tenancy.

**Nightingale:** tenants share one database, every data call works in the
tenant it was authorized for, and the wildcard spans tenants on reads of
`$all` and the virtual streams by position (`DESIGN.md`, seam 9). Partitioning
the events table by tenant is an initialization choice,
`Nightingale:Store:Partitioning` set to `Tenant`; under it each tenant numbers
its events from its own sequence, so positions, the high-water mark and the
ordinals are per tenant, and the wildcard read across tenants is refused.
