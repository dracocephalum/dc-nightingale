# Delivery on GitHub

The mechanics of [`delivery.md`](delivery.md) on GitHub: a pipeline is a
GitHub Actions workflow, and the registry for `ci` builds is GitHub Packages.

Verified on a repository of a personal account, with two packages, private
and then public: the three workflows, a pull request that shows the one
check of `dev` and no deploy, a merge that deploys, a deploy run a second
time, the listing of versions, a restore of a published package on a
developer's machine, a change to none of the paths starting nothing, the
packages staying private when the repository was made public, and a deploy
to them afterwards; and, in a second repository, its `dev` pipeline
restoring the first one's packages, before that change and after it. **Not
yet verified:** a merge of a change to a tool or a test alone starting no
`ci` run; the removal of a version, which first happens when a package has
more `ci` versions than are kept; a package first published from a
repository that is public; an organization's repository.

## Adding the pipelines

Three files go to `.github/workflows/`: `build.yml` from
`agentics/templates/dotnet/`, the toolchain's, and the other two from
`agentics/templates/`:

| File | Is | Taken by |
|---|---|---|
| `build.yml` | the steps the others share, never started by itself | every repository that takes either of the others |
| `dev.yml` | the `dev` pipeline: every pull request, build and test | every repository |
| `ci.yml` | the `ci` pipeline: every push to the default branch, build, test, pack, deploy | a repository that publishes a package |

One part of `dev.yml` and of `ci.yml` is the repository's: the list under
`on:` of what the pipeline is started by, *It runs for what it builds*
below. Each comes as a standalone repository's; a monorepo puts its
category folders where `src/**` and `test/**` stand, and an entry for a
file the repository does not have, `NOTICE` where there is no licence, is
taken out. The rest is used as it is, and builds every solution git tracks.
`ci.yml` deploys from `main`, and a repository whose default branch has
another name changes that one line, under `on:`. Open a pull request with
them; `dev` runs on that pull request, and `ci` first runs when it is
merged. Never push `ci.yml` to the default branch directly, a new
repository's first push included: that push deploys at once.

A repository may have any number of workflows, one file each, and each
with a list of its own. These are the ones the toolkit ships.

**What is the user's to do**, and to be reminded of when the pipeline is
added:

- make the token for reading packages and set the variable, below, on each
  machine that restores them - the two files that use it are an agent's to
  write, the token is not;
- give `gh` the package scope, if the registry is to be looked at from the
  machine;
- give another repository's workflows read access to a package, on the
  package's settings page, when one is to build against it.

None of it is needed for the pipeline itself to build and publish.

## What the workflow does, and why

- **The steps are written once**, in `build.yml`, which the other two call
  with the kind of build they want. A check is then named by the job that
  calls and the job called, *Build and test / Solutions*, under the
  workflow's name, *Dev*.
- **The kind of build is set once**, as the environment variables
  `BuildKind` and `BuildNumber`, the second from the run's number. MSBuild
  reads its properties from the environment, so every `dotnet` command of the
  run builds the same version without being told.
- **A pull request has no deploy**, not one that is passed over: `dev.yml`
  has no such job. Nothing a pull request starts can reach a registry.
- **Restore is `--locked-mode`.** A restore that would change a lock file
  fails.
- **Deploy publishes the artifact of the build it follows**, not a second
  build, so what is published is what was tested. The artifact is kept one
  day: it shares the account's storage with the packages.
- **A called workflow has the permissions of the job that calls it**, so
  `build.yml` names none, and what a pipeline may do is read in the
  pipeline's own file.
- **Deploy is skipped when nothing was packed**, so the workflow suits a
  repository with no package.
- **Deploy can be run again.** A run that is started again keeps its number,
  so the push passes over a version the registry already has.
- **Actions are pinned to a commit**, with the release it belongs to in a
  comment. A tag can be moved to other code; a commit cannot. Raising one is
  a dependency change: look the release's commit up, and change both.
