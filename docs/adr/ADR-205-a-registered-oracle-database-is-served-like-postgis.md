# ADR-205 — A registered Oracle database is served as a registered PostGIS database is

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` for the shape · `LOW` for the driver's licence until condition 1 runs |
| **Decided** | 2026-10-19, by owner decision — Oracle enters v1 as a registered database (§1), and the three answers of §1b confirmed the same day ([Q-182](../open-questions.md), *"evet"*) |
| **Supersedes** | [v1-scope.md](../v1-scope.md) §3a's deferral, for Oracle only; [ADR-008](ADR-008-query-engine.md)'s Q-67 amendment, *registered Oracle layers never serve tiles* (§5c) |
| **Superseded by** | — |

---

## 1. Context

### 1a. What the owner said

Verbatim, 2026-10-19, in order. SQL Server was tried first on the VPS and cannot run there — it publishes amd64 only
and segfaults under emulation (measured, both 2022 and 2025) — and then:

> *"Oracle kurmayı denesek?"* … *"Alıyoruz. Enterprise geodatabase olarak gelecek"* … *"Şirketi geç. Sıfırdan
> kurduğumuzu düşün. Branch versioning olsun. Sen yarat."* … *"Bir de şu var. Biz enterprise geodb derken. SDE
> kurmuyoruz"* … *"Portaldaki yapısı o olacak."* … *"Ya versiyonlu da olabilir, olmayabilir de. Onu düşünürüz.
> Registered database evet. Şu anda öncelik graticula. Ama pro dan bağlanabilirsek tadından yenmez."*

**Settled by them:**

1. **Oracle is in v1**, reversing [v1-scope.md](../v1-scope.md) §3a for Oracle.
2. **As a registered database** — the customer's own Oracle, read in place, as a registered PostGIS database is
   ([ADR-095](ADR-095-registered-postgis-layers-serve-vector-tiles.md)) and as ArcGIS Enterprise references data in a
   registered enterprise geodatabase.
3. **No SDE.** Graticula does not install, read or depend on Esri's geodatabase schema or its `ST_GEOMETRY`.
4. **Versioning is undecided** — *"versiyonlu da olabilir, olmayabilir de"*. Branch versioning was asked for and then
   left for later; nothing here builds it, and nothing here may make it harder.
5. **Graticula first; ArcGIS Pro opening the same tables directly is a prize, not a requirement.**

### 1b. ~~`INFERRED`, for confirmation~~ Confirmed by the owner 2026-10-19 — [Q-182](../open-questions.md), *"evet"*

- **Geometry is `SDO_GEOMETRY`**, Oracle's own type. *Sıfırdan* means we choose; it is the one type Oracle decodes
  itself (`SDO_UTIL.TO_WKBGEOMETRY`), the one ArcGIS Pro reads from an Oracle database with no geodatabase in it, and
  measured working on the arm64 test server (§4). Esri's `ST_GEOMETRY` would need Esri's library, which is outside the
  owner's *no SDE* and lists no arm64 build.
- **Read first, editing second, versioning not at all** until the owner returns to it (§5).
- **Tiles through the datastore**, as DuckDB-read layers are tiled today (§5c).

### 1c. What is true in the code, measured on `origin/main` at `b4b5eb77`

- **A provider is chosen by a two-way branch.** `LayerConnections.SourceFor` (`LayerConnections.cs:241-307`) builds a
  `GeoParquetFeatureSource` when `GeoParquetLocator.Is(...)` and a `PostGisFeatureSource` otherwise; the writer is
  PostGIS-only (`:478`), the tile branch the same (`:654`), the registration probe the same (`DataSourceProbes`,
  `GeoParquetSources.cs:1267`). **An Oracle connection string today would be read as PostGIS**, because
  `GeoParquetLocator.KindOf` (`DataSourceLocator.cs:164`) sends every unrecognised locator there.
- **The attribute filter already has a dialect seam** — [D-162](../architecture-debt.md)'s partial repayment.
  `PredicateSql.TryEmit` (`PredicateSql.cs:67`) emits from the parsed tree for a `SqlDialect`, a sealed class with
  two instances, `PostgreSql` (`@w0`) and `DuckDb` (`$w0`). Oracle is a third instance (`:w0`). Paging, ordering and
  the spatial filter are each provider's own SQL.
- **The geometry boundary is WKB into Core's own types** (`WkbReader`, `WkbWriter`; `Graticula.Core` takes no
  package reference, by rule). Oracle speaks WKB through `SDO_UTIL`.
- **A non-PostGIS source can already be tiled.** `GeoParquetTileSource` reads rows through the feature source and
  hands them to `IMvtEncoder`, whose one implementation sends WKB to the PostGIS datastore and runs `ST_AsMVT` there
  (`PostGisMvtEncoder.cs:259-274`). Q-67's reason for *never tiles* — `ST_AsMVT` is PostGIS's, and an in-process
  encoder cost 80.9 % in garbage collection — does not apply to this route, which nobody has measured for Oracle.
- **The driver's licence is the highest-risk row in [DEPENDENCY-LICENSES.md](../../DEPENDENCY-LICENSES.md)**, written
  as `UNVERIFIED` when the outbound licence was Apache-2.0 — two licences ago; it is now the Graticula Community
  License 1.1 ([ADR-180](ADR-180-the-outbound-licence-is-the-community-licence.md)). The row's question stands and
  its premise is stale.

## 2. Alternatives considered

### Alternative A — read an Esri enterprise geodatabase in Oracle (SDE)

**Argument for.** It is what most Oracle shops running ArcGIS have, and reading their `GDB_ITEMS` would bring their
domains, subtypes and relationships across unchanged.

**Argument against.** The owner ruled it out — *"SDE kurmuyoruz"*. Its default geometry, `ST_GEOMETRY`, is decoded by
Esri's library inside the customer's database or by reproducing Esri's storage format, which CLAUDE.md §5 forbids; and
creating one to test against needs ArcGIS Server's authorization file, which this project does not have (measured: no
`keycodes` on this machine).

### Alternative B — Graticula's own geodatabase schema inside Oracle, with versioning, now

**Argument for.** It is the owner's first sentence — *branch versioning olsun* — and it is what ArcGIS Pro's version
management would talk to.

**Argument against.** The owner withdrew it two messages later — *"onu düşünürüz"*. It is the largest design in this
repository after the tenancy (a storage model for branches, a reconcile, a Version Management Server), and building it
before a single Oracle row has been served would design the hard half on no evidence about the easy half.

### Alternative C — a registered Oracle database whose `SDO_GEOMETRY` tables are served as registered PostGIS tables are *(chosen)*

**Argument for.** It is what *registered database evet* and *öncelik Graticula* ask for; every seam it needs exists
(§1c); and plain `SDO_GEOMETRY` tables with Oracle's spatial metadata and a spatial index are also what ArcGIS Pro
opens from an Oracle database connection — the owner's prize, at no extra cost if it holds (condition 5).

**Argument against.** §3.

## 3. Counterarguments to the preferred option

- **A second engine is where every PostGIS-shaped assumption shows.** v1-scope §3a said so in advance: the cost of
  switching the second dialect off *"is paid later by whoever adds Oracle"*. The forty-odd call sites that ask
  `GeoParquetLocator.Is` to mean *not PostGIS* (§1c) are the visible part.
- **The driver may not be ours to ship** (condition 1). If it is not, Oracle support is a component the customer adds,
  which is a packaging decision ([Q-71](../open-questions.md)) and a worse first experience.
- **Tiles through the datastore move every Oracle tile's rows across the network twice** — Oracle to the server, the
  server to PostGIS. Fine for a city's parcels, unmeasured for a country's.
- **The arm64 test server is not what customers run.** Oracle Free has a 2 GB memory ceiling and 12 GB of user data;
  a plan or a timing measured there says little about an Exadata. CI on amd64 (condition 3) narrows it, not closes it.
- **Oracle's idea of a valid geometry is not PostGIS's**, measured the day this was written. The showcase's Türkiye
  land, copied unchanged from PostGIS into Oracle, is invalid there: both polygons fail
  `SDO_GEOM.VALIDATE_GEOMETRY_WITH_CONTEXT` with **13356** — adjacent points closer than the tolerance — which PostGIS
  does not object to. `SDO_UTIL.RECTIFY_GEOMETRY` makes them valid and **changes the larger one's area by 1.2 %**
  (846,425 to 856,333 km²), so repairing on the way in is not free either. A customer's table will hold such rows:
  the provider reads WKB whatever the validity, and an exact relation that Oracle refuses on an invalid geometry is
  answered or refused deliberately, not as a 500 (condition 8).

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Oracle runs natively on the VPS | `container-registry.oracle.com/database/free` publishes arm64; Oracle AI Database 26ai Free 23.26.3 up in 30 s | measured 2026-10-19 |
| Oracle Spatial works there | `dba_registry` SDO `VALID`; an `SDO_GEOMETRY` table, `spatial_index_v2`, an `SDO_FILTER` window query that found Istanbul and not Ankara, `SDO_UTIL.TO_WKTGEOMETRY` and `TO_GEOJSON` | measured 2026-10-19 |
| A customer-shaped Oracle table exists to build against | schema `TRDATA` in FREEPDB1: `TR_KARA` (2 polygons, 72,919 vertices in the larger, ~1.1 MB of WKB) and `TR_YER` (1,421 points with three text fields), copied from the showcase's PostGIS layers through WKB, spatial metadata registered, `spatial_index_v2`; an `SDO_FILTER` window over Istanbul finds 41 places | measured 2026-10-19 |
| Oracle and PostGIS disagree on validity | both land polygons: 13356 in Oracle, valid enough for PostGIS; rectifying moves the area 1.2 % | measured 2026-10-19, §3 |
| SQL Server cannot run there | `mcr.microsoft.com/mssql/server` 2022 and 2025: amd64 only; under QEMU both exit 139 | measured 2026-10-19 |
| Esri supports Oracle 26ai for geodatabases | Esri's Oracle database requirements list 26ai (23.26.1+) on Linux, and 19c | [doc.esri.com](https://doc.esri.com/en/arcgis-pro/latest/help/data/databases/database-requirements-oracle.html) |
| An Oracle provider's seams exist | dialect, WKB boundary, datastore tile route | §1c |
| The datastore tile route is affordable for Oracle | **Not measured** | condition 4 |
| ArcGIS Pro opens the same tables directly | **Not measured** | condition 5 |
| The managed driver may be redistributed in the server image | **Not verified** | condition 1 |

## 5. Decision

**A registered data source may be an Oracle database. Its tables with an `SDO_GEOMETRY` column are published, queried
and drawn as a registered PostGIS table's are — through `Graticula.Providers.Oracle`, behind the same `IFeatureSource`
the other providers implement, with Oracle's types nowhere in a Core signature. Versioning is not built.**

### 5a. Phase 1 — read

- `DataSourceKinds.Oracle`, recognised by its own locator rather than falling through to PostGIS; a registration
  probe that names the database's version and whether Oracle Spatial is valid in it; the table list read from
  `ALL_SDO_GEOM_METADATA` and `ALL_TAB_COLUMNS`, so a table without spatial metadata is offered as a table, not a layer.
- `OracleFeatureSource : IFeatureSource, IFeatureSummaries`: features, ids, counts, extent, statistics and groupBy;
  the where clause from the parsed tree through `SqlDialect.Oracle`; paging by `OFFSET … FETCH`; the spatial filter in
  the database with `SDO_FILTER` and, for exact relations, `SDO_RELATE`, the way PostGIS answers them; geometry out as
  WKB (`SDO_UTIL.TO_WKBGEOMETRY`) and in the same way.
- The FeatureServer, OGC API Features, WFS and WMS faces answer for it as they do for registered PostGIS.

### 5b. Phase 2 — editing

`OracleFeatureWriter : IFeatureWriter`: `applyEdits` and the OGC writes, in one transaction, with the domain, subtype
and editor-tracking rules the PostGIS writer enforces — the same `EditBatch`, a second implementation. Measured against
the PostGIS writer's own tests before it is offered (condition 6).

### 5c. Tiles — through the datastore

A registered Oracle layer is tiled as a DuckDB-read layer is: rows through the feature source, `ST_AsMVT` in the
PostGIS datastore. `TileSources.Tiled` names Oracle, and ADR-008's Q-67 amendment no longer holds for it. Its tiles
are `best-effort` and keep five minutes by default, as every source other tools write to does (`TileSources.WrittenElsewhere`).

### 5d. What is not decided

- **Versioning** — the owner's to return to. Nothing in phases 1 and 2 adds a column to a customer's table or keeps
  state about one outside the catalogue, so a versioning design starts from the customer's tables as they are.
- **Oracle as the datastore** — A-022 was superseded by Q-32: hosted data lives in PostGIS, and this does not change it.
- **SQL Server, MySQL, MariaDB** — still deferred; the VPS cannot host SQL Server (§4).

## 6. Consequences

**Positive.**

- An ArcGIS shop on Oracle can use Graticula without moving its data — the cost v1-scope §3a named.
- The second dialect returns, and with it the forcing function against PostGIS-shaped assumptions that §3a regretted
  switching off.
- If condition 5 holds, the same table is a Graticula service and a layer in ArcGIS Pro, with no geodatabase between.

**Negative.**

- Every call site that reads *not GeoParquet* as *PostGIS* has to be found and made to ask the kind (§1c, condition 2).
- A second writer doubles the surface where an edit rule can be enforced in one place and forgotten in the other.
- The test server is arm64 Oracle Free and the customers' are not; CI carries the other half.
- If the driver cannot be redistributed, the image ships without it and a customer adds it.

**State.** In the catalogue: data sources of kind `oracle`, their sealed connection strings as today. At runtime: one
Oracle connection pool per registered source, inside the same admission budget the PostGIS pools are counted in.

**Ports created.** `Graticula.Providers.Oracle`, implementing `IFeatureSource`, `IFeatureSummaries` and, in phase 2,
`IFeatureWriter`; the Oracle driver is referenced there and nowhere else, as Npgsql is in `Graticula.Providers.PostGis`.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| A-087 | A customer's spatial tables in Oracle carry `SDO_GEOMETRY` with spatial metadata and an index, or can be given them without moving data | `UNVALIDATED` — new 2026-10-19 with this ADR |

## 8. Dependencies

**Depends on:** [ADR-002](ADR-002-primary-data-architecture.md) (registered sources; the datastore stays PostGIS),
[ADR-008](ADR-008-query-engine.md) (the query engine and its dialects), [ADR-066](ADR-066-geoparquet-layers-read-by-duckdb.md)
(the first non-PostGIS provider and its tile route), [ADR-095](ADR-095-registered-postgis-layers-serve-vector-tiles.md)
(registered layers' tiles and their lifetime), [ADR-180](ADR-180-the-outbound-licence-is-the-community-licence.md)
(what we may ship).

**Depended on by:** none yet.

## 9. Revisit triggers

- **Condition 1 finds the driver cannot be redistributed** — the image ships without it, and how a customer adds it
  becomes a decision of its own.
- **Condition 4 measures an Oracle tile over 500 ms at p95 on a layer a registered PostGIS layer draws in under 100** —
  the datastore route is the wrong one for Oracle, and an in-database encoder (Oracle's own, if it has one by then) or
  pre-seeding is reconsidered.
- **The owner returns to versioning** — a new ADR, starting from §5d.

## 10. Conditions

1. **The driver's licence is read before a line of the provider is merged.** `Oracle.ManagedDataAccess.Core` — the
   managed driver, no native client — under its actual terms, against shipping it in the server image under the
   Graticula Community License 1.1; DEPENDENCY-LICENSES.md's row rewritten from what was read, and its stale Apache-2.0
   premise corrected. If it may not be shipped, phase 1 still proceeds and the image does not carry it.
   **PARTLY DISCHARGED 2026-10-19** — read from the package itself (23.26.301): the Oracle Free Distribution, Hosting, and Use Terms permit redistributing the unmodified driver, with the licence copy, no fee for the driver, Oracle's notices kept, export control the user's, no reverse engineering; the bundled notices are MIT/BSD-style. **It may be shipped.** DEPENDENCY-LICENSES.md's row is rewritten. **Still owed:** the image carries `LICENSE.txt` and `info.txt` beside the driver, held by a test when the provider is merged.
2. **No call site reads *not GeoParquet* as *PostGIS* any more.** Every `GeoParquetLocator.Is` that means *not
   PostGIS* asks the kind instead, and an architecture test fails on a new one.
3. **CI runs the Oracle provider's tests against Oracle Free on amd64** (`gvenzl/oracle-free` as a service container),
   and the fixture recipe seeds the same tables there as on the VPS.
4. **Oracle tiles through the datastore are timed** against the same layer registered from PostGIS, at the
   scale target's largest layer the test server can hold.
5. **ArcGIS Pro opens a table Graticula publishes**, through an Oracle database connection, and draws and identifies
   it — measured with the arcpy harness through an SSH tunnel to the VPS. The owner's prize; its failure blocks nothing.
6. **The Oracle writer passes the PostGIS writer's edit-rule tests** — domains, subtypes, editor tracking, own-only
   editing (ADR-115), one transaction — before editing is offered on an Oracle layer.
7. **[Q-182](../open-questions.md) is answered** — `SDO_GEOMETRY`, the phasing, and the tile route — before phase 1 is
   merged. **DISCHARGED 2026-10-19** — the owner answered *"evet"* to all three.
8. **A geometry Oracle calls invalid is still served.** `TRDATA.TR_KARA`'s two polygons (13356) come back as
   features and tiles, and a spatial relation Oracle refuses on them answers with a refusal that says why — tested
   against them as they are, never against a repaired copy.

## 11. Dissent

None recorded. Alternative B is the owner's own first ask and was set aside by them, not argued down; it returns
when they do.
