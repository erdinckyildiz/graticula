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

**Amended 2026-08-24.** That gate's carried F3 says the statement timeout *"was not
re-tested; it needs a deliberately slow query and was judged outside a bounded-load
gate"*. Round 2 below is that deliberately slow query, and it is a lock rather than a
plan — which is why it did not belong in a load gate and does belong here. D-08 is
closed on the strength of it. Whether the Performance gate's own verdict moves is the
gate's to say and not this document's: it is re-run, not amended in passing.

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

## Round 2, 2026-08-24 — the two things round 1 could not say

Both are measured at the store rather than through a face, deliberately:
`statement_timeout` is a PostgreSQL setting and it is the store's clock that runs out.
What a face adds — encode, serialise, TLS — is already decomposed in
[benchmarks/feature-query](../feature-query/RESULTS.md), and adding it here would put two
questions in one number.

### A table a hundred times this one: linear, and the extrapolation was sound

Round 1 measured one size and extrapolated. One point cannot tell a linear cost from a
superlinear one, and the difference decides whether the ceiling arrives at ninety million
rows or at nine. So the same read — every geometry in the layer's own extent, as
`ST_AsBinary`, which is what a renderer receives — was measured at three sizes, each with
a GiST index and fresh statistics, best of three by `EXPLAIN ANALYZE` execution time:

| Rows | Full-extent render read | Ratio to previous | `count(*)` |
|---|---|---|---|
| 1,000,000 | **250 ms** | — | 24 ms |
| 4,000,000 | **1,024 ms** | 4.10× for 4× the rows | 87 ms |
| 16,000,000 | **3,832 ms** | 3.74× for 4× the rows | 356 ms |

**Linear across a sixteen-fold range, and very slightly sublinear at the top.** So the
ceiling is reached at **roughly 120 million rows** extrapolating from either end — 30 s /
250 ms × 1 M gives 120 M, and 30 s / 3,832 ms × 16 M gives 125 M. Round 1's figure was
90 million from a 330 ms measurement through the HTTP face; this series is store-side and
faster per row, and the two agree on the shape, which is what was in question. **The
extrapolation is now a slope rather than a guess**, and the answer is that no layer this
product is likely to hold reaches the ceiling by size alone.

### A lock wait: constructed, and the ceiling holds to the second

Two sessions. One takes `ACCESS EXCLUSIVE` on the table and holds it past the budget; the
other reads and is timed.

| `statement_timeout` | `lock_timeout` | Blocked for | Ended by |
|---|---|---|---|
| 3 s | unset | **3.31 s** | `canceling statement due to statement timeout` |
| **30 s** — the configured ceiling | unset | **30.30 s** | `canceling statement due to statement timeout` |
| 30 s | 2 s | **2.30 s** | `canceling statement due to lock timeout` |
| — | — | 0.296 s | completed, unblocked control |

**Three findings.**

**The number does the job it now exists for.** `statement_timeout` fires on a lock wait
— this was not obvious, because a blocked statement is not running — and it fires at the
budget, to within 300 ms of it at both 3 s and 30 s.

**And the cost is exactly D-08's stated one.** The read that takes 296 ms unblocked
occupies its pooled connection for **30.30 seconds**, a hundredfold, while every layer
sharing that data source waits behind it. That is the sentence D-08 was opened on, now
with a number under it.

**`lock_timeout` separates the two cases and nothing sets it.** With it at 2 s the wait
ends in 2.30 s with a **different SQLSTATE** — `55P03` rather than `57014` — which is the
distinction *waiting for somebody* versus *running too long*. `LayerConnections`
preserves whatever `Options` an operator set, so a deployment can set it today. **Doing so
is not yet advisable**: `55P03` reaches `ErrorResponse` with no arm of its own and is
answered *a database this server depends on is unreachable*, which is
[D-150](../../docs/architecture-debt.md).

ADR-007 §4.8's quiesce — *drain its connections, hold its requests, let the DBA work,
resume* — is still the mechanism that would make the behaviour under DDL a choice rather
than an accident, and it is still absent. It is tracked as an obligation in
[architecture-completeness.md](../../docs/architecture-completeness.md) rather than here:
this benchmark's question was what the number bounds, and the answer is that it bounds
this, for thirty seconds, at the price above.

## What this still cannot say

**Concurrency.** Every number here is one request at a time. Under load the same statement
costs more, and how much more is ADR-046's measurement rather than this one.
