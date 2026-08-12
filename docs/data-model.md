# Data Model — Storage Concepts

**Status:** FIRST PASS — the storage model is settled in outline; the entity
model below it is not written.
**Required by:** §68
**Decided by owner:** 2026-08-12 — hosted data in the datastore, referenced data
in registered databases.

---

## 1. Three storage concepts

The platform has three, and confusing them causes bad design. They are
deliberately separate concepts even where a deployment puts them in one place.

```text
┌─────────────────────────────────────────────────────────────┐
│ PLATFORM STORE            our own metadata                  │
│ SQLite | PostgreSQL | SQL Server | Oracle                   │
│ services, catalog, roles, jobs, styles, cache index         │
└─────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────┐
│ DATASTORE                 hosted spatial data — we own it   │
│ PostgreSQL/PostGIS                                          │
│ uploaded data, migrated copies, editable layers,            │
│ generalised geometry, tile cache contents                   │
└─────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────┐
│ REGISTERED SOURCES        referenced in place — not ours    │
│ PostGIS | Oracle Spatial | SQL Server Spatial | files       │
│ the organisation's existing data, often read-only, many     │
└─────────────────────────────────────────────────────────────┘
```

| | Who owns it | How many | Our rights | Holds |
|---|---|---|---|---|
| **Platform store** | Us | Exactly one | Full | Metadata only. No spatial data. |
| **Datastore** | Us | Zero or one | Full, including DDL | Hosted spatial data |
| **Registered sources** | The organisation | Many | Whatever we were granted, often read-only | Referenced spatial data |

**Co-location is allowed, conflation is not.** A small deployment may put the
platform store and the datastore in one PostgreSQL instance, and should by
default. They remain distinct concepts with distinct lifecycles: metadata is
small and precious, hosted data is large and reproducible, and their backup and
growth profiles have nothing in common.

## 2. The datastore is a provider, not a parallel universe

**Design rule.** The datastore is not a separate subsystem alongside the
provider abstraction. It is **a provider we happen to have write and DDL rights
on.**

Building it as a parallel concept would give us two of everything: two
publishing paths, two query paths, two authorization models, two sets of bugs.
The only real difference between the datastore and a registered PostGIS database
is **permission**, and permission is already what the capability model exists to
describe.

### The capability model gains a write dimension

Capability negotiation currently answers query questions — can this provider
evaluate a spatial predicate, can it clip, can it simplify
([ADR-008](adr/ADR-008-query-engine.md)). It now also answers:

- Can I write rows here?
- Can I create tables?
- Can I create indexes?
- Can I alter a schema?
- Can I create generalised geometry tables for the tile path?

The datastore answers yes to all of them. A read-only registered Oracle answers
no to all of them. Same mechanism, one extra dimension.

### A useful consequence

The rule stops being *"this feature requires the datastore"* and becomes
**"this feature works wherever we can write."**

If an organisation grants us write access to their own PostGIS, they get hosted
capabilities there without copying data into our datastore. That is more honest,
more flexible, and costs nothing extra because the capability check already
exists.

## 3. Registered, derived, managed — three roles

> **Corrected 2026-08-12.** An earlier version of this section said "hosted data
> gets full capability, registered data gets whatever its provider supports".
> **That was wrong**, and researching the ArcGIS model properly showed it was in
> places backwards: referenced data there is editable and writes through to the
> source, and some advanced capabilities *require* referencing rather than
> copying. See [research/arcgis-datastore-model.md](research/arcgis-datastore-model.md).
>
> The axis is not capability. It is **who manages the storage, and where edits
> land.**

Taking the owner's posture — editing happens in the source database, with a QGIS
extension as the tool — our model can be simpler than ArcGIS's. Three roles, not
two modes:

### 1. Registered source — the system of record

Always the organisation's. Always current. **This is the normal case, and
everything should be designed around it being the default.** No staleness,
because we are not holding a copy.

### 2. Derived store — our materialisations

Generalised geometry per zoom level, tile caches, query response caches, index
helpers. Explicitly derived, explicitly regenerable, **never the system of
record**. If it is lost we rebuild it.

Once editing lives at the source, this is what the "datastore" mostly turns out
to be — a performance store, not a content store.

Note that this splits further by lifecycle, as ArcGIS's does: cache bytes are
disposable and high-churn, generalised geometry is expensive to build and
long-lived. They should not share storage merely because both are derived.

### 3. Managed store — data with nowhere else to live

Uploaded files, migration landing area. **This is the only genuine hosting**, and
it may not be needed in v1 at all. Q-40 decides it.

### Why this beats copying the ArcGIS model

- It removes the staleness problem rather than managing it. ArcGIS's copy mode
  goes stale and the remedy is to overwrite or republish. A derived store is
  stale by definition and has a refresh policy, which is a far smaller promise.
- It matches the owner's posture: hosting is not where content lives, it is
  where speed comes from.
- It dissolves Q-33. Copy-versus-replicate stops being a question when the copy
  is openly a derived artefact.
- The write-capability dimension in §2 still applies, but it now governs **what
  we can materialise**, not what the user can do.

## 4. What the datastore is not

Worth stating, because each of these is a plausible misreading:

- **Not mandatory.** Without it, the platform serves registered sources only.
  Removing mandatory PostgreSQL was a deliberate decision
  ([research/multi-database-consequences.md](research/multi-database-consequences.md));
  a mandatory datastore would reinstate it under a different name.
- **Not the platform store.** Different content, different lifecycle. Co-located
  by default, never merged conceptually.
- **Not a requirement for vector tiles.** We write our own MVT encoder, so tiles
  work from any provider. Hosting makes them faster, not possible
  ([hosted-datastore-and-tiles.md](research/hosted-datastore-and-tiles.md) §4).
- **Not a replication target.** Under §3 the derived store is openly a derived
  artefact with a refresh policy, which dissolves the copy-versus-replicate
  question rather than answering it.
- **Not where the customer's data lives.** The system of record is the
  registered source. We materialise for speed; we do not take custody.

## 5. Open questions this raises

| # | Question |
|---|---|
| Q-40 | **[OWNER]** Do we accept data uploads at all, or does the administrator load data into their own database with QGIS and register it? If the latter, role 3 disappears from v1 and the architecture gets materially smaller. |
| Q-41 | Do we offer an optional companion schema in a registered database where we are granted rights, to hold versioning and editor-tracking bookkeeping? That is how ArcGIS gives referenced data advanced capability — via an enterprise geodatabase schema it controls inside the customer's database. It means being a resident in someone else's database. |
| Q-34 | Are generalised geometry tables derived-store-only, or attempted wherever writable? **§2 suggests the latter.** (already open) |
| Q-31 | **Reopened.** The previous answer rested on a wrong reading of the ArcGIS model. |

**Q-38 answered 2026-08-12: no.** A layer does not migrate between registered
and hosted. If someone wants to edit, they edit the source database directly,
with QGIS. This is a deliberate narrowing and it is what makes §3's simplification
available.

## 6. Not yet written

The entity model itself: service definitions, layer definitions, stable identity
(§37), field and domain model, subtypes, relationships, attachments, editor
tracking, optimistic concurrency (§28). This document currently covers only
where things live, not what they are.
