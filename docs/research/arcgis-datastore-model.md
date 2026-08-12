# ArcGIS Data Store and Reference-Registered Data

**Status:** FIRST PASS — written 2026-08-12 at the owner's request, to correct an
earlier assumption before we design on top of it.
**Corrects:** [hosted-datastore-and-tiles.md](hosted-datastore-and-tiles.md) §4
and [data-model.md](../data-model.md) §3, both of which described the
hosted/registered split incorrectly.
**Clean room:** publicly documented behaviour only (§5). Sources at the end.

---

## 1. The correction

Earlier notes claimed:

> Hosted data gets full capability. Registered data gets whatever its provider
> supports.

**That is wrong, and in places backwards.** The documented behaviour:

- **Referenced data is editable.** "Edits made to the web layer are reflected in
  the data source." Referencing is not a read-only mode.
- **Some advanced capabilities *require* referencing, not copying.** "Utility
  network, network analysis, parcel fabric… require data to be referenced." The
  capability arrow points the other way for the most sophisticated features.
- **Some capabilities require copying.** Sharing to ArcGIS Online, and portal
  analysis outputs, which have nowhere else to live.

So the axis is not capability. It is **who manages the storage, and where edits
land.**

This matters because our whole hosted-versus-registered design was built on the
wrong axis. Better to find that now than after ADR-008.

## 2. What ArcGIS actually distinguishes

| | Reference registered data | Copy all data |
|---|---|---|
| Where data lives | The organisation's folder, database or cloud store | ArcGIS-managed storage |
| Currency | "Automatically reflects changes to the data" | "Changes to the source data won't appear in the web layer. You must overwrite the web layer or share a new web layer" |
| Editing | Writes through to the source | Writes to the ArcGIS-managed copy; source unaffected |
| Publish cost | "Takes less time and additional server storage space is not required" | Copies everything |
| Prerequisite | "You must register the data source with the server" | None — "unregistered data is copied when possible" |
| Layer types | "Map image, feature, vector tile, imagery, scene, and elevation layers" | Broader portal support, including ArcGIS Online |

The plain reading: **referencing is the enterprise default, copying is the
self-service default.** Copying exists because ArcGIS Online has no customer
database to point at, and because analysis outputs need somewhere to land.

## 3. ArcGIS Data Store is five stores, not one

Worth knowing, because it shows a separation we half-made by accident.

| Store | Holds |
|---|---|
| Relational | Hosted feature layer data, and portal analysis outputs |
| Tile cache | Caches for hosted scene layers |
| Spatiotemporal big data | Real-time observation archives, GeoAnalytics results |
| Graph | Knowledge graphs |
| Object | Video layers, and **cached query responses for hosted feature layers** |

Two observations that matter for us:

1. **Cache storage is separated from data storage.** Tile caches are not in the
   relational store. Our [ADR-002](../adr/ADR-002-primary-data-architecture.md)
   already says large binary assets are not database rows, so we are consistent
   — but we had been describing the datastore as the home for cache bytes, and
   that conflates two very different lifecycles. Cache is disposable,
   regenerable and high-churn. Hosted data is not.
2. **Query response caching is a distinct store.** They cache feature query
   responses, not just tiles. Worth remembering when
   [ADR-010](../adr/ADR-010-caching.md) is written.

## 4. One structural difference we must not gloss over

ArcGIS's "registered database" is usually an **enterprise geodatabase** — an
ArcGIS-defined schema layered on top of Oracle, SQL Server or PostgreSQL, with
its own system tables for versioning, editor tracking and behaviour.

`VERIFY` the exact boundary of what a plain, non-geodatabase database supports
when registered. But the general shape is clear enough to matter:

> **Even in the customer's own database, ArcGIS controls a schema.**

That is how referenced data gets versioning, conflict handling and editor
tracking without owning the server.

**We will not have that.** We are pointing at arbitrary customer tables that were
not designed for us. So capabilities ArcGIS provides on referenced data — via
its geodatabase schema — are not automatically available to us on a registered
table.

This is the real capability boundary, and it is a different one from
hosted-versus-registered:

| | ArcGIS | Us |
|---|---|---|
| Owns the server | Only for hosted | Only for the datastore |
| Owns a schema in the customer's database | Yes, via the geodatabase | **No** |
| Can add system tables for versioning and tracking | Yes | Only where granted write access |

Two honest options follow, and this is a real decision rather than a detail:

- **Accept plain tables as they are.** Query and serve them. No versioning, no
  editor tracking, no conflict detection, because there is nowhere to put the
  bookkeeping. Simple, honest, and limited.
