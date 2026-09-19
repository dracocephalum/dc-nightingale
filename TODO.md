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
  server's two projects and the client reference `core`, and the example
  references the client and the server's backend. Close by: publish `core`,
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
  SQL Server: `Append` and a bounded `Read` of a plain stream, through the
  server, the store adapter and the client, with the `examples/append-and-read`
  sample as the end-to-end run. Not yet: `Delete`, `Tombstone`, `$all` and the
  virtual streams, subscriptions (all answer unimplemented), and the
  `PersistentSubscriptions` area, which is still an empty service. Close by:
  the subscriptions slice next, since it brings the tailer everything else
  builds on.
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

- **One guard test still owed.** Two of the three behaviours the design relies
  on are guarded in the polecat component's tests: the event wrapper that
  keeps the client's type name through the store's append, and the patched
  schema applying twice with an empty second delta. The third, the head of a
  virtual stream never reporting the global head, waits for the `$all` slice.
  If a store release breaks one, the package version stays pinned. Close by:
  the third test, with the virtual streams.
- **Migration DDL is not logged.** The store's default migration logger
  writes every statement to the console, so the gateway's database class
  silences it; an operator has no record of what a startup applied. Close by:
  routing the migration logger to the host's logging, at warning level for
  changes, once the database class can reach a logger.
- **Upstream requests to the store's maintainers.** Honour a caller-supplied
  event type name on a prebuilt event; expose Weasel's ignored-index list or an
  add-index call for the events table, or a first-class category column; note
  that the event-store table ensurer bypasses the tenancy database. Close by:
  issues filed, links recorded here.
- **No authentication until the first beta.** See `PENDING.md`. Close by:
  username and password per call, before beta.
