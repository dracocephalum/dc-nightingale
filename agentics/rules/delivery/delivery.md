# Delivery

Applies when giving a repository a version of its own, adding or changing a
pipeline, publishing a package, changing a version number, or referring to a
package this organization builds. This file is the rules; the mechanics of
one host are in the file the [Hosts](#hosts) section names.

A repository starts with none of this. It builds as the SDK's default
`1.0.0`, with the commit hash the root targets file appends, and nothing
builds it but its developers. That is a complete state, not an unfinished
one: what follows is adopted when there is first something to publish, by
the steps under [Adopting it](#adopting-it), and not before.

## The version is in the code

`Version.props` at the repository root holds one number, and every package
built beneath the file takes it:

| Part | Raised for |
|---|---|
| major | a change that breaks a caller, a major dependency replaced among them |
| minor | a change that does not |
| patch | a fix |

**It is always the next release's number.** A release publishes the number
as it stands, so the change that follows a release raises it. Left alone, the
builds after a release carry the released number with a label, and sort below
the release they came after.

**Raising it is an ordinary change**, reviewed like any other, and the only
edit a version ever gets. Nothing writes to the file during a build.

**A component versioned apart carries a `Version.props` of its own**, in its
folder, found before the root's. Packages of one component always share a
number, which is what *build and version together* means in
[`../layout.md`](../layout.md); a package that needs its own number is a
component of its own. Start with the root file alone.

## The build says what kind it is

What follows the number is written nowhere. `Packaging.props` derives it
from two properties the build is given:

| `BuildKind` | Version | Built by | Goes |
|---|---|---|---|
| `dev`, the default | `1.2.0-dev` | a developer's machine, and the pipeline of a change request | nowhere |
| `ci` | `1.2.0-ci.42` | the pipeline of the default branch; `BuildNumber` is the pipeline's own counter | to the host's registry |
| `release` | `1.2.0` | the pipeline a release starts, and nothing else | to nuget.org |

- **`dev` is the kind that is safe.** Whatever a dev build does, it reaches
  no registry and nobody else's machine. A machine and a change request's
  pipeline are one kind for that reason, and need no telling apart: the
  commit hash in the informational version says which code it was.

- A package with no label was built as a release. No other build can produce
  one, so a version cannot claim to be what it is not.
- The counter stands after a dot, where it is compared as a number: `ci.9`
  comes before `ci.10`, and `ci9` would come after `ci10`.
- Never four numbers. `1.2.0.42` is a stable version to NuGet, and outranks
  the `1.2.0` it was built towards.
- `dev` sorts after `ci`, so a package packed on a machine is preferred there
  to the pipeline's of the same number.
- The commit hash is appended to the informational version in every kind, as
  before.

## Two registries, one kind each

| Registry | Takes | Kept |
|---|---|---|
| the host's own, beside the repository | `ci` builds | the newest few; the pipeline removes the rest |
| nuget.org | releases | for good |

A `ci` or `dev` package never goes to nuget.org: a version there cannot be
removed, and whoever finds it should be able to take it for finished. A
`dev` package goes to neither. A release is not written yet in this
toolkit; until it is, a repository publishes `ci` builds only.

**A `ci` package is meant to stay private**, in a repository that is
public too: it is what a pipeline leaves on its way, and whoever found it
beside the code would take it for what the repository publishes. A
repository that wants its `ci` packages seen says so. The host file has
how a package's visibility is decided there and how it is checked.

## A pipeline for each kind

A pipeline is named for the kind of build it makes, and the kind says how
far its work can reach:

| Pipeline | Started by | Does | Reaches |
|---|---|---|---|
| `dev` | a change request | builds and tests | nothing |
| `ci` | a push to the default branch | builds, tests, packs, deploys | the host's registry |
| `release` | a release | builds, tests, packs, deploys | nuget.org |

- **They share their steps.** Restore from the lock files, build, run the
  unit tests and, for the two that publish, pack: written once, and each
  pipeline calls it with its kind. Integration tests need a live dependency
  and are not run here.
- **`dev` has no deploy at all**, not one that is passed over. A change
  request shows the one thing that happens to it.
- **`ci` and `release` deploy exactly the packages their own run tested.** A
  repository that packs nothing has no deploy. `ci` builds again what `dev`
  built: the merged commit is not the one the change request tested.
- **A repository takes what it has a use for.** `dev` alone suits one that
  publishes nothing. `release` is not written yet in this toolkit.

**A pipeline runs for what it builds.** It names the folders and files its
build reads, and a change that touches none of them starts no run. The two
lists differ, each by what its pipeline is for:

- **`dev`'s is everything a build reads**: all the code, the tests, the
  build files every project inherits, what is packed.
- **`ci`'s is what a package is built from**: the code that is published
  and the build files it inherits. A change to a tool, a sample or a test
  alone was built and tested as a change request, and gives no package
  that differs from the last, so it starts no `ci` run and publishes
  nothing.

**What that leaves unbuilt.** A change to tests alone is never built on the
default branch as merged. Two such changes that each pass as a change
request and fail together are found by the next change to the code, whose
`ci` run then fails and deploys nothing until the tests are mended. That
is taken for the price of not publishing a package that is the last one
again; a repository that will not pay it puts its tests on `ci`'s list.

A list is its pipeline's own account of its sources, and a repository
drifts from it in silence: a build file left off it is a change nothing
builds. A scrub compares each with the tree, [`../scrub.md`](../scrub.md),
*A pipeline's trigger list is still true*.

A pipeline is given no stored secret where the host can do without one, and
the host file says how.

## A committed reference names a release

A project refers to a package this organization builds by a released
version, never a `-ci.` or `-dev` one: a build that depends on a version
the registry has since removed cannot be repeated. To try a change against a
`ci` or `dev` build, the version is overridden from a file git ignores or
from the environment, never by editing a tracked file — an edit meant to be
temporary is committed sooner or later, and it rewrites the lock files with
it.

**One exception, with an end.** Until a package has its first release there
is no released version to name, and a repository that needs it commits a
`-ci.` reference. Such a repository says so in its `TODO.md`, with the first
release as what closes the entry, and builds only where the registry can be
read. What it commits still names public sources alone: the registry is
reached through files git ignores, which the host file describes under
*Reading a package*.

The exception is taken in two places, and each is temporary and has a
`TODO.md` entry of its own, closed by the same first release:

| Where | What is relaxed | Offered |
|---|---|---|
| a developer's machine | the committed reference is a `-ci.` version, restored from the host's registry | when the reference is added; at initialization only where the user names such a package there, never as a question of its own |
| the repository's pipeline | its restore reads the host's registry as well | only once the repository is on its host |

**The second cannot be offered at initialization, and the order is
fixed.** A pipeline reads another repository's package only after that
package has been told to let it, and a package can only be told about a
repository that exists on the host. So: the repository is pushed; the user
gives it read access on each package it needs, which is theirs to do and
no agent's; and only then is the pipeline added, as its own change
request. A pipeline added before the access is given fails at restore, and
reads as a broken pipeline and not as a missing permission. The host file
has the steps, under *Another repository's pipeline*.

## Adopting it

Offered when the repository is initialized, and again whenever a pipeline,
a package or a version is asked for in a repository whose `.agentics.yaml`
still says `delivery.versioning: false`. The `ci` pipeline packs and
publishes and so needs versioning: asking for it is asking for both.
Versioning alone is a complete answer, and so is the `dev` pipeline alone,
which needs none.

**Confirm first, in one message**, what is not already said:

- how far to go: versioning alone, the `dev` pipeline alone, or versioning
  with both pipelines;
- the number to start from, `0.1.0` unless the user has another;
- the name packages are published under;
- with a pipeline: the name of the environment variable a developer's
  machine keeps its package-reading token in. Offer the host file's default
  and use what the user confirms; a machine may already have one under
  another name.

Then, each step leaving the repository building:

1. Copy `agentics/templates/Version.props` to the repository root and set
   the number: `0.1.0` for what has never been released.
2. Copy `agentics/templates/Packaging.props` to the root. Replace
   `{{PACKAGE_AUTHORS}}` with the name packages are published under, and
   `{{LICENCE}}` with `repository.licence` from `.agentics.yaml`; where that
   is `none`, delete the line.
3. In `Directory.Build.props`, after the import of `StyleCop.props`, add:

       <Import Project="$(MSBuildThisFileDirectory)Packaging.props" />

4. Give every project that is packed a `<Description>` in its `.csproj`.
5. Check the three kinds, from the component's folder:

       dotnet pack <solution> -o <a folder outside the repository>
       dotnet pack <solution> -o <the folder> -p:BuildKind=ci -p:BuildNumber=1
       dotnet pack <solution> -o <the folder> -p:BuildKind=release

   The folder then holds each package as `-dev`, `-ci.1` and with no label.
6. Add the pipelines, by the host file, as a change request of their own.
   They are never part of a repository's first push, which would deploy
   before anyone had looked: a repository that is being initialized takes
   this step once it is on its host, and stands until then as one that is
   versioned and has no pipeline.
7. Say in `DESIGN.md` that the repository is versioned and where its packages
   go, and add `Packaging.props` and `Version.props` to the list of root
   build files in `AGENTS.md`.
8. Record it in `.agentics.yaml`, under `delivery:` — `versioning: true`,
   the `pipeline`, and the variable's name as confirmed — and remove the
   entry initialization left in `TODO.md`. Remind the user of what is theirs
   to do and no agent can: the host file lists it.

Steps 1 to 5, with 7 and 8, stand without the pipeline: a repository can be
versioned and have none. The `TODO.md` entry then stays, reworded to the
pipeline alone.

**The `dev` pipeline alone** is step 6 and step 8 and nothing else: the
change request of step 6 adds `build.yml` and `dev.yml` and leaves `ci.yml`
where it is, and step 8 records the `pipeline` with `versioning: false`.
The `TODO.md` entry stays, reworded to the version and the pipeline that
publishes. A repository that later publishes takes the other steps then,
and adds `ci.yml` beside the two it has.

**The copies are the repository's.** A sync replaces the templates and not
the files copied from them, and its report does not yet say which
templates moved. After a sync, and before it is committed, look:

    git status --short -- agentics/templates/

Where one of them is listed, compare it with the copy and take what
applies.

## Hosts

| Host | File |
|---|---|
| GitHub | [`github.md`](github.md) |

Only GitHub today. For another host, add a sibling file here with the same
sections, and a row above; do not generalise this file around it.
