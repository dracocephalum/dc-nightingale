# Optimize — what the documents cost to read

Applies when asked to optimize, compact, slim, shrink or measure the documents
agents read in this repository, or asked what they cost in context.

**This is about cost, not truth.** Whether the documents still say what is
true is [`scrub.md`](scrub.md), and the two are kept apart on purpose: a
scrub is cheap and safe enough to run without deciding to, and nothing here
is both. Never run any of this as part of a scrub, and never report a scrub
as clean or failed on the strength of it.

## Three operations, in rising risk

| Operation | What it does | When |
|---|---|---|
| **measure** | reports what each document weighs and when it is loaded; changes nothing | the default, and always first |
| **compact** | removes what is verifiably redundant; no rule is lost | when asked, on what the measurement points at |
| **defaults** | removes what a named model does without being told | only when asked for by name, with the guards below |

Asked to "optimize" with nothing more, measure, report, and say which of the
other two the numbers justify. Do not go on to change anything.

## 1. Measure

    find . -name '*.md' -not -path './node_modules/*' -not -path '*/bin/*' \
      -not -path '*/obj/*' -exec wc -c {} + | sort -n

Bytes divided by four is close enough to tokens for prose. Weight alone says
little; what matters is **how often it is paid**, so report each document in
one of three groups:

| Group | What is in it | Paid |
|---|---|---|
| always loaded | `AGENTS.md`; the name and description of every skill shim | every session |
| loaded on a trigger | each document under `agentics/rules/` | when a task touches its subject |
| loaded when asked | `README.md`, `DESIGN.md`, `TODO.md`, `PENDING.md`, `VARIANCES.md`, component READMEs, `docs/` | when a task needs that knowledge |

The report is a table of group, document, bytes and approximate tokens, with
each group's total, and the trend where history shows one:

    git show <an older commit>:<path> | wc -c

A document that has doubled is a finding; a large document that has always
been large is a fact.

**Then the arithmetic, before recommending anything.** A compaction pass
costs reading every document in its scope, writing the ones it changes —
and written tokens cost several times what read ones do — and a scrub
afterwards. It saves the bytes it removes, multiplied by how often that
document is loaded. So:

- An **always-loaded** document repays quickly: every session pays for every
  line of it.
- A document **loaded on a trigger** repays slowly, and one loaded when asked
  barely at all. Trimming a few percent from them is recovered only after
  hundreds of sessions.
- The cheapest saving is never a rewrite. It is deleting what is finished:
  a `TODO.md` or `PENDING.md` entry for something that shipped, which the
  scrub already reports.

Say what a pass would cost and save in those terms, and recommend one only
where it comes out ahead.

## 2. Compact

Removes redundancy that can be **shown**, not judged:

| May go | Because |
|---|---|
| a rule stated in full in two documents | one becomes a pointer to the other; the scrub's *No rule stated twice* finds them, this fixes them |
| prose restating what a linked document says | the link stays correct while the summary goes stale |
| an instruction for something that no longer exists | it reads as current |
| an entry for work that shipped, or an approach that was dropped | once the user confirms it is closed |
| the same rule said twice within one document | the second saying |

Wording may be tightened where the rule survives word for word in meaning.
The account of how a decision was reached may shrink to the decision and its
reason; the reason itself never goes, since it is what stops the decision
being reopened.

**The invariant: no rule is lost.** Before changing a document, list what it
requires — every bold lead, table row, numbered step and command. After,
list again. The two lists match, or the report names each rule that left and
the document that now owns it. A compaction that cannot produce those lists
has not been verified and is not finished.

One document, or one closely related set, per change, so it can be reviewed;
then the scrub, since a pointer is a link and a moved rule can orphan an
index row.

**Know whose document it is.** In a repository the toolkit initialized, the
documents under `agentics/rules/` and `agentics/templates/`, and the skill
shims, are the toolkit's: an upgrade replaces them when they are unmodified
and has to merge them by hand when they are not. Compacting them here turns
every later upgrade into that merge. They are compacted in the toolkit and
arrive by upgrade. What this repository compacts is its own: the rows and
sections it added to `AGENTS.md`, its READMEs, `DESIGN.md`, `TODO.md`,
`PENDING.md`, `VARIANCES.md`, and `docs/`.

## 3. Defaults

Removes an instruction on the grounds that a particular model follows it
without being told. It saves the most and it is a different kind of change,
because it trades something that can be checked for something that cannot:

- **It cannot be verified, only observed.** "The model does this by default"
  is an assertion whose truth moves with the model, its version, the length
  of the context and how much of the document reached the prompt.
- **The failure is invisible.** A dropped rule raises no error. It appears
  months later as a repository that stopped meeting a standard nobody
  noticed had gone.
- **The documents are tool-neutral.** `AGENTS.md` is a convention across
  tools. Trimming against one model's habits couples the repository to that
  model, and a different agent quietly loses rules it does not default to.
- **An instruction also informs people.** It tells a reader what the standard
  is and makes the rule reviewable in a diff, whether or not a model needed
  it.

So it runs only under all of these:

1. **Asked for by name**, with the model line named. "Optimize" or "compact"
   never means this.
2. **Never on what guards something irreversible or private**:
   `security-reminders.md`, the source-control rules about merging, pushing
   and history, the publish-safe rule, and any rule that says *never*.
3. **Never on the toolkit's documents in a target**, for the reason under
   *Compact*, and because they are shared by every agent that reads them.
4. **Every removal is logged** in `agentics/optimize-log.md`, created on the
   first run: the date, the model line, the document, and the removed text
   verbatim. The log is what makes the change reversible; a removal that is
   not in it is a deletion.
5. **Reported as an assertion.** The report says the removals rest on
   observed behaviour of the named model and are not verified.

**When the model line changes, the log is read again**: each entry is either
still believed of the new model, or its text goes back. An agent that is not
the model named in the log treats every entry as a rule still in force.
