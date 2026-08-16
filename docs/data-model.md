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
│ PostgreSQL — only (Q-70)                                     │
│ services, catalog, roles, jobs, styles, cache index          │
│ small, precious, backed up carefully                         │
└──────────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────────┐
│ DATASTORE               hosted data — we own the schema      │
│ PostGIS — only (Q-32), and MANDATORY (Q-69)                  │
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
- **Can I assume a principal's identity for the duration of a statement?**
  Added 2026-08-12 ([security.md](security.md) §2.2) — this is what makes
  delegating row filtering to the provider's own row-level security possible.
  Where it is absent the layer does not offer delegation, and our own
  authorization still applies.

A feature's precondition is therefore **"we can write here"**, not "the data is
hosted". An organisation that grants write access to its own database gets
hosted-grade behaviour there without copying anything.

### The datastore is PostGIS only, and it is a managed appliance

**Decided 2026-08-12 (Q-32). This reverses an earlier decision, and the reason
is worth recording.**

An earlier version of this section said the datastore could be any writable
spatial provider - PostGIS, SQL Server or Oracle - reasoning that a PostGIS-only
datastore would force an Oracle shop back to PostgreSQL and undo the
no-mandatory-PostgreSQL decision by the side door.

**That was an over-application, and the owner caught it.** The distinction it
missed:

> **Running PostgreSQL and having a managed PostgreSQL appliance are different
> asks.**

ArcGIS Data Store *is* PostgreSQL, but one that ArcGIS installs, configures,
backs up and upgrades. The customer never operates it, needs no PostgreSQL
expertise, no DBA time and no backup tooling of their own. The original
objection was about **operational burden**, and a managed appliance removes most
of it.

So: **the datastore is PostGIS, shipped as a managed component**, and it is
**optional**. An organisation that still refuses any additional database process
runs registered-only and loads data into their own database with QGIS - the same
workflow already endorsed for editing. The capability is not lost; it moves to a
tool they already have.

**A-022 is retired.** Three spatial DDL implementations - geometry columns,
spatial indexes, Oracle `SDO_GEOMETRY` metadata registration - are removed for a
capability nobody had asked for. This is the same reasoning that cut the
platform stores from four engines to two (Q-51), applied a second time to our
own work.

### One schema in the datastore, and it is not a knob

**Owner statement, 2026-08-16.** Hosted data lives in `hosted`, and *"in the
datastore there will be no other schema used by the application"*. `hosted` is
not one schema among several that the server might choose between; it is the only
one it ever writes to. No schema per organisation, per service, per tenant or per
import.

This was already how the code behaved and it was written down nowhere, which is
why it is recorded here rather than left as an implementation detail:
`PostGisImporter.HostedSchema` is a `const`, not configuration, and hosted
attachment tables land in the same schema as the layer they belong to.

**What the rule buys is the safety of one comparison.** `PostGisImporter.DropAsync`
refuses to drop any table outside `hosted`:

> *"only tables in the `hosted` schema were created by this server, and a table we
> did not create is somebody else's data."*

That refusal is a complete guarantee only because the set of schemas the
application owns has exactly one member. With two or more, *is this table ours?*
stops being a string comparison and becomes a lookup against a list — and a
lookup can be stale, incomplete, or race an unpublish. An unpublish that deletes
a customer's table is the worst failure in the whole data model, and this is the
cheapest possible defence against it.

**Consequences to hold to.** A future request for schema-per-tenant isolation is a
change to this rule and needs an ADR, not a configuration setting. Anything that
looks like a second application schema — a staging area for imports, a quarantine
for failed loads, a versioning shadow — must live as tables inside `hosted` or
outside the datastore entirely. And note what the rule does *not* cover:
attachments for a **registered** layer are created in the customer's own schema,
because they belong beside their layer. That is a different decision with a
different justification (§4c), and it is not an exception to this one.

## 3. Hosted and registered — the operational difference

This is the distinction that matters, and it is not about capability. It is
about **who controls the schema, and what happens when it changes.**

### Choosing between them — the lifecycle test

**Decided 2026-08-12 (Q-08). Neither mode is the default.** The choice is made
per layer, using a test taken from Esri's published guidance, which framed this
better than our original question did:

> **Does this layer's data have a life outside the service?**

