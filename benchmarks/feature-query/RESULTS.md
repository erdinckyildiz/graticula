# Feature query — where the time goes

**Run 5. 2026-08-16.** Answers [D-30](../../docs/architecture-debt.md) and
finding F1 of [performance gate 1](../../docs/reviews/performance-gate-1.md).

Runs 1–4 measured tile generation and geometry overlay. The word *query* appears
in neither results document, and the FeatureServer query path is the most-used
surface in the product. A black-box probe run for the gate found throughput
plateauing at 5–7× for 24× the concurrency and **could not say why** — from
outside the process, an allocation ceiling, a connection pool limit and a
contended host look identical. The gate's own conclusion was that this needs
in-process instrumentation.

---

## 0. Read this first: half of this run is void

**The throughput half failed its own control and is reported as void.** F4's
rule, written into the gate after a harness in this project had been wrong three
times, is that no load figure is believed without a control run against a path
the server barely touches at the same concurrency. The control here —
`/rest/info`, which reads nothing — recorded **14 requests/second at concurrency
1 and 1/second at concurrency 4**. That is not a measurement of anything; the
Python threaded TLS client is the bottleneck and its variance swamps the signal.

The numbers are printed in §3 anyway, so that nobody re-runs the same harness
believing it is new evidence.

**The phase decomposition in §2 is not affected by this.** It comes from the
server's own clock, at concurrency 1, and it is the result this run exists for.

---

## 1. Method

The server records one line per feature query, decomposing it:

| Phase | What it is |
|---|---|
| **lookup** | The per-request catalogue read. A round trip to Postgres, deliberately not cached ([D-17](../../docs/architecture-debt.md)) — it carries the sharing scope and the started/stopped status |
| **prepare** | Authorization, the described shape (cached), parameter parsing |
| **driver** | Time inside Npgsql: executing the data query and every row wait. Wire and driver parsing included; this is *waiting*, not working |
| **decode** | WKB into an object graph and column values into a row |
| **serialise** | The remainder of the body — JSON writing and the flush to the socket. A remainder rather than a measurement, because timing it directly would mean a stopwatch pair per feature |

**It costs nothing when it is off.** Nothing is timed unless the `query` logger
is enabled for Debug: no trace object, no timestamps, and no branch in the row
loop that does any work. That is a condition of leaving an instrument in a hot
path, and this project has four wrong harnesses behind that rule.

Layer `buildings`, 40 repetitions per case after warm-up, p50. One machine, one
PostGIS 16-3.4 container on loopback.

---

## 2. Result — the phase decomposition

| Case | server total | lookup | prepare | driver | decode | serialise | out |
|---|---:|---:|---:|---:|---:|---:|---:|
| 1 feature | 2,560 µs | **1,802** | 30 | 688 | 7 | 34 | 1.0 kB |
| 100 features | 2,979 µs | **1,750** | 25 | 796 | 60 | 341 | 48 kB |
| 1,000 features | 6,316 µs | 1,920 | 34 | 1,009 | 558 | **2,691** | 410 kB |
| count only | — | — | — | — | — | — | not instrumented |

### 2.1 The catalogue read is the largest single cost, and it is a fixed one

**1.8 ms on every query, whatever the query is.** On a one-feature request it is
**70% of the server's time**; at a hundred features it is still 59%. It does not
move with row count because it has nothing to do with the rows.

**It is also 2.6× the cost of the data query it precedes** — 1,802 µs against
688 µs for the query that actually fetches the feature. Both are one round trip
to the same database on the same loopback, so the difference is the catalogue
query itself, which returns a service and its layers.

This is not a defect. [D-17](../../docs/architecture-debt.md) records the
per-request catalogue read as deliberate: it carries the sharing scope and the
started/stopped status, and those are not safe to remember. **What was missing
was the price**, and the price is 1.8 ms per request. Anybody proposing to cache
it is now arguing about a number rather than about a feeling, and anybody
defending it is defending a known cost.

### 2.2 At size, serialisation dominates — not the database

At 1,000 features: **serialise 43%, driver 16%, decode 9%**.

That is the answer F1 asked for and could not get from outside. The plateau is
not the database and not the connection pool: **the time is in this repository's
own code**, turning features into JSON and pushing them at the socket. A-037
predicted allocation would be the binding constraint on this path and nothing
had checked; this is the first evidence either way, and it points the same way.

**Decode is smaller than expected and that is worth saying.** Finding 10 of the
tile rounds measured "three to four copies of every coordinate" and D-30 warned
the feature path runs the same mechanism. It does — but at 6,970 vertices the
decode is 558 µs, a twelfth of the request. The copies are real; on this shape
of data they are not what binds.

