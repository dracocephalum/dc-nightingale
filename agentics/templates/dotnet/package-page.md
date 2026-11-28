<!--
  A package's own page: copy to PACKAGE.md beside the project file, fill every
  double-brace placeholder and delete this comment and every other one.
  Packaging.props packs it as the package's README.md.
  agentics/rules/delivery/dotnet.md, "What a package holds beside its assemblies".

  It is read by someone who has only the package, on a registry's page: every
  link is a whole address, and nothing points into the repository by a path.
-->
# {{PACKAGE_ID}}

{{ONE_LINE_PURPOSE}}

<!--
  Only for a package of derived work, agentics/rules/derived-work.md, step 6;
  delete the paragraph otherwise.
-->
**It is unofficial: not a product of {{OTHER_PARTY}}, who neither made nor
supports it.** Their own work is at <{{OTHER_PARTY_ADDRESS}}>.

## What it is

{{WHAT_IT_IS}}

    {{THE_FEW_LINES_THAT_USE_IT}}

## What it does not do yet

{{WHAT_IT_DOES_NOT_DO_YET}}

<!--
  Only for a tool that writes files from what its user gives it; delete the
  section otherwise.
-->
## What it writes

What this tool writes is made from what it was given, and carries the terms
of that and not this package's.

## Terms

<!-- One of the two, and the other deleted. The second is derived work's. -->
{{LICENCE}}, the text of which is at <{{LICENCE_ADDRESS}}>.

Two sets apply, and `LICENSE.md` in this package says which covers what:

- **{{WHAT_IS_DERIVED}}** is derived from {{THEIR_WORK}} and is under the
  rules {{OTHER_PARTY}} publishes for it, <{{THEIR_TERMS_ADDRESS}}>.
- **Everything else** is under {{LICENCE}}.

## Where to ask

Questions, faults and requests go to <{{ISSUES_ADDRESS}}>.
