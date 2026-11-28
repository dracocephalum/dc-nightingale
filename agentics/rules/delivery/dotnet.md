# Delivery — .NET

The .NET half of [`delivery.md`](delivery.md), for a repository whose
toolchains include `dotnet`: the files that carry the version and the kind of
build, what NuGet asks of a version, the registries, and the steps that adopt
them. The rules themselves — the kinds of build, a pipeline for each, what a
committed reference may name — are in `delivery.md`, and are not repeated
here.

## The files

| File | Holds |
|---|---|
| `Version.props`, at the repository root | the one number. A component versioned apart has one of its own in its folder, found before the root's |
| `Packaging.props`, at the root, imported by `Directory.Build.props` | what follows the number, derived from the kind of build; the name packages are published under, the licence and the repository |

Before a repository adopts them it builds as the SDK's default `1.0.0`, with
the commit hash the root targets file appends.

## The kind of build

`Packaging.props` derives what follows the number from two properties the
build is given:

| `BuildKind` | `BuildNumber` | Version |
|---|---|---|
| `dev`, the default | not read | `1.2.0-dev` |
| `ci` | the pipeline's own counter; a `ci` build without one is refused | `1.2.0-ci.42` |
| `release` | not read | `1.2.0` |

- A kind that is none of the three is refused by the build.
- MSBuild reads its properties from the environment, so a pipeline sets the
  two once and every `dotnet` command of its run builds the same version.
- Never four numbers. `1.2.0.42` is a stable version to NuGet, and outranks
  the `1.2.0` it was built towards.
- The commit hash is appended to the informational version in every kind, as
  before.

## Registries

A release goes to nuget.org. A `ci` build goes to the host's NuGet registry,
and the host file has how it is written to, read and kept private.

## Adopting it

The first step of *Adopting it* in `delivery.md`, in five, each leaving the
repository building:

1. Copy `agentics/templates/dotnet/Version.props` to the repository root and set
   the number: `0.1.0` for what has never been released.
2. Copy `agentics/templates/dotnet/Packaging.props` to the root. Replace
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

The files the rule's third step adds to the list of root build files in
`AGENTS.md` are `Packaging.props` and `Version.props`.

## What a package holds beside its assemblies

`Packaging.props` packs these wherever they are, so no project file names
them:

| File | Packed as | For |
|---|---|---|
| `NOTICE` at the repository root | `NOTICE` | whoever redistributes the package |
| `PACKAGE.md` beside a project file | `README.md`, named as the package's readme | whoever sees the package on a registry |

- **A package's page is not the component's `README.md`.** That one is
  for someone in the repository, with links that hold only there.
  `PACKAGE.md` is for someone who has only the package: what it is, the
  few lines that use it, what it does not do yet, and whole addresses for
  the rest.
- **Every packed project has one before its first release**, started
  from `agentics/templates/dotnet/package-page.md`. A package without it
  shows a description of one line.
- **A package under terms that are not the repository's alone** names a
  licence file in place of the expression `Packaging.props` gives every
  package: an empty `PackageLicenseExpression` and a `PackageLicenseFile`
  in its project file, with the files packed beside it.
  [`../derived-work.md`](../derived-work.md) has what the file says, and
  which package is such a one.
- **A tool's package gives one command**, its `ToolCommandName`. A second
  program is not a second package: `delivery.md`, *What is not a package
  goes to the host's releases*.

## A version that is derived

A `Version.props` nearer a project than the root's need not hold a number.
Where a component's version is kept in a file something else writes, a
build step or a tool, its `Version.props` imports that file, and the
project files import nothing: `Packaging.props` finds the nearest
`Version.props` for every project beneath it.

A release that refers to a package with a label is refused by the packing
itself, `NU5104`: a version with no label cannot depend on one that has
one. It is a warning, and the root `Directory.Build.props` makes every
warning an error. That is the rule of `delivery.md`, *A committed reference names a
release*, kept by the tool, and is not to be silenced.

## Trying a package before it is published

A change to a package is tried in the repository that uses it before it is
merged, from a folder and never from a registry:

1. Pack the changed repository as `dev`, the default, to a folder outside
   both repositories.
2. Restore the using repository with a configuration, also outside it,
   that adds the folder as a source, and with the version overridden to
   the `-dev` one as `delivery.md`, *A committed reference names a
   release*, says: from a file git ignores or from the environment.
   Where the repository's own configuration maps each package to a
   source, this one maps the package to the folder, or the folder is
   never asked.
3. Remove that version from the machine's package cache before each
   restore. A `dev` build is made again under the same number, and the
   cache would hand back the one before.

Nothing of this is committed: no source, no version, no lock file written
against the folder.

## The steps a pipeline shares

`agentics/templates/dotnet/build.yml` is what the rule calls the shared
steps, for .NET: restore from the lock files in locked mode, build, run the
unit tests and, where it is asked to, pack. It finds every solution git
tracks, so it is used as it is in both layouts. The host file says where it
is copied to and why each step is as it is.
