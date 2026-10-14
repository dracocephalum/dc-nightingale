# binary-data

An event's second body, as a program that narrates each step and as a test
that asserts the same run.

## What it does

Creates a tenant of its own in the run's database and drives the run's server
through the client working in the tenant: append a registration whose
personal data is encrypted under a key of that person's own and carried as
binary data beside JSON that stays readable, and a thumbnail as binary data
alone; read both back, see the content type among the metadata, decrypt the
personal data with the key, and see the thumbnail's JSON read as `{}`;
destroy the key and read again, the JSON still saying what happened while
the personal data can no longer be decrypted; read `$ce-people` and see the
same bytes there; then have an event that names a content type for binary
data it does not carry refused.
The scenario's test runs exactly the same code and asserts the report it
returns.

## Run it

From the component's folder:

    dotnet run --project src/Dracocephalum.Nightingale.Examples.Polecat -- binary-data

Its test is `BinaryDataTests` in the component's test project.

## Notes

The keys live outside the store, one per subject, which is the point:
destroying a key forgets the person in a store that never deletes, and the
event keeps its readable part. The server never looks inside binary data; it
stores the bytes as they are sent and returns them as they were stored.
`$binaryContentType` among an event's metadata says what they are, and
without it they are `application/octet-stream`. The name and address the
sample encrypts are placeholders.
