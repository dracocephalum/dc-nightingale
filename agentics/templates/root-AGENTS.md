<!--
  TEMPLATE - copied to the new repository root as AGENTS.md.

  Fill every double-brace placeholder, delete the layout variant that does not
  apply, and trim the folder table to folders that actually exist.

  Delete this comment block, then confirm no double-brace token remains
  anywhere in the file.

  Named root-AGENTS.md rather than AGENTS.md so it is not picked up as live
  agent instructions - by the toolkit, or by any tool that reads a nested
  AGENTS.md as directory-scoped instructions. Keep the suffix.

  This file is COPIED to the root and transformed there; it stays here
  afterwards, unchanged, because a layout conversion needs the variant that was
  deleted from the generated copy.

  Every link below is relative to the REPOSITORY ROOT, where the output lands -
  so they do not resolve from this directory. That is correct; do not "fix"
  them to climb out of agentics/templates/.

  The block between the two agentics:guidelines comments is regenerated in the
  generated copy by every sync, from this file and from the repository's own
  rule indexes: nothing inside it may carry a placeholder, and nothing outside
  it is the toolkit's to change afterwards.

  A line that reads "toolchain: <name>" between four equals signs stands for
  the part of that name in agentics/templates/<toolchain>/root-AGENTS.md, of
  each toolchain the repository lists. The command in the toolkit's
  repo/documents.md puts them in and takes the lines out; none is left in a
  generated file.

  A row of that table is one toolchain's when it links into a path the
  toolchain's manifest names (agentics/templates/<toolchain>/manifest), and a
  sync leaves it out of a repository that does not list the toolchain. So a
  row that every repository is to have links into no toolchain's rules; it
  says "the toolchain's file" and lets the rule it links name it.
-->

# {{REPO_NAME}}

{{ONE_LINE_PURPOSE}}

<!-- ==== toolchain: intro ==== -->

## Repository layout

<!-- ==== STANDALONE variant - delete if this is a monorepo ==== -->

| Path | Purpose |
|---|---|
| `agentics/` | the toolkit's `rules/` and `templates/`; replaced whole by a sync, never edited in place |
| `docs/rules/` | this repository's own rules, once it has any, with their index, `AGENTS.md` |
| `.claude/skills/` | Claude Code shims; the toolkit's are replaced whole by a sync |
<!-- ==== toolchain: layout-standalone-rows ==== -->

<!-- ==== MONOREPO variant - delete if this is standalone ==== -->

Each component is a kebab-case folder under its category, with a `README.md`
and, inside, what its toolchain lays a component out as. Nothing at the
repository root builds them all, and there is no component list here: **each
category's `README.md` is the map** of what it contains, kept current by the
new-project procedure. Read the category map before assuming a component does
or does not exist.

<!-- ==== toolchain: layout-monorepo ==== -->

| Path | Purpose |
|---|---|
| `services/` | deployable services |
| `libraries/` | packages published for others to consume |
| `jobs/` | scheduled executables (Kubernetes CronJobs and similar) |
| `tools/` | internal tooling, never shipped to production |
| `examples/` | runnable samples of this repository's own libraries, never shipped |
| `infrastructure/` | provisioning (Terraform and similar) |
| `ui/` | front-end applications |
| `agentics/` | the toolkit's `rules/` and `templates/`; replaced whole by a sync, never edited in place |
| `docs/rules/` | this repository's own rules, once it has any, with their index, `AGENTS.md` |
| `.claude/skills/` | Claude Code shims; the toolkit's are replaced whole by a sync |
| `build/` | CI/CD pipeline definitions |

<!-- ==== end variants ==== -->

<!-- ==== toolchain: layout ==== -->

## Agent guidelines

Read the linked file **before** acting on a matching request, rather than
working from memory. Each row states *when* it applies; that trigger is what
makes it selectable from a plain-language request.

<!-- agentics:guidelines -->

