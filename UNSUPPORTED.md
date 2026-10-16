# Unsupported

What the gateway deliberately does not do, though someone may come looking
for it: each entry says why not, and what would make it worth doing.
Anything this repository neither does nor mentions is unsupported anyway; an
entry here is one that was weighed and set aside, recorded so it is not
weighed again from nothing. An entry moves to [`PENDING.md`](PENDING.md)
when evidence makes it wanted and its approach is decided. Deliberate
differences in behaviour from the reference event store are in
[`VARIANCES.md`](VARIANCES.md); what only the store's maintainers can change
is in [`UPSTREAM.md`](UPSTREAM.md).

## Marten's rich append mode

Marten's opt-in `EventAppendMode.Rich` takes an append's sequence numbers
with `nextval()` on a connection of its own before the append's transaction
opens, so that inline projections and pre-commit listeners see each event's
final position and version before the commit; Wolverine's fast event
forwarding relies on it to forward events keyed by those numbers before they
are committed. The Marten backend appends in quick mode, Marten 9's
default, and an application writing to the journal in rich mode is a writer
the gateway does not support.

**Why not:** a number taken outside the transaction that will use it is
invisible to every horizon, Marten's own detector included, so a slow rich
append can commit below a head that has passed it: the skipping Marten's
documentation warns of under load. Forwarding before the commit publishes
events a rollback may still undo. Neither fits a gateway whose numbers count
once committed and below the head, and the gateway itself needs no number
before its commit.

**Worth doing if:** an application shows a need for a position before its
commit that cannot be met after it; and then never by forwarding before the
commit.

## Skipping a gap while a transaction may still fill it

Marten's detector can be told to skip a gap once it has been held long
enough, `SkipStaleGapsDespiteLiveTransactionsAfter`, trading events for
progress. The gateway's tails never do: under the transaction horizon a gap
waits for as long as a transaction that could still fill it is open, and a
warning names the transaction after half a minute.

**Why not:** a skipped position that commits later is an event no reader by
position ever gets, a silent inconsistency; a stalled head is visible, and
the warning says whom to ask. The gateway owns its journal, so a transaction
holding the head for long is one somebody can end.

**Worth doing if:** never as a default; an operator's one-off command to give
a named gap up, with the event's fate written down, if a stall ever has no
other way out.

## A push across instances

An append wakes the tails of the instance it was made through at once; the
other instances find it at their next look, a second later at most by
default, a quarter of a second at the host's setting. A signal across
instances, Service Broker or query notifications on SQL Server, `LISTEN` and
`NOTIFY` on PostgreSQL, would cut that to milliseconds.

**Why not:** the machinery, its permissions and its failure modes, for what
`Nightingale:TailPollingInterval` already buys down with one small query a
look per instance (`DESIGN.md`, seam 4).

**Worth doing if:** a deployment needs sub-quarter-second delivery across
instances and measures the polling as its bottleneck. PostgreSQL's
`LISTEN`/`NOTIFY` is the cheapest place to start.

## Disabling one system projection at a time

KurrentDB disables `$by_category`, `$by_event_type`, `$by_correlation_id` and
`$streams` one by one. The gateway's ordinals are one sequencer's: it is
disabled as a whole, and every system ordinal stops at the same position, so
none disagrees with another.

**Why not:** a sequencer per ordinal kind would multiply the numbering's
passes, leases and progress for a switch nobody has asked for.

**Worth doing if:** KurrentDB clients of the adapter rely on it. The way
there is a disable the adapter fakes: the sequencer keeps numbering, the
disabled projection records the position it was disabled at, and its virtual
stream reads and subscribes only up to it, as KurrentDB's does while
disabled; an enable shows the rest at once, the same result as KurrentDB
resuming from its checkpoint, though it saves none of the work an operator
disables a projection to save.

## A reset inside an append to several streams

Refused. The mark a reset writes is the stream's last revision plus one, and
in the same commit that revision includes the commit's own events, which
exist only inside the store's write.

**Why not:** a transaction participant reading the stream's version under a
lock after the store's inserts could compute it, one more statement the
gateway writes itself, for a combination no client has needed.

**Worth doing if:** a client needs a reset in the same commit as appends to
other streams.

## The store's own append conditions

Polecat and Marten offer tag-based conditions on an append, of the form "no
event matching this query since position N", richer than a stream's expected
revision. The gateway's checks assert a stream's state only; the reference's
query check is unimplemented too.

**Why not:** nothing in the wire contract asks for it, and the reference has
no counterpart for a client to expect.

**Worth doing if:** an application needs consistency across streams that
expected revisions on each cannot give.
