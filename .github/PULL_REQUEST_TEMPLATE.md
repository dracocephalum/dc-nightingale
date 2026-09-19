<!-- Title: `#<ticket> <type>(<scope>): <summary>`, e.g. "#123 feat(billing): retry failed charges once".
     It becomes the squash commit. The first line below links the ticket; GitHub closes it on merge.

     Write prose UNWRAPPED, one paragraph per line: GitHub renders every newline here as a line
     break, so text wrapped at 80 columns stays wrapped for every reader. Lists and tables are fine. -->

Closes #[ticket]

## What

<!-- Bullet points, one per change in behaviour or structure - not a paragraph, not a file list.
     Scale it to the diff: a small PR is two or three bullets; a large PR groups them under
     "### Area" headings so a reviewer can navigate the diff from here. -->

-

## Why

<!-- The problem, the constraint, or the request - beyond what the ticket already says. -->

## How

<!-- Optional - for a change that is not trivial. The approach and the rationale behind it, in whatever
     form makes them easiest to grasp later, by a person or an agent: a list of steps, a table of
     options considered, a mermaid diagram of the flow. Delete if the diff speaks for itself. -->

## How it was verified

<!-- Build and tests green is assumed. A checklist of what was actually run or checked beyond that:
     new tests, manual steps, environments. An unchecked box means not done - say why. -->

- [ ]

## Risks and rollback

<!-- What could this break, who would notice, and how is it reverted - a plain
     revert, or does data/config need care? "None" is a valid answer if true. -->

## Actions

<!-- A checklist of anything a person must do at merge or deploy time that the change does not do
     itself: a SQL script to run by hand, a setting to flip, a follow-up ticket to open. Delete if empty. -->

- [ ]

## Notes for the reviewer

<!-- Where to start reading, decisions you want challenged, anything deliberately left out and why. Delete if empty. -->
