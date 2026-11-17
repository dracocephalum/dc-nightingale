# Where facts live

Applies when writing a comment or a document, when a change to behaviour has
to be said somewhere, and when unsure whether something belongs in the code
or in a document. Language-neutral; a language's coding rules carry how its
doc comments are spelled and link here rather than repeat it.

Every fact about the code has one home. Anywhere else it is a pointer: a
heading named from a comment, a type named from a document. A fact said in
full twice drifts, and the reader cannot tell which copy is right.

## The homes

| The reader asks | Home | Shape |
|---|---|---|
| What does this do, promise, refuse | the declaration's doc comment | one to three sentences: inputs, result, failures, ordering, thread safety. Never the algorithm |
| How does it do it | the body, above the line that raises the question | a sentence on the trick, the lock, the literal, and the reason when it is local to that line |
| Why is it shaped this way | `DESIGN.md`, the seam | the decision, its reason, the alternative rejected; it names the type that holds the how |
| What does an operator set, and to what by default | the component's `README.md` | one table row a setting; the value appears nowhere else |
| What is owed, blocked or undecided | `TODO.md` | as [`../layout.md`](../layout.md), *Root documents*, says |
| How does it differ from the reference product | `VARIANCES.md`, where the repository has one | what the reference does, what this does, why |
| What is deferred, or declined | `PENDING.md`, `UNSUPPORTED.md`, where the repository has them | the approach decided; the reason it was set aside |
| What does this word mean | the glossary, where the repository has one | one line |

The split in the first three rows is the one the language communities agree
on: the declaration's comment is the contract a caller reads without opening
the body, the algorithm is said inside the body where it can be checked
against the lines beside it, and a decision that spans types is a record of
its own, kept small because a large document is never kept up to date.

## Four rules

- **One home.** Before writing a paragraph anywhere, ask where it is already
  said. If somewhere, point there. If nowhere, the table says where it goes.
- **Same change.** A change to behaviour edits the fact's home in the same
  commit; a pointer to it needs no change. A comment or a paragraph about
  something removed is removed with it.
- **Short survives.** A doc comment past about six lines, or a seam past a
  page, is carrying another home's fact. Move the fact and leave the pointer.
  A document holds the detail only when it is the one place that detail can
  be: a setting's meaning, a decision's reason.
- **A pointer names something that exists.** A comment that names a heading,
  and a document that names a type, a member or a setting, are claims the
  scrub checks ([`../scrub.md`](../scrub.md), *No rule stated twice*).

## Smells

| Smell | What it usually means |
|---|---|
| A doc comment that walks through the steps of the method | the how, in the contract's place: move it into the body, beside the steps |
| A doc comment that explains why the system is built this way | a seam's reason: move it to `DESIGN.md` and name the seam |
| A design document that lists a type's members or a method's parameters | reference written by hand: the declarations already say it, and will keep saying it |
| A default value written in a comment, a design document and a README | three copies of an operator's fact: the README's row is the home |
| The same paragraph, reworded, in two documents | one is the home; the other becomes a pointer, or goes |
| A pointer to a heading, a type or a setting that is not there | the home moved or was renamed, and the pointer was not in the same change |

Compacting a body of comments or documents that predates this rule follows
[`../optimize.md`](../optimize.md): list what each one states before and
after, name the new home of every fact that moved, and nothing is lost.
