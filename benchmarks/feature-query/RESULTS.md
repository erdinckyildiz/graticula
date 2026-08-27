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

> **§3 was superseded within the day by §3b.** The throughput half was void when
> this was written; a C# generator replaced the Python one and the concurrency
> question now has an answer. §3 is kept because the void numbers are the reason
> the rest exists, and because §3c is about how nearly the same mistake was made
> twice more.

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

## 3b. Concurrency, measured — and the ceiling is not what four rounds predicted

§3's throughput table was void because the generator failed its own control.
`benchmarks/harness` now has a C# one (`GisBench queryload`) that speaks TLS to
the development certificate, signs in, and reads the server's own GC and CPU
counters from `/admin/health`, where they sit behind `admin:manageServer`
alongside everything else that route already redacts.

100 features per request, four seconds per level, after a warm-up at each level:

| conc | req/s | p50 ms | p95 ms | MB/s | alloc KB/req | gen2 | GC pause % | CPU cores |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 1 | 198 | 4.8 | 6.4 | 9.1 | 142 | 0 | **0.2%** | 0.78 of 16 |
| 2 | 369 | 5.3 | 6.4 | 17.0 | 157 | 0 | 0.8% | 0.86 |
| 4 | 641 | 6.0 | 8.3 | 29.4 | 156 | 0 | 2.5% | 2.14 |
| 8 | **942** | 8.1 | 12.1 | 43.2 | 164 | 1 | 2.2% | 3.60 |
| 16 | 792 | 19.0 | 33.2 | 36.4 | 156 | 1 | 2.0% | 3.54 |
| 24 | 753 | 29.6 | 52.8 | 34.6 | 159 | 1 | 1.7% | 3.25 |
| 32 | 772 | 39.3 | 61.3 | 35.4 | 158 | 1 | 1.6% | 3.11 |

### 3b.1 It is not allocation, and that is the headline

**GC pause is 0.2% at concurrency 1 and never exceeds 2.5%.** Allocation is
142–164 kB per request and **flat across concurrency** — eight times the load
does not change what a request allocates.

Set that beside what the four earlier rounds concluded and what this gate carried
forward: *the cost of this system is memory traffic and it is invisible at
concurrency 1*, from a tile round that measured **80.9% of wall time in GC pause
at 18% CPU**. F1's worry was that the feature path runs the same
WKB-into-an-object-graph mechanism and would behave the same way.

**On this shape of data it does not.** 2.5% against 80.9% is not a difference of
degree. A-037 is not refuted — it was measured on tiles, where a single z12 tile
allocated 404 MB — but **it does not transfer to the feature query path**, and
that transfer was the assumption D-30 was opened on.

### 3b.2 Nor is it CPU

At its peak the server uses **3.6 of 16 cores** and serves 942 requests per
second. Past that, throughput falls and latency grows linearly — the signature of
a queue, not of saturation. The machine has twelve cores idle at the point where
the server stops going faster.

### 3b.3 What the ceiling is, and the honest limit of this run

**The control saturates in the same place.** `/rest/info` reads nothing, touches
no database, and goes through the same TLS, the same middleware and the same
authentication. It peaks around 800 requests per second — and beyond concurrency
4 the feature query is at 83%, then 108–124%, of it.

So the query path is **not** the ceiling. Something shared by both is: the TLS
and HTTP pipeline, the authentication that runs on every request, or the
generator. This harness cannot separate those three, and saying which would need
a run against a second machine or a server with authentication disabled — neither
of which exists here.

**What can be said, and it is what F1 asked:** the feature query path scales
**4.75× from concurrency 1 to 8** and is not what stops scaling after that. It is
not allocation-bound, not GC-bound and not CPU-bound at any level measured.

### 3b.4 The control rule was wrong, and correcting it is part of the result

The first version of this harness **aborted** unless the control scaled fourfold,
on the reasoning that a generator which cannot outrun a do-nothing path is
measuring itself. It aborted — and the reason was not a weak generator. **That
rule conflated two different failures:** a generator too weak to load the server,
and a server that saturates early on every path including the cheap one. It
reported the second as the first, and would have thrown away a good measurement.

The control is a *ceiling to compare against*, not a threshold to pass. Every row
above is now printed with its share of the control at the same concurrency, and
the rows past 80% carry a line saying that nothing about the query engine can be
concluded from them.

---

## 3c. The fifth wrong harness was the measurement setup itself

**Before any of §3b could be measured, the server appeared to saturate at 130
requests per second on a path that reads nothing.** Latency stepped from 1.6 ms
to 7.4 ms after a few hundred requests and stayed there, identically for
anonymous and authenticated callers, with allocation unchanged and GC pause at
zero. Two independent client processes saw it at once, so it was not the client.
It recovered after twenty seconds idle.

It was **the console logger writing to a redirected file**. The server had been
started with its stdout piped to `trace.log` so the phase lines could be read
back — and .NET's console logger has a bounded queue whose default behaviour when
full is to block the producer, which is the request thread. Fast until the queue
fills, then pinned at the drain rate, then recovering when the load stops. Every
symptom.

