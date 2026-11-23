# Delivery

Applies when giving a repository a version of its own, adding or changing a
pipeline, publishing a package, changing a version number, or referring to a
package this organization builds. This file is the rules; the mechanics of
one toolchain and of one host are in the files the
[Toolchains](#toolchains) and [Hosts](#hosts) sections name.

A repository starts with none of this. It builds with the version its
toolchain gives a build that was told none, and nothing builds it but its
developers. That is a complete state, not an unfinished one: what follows is
adopted when there is first something to publish, by the steps under
[Adopting it](#adopting-it), and not before.

## The version is in the code

A file at the repository root holds one number, and every package built
beneath the file takes it. Which file it is, is the toolchain's to say, and
[Toolchains](#toolchains) names where. The number has three parts:

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

**A component versioned apart carries a version file of its own**, in its
folder, found before the root's. Packages of one component always share a
number, which is what *build and version together* means in
[`../layout.md`](../layout.md); a package that needs its own number is a
component of its own. Start with the root file alone.

## The build says what kind it is

What follows the number is written nowhere. The build is told its kind and,
for `ci`, the pipeline's counter, and derives the rest:

| Kind | Version | Built by | Goes |
|---|---|---|---|
| `dev`, the default | `1.2.0-dev` | a developer's machine, and the pipeline of a change request | nowhere |
| `ci` | `1.2.0-ci.42` | the pipeline of the default branch, numbered by the pipeline's own counter | to the host's registry |
| `release` | `1.2.0` | the pipeline a release starts, and nothing else | to the public registry |

- **`dev` is the kind that is safe.** Whatever a dev build does, it reaches
  no registry and nobody else's machine. A machine and a change request's
  pipeline are one kind for that reason, and need no telling apart: the
  commit hash in the informational version says which code it was.

- A package with no label was built as a release. No other build can produce
  one, so a version cannot claim to be what it is not.
- The counter stands after a dot, where it is compared as a number: `ci.9`
  comes before `ci.10`, and `ci9` would come after `ci10`.
- `dev` sorts after `ci`, so a package packed on a machine is preferred there
  to the pipeline's of the same number.

How a toolchain's build is told its kind, and what its registry asks of a
version besides, is in the toolchain's file.

## Two registries, one kind each

| Registry | Takes | Kept |
|---|---|---|
| the host's own, beside the repository | `ci` builds | the newest few; the pipeline removes the rest |
| the toolchain's public registry | releases | for good |

A `ci` or `dev` package never goes to the public registry: a version there
cannot be removed, and whoever finds it should be able to take it for finished. A
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
| `release` | a release | builds, tests, packs, deploys | the public registry |

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

1. Give the repository its version and its packaging, by the toolchain's
   file: the number set, the build able to make all three kinds, and each
   kind checked.
2. Add the pipelines, by the host file, as a change request of their own.
   They are never part of a repository's first push, which would deploy
   before anyone had looked: a repository that is being initialized takes
   this step once it is on its host, and stands until then as one that is
   versioned and has no pipeline.
3. Say in `DESIGN.md` that the repository is versioned and where its packages
   go, and add the files step 1 put at the root to the list of root build
   files in `AGENTS.md`.
4. Record it in `.agentics.yaml`, under `delivery:` — `versioning: true`,
   the `pipeline`, and the variable's name as confirmed — and remove the
   entry initialization left in `TODO.md`. Remind the user of what is theirs
   to do and no agent can: the host file lists it.

Step 1, with 3 and 4, stands without the pipeline: a repository can be
versioned and have none. The `TODO.md` entry then stays, reworded to the
pipeline alone.

**The `dev` pipeline alone** is step 2 and step 4 and nothing else: the
change request of step 2 adds `build.yml` and `dev.yml` and leaves `ci.yml`
where it is, and step 4 records the `pipeline` with `versioning: false`.
The `TODO.md` entry stays, reworded to the version and the pipeline that
publishes. A repository that later publishes takes the other steps then,
and adds `ci.yml` beside the two it has.

**The copies are the repository's.** A sync replaces the templates and not
the files copied from them, and its report does not yet say which
templates moved. After a sync, and before it is committed, look:

    git status --short -- agentics/templates/

Where one of them is listed, compare it with the copy and take what
applies.

## Toolchains

| Toolchain | File |
|---|---|
| `dotnet` | [`dotnet.md`](dotnet.md) — `Version.props` and `Packaging.props`, the build's two properties, what NuGet asks of a version, and the first step of adopting |

Only .NET today. Another toolchain gets a sibling file and a row; what it
cannot do as this file says — a version that is a tag and not a file, a
toolchain with no registry — is said there, and this file is then reworded
to what both hold to.

## Hosts

| Host | File |
|---|---|
| GitHub | [`github.md`](github.md) |

Only GitHub today. For another host, add a sibling file here with the same
sections, and a row above; do not generalise this file around it.
