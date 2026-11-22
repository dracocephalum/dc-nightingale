# Scrub — recurring consistency and drift checks

Applies when asked to scrub, sweep, audit, or check this repository for
consistency or drift; and as a matter of routine after a move or rename, before
a release, and whenever the toolchain has moved underneath the repository.

**This finds inconsistency, not bugs and not rule violations.** The bug pass is
the built-in `/code-review`; the rules pass is
[`coding/code-review.md`](coding/code-review.md). What the documents cost to
read, and making them smaller, is [`optimize.md`](optimize.md), and no part of
it runs here. Building and testing are in
`AGENTS.md` under *Building and testing*, and are deliberately not repeated
here: a scrub should be cheap enough to run without thinking about it.

## Report, do not fix

Produce a findings table and change nothing. Some findings are genuinely the
user's call — a baseline drift is a decision about whether to re-baseline, not
a file to rewrite — and a check that silently edits is a check nobody trusts.

    | # | Check | Result | Detail |
    |---|---|---|---|
    | 1 | Markdown lint | pass | 0 issues, 41 files linted |
    | 4 | Line endings | **fail** | AGENTS.md: 12 CRLF lines, 160 LF |

State `pass`, `fail`, or `skipped` with the reason, and for every failure what
would close it. Apply fixes only when asked, and then as a separate, reviewable
change.

## The checks

### 1. Markdown lint

    npx --yes markdownlint-cli2@0.23.2 "**/*.md" "#node_modules"

**Read the `Summary:` line, not the exit code — and the `Linting:` line above
it.** `Summary: 0 issues in 0 files` counts the files *with issues*, so it
reads the same when every file is clean and when the pattern matched no file
at all. `Linting: N files` is how many were looked at; zero there is a failed
check, not a clean one, and the usual cause is a pattern the shell or the
working directory did not resolve. The configuration is `.markdownlint.yaml`;
every rule left enabled points at a real defect.

### 2. Relative links resolve

Every `](path)` in every markdown file must point at something that exists:

    find . -name '*.md' -not -path './node_modules/*' | while read -r f; do
      sed -e 's/^    .*$//' -e 's/`[^`]*`//g' "$f" \
        | grep -oE '\]\([^)#][^)]*\)' | sed 's/^](//;s/)$//' | while read -r l; do
          case "$l" in http*|mailto*) continue;; esac
          [ -e "$(dirname "$f")/${l%%#*}" ] || echo "$f -> $l"
        done
    done

**The two `sed` expressions are not optional.** They blank indented code blocks
and strip inline code spans, because a document that *describes* this check
contains link-shaped text that is not a link — this file does, and without the
filter it reports itself. Any repository whose documentation quotes a path in
backticks hits the same thing.

Template files are the other exception: `agentics/templates/*` link into the
repository they are copied into, not the place they sit. Note them and move
on.

### 3. No unfilled placeholders

    for f in $(git ls-files '*.md' '*.yaml' | grep -v 'agentics/templates/\|\.template\.md'); do
      sed -e 's/^    .*$//' -e 's/`[^`]*`//g' "$f" | grep -n "{{" | sed "s|^|$f:|"
    done

A `{{PLACEHOLDER}}` outside a template is a document that shipped half-written.
The same filtering applies and for the same reason: a document explaining what
a placeholder looks like has to write one down.

### 4. No mixed line endings within a file

    for f in $(git ls-files); do
      cr=$(tr -dc '\r' < "$f" | wc -c); lf=$(tr -dc '\n' < "$f" | wc -c)
      [ "$cr" = "$lf" ] || [ "$cr" = 0 ] || echo "mixed: $f ($cr CR, $lf LF)"
    done

**Whole-file LF and whole-file CRLF are both correct.** `.gitattributes` sets
`* text=auto`, so the repository stores LF and each platform checks out its
own — which of the two a working copy holds says nothing. A file containing
**both** is the defect, and it survives review because no diff renders it.

**Know what git launders and what it does not**, or this check reads as either
paranoia or a false alarm:

