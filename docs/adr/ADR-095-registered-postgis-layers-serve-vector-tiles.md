# ADR-095 — Registered PostGIS layers serve vector tiles

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-09-29, by owner decision. The owner asked for the vector tile face's remaining gaps to be closed one at a time; this is item 9 on that list, and the owner stated the decision directly: **a layer published from a registered PostGIS database serves vector tiles**, reversing [Q-67](../open-questions.md) for PostGIS and for nothing else. The default lifetime of a registered layer's tiles, the cache-key identity, the purge on re-pointing a source, the spatial-index notice and the query face following the tile default are this session's design and are marked **INFERRED** where they matter (§5). |
| **Reverses** | [Q-67](../open-questions.md), for registered PostGIS databases only |
| **Depends on** | [ADR-021](ADR-021-tile-encoding.md), [ADR-010](ADR-010-caching.md), [ADR-066](ADR-066-geoparquet-layers-read-by-duckdb.md), [ADR-059](ADR-059-quiescing-a-data-source.md), [ADR-069](ADR-069-query-responses-carry-the-layer-s-cache-lifetime.md), [ADR-070](ADR-070-a-layer-has-a-visible-scale-range.md), [ADR-085](ADR-085-a-tile-leaves-out-what-it-cannot-draw.md), [ADR-093](ADR-093-seeding-the-tile-cache.md) |
| **Amends** | [ADR-021](ADR-021-tile-encoding.md) §1 and §4 (*the datastore is ours* is no longer the only database a tile is encoded in); [ADR-010](ADR-010-caching.md) §5.3 and §6b (a registered layer's default lifetime and its read-back) |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

[Q-67](../open-questions.md) decided on 2026-08-12 that vector tiles come only from hosted data, and
`VectorTileEndpoints.Tileable` enforced it: a service with one layer from a registered database had no
VectorTileServer at all. [ADR-066](ADR-066-geoparquet-layers-read-by-duckdb.md) §9 made the first
exception on 2026-09-13, for GeoParquet and DuckDB sources, and that change is the shape this one copies.

**Q-67's evidence was about a path a registered PostGIS layer never needed.** Run 3 of
[benchmarks/mvt-generation](../../benchmarks/mvt-generation/RESULTS.md) measured the *in-process* tile path
— rows read into the server and encoded there — at 28.3 req/s and 80.9% GC pause, against 96.3 req/s and
0.3% for PostGIS pushdown. The in-process path exists for engines without `ST_AsMVT`: SQL Server and Oracle.
A registered PostGIS database has `ST_AsMVT`, so the fast path was always available to it; Q-67 refused
it for a different reason, which its own row gives as *"tiles come from data this server owns as system of
record"* — the cache cannot see writes to somebody else's database.

**That reason is real, and [ADR-010](ADR-010-caching.md) had already answered it for every other cache.**
§5.2 says coherence cannot be guaranteed for data we do not control and that TTL is the floor; §5.3 says
volatility is declared per layer; §6b says the policy must be readable per layer. Every registered layer's
`query` answer has been cached under exactly those rules since [ADR-069](ADR-069-query-responses-carry-the-layer-s-cache-lifetime.md).
The tile was the one face still refusing on the ground the others had already crossed.

What breaks without this: an organisation with its data in its own PostGIS has feature services and no
tiles, so every map over a dense registered layer draws from `query` — the complaint ADR-069 was written
about — and the console's preview, which draws from tiles wherever a layer offers them, draws those layers
the slow way.

## 2. Alternatives considered

### Alternative A — keep Q-67: registered data copies into the datastore for tiles *(not chosen)*

**Argument for.** The cache follows every write, because every write comes through this server. Nothing
about coherence has to be explained to anybody.

**Argument against.** The owner has decided otherwise, and the cost Q-67 accepted — *an organisation must
copy data to get tiles, not merely point at it* — is the cost the owner no longer wants to charge. The
copy is also stale the moment the source changes, so it buys exactness of the cache by giving up
freshness of the data, which is not a better answer to the same problem.

### Alternative B — registered PostGIS through the hosted tile statement, bounded by a lifetime *(chosen)*

The same `PostGisTileSource` statement, run in the registered database through its own pool; the tile's
lifetime bounds staleness from writes this server does not see; writes it does see purge at once, as for a
hosted layer.

**Argument for.** Nothing new is encoded or measured — the statement is the one ADR-021 measured, and
0eaf635's per-row transform and Q-141's datum notice already handle a registered table's own reference.
The coherence story is ADR-010's, written down before this existed.

**Argument against.** A map can show a registered edit made in another tool up to one lifetime late,
and the operator has to be told so (§5.3).

### Alternative C — change detection on registered sources (triggers, a modified-timestamp column, logical decoding) *(not chosen)*

**Argument for.** Exact invalidation, which ADR-010 §5.2 lists as mechanism 2.

**Argument against.** It needs DDL or privileges in somebody else's database, and [ADR-002](ADR-002-primary-data-architecture.md)
§4.2 says this server writes rows into a database it does not own and never schema. It is also a
subsystem, and the owner's instruction was not to invent one. [D-266](../architecture-debt.md) names the
cheap version — a PostGIS data version an edit counter could share — as a revisit trigger, and it stays
one (§9).

### Alternative D — tiles for every registered engine *(not chosen)*

**Argument for.** One rule for all registered data.

**Argument against.** There is no other registered engine in v1 ([v1-scope](../v1-scope.md) §3a), and for
SQL Server and Oracle Q-67's measured reason — no `ST_AsMVT`, so the 80.9%-GC in-process path — is intact.

## 3. Counterarguments to the preferred option

**"Best-effort" will be read as a bug the first time somebody edits in QGIS and the map does not change.**
[ADR-010](ADR-010-caching.md) §14 says exactly this. The answer here is the one §6b asks for: the policy is
readable per layer (`coherence: "best-effort"` and the lifetime, §5.3), and the default is short enough
that an operator can say it in one sentence.

**A cold tile is now a query against somebody else's database**, possibly an expensive one — a table with
no spatial index scans on every cold tile (§5.2). The budget (D-277) bounds how many run at once, per
source; it does not make each one cheap.

**The shorter default makes a registered map colder than a hosted one**: a pan revisiting an area after
five minutes rebuilds its tiles. That is the price of the bound, and the per-layer setting is how an
administrator who knows the layer is static buys it back.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| The tile statement is fast on PostGIS; the slow path is the in-process one | 96.3 req/s and 0.3% GC pause (pushdown) against 28.3 req/s and 80.9% (in-process) | [benchmarks/mvt-generation/RESULTS.md](../../benchmarks/mvt-generation/RESULTS.md) run 3 |
| A table outside Web Mercator tiles correctly, with its index still used | 74.6 ms against 21.6 ms for a transformed tile, index on the stored column | Q-96, `PostGisTileSource`, 0eaf635 |
| A registered table with no index and in EPSG:4326 builds the right tile | `ARegisteredTableTilesWithOrWithoutAnIndexTests` (needs `GRATICULA_TEST_PG`) | this ADR |
| A registered layer's tile holds what the hosted layer over the same table holds, and no more than the FeatureServer finds in the box | `ARegisteredPostGisLayerServesTilesTests` (needs a running server) | this ADR |
| Not measured: throughput from a remote registered database, or any tile over a network link to one | — | §9 |

## 5. Decision

**A layer is tileable when it is hosted, when its source is a registered PostGIS database, or when its
source is GeoParquet or DuckDB. A registered PostGIS layer's tile is built by the hosted layer's statement
in its own database, keyed by that database, kept for five minutes unless an administrator says otherwise,
and read back as best-effort.** In detail:

### 5.1 One rule, by kind

`TileSources.Tiled(hosted, kind)` in `Graticula.Platform.Admin` is the rule, and every reader asks it:
`VectorTileEndpoints.Tileable` (the tile face, the seed, the services directory and the portal through
`ServiceFaces.Tileable`), and `PostgresAdminCatalog.ListLayersAsync`, which spelled a copy of it in SQL
until today. **A whitelist of kinds**: the five kinds a registration can name today all tile, and a kind
added later — the SQL Server, Oracle or MySQL provider v1-scope §3a defers — is refused with the existing
400, reworded to say which sources do tile, until somebody decides how its rows reach a tile. The base
map ground and the server settings' candidate list now ask `ServiceFaces.Tileable` as well; they asked
only whether the face was switched on.

### 5.2 Encoding: the hosted statement, the layer's own database

`LayerConnections.TileSourceFor` already built `PostGisTileSource` over `PoolFor(layer.ConnectionString)`
for every non-file layer, so a registered layer reaches its own pool through the same gate — quiesce
(ADR-059), then the breaker, then `ConnectionBudget` keyed on its connection string (D-277). Nothing in
the statement assumes the datastore: schema, table, geometry column and SRID come from the layer
definition, and a table in another reference is transformed per row with the datum crossing reported once
(Q-141). Registration offers only `geometry` columns with a declared SRID (`PostgresDataSourceProbe`), so
a `geography` or SRID-0 column does not reach the statement.

- **Identity.** No MVT feature id is set for any layer, hosted or registered; the identity column rides
  as a tag like every other attribute, within `VectorTileEndpoints.AttributesOf`'s twelve-column cap. A
  registered layer uses its declared identity (ADR-013) exactly as a hosted one uses `objectid`.
- **No spatial index.** Served, slowly — the `&&` scans. The describe now reads whether the geometry
  column has a GiST, SP-GiST or BRIN index (`LayerDescription.SpatiallyIndexed`, null for a view or a
  foreign table), the tile path logs a warning once per layer, `/admin/health` lists them under
  `unindexedLayers`, and `GET /admin/services/{name}/cache` reports `spatialIndex` per layer. Creating
  the index is the table owner's; this server writes no schema into a registered database (ADR-002 §4.2).
- **PostGIS 3.0 or later**, for `ST_TileEnvelope`. An older registered database answers with an undefined
  function, which `ErrorResponse` now names as a version question rather than a missing install.

### 5.3 Lifetime and coherence — INFERRED: five minutes

A registered PostGIS layer with no lifetime of its own keeps its tiles **five minutes**, or the server's
default when that is shorter (`TileSources.RegisteredLifetime`). *INFERRED*: the owner decided the
lifetime is shorter than a hosted layer's; the number is this session's. Sixty minutes is the hosted
default, where a layer's own edits empty its cache at once; one minute keeps almost nothing of a map a
person is panning over; five is a window an operator can state — *an edit made in another tool appears
within five minutes* — and is the order of ADR-010 §5.2's WMS time-extent window. The same number goes out
as `Cache-Control: max-age`, and ADR-069 makes the query face carry it too, so a registered layer's query
answers moved from the server default to five minutes the same day (`QueryResponseCaching.LifetimeOf`
now asks `VectorTileEndpoints.LifetimeOf`).

`PUT /admin/layers/{name}/cache` is ADR-010 §5.3's declared volatility and overrides the default either
way. An edit made through this server's FeatureServer or OGC API Features to a registered layer empties
its tiles at once (`TilePurgingWriter`, unchanged). A layer somebody can edit is still sent `no-cache`
(V-56) and revalidates against its ETag.

**Readable per layer (ADR-010 §6b).** `/admin/layers` carries `kind`, `tileLifetimeSeconds` and
`coherence` — `exact` for hosted, `file-version` for a file whose version rides in its tile key,
`best-effort` for a registered PostGIS or MotherDuck database. `GET /admin/services/{name}/cache` carries
the same per layer, with `lifetimeFrom` saying whether the number is the layer's own or the default.

### 5.4 Cache key and a source pointed elsewhere — INFERRED

`PUT /admin/datasources/{id}` can point a source at another database and keep every layer id, and a tile's
key was the layer id and a fingerprint of the layer's shape — so the new database's map was served the old
one's tiles for their whole lifetime. Two changes, both INFERRED from the owner's instruction:

- **The fingerprint carries the database.** For a registered PostGIS layer, `VectorTileEndpoints.KeyOf`
  passes `SourceQuiesce.DatabaseKey` — host, port and database, the fold ADR-059 §5d already uses for
  *the same database* — as the fingerprint's version. A rotated password or a pool setting keeps the
  pyramid; another database does not. Hashed, never in the path. Null for hosted, so no hosted key moved
  and `TilePipeline.Version` did not.
- **The update purges when the place moved.** `UpdateDataSourceAsync` purges every layer on the source when
  `DatabaseKey` changed (or the file locator, for a file source, or when the old locator cannot be read),
  and says how many tiles in `tilesPurged`. The fingerprint is what holds on a node the purge did not
  reach; the purge is what frees the disk.

### 5.5 Seeding

ADR-093 seeds a service with registered PostGIS layers the way it seeds a hosted one: `WhyNotSeedable`
asks `Tileable`, and each build takes `AdmitTileBuildAsync(layer)` — a permit from the registered source's
own budget. The seed's concurrency rule (§5.5 of ADR-093) is per seed, and a seed's layers are its source's.

## 6. Consequences

**Positive.** A registered PostGIS database gets tiles without copying its data; the preview and a map
draw it from tiles; the cache follows ADR-010's rules for registered data rather than a refusal.

**Negative.**

- A map may show a registered layer up to five minutes behind an edit made in another tool. Said in the
  read-back rather than implied away; it is ADR-010 §14's dissent arriving.
- A registered layer's `query` answers are kept five minutes rather than the server default (§5.3). A
  behaviour change for every registered layer nobody set a lifetime on.
- A cold tile is a query against somebody else's database; an unindexed table scans per cold tile.
- **Not done, and named**: a MotherDuck table also changes behind this server with no version to key on
  and reads back `best-effort`, but keeps the server default lifetime — its default was not part of the
  owner's decision ([D-279](../architecture-debt.md)). A wide registered table whose identity column is
  past the first twelve taggable columns carries no identity in its tiles — true of hosted layers too, and
  not changed here because it would move hosted tile bytes. Stale-while-error ([D-278](../architecture-debt.md))
  matters more now that a registered source's outage is felt on tiles.

**Ports created.** None; `ITileSource` and `PostGisTileSource` are unchanged in shape.

**State.** *Catalogue*: none — no column or table; a layer's kind, lifetime and identity were already
there, and `tilesPurged` is a response field. *Runtime*: `UnindexedLayerNotices`, one entry per layer
tiled from an unindexed table, **node-local**, bounded at 1,024 and lost on restart, like
`DatumShiftNotices`. The tiles themselves are ADR-010's cache, node-local on disk; a data source's purge
reaches this node's cache only, and the database identity in the key is what holds on the others.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| A-028 | Administrators can and will declare layer volatility usefully | `UNVALIDATED` — the five-minute default is what a layer gets when they do not |

## 8. Dependencies

**Depends on:** [ADR-021](ADR-021-tile-encoding.md) (the statement), [ADR-010](ADR-010-caching.md) (§5.2,
§5.3, §6b), [ADR-059](ADR-059-quiescing-a-data-source.md) (the source key), [ADR-069](ADR-069-query-responses-carry-the-layer-s-cache-lifetime.md),
[ADR-093](ADR-093-seeding-the-tile-cache.md).

**Depended on by:** anything that lists faces — the services directory, the portal, the base map ground.

## 9. Revisit triggers

- **A cheap PostGIS data version is decided** ([D-266](../architecture-debt.md)'s trigger): a registered
  layer's tiles could then be keyed by it and the lifetime stop being the only bound.
- An operator reports a registered layer's map as stale inside its lifetime and the read-back did not tell
  them why — §6b failing at its own job.
- A registered source's cold-tile load is measured as the reason a customer's database is slow, which is
  the moment to measure tile throughput against a remote registered database (§4, not measured).
- A second registered engine is added (v1-scope §3a): Q-67 decides it unless a new ADR does.

## 10. Dissent

Q-67's own framing — *publishers host and get tiles; GIS administrators register from desktop and get
features* — was a clean division of the two user types, and this blurs it. Kept as the record of what
was given up; the owner's decision is that pointing at data should be enough.
