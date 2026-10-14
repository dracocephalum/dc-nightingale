# stream-metadata

A stream's metadata against a Nightingale server, as a program that narrates
each step and as a test that asserts the same run.

## What it does

Creates a tenant of its own in the run's database, connects to the run's
server, and drives it through the client working in the tenant: set the
metadata of `orders-1` before the stream has an event, a retention of thirty
days and an owner of the application's, and see the stream still read as not
found; append two events to it; have a set that expects no metadata refused
with the metadata stream's own revision, which counts apart from the
stream's; set it again under the right revision and read it back; read
`$$orders-1` as a stream and find the two `$metadata` events that are its
history; have a set carrying `$acl` refused; and count the tenant's events in
`$all`, which holds the metadata events, in `$de`, the data events, which
does not, and in `$mde`, the metadata events.
The scenario's test runs exactly the same code and asserts the report it
returns.

## Run it

From the component's folder:

    dotnet run --project src/Dracocephalum.Nightingale.Examples.Polecat -- stream-metadata

Its test is `StreamMetadataTests` in the component's test project.

## Notes

The server stores the metadata and gives it back; it does not apply
`$maxAge`, `$maxCount` or `$tb` to reads of the stream yet (`VARIANCES.md`).
`$cacheControl` and `$tmp` are kept and returned without effect, as keys
carried over from the reference. A set replaces the metadata as a whole, so
the second set repeats the retention it keeps.
