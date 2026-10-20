# Registered engines against PostGIS — the same queries, timed (2026-10-20/21)

The owner asked whether MariaDB answers as fast as PostGIS, and then whether Oracle, with a spatial index of its own,
should not be as fast too. This is what was measured, what was slow and why, and what was changed. It is evidence for
[ADR-205](../adr/ADR-205-a-registered-oracle-database-is-served-like-postgis.md),
[ADR-208](../adr/ADR-208-a-registered-mysql-database-is-served-like-postgis.md) and
[ADR-209](../adr/ADR-209-mariadb-is-a-dialect-of-the-mysql-provider.md), not a decision.

## Method

- **Data.** 200,000 points with three attributes (`ad`, `tur`, `nufus`) in a box over Turkey (26–45 E, 36–42 N),
  seeded, one CSV (sha256 prefix `1d6df9807e72ea06`) loaded into each engine: PostGIS 3 (`gis` database, schema
  `zz_bench`, GiST), MariaDB 11.8 (`SPATIAL` index, SRID 4326), Oracle AI Database 26ai Free 23.26.3
  (`spatial_index_v2`, `LAYER_GTYPE=POINT`, SRID 4326, which Oracle indexes **geodetically**).
- **Where.** All three databases and the server run on the VPS (arm64, 4 cores); the client is a script on the same
  machine calling the dev server's REST face (`/FeatureServer/{n}/query`), so only the server sits between the client
  and the databases. Every answer is first checked against PostGIS's — counts, sorted id lists, attributes — and the
  run stops on a difference or a non-200; every run below agreed on every query.
- **Timing.** Per query, 5 warm-ups then 40 runs, engines alternating and the order reversed every other run; median
  and 95th percentile of the whole request in milliseconds.
- **Not a benchmark of the databases.** It times Graticula reading each through its provider, which is what a user
  waits for. One machine, one dataset, points only; polygons and lines were not measured.

## Result, after the changes below

| Query | PostGIS | MariaDB | Oracle |
|---|---:|---:|---:|
| count all | 41 | 60 | 33 |
| count where `tur = 'city'` | 64 | 69 | 38 |
| 1×1 degree box, features (1,768) | 45 | 51 | 75 |
| whole region box, count | 78 | 144 | 97 |
| pentagon intersects, ids | 106 | 122 | 219 |
| count and avg by `tur` | 100 | 207 | 78 |
| one feature by id | 25 | 28 | 28 |
| top 100 by `nufus` | 97 | 94 | 109 |
| within 50 km of a point, count | 90 | 33 | 54 |
| page at offset 100,000 (1,000 rows) | 76 | 90 | 122 |

Median ms, 2026-10-21, `spatial-pushdown` at `445590eb`.

**Every engine is within about twice PostGIS on every query**, and faster on some. Oracle's remaining gap is the
pentagon: 20,092 candidates read out of Oracle as coordinates and tested here, because the exact test of a shape
that is not a box is this process's (ADR-205 §5a).

## What was slow, and why

Before 2026-10-20 the providers answered everything spatial the way the general path does: the index's candidates,
each converted to WKB, read here and tested. For points that is most of the cost.

| Found | Cost before | Change | After |
|---|---|---|---|
| A box against points was refined here, every candidate read as WKB | Oracle 5 s for a box holding 200,000 | MySQL, MariaDB and Oracle decide a box against points in the database, as a comparison of coordinates | 97 ms |
| Oracle converts the shape (`SDO_UTIL.TO_WKBGEOMETRY`) for every row it sorts, before the offset and fetch take the page | 28 s for the first thousand, 23 s at offset 100,000 | the page is chosen first; shapes are read for its rows only | 122 ms |
| `SDO_GEOM.SDO_MIN_MBR_ORDINATE` for a point's numbers | 177 s for 200,000 | read `SDO_POINT.X/Y`, the minimum-MBR call only where a point is kept in its ordinate array | 0.43 s, then dropped from the hot path |
| ODP.NET fetched each WKB `BLOB` in a round trip of its own | 28 s for 1,421 shapes over the test tunnel | `InitialLOBFetchSize = -1` | inline |
| The second pass matched ids with `in (select … json_table)` | 330 ms a page | the id list drives a join | 52 ms |
| A second pass at all, for a point layer | 78 ms for one feature (28 ms in one statement) | points' x and y are read with the attributes | 28 ms |
| Oracle's geodetic `SDO_FILTER` costs ~8.5 µs a hit | 230–420 ms for a box holding all 200,000; 210 ms for the pentagon's box (24,596) | a box over a twentieth or more of the layer's extent is compared on its own, which is exact, instead of asked of the index | 97 ms; pentagon 304 → 219 ms |
| A point within a distance of a point was refined as geometry | — | measured here on the ellipsoid (Vincenty, WGS84), under 1 cm from PostGIS `geography` | MariaDB 33 ms, Oracle 54 ms |
| MySQL `NULLS LAST` emulation on every sort column | MariaDB's deep page slowed | written only for a column that can hold a null | 90 ms |

Each change kept the answers PostGIS gives — the parity check above ran after every one, and each provider's own
suite (Oracle 100, MariaDB 66) passed.

## What is not known

- **A projected Oracle index.** The twentieth-of-the-extent threshold was set where a *geodetic* index and a scan
  cost the same on this data. A projected index answers far more cheaply per hit and its crossover is higher; it was
  not measured, so the same threshold applies, which may scan where the index would have been faster.
- **Larger tables.** The threshold is a share of the extent, standing in for a share of rows; it assumes points are
  spread over the extent. Clustered data with an outlier breaks that in the slow direction only.
- **Polygons and lines.** None of the point paths apply; their cost is still the WKB read and the test here.
- **PostGIS's distance query** (90 ms) is the slowest of the three because it measures `geography` on every
  candidate in the widened box; the two others measure here. Not changed.

## Environment notes

- On the VPS's Oracle, the automatic **SQL Tuning Advisor** task re-ran the old 23 s statements in the maintenance
  window during measurement; it was disabled in that test instance
  (`dbms_auto_task_admin.disable('sql tuning advisor')`). A customer's Oracle is not touched.
