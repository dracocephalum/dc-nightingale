<!--
  TEMPLATE - ships with the repository under agentics/templates/. Copied and filled
  when a component is created; the placeholders here are intentional.
  this is the page someone reads before touching the component.
-->

# {{COMPONENT_NAME}}

{{ONE_LINE_PURPOSE}}

| | |
|---|---|
| Category | `{{CATEGORY}}/` |
| Kind | {{KIND}} <!-- service / library / job / tool --> |
| Solution | `{{PREFIX}}.{{PROJECT_NAME}}.slnx` |
| Projects | `src/{{PREFIX}}.{{PROJECT_NAME}}`, `test/{{PREFIX}}.{{PROJECT_NAME}}.Tests` |

## What it does

<!-- Two or three sentences. What it is for, what it consumes, what it produces. -->

## Run it

    dotnet build {{PREFIX}}.{{PROJECT_NAME}}.slnx --nologo -v:q
    dotnet test  {{PREFIX}}.{{PROJECT_NAME}}.slnx

<!-- Beyond build and test: required configuration, local dependencies, how to start it, where it listens. -->

## Configuration

<!-- Settings it reads, where they come from, which are required. "None" is a valid answer. -->

## Notes

<!-- Decisions worth knowing, known limitations, links to design docs or issues. Delete if empty. -->
