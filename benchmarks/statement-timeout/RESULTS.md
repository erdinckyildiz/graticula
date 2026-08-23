# D-08 — what the statement timeout is bounding, counted rather than guessed

**Run 2026-08-23.** PostgreSQL 17 in `gis-experiment-postgis`, one Graticula worker,
the development catalogue plus one table built for this: `hosted.d08_bench`,
**1,000,000 random points in EPSG:4326 with a GiST index**, because nothing in the
existing corpus is large enough to make a 30-second ceiling falsifiable.

[D-08](../../docs/architecture-debt.md) says the timeout *is 30 seconds because a
number was needed*, and names the trade: **too low truncates legitimate large extents;
too high lets one query hold a pooled connection while every layer sharing that data
source waits.** [Performance gate 2](../../docs/reviews/performance-gate-2.md) F3 keeps
that gate at FAIL for this and nothing else.

## What was measured

Two levels, because they answer different halves.

**Requests, sequentially, median of five after one warm-up.** The question is *what does
one honest request cost*, not *what happens under load* — load is
[ADR-046](../../docs/adr/ADR-046-admission-control-bounds-the-queue-not-the-wait.md)'s
question and has its own measurement.

| Request | Median | Worst | Bytes |
|---|---:|---:|---:|
| ArcGIS `query`, 1,000 lines with geometry | 28.6 ms | 39.5 ms | 657 kB |
| ArcGIS `query`, 1,000 polygons with geometry | 31.4 ms | 35.3 ms | 841 kB |
| ArcGIS `returnCountOnly` over 46,041 | 18.6 ms | 19.8 ms | 15 B |
| ArcGIS `returnExtentOnly` over 46,041 | 30.9 ms | 34.2 ms | 137 B |
| WFS `GetFeature`, 1,000 lines | 35.7 ms | 38.5 ms | 850 kB |
| WFS `GetFeature`, whole 25,280-polygon layer | 582.4 ms | 747.5 ms | 29.0 MB |
| OGC API Features `items`, 1,000 lines | 30.1 ms | 38.9 ms | 658 kB |
| WMS `GetMap` 1024², 46,041 lines | 414.0 ms | 463.8 ms | 272 kB |
| WMS `GetMap` 4096², 46,041 lines | 1,241.8 ms | 1,282.0 ms | 1.4 MB |
| MapServer `export` 4000×3000, 25,280 polygons | 1,340.1 ms | 1,446.0 ms | 2.5 MB |
| VectorTile z8 | 15.4 ms | 16.9 ms | 33 kB |

**Statements, forced to completion with `COPY … TO '/dev/null'`.** The first attempt used
`SELECT count(*)` over a subquery and reported 1,000,000 rows read in 33 ms, which is not
a fast database — it is PostgreSQL eliding a projection nothing consumes. `COPY` makes
every row cross the wire.

| Statement, over 1,000,000 rows | Time |
|---|---:|
| `count(*)` | 26.9 ms |
| `ST_Extent(geom)` | 70.2 ms |
| 50,000 rows with geometry — `FeatureQuery.MaximumLimit`, the absolute page ceiling | 26.5 ms |
| **all 1,000,000 rows with geometry** — the shape a full-extent render issues | **329.7 ms** |
| all 1,000,000 rows reprojected to 3857 | 415.3 ms |
| `count(*)` over a full-extent `ST_Intersects` | 304.0 ms |
| the whole 25,280-polygon layer's geometry | 74.1 ms |
| `like '%999%'` — an unindexed scan a caller can ask for | 28.2 ms |
| `ORDER BY name LIMIT 50000` — an unindexed sort a caller can ask for | 220.6 ms |
| a geodesic buffer over every row | 9,279.4 ms |
| a self-join on `ST_DWithin`, `LIMIT 100` | 5,817.2 ms |

## The finding

**Nothing this server can be made to issue comes near thirty seconds.** The most
expensive statement reachable through any face, over a million rows, is a full-extent
render read at **330 ms** — ninety times inside the ceiling. The most expensive one a
*caller* can steer is an unindexed sort at **221 ms**.

**The two statements that do approach the ceiling are not ones this server can issue**,
and that is the finding rather than an aside. A geodesic buffer over every row takes 9.3
seconds and a spatial self-join takes 5.8; **the query model has no way to write either.**
Checked against the running server rather than read out of the grammar:

| `where` | Answer |
|---|---|
| `lower(il) = 'x'` | refused — *'lower' is not a field of this layer* |
| `il in (select il from x)` | refused — *expected a value at position 7* |
| `il = il` | refused — *expected a value at position 5* |
| `il like '%9%'` | accepted |
| `il = 'Ankara'` | accepted — 248 |

`AttributePredicate`'s own remark says what is absent: *arithmetic, function calls,
column-to-column comparison and subqueries*. **That was written as a safety property and
this is the first time it has been measured as a performance one.** The thing bounding a
caller's statement cost is the grammar, not the timeout.

**So the timeout is not protecting against what D-08's sentence implies.** It cannot fire
on a legitimate large extent — 330 ms is not 30 s — and it cannot fire on a caller's
filter, because the filter cannot be made expensive. What is left for it to bound is a
pathological plan and a lock wait: a DBA's `ALTER TABLE`, a checkpoint, a table that has
lost its statistics.

## What this cannot say

**A table a hundred times this one.** The scale target is 100–1,000 *services*
([CLAUDE.md §7](../../CLAUDE.md)), and nothing says how large one layer may be. At the
measured rate a full-extent render read reaches 30 seconds at roughly **90 million rows**
— an extrapolation from one point, on one machine, with points rather than polygons, and
it is written here as an extrapolation.

**A lock wait**, which is the case the number now exists for and the one this measurement
did not construct. ADR-007 §4.8's quiesce — *drain its connections, hold its requests, let
the DBA work, resume* — is the mechanism that would make the timeout's behaviour under DDL
a choice rather than an accident, and it is still absent.

**Concurrency.** Every number here is one request at a time. Under load the same statement
costs more, and how much more is ADR-046's measurement rather than this one.