| When you are | Read |
|---|---|
| Writing, reviewing or refactoring C# in this repository | [`agentics/rules/coding/csharp/csharp-coding-rules.md`](agentics/rules/coding/csharp/csharp-coding-rules.md) |
| Shaping a boundary — where a type lives, what an assembly may reference, wrapping a generated or third-party API, adding a transport or a backend | [`agentics/rules/coding/architecture.md`](agentics/rules/coding/architecture.md) — language-neutral |
| Writing a comment or a document, or unsure whether something belongs in the code or in a document | [`agentics/rules/coding/where-facts-live.md`](agentics/rules/coding/where-facts-live.md) — one home per fact; language-neutral |
| Naming or changing a tool's commands, options, settings keys, or the files and folders it reads and writes | [`agentics/rules/coding/command-line.md`](agentics/rules/coding/command-line.md) — language-neutral |
| Making a change for speed, judging one that may cost speed, or writing a figure about speed into a document or a change description | [`agentics/rules/coding/measuring-speed.md`](agentics/rules/coding/measuring-speed.md) — language-neutral |
| Touching an entity, a `DbContext`, or a migration | [`agentics/rules/coding/csharp/csharp-ef-core-rules.md`](agentics/rules/coding/csharp/csharp-ef-core-rules.md) |
| Adding a project, component, or test project | [`agentics/rules/coding/csharp/csharp-new-project.md`](agentics/rules/coding/csharp/csharp-new-project.md) |
| Asking where something goes, how it should be named, or what the other layout looks like | [`agentics/rules/layout.md`](agentics/rules/layout.md) — both modes; then the file it names for the toolchain |
| Asking how a solution, a project, a namespace or a test project is named, where build configuration lives, or how the test stack is kept current | [`agentics/rules/layout/dotnet.md`](agentics/rules/layout/dotnet.md) — the .NET half of the layout |
| Starting to build with a toolchain this repository does not list yet | [`agentics/rules/layout.md`](agentics/rules/layout.md), *Adopting a toolchain* |
| Checking a change for secrets, personal data, or local paths — or anything before a first push | [`agentics/rules/security-reminders.md`](agentics/rules/security-reminders.md) |
| Adding, upgrading, or replacing a package (any ecosystem) | [`agentics/rules/dependencies.md`](agentics/rules/dependencies.md) |
| Building, committing or publishing what is made from another's code or data, a translation, a port or generated files, where their terms are not a permissive licence | [`agentics/rules/derived-work.md`](agentics/rules/derived-work.md) — settled by the user before anything derived is committed or packed |
| Creating or finding a ticket, or asking which ticket a change is for | [`agentics/rules/change-tracking/change-tracking.md`](agentics/rules/change-tracking/change-tracking.md), then the tracker file it names |
| Starting work on a ticket, naming a branch, publishing the repository, committing, or opening / updating / merging a pull request | [`agentics/rules/source-control/source-control.md`](agentics/rules/source-control/source-control.md), then the host file it names |
| Giving the repository a version, adding or changing a pipeline, publishing a package, changing a version number, or referring to a package built here by a `-ci.` or `-dev` version | [`agentics/rules/delivery/delivery.md`](agentics/rules/delivery/delivery.md), then the toolchain's file and the host file it names — opt-in |
| Reviewing a pull request, a diff, or a set of changes | [`agentics/rules/coding/code-review.md`](agentics/rules/coding/code-review.md) — then the checklist it names for the language of the change |
| Scrubbing, sweeping or auditing this repository for drift or inconsistency — or after any move, rename or restructure | [`agentics/rules/scrub.md`](agentics/rules/scrub.md) |
| Measuring what this repository's documents cost an agent to read, or compacting them | [`agentics/rules/optimize.md`](agentics/rules/optimize.md) — measures unless asked for more |
| Bringing this repository up to a newer version of the toolkit it was initialized with | the toolkit's own `repo/upgrade.md`, operation 2, run from a checkout of dc-agentics; `.agentics.yaml` records the commit this repository came from |
| Finding what exists — which component does what, where something lives | `README.md` here; in a monorepo each category's `README.md` maps its components, and each component's says how to run it |
| Understanding how the pieces fit, or why the code is shaped the way it is | `DESIGN.md` at the root, once it is written — the seams and the reasons; *Root documents* in [`agentics/rules/layout.md`](agentics/rules/layout.md) |
| Starting any source-control action, or asked what this repository's settings and defaults are | [`.agentics.yaml`](.agentics.yaml) — the mode the agent works in (`auto`, `local`, `manual`) and every choice made at initialization |
| Leaving something unfinished, blocked, or undecided — or asked what is still open | [`TODO.md`](TODO.md) — add it there; a remark in a conversation is lost |