- **The runner is Linux.** It is the cheapest, and a build that passes on a
  developer's Windows machine and on a Linux runner has been tried on two
  platforms.

## It runs for what it builds

A workflow starts for a change to a path on its list, `paths:` under
`on:`, and for no other.

- **On `dev.yml`'s list**: the workflow itself and `build.yml`; the root
  files every project inherits, with `.editorconfig`, which the analyzers
  read, and `.gitattributes`, which decides the bytes of a checkout; what is
  packed beside the assemblies; and every folder of code. The root files are
  not optional: a list of the code alone stays silent when a root file
  changes what every project builds as.
- **On `ci.yml`'s list**: the same, less what no package is built from: the
  tests and `Tests.props`, and in a monorepo the categories that are never
  published. A change to those alone is built and tested as a pull request
  and publishes nothing when it is merged.
- **On neither**: documents, `agentics/`, the settings and whatever no step
  reads. A file that a new step reads is added in the change that adds the
  step.
- **Two lists, kept in step by hand.** A file cannot share a list with
  another, so a root build file added to one is added to the other in the
  same change. The scrub prints what `dev.yml` has and `ci.yml` lacks, for
  a reader to tell what is left out on purpose from what was forgotten.
- **Order matters.** The last entry, `'!**/*.md'`, takes every markdown file
  away again, a component's `README.md` among them. An entry after it puts
  one back: a readme that is packed is named there.
- **A pull request that touches nothing on the list has no run and shows no
  check.** That is the point of it, and it is why `dev` cannot be a
  required status check as it stands: such a pull request would wait for a
  check that never reports. A repository that requires the check moves the
  filter into the job, which then starts every time and ends at once.

The list is checked against the tree by
[`../scrub.md`](../scrub.md), *A pipeline's trigger list is still true*.

## No stored secret

The workflow uses the token GitHub makes for each run. The deploy job asks
for `packages: write` and gets nothing else beyond reading the repository;
the token is gone when the run ends. Nothing is added under the repository's
secrets, and no person's token is in a pipeline.

A package pushed with that token, from a build whose `Packaging.props` sets
`PublishRepositoryUrl`, belongs to the repository that pushed it: the repository's workflows may
administer it. Who else may see it is *A package's visibility is its own*
below.

## A package's visibility is its own

