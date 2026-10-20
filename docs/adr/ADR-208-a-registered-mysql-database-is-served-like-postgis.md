# ADR-208 — A registered MySQL database is served like PostGIS

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-10-20, by owner decision: asked whether MySQL could be installed on the server (*"mysql kurabiliyor muyuz vps'e"*), told to do it (*"hepsini yap"*), and after Oracle was finished, to begin (*"başla"*). The scope — the same as Oracle's, nothing left out — is `INFERRED` from *"önce oracle tarafını bitirelim. dışarıda bırakılmasın bir şey"* and listed in §5 for confirmation. |
| **Reverses** | [v1-scope.md](../v1-scope.md) §3a for MySQL, which deferred it with SQL Server and MariaDB |
| **Supersedes** | — |
| **Superseded by** | — |

---

## 1. Context

A registered MySQL database is to be served as a registered PostGIS or Oracle one is: its spatial tables published
in place as feature services, queried and edited, with what [ADR-205](ADR-205-a-registered-oracle-database-is-served-like-postgis.md)
and [ADR-207](ADR-207-attachments-and-history-on-registered-layers.md) gave Oracle. MySQL 8.4.11 runs on the test
server since 2026-10-20 (`graticula-mysql`, `127.0.0.1:13306`, memory capped at 512 MB).

### 1a. What MySQL does, measured on 8.4.11 before anything was designed

| Question | Answer |
|---|---|
| Axis order under EPSG:4326 | **Latitude first.** `POINT(39.93 32.85)` with SRID 4326 is latitude 39.93; `ST_AsBinary` writes latitude first unless asked `'axis-order=long-lat'`, and `ST_GeomFromWKB` reads the same way. |
| Z and M | **None.** `ST_GeomFromWKB` refuses an ISO Z point (error 3037). MySQL stores x and y only. |
| Spatial index | Used by `MBRIntersects` (`EXPLAIN`: range on the index); it needs the column `NOT NULL` with an `SRID` attribute. |
| Catalogue | `information_schema.ST_GEOMETRY_COLUMNS` gives each spatial column's SRID and type; `ST_SPATIAL_REFERENCE_SYSTEMS` says which are EPSG's. |
| SQL | With `sql_mode` `ANSI`, double-quoted identifiers and `\|\|` work as in PostgreSQL; `substring … from … for`, `position … in`, `extract`, `cast(… as double precision)`, `json_table` all work. No `ilike`, no `::`, no `RETURNING`. |
| Relations under 4326 | MySQL computes them on the ellipsoid, where PostGIS's `geometry` is planar — so the two would disagree on the same rows. |

## 2. Alternatives considered

### Alternative A — answer every relation in MySQL

**Against.** Under a geographic reference MySQL's answer is the ellipsoid's and PostGIS's is the plane's; the same
query on the same rows would answer differently by engine, which ADR-205's parity runs exist to rule out.

### Alternative B — MySQL's index finds the candidates, this server decides *(chosen)*

`MBRIntersects` asks the spatial index for the rows whose boxes meet the filter, and the shapes are decided here —
`GeometryPredicates` for intersection, the geometry engine for the rest (`EngineRelations`) — exactly as an Oracle
layer's are. The answers are PostGIS's by construction.

### Alternative C — a second set of `IsMySql` branches beside the `OracleLocator.Is` ones

**Against.** There are about thirty of those branches in the host. A third engine wired the same way is the
propagation failure [D-130](../architecture-debt.md) names: each new capability has to remember every engine, and the
one it forgets answers as PostgreSQL. **The second non-PostgreSQL SQL engine is the moment to put a seam in**: one
place in the host that, for a locator, gives the source, the writer, the attachments, the history, the related
records, the table checks and the pruning — with Oracle moved behind it too.

## 3. Counterarguments to the preferred option

- **Moving Oracle behind a seam risks what was just finished.** Mitigated by moving it with its tests and its REST
  runs repeated afterwards, before MySQL is put beside it.
- **MySQL has no row version.** There is no `xmin` and no `ORA_ROWSCN`; a version has to be computed from the row's
  content, which costs a hash per conditional edit.
- **No Z or M** means a 3D client writing to a MySQL layer is refused, where PostGIS would store it.

## 4. Evidence

§1a, measured with `D:/temp/mysql-probe.py` against MySQL 8.4.11 on the test server, 2026-10-20.

## 5. Decision

**A registered MySQL 8.0 or later database is served as a registered PostGIS or Oracle one is**, through
`Graticula.Providers.MySql`, the only project that names the driver (**MySqlConnector**, MIT). Its locator is
`mysql:` followed by JSON — host, port, database, user, password — sealed like every other.

