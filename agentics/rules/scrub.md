# Scrub — recurring consistency and drift checks

Applies when asked to scrub, sweep, audit, or check this repository for
consistency or drift; and as a matter of routine after a move or rename, before
a release, and whenever the toolchain has moved underneath the repository.

**This finds inconsistency, not bugs and not rule violations.** The bug pass is
the built-in `/code-review`; the rules pass is
[`coding/code-review.md`](coding/code-review.md). Building and testing are in
`AGENTS.md` under *Building and testing*, and are deliberately not repeated
here: a scrub should be cheap enough to run without thinking about it.

## Report, do not fix

Produce a findings table and change nothing. Some findings are genuinely the
user's call — a baseline drift is a decision about whether to re-baseline, not
a file to rewrite — and a check that silently edits is a check nobody trusts.

    | # | Check | Result | Detail |
    |---|---|---|---|
    | 1 | Markdown lint | pass | 0 issues in 0 files |
    | 4 | Line endings | **fail** | AGENTS.md: 12 CRLF lines, 160 LF |

State `pass`, `fail`, or `skipped` with the reason, and for every failure what
would close it. Apply fixes only when asked, and then as a separate, reviewable
change.

## The checks

### 1. Markdown lint

    npx --yes markdownlint-cli2@0.23.2 "**/*.md" "#node_modules"

**Read the `Summary:` line, not the exit code.** The configuration is
`.markdownlint.yaml`; every rule left enabled points at a real defect.

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

Template files are the other exception: `agentics/templates/*` and any
`*.template.md` link into the repository they are copied into, not the place
they sit. Note them and move on.

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

### 7. `AGENTS.md` index is complete in both directions

Every document under `agentics/rules/` is **reachable** from the table, and every
row's link resolves. A document nobody can arrive at will not be read, and a row
pointing at nothing teaches an agent to distrust the table.

    find agentics/rules -name '*.md' | sort
    grep -oE '\(agentics/rules/[^)]*\.md\)' AGENTS.md | tr -d '()' | sort -u

**And no row twice.** A table row that appears more than once is an upgrade
that applied the same template change twice; nothing else flags it:

    grep '^| ' AGENTS.md | sort | uniq -d

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

**This is about duplication, not brevity.** Do not shorten a document because
a model would probably behave correctly without the instruction — see *A
compacting `/scrub`* in the toolkit's `PLAN.md` for why that is a different and
far riskier operation.

### 10. `TODO.md` is still true

Read every entry and ask whether it is still open. An entry that is done, or
that describes a state the repository has left, is worse than no entry: it is
the file agents are told to trust for what is unfinished. Report the stale ones
rather than deleting them — closing an item is the user's call.
