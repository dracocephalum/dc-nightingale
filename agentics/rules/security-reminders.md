# Security & privacy reminders

A checklist for any repository that may be pushed to a remote, public or private.

These are **reminders, not enforcement**. Nothing here blocks anything. Adopting
them — and deciding which to make mandatory — is the consuming project's call.
*Turning reminders into gates*, near the end, covers making the ones that matter enforceable.

Cheapest time to act on all of this is before the first push. Most items become
a history rewrite afterwards.

## What must not land in a tracked file

Applies to code, comments, docs, config, tests, fixtures, and commit messages
alike — including example and placeholder values, which get copied verbatim far
more often than people expect.

- **Machine-specific paths.** Absolute paths naming a drive, a home directory, or
  a developer's folder layout. They break for everyone else and reveal the
  author's environment. Use relative paths, environment variables, or a
  documented placeholder.
- **Personal information.** Real names, email addresses, usernames, hostnames,
  internal domain names.
- **Credentials.** Tokens, API keys, connection strings, passwords. A secret
  committed once is compromised, even if the next commit removes it — it stays
  in history and in every clone and fork.
- **Internal identifiers.** Ticket URLs, internal hostnames, account numbers, and
  similar, where the repo may be public.

If a real path or identifier is genuinely needed to run something, put it in a
gitignored local config file and reference that file instead. Commit a
`.example` version with placeholder values.

## Names that are not yours to publish

Governed by `repository.publish-safe` in `.agentics.yaml`, default `true`.

When it is `true`, everything that leaves the machine — code, comments,
documents, examples, commit messages, issue and pull-request titles and bodies,
review comments — refers to other repositories, projects, customers, systems,
and people **by role, never by name**: "the standalone reference repository",
"the upstream service", "the customer". Examples use `Contoso`. The context an
agent works in is full of such names, and they land in a pull-request body
without anyone deciding to publish them.

**The test for a name is whether it is already here.** A name that appears in
this repository's tracked files was published deliberately and is free to use;
one that does not is treated as internal:

    git grep -il '<name>' -- . ':!node_modules'      # any hit: already published

That needs no list of internal terms. A list would itself be the most
concentrated internal information in the repository, and could never be
committed. The test is best effort by nature — a paraphrase carries no name to
check — and the draft pull request is the backstop.

When it is `false`, the repository will never be public, and its text may name
internal things freely. **It governs names only.** Everything in the section
above — paths, personal information, credentials, internal identifiers — is
never allowed in either setting.

**The flag is one-way.** `true` can become `false` at any time; nothing is
lost by relaxing. `false` becomes `true` only for a new repository: the history
already holds what it holds, and editing a value does not remove it.

## Commit identity

Git writes an author identity into every commit object, and it is immutable.
This one is easy to miss because **git does not refuse to commit when identity
is unset** — it silently derives one from the machine, typically
`<local-username>@<hostname>`, and bakes that into the history.

Check what will actually be used, from inside the repo:

    git var GIT_AUTHOR_IDENT

If that shows a machine-derived value, set an identity deliberately before
committing. On GitHub, the `ID+username@users.noreply.github.com` form keeps a
real address private while still linking commits to the account; the numeric ID
is on the account's email settings page. The bare `username@users.noreply...`
form is legacy and does not reliably link.

Audit what history already contains before a first push:

    git log --all --format='%an <%ae>' | sort -u

Cloned repos will legitimately list upstream contributors. What matters is
whether *your* commits carry something you didn't intend to publish.

## Tooling that adds its own metadata

AI coding tools, IDEs, and CI systems may append trailers to commit messages or
PR bodies. Some embed session URLs, account identifiers, or machine details.

Check what your tools add, and configure them if the metadata is more than you
want public. This is separate from the content rules above — the tool adds it
after you write the message, so reviewing your own text is not enough.

## Turning reminders into gates

A checklist depends on everyone remembering. These do not:

- **`.gitignore`** — local config, environment files, credential stores, editor
  and OS cruft. Add entries *before* the files exist; ignoring a file that is
  already tracked has no effect.
- **Secret scanning in pre-commit** — `gitleaks` or `trufflehog` as a hook
  blocks the commit rather than reporting it later. Run the same scan in CI so
  it cannot be bypassed with `--no-verify`. A machine-wide gitleaks hook, with
  the checks that prove it is actually active, is in the toolkit's
  `machine/gitleaks.md`.
- **Platform push protection** — GitHub's secret scanning push protection
  rejects pushes containing recognized credential formats. GitHub also offers a
  per-account setting to block command-line pushes that would expose a real
  email address, which catches identity misconfiguration on a new machine or in
  a repo with a local override.
- **`git secrets`-style path checks** — a hook rejecting absolute-path patterns
  is straightforward to write and catches the most common portability leak.

## If something has already been pushed

Treat a pushed secret as compromised and **rotate it first**. Removing it from
history does not un-publish it: it may already exist in forks, clones, CI logs,
and platform caches.

Only after rotating is it worth rewriting history, and be aware that a rewrite
changes every downstream commit hash and requires a force-push plus coordination
with anyone who has cloned the repository.
