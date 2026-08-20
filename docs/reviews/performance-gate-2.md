# Performance gate 2 — the admission control protects the wrong surface

**Run 2026-08-20** by an independent reviewer that did not write the code, per §67.
Scope: the five faces added on 2026-08-19 and 2026-08-20 under load, and a re-check of
every finding in [performance-gate-1](performance-gate-1.md).

**Against the running server**, 16 cores, PostGIS on loopback, 14 registered layers,
server GC. Nothing in the repository was modified by the reviewer and the server was
never stopped, built or restarted.

## Result

**FAIL, on one finding, and it is a claim about behaviour rather than about speed.**
The connection budget that [ADR-007](../adr/ADR-007-service-runtime.md) §4.8 exists
to provide — admission control that turns overload into a fast, clear refusal — works
cleanly for map rendering and does nothing at all for feature queries.

**The gate did not find the server slow.** It found the load-shedding pointed at the
rare surface and absent from the common one, which is worse than slow because a client
cannot see it happening.

---

## F1. The budget protects rendering and does not protect queries — HIGH

`ConnectionBudget` refuses with a 503 when a data source already has
`PerSourceConcurrency` (24) requests in flight and the caller has waited five seconds.
`LayerConnections.SourceFor` wraps every source identically, so every face should
inherit it. Measured against `hosted/tr_ilce` — 25,280 polygons on `datastore`:

| Request | Concurrency | 503s |
|---|---|---|
| ArcGIS `MapServer/export`, 1024² PNG | 16 / 24 / 30 / 40 | 0% / 0% / **13%** / **35%** |
| WMS `GetMap`, same extent | 24 / 32 | 0% / **12.5%** |
| ArcGIS `query`, full table, full precision | 60 / 100 / 150 | **0% at every level** |
| WFS `GetFeature`, full table GML | 40 | **0%** |
| 50 concurrent reads, client throttled to hold each ≥15 s | 50 | **0%** |

**The last row is the one that settles it.** The connections were held open for 23
seconds — more than four times the five-second admission window, at twice the
configured budget — and not one caller was refused.

**Reproduced independently before this was written down.** Sixty concurrent
full-precision full-table queries: **60 × 200, zero 503, p50 12.4 s** against **0.67 s
for the same request alone**. An 18× collapse with no status code anywhere in it. The
render path, probed at the same time, refuses correctly and names its own setting in
the refusal.

**Why, and this half is inference rather than measurement.** The budget's timer bounds
the wait *for a slot*, not the time a request holds one after it is admitted. A render
costs about 4 s of its own at concurrency 16, so competitors routinely wait past five
seconds and are refused. A feature query is fast *per request* even while the aggregate
degrades catastrophically, so a slot keeps freeing inside the window and nobody ever
waits long enough to be turned away. `pg_stat_activity` sampled during the render flood
showed a peak of **4 active backends**, so the bottleneck on that path is not the
database.

**What it means for a deployment.** An operator who reads §4.8 and expects load
shedding under a query flood gets none on the surface WFS, QGIS and ArcGIS clients
actually hammer while panning. Everybody just gets slower together, with no signal to
back off. The rendering surface, which is comparatively rare, is the one that gets
clean refusals. **That is backwards from what the ADR describes.**

**Not repaired**, and deliberately: a hold-time bound is a concurrency design change
with its own failure modes — a request killed mid-stream is a truncated document — and
it belongs in an ADR rather than in a gate's same-day patch.
[D-128](../architecture-debt.md), and it confirms the gap
[benchmarks/feature-query/RESULTS.md](../../benchmarks/feature-query/RESULTS.md) §4
left open in as many words: *the connection budget needs the same harness under
concurrency, so it stays open*. This is that harness.

## F2. The refusal promised a `Retry-After` and never sent one — MEDIUM, repaired

`ConnectionBudgetFullException`'s own remark says the refusal comes *"with a
`Retry-After`, which is the retry signal ADR-007 §4.9 asks admission control to send"*.
The reviewer read the headers off a live 503:

```
Connection, Content-Type, Date, Cache-Control, Expires, Pragma, Transfer-Encoding,
X-Content-Type-Options, Referrer-Policy, X-Frame-Options, Content-Security-Policy,
Strict-Transport-Security
```

No `Retry-After`. `ErrorResponse.Classify` mapped the exception to a status and a
sentence and nothing in the writing path ever set a header. **A documented contract
that no code implemented** — the same shape as the security gate's finding, where
`Authentication`'s remark named a redaction that had not been written.