| Answer | Mode | Why |
|---|---|---|
| Yes — other applications use it, ETL updates it, it existed before the service and will outlive it | **Registered** | `VERIFY` "If the data in the registered data source changes, you will see those changes in the web layer." |
| No — the service essentially *is* the data | **Hosted** | `VERIFY` Esri: hosted data "is automatically deleted by the system when the service or item associated with the data is deleted." Hosting is not merely a location; **it is taking ownership of a lifecycle, including deletion.** |

**A caveat stated rather than hidden: the two modes are not symmetric in
difficulty.** Everything hard sits on the registered side — schema drift
detection, the DDL-lock discipline, capability negotiation across three
dialects, `objectId` synthesis (Q-57), and transaction semantics we do not
control. Hosted is comparatively easy because we own the schema.

So engineering effort concentrates on registered whichever mode proves more
common, and documentation should not imply a parity of difficulty that does not
exist.

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
| Data editing | Through our API | Through our API where we have write rights |
| Schema editing | Through our admin API; we sequence it | In the source database; we detect drift and refresh |
| Who backs it up | Us | Them |
| **Vector tiles** | **Yes** | **No — never** (Q-67) |

**The one asymmetry that is a hard rule (Q-67, 2026-08-12):** tiles come only
from hosted data. This is not a capability gap to be closed later — it is the
decision that removed the three-dialect tile path from the architecture. A
registered layer's capability report says tiles are unavailable and why, per
ADR-008 §2's never-degrade-silently principle; a tile endpoint does not exist
and fail. Consequence for the lifecycle test in §1: *does this data have a life
outside the service?* now has a second input — if the answer is yes but the
layer needs tiles, those two facts conflict and the organisation must choose.

Both modes are otherwise first-class. An organisation may run entirely hosted,
entirely registered, or a mixture. **An organisation need not have a GIS database at
all** — datastore-only is a supported and probably common deployment.

## 5. Schema editing and data editing

**Clarified by the owner, 2026-08-12.** These are separate questions and an
earlier version of this section conflated them, producing the wrong conclusion
that editing was out of scope entirely.

### Schema — table structure

| Layer mode | Where schema changes happen |
|---|---|
| Registered | **In the source database**, by whoever administers it. We detect drift and refresh (§3). We do not offer DDL over the feature API. |
| Hosted | Through our administrative API, because we own the schema and can sequence the change ([research/runtime-schema-evolution.md](research/runtime-schema-evolution.md)). |

### Data — features, attributes, geometry

**In scope, through our API.** Feature write endpoints exist. The surface is
Q-44, since OGC API Features Part 4 is still a draft.

### The complication

Data writes can also bypass us. Anyone with database credentials — QGIS, a
script, a DBA — can write rows directly. That is not the designated path, but it
is physically possible and it will happen.

**So our bookkeeping cannot assume it sees every change.** Editor tracking,
change history and any concurrency scheme built on our own record of events will
miss direct writes. The rule that follows, from
[ADR-005](adr/ADR-005-api-architecture.md) §3.8: **optimistic concurrency must
be built on database-maintained state, never on what we remember seeing.**

The QGIS extension therefore has a genuine architectural role rather than being
a convenience, covering both the schema path and the direct-write path:

| Extension role | Why it matters |
|---|---|
| **Cache invalidation notification** | Closes the loop we would otherwise have no way to close |
| Publishing | Create a service from a QGIS layer |
| Style transfer | QGIS symbology to MapLibre style — lossy, needs design |
| Service management | Layer list, status, trigger cache seeding |

Where the extension is not used, we fall back to schema-drift polling and
TTL-based expiry. That must work, because we cannot require a specific desktop
client.

Section 28's CRUD, batch editing, editor tracking and optimistic concurrency
are therefore all in scope. The provider-dependent transaction semantics noted
in [ADR-008](adr/ADR-008-query-engine.md) return with them: isolation levels,
locking behaviour and what a conflict looks like differ across PostGIS, SQL
Server and Oracle, and those differences produce provider-dependent bugs rather
than provider-dependent features.

## 5a. Hosted items have owners

**Added 2026-08-12.** Self-service publishing means a hosted layer is not only
data in a store - it is an **item with an owner**.

- The creator owns it, shares it and may delete it.
- Sharing scope is the owner's decision, not an administrator's.
- An administrator may override; they are not in the normal path.
- **Deleting the item deletes the data**, which is what makes the Q-08 lifecycle
  test correct for this case.

Registered services have no owner in this sense. They are corporate services
published by an administrator from a source the organisation already owns.

The entity model must therefore carry ownership and sharing on hosted items, and
that is a distinction the §37 identity model has not yet been designed around.

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
