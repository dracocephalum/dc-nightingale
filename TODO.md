# TODO

Open items for this repository that are not a ticket yet: steps
initialization could not finish, decisions deferred, manual actions still
owed. Each entry says what would close it, so it can be picked up cold. When
an item becomes real work, open a ticket and remove it from here; when it is
done, remove it. An agent that leaves something unfinished, blocked, or
undecided adds it here rather than mentioning it once in a conversation.

## Initialization

- **Change tracking is `none`.** `.agentics.yaml` records no tracker, so
  branch names and pull-request titles carry no ticket. Close by: when the
  repository has a tracker, set `change-tracking.tracker: github-issues`.

## Decisions pending

- **Cross-component references are project references, not packages.**
  `agentics/rules/layout.md`, *Components reference each other as packages*,
  allows this as a recorded workaround while no package feed exists; each
  such reference carries the standard comment in its project file: the
  server's two projects and the client reference `core`, the samples' shared
  library references the client and the server's backend, and every sample
  references that library. Close by: publish `core`,
  `client` and the server's two packages to a feed and replace every
  `ProjectReference` that crosses a component folder with a
  `PackageReference` at a version. Until then the coverage number of a
  component that references another is wrong: the referenced component's
  assembly is built from source and instrumented too, and its classes count
  at 0% (`server` reads 63% and `polecat` 71% with every class of their own
  at 100%). Read the per-class rates in the cobertura file for those two, not
  the total.
- **The gRPC contract is half drafted, and the first slice of it works end to
  end.** `libraries/core/src/.../Protocol/` splits the contract by area the
  way the reference protocol does. `streams.proto` is drafted in full, with
  `shared.proto` for the numbers, names, bodies, headers and error conventions
  and `errors.proto` for the reasons. Implemented and verified against a local
  SQL Server: `Append`, bounded reads and catch-up subscriptions over plain
  streams, `$all` and the virtual streams, `Delete` and `Tombstone` behind the
  host's switches, persistent subscriptions with create, delete, one
  consumer with acknowledgements, retries, parking and replay, on a lease per
  group, and the virtual streams by ordinal on a store initialized with
  `Nightingale:Store:AssignOrdinals`; the samples under `examples/` are the
  end-to-end runs. Not yet: filters and checkpoints on `$all` (they answer
  unimplemented), group info, listing and updating, competing consumers,
  in-cluster forwarding and the ordinal backfill, all in `PENDING.md`. Close
  by: forwarding next.
- **A replay wakes only a consumer connected to the serving instance.** The
  replay call moves the rows and wakes the group through this instance's
  registry; a consumer connected to another instance receives them at its
  next connection. Close by: with in-cluster forwarding, which resolves the
  owning instance and relays the wake.
- **The sequencer's progress is read as SQL text beside the context.** The
  sequencer's batch is raw by design, and its progress row is read raw by the
  virtual-stream reader too, although the context maps that row. Close by:
  reading the progress through the context once the backend's stream store
  takes the context factory, which the in-cluster forwarding work will do
  for the instance registry anyway.
- **A delete's expected-revision check is not atomic with the archive.** The
  adapter reads the stream's revision, compares, then archives in the same
  session; an append that lands between the two is archived with the rest,
  so the outcome is still a deleted stream, but the check passed on a
  revision that was no longer current. Close by: a conditional archive
  statement that carries the expected revision, if the store grows one.
- **The default-host integration test fails once in a few full runs.** The
  second `ProgramTests` case, which hosts the default program beside the
  fixture's host on the same database, has failed about one run in three of
  the full suite with "The CancellationTokenSource has been disposed", and
  never when its class runs alone, eight times over. The tailer's shutdown
  was reordered to drain its loop before stopping the store's daemon, which
  cured the one reproducible case; what remains looks like a background task
  of a stopped daemon throwing after disposal while several hosts stop at
  once. Close by: capturing the stack from a failing full run and either
  fixing the shutdown or filing it upstream.
- **Live delivery waits out a polling interval.** The tailer runs the store's
  high-water agent, which polls on the store's cadence, so an event reaches a
  live subscriber up to a quarter of a second after its append. The store's
  daemon settings carry a wake-up hook the store never sets, and the gateway
  is the writer, so it could wake the agent after its own appends. Close by:
  implementing the hook once its surface is confirmed on the pinned store
  version, and a test that a live event arrives well inside the interval.
