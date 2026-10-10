# Architecture — boundaries and what crosses them

Applies when shaping a boundary: deciding where a type lives, what an
assembly may reference, how a generated or third-party API is wrapped, or how
a second transport, backend or integration is added. Language-neutral; the C#
coding rules carry the C#-specific half (*Design*, *gRPC contracts*) and link
here rather than repeat it. The shape is ports and adapters: an application
core that knows no transport and no storage, with every outside thing an
adapter that depends inward.

## The dependency points inward

- **The core defines what a call must satisfy and what it may do; an adapter
  bridges one outside thing to that.** A transport parses the wire, calls the
  core, maps the answer. A backend implements a port the core declares. Neither
  holds a rule — who may, what is valid, what happens next — because a rule in
  an adapter has to be copied into the second adapter, and the two copies
  drift.
- **The line is held by the compiler, not by review.** The domain assembly
  references no wire, driver or framework package; the contract and each
  adapter reference the domain and never each other. A `using` that cannot
  compile is a boundary that cannot erode. Judge by the assembly graph, not by
  folder names.
- **Hosts compose, adapters do not.** One registration composes the core with
  a transport (`AddX()` / `MapX()`); the host sees that pair, never the
  adapter's parts, so a second transport is a second registration and the
  host's shape does not change.

## Generated and third-party types stop at the adapter

The anti-corruption layer: the core consumes its own records and throws its
own exceptions, and the generated or vendored type never appears on a core
signature. The adapter owns the translation in both directions.

- **Small and infrequent: copy.** A request with a handful of scalars becomes
  a core record; the adapter maps the wire's optionality encoding (absent, empty,
  sentinel) once. Not an interface implemented by the generated partial, which
  still has to translate the encoding and ties the contract to the core's
  callers.
- **Large and frequent: a view.** A payload crosses as `ReadOnlyMemory<byte>`
  or an equivalent window over the message's buffer, inbound without a copy;
  outbound one copy into the wire type, and an unsafe zero-copy wrap only after
  measuring that the copy is the cost. Scalars beside the payload are copied.
- **Collections stream.** A sequence crosses as a stream of core records, never
  as a materialized list of wire types; the adapter maps each element as it
  passes.
- **Refusals are the core's, translated once per adapter.** The core throws
  its shared exceptions or returns its result records; the adapter has one
  translation from those to the wire's statuses, and a call catches only
  what the translation cannot know (a name the request carried). Not a
  catch per exception per call.

## Three tiers of names

| Tier | Named by | Example |
|---|---|---|
| generated | the generator, as generated, never wrapped or renamed for style | `Tenants.TenantsBase`, `CreateTenantRequest` |
| bridge | the generated contract plus its role, so the chain reads from either end | `TenantsService` |
| ours | the domain, singular unit nouns, no echo of any transport | `TenantManager`, `Tenant`, `NewCredential` |

A domain name wins inside the product namespace; a wire type that mirrors a
domain type gets a wire-specific name. The C#-specific spellings are in
[`csharp/csharp-coding-rules.md`](csharp/csharp-coding-rules.md), *Naming* and
*gRPC contracts*.

## Namespace first, assembly second

- **A namespace separates; an assembly forbids.** Put the adapter in its own
  namespace and folder from the first day. Split it into an assembly when the
  second adapter of that kind exists, or when a consumer must be unable to see
  it (a client whose public surface carries no transport type). An assembly
  split with one adapter buys nothing the namespace does not, and costs a
  project to version.
- **The contract is always its own assembly.** Generated code is the one
  thing a consumer may need without the adapter — a client and a server share
  it — so it is built once, referencing the domain and nothing else.

## Record the anticipated shape; do not build it

A second stack, a fallback, discovery: when a design round settles how it
would be done, write the shape down — `PENDING.md` in a repository that keeps
one, otherwise `DESIGN.md` — with the decisions and the reasons, so it is not
re-derived, and leave room for it where leaving room is free (the sibling
assembly name, the seam a second adapter would implement). Build it when it is
asked for. The C# *Design* rule says the same of shared behaviour: extract on
the third concrete use, not in advance.

## Review

| Smell in the diff | Section | Label |
|---|---|---|
| A generated, driver or framework type on a core type's public surface, or a wire package referenced by the domain assembly | Generated and third-party types stop at the adapter | `issue` |
| A rule about who may, what is valid or what happens next inside a transport handler or a storage implementation | The dependency points inward | `issue` |
| A collection of wire types materialized to cross the seam; a payload copied where a view would do, or wrapped unsafely with no measurement | Generated and third-party types stop at the adapter | `suggestion` |
| The same exception-to-status mapping in more than one call | Generated and third-party types stop at the adapter | `suggestion` |
| An adapter split into its own assembly with no second adapter and no consumer that needs it hidden | Namespace first, assembly second | `question` |
| A second transport, backend or stack started without the shape recorded first | Record the anticipated shape | `suggestion` |

The general standard and the comment format are in
[`code-review.md`](code-review.md).
