# Command lines — commands, options and a tool's files

Applies when naming or changing what a tool shows outside its code: a
command, an option and its values, an alias, a key in its settings file, a
file or folder it reads or writes. Language-neutral; the names inside the
code are the language's, and the tests for choosing one are the same: for
C#, [`csharp/csharp-coding-rules.md`](csharp/csharp-coding-rules.md),
*Choosing a name*.

## Commands

- **The word that runs is a verb**: `git commit`, `tool translate csharp`.
- **A noun may come first, for the verbs after it to act on**:
  `dotnet package add`, `gh pr create`, `git remote add`. It groups a
  tool's commands by what they act on, and a tool keeps one order.
- **Never a noun alone, never a value.** `tool translation` names no act;
  `tool csharp` makes the argument the command.

## Options and settings

- **An option names what it is, and is not a word that is both a verb and
  a noun.** `--inventory <file>` for the list of what a run read and wrote,
  not `--record <file>`, which reads as an instruction to record.
- **A choice of several answers is one option with values, named for the
  question**: `--fetch never|once|always`, not `--offline` beside a
  `restore` command. Left out, it takes the answer a caller who chose
  nothing wants, `once`.
- **A key in a settings file names the act it controls**:
  `"translate": false`, not `"enabled": false`, which reads as whether the
  file is in the build at all.
- **An alias is for a person at a prompt**: `cs` for `csharp`, `--fe` for
  `--fetch`. Scripts, build files, pipelines and documents use the name it
  stands for, the one a search finds.
- **A renamed command or option keeps no alias before a first release**, and
  every script and document that uses it changes with it.

## A tool's files

- **The plain name belongs to the file people write; a derived one adds one
  affix per role**: `orders.tool.json` for the settings,
  `orders.tool-summary.json` for what a run made of them,
  `.tool-manifest.json` for the tool's own bookkeeping.
- **A folder is named for what it holds**: `cache/` for downloads and what
  was made from them, not the name of the vendor its first content came
  from.

## Not settled

How deep commands nest, how short forms are made, when a flag is enough and what its absence means,
exit codes and which stream a message goes to, and help text. Until a rule
says, a repository's existing tools are the convention.

## Review

| Smell in the diff | Section | Label |
|---|---|---|
| A command that is a noun alone or a value (`tool translation`, `tool csharp`), or two orders in one tool | Commands | `suggestion` |
| An option or settings key that is a verb-or-noun word, names the wrong question (`"enabled"` for "translate"), or a second flag for a third answer | Options and settings | `suggestion` |
| An alias in a script, build file, pipeline or document; a renamed option that leaves a use or, before a first release, an alias behind | Options and settings | `suggestion` |
| A tool's derived file under the plain name, or a folder named for where its content came from | A tool's files | `suggestion` |

The general standard and the comment format are in
[`code-review.md`](code-review.md).
