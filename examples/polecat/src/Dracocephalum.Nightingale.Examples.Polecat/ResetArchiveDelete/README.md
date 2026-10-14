# reset-archive-delete

The three ways a stream is taken away from a Nightingale server, as a program
that narrates each step and as a test that asserts the same run.

## What it does

Creates a tenant of its own in the run's database, starts a server over that
database with the deletion level at `Delete`, which the default host does not
have, and drives it through the client working in the tenant: append to two
streams; reset one and see it read as not found while `$ce-orders` still holds
its events, and its metadata say it is reset; append to it expecting no
stream and see the revisions continue, the older events hidden; have a stale
archive refused with the actual revision; archive the stream and see its read
and its append fail and the category stream shrink; delete it and see the
name read as never used; append to the freed name as a new stream; then ask
the run's server, which has the default settings, and see it refuse to
archive and still reset.
The scenario's test runs exactly the same code and asserts the report it
returns.

## Run it

From the component's folder:

    dotnet run --project src/Dracocephalum.Nightingale.Examples.Polecat -- reset-archive-delete

Its test is `ResetArchiveDeleteTests` in the component's test project.

## Notes

A reset is the reference client's delete: one metadata event, nothing
removed, undone by setting the metadata, so every server allows it. Archive
and delete are the server's decision: `Nightingale:Deletion:Allowed` is
`None` unless the host raises it to `Archive`, or to `Delete`, which allows
both. The sample's own server raises it to show what they do; the run's
server shows the default.
