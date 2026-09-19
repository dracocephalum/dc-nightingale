# dc-nightingale

> Initialized with <https://github.com/dracocephalum/dc-agentics>. The
> choices made, and the toolkit commit, are in
> [`.agentics.yaml`](.agentics.yaml); open items are in [`TODO.md`](TODO.md).

A .NET gRPC server and client for the Polecat event-sourcing framework,
following the client pattern of the reference event store. Where the
behaviour differs from it on purpose, [`VARIANCES.md`](VARIANCES.md) says how
and why; features deferred with their approach decided are in
[`PENDING.md`](PENDING.md).

## What is in here

Each category folder holds independent components, one solution each. The
**map of components lives in each category's `README.md`**; this file is the
map of categories.

| Category | What lives there | Map |
|---|---|---|
| `libraries/` | packages published to NuGet | [`libraries/README.md`](libraries/README.md) |
| `examples/` | end-to-end sample programs that double as integration tests | [`examples/README.md`](examples/README.md) |

## Working here

- One solution per component; **no root solution** — build the component you
  are changing.
- Build configuration is inherited from the root by every component.
- Every component has its own `README.md`: what it is, how to run it, how to
  test it.

## Conventions and rules

`AGENTS.md` is the index. The rules under `agentics/rules/` are the standard for
code, tests, dependencies, pull requests, and review — for people as much as
for agents.

## Licence

Apache-2.0 — see [`LICENSE`](LICENSE). Copyright 2026 Chris Har.