The documents above are the substance, for every tool. In Claude Code some
are also invocable through a shim in `.claude/skills/` — `/new`, `/review`,
`/change-tracking`, `/scrub`, `/optimize`, `/upgrade`, `/source-control` — or
just ask in plain language; each follows the same document. `/review` is the
rules pass and the built-in `/code-review` the bug-hunting pass, and the two
complement each other. `/scrub` is neither: it looks for drift and
inconsistency, and reports rather than fixing.

This block is generated, and a sync rewrites it: the toolkit's rows, then the
rows of every index the `rules.local` folders in `.agentics.yaml` hold
(`docs/rules/AGENTS.md`). A rule of this repository's own is a file under
`docs/rules/` and a row in that index, never a row typed here. Everything
outside the block is this repository's: a row that only points at one of its
own documents, with no rule behind it, goes in a second table right after
the block. A local rule that disagrees with a toolkit rule has no referee:
replace by excluding, amend by saying so in the document — *Precedence* in
[`agentics/rules/layout.md`](agentics/rules/layout.md).

<!-- /agentics:guidelines -->

## Conventions

| Thing | Convention | Example |
|---|---|---|
| Directories | lowercase, hyphenated | `proxy-gateway` |
<!-- ==== toolchain: conventions-rows ==== -->

<!-- ==== toolchain: conventions ==== -->

## Code intelligence — use what is connected, fall back when it is not

Two optional tools answer questions about symbols instead of text. Which are
there depends on the machine and shows in the session's own tool list; neither
is required, and neither is recorded in `.agentics.yaml`.

- **The `LSP` tool** (Claude Code, with a language-server plugin). Reads only:
  definitions, references, implementations, symbols, and diagnostics after an
  edit. **Connected:** prefer it over grep for "where is this used" and "what
  implements this". A first call that fails with `server is starting` is
  called again; the server loads on the first question, and searches the
  session's working directory of that moment. Be at the repository root
  then: from a folder of several repositories it loads the largest solution
  of any of them.

  <!-- ==== toolchain: code-intelligence-lsp ==== -->

- **[Serena](https://github.com/oraios/serena)**, for which this repository
  carries a project file (`.serena/project.yml`). Reads the same and also
  edits by symbol. **Connected:** call `initial_instructions` once per coding
  task. Use it for `rename_symbol`, `replace_symbol_body` /
  `insert_after_symbol` and `safe_delete_symbol` — one method body instead of
  a 400-line file — and for navigation when the `LSP` tool is absent
  (`get_symbols_overview`, `find_symbol`, `find_referencing_symbols`). A
  rename leaves what is only named after the symbol, test method names for
  one; search for those after it.
- **Neither connected:** use the ordinary tools; do not stop, install, or ask
  about it mid-task.

<!-- ==== toolchain: code-intelligence ==== -->

## Building and testing

<!-- ==== toolchain: building ==== -->

Do not report work as complete until the build and the tests both pass, with
the test count you expect: zero tests and a green exit is a misconfigured
runner, not success.