A NuGet package on GitHub belongs to the account, not to the repository
that publishes it, and has a visibility of its own. Three things follow,
each as the host documents it
([access and visibility](https://docs.github.com/en/packages/learn-github-packages/configuring-a-packages-access-control-and-visibility),
[the NuGet registry](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-nuget-registry)):

- **A package is private when it is first published**, whatever its
  repository is.
- **Making the repository public does not make its packages public.** Seen:
  two packages stayed private, their pages gave a visitor who was not
  signed in a 404, and neither was listed beside the repository.
- **A package made public cannot be made private again.** So no agent
  changes a package's visibility, and none asks for it as a step on the
  way to something else.

**The setting that sounds as if it decided this does not.** *Inherit access
from source repository*, on a package's settings page, is about who has
access: on, whoever may read or write the repository may read or write
the package; off, only those the page lists. It gives the public nothing
either way. It was tried both ways on a public repository, and the
packages were as private with it on as with it off. Leave it as it is; no
command changes it, and nothing here needs it changed.

`ci` packages are meant to stay private, and [`delivery.md`](delivery.md),
*Two registries, one kind each*, has why. A repository that wants them seen
says so, and the change is its owner's to make on each package's page.

**Check, when the repository's visibility is about to change and when a
package has first been published:**

    gh api --paginate "users/<owner>/packages?package_type=nuget" \
      --jq '.[] | select(.repository.full_name == "<owner>/<name>") | "\(.name) \(.visibility)"'

An organization's are under `orgs/<owner>`. The stored login needs
`read:packages`, *Reading a package* below. Every line is to end in
`private`. One that does not is told to the user at once and changes
nothing else: it cannot be undone, and the repository's visibility is
still theirs to decide. A scrub asks the same,
[`../scrub.md`](../scrub.md), *The host's packages are as private as they
are meant to be*.

### When a package is first published

A package the pipeline has not published before needs little, and one
thing of it is the user's. In order, the day the change that adds it is
merged:

1. **Run the check above.** The new package is to be there, `private`.
   This is what the host documents and has not been seen for a repository
   that is public; where it reads `public`, say so at once.
2. **Nothing for the repository that publishes it.** A package a workflow
   publishes gives that repository's workflows leave to administer it.
3. **For each repository whose pipeline is to restore it, the user gives
   read access**, on the package's page: *Package settings*, *Manage
   Actions access*, *Add repository*, role *Read*. No command does this.
   Name the package, its page,
   `https://github.com/users/<owner>/packages/nuget/<package>/settings`,
   and the repositories; *Another repository's pipeline* below has what a
   restore without it looks like.
4. **In each such repository**, the pattern of packages its two ignored
   files and its pipeline read from the registry is to match the new
   name, and its `TODO.md` entry on reading the registry names the
   package. A machine that restores it needs nothing new: the token reads
   every package its owner can.

The same list serves a package that is renamed, which the host takes for
a new one.

## Limits to know

- **Space.** A free account has 500 MB for private packages, shared with
  workflow artifacts, and 1 GB of download a month; a download by a workflow
  with the run's token is not counted. Public packages are free. The workflow
  keeps the newest 20 `ci` versions of each package, `KEEP_CI_VERSIONS`.
- **A public package's version cannot be removed once it has been downloaded
  more than 5,000 times.**
- **Minutes.** A private repository's workflows are metered; a public one's
  are not.
- **No approval step on a private repository.** On the Free, Pro and Team
  plans, an environment's required reviewers are for public repositories
  only.

## Reading a package

Reading always needs a token, a public package included, and only a classic
personal access token works with the NuGet registry.

- **A person's machine**: a classic token with `read:packages` and nothing
  else, in an environment variable. The name is the user's to confirm when
  the repository adopts a pipeline: offer `GITHUB_PACKAGES_TOKEN`, take what
  they say, and record it as `delivery.packages-token-variable` in
  `.agentics.yaml`, which is the name every configuration file then uses.
  Never `GH_TOKEN` or `GITHUB_TOKEN`, whatever is offered: the `gh` command
  reads those two before its own stored login, and a token that only reads
  packages would then stand in for it. The token itself is never written to
  a tracked file, never asked for in a conversation, and a variable set
  while a session runs is not seen by it until it is started again.
- **Another repository's workflow**: the run's token, once that repository
  is given read access on the package's own settings page.
- **Looking at the registry with `gh`**: the stored login needs the scope,
  which is not part of a machine's setup. Check `gh auth status`, and hand
  the user `gh auth refresh -h github.com -s read:packages` when it is
  missing; then check that the git protocol is still the one it was.

### On a machine, through two files git ignores

The committed `nuget.config` names nuget.org alone, and maps every package
to it, so a checkout builds from public sources and says nothing of a
private registry. A repository under the exception in `delivery.md`, *A
committed reference names a release*, reads the registry through two files
at its root that are never tracked. Verified with the `dotnet` command on
Windows: a solution restored, restored again in locked mode, and built.

1. **`nuget.local.config`**: a copy of `nuget.config` with the registry
   added to it. The source, the packages that come from it, and the token
   by its variable:

       <packageSources>
         <!-- what nuget.config has, then: -->
         <add key="github" value="https://nuget.pkg.github.com/<owner>/index.json" />
       </packageSources>
       <packageSourceMapping>
         <!-- what nuget.config has, then: -->
         <packageSource key="github">
           <package pattern="<Prefix>.<Component>*" />
         </packageSource>
       </packageSourceMapping>
       <packageSourceCredentials>
         <github>
           <add key="Username" value="token" />
           <add key="ClearTextPassword" value="%GITHUB_PACKAGES_TOKEN%" />
         </github>
       </packageSourceCredentials>

   - It is a whole copy because a restore pointed at a file reads that file
     and no other. What `nuget.config` says is repeated here or lost, and a
     later change to `nuget.config` is made in both.
   - The variable is the one recorded as `delivery.packages-token-variable`.
     `%NAME%` is NuGet's spelling on every platform, and it is read when the
     restore runs: the file holds the name, never the token.
   - The registry does not compare the username with the token, so nobody's
     is written.

2. **`Build.local.props`**, which points the restore at it:

       <Project>
         <PropertyGroup>
           <RestoreConfigFile>$(MSBuildThisFileDirectory)nuget.local.config</RestoreConfigFile>
         </PropertyGroup>
       </Project>

3. **Once for the repository, and committed.** At the end of
   `Directory.Build.props`, the import of the file where there is one:

       <Import Project="$(MSBuildThisFileDirectory)Build.local.props" Condition="Exists('$(MSBuildThisFileDirectory)Build.local.props')" />

   and both names ignored. `.gitignore` has `*.local.props`; add
   `*.local.config` beside it when it is not there. Then:

       git check-ignore Build.local.props nuget.local.config     # prints both names

   Do this before either file is made, so that neither is ever seen as
   untracked.

Each machine makes the two files for itself; an agent may write them, since
they hold no secret. What goes wrong reads as follows:

| At restore | Cause |
|---|---|
| `NU1101`, the package not found, with nuget.org the only source named | the two files are not there, or the import line is not |
| `NU1301` with `401 (Unauthorized)`, and a warning that the request could not be authenticated | the variable is not set where the build runs, or the token lacks `read:packages` or has expired |
| `NU1100`/`NU1101` on a package from nuget.org | `nuget.local.config` has fallen behind `nuget.config` |

The same holds on Linux, seen in a pipeline, below. **Not yet verified**:
macOS, and an editor's own restore.

### Another repository's pipeline

For a repository under the same exception whose pipeline must restore a
`-ci.` package another repository published. Verified with two private
repositories of one personal account: a restore on the host's Linux
runner, in locked mode for one package and of a scratch project for a
second. The order is the rule's, and the second step is why none of this
is offered at initialization:

1. **The repository is on GitHub.** A package cannot be told about one
   that is not.
2. **The user gives it read access**, for each package it restores: the
   package's page, *Package settings*, *Manage Actions access*, *Add
   repository*, role *Read*. An agent cannot do this and says so, naming
   the packages - every one the restore fetches, a dependency of a
   package included, and again for a package added later. The same page
   has *Manage Codespaces access* beside it, which looks alike and does
   nothing here.
3. **The pipelines are added**, as a change request of their own, with
   three things the templates carry commented out, each marked
   `TEMPORARY`. Take the comment marks off all three and fill in the one
   value, the pattern of the packages that come from the registry:
   - `packages: read` under `permissions` of the job that calls
     `build.yml`, in `dev.yml` and in `ci.yml` where the repository has it;
   - in `build.yml`, a step before the restore that writes the two files
     of the section above, since they are in no checkout;
   - in `build.yml`, the variable those files name, set on the restore
     step to the run's own token, `${{ github.token }}`. No stored secret
     is added.
4. **Two entries in `TODO.md`**, one for the committed `-ci.` reference and
   one for the pipeline's three additions, both closed by the package's
   first release: the reference then names it, and the additions go.

A restore that fails with `NU1301` and `403 (Forbidden)` on the package,
while the run's log shows `Packages: read` for the token, is step 2: the
package has not been given the repository under *Manage Actions access*,
or was given it under *Manage Codespaces access*.

The registry keeps only the newest `ci` versions of a package, so a
committed reference is raised before the version it names is removed.