Started without redirection, the same path holds **1.15–1.38 ms flat across 1,500
requests**.

**This was one command away from being written up as a server defect** — "the
server saturates at 130 rps on a no-op path" — with a plausible mechanism and a
reproducible measurement behind it. F4 says a harness in this project has been
wrong three times; four, with the corpus discovery; this is the fifth, and the
first where the instrument's *output channel* was the distortion.

**§2's phase numbers are not affected, and that was checked rather than
assumed.** `Log.QueryTimings` is called after the measurement window closes, so
blocking on the log queue lands outside it. Verified directly: 60 requests then
700 more, and the phases got *better* (total p50 4,238 µs → 2,954 µs, lookup
2,070 → 1,711), which is warm-up, not queue pressure. Had the queue been inside
the window they would have quadrupled.

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

- **Which of three shared costs is the ceiling.** §3b establishes that the query
  path is not it — the control saturates in the same place — but TLS, the
  per-request authentication and the generator itself cannot be separated from
  one machine with authentication on.
- **ADR-007 condition 3's connection budget.** It needs the same harness under
  concurrency, so it stays open.
- ~~**Allocation.** No allocation figure was taken.~~ **Taken in §3b**: 142–164 kB
  per request, flat across concurrency, GC pause never above 2.5%. Process-wide
  counters sampled either side of a run are attributable when the run is the only
  thing happening, which is the case here.
- **One machine, one shape of data.** `buildings` is small polygons. A layer of
  national outlines would move decode and serialise and might move the ranking.

---

## §4 — the decomposition under concurrency, 2026-08-27

[D-30](../../docs/architecture-debt.md)'s unpaid half. Every earlier run of this
decomposition was serial, and the row's trigger says so. `concurrency.py` drives the same
one-row query at five concurrencies and reads **the server's own per-request breakdown out
of its log** rather than timing from outside — because the question is *which component
grows*, and a wall clock answers a different one.

| callers | requests | wall p50 | in-handler total | lookup | prepare | driver | decode | serialise |
|---|---|---|---|---|---|---|---|---|
| 1 | 20 | 18.3 ms | 2.75 ms | **1.93** | 0.13 | 0.60 | 0.01 | 0.09 |
| 4 | 80 | 10.3 ms | 2.63 ms | **1.85** | 0.07 | 0.64 | 0.01 | 0.06 |
| 8 | 160 | 14.9 ms | 3.16 ms | **2.25** | 0.05 | 0.71 | 0.01 | 0.04 |
| 16 | 320 | 25.2 ms | 4.63 ms | **3.30** | 0.06 | 0.96 | 0.01 | 0.05 |
| 32 | 640 | 47.4 ms | 4.12 ms | **2.89** | 0.06 | 0.89 | 0.01 | 0.05 |

**Three findings, and the first reframes the other two.**

**(a) Most of what concurrency costs happens outside the handler.** Wall latency grows
2.6× from one caller to thirty-two — 18.3 ms to 47.4 ms — while the handler's own time
grows **1.5×**, 2.75 ms to 4.12 ms. So roughly four fifths of the added latency is queueing,
TLS and the thread pool rather than the query path. That is the same conclusion §3b reached
from the other direction, now with the inside of the request visible instead of inferred.

**(b) The per-request catalogue read is both the largest component and the one that
degrades.** `lookup` is 70% of the handler at one caller and 71% at sixteen, and it grows
from 1.93 ms to 3.30 ms while everything else is flat. [D-17](../../docs/architecture-debt.md)
put that read on every request deliberately — it carries the sharing scope and the
started/stopped status, and holding it would be caching an authorization decision — and
[Q-04](../connection-budget/RESULTS.md) found the platform store is the `+ 1` pool every
request touches whatever layer it asks for. This is those two facts meeting: the one pool
every request shares is where the contention lands.

**(c) The database query costs a third of the permission to run it.** `driver` is
0.60–0.96 ms against `lookup`'s 1.93–3.30. On a one-row query the server spends more time
finding out whether the caller may read the layer than reading it. That ratio is a property
of the *query size* rather than of the design — a thousand-row query moves `driver` and
`serialise` and leaves `lookup` where it is — but it is the ratio a catalogue browser, a
tile client or an ArcGIS Pro layer list actually meets, because those ask small questions
many times.

**What this does not settle.** One machine, one small-polygon layer, one row per query, and
the wall figures include a client that is also on it. The in-handler numbers are the
server's own and are not affected by that; the wall column is context rather than a result.

**The harness reports its own failures, and the first version did not.** That version
swallowed every exception and returned the elapsed time, so a run where nothing worked
reported *0.3 ms at every concurrency, no traced requests* — and the obvious reading of that
was *the query logger is not at Debug*, which was wrong. Every request had been refused
instantly by `urllib` because Git Bash rewrote the path argument `/rest/services/…` into
`/Program Files/Git/rest/services/…` before Python ever saw it. **A benchmark that cannot
say *nothing happened* reports a very fast nothing**, which looks like a result.
