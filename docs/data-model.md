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

## 3. Hosted and registered

The distinction an administrator has to hold in their head, and the answer to
Q-31.

| | Hosted | Registered |
|---|---|---|
| Where the data lives | Datastore (or any writable source) | The organisation's own database or files |
| How it got there | Uploaded, or copied during migration | Already existed; we point at it |
| Editing | Yes, with our concurrency model | Only if the provider allows it and we were granted rights |
| Runtime schema change | Yes ([runtime-schema-evolution.md](research/runtime-schema-evolution.md)) | No |
| Generalised geometry for tiles | Yes | Only where writable |
| Tile performance | Best | Depends on the provider; the cache absorbs the difference |
| Who is responsible for the data | Us | The organisation |

**The promise to state in documentation:** hosted data gets full capability;
registered data gets whatever its provider supports and whatever rights we were
granted. Not a capability matrix per provider — a single distinction, with a
capability report available per layer for anyone who wants the detail.

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
- **Not a replication target.** Whether hosting copies once or tracks the source
  continuously is Q-33, and continuous replication is a synchronisation product
  nobody asked for.

## 5. Open questions this raises

| # | Question |
|---|---|
| Q-38 | Can a layer move between registered and hosted while keeping its service identity and URL? An administrator who registers a layer and later wants editing will ask for exactly this. Stable IDs (§37) should make it possible; it needs to be designed rather than discovered. |
| Q-39 | If a registered source is writable, do we offer hosted-grade capabilities there automatically, or only on explicit opt-in? Automatic is friendlier and surprises DBAs. Opt-in is safer and gets forgotten. |
| Q-33 | Does hosting copy once or track the source? (already open) |
| Q-34 | Are generalised geometry tables datastore-only, or attempted wherever writable? **§2 suggests the latter.** (already open) |

## 6. Not yet written

The entity model itself: service definitions, layer definitions, stable identity
(§37), field and domain model, subtypes, relationships, attachments, editor
tracking, optimistic concurrency (§28). This document currently covers only
where things live, not what they are.
