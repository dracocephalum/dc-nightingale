# Repository layout

Two modes. Ask which one; do not infer it from the path name.

The unit of organization is the **component**: one solution, one `src/` and
`test/` pair. A component may hold as many projects as it needs, and may produce
**more than one deliverable** — an internal and an external API built from the
same solution, each with its own pipeline and its own release cadence.

What makes it one component is that its projects **build and version together**,
not that they deploy together. The solution boundary is a build decision; when
each artifact ships is a pipeline decision, and the two are allowed to differ.

**Components reference each other as packages, never as projects.** A
`ProjectReference` across component folders builds, and that is the problem: it
quietly makes the two components one — a change to either rebuilds both, and
neither can be released without the other — while the folders still claim they
are separate. So a component consumes another through a `PackageReference`, at
a version, from a feed.

Until a feed exists, a `ProjectReference` is allowed as a **recorded**
workaround, and the record is what keeps it temporary: this comment above each
one, verbatim, and one entry under *Decisions pending* in `TODO.md` naming the
switch to packages as what closes it.

    <!-- TEMPORARY: cross-component project reference. Components reference each
         other as packages (agentics/rules/layout.md); this stands in until a
         package feed exists. Listed in TODO.md. -->

It is not a pipeline's job to police this. A pipeline builds its component as
though the references were packages; while the workaround stands, its trigger
has to include the referenced components' paths, and that is the whole of the
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
| Component folder | the main project's **last** name segment, kebab-cased | `Contoso.ProxyGateway.Core` → `core/` |
| Solution file | its component's main project | `Contoso.ProxyGateway.slnx` |
| .NET project + assembly | PascalCase, prefixed | `Contoso.ProxyGateway` |
| Default namespace | project name, minus `.Core` | `Contoso.ProxyGateway` |
| Test project | project name + `.Tests` | `Contoso.ProxyGateway.Tests` |

The rule underneath: **directories are kebab-case, .NET artifacts are
PascalCase.** The solution is a .NET artifact, not a directory-level one: its
name is what appears in every build command, in the IDE title bar, and in the
component README, so it takes the main project's name rather than the folder's.
Where a component holds several projects, the main one is the deliverable the
others support.

**The folder takes the last segment only**, because the repository already
carries the rest: `Contoso.Ordering.Core` in a repository called
`contoso-ordering` lives in `core/`, not `ordering-core/`, which would repeat
what every path under the repository already says. Use more segments only
when two components would otherwise collide — `Contoso.Ordering.Api` and
`Contoso.Billing.Api` in one repository become `ordering-api/` and
`billing-api/`. Never the repository's own directory name: that names the
repository, not a component, and the two stop being interchangeable the
moment a second component exists.

**When there is no single main deliverable** — two peer APIs, say, an internal
and an external face of the same service — name the solution after the project
they share, which is usually the core library, or after the namespace root they
all sit under: `Contoso.Ordering.slnx` over `Contoso.Ordering.Api.Internal` and
`Contoso.Ordering.Api.External`. Still never the folder name.

### The `.Core` exception

A `.Core` segment names the assembly, not the namespace, so it is dropped from
the default namespace:

| Project (assembly) | Namespace |
|---|---|
| `Contoso.Core` | `Contoso` |
| `Contoso.Core.Tests` | `Contoso.Tests` |
| `Contoso.ProxyGateway` | `Contoso.ProxyGateway` (unchanged) |
| `Contoso.CoreBanking` | `Contoso.CoreBanking` (unchanged) |

This is applied automatically by `Directory.Build.props` — it sets
`RootNamespace` and leaves `AssemblyName` alone, so the DLL keeps its full name.
A `.csproj` can override it, since the project body is evaluated afterwards.

The condition matches a whole trailing `.Core` segment, not a substring — which
is why `Contoso.CoreBanking` is left alone. A naive string replace would turn it
into `ContosoBanking`.