### 2.3 What scales with what

| | 1 → 100 features | 100 → 1,000 features |
|---|---|---|
| rows | ×100 | ×10 |
| bytes out | ×47 | ×8.5 |
| **lookup** | ×0.97 — flat, as it should be | ×1.1 |
| **driver** | ×1.16 | ×1.27 |
| **decode** | ×8.6 | ×9.3 — linear in rows |
| **serialise** | ×10 | ×7.9 — linear in bytes |

**Driver time barely moves.** Ten times the rows costs 27% more time inside
Npgsql. Whatever binds this path, it is not the database doing more work.

### 2.4 `returnCountOnly` is not instrumented

It takes `AlternateShapeAsync` and never reaches the traced block. Its client-side
p50 was 5.35 ms, which is *slower* than fetching one feature — worth a look, and
this run cannot say why. Recorded as a gap rather than omitted.

---

## 3. The void half, printed so nobody repeats it

Requests per second, three-second windows:

| | 1 | 2 | 4 | 8 | 16 | 24 |
|---|---:|---:|---:|---:|---:|---:|
| 1 feature | 170 | 62 | 56 | 59 | 72 | 29 |
| 100 features | 4 | 3 | 9 | 40 | 70 | 77 |
| 1,000 features | 47 | 70 | 76 | 48 | 9 | 2 |
| **control `/rest/info`** | **14** | **20** | **1** | **4** | **4** | **6** |

The control does no work and should be the fastest row on the page. It is the
slowest. **Nothing in this table is evidence about the server.**

A load harness for this path needs to not be a Python thread pool doing TLS.
That is its own piece of work and it is recorded as still open.

---

## 3a. A follow-up, and a fix that could not be measured

§2.1 sent somebody looking at the catalogue read, and the look found something:
`FindServiceAsync` ran its group query with **no `where` clause**. Resolving one
service read every group layer in the catalogue and discarded all but one
service's. Correct, and O(all services) on the most-used path in the product
against a stated scale target of 100 to 1,000 services. It now filters to the
services the first query actually returned, and skips the query entirely when
there are none — a lookup that finds nothing used to cost two round trips.

**The fix made no measurable difference, and that is the honest report.**

| | run 5 | after the fix |
|---|---:|---:|
| lookup, 1 feature | 1,802 µs | 1,761 µs |
| lookup, 100 features | 1,750 µs | 1,824 µs |
| lookup, 1,000 features | 1,920 µs | 2,116 µs |

There are five group layers in this catalogue. Reading five rows instead of one
costs nothing anybody can time, and no improvement should have been expected.
**The evidence for the change is the query plan** — a sequential scan returning
every row became a filter on an indexed column returning one — not a stopwatch.

**Two things were learned about measuring on this machine.**

The first attempt to re-measure reported everything 2× worse, *including phases
the change cannot touch*: decode went from 558 µs to 6,743 µs. That was not the
fix. A stray `testhost` was holding the build's DLLs, and a container stack
belonging to another project was burning about 2.7 cores in a crash loop —
`docker stats` showed three containers at 110%, 98% and 61%, retrying against a
database and a message bus that had been down for three days. Host CPU was 58%.
With that stopped it was 19%, and the numbers above are from the quiet run.

The second is that **cross-run comparison on this machine is not reliable below
about a factor of two**, even quiet. Decode at 1,000 features was 558 µs in run 5
and 2,709 µs in the quiet re-run, with nothing between them that touches decode.
Any future claim of a small improvement here needs an A/B in one session, not two
runs on two days.

---

## 4. What this settles and what it does not

**Settled:**

- The feature query path is measured. D-30's central complaint is answered.
- Under no concurrency, the per-request catalogue read is the largest cost on
  small queries and serialisation is the largest on big ones. Neither is the
  database doing more work.
- The instrument is permanent, costs nothing when off, and any future run
  starts from `Logging__LogLevel__query=Debug` rather than from a new harness.

**Not settled, and named:**

- **Everything about concurrency.** §3 is void. The plateau the gate found is
  still unexplained; what has changed is that the tool to explain it now exists
  and only the load generator is missing.
- **ADR-007 condition 3's connection budget.** It needs the same harness under
  concurrency, so it stays open.
- **Allocation.** No allocation figure was taken. `GC.GetTotalAllocatedBytes`
  is process-wide and only attributable at concurrency 1, which is exactly the
  case where allocation pressure does not show.
- **One machine, one shape of data.** `buildings` is small polygons. A layer of
  national outlines would move decode and serialise and might move the ranking.
