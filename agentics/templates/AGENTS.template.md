<!--
  TEMPLATE - copied to the new repository root as AGENTS.md.

  Fill every double-brace placeholder, delete the layout variant that does not
  apply, and trim the folder table to folders that actually exist.

  Delete this comment block, then confirm no double-brace token remains
  anywhere in the file.

  Named AGENTS.template.md rather than AGENTS.md so it is not picked up as live
  agent instructions - by the toolkit, or by any tool that reads a nested
  AGENTS.md as directory-scoped instructions. Keep the suffix.

  This file is COPIED to the root and transformed there; it stays here
  afterwards, unchanged, because a layout conversion needs the variant that was
  deleted from the generated copy.

  Every link below is relative to the REPOSITORY ROOT, where the output lands -
  so they do not resolve from this directory. That is correct; do not "fix"
  them to climb out of agentics/templates/.
-->

# {{REPO_NAME}}

{{ONE_LINE_PURPOSE}}

Primary framework: .NET. Namespace prefix: `{{PREFIX}}`.

## Repository layout

<!-- ==== STANDALONE variant - delete if this is a monorepo ==== -->

| Path | Purpose |
|---|---|
| `src/` | production code |
| `test/` | test projects, one per project under test |
| `agentics/` | what agents follow — `rules/` this repository is held to, see *Agent guidelines* below, and `templates/` |
| `{{SOLUTION_NAME}}.slnx` | the solution — this repository is a single component |

<!-- ==== MONOREPO variant - delete if this is standalone ==== -->

Each component is a kebab-case folder holding its own `.slnx` — named after its
main project — plus `src/`, `test/` and `README.md`. There is deliberately **no
solution at the repository root**, and no component list here: **each category's
`README.md` is the map** of what it contains, kept current by the new-project
procedure. Read the category map before assuming a component does or does not
exist.

| Path | Purpose |
|---|---|
| `services/` | deployable C# services |
| `libraries/` | packages published to NuGet |
| `jobs/` | scheduled executables (Kubernetes CronJobs and similar) |
| `tools/` | internal tooling, never shipped to production |
| `infrastructure/` | provisioning (Terraform and similar) |
| `ui/` | front-end applications |
| `agentics/` | what agents follow — `rules/` this repository is held to, see *Agent guidelines* below, and `templates/` |
| `build/` | CI/CD pipeline definitions |

<!-- ==== end variants ==== -->

Build configuration lives **once, at the repository root** — `Directory.Build.props`,
`Directory.Build.targets`, `Directory.Packages.props`, `Tests.props`,
`StyleCop.props`, `stylecop.ruleset`, `stylecop.json`, `.editorconfig`. Every
project inherits it. The targets file stamps every assembly's informational
version with the short commit hash (`1.2.3+abcdef12`, `-dirty` when the tree
is not clean); it never fails the build.

Never add a `Directory.Build.props`, `Directory.Build.targets`, or
`Directory.Packages.props` inside a subfolder — a nested copy silently severs
that subtree from everything above, with a green build and no warning.
[`agentics/rules/layout.md`](agentics/rules/layout.md), *Config lives at the root only*.

## Agent guidelines

Read the linked file **before** acting on a matching request, rather than
working from memory. Each row states *when* it applies; that trigger is what
makes it selectable from a plain-language request.

| When you are | Read |
|---|---|
| Writing, reviewing or refactoring C# in this repository | [`agentics/rules/coding/csharp/csharp-coding-rules.md`](agentics/rules/coding/csharp/csharp-coding-rules.md) |
| Touching an entity, a `DbContext`, or a migration | [`agentics/rules/coding/csharp/csharp-ef-core-rules.md`](agentics/rules/coding/csharp/csharp-ef-core-rules.md) |
| Adding a project, component, or test project | [`agentics/rules/coding/csharp/csharp-new-project.md`](agentics/rules/coding/csharp/csharp-new-project.md) |
| Asking where something goes, how it should be named, or what the other layout looks like | [`agentics/rules/layout.md`](agentics/rules/layout.md) — both modes, so this repository can describe a shape it does not currently have |
| Checking a change for secrets, personal data, or local paths — or anything before a first push | [`agentics/rules/security-reminders.md`](agentics/rules/security-reminders.md) |
| Adding, upgrading, or replacing a package (any ecosystem) | [`agentics/rules/dependencies.md`](agentics/rules/dependencies.md) |
| Creating or finding a ticket, or asking which ticket a change is for | [`agentics/rules/change-tracking/change-tracking.md`](agentics/rules/change-tracking/change-tracking.md), then the tracker file it names |
| Starting work on a ticket, naming a branch, publishing the repository, committing, or opening / updating / merging a pull request | [`agentics/rules/source-control/source-control.md`](agentics/rules/source-control/source-control.md), then the host file it names |
| Reviewing a pull request, a diff, or a set of changes | [`agentics/rules/coding/code-review.md`](agentics/rules/coding/code-review.md) — then the C# or markdown checklist it names |
| Scrubbing, sweeping or auditing this repository for drift or inconsistency — or after any move, rename or restructure | [`agentics/rules/scrub.md`](agentics/rules/scrub.md) |
| Bringing this repository up to a newer version of the toolkit it was initialized with | the toolkit's own `repo/upgrade.md` — it diffs two commits of dc-agentics, so a checkout of it must be present; `.agentics.yaml` records which commit this repository came from |
| Finding what exists — which component does what, where something lives | `README.md` here; in a monorepo, each category's `README.md` is the map of its components, and each component's `README.md` says how to run it |
| Understanding how the pieces fit, or why the code is shaped the way it is | `DESIGN.md` at the root — the seams and the reasons; type comments say what, it says why. Written the day a reader could not infer it; see *Root documents* in [`agentics/rules/layout.md`](agentics/rules/layout.md) |
| Starting any source-control action, or asked what this repository's settings and defaults are | [`.agentics.yaml`](.agentics.yaml) — the mode the agent works in (`auto`, `local`, `manual`) and every choice made at initialization |
| Leaving something unfinished, blocked, or undecided — or asked what is still open | [`TODO.md`](TODO.md) — add it there; a remark in a conversation is lost |