| The file | On commit | So a mixed file |
|---|---|---|
| ordinary, under `* text=auto` | CRLF normalized to LF | never reaches the repository — verified: a 2 CR / 3 LF file staged as 0 CR |
| exempt from normalization (`-text`) | stored byte-for-byte | **persists**, and for anything hash-verified breaks the hash it exists to prove |
| in a path or repository with no `text` attribute | stored as-is | persists |

So the finding is real in the working tree you are about to commit from, and
real for good in an exempt file.

**`dotnet format` is the likeliest source, not a stray `sed -i`.** When it
inserts a line — the blank separator between using groups, say — it writes
that line with the OS-native ending, because nothing sets `end_of_line` in
`.editorconfig`. Measured: a pure-LF file at 0 CR and 20 LF came back from
`dotnet format analyzers` at 1 CR and 21 LF, and built green. Normally this is
invisible, since git checks out native endings too and the inserted line
matches the rest. It bites when something wrote **non-native** endings first —
an agent creating a file with LF on Windows. So after formatting a file you
created rather than checked out, run this check before committing.

### 5. Privacy

No local paths, no personal names or addresses, no credentials — in code,
comments, documentation, configuration, tests, or commit messages, example
values included. The standard is
[`security-reminders.md`](security-reminders.md); read it rather than
improvising a pattern list. A first sweep:

    grep -rniE '[a-z]:[\](users|dev)|/(home|users)/[a-z]|@(gmail|outlook|hotmail)\.' .

**Match the backslash with a bracket expression, `[\]`, never `\\`.** Escaping
a backslash inside an alternation is fragile: `grep -E` reads `\\(` as an
escaped literal paren, the groups fall apart, the `|` regroups at the top
level, and one branch degrades to the bare text `dev)`. The pattern then
matches ordinary prose while still looking correct. That is not hypothetical —
the first run of this check reported the URL `https://www.sigstore.dev)` as a
Windows path. A bracket expression has no escaping to get wrong.

Findings here are reported, never quietly deleted: a real credential in history
needs rotating, not removing.

**And names, when `publish-safe` is `true`.** There is nothing to grep for, so
read: other repositories, projects, customers, systems, or people named rather
than referred to by role, in documents and in the commit messages of the range
under review. The rule and its one test — is the name already in the tracked
files — are in [`security-reminders.md`](security-reminders.md), *Names that
are not yours to publish*.

### 6. `.agentics.yaml` still tells the truth

It records what was chosen at initialization. A repository drifts away from it
silently, because nothing enforces it:

| Setting | Check against |
|---|---|
| `licence` | the `LICENSE` file — present, and the same licence |
| `copyright-holder` | the holder named in `LICENSE` or `NOTICE` |
| `layout` | the actual tree: `standalone` has one solution at the root, `monorepo` has category folders and none |
| `delivery.pipeline` | the pipeline files git tracks: `none` has none, a host's name has that host's |

### 7. `AGENTS.md` index is complete in both directions

Every document under `agentics/rules/` is **reachable** from the table, and every
row's link resolves. A document nobody can arrive at will not be read, and a row
pointing at nothing teaches an agent to distrust the table.

    find agentics/rules -name '*.md' | sort
    grep -oE '\(agentics/rules/[^)]*\.md\)' AGENTS.md | tr -d '()' | sort -u

**And no row twice.** A table row that appears more than once is a local index
repeating a toolkit row; nothing else flags it:

    grep '^| ' AGENTS.md | sort | uniq -d

**The block between the `agentics:guidelines` comments is generated.** A
finding inside it is fixed at its source — the toolkit's template, or the
index under `docs/rules/` — and by re-running the sync, never by editing the
block; a row the repository typed into it by hand is itself a finding, because
the next sync removes it. [`layout.md`](layout.md), *The toolkit's rules and
the repository's*. A local index links relative to the repository root, where
its rows land, so check 2 resolves its links from there.

**Reachable, not listed.** Several documents are deliberately reached through
another — the C# and markdown checklists via `coding/code-review.md`, each host
file via the rule document that names it. A document missing from the table is
only a finding if nothing in the table leads to it, so follow the links one hop
before reporting. The reverse direction has no such nuance: a row whose target
does not exist is always a defect.