`Contoso` is a placeholder: the prefix comes from the user, usually an
organization or product name, and is recorded in `.agentics.yaml` under
`repository.namespace`.

## Standalone

    <repo-root>/
      AGENTS.md
      README.md                      the component README - what it is, how to run it
      .editorconfig
      .gitattributes
      .gitignore
      Directory.Build.props
      Directory.Build.targets
      Directory.Packages.props
      Tests.props
      StyleCop.props
      stylecop.json
      stylecop.ruleset
      Contoso.Thing.slnx             named after the main project, not the repo
      docs/
        rules/                     the rules this repository is held to
          coding/csharp/csharp-coding-rules.md
      src/
        Contoso.Thing/
          Contoso.Thing.csproj
      test/
        Contoso.Thing.Tests/
          Contoso.Thing.Tests.csproj

Production code in `src/`, tests in `test/`. Both singular, matching `src/`.

## Monorepo

Root-level folders, created **only as needed** — an empty directory tree is
noise, and a folder that exists implies a decision that has not been made:

| Folder | Contents |
|---|---|
| `services/` | deployable C# services |
| `libraries/` | packages published to NuGet |
| `jobs/` | scheduled executables (Kubernetes CronJobs and similar) |
| `tools/` | never shipped to production |
| `infrastructure/` | Terraform and similar provisioning |
| `ui/` | front-end applications that are **deployed** — a shared component library is a package and belongs in `libraries/` |
| `docs/` | documentation written for people — architecture, decisions, guides; created only when there is some |
| `agentics/` | what agents follow: `rules/` and `templates/`; ships with every repository |
| `build/` | CI/CD pipeline definitions |

Each C# component gets a kebab-case folder, and **inside it the standalone
layout repeats**:

    <repo-root>/
      Directory.Build.props          <- one set at the root, inherited by all
      Directory.Build.targets
      Directory.Packages.props
      .editorconfig
      stylecop.ruleset
      README.md                      map of categories
      services/
        README.md                    map of components in this category - one row each
        proxy-gateway/
          README.md                  what it is, how to run it, configuration
          Contoso.ProxyGateway.slnx
          src/
            Contoso.ProxyGateway/
          test/
            Contoso.ProxyGateway.Tests/
      libraries/
        README.md
        message-contracts/
          README.md
          Contoso.MessageContracts.slnx
          src/
            Contoso.MessageContracts/
          test/
            Contoso.MessageContracts.Tests/

### One solution per component, none at the repository root

Each component carries its own `.slnx`, named after its main project, covering
only its own `src/` and `test/`.

**Do not create a root solution spanning the repository.** It gets out of hand
quickly: load times grow with every component added, an unrelated change forces
a rebuild across the tree, and the file becomes a merge-conflict magnet that
every component author has to touch.

This is a decision, not a default — do not add one even if asked to "make a
solution for the repo" without the tradeoff being discussed first.

To build everything, build each `*.slnx` found under the repository; CI should
build only the components a change touches, which is the point of the layout.
`dotnet build` with no argument at the root fails with `MSB1003` — the expected
consequence of having no root solution, not a misconfiguration.

### Config lives at the root only

`Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`,
`.editorconfig` and the StyleCop files exist **once**, at the repository root.
Every component inherits them.

Do not copy them into component folders. All three `Directory.*` files
resolve by searching upward and stopping at the first hit, so a copy inside
`services/proxy-gateway/` silently cuts that component off from the root
configuration — with a green build and no warning.

Non-.NET folders (`infrastructure/`, `ui/`, `docs/`, `agentics/`) are unaffected: MSBuild
files only apply to MSBuild projects.

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
reasons, not a restatement of the type comments, which already say what. It
is the page an agent reads before changing anything, so `AGENTS.md` points at
it. A repository that mirrors a reference product may add `VARIANCES.md` for
the deliberate differences and `PENDING.md` for features deferred with their
approach decided; both leave `TODO.md` for what is owed soon.

## Creating a component

