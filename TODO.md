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
  server's two projects and the client reference `core`, the sample
  program references the client and the server's backend. Close by: publish `core`,
  `client` and the server's two packages to a feed and replace every
  `ProjectReference` that crosses a component folder with a
  `PackageReference` at a version. Until then the coverage number of a
  component that references another is wrong: the referenced component's
  assembly is built from source and instrumented too, and its classes count
  at 0% (`server` reads 63% and `polecat` 71% with every class of their own
  at 100%). Read the per-class rates in the cobertura file for those two, not
  the total.
- **The gRPC contract is half drafted, and the first slice of it works end to
  end.** `libraries/core/src/.../Protocols.Grpc/V1/` splits the contract by area the
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
  unimplemented) and the ordinal backfill, both in `PENDING.md`. Close by: see `PENDING.md`.
- **A delete's expected-revision check is not atomic with the archive.** The
  adapter reads the stream's revision, compares, then archives in the same
  session; an append that lands between the two is archived with the rest,
  so the outcome is still a deleted stream, but the check passed on a
  revision that was no longer current. Close by: a conditional archive
  statement that carries the expected revision, which is asked for in
  `UPSTREAM.md`.
- **Only the appending instance's tail is woken.** An append wakes the tail of
  the process it was made through; the other instances' tails find it at
  their next poll, up to a quarter of a second later while events flow and a
  second when they do not. A subscriber connected to another instance than
  the writer waits that long. Close by: a signal across instances, which on
  SQL Server is Service Broker or query notifications, once a measurement
  shows the interval matters there.
- **An initialization interrupted after the gateway's migrations and before
  the settings rows is not recognized.** A start that dies between the two
  halves is carried on by the next, which finds only the server's own tables,
  all empty; one that dies after the migrations' transaction committed and
  before the settings rows were written leaves an initialized store with no
  record of its settings, which the next start reads as a store initialized
  with the defaults: served when the configuration is the defaults, refused
  as a settings conflict when it is not. The window is a few row writes wide.
  Close by: writing the settings rows in the migration's own transaction, or
  recognizing a store whose property table is empty as one to finish.
- **Range partitioning of the events table is not a store feature.** The
  store partitions events by tenant or by the archived flag only, and adds
  the partition column to its unique stream-and-version index when it does.
  A sliding window by sequence for a very large table would be ours to add
  through the patched schema, and it would force the sequence into that
  unique index, leaving per-stream version uniqueness to the store's own
  commit-time check, and it would be a fourth value of
  `Nightingale:Store:Partitioning`, exclusive with the others like them. Close by: deciding when sizing demands it; see
  `PENDING.md`.