### 8. Baseline drift

Three files were forked from `dotnet new` templates and carry a marker naming
the SDK they were captured from and the SHA-256 of the pristine original:
`.editorconfig`, `.gitignore`, `.gitattributes`.

    dotnet new <name> -o <scratch>
    tr -d '\r' < <scratch>/<file> | sha256sum       # macOS: shasum -a 256

**Hash the newline-normalized bytes, never the raw file** — `dotnet new` emits
CRLF while `* text=auto` checks out LF elsewhere, so a raw hash false-positives
on every non-Windows machine.

**On a mismatch, stop and report; do not regenerate and do not merge.** Say
which file drifted and the recorded SDK against the installed one, and let the
user decide. It matters most for `.editorconfig`, whose reconciliation with the
ruleset depends on the generated content; for the other two the repository's
changes are additive, so drift is informational.

### 9. No rule stated twice

Two documents stating the same rule is a drift hazard, not merely length: the
copies diverge, and nothing marks which one is authoritative. Read for it rather
than grepping — the duplicates that matter are paraphrases, not copy-paste.

Three shapes worth reporting:

| Shape | Why it matters |
|---|---|
| The same rule, stated in full in two documents | they will drift, and a reader cannot tell which won |
| A document restating what the document it links to already says | the summary goes stale while the link stays correct |
| An instruction for something that no longer exists | it reads as current, and costs a reader the time to find out otherwise |

The fix is a pointer, not a second copy: name the document that owns the rule
and delete the restatement. Report these; deciding which copy is authoritative
is the user's call, not a mechanical one.

A pointer is a claim too, and these two are mechanical
([`coding/where-facts-live.md`](coding/where-facts-live.md)): every heading a
comment or a document names exists in the document it names, and every type,
member or setting a document names exists in the code. Report each that does
not; the home moved or was renamed, and the pointer was not in the same
change.

**This is about duplication, not brevity.** The scrub reports the duplicate;
shortening documents, whether by removing redundancy or because a model would
probably behave correctly without the instruction, is
[`optimize.md`](optimize.md), which says why the second is a different and far
riskier operation.

### 10. `TODO.md` is still true

Read every entry and ask whether it is still open. An entry that is done, or
that describes a state the repository has left, is worse than no entry: it is
the file agents are told to trust for what is unfinished. Report the stale ones
rather than deleting them — closing an item is the user's call.

The same reading applies to `PENDING.md` where the repository has one: an
entry that describes what has since shipped, or an approach since dropped.

**A reversed decision is the cheap moment.** When a change abandons an
approach that the documents had named — a design replaced, a planned slice
dropped for another — search for the abandoned approach's own words in that
same change, not at the next scrub:

    grep -rn --include='*.md' -i '<the abandoned term>' .

The sentences it finds are rarely the entry about the decision itself, which
gets rewritten. They are the other entries that leaned on it: a "close by"
that names the dropped slice as next, a pending item waiting on work that
will now never happen.

### 11. A pipeline's trigger list is still true

