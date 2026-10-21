# Other databases, tried beside PostGIS (2026-10-21)

The owner asked, after MariaDB and Oracle were measured, for any other database worth trying — *"nosql db ler de
olabilir"*. Four were run on the VPS, loaded with the same 200,000 points, and asked the same ten questions as
[registered-engine-speed.md](registered-engine-speed.md), **each in its own query language, not through Graticula**:
none has a provider, and the question is whether one would be worth writing. Nothing here is decided;
[Q-187](../open-questions.md) asks the owner.

## Method

- **Data and questions** as in registered-engine-speed.md: 200,000 points, one CSV (sha256 prefix
  `1d6df9807e72ea06`), ten queries; every answer compared with PostGIS's before anything is timed.
- **Engines**, each in a container on the VPS (arm64, 4 cores, 8 GB shared with the showcase), started for the trial
  and stopped after it, memory-capped: **MongoDB 8.0** (640 MB, WiredTiger cache 256 MB; `2d` and `2dsphere`
  indexes), **Redis 8.2** with its query engine (640 MB; `GEO` and `GEOSHAPE FLAT` fields), **CockroachDB v25.2.4**
  single node (900 MB, cache 128 MB; `GIST`-named inverted index), **OpenSearch 2.19.2** single node (1.3 GB, heap
  512 MB; `geo_point`, one shard, force-merged).
- **Client**: one Python container on the same Docker network, each engine's own driver (psycopg, pymongo, redis-py,
  opensearch-py); 5 warm-ups and 40 runs, engines alternating; median ms of the call, rows fetched.
- **PostGIS here is native SQL**, which is why its numbers are lower than in registered-engine-speed.md, where they
  include Graticula's REST face. Compare the columns of one table, not across the two documents.
- Script: `D:/temp/nosql-bench.py` (outside the repository, disposable).

## Answers

| Query | MongoDB | Redis | CockroachDB | OpenSearch |
|---|---|---|---|---|
| count all, count where, group by | same | same | same | same |
| 1×1 box, features; whole-region box count | same (`2d`, planar) | same (`FLAT`) | same | same |
| pentagon intersects | same with `2d`'s `$polygon`; **112 of 14,983 differ** with `2dsphere` | same | same | same |
| a lat/lon box as a `2dsphere` polygon | **191,519 against 200,000** | — | — | — |
| within 50 km | 1,459 against 1,463 | 1,465 | same | 1,468 |
| page at offset 100,000 | same | **wrong rows**, see below | same | same, after raising `max_result_window` |

**Edges.** MongoDB's `2dsphere` draws a polygon's edges as great circles, so a box from 26° to 45° E along 36° N
bulges north and loses 8,481 points near its southern edge; ArcGIS and PostGIS `geometry` draw them straight in
degrees. A provider would have to use the planar `2d` index for envelopes and polygons — which MongoDB documents as
legacy — and `2dsphere` only for distances.

**Distance.** Each engine measures on its own sphere (MongoDB 6,378.1 km, Redis 6,372.8 km, OpenSearch 6,371.0 km)
where PostGIS `geography` measures on the WGS84 ellipsoid; 2–5 of 1,463 points move across the 50 km line. A
provider would refine those here, as the MySQL and Oracle ones do with Vincenty.

**Redis answers a slow query partly, and says nothing.** The query engine's default is `TIMEOUT 500` with
`ON_TIMEOUT return`: the sorted page at offset 100,000 took 1.15 s and came back with 183,799–191,599 *total*
results and rows starting at 103,095–108,898 instead of 100,001, different on each run. With `TIMEOUT 0` it took
1.3 s and was right. A provider would have to set `ON_TIMEOUT fail` and treat a timeout as an error.

**CockroachDB refuses `&&`.** `geometry && box2d` is *"experimental"* behind a cluster setting, so the box queries
were written `ST_Intersects`. Every other answer, including the geography distance, matched PostGIS exactly.

## Speed, median ms

| Query | PostGIS | MongoDB | Redis | CockroachDB | OpenSearch |
|---|---:|---:|---:|---:|---:|
| count all | 13–18 | 56 | 12 | 153 | 7 |
| count where `tur = 'city'` | 62–72 | 82 | 7 | 168 | 7 |
| 1×1 box, 1,768 features | 7–9 | 44 | 87 | 100 | 92 |
| whole region box, count | 65–76 | 1,147 | 132 | 791 | 7 |
| pentagon, 14,983 ids | 76–83 | 290 | 78 | 434 | 465 |
| count and avg by `tur` | 68–79 | 161 | 300 | 198 | 9 |
| one feature by id | 0.5 | 1.3 | 0.5 | 2.2 | 3.2 |
| top 100 by `nufus` | 62–83 | 145 | 29 | 159 | 17 |
| within 50 km, count | 65–91 | 23 | 4 | 739 | 7 |
| page at offset 100,000 | 20–26 | 66 | 1,207 (wrong) | 123 | 122 |

PostGIS's range is over the three runs it took part in. MongoDB's `2dsphere` whole-region count took 1,747 ms
and its `2dsphere` pentagon 196 ms.

## What each would take, and what it would give

- **MongoDB.** Slower than PostGIS on every question but the distance, 15× on a wide box. The geometry model is
  GeoJSON with spherical edges, so a provider is a translation layer with a planar index beside the spherical one,
  and its writes would be documents without a schema the FeatureServer can describe ahead of time. ArcGIS Pro does
  not connect to MongoDB. What it would give: serving data an organisation already keeps there.
- **Redis.** Fast where an index answers a count, and the silent partial answer disqualifies it as a store of
  record without `ON_TIMEOUT fail`. It is a cache that has learned to search, and Graticula's caches are its own.
  Not a candidate for a provider.
- **CockroachDB.** The only one whose answers were all PostGIS's — it speaks PostGIS's SQL and wire protocol — and
  2–11× slower on one node. Registered through the existing PostGIS provider on dev, it was diagnosed correctly
  (*"PostGIS is not installed in this database"*, `canPublish: false`, server version 13.0.0): the provider relies on
  `postgis_version()` and functions Cockroach lacks. A dialect of the PostGIS provider is the shape a provider would
  take, as MariaDB is MySQL's. What it would give: a distributed SQL store, which the scale target (100–1,000
  services) does not need.
- **OpenSearch** (and Elasticsearch, whose query language it shares). Counts, aggregations and distance in single
  milliseconds — 8–10× faster than PostGIS — and slow to return documents: 465 ms for the pentagon's ids. Deep
  paging needs `max_result_window` raised or `search_after`, which `resultOffset` does not map to directly. ArcGIS Pro
  lists Elasticsearch among its database connections (ADR-208 §4). What it would give: statistics and counts over
  large point sets, where PostGIS is slowest.

## Found on the way

- **The admin API publishes from a source its own probe refused.** `POST /admin/datasources` answered
  `canPublish: false` for CockroachDB, and `POST /admin/layers` then published `public.places` from it, counted its
  200,000 geometries as valid, and every query of the layer answered 500. The console's dialog checks `canPublish`;
  the API does not. [D-311](../architecture-debt.md).

## Not tried

- **SQL Server** — no arm64 image runs on the VPS (ADR-205 §4).
- **SQLite / SpatiaLite / GeoPackage** — a file, not a server. A GeoPackage is already uploaded and copied into the
  datastore as hosted data (ADR-120); reading one in place, as GeoParquet is, would be a file-format question for the
  DuckDB path rather than a database to register.
- **Cassandra, Couchbase, Neo4j** — no spatial query beyond a radius, or none at all.