**Repaired the same day.** The header is set for `ConnectionBudgetFullException` and
for nothing else: five seconds, the budget's own wait window, because a caller is
refused only *after* the server has already waited that long, so a smaller hint would
send them back into a queue that has not moved. **The other 503s deliberately keep
none** — an unreachable database and an out-of-memory geometry request produce the same
outcome on retry, and `GeometryServerEndpoints` already says so in its own remarks.
Verified on a live refusal: `Retry-After: 5`.

## F3. D-118's open question, answered with a number — MEDIUM

[D-118](../architecture-debt.md) records that every `GetFeature` counts the whole
result before writing a page of it, and asks whether that is 5% or 50%. **Both, and it
depends entirely on the page size.** Median of 15 runs, `resultType=hits` against
`count=N`:

| Layer | hits | count=10 | count=1000 | full |
|---|---:|---:|---:|---:|
| `tr_yol` (46,041 lines) | 10.5 ms | 19.6 ms | 33.0 ms | 393.2 ms |
| `tr_ilce` (25,280 polygons) | 10.0 ms | 11.6 ms | 35.4 ms | 490.6 ms |

The count is a near-constant **~10 ms regardless of table size** — an unfiltered
`COUNT(*)` over 46,041 rows costs about what it costs over 25,280. As a share of the
request it is **50–90% of a small page**, **~30% of the default 1,000**, and **2–3% of
a full dump**.

**This supports the debt's existing disposition rather than overturning it.** Buffering
the page to avoid the count trades a bounded 10 ms for the allocation risk A-037 ruled
out. But the 50–90% figure on small pages is the shape a panning WFS client actually
sends, and D-118 had only guessed at it. **ArcGIS `query` does not pay this at all** —
it uses `exceededTransferLimit` instead of a count — so it is a WFS and OGC API
Features cost, not a shared one.

## F4. At full precision the map's advantage over JSON narrows to parity — a nuance, not a contradiction

[benchmarks/map-rendering/RESULTS.md](../../benchmarks/map-rendering/RESULTS.md) found
a map cheaper than the JSON of the same features, **at matched simplification
tolerance**. Extended here at full precision, `tr_ilce`, median of 8:

| Face | ms | bytes |
|---|---:|---:|
| WFS GML, full | 663.3 | 29.0 MB |
| WFS GeoJSON, full | 597.2 | 26.5 MB |
| ArcGIS `query` JSON, full | 647.5 | 25.5 MB |
| WMS `GetMap` 1024², whole extent | 683.0 | 538 KB |
| ArcGIS `MapServer/export`, same | 694.9 | 538 KB |

**Roughly at parity — 597 to 695 ms across all five — for 47–54× fewer bytes.** The
earlier benchmark's comparison depended on matching tolerance, and nothing on the query
path can match it: there is no simplification parameter in
`FeatureServerQueryParameters`. A client asking for real full-precision geometry pays
about the same wall clock as a render and carries fifty times the payload.

**Correction to this finding, found while verifying it.** The gate wrote that *no
`maxAllowableOffset`/simplification parameter exists in `FeatureServerQueryParameters`*.
It does, it is parsed, and it works: the same full-table request with
`maxAllowableOffset=0.01` returns **4.8 MB instead of 26.2 MB**. So the query path
*can* match the map's tolerance, and the comparison the map-rendering benchmark makes
is available to a client rather than only to a benchmark. **What is true is the
measurement above** — at full precision, which is what a client gets by default, time
is at parity and bytes are not.

**And the reason the gate believed otherwise is itself a defect.** The parameter is
listed in `IgnoredParameters` with the reason *"geometry is returned ungeneralised"* —
a sentence that is logged to the operator on every request that uses it. **The server
was announcing that it had dropped a parameter it had honoured.** Two more entries in
the same table were false the same way: `token` said *authentication is by header*
while `?token=` authenticated on every route, and `datumTransformation` said *no
reprojection happens* while `outSR=3857` demonstrably reprojects. **All three
repaired**, and recorded as [D-129](../architecture-debt.md) because the class — a
declaration that rots while the code grows past it — is the same class as the security
gate's finding and F2 above, three times in two days.

**Stated carefully because the two are not the same request.** For a 1,000-feature page
the JSON paths cost 33–40 ms against 670–695 ms for a full-extent render — but the map
necessarily draws everything in the extent, so that comparison is not like for like
either. What is true in both directions is that the bytes differ by a factor of fifty
and the time does not.

## F5. Capabilities cost at the stated scale — extrapolated, and labelled as such

