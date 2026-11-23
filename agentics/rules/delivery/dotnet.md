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

## The steps a pipeline shares

`agentics/templates/dotnet/build.yml` is what the rule calls the shared
steps, for .NET: restore from the lock files in locked mode, build, run the
unit tests and, where it is asked to, pack. It finds every solution git
tracks, so it is used as it is in both layouts. The host file says where it
is copied to and why each step is as it is.