{{ADDITIONAL_GUIDELINE_ROWS}}

The documents above are the substance, for every tool. In Claude Code some
are also invocable through a shim in `.claude/skills/` -
`/new [project|component] <name>`, `/review [PR# | branch | paths | comments PR#]`,
`/change-tracking new|find`, `/scrub [paths...]`, `/upgrade`,
`/source-control start|commit|pr|setup`, or just ask in plain language - each
following the same document. `/review` is the rules pass; the built-in
`/code-review` is the bug-hunting pass, and the two complement each other.
`/scrub` is neither: it looks for drift and inconsistency, and reports rather
than fixing.

Add a row whenever a rules document is added under `agentics/rules/`. A document
nobody is pointed at will not be read.

## Conventions

| Thing | Convention | Example |
|---|---|---|
| Directories | lowercase, hyphenated | `proxy-gateway` |
| Projects, assemblies, namespaces | PascalCase, prefixed | `{{PREFIX}}.ProxyGateway` |
| Test projects | `test/<Project>.Tests/` mirrors `src/<Project>/` | `{{PREFIX}}.ProxyGateway.Tests` |

A trailing `.Core` names the assembly, not the namespace — `{{PREFIX}}.Core` has
namespace `{{PREFIX}}` — applied by `Directory.Build.props`; the full rule and
its edge cases are in [`agentics/rules/layout.md`](agentics/rules/layout.md).

## Serena — use it when connected, fall back when not

This repository carries a [Serena](https://github.com/oraios/serena) project
file (`.serena/project.yml`); whether the server is available depends on the
machine. **Connected:** call `initial_instructions` once per coding task, then
prefer the symbolic tools — `get_symbols_overview` before reading a file,
`find_symbol` / `find_referencing_symbols` over grep, `replace_symbol_body` /
`insert_after_symbol` for symbol-scoped edits — one method body instead of a
400-line file. **Not connected:** use the ordinary tools; do not stop, install,
or ask about it mid-task. Its C# language server needs a restored solution —
`dotnet restore` first if references look incomplete.

## Building and testing

    dotnet build <solution> --nologo -v:q
    dotnet test  <solution>

**If the build fails only on spacing rules** — `SA1009`, `SA1011`, `SA1111`,
`SA1028`, `SA1518` — do not fix them by hand:

    dotnet format analyzers <solution>

corrects every one at once; rebuild. Run the same command before each commit
regardless. It costs about as much as a build, and `--include` does not make
it cheaper, so it is a repair and a pre-commit step rather than a prefix to
every build.

**Keep build output out of context.** Build with `--nologo -v:q` and grep for
`error|warning`. Test output is already terse — the platform's `dotnet test`
rejects `--nologo` — so grep it for `total:|failed:|error` and report those
lines, never a full log.

**Warnings are errors.** StyleCop rules set to `Warning` fail the build; rules
at `Info` never surface at build time and appear only in the editor.

A `*.Tests` project inherits the whole test stack from `Tests.props`, so its
`.csproj` holds only `TargetFramework` and a `ProjectReference` —
[`agentics/rules/coding/csharp/csharp-unit-tests-rules.md`](agentics/rules/coding/csharp/csharp-unit-tests-rules.md).
It holds unit tests only. A `*.Tests.Integration` project holds tests that
need a live dependency; a plain `dotnet test` builds it and runs none of it —
pass `-p:RunIntegrationTests=true` to run them.

Do not report work as complete until `dotnet build` and `dotnet test` both pass,
with the test count you expect: zero tests and a green exit is a misconfigured
runner, not success.