The procedure — templates, the sample code each one emits that fails the
ruleset, central-package-management fixes, test wiring, verification — is
[`coding/csharp/csharp-new-project.md`](coding/csharp/csharp-new-project.md),
which also backs `/new`. The first component created at initialization
follows it too.

## Choices made here

Recorded so they are not re-litigated per repository:

**`test/`, not `tests/`.** Matches `dotnet/runtime`, `dotnet/aspnetcore` and
`dotnet/roslyn`, and pairs symmetrically with `src/`. Both spellings are common
in the wider ecosystem — this is a coin flip that has been flipped. Be
consistent rather than right.

**`agentics/` for the rules, `docs/` for people.** The rules and templates
agents follow live in `agentics/`, named for their audience, and ship with
every repository. `docs/` is kept for documentation written for people —
architecture, decisions, guides — and exists only when there is some. Mixing
the two in one folder is what happened first: the rules were under `docs/`,
on the grounds that `docs/` is what GitHub Pages expects — which is a reason
about human documentation, and the wrong one for a folder of agent rules.
When `docs/` does exist, it is `docs/`, not `doc/`: what GitHub Pages expects
and what the .NET repositories use.

**`.slnx`, not `.sln`.** The SDK 10 default, XML, and reviewable in a diff —
unlike the legacy format's GUID soup.

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

## Test projects

Everything a test project needs lives in `Tests.props` at the repository root.
It is imported from `Directory.Build.targets` under one condition:

    <Import Project="$(MSBuildThisFileDirectory)Tests.props"
            Condition="($(MSBuildProjectName.EndsWith('.Tests')) or $(MSBuildProjectName.EndsWith('.Tests.Integration'))) and '$(ManagePackageVersionsCentrally)' != 'false'" />

So any project named `*.Tests` or `*.Tests.Integration`, at any depth, picks
up the whole stack and nothing else does. The second name is for tests that
need a live dependency: `Tests.props` leaves such a project an ordinary
executable, `IsTestingPlatformApplication=false`, unless the run passes
`-p:RunIntegrationTests=true`, so a plain `dotnet test` builds it and runs
none of it. Verified on a real repository: `IsTestProject=false` alone does
not stop the platform's `dotnet test`, and xunit v3 refuses
`OutputType=Library`; the platform flag is the one lever that skips the
project while it still compiles. Verified: a `src/` project resolves 2 package references
(the StyleCop and banned-API analyzers), a `.Tests` project resolves 13. The
second half of the condition is the escape hatch: a test project that opts out
of central package management in its own `.csproj` is left alone and carries
its own stack — [`csharp-new-project.md`](coding/csharp/csharp-new-project.md),
step 4.

**Why it is imported from the targets file**, not `Directory.Build.props`: the
props file is evaluated before the project body, so a property the project
sets is not visible there yet; the targets file comes after. Everything in
`Tests.props` is consumed later than evaluation, so a project that does not
opt out sees no difference.

**Why the condition lives in a root file** rather than a `Directory.Build.*`
inside `test/`: a nested one would *replace* the root file for that subtree —
first-found-wins, no merging — silently cutting test projects off from central
package management, StyleCop and warnings-as-errors.

Test projects also get `IsPackable=false`, so they can never be published by
accident.

### What is included

| Package | Role |
|---|---|
| `xunit.v3` | the framework, and with it Microsoft.Testing.Platform — every test project is an executable |
| `Microsoft.Testing.Extensions.CodeCoverage` | coverage, cobertura out, settings in `coverage.config` |
| `FakeItEasy` | faking |
| `FakeItEasy.Analyzer.CSharp` | catches FakeItEasy misuse at compile time |
| `AutoFixture` | test data generation |
| `Shouldly` | assertions |
| `DeepEqual` | structural comparison |

