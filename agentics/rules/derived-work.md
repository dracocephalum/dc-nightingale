# Derived work

Applies when a repository builds, commits or publishes something made from
what is not its own to license: a translation or a port of another's code,
another's data, files generated from another's sources. It applies where
their terms are not a permissive licence with an identifier, or are not
clear. A dependency that is only referred to is not this:
[`dependencies.md`](dependencies.md).

A repository's own work is released as [`delivery/delivery.md`](delivery/delivery.md)
says, and needs none of what follows. A tool that makes derived work from
its user's input is the repository's own, and is released so; its page
says that what it writes carries the terms of what it was given, and not
the tool's.

The steps are in the order the work meets them.

## 1. Settled before anything derived is committed or packed

What may be published at all is the owner's to settle with the other
party. It is settled before the first derived file is committed and before
the first package is built by a pipeline, not before the first release:
a pushed commit is published the day it is pushed, and a new package may
start public on its host whatever was meant
([`delivery/delivery.md`](delivery/delivery.md), *Two registries, one kind
each*).

- **It is the user's to do, and no agent's.** An agent that finds derived
  work with nothing recorded stops and asks, and does not add such a
  project to a pipeline.
- **What was settled is recorded in the repository's own words**, with
  the other party's published rules as its source: in the plan until the
  package is built, and from then in `DESIGN.md`, where the split of the
  terms is described. What they wrote in private is not quoted, there or
  anywhere.
- **"Terms" is more than copyright.** What they say of export, of the use
  of their name, and of what the work may be used for is read with the
  rest.
- **Where the answer is no, or there is none, nothing derived is
  published.** The tool that makes it is published instead, and whoever
  wants the result fetches the original and makes it on their own
  machine.

## 2. Their original is fetched, and not held

What the work is made from is fetched from where they publish it, by an
address and a hash the repository commits, into a folder git ignores. It
is neither committed nor packed. A build from one commit then reads the
same bytes every time, or fails and says the original has changed.

That keeps the repository small and nothing stale in it. It does not
lighten the terms: what is made from their work is theirs in another
form, and the steps below are what answer for it.

## 3. What is derived has a component and a package of its own

- **One component, one package**, with nothing in it that could stand
  outside it. The few files of the repository's own that it needs, a
  project file, a stub, are there and say whose they are (step 4).
- **The repository's own code that builds on it is another package**,
  which depends on that one and is under the repository's licence alone.
  So one package carries their terms, and every other package's terms
  stay an identifier. A package that depends on the derived one says on
  its page that it brings it in, and under which terms.
- **The component is declared** in `.agentics.yaml`, under
  `derived-work`: its folder, its terms file, its tag, and which files
  carry a tag. A scrub reads the declaration:
  [`scrub.md`](scrub.md), *Derived work is marked*.

## 4. Each file says whose it is

One line among a file's opening comments, before any code, in whatever
language the file is written:

    SPDX-License-Identifier: <identifier>

| File | Identifier |
|---|---|
| the repository's own, in a declared component | the repository's licence, `Apache-2.0` say |
| derived | `LicenseRef-<name>`, the form for terms that have no identifier; the name is the repository's to choose and says whose terms they are |

- **A derived file's first lines also say** what it was made from and of
  which version, that it is changed and is not their product, and that
  questions go to the repository it is kept in. Whatever notice or
  disclaimer the original file carried is kept as it stood.
- **The tool that makes the file writes them**, so they are never
  missing, and a test of the tool asserts it of every file it writes. The
  tag is one constant of the tool's. A test never contains the whole tag
  line: it builds that line from the constant, and it pins the constant's
  value once on its own, so a constant that is wrong still fails. The
  line then stands in no file but a derived one, and a scrub that reads
  for it outside the component finds none.
- **A mark that a file was generated says who wrote it, not whose terms
  apply.** A generated file is tagged like any other. Where a toolchain
  has such a mark and wants it first, the tag stands below it.
- **A file that cannot carry a comment** is covered by a sentence in the
  component's terms file. So is every file the declaration does not name
  as tagged.
- **Outside a declared component no file needs a tag.** The repository's
  licence covers it.

## 5. The component's terms file

`LICENSE.md` in the component's folder, from
`agentics/templates/component-LICENSE.md`. It is the package's licence in
place of an identifier, and the toolchain's file under
[`delivery/`](delivery/delivery.md) has how a package names it.

- **It says which terms cover which files**, by the tag each carries,
  names where the other party publishes theirs, and is packed with the
  repository's own licence text beside it.
- **Their notice or disclaimer is quoted there once, from where they
  publish it**, with the address, the heading it stands under and the
  month it was read, and a sentence that their page is the authority
  where the two differ. That is beside what each derived file keeps, and
  does not replace it.
- **The repository's `NOTICE` says what its licence does not cover**, and
  points at the component's file.

## 6. The package says what it is not

- **In its description and on its page:** that it is unofficial, that the
  other party neither made nor supports it, and that questions go to
  this repository and not to them. The page names where their own work
  is to be had.
- **Its tags and its name do not borrow theirs** beyond what says what it
  is made from.

## 7. Kept true

- **Their terms are read again when a new version of their work is
  taken**, and the month beside the quoted text changes with it.
- **A file made at build is checked by the build.** A scrub reads only
  what git does not ignore. Where the derived files are written as the
  project builds and are ignored, the test of step 4 is what holds them,
  and the package is not built from a file without its tag.
- **A file that is committed is checked by the scrub**, the day it stops
  being ignored, with nothing to change in the declaration.

## Review

| Finding | Rule |
|---|---|
| derived work committed or packed with nothing recorded of what was settled | step 1 |
| their original committed, or packed | step 2 |
| the original fetched without a committed hash | step 2 |
| a derived file outside its component, or the repository's own code inside it that could stand outside | step 3 |
| a file in a declared component, of a kind the declaration names, without a tag | step 4 |
| a derived file that lost the notice its original carried | step 4 |
| a package of derived work under a licence identifier | step 5 |
| their disclaimer copied from memory or from their sources, with no address and month | step 5 |
| a description or a page that does not say it is unofficial | step 6 |
