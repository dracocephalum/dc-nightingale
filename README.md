# dc-nightingale

> Initialized with <https://github.com/dracocephalum/dc-agentics>. The
> choices made, and the toolkit commit, are in
> [`.agentics.yaml`](.agentics.yaml); open items are in [`TODO.md`](TODO.md).

A .NET gRPC server and client for the Polecat event-sourcing framework,
following the client pattern of KurrentDB, the reference event store. Where
the behaviour differs from it on purpose, [`VARIANCES.md`](VARIANCES.md) says how
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

## Documents

The repository's own documents sit at the root, each answering one question.

| Document | Answers |
|---|---|
| [`DESIGN.md`](DESIGN.md) | how the pieces fit, and why the code is shaped the way it is |
| [`GLOSSARY.md`](GLOSSARY.md) | what a term means here |
| [`VARIANCES.md`](VARIANCES.md) | where the behaviour differs from the reference event store on purpose, and why |
| [`PENDING.md`](PENDING.md) | what is deferred, with its approach decided |
| [`UNSUPPORTED.md`](UNSUPPORTED.md) | what was weighed and set aside, and what would make it worth doing |
| [`UPSTREAM.md`](UPSTREAM.md) | what only the store's maintainers can change, and the workaround meanwhile |
| [`TODO.md`](TODO.md) | what is owed soon |

## Working here

- One solution per component; **no root solution** — build the component you
  are changing.
- `agentics/` and the toolkit's shims under `.claude/skills/` are managed by
  the toolkit and replaced whole by a sync, a shim of the same name included;
  the rest of `AGENTS.md` is this repository's.
- Build configuration is inherited from the root by every component.
- Every component has its own `README.md`: what it is, how to run it, how to
  test it.

## Conventions and rules

`AGENTS.md` is the index. The rules under `agentics/rules/` are the toolkit's
standard for code, tests, dependencies, pull requests, and review — for people
as much as for agents — and a sync replaces that folder whole, so an edit
there is lost. Its documents beyond the toolkit's, those under *Documents*
above, have their rows in `AGENTS.md` outside the generated block. Its own
rules go under `docs/rules/` with an index there, as `agentics/rules/layout.md`
says.

## Licence

Apache-2.0 — see [`LICENSE`](LICENSE). Copyright 2026 Chris Har.

The client API's shape is taken from the KurrentDB .NET client, Apache-2.0,
Copyright Kurrent, Inc.; the acknowledgement is in [`NOTICE`](NOTICE).
KurrentDB is a trademark of Kurrent, Inc. This project is not affiliated
with, or endorsed by, Kurrent, Inc.