`FakeItEasy.Analyzer.CSharp` carries `PrivateAssets=all` plus the analyzer
asset list — it is build-time tooling and must not flow anywhere. There is no
test host package and no VSTest adapter: on .NET 10 SDK, xunit v3 runs on the
platform and the SDK refuses the VSTest path, so `Tests.props` sets
`OutputType=Exe` with the two platform properties, and `global.json` opts
`dotnet test` in.

Since test projects are never shipped, the bar for adding a package here is
low: a redundant reference costs a restore entry and nothing else. Still confirm
the convenience libraries (`AutoFixture`, `Shouldly`, `DeepEqual`) with the user
at initialization rather than assuming.

### Enabled only when production code uses them

Commented out in `Tests.props`; uncomment when the corresponding library is
actually referenced by the application:

- `FluentValidation` — validator test helpers
- `Serilog.Sinks.TestCorrelator` — asserting on emitted log events

### Do not bump these piecemeal

`xunit.v3` pairs with `Xunit.Combinatorial` **2.x** (1.x is the xunit 2 line)
and brings Microsoft.Testing.Platform with it, which is why the coverage
collector is the platform's own extension and not coverlet — a VSTest data
collector cannot run there. Taking "latest" for each package independently
produces exactly those mismatches. Move the whole set together, and run
`dotnet test` afterwards to confirm.

## Keeping the test stack current

The versions in `Directory.Packages.props` are a **known-good floor, not a
ceiling**. They record a combination that was verified to restore, build and
test together — nothing more. A repository initialized a year later should not
inherit a year-old stack by default.

So at initialization, **try to move the whole set forward**, then keep what
survives verification.

### Why not just take latest for each package

Because these packages are not independent, and the failures are compile-time
ambiguities rather than helpful version errors. Two real examples, both found by
attempting exactly that:

    xunit 2.9.3 + xunit.runner.visualstudio 4.0.0
      -> runner 4.x targets xunit.v3; tests silently fail to discover

    xunit 2.9.3 + Xunit.Combinatorial 2.1.41
      -> error CS0433: 'TheoryAttribute' exists in both xunit.core
         and xunit.v3.core

    xunit.v3 4.0.1 + Microsoft.NET.Test.Sdk + coverlet.collector
      -> "Testing with VSTest target is no longer supported by
         Microsoft.Testing.Platform on .NET 10 SDK and later"; the
         collector never runs

"Latest of everything" is not a valid combination. **Latest mutually compatible**
is the goal.

### Procedure

1. **Check the marker.** `Directory.Packages.props` carries
   `agentics-verified: sdk=<version> date=<date>`. If it is recent and the
   SDK matches, there is little to gain — proceed.

2. **Look up current versions** for the test packages:

       dotnet package search <id> --exact-match

3. **Move families together, not package by package.** The couplings recorded
   in `Directory.Packages.props` are the ones known to matter:
   the xunit family, `Xunit.Combinatorial`, the coverage extension, and
   `Mvc.Testing` against the TFM. Treat a major-version jump in `xunit.v3` as
   a decision to discuss, not a bump — the 3.x to 4.x move changed the test
   platform underneath, not just numbers.

4. **Verify by running, not by reading.** A restore that succeeds proves
   nothing:

       dotnet build      # must be clean under warnings-as-errors
       dotnet test       # must actually discover and pass tests

   Discovery failures are the dangerous case: a mismatched runner produces
   *zero tests* and a green exit code. Confirm the expected test count.

5. **Keep or revert per family.** If a family fails, fall back to the pinned
   versions for that family and keep the rest. Partial progress is fine.

6. **Update the marker** — `sdk=` and `date=` — so the next initialization knows
   when the set was last confirmed.

### If a bump fails

Bisect within the family rather than abandoning the attempt. `Xunit.Combinatorial`
was pinned at `1.7.31` exactly this way: `2.1.41` and `2.0.24` both failed with
CS0433, `1.7.31` passed, so `1.7.31` is the newest compatible version — not a
guess, and not simply "whatever the template said".

Record any newly discovered coupling in the comment block in
`Directory.Packages.props`, so the next person does not rediscover it.
