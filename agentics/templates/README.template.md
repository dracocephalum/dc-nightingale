<!--
  TEMPLATE - copied to the repository root and transformed there into README.md.
  Fill every double-brace placeholder, keep the variant that applies, delete
  every prompt comment as it is answered. Written for people; agents use AGENTS.md.

  It stays here afterwards, unchanged, so a layout conversion has the variant
  that was deleted from the generated copy. Every link below is relative to the
  REPOSITORY ROOT, where the output lands - they do not resolve from this
  directory, and that is correct.
-->

# {{REPO_NAME}}

> Initialized with <https://github.com/dracocephalum/dc-agentics>. The
> choices made, and the toolkit commit, are in
> [`.agentics.yaml`](.agentics.yaml); open items are in [`TODO.md`](TODO.md).

{{ONE_LINE_PURPOSE}}

<!-- ==== STANDALONE variant - delete if this is a monorepo ==== -->

## What it is

<!-- Two or three sentences: what this component does, who consumes it, what it depends on. -->

## Run it locally

    dotnet build {{SOLUTION_NAME}}.slnx --nologo -v:q
    dotnet test  {{SOLUTION_NAME}}.slnx

<!-- Anything beyond build and test: configuration to set, a database to start, a URL to open. -->

## Layout

| Path | Purpose |
|---|---|
| `src/` | production code |
| `test/` | tests |
| `agentics/rules/` | the rules this repository is held to — see `AGENTS.md` |

<!-- ==== MONOREPO variant - delete if this is standalone ==== -->

## What is in here

Each category folder holds independent components, one solution each. The
**map of components lives in each category's `README.md`**; this file is the
map of categories.

| Category | What lives there | Map |
|---|---|---|
| `services/` | deployable services | [`services/README.md`](services/README.md) |
| `libraries/` | packages published to NuGet | [`libraries/README.md`](libraries/README.md) |
| `jobs/` | scheduled executables | [`jobs/README.md`](jobs/README.md) |
| `tools/` | internal tooling, never shipped | [`tools/README.md`](tools/README.md) |
| `infrastructure/` | provisioning | — |
| `ui/` | front-end applications | — |

<!-- Keep only the rows for folders that exist. -->

## Working here

- One solution per component; **no root solution** — build the component you
  are changing.
- Build configuration is inherited from the root by every component.
- Every component has its own `README.md`: what it is, how to run it, how to
  test it.

<!-- ==== end variants ==== -->

## Conventions and rules

`AGENTS.md` is the index. The rules under `agentics/rules/` are the standard for
code, tests, dependencies, pull requests, and review — for people as much as
for agents.

## Licence

<!-- Delete this section if the repository has no LICENSE file. -->

{{LICENCE_NAME}} — see [`LICENSE`](LICENSE). Copyright {{YEAR}} {{COPYRIGHT_HOLDER}}.