- **One query per stream subscription per advance.** A subscription to a plain
  stream wakes on every advance of the global head and re-reads its own
  stream, which is one cheap query per subscription per interval and scales
  with the number of subscriptions, not the number of events. Close by: a
  router that reads each advanced range of `$all` once and hands events to
  the subscriptions by stream, when the subscription count demands it.
- **An ordinal subscription polls while the sequencer is behind.** After the
  tail advances, a subscription under ordinal numbering reads the numbered
  rows, and while the sequencer has not yet passed the head it woke for, it
  looks again every quarter second: one cheap query per subscription per
  interval, only during the sequencer's lag, which is about one tail interval.
  Close by: the sequencer publishing its progress in-process, and one poller
  per process for the other instances, once the subscription count demands it.
- **A bounded read abandoned mid-page has stalled the next read.** While the
  ordinals sample polled a virtual stream by disposing each read right after
  its head, the following read sometimes got no answer for a minute, with no
  blocking visible in SQL Server, and never once the sample consumed each read
  to its end. Disposing the call cancels the server's page query mid-flight;
  the suspect is the pooled connection that query ran on. Close by: a test
  that cancels a streaming read during a page and then reads again on the
  same host, and either a fix in how the reader cancels its command or an
  upstream issue on the SQL client.
- **The sequencer's takeover is not tested with two instances.** The lease
  logic is the group store's, tested there, and the batch transaction reads
  the progress row under an update lock so an overlapping sequencer continues
  from what the other committed; no test runs two hosts against one store to
  see it happen. Close by: a two-host integration test with the in-cluster
  forwarding work, which needs the same fixture.
- **Subscriptions under tenant partitioning follow the wrong mark.** The
  tailer follows the store's single high-water mark; with a sequence per
  tenant the store keeps a mark per tenant, and the tail would need one too.
  Close by: with the multi-tenancy work in `PENDING.md`.
- **Range partitioning of the events table is not a store feature.** The
  store partitions events by tenant or by the archived flag only, and adds
  the partition column to its unique stream-and-version index when it does.
  A sliding window by sequence for a very large table would be ours to add
  through the patched schema, and it would force the sequence into that
  unique index, leaving per-stream version uniqueness to the store's own
  commit-time check, and it would be a fourth value of
  `Nightingale:Store:Partitioning`, exclusive with the others like them. Close by: deciding when sizing demands it; see
  `PENDING.md`.
- **Schema migration is a startup switch, not a command.**
  `Nightingale:ApplySchemaChanges` lets a host apply a pending schema change
  at startup, which is the only way to move a store initialized by an older
  server forward. An operator cannot preview the change, apply it out of band,
  or apply it while the service is stopped. Close by: a `migrate` command in
  the default host beside the `--export-schema` switch it already has, with a
  dry run that prints the delta, after which the switch stays off in
  production hosts.
- **Protocol compatibility or API compatibility.** Speaking the reference event store's
  own protocol so its official client works unmodified means implementing server
  features, gossip, the trailer error model and its UUID byte order exactly.
  Default: our own proto in the same shape, with our own client. Close by:
  confirming the default before the proto is written; it changes message
  shapes.
- **Event identity and idempotent retries.** The reference event store de-duplicates by
  client-supplied event id within a stream, which makes an append safe to
  retry after a timeout. Default: honour client ids and enforce uniqueness
  per stream, which needs a unique index on the event id in the patched
  schema. Close by: deciding, then adding the index and the retry semantics
  to the contract.

## Owed before the first server code

- **Migration DDL is not logged.** The store's default migration logger
  writes every statement to the console, so the gateway's database class
  silences it; an operator has no record of what a startup applied. Close by:
  routing the migration logger to the host's logging, at warning level for
  changes, once the database class can reach a logger.
- **Upstream requests to the store's maintainers.** Honour a caller-supplied
  event type name on a prebuilt event; expose Weasel's ignored-index list or an
  add-index call for the events table, or a first-class category column; note
  that the event-store table ensurer bypasses the tenancy database; guard the
  high-water mark's update so a lagging detector cannot lower it (several
  gateway instances each run one). Close by: issues filed, links recorded here.
- **No authentication until the first beta.** See `PENDING.md`. Close by:
  username and password per call, before beta.
