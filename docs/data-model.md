# Data Model — Storage and Layer Modes

**Status:** SECOND PASS — storage model settled in outline. Entity model not
written.
**Required by:** §68
**Revision history:** see §8. This document has been rewritten twice; the record
of what changed and why is kept rather than the corrections being layered inline.

---

## 1. Three stores

Distinct concepts with distinct lifecycles, even where a deployment puts them in
one place.

```text
┌──────────────────────────────────────────────────────────────┐
│ PLATFORM STORE          our metadata, no spatial data        │
│ SQLite | PostgreSQL | SQL Server | Oracle                    │
│ services, catalog, roles, jobs, styles, cache index          │
│ small, precious, backed up carefully                         │
└──────────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────────┐
│ DATASTORE               hosted data — we own the schema      │
│ PostGIS | SQL Server Spatial | Oracle Spatial                │
│ the system of record for hosted layers                       │
│ large, authoritative, must be backed up                      │
└──────────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────────┐
│ REGISTERED SOURCES      referenced in place — not ours       │
│ PostGIS | SQL Server | Oracle | files                        │
│ the organisation's own data, many, often read-only           │
└──────────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────────┐
│ DERIVED ARTEFACTS       regenerable, never authoritative     │
│ filesystem | object store | datastore tables                 │
│ tile caches, query caches, generalised geometry              │
│ disposable — losing them costs time, not data                │
└──────────────────────────────────────────────────────────────┘
```

Derived artefacts are listed separately on purpose. They are not a store so much
as a *lifecycle*: whatever holds them, they can always be rebuilt, and they must
never be backed up as though they were data. ArcGIS separates its tile cache
store from its relational store for the same reason
([research/arcgis-datastore-model.md](research/arcgis-datastore-model.md) §3).

**Co-location is allowed, conflation is not.** A small deployment may put the
platform store and the datastore in one instance, and should by default.

## 2. The datastore is a provider we own

**Design rule.** The datastore is not a subsystem parallel to the provider
abstraction. It is **a provider we have write and DDL rights on.**

Building it as a separate concept would give us two publishing paths, two query
paths and two authorization models. The only real difference between our
datastore and a registered database is permission, and permission is what the
capability model already describes.

### The capability model has a write dimension

Alongside the query questions in [ADR-008](adr/ADR-008-query-engine.md) — can
this provider evaluate a predicate, clip, simplify — it answers:

- Can I write rows?
- Can I create tables and indexes?
- Can I alter a schema?
- Can I materialise generalised geometry?

A feature's precondition is therefore **"we can write here"**, not "the data is
hosted". An organisation that grants write access to its own database gets
hosted-grade behaviour there without copying anything.

### The datastore need not be PostgreSQL

**Decision, 2026-08-12.** The datastore may be any writable spatial provider:
PostGIS, SQL Server Spatial or Oracle Spatial.

This follows from an existing decision rather than being a new one. PostgreSQL
was made non-mandatory because an Oracle shop should not have to run it for our
benefit ([research/multi-database-consequences.md](research/multi-database-consequences.md)).
If the datastore were PostGIS-only, any customer wanting hosted content would be
forced back to PostgreSQL, which would undo that decision by the side door.

An Oracle organisation therefore hands us an empty Oracle schema and says *this
is your datastore.*

**The cost is real.** Our datastore schema must be creatable on three spatial
engines: geometry column definition, spatial index creation, and Oracle's
`SDO_GEOMETRY` metadata registration all differ. Implementation order should be
PostGIS first, the other two after. Recorded as A-022.

## 3. Hosted and registered — the operational difference

This is the distinction that matters, and it is not about capability. It is
about **who controls the schema, and what happens when it changes.**

### Registered: the schema changes under us

When a DBA alters a registered table:

- **The running service is stale.** Its field list, types and metadata no longer
  match. ArcGIS requires a manual service restart.
- **We can block the DBA.** A running service holds open connections, and DDL
  needs an exclusive lock. This fails differently on each engine and none of
  them is pleasant:

| Engine | What happens during DDL while we hold connections |
|---|---|
| PostgreSQL | `ALTER TABLE` needs `ACCESS EXCLUSIVE`. It waits behind our open query — and every subsequent query queues behind the waiting DDL. One connection of ours can stall the whole table. |
| SQL Server | The schema-modification lock conflicts with the schema-stability locks held by running queries. Blocks. |
| Oracle | Fails fast with a resource-busy error, or waits for `DDL_LOCK_TIMEOUT`. |

Concretely on PostgreSQL: the DBA runs `ALTER TABLE`, it does not return, and
every subsequent query against that table queues behind the waiting DDL. From
the DBA's side the table has stopped responding, and the cause is a connection
of ours. This is a requirement on the runtime, not a preference.

### Hosted: we coordinate the change

