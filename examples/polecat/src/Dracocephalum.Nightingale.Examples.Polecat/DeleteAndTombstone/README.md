# delete-and-tombstone

Deletion against a Nightingale server, as a program that narrates each step
and as a test that asserts the same run.

## What it does

Reserves a random database name on the local SQL Server, hosts the
Polecat-backed server in its own process on a loopback port with delete and
tombstone allowed, which the default host does not, and drives it through the
client: append to two streams, have a stale delete refused with the actual
revision, delete one stream and see its read and its append fail and the
category stream shrink, tombstone it and see the name read as never used,
append to the freed name as a new stream, then start a second server with the
default settings and see it refuse to delete at all. Then it drops the
database.
The scenario's test runs exactly the same code and asserts the report it
returns.

## Run it

From the component's folder:

    dotnet run --project src/Dracocephalum.Nightingale.Examples.Polecat -- delete-and-tombstone

Its test is `DeleteAndTombstoneTests` in the component's test project.

## Notes

Deletion is a server-side decision: the host enables delete and tombstone
separately under `Nightingale:Deletion`, and both are off unless it does. The
sample's own server turns them on to show what they do; its second server
shows the default.
