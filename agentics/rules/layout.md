# Repository layout

Two modes. Ask which one; do not infer it from the path name.

The unit of organization is the **component**: what builds and versions
together, in a folder of its own. What that is on disk is its toolchain's —
for .NET one solution with its `src/` and `test/` pair — and *A toolchain's
own layout* below names the file for each. A component may hold as many
projects as it needs, and may produce **more than one deliverable** — an
internal and an external API built from the same component, each with its
own pipeline and its own release cadence.

What makes it one component is that its parts **build and version together**,
not that they deploy together. The component's boundary is a build decision;
when each artifact ships is a pipeline decision, and the two are allowed to
differ.

**Components reference each other as packages, never by path.** A reference
from one component's folder into another's builds, and that is the problem:
it quietly makes the two components one — a change to either rebuilds both,
and neither can be released without the other — while the folders still claim
they are separate. So a component consumes another as a package, at a
version, from where packages are published.

Until there is such a place, a reference by path is allowed as a **recorded**
workaround, and the record is what keeps it temporary: the comment the
toolchain's file gives, above each one, verbatim, and one entry under
*Decisions pending* in `TODO.md` naming the switch to packages as what closes
it.

**A component that is never released may reference another by path.**
The rule above protects a release: two components that are each released,
as a package or as a deployment, must each be releasable alone. A component
under `tools/` or `examples/` has no release, and is of little use except
against the others as they are in the tree: a benchmark that measured the
last published package would say nothing of the change being made. So such
a component may reference any other by path, with no comment and no
`TODO.md` entry, because it is not a workaround. Three things keep it from
spreading:

- **One direction only.** A released component, whatever its category,
  never references one under `tools/` or `examples/`, and still reaches
  another released one only as a package. Between two that are never
  released there is no release to protect, and either may reference the
  other.
- **It is allowed, not required.** A tool the build itself runs may prefer
  the last released package, so that a broken tree cannot break the tool
  that builds it; a sample may prefer it to show what a consumer gets.
- **Nothing of it is packed.** The referencing component is marked as never
  packed, as its category already asks; the toolchain's file has the
  spelling.

It is not a pipeline's job to police any of this. A pipeline builds its
component as though the references were packages; where a reference by path
stands, by the workaround or by the paragraph above, the trigger has to
include the referenced components' paths, and that is the whole of the
pipeline's involvement.

Split into two components when the code genuinely diverges — separate
dependencies, separate lifecycles, a change to one that cannot break the
other — not merely because there are two things to deploy.

| Mode | Components | Shape |
|---|---|---|
| **standalone** | exactly one | the component *is* the repository |
| **monorepo** | many | each lives under a category folder at the root |

So standalone is not "a small repo" — it is a **single-component** repo. Several
projects built from one solution are still standalone, and so are several
deliverables produced from it.

## Naming rules

These apply in both modes and are the source of most inconsistency if left
implicit:

| Thing | Convention | Example |
|---|---|---|
| Directories | lowercase, hyphenated | `proxy-gateway` |
| Component folder | the main deliverable's **last** name segment, kebab-cased | `Contoso.ProxyGateway.Core` → `core/` |

What a toolchain names besides — a solution, a project, a namespace, a test
project — is in its file, with the case it writes them in.

**The folder takes the last segment only**, because the repository already
carries the rest: `Contoso.Ordering.Core` in a repository called
`contoso-ordering` lives in `core/`, not `ordering-core/`, which would repeat
what every path under the repository already says. Use more segments only
when two components would otherwise collide — `Contoso.Ordering.Api` and
`Contoso.Billing.Api` in one repository become `ordering-api/` and
`billing-api/`. Never the repository's own directory name: that names the
repository, not a component, and the two stop being interchangeable the
moment a second component exists.

## Standalone

The component is the repository. Its code is at the root, beside the root
documents, `agentics/` and, once there is something to put in it, `docs/`:

    <repo-root>/
      AGENTS.md
      README.md                      the component README - what it is, how to run it
      agentics/                      the toolkit's: rules/ this repository is held to, and templates/; replaced whole by a sync
      docs/
        rules/                     the repository's own rules and their index, AGENTS.md - once it has any
      ...                            the component, as its toolchain lays it out

