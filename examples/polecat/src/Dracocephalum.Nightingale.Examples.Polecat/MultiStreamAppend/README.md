# multi-stream-append

An append to several streams in one commit, as a program that narrates each
step and as a test that asserts the same run.

## What it does

Creates a tenant of its own in the run's database and drives the run's server
through the client working in the tenant: open two accounts in one commit;
move money from one to the other in one commit, debit and credit together,
on the condition that a stream the transfer does not write, the sender's
freeze, does not exist; send a second transfer decided on revisions that
have moved on and see it refused with both accounts named and nothing
written;
freeze the sender and see the next transfer refused by its check, numbered
after the two appends; open a third account and set its metadata in the same
commit; and see a reset through metadata refused, a reset being its own call.
The scenario's test runs exactly the same code and asserts the report it
returns.

## Run it

From the component's folder:

    dotnet run --project src/Dracocephalum.Nightingale.Examples.Polecat -- multi-stream-append

Its test is `MultiStreamAppendTests` in the component's test project.

## Notes

The whole append is one transaction of the store: every stream under its own
expected state, every event or none. The events are positioned stream by
stream, in the order the appends are given, so a reader of `$all` meets the
debit before the credit. A refusal names every condition that failed, not
only the first, in the order the conditions were given: the appends first,
then the checks. A check holds at the commit, not only when it was read: the
checked stream's row is locked until the append commits, so the freeze cannot
land between the check and the transfer. Each transfer is new events; sending
the same events again under the same expectations is a retry, and is
answered with what the first wrote rather than refused.