WMS describes **every** layer in `GetCapabilities` unconditionally. WFS is selective:
only 4326 layers get a full describe, explicitly to bound this. Measured on WMS with 14
layers, cold after 32 s of silence against warm:

| | time |
|---|---:|
| Cold | 63.2 ms |
| Warm | 16.5 ms |
| Marginal per-layer describe | ≈ 3.3 ms — in line with D-17's independent 4–6 ms |

**Extrapolated linearly, which is an extrapolation and not a measurement:**

| Services | Cold | Warm |
|---:|---:|---:|
| 14 (today) | 63 ms | 17 ms |
| 100 | ≈ 346 ms | ≈ 137 ms |
| 1,000 | ≈ 3.3 s | ≈ 1.2 s |

**Cold is the realistic case, not the worst one.** The describe cache holds for 30
seconds and a capabilities document is what a client fetches once at connect time, far
less often than every 30 seconds. At the top of the owner's stated 100–1,000 range that
is a ~3.3-second capabilities fetch per fresh client connection, on the one face that
describes unconditionally. `/rest/services/{folder}` stayed at 12–19 ms throughout,
confirming the cost is the per-layer describe and not the directory walk.

## F6. Labels are cheap at n=12, and that does not answer the question

`hosted/look_parcels` — 12 polygons with a stored label style — at 1024²:

| conc | p50 | p95 | alloc | GC pause |
|---:|---:|---:|---:|---:|
| 1 | 63.6 ms | 69.4 ms | 0.4 MB | 0% |
| 8 | 73.0 ms | 91.0 ms | 3.2 MB | 0% |
| 16 | 93.9 ms | 111.5 ms | 5.5 MB | 0% |
| 32 | 176.2 ms | 257.1 ms | 10.9 MB | 0% |

A genuine positive result, and it says nothing about a layer with thousands of labelled
features, because no such layer exists in this dataset. **The gap
`map-rendering/RESULTS.md` named stands, narrower and still open.**

---

## Re-checking performance gate 1

- **F1 (the feature-query path was never measured) — RESOLVED**, recorded as D-30
  `PARTLY PAID`. `benchmarks/feature-query/RESULTS.md` run 5 instruments it in-process.
  **The one piece it explicitly left open — the budget under concurrency — is this
  gate's F1**, and the answer is a real gap rather than the clean pass the rest of that
  benchmark found.
- **F2 (the evidence base is smaller than it looks)** — a bookkeeping finding about
  historical rounds, not a live-server property. Not re-checked, by design.
- **F3 (governing numbers unmeasured)** — the connection budget's *numbers* were
  measured on 2026-08-19 (Q-04) and its *enforcement* is measured here, which turns out
  to be uneven across faces rather than simply undischarged. The statement timeout
  (D-08) was not re-tested; it needs a deliberately slow query and was judged outside a
  bounded-load gate.
- **F4 (the harness was wrong three times)** — **the discipline held.** The first
  concurrency run produced a 13.8 s p50 that looked like the server falling over; a
  control against `pg_stat_activity` and a forced-hold probe showed the real mechanism
  instead. The client was not the artefact this time, and checking before believing is
  what turned *"the server collapses at concurrency 60"* into the more useful and more
  precise F1.

## What held

- **GC pause stays low on every query-path test at realistic concurrency**: 0.2–2.5% up
  to concurrency 32, matching `feature-query/RESULTS.md` §3b exactly. It reaches
  3.4–19.7% only under the deliberately extreme 60- and 100-way full-table floods, and
  even there it is a minority of wall time rather than A-037's 80.9% tile-path ceiling.
- **The map-rendering benchmark's core claim holds**: 64–278 ms across concurrency 1–32
  at 0% GC pause. Rendering is not the allocation risk ADR-004 §0 feared; it is the one
  surface where admission control works as designed.
- **Recovery is immediate.** The request after the heaviest 503-producing run succeeded
  in 665 ms. No lingering degradation, no leaked semaphore state.
- **Directory and folder listings stay cheap regardless of scale** — 12–19 ms — because
  they describe nothing.

## What this run did not measure

The statement timeout and its interaction with the budget; sustained load over minutes
rather than seconds, so F1 is about admission at burst arrival and not steady state; a
labelled layer large enough to stress placement; multi-node scaling; and the root cause
of F1, which is inferred from `pg_stat_activity` and code reading rather than from an
attached profiler. **Named rather than skipped**, and F1's causal half is flagged
medium confidence while its behavioural half — four independent methods agreeing, plus
one reproduction — is high.
