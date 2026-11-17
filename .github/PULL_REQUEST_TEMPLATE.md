<!-- Title: `#<ticket> <type>(<scope>): <summary>`, e.g. "#123 feat(billing): retry failed charges once".
     It becomes the squash commit, and this body becomes its message: go to the point.

     Write prose UNWRAPPED, one paragraph per line: GitHub renders every newline here as a line
     break, so text wrapped at 80 columns stays wrapped for every reader. Lists and tables are fine.

     Nothing below is a form. Shape the body to the change and delete what has nothing to say;
     agentics/rules/source-control/source-control.md, "Opening a change request", has the rule. -->

Closes #[ticket]

<!-- One sentence of why. The ticket holds the problem and the discussion; do not restate them. -->

<!-- What changed: bullets, one per change in behaviour or structure, concise and precise.
     A table or a diagram where it says it better - what moved where, versions before and after, a flow.
     A section of its own only for the part a reviewer could not reconstruct from the diff. -->

-

## Verified

<!-- Plain bullets: what was run and what it showed, numbers included. Say what was not verified. -->

-

<!-- Add only when there is something to say:
     "## Actions" - a checklist of what a person must do at merge or deploy that the change does not do itself.
     "## Risks" - what this could break, who would notice, how it is reverted.
     "## For the reviewer" - where to start reading, what you want challenged, what was left out and why. -->
