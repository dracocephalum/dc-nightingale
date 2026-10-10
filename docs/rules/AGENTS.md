# Rules of this repository

The rules this repository adds to the toolkit's. The toolkit's live under
`agentics/rules/` and are replaced whole by every sync; these are never
touched by one. A rule is a file beside this index and a row below, in the
same two columns as the root `AGENTS.md` table, with links relative to the
repository root — the sync copies the rows into the guidelines block of the
root `AGENTS.md`, so a rule added here is reachable without editing that file.
`agentics/rules/layout.md`, *The toolkit's rules and the repository's*, is the
rule, and its *Precedence* is the warning: a rule here that disagrees with a
toolkit rule has no referee. To replace one, exclude it in `.agentics.yaml`
and write the replacement; to amend one, say so in the document's first lines
and give the row a trigger of its own. Nothing guarantees an amendment holds
against a toolkit rule that still exists.

| When you are | Read |
|---|---|
| Making the API behave differently from the reference event store on purpose, or asked why it differs | [`VARIANCES.md`](VARIANCES.md) — record the difference and the reason there |
| Deferring a product feature with its approach already decided | [`PENDING.md`](PENDING.md) — record it there; `TODO.md` is for what is owed soon |