The whole tree, with the build files a toolchain puts at the root, is in the
toolchain's file.

## Monorepo

Root-level folders, created **only as needed** — an empty directory tree is
noise, and a folder that exists implies a decision that has not been made:

| Folder | Contents |
|---|---|
| `services/` | deployable services |
| `libraries/` | packages published for others to consume |
| `jobs/` | scheduled executables (Kubernetes CronJobs and similar) |
| `tools/` | never shipped to production: what builds, tests or measures the rest, a benchmark project among them |
| `examples/` | runnable samples of the repository's own libraries, a component per subject holding its scenarios; never shipped — see *Samples* below |
| `infrastructure/` | Terraform and similar provisioning |
| `ui/` | front-end applications that are **deployed** — a shared component library is a package and belongs in `libraries/` |
| `docs/` | documentation written for people — architecture, decisions, guides — and `rules/`, the repository's own rules with their index |
| `agentics/` | what agents follow: `rules/` and `templates/`; the toolkit's, shipped with every repository and replaced whole by a sync |
| `build/` | CI/CD pipeline definitions |

Each component gets a kebab-case folder under its category, and **inside it
the standalone layout of its toolchain repeats**:

    <repo-root>/
      README.md                      map of categories
      services/
        README.md                    map of components in this category - one row each
        proxy-gateway/
          README.md                  what it is, how to run it, configuration
          ...                        the component, as its toolchain lays it out
      libraries/
        README.md
        message-contracts/
          README.md
          ...

### Nothing builds the repository as one

Each component is built by itself. No file at the root lists every component
for a build, whatever a toolchain calls such a file: it grows with every
component added, it makes an unrelated change rebuild the tree, and it is the
one file every component's author has to touch. A pipeline builds only the
components a change touches, which is the point of the layout.

What a toolchain shares between its components — the configuration every one
of them inherits — exists once, at the repository root, and is never copied
into a component's folder. For .NET both are in
[`layout/dotnet.md`](layout/dotnet.md): *One solution per component, none at
the repository root* and *Config lives at the root only*.

## The toolkit's rules and the repository's

Two folders are the toolkit's, and a sync replaces them whole: `agentics/` —
the rules under `rules/` and the templates — and the toolkit's shims under
`.claude/skills/`, a shim of the same name included. Nothing in either is
edited in place. A change to a rule that should hold everywhere is a pull
request to the toolkit; one that holds only here is a rule of the repository's
own.

The repository's own rules live in the folders `rules.local` in
`.agentics.yaml` names — `docs/rules/` by default — each with an `AGENTS.md`
index: the same two-column table as the root's, links relative to the
repository root, pointing at anything — a rule beside it, `DESIGN.md`, a
folder's README. Neither the folder nor the index exists until the first rule
does; that day, the index starts as a copy of
`agentics/templates/rules-AGENTS.md`. The sync copies its rows into the
generated block of the root `AGENTS.md`, after the toolkit's, so adding a rule
is a file and a row in the index and never an edit of the root. A shim of the
repository's own keeps a name no toolkit shim has.

A row with no rule behind it is not a rule and does not open the folder: one
that only sends the reader to a document of the repository's own —
`VARIANCES.md`, `PENDING.md`, a folder's README — goes in the root `AGENTS.md`
outside the block, a second table in the same columns right after it, which no
sync touches. The index is for rules; its links resolve only once its rows are
in the block, so an index that holds nothing else is a broken file wherever it
is read in place.

Opting out of a toolkit rule is declared: its path under `agentics/rules/` in
`rules.excluded`. The sync removes the file and its row; a link to it from
another toolkit document then fails the link check, which is the honest
statement that this repository does not hold that rule. Deleting the file
instead is undone by the next sync.

The root `AGENTS.md` is the repository's, except the block between
`<!-- agentics:guidelines -->` and `<!-- /agentics:guidelines -->`, which the
sync regenerates from the toolkit's template and the local indexes. Anything
outside the block — a section, a second table, a sentence — is never touched.

### Precedence

A local rule and a toolkit rule that disagree have no referee: an agent reads
whichever row its request matched, and reconciling two documents it was not
told are in conflict is a hope, not a mechanism. So precedence is declared,
never implied:

- **To replace a toolkit rule, exclude it and write the replacement.** Then
  one rule exists, and the question does not arise. This is the only override
  with a guarantee.
- **To narrow or extend one, the local document names the toolkit rule it
  amends in its first lines** — "applies on top of `agentics/rules/x.md`;
  where they differ, this one holds" — and its row keeps a trigger of its own,
  so the agent arrives at the amendment from the index rather than at the
  original. A local row whose trigger a toolkit row already has is a competing
  row, and the sync reports it. Local rows sit after the toolkit's in the
  table; that order is not precedence, and nothing reads it as such.
- **No fine-tuning is guaranteed to hold** against a toolkit rule that still
  exists, and the toolkit cannot test the combination: the rules are verified
  as shipped. A repository that amends one owns the result.

## Root documents

Four documents live at the repository root, each answering one question, so
nothing is said twice:

| Document | Question | When |
|---|---|---|
| `README.md` | what is this and how do I run it | always |
| `AGENTS.md` | which rule applies to what I am about to do | always |
| `TODO.md` | what is owed, blocked or undecided | always |
| `DESIGN.md` | why is the code shaped the way it is | the day a reader could not infer it |

`DESIGN.md` is written once the first non-obvious seam exists — a wrapper that
defeats a framework default, a schema patched rather than replaced, a
lifecycle the code enforces at startup — and kept short: the seams and the
reasons, not a restatement of the type comments, which already say what;
which fact goes in which of these documents, and which stays in the code, is
[`coding/where-facts-live.md`](coding/where-facts-live.md). It
is the page an agent reads before changing anything, so `AGENTS.md` points at
it. A repository that mirrors a reference product may add `VARIANCES.md` for
the deliberate differences and `PENDING.md` for features deferred with their
approach decided; both leave `TODO.md` for what is owed soon, and their rows
go in `AGENTS.md` outside the generated block, not in a local rules index —
*The toolkit's rules and the repository's* above.

## Creating a component

The procedure is the toolchain's: the templates, what they emit that the
build refuses, the wiring of tests, the verification. For .NET it is
[`coding/csharp/csharp-new-project.md`](coding/csharp/csharp-new-project.md),
which also backs `/new`. The first component created at initialization
follows it too.

### Samples

A sample under `examples/` is a component like any other, with its own
solution, `src/` and `test/`, and it exists to show a consumer how the
libraries are used against something real. Its shape keeps it from drifting
away from what is verified, and keeps it cheap to build:

- **One component per subject, not per scenario.** Every scenario of one
  thing a consumer would pick, one backend or one library, lives in one
  program: `examples/<subject>`. A component per scenario builds the same
  dependencies once per scenario. Measured on eight of them: about six
  minutes to build and test them all, against two and a half as one.
- **A scenario is a folder in the source project.** Its code and a
  `README.md` that walks through it, nothing else. The component's README
  lists the scenarios and links each.
- **One scenario method does the work and returns a report.** A static
  `Scenario.RunAsync` takes what it needs and a `TextWriter`, narrates each
  step to the writer, and returns a record of what it observed.
- **One entry point names the scenario.** Without an argument the program
  lists its scenarios; with names it runs those; with `all` it runs every
  one and goes on after a failure. One file holds the list: name, a line of
  what the scenario shows, how to run it. The exit code says whether every
  scenario asked for completed.
- **The program prints; the test asserts.** The test project has a test per
  scenario that runs the *same method* with a null writer and asserts the
  report. A scenario whose test exercised different code would be a scenario
  nobody has run.
- **What scenarios share is a folder of the same project.** The hosting, the
  throwaway resources and the program around the scenarios go in `Common`,
  so each scenario is only its steps.
- **A scenario owns what it creates and leaves nothing behind.** Each makes
  its own database, file or container, never one another scenario uses, and
  removes it, pass or fail. Their tests run one at a time when what they
  create is heavy.

A sample that needs a real dependency is an integration test and is gated
like one; see `csharp-unit-tests-rules.md`.

## Choices made here

Recorded so they are not re-litigated per repository:

**`agentics/` for the rules, `docs/` for people.** The rules and templates
agents follow live in `agentics/`, named for their audience, and ship with
every repository. `docs/` is kept for documentation written for people —
architecture, decisions, guides — and exists only when there is some. Mixing
the two in one folder is what happened first: the rules were under `docs/`,
on the grounds that `docs/` is what GitHub Pages expects — which is a reason
about human documentation, and the wrong one for a folder of agent rules.
When `docs/` does exist, it is `docs/`, not `doc/`: what GitHub Pages expects
and what the .NET repositories use.

**`ui/`, not `apps/`.** The categories are cut by *kind of deliverable*, and
`ui/` names that axis for a front-end the way `jobs/` does for a scheduled
executable. `apps/` would overlap: a web API in `services/` is also an app.

This does cut against the dominant monorepo convention, which is worth knowing
rather than discovering. Nx uses `apps/` and `libs/`; Turborepo uses `apps/`
and `packages/`, and in its own starter `packages/ui` is a **shared component
library** consumed by the apps, not an application. Anyone arriving from that
world will read `ui/` as the design system. The row above says which it is —
deployed front-ends here, component libraries in `libraries/` — because the
name alone cannot carry that.

`test/` and `.slnx` are choices of the .NET layout, and are with it.

## A toolchain's own layout

What a component is on disk, what is named besides the folders, and how its
tests are set up belong to the toolchain that builds it:

| Toolchain | File | Holds |
|---|---|---|
| `dotnet` | [`layout/dotnet.md`](layout/dotnet.md) | references between components as .NET spells them; the names of solutions, projects, namespaces and test projects, with the `.Core` exception; both trees with their build files; one solution per component; configuration at the root only; test projects, the test stack and keeping it current |

A section that was once in this document and is one toolchain's is in that
file under the heading it had.

**What a toolchain owns is listed.** `agentics/templates/<toolchain>/manifest`
names its rules and its templates, and `root/` beside it holds the files it
puts at the repository root, each at the path it has there. Every
toolchain's files are under `agentics/`, listed in `repository.toolchains`
or not; only a listed one has its files at the root and its rows in
`AGENTS.md`.

### Adopting a toolchain

Applies when a repository starts to build with a toolchain it does not
list: a first Go module in a repository of .NET components, say. It needs
no toolkit at hand, since the files are already under `agentics/`, and it is
a change of its own, from a clean tree.

1. **The toolchain has a folder**, `agentics/templates/<toolchain>/`, with a
   `manifest`. Where it has none, the toolkit this repository was last
   synced to did not have the toolchain: sync first, as a change of its own,
   and where the toolkit still has none, say that the toolchain is not
   supported and stop.
2. **Its root files**, never over one that is there:

       (cd agentics/templates/<toolchain>/root && find . -type f) | sed 's|^\./||' \
         | while read -r f; do [ -e "$f" ] && echo "exists: $f" || echo "to copy: $f"; done

   Copy each that is to copy, to the same path under the root. One that
   exists is the repository's: compare the two, take across what the
   toolchain needs, and say what was done.
3. **The list**: add the toolchain to `repository.toolchains` in
   `.agentics.yaml`.
4. **Its rows in `AGENTS.md`**: the rows of the block in
   `agentics/templates/root-AGENTS.md` that link into a path the manifest
   names, added to the block of `AGENTS.md` where the template has them. A
   later sync writes the same rows.
5. **Its parts in `AGENTS.md`**, outside the block. The template has a line
   `toolchain: <name>` wherever a toolchain's text belongs, and the
   toolchain's own `root-AGENTS.md` has the part of each name:

       awk -v name=<name> '{ sub(/\r$/, "") }
         /^<!-- ==== part: / { on = ($0 == "<!-- ==== part: " name " ==== -->"); next }
         on' agentics/templates/<toolchain>/root-AGENTS.md

   Put each part where the template's line stands between the text
   `AGENTS.md` already has, after the part of a toolchain that is there, and
   fill its placeholders.
6. **The first component**, by the toolchain's procedure, *Creating a
   component* above.
7. **Verify**: the toolchain's build and tests, and a scrub, whose checks 6
   and 13 compare the list with the code and with the root.

Dropping a toolchain is the same in reverse and is never done in passing:
its code first, then its root files, its rows, its parts and its place on
the list, each named to the user before it goes.