- **Offer an optional companion schema** in a registered database where we are
  granted rights, holding our bookkeeping alongside the customer's tables. More
  capable, and it is the ArcGIS answer — but it means asking a DBA for DDL, and
  it makes us a resident in someone else's database.

## 5. Reconsidering our structure

Taking the owner's stated posture — editing happens in the source database, with
a QGIS extension as the tool — the model simplifies past ArcGIS's.

### Proposed: three roles, not two modes

**1. Registered source — the system of record.** Always the organisation's.
Always current. This is the normal case and should be the default everything is
designed around.

**2. Derived store — our materialisations.** Generalised geometry per zoom,
tile caches, query response caches, spatial index helpers. Explicitly derived,
explicitly regenerable, **never the system of record.** If it is lost, we rebuild
it. This is what the "datastore" mostly turns out to be once editing moves to
the source.

**3. Managed store — data with nowhere else to live.** Uploaded files, migration
landing area. This is the only genuine *hosting*, and it may not be needed in
v1 at all.

### Why this is better than copying ArcGIS's model

- **It removes the staleness problem instead of managing it.** ArcGIS's copy
  mode goes stale and the documented remedy is to overwrite or republish. Our
  derived store is stale *by definition* and has a refresh policy, which is a
  much smaller promise to keep.
- **It matches the owner's posture.** If editing is at source, hosting is not
  where content lives — it is where speed comes from.
- **It answers Q-33 by dissolving it.** Copy-versus-replicate stops being a
  question when the copy is openly a derived artefact with a refresh policy.
- **It keeps the capability model honest.** The write dimension from
  [data-model.md](../data-model.md) §2 still applies, but it now governs *what we
  can materialise*, not *what the user can do*.

### What must be decided

Role 3 is the open one. If an administrator can be expected to load a shapefile
into their own database with QGIS and then register it, role 3 disappears and
the architecture gets materially smaller. If we want a drag-and-drop upload
experience, it does not.

That is a product question, not a technical one, and it is Q-40.

## 6. Effect on earlier positions

| Earlier claim | Status |
|---|---|
| "Hosted gets full capability, registered gets provider capability" | **Wrong.** Referenced data is editable and some advanced capabilities require it. Q-31's answer must be rewritten. |
| "Runtime schema evolution is hosted-only" | **Still true**, and unaffected — it needs DDL rights, which is a permission question. See [runtime-schema-evolution.md](runtime-schema-evolution.md). |
| "The datastore is a provider with write rights" | **Still true and now clearer.** Under §5 it is mostly a *derived* store, which is a narrower and easier thing to build. |
| "The datastore holds cache bytes" | **Refine.** ArcGIS separates cache stores from the relational store. Cache is disposable and high-churn; hosted data is not. Keep them separate. |
| "The datastore is optional" | **Unchanged**, and easier to justify now: a derived store is an optimisation, and optimisations are optional by nature. |

## 7. New questions

| # | Question |
|---|---|
| Q-40 | **[OWNER]** Do we accept data uploads at all, or does the administrator load data into their own database (with QGIS) and register it? If the latter, the managed store disappears from v1 and the architecture gets materially smaller. |
| Q-41 | Do we offer an optional companion schema in a registered database where granted rights, to hold versioning and editor-tracking bookkeeping? This is how ArcGIS gives referenced data advanced capability, and it means being a resident in someone else's database. |
| Q-31 | **Reopened.** The previous answer was based on a wrong reading of the ArcGIS model and must be rewritten once editing scope is settled. |

## Sources

- [Understanding reference registered data and copy all data — ArcGIS Pro](https://doc.esri.com/en/arcgis-pro/latest/help/sharing/overview/understanding-reference-registered-data-and-copy-all-data.html)
- [Data and publishing in ArcGIS Enterprise](https://enterprise.arcgis.com/en/portal/latest/use/data-publishing-and-enterprise.htm)
- [Share feature layers and view data as copies — Portal for ArcGIS](https://enterprise.arcgis.com/en/portal/latest/administer/windows/about-sharing-feature-layer-data-as-copies.htm)
- [Introduction to ArcGIS Data Store](https://enterprise.arcgis.com/en/data-store/latest/install/windows/what-is-arcgis-data-store.htm)
- [ArcGIS Data Store vocabulary — Portal for ArcGIS](https://enterprise.arcgis.com/en/portal/latest/administer/windows/arcgis-data-store-terms.htm)
- [Apps and functionality that require ArcGIS Data Store](https://enterprise.arcgis.com/en/portal/latest/administer/windows/what-requires-arcgis-data-store.htm)
