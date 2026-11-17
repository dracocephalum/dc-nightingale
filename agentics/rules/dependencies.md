# Dependency licensing

Applies to every third-party package a repository takes on, in any ecosystem,
**including transitive ones** — the package you did not choose is still yours to
ship. Adding, upgrading, or replacing a dependency triggers this document.

This is engineering policy, not legal advice. Anything outside *Allowed* goes
to a human.

## Policy, by SPDX identifier

| Tier | Licences | Meaning |
|---|---|---|
| **Allowed** | `MIT`, `MIT-0`, `0BSD`, `BSD-2-Clause`, `BSD-3-Clause`, `ISC`, `Apache-2.0`, `Unlicense`, `CC0-1.0`, `Zlib`, `PostgreSQL`, `MS-PL` | use freely; retain notices when redistributing |
| **Review** | `MPL-2.0`, `LGPL-2.1`, `LGPL-3.0`, `EPL-1.0`, `EPL-2.0`, `CDDL-1.0`, `MS-RL`, URL-only or custom licences | usable with conditions; a person decides and records why (*File licences and accepted exceptions* below) |
| **Denied** | `GPL-*`, `AGPL-*`, `SSPL-*`, `BUSL-*`, `Elastic-2.0`, Commons Clause, commercial, "source-available", **no licence at all** | do not add; find an alternative |

Two corrections to common assumptions:

- **`BSD-3-Clause` needs no extra handling.** Its third clause is a
  *restriction* — do not use contributors' names to endorse your product —
  not an obligation. The "advertising clause" that did demand acknowledgement
  was `BSD-4-Clause`, obsolete and not in the allowed list.
- **"Permissive" does not mean "no obligations."** MIT, BSD and Apache all
  require the copyright notice and licence text to travel with redistributed
  code. `Apache-2.0` also propagates a `NOTICE` file when the project has one.
  For anything you *ship* — a NuGet package from `libraries/`, a container, an
  installer — generate a `THIRD-PARTY-NOTICES.txt`. For an internal service it
  is good hygiene; for a published artifact it is the licence.

**A version bump can change the licence.** MediatR moved to a commercial
licence at v13; the package id did not change. The check runs on every
restore, not once at adoption.

## Enforcement — .NET

The repository ships a local tool manifest, an allow-list, and an override
file. From the repository root:

    dotnet tool restore
    dotnet nuget-license -i <solution>.slnx -t -a allowed-licenses.json -override license-overrides.json

`-t` walks the **transitive** graph. Exit code is non-zero on any package
outside the allow-list, so this belongs in CI as-is.

### URL-only packages and the override file

Packages published before NuGet supported SPDX expressions carry only a
licence *URL*, which no tool can classify. They show up as `Url` in the
output and fail the check. Resolve each one **by reading the licence at that
URL**, then record the verified identifier in `license-overrides.json`:

    { "Id": "Fare", "Version": "2.1.1", "License": "MIT" }

Overrides are pinned to a version on purpose: a new version must be verified
again. The shipped file already covers the four such packages the toolkit's own
test stack pulls in. An override is a **claim you have checked**, never a way to
make the check pass.

To find who is pulling a transitive package in: `dotnet nuget why <PackageId>`.

### File licences and accepted exceptions

A package may carry its licence as a **file** inside the package: no
identifier, and often terms that are not open source at all — the native
network library every SQL Server client pulls in is one. It shows up as `File`
in the output and fails the check. Such terms are in the *Review* tier: a
person reads them and decides, and the decision is for one use, a tool that
only tests is not a component that ships. An acceptance is recorded in three
places, so that it is neither invisible nor a false claim:

1. **The text**, copied byte for byte from the package to
   `licenses/<identifier>.txt`. The identifier is the repository's own, in the
   SPDX form for one: `LicenseRef-<Owner>-<Name>`. The copy is what was read
   and agreed to; the file in a later version can change with nothing to flag
   it. `.gitattributes` keeps `licenses/` byte-exact, so its hash holds on
   every checkout.
2. **The record**, an entry in `license-exceptions.json` at the repository
   root, a file made with the first exception as `licenses/` is, so that
   neither exists where nothing was accepted: what was accepted,
   where the text came from so it can be fetched again, its hash, when, for
   what use, and the conditions the terms set.

       {
         "License": "LicenseRef-<Owner>-<Name>",
         "Id": "<PackageId>",
         "Version": "<version>",
         "Source": "https://api.nuget.org/v3-flatcontainer/<id, lower case>/<version>/<id, lower case>.<version>.nupkg",
         "File": "<path of the licence inside the package>",
         "Page": "https://www.nuget.org/packages/<PackageId>/<version>/License",
         "Text": "licenses/LicenseRef-<Owner>-<Name>.txt",
         "Sha256": "<hash of the text>",
         "Accepted": "<date>",
         "Use": "<test only | redistributed with <what>>",
         "Conditions": ["<each thing the terms require or forbid that the use touches>"]
       }

3. **The claim to the tool**: an override naming the identifier, pinned to the
   version like every override, and the identifier in `allowed-licenses.json`.
   Both are needed; the override alone still fails, the identifier being
   outside the allow-list. The allow-list is the repository's own, so the
   acceptance is its own decision and nothing another repository inherits.

       { "Id": "<PackageId>", "Version": "<version>", "License": "LicenseRef-<Owner>-<Name>" }

Never record such a package under an open-source identifier, and never pass
it to the tool's ignore list: the first is false, and the second takes the
package out of the check for every later version too.

A package is a zip, so the text is fetched and checked without restoring
anything:

    curl -sL <Source> -o package.zip
    unzip -p package.zip <File> | sha256sum       # the hash to record
    sha256sum licenses/<identifier>.txt           # the copy, the same hash

**A new version is verified again**, and the pin is what forces it: the
override no longer matches, so the check fails. Fetch the new version's text
and compare its hash with the record's. The same hash is the same terms: move
the version in the override and the record. Another hash is another licence:
read it, and replace the copy, the hash and the date, or stop.

Conditions that travel with a shipped artifact, a notice or the terms
themselves, go in its `THIRD-PARTY-NOTICES.txt` like any other licence's.

## Enforcement — npm

    npx license-checker-rseidelsohn --production --onlyAllow "MIT;MIT-0;0BSD;BSD-2-Clause;BSD-3-Clause;ISC;Apache-2.0;Unlicense;CC0-1.0"

`--onlyAllow` fails on the first package outside the list. Use the maintained
fork named above; the original `license-checker` has been unmaintained since
2022.

## Enforcement — pull requests

On GitHub, `actions/dependency-review-action` blocks a pull request that
introduces a dependency outside `allow-licenses` (SPDX identifiers). It needs
the dependency graph, which for NuGet sees transitive packages only through
lock files — set `RestorePackagesWithLockFile` if relying on it. Free for
public repositories; private ones need GitHub Advanced Security.

It complements the tool-based check rather than replacing it: the action sees
the diff, the tool sees the whole graph.
