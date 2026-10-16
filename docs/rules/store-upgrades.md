# Upgrading the event store's packages

Applies to a new version of Polecat, of Marten when it comes, and of the
JasperFx and Weasel packages they bring. The test suites are the checklist:
each assumption the gateway makes of the store is a test that fails when the
store changes it. This is the order to run them in, and what no test can do.

1. **Read what changed** between the two versions: the release notes of the
   store, JasperFx and Weasel, for schema, append, high-water and exception
   changes.
2. **Check every workaround against the new source**, not against the notes:
   fetch the store's reference checkout, check out the new version's tag,
   and see whether each issue a workaround stands on is still there. That is
   every row of `UPSTREAM.md`, raised by us or not, and every place the code
   names the store's behaviour or reaches past its public surface. A fix can
   come from anyone; when one has, remove the workaround and its row.
3. **Bump the version** in `Directory.Packages.props`. `dotnet nuget why`
   shows the JasperFx and Weasel versions it brings, and whether a version
   the repository overrides, each with its reason beside it there, is still
   asked for below its fix; drop the override when none is. Run the licence check of
   [`agentics/rules/dependencies.md`](../../agentics/rules/dependencies.md).
4. **Build, then the unit tests.** A changed approved schema
   (`Approved/store-schema.sql`) is a review, not a re-approval: read the
   diff, see the gateway's additions survive, then approve again.
5. **The integration suite, then the examples**, one after the other, never
   together.
6. **Record what moved** in `VARIANCES.md` and `PENDING.md`.

After the first release, before a release that carries the upgrade: serve a
database the old version created with the new one, and run the integration
suite over it. Until then a store is initialized again rather than migrated.