When we own the schema we can sequence it: validate, quiesce, alter, update the
definition, invalidate caches, refresh workers, resume. No surprise, no restart,
no lock fight. This is what
[research/runtime-schema-evolution.md](research/runtime-schema-evolution.md)
describes, and it is the strongest argument for hosting.

### What we must build because of this

Two obligations, and the second is an improvement on the incumbent.

**1. Never stand in the DBA's way.**
Short-lived connections. Never idle in transaction. Aggressive idle timeouts. An
admin operation to quiesce a data source — drain its connections and hold
requests — so a DBA can run DDL cleanly. Belongs to
[ADR-007](adr/ADR-007-service-runtime.md) and the admin API (§39).

**2. Detect schema drift and refresh ourselves.**
Keep a fingerprint of each layer's schema. Poll `information_schema` cheaply, or
check on error, and compare. If it changed, refresh the service definition,
invalidate caches and refresh workers automatically.

ArcGIS requires a manual restart here. Detecting and refreshing is a concrete
improvement over the incumbent, which is what §86 asks for. Polling was already
the assumed mechanism because `LISTEN`/`NOTIFY` is not portable
([ADR-002](adr/ADR-002-primary-data-architecture.md) §4a.4), so this reuses
machinery we need anyway.

Recorded as A-023 — that a cheap schema fingerprint can be polled often enough
to be useful without loading the source database.

## 4. Layer modes

| | Hosted | Registered |
|---|---|---|
| System of record | Our datastore | The organisation's database |
| Schema control | Ours | Theirs |
| Schema change | Coordinated by us; no restart | Happens under us; drift detection and refresh |
| Lock risk to the DBA | None | Real; mitigated by quiescing |
| Data currency | Authoritative | Authoritative |
| Editing | In the datastore, with QGIS (§5) | In the source, with QGIS |
| Who backs it up | Us | Them |

Both modes are first-class. An organisation may run entirely hosted, entirely
registered, or a mixture. **An organisation need not have a GIS database at
all** — datastore-only is a supported and probably common deployment.

## 5. Editing is done at the source, with QGIS

**Owner decision.** Editing is not performed through our API. It is done against
the database, using QGIS, for which we will provide an extension.

This resolves cleanly because **the datastore is an ordinary spatial database**.
QGIS connects to it directly, exactly as it connects to a registered source. One
editing story, not two.

The consequence is that we do not see edits — in either mode, including our own
datastore. So the QGIS extension has a genuine architectural role rather than
being a convenience:

| Extension role | Why it matters |
|---|---|
| **Cache invalidation notification** | Closes the loop we would otherwise have no way to close |
| Publishing | Create a service from a QGIS layer |
| Style transfer | QGIS symbology to MapLibre style — lossy, needs design |
| Service management | Layer list, status, trigger cache seeding |

Where the extension is not used, we fall back to schema-drift polling and
TTL-based expiry. That must work, because we cannot require a specific desktop
client.

**Q-42 is recorded as resolved on this reading**: no feature write endpoints in
our API. If that reading is wrong, §28's CRUD, batch editing, editor tracking
and optimistic concurrency all return, and this section is rewritten.

## 6. What the datastore is not

- **Not mandatory.** Registered-only deployments are supported.
- **Not the platform store.** Different content, different lifecycle. Co-located
  by default, never merged conceptually.
- **Not required for vector tiles.** We write our own MVT encoder, so tiles work
  from any provider. Hosting makes them faster, not possible.
- **Not the home for derived artefacts.** Caches may physically live there, but
  they are a different lifecycle and must never be treated as data.

## 7. Open questions

| # | Question |
|---|---|
| Q-41 | Do we offer an optional companion schema in a registered database where granted rights, for bookkeeping? This is how ArcGIS gives referenced data advanced capability. It means being a resident in someone else's database. Lower priority now that editing is out. |
| Q-39 | If a registered source is writable, is hosted-grade capability automatic or opt-in? |
| Q-34 | Are generalised geometry tables datastore-only, or attempted wherever writable? §2 suggests the latter. |
| Q-43 | What is the schema-drift polling interval, and what does it cost against a large registered database with many layers? |

## 8. Revision history

| Date | Change |
|---|---|
| 2026-08-12 | First pass. Three stores; datastore modelled as a provider with write rights. |
| 2026-08-12 | Corrected. Had claimed "hosted gets full capability, registered gets provider capability" — wrong, and backwards in places. In ArcGIS, referenced data is editable and some advanced capabilities require referencing. |
| 2026-08-12 | Over-corrected, then corrected back. Had reframed the datastore as a derived performance store rather than a content store. The owner restored it as real hosting and supplied the argument that makes hosting worth having: schema changes on registered data require a restart, and our own open connections can block the DBA's DDL. |

## 9. Not yet written

The entity model: service and layer definitions, stable identity (§37), fields,
domains, subtypes, relationships, attachments. This document covers where things
live and how they change, not what they are.