1. **Spatial tables** with an `SRID` attribute and a spatial index are offered; one without either is listed as
   skipped with the reason, as Oracle's are.
2. **Geometry crosses as WKB in longitude–latitude order** both ways (`'axis-order=long-lat'`), so nothing above the
   provider sees MySQL's latitude-first storage.
3. **The spatial filter is MySQL's index, the decision is this server's** (Alternative B).
4. **Where clauses are written again from the parsed tree** in a MySQL dialect, on a session in `ANSI` mode.
5. **Editing, GlobalIDs, row versions, relationships, attachments and history** as for Oracle (ADR-205 as amended,
   ADR-207), each where MySQL can carry it: no Z or M; a row version computed from the row.
6. **The host reaches every registered SQL engine through one seam**, Oracle's moved behind it first.

**`INFERRED` — for the owner to confirm:**

- **The scope is Oracle's** — editing, attachments and history included — from *"dışarıda bırakılmasın bir şey"*,
  which was said of Oracle.
- **MySQL 8.0 or later, and not MariaDB**, whose spatial support has no SRID-aware axis order and no
  `ST_GEOMETRY_COLUMNS`.
- **A row's version is a hash of its content**, for If-Match.

## Conditions

1. **The engine seam**, with Oracle behind it and its tests and REST runs passing unchanged. **DISCHARGED 2026-10-20** —
   `ISqlEngine` in the host, `SqlLocators` in the platform; afterwards Oracle answered 110/110 relations and 87/87 query
   features against PostGIS, its attachment and history runs had 0 failures, and its provider tests 98/98.
2. **Phase 1, reading**: registration and the probe, features, ids, counts, extent, statistics, every relation and a
   distance, the query features Oracle answers — measured against a PostGIS layer through REST. **DISCHARGED 2026-10-20**
   — the Oracle runs' two sets asked of a MySQL layer and a PostGIS layer holding the same 1,421 places and two land
   polygons: 110 of 110 relation queries and 87 of 87 query features alike, and 40 of 40 percentiles (which MySQL has no
   function for, computed here from window ranks as PostgreSQL defines them). Two differences found and repaired on the
   way: MySQL averages and divides whole numbers into a DECIMAL kept to four places (now a double, as PostgreSQL's), and
   it computes no envelope under a geographic reference (the extent is taken with the SRID set to 0, longitude first).
3. **Phase 2, editing**: adds, updates and deletes under `EditColumns`' rules, GlobalIDs, row versions, one
   transaction across layers. **DISCHARGED 2026-10-20** — `MySqlFeatureWriter`, under `EditColumns` as Oracle's writer
   is: its provider tests (adds, updates, deletes; a Z shape and a table without `AUTO_INCREMENT` refused by name;
   GlobalIDs kept as `CHAR(38)`, `CHAR(36)` and `BINARY(16)`, minted, filtered and looked up; an edit at a row version
   and refused after it moved; two layers kept or discarded together) 14 of 14, and through REST on the local fixture
   26 of 26: applyEdits adds in 4326 and 3857 stored longitude first, an update, a delete, a bad value, a Z shape,
   `rollbackOnFailure`, editor tracking filled, a GlobalID returned and queried, and OGC API Features create and
   replace with `If-Match` at the version read (and 412 at one that moved). The writer's connections are the only ones
   that allow session variables, which the history trigger of condition 4 will read the editor from.
4. **Relationships, attachments and history** as ADR-207 gives them.
5. **A CI job** against a MySQL container, as the Oracle job runs against one.

## 6. Consequences

**Positive.** An organisation on MySQL publishes in place. The engine seam makes the next engine (SQL Server, when
there is a machine to run it) one implementation rather than thirty edits.

**Negative.** A third database in CI. MySQL's lack of Z, M and row versions shows through as refusals and as a
computed version.

**State.** In the catalogue: a data source of kind `mysql` (a migration widens the kind's check). Nothing new at
runtime beyond a pool per database.

**Ports created.** The engine seam in the host; MySqlConnector behind `Graticula.Providers.MySql`.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | A customer's MySQL spatial tables carry an SRID attribute and a spatial index, or can be given them | `UNVALIDATED` |

## 8. Dependencies

**Depends on:** [ADR-205](ADR-205-a-registered-oracle-database-is-served-like-postgis.md),
[ADR-206](ADR-206-vector-tiles-are-served-from-the-datastore-only.md),
[ADR-207](ADR-207-attachments-and-history-on-registered-layers.md).

**Depended on by:** —