Skipped where the repository tracks no pipeline; one that is tracked while
`.agentics.yaml` says `delivery.pipeline: none` is a finding of check 6. A pipeline
names the paths its build reads and starts for no other
([`delivery/delivery.md`](delivery/delivery.md), *A pipeline for each
kind*). Nothing fails when the list falls behind the tree: the pipeline is
only never started for the file it does not name, and stays green from its
last run.

    triggers() {
      awk '/^ *paths:/ { if (seen) exit; seen = 1; next }
           seen && /^ *#/ { next }
           seen && /^ *- / { sub(/^ *- */, ""); gsub(/["\047]/, ""); print; next }
           seen { exit }' "$1"
    }

    for w in $(git ls-files '.github/workflows/*.yml'); do
      grep -q '^  workflow_call:' "$w" && ! grep -q -E '^  (push|pull_request):' "$w" && continue
      [ -n "$(triggers "$w")" ] || { echo "$w: no list, so every change starts it"; continue; }
      triggers "$w" | grep -v '^!' | while read -r p; do
        [ -n "$(git ls-files -- ":(glob)$p")" ] || echo "$w: matches nothing: $p"
      done
      grep -q '^  pull_request:' "$w" || continue
      git ls-files -- . $(triggers "$w" | grep -v '^!' | sed 's/^/:(exclude,glob)/') \
        | grep -v '^agentics/' \
        | grep -E '\.(cs|csproj|slnx?|props|targets|ruleset)$|(^|/)(packages\.lock\.json|BannedSymbols\.txt|\.editorconfig|\.gitattributes|global\.json|nuget\.config|stylecop\.json)$' \
        | sed "s|^|$w: read by a build, and no entry matches it: |"
    done

    # What the change request's list has and the publishing one lacks.
    d=.github/workflows/dev.yml; c=.github/workflows/ci.yml
    if [ -f "$d" ] && [ -f "$c" ]; then
      triggers "$d" | grep -v -e '^!' -e '^\.github/workflows/' | while read -r p; do
        triggers "$c" | grep -qxF -- "$p" || echo "$c: on $d's list and not on this one: $p"
      done
    fi

It prints nothing when the lists stand, but for the last line, which it
prints for whatever is left off the publishing list on purpose. What it
prints is one of four:

| Line | Means |
|---|---|
| `on dev.yml's list and not on this one` | a path the publishing pipeline is not started by. Right for tests, `Tests.props` and a category that is never published; a finding for a root build file or a folder of published code |
| `matches nothing` | an entry for a path that was moved, renamed or never there; the folder it was meant for is the one to look for |
| `read by a build, and no entry matches it` | a project, a source, a lock file or a root build file whose change starts nothing |
| `no list` | a workflow every change starts; a finding for one that builds, not for one that is meant to |

**Then read what no entry matches**, since `read by a build` knows a build
file only by its name:

    git ls-files -- . $(triggers <workflow> | grep -v '^!' | sed 's/^/:(exclude,glob)/')

A finding there is a file some project includes by its path, a file that is
packed, or one a step of the workflow reads. Read the entries that begin
with `!` as well: each takes away what an earlier one gave, and a file a
build reads may be among them.

**Which workflow is asked what.** One that is only called by others, the
shared steps, has no list and is passed over. One that a change request
starts is to name everything a build reads, so `matches nothing` and
`read by a build` are both asked of it. One that only a push starts, the
pipeline that publishes, names what a package is built from and leaves
out tools, samples and tests on purpose, so only `matches nothing` is
asked of it, and `on dev.yml's list and not on this one` sets its list
beside the change request's.

The entries are matched as git matches a glob, which agrees with the host
for a name, a folder and `**`. The check reads the first list of each file.

### 12. The host's packages are as private as they are meant to be

Skipped where `.agentics.yaml` says `delivery.pipeline: none`, where the
repository has no remote, and where the stored login cannot read
packages; say which. A pipeline's `ci` packages are meant to stay private
([`delivery/delivery.md`](delivery/delivery.md), *Two registries, one kind
each*), and a package that has been made public cannot be made private
again, so this is looked at while there is still nothing to regret.

    repo="$(gh repo view --json nameWithOwner --jq .nameWithOwner)"
    owner="${repo%%/*}"
    case "$(gh api "users/$owner" --jq .type)" in Organization) scope="orgs/$owner" ;; *) scope="users/$owner" ;; esac
    gh api --paginate "$scope/packages?package_type=nuget" \
      --jq ".[] | select(.repository.full_name == \"$repo\") | \"\(.name) \(.visibility)\""

Every line is to end in `private`. A line that ends otherwise is a
finding, and never one to fix: it is the user's to say whether the
package was meant to be seen. A package the list has and no project of
the repository packs any more is a second finding, a name left behind by
a rename, for the user to remove.

The host has no command that says which repositories may read a package,
so that is not checked here. It shows where it matters, as a restore
refused in the pipeline of the repository that was not given leave;
[`delivery/github.md`](delivery/github.md), *When a package is first
published*, is the list that keeps it from being forgotten.
