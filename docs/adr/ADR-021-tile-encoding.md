# ADR-021 — Tiles are encoded by PostGIS, and we do not write an encoder

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `HIGH` for the decision · `MEDIUM` for the condition in §7 |
| **Decided** | 2026-08-14 |
| **Answers** | [Q-68](../open-questions.md) |
| **Reverses** | the tile-encoding half of [build-vs-adopt-policy.md](../build-vs-adopt-policy.md) Tier 1 |
| **Evidence** | [benchmarks/mvt-generation/RESULTS.md](../../benchmarks/mvt-generation/RESULTS.md) runs 1–4 |

---

> **Scope note, 2026-08-18 — v1 serves PostGIS only, and the other engines are
> deferred rather than cut.** This decision reasons about several database engines.
> Owner decision: *"Şimdilik postgis ile gideceğiz. Sonra diğer db'ler eklenecek. V1'de
> sadece Postgis olarak kalabiliriz."* — [v1-scope](../v1-scope.md) §3a, which is the one
> place that says what the deferral means.
>
> **The multi-engine reasoning here is kept on purpose**, because it is what the second
> engine will be built from and because deleting it would make it be re-derived later
> from nothing. What it is not is a description of what v1 does. Where a sentence below
> reads as *the server supports Oracle today*, it has been corrected; where it reads as
> *this is how several engines would be supported*, it stands and waits.
>
> [D-27](../architecture-debt.md).

## 1. Context

An in-process MVT encoder was written, measured over three benchmark rounds, and
found to be good: 3.8 ms to encode a dense tile, `RectClip` 63× faster than
`NTS.Intersection`, `TileSimplify` 47× faster than Douglas–Peucker on the stage.
[ADR-003](ADR-003-geometry-engine.md) Alternative B was validated by those
numbers rather than by preference.

**Then its reason for existing was removed.** The encoder was justified by
`ST_AsMVT` being absent from SQL Server and Oracle. [Q-67](../open-questions.md)
decided that vector tiles come only from hosted data; hosted data is PostGIS;
`ST_AsMVT` is always available. Q-68 recorded the one argument that survived and,
correctly, refused to settle it by argument:

> `ST_AsMVT` is one database round trip per tile, whereas reading once and
> encoding many tiles in process is only possible with our own encoder, and that
> is exactly what seeding does. **Decide by measuring.**

---

## 2. Decision

**Vector tiles are produced by `ST_AsMVTGeom` and `ST_AsMVT` in the datastore.
We do not write, promote or maintain an MVT encoder, a rectangle clipper or a
tile simplifier in `/src`.**

Run 4 measured the surviving argument and it did not survive:

| | `ST_AsMVT` | read-once-encode-many |
|---|---|---|
| round trips, z12→z16 | 512 | **2** — 256× fewer |
| median tiles/s, z12→z16 | **170.2** | 144.0 |
| per-round ratio, z12→z14 | — | 0.61 to 1.84, median ~1.15 |
| allocated per tile | **0.02–0.15 MB** | 4.90–18.64 MB |
| GC pause at concurrency 4 | **0.0%** | **35.5%** |

**Round-trip count was the whole case.** It is real and structural — 256× fewer
is not an estimate. It buys nothing measurable, because a round trip to a local
PostgreSQL is not what a tile costs. What the in-process path costs instead is
124–245× the allocation, and a GC pause that climbs with concurrency while
`ST_AsMVT`'s does not.

That is A-037 — *allocation rate, not CPU, sets the ceiling* — appearing in the
one workload that was supposed to be the encoder's best case.

---

## 3. What this does not throw away

**The measurements stand and they were not wasted.** Runs 1–3 established four
things that are now load-bearing elsewhere:

- **A-004 validated**: general-purpose overlay on a hot path is 79% of a request.
  That is why [ADR-003](ADR-003-geometry-engine.md) Alternative B holds, and it
  is a fact about the feature path as much as the tile path.
- **A-021 promoted from tuning to structural**: without pushdown, a z16 tile
  reads 201,580 vertices to emit 2,080. [ADR-008](ADR-008-query-engine.md)'s
  pushdown design is not an optimisation.
- **A-037 validated**: the ceiling is allocation. [ADR-007](ADR-007-service-runtime.md)
  sizes workers against CPU and a context budget, and neither is the binding
  constraint on geometry-heavy work.
- **Finding 11**: a tile's cost floor is set by the largest geometry overlapping
  it — four administrative polygons are 90% of everything read for a tile
  showing one city block. That is engine-independent and it shapes the feature
  path too.

**And the cheapest possible moment to learn this.** `RectClip`, `TileSimplify`
and `MvtEncoder` live in `/benchmarks`, which [CLAUDE.md](../../CLAUDE.md) §1
says is never promoted. Retiring them costs no production code, because none was
written. The rule that experiments are rewritten fresh rather than promoted is
what made this reversible.

---

## 4. The policy this contradicts, stated rather than skirted

[build-vs-adopt-policy.md](../build-vs-adopt-policy.md) puts the **tiling
pipeline** in Tier 1 — *written by us, always*. This decision moves its encode
stage into PostgreSQL.

**Why that is not the policy being quietly abandoned:**

- `ST_AsMVT` is not a library and not a Tier 3 product. It is a capability of
  the datastore, reached the same way [ADR-008](ADR-008-query-engine.md) reaches
  `ST_Intersects` and `ST_ClipByBox2D`. Pushdown *is* the query-engine design;
  this is that design followed to its conclusion for one output format.
- The datastore is ours. [ADR-019](ADR-019-portal-server-split.md) fused it into
  the product and [Q-69](../open-questions.md) made it mandatory. This is not a
  dependency on a database somebody else chose.
- **Most of the tiling pipeline stays Tier 1 and unaffected**: tile addressing
  and the pyramid, the cache and its invalidation ([ADR-010](ADR-010-caching.md)),
  seeding, the service model, the VectorTileServer metadata and style documents,
  and the layer definition. What moves is one stage — turning geometry into
  bytes in a documented format.

**Why it is still a real narrowing, and should be read as one:** we can no longer
serve a vector tile from anything that is not PostGIS without writing the
encoder after all. Q-67 already decided we will not, so the cost is currently
zero — but the cost of *reversing* Q-67 has gone up, and this ADR is part of why.

---

## 5. Consequences

- **VectorTileServer is a much smaller piece of work than planned**, and it is
  now mostly catalogue, cache and metadata rather than geometry. *(Built
  2026-08-14: the service document, the style, and the tile endpoint. The
  provider is 140 lines and parses no geometry.)*
- **The tile cache becomes more important, not less.** With encoding in the
  database, every cache miss is datastore load — see §7.
- **`/benchmarks/harness` stays exactly where it is**, as the evidence for this
  decision. It is not deleted; a retired implementation with numbers attached is
  how the next person avoids rewriting it.
- **Q-66 loses its urgency.** *Does the provider interface hand back geometry or
  coordinates on the tile path?* — there is no in-process tile path. The question
  survives for the feature path, where finding 11 still applies, and should be
  re-scoped rather than closed.
- **ADR-003's boundary is unchanged.** Own hot-path primitives, adopted topology.
  There is simply no tile hot path in our process any more.

---

## 6. Alternatives rejected

| | Why not |
|---|---|
| **Keep the encoder for seeding only** | Its case was seeding, and run 4 measured seeding. Two code paths for one output, one of them slower and 245× more allocating, maintained for an advantage that did not appear |
| **Keep it behind a flag** | A flag is a decision deferred, and this one has an answer. An unflagged path nobody runs rots; a flagged path nobody runs rots and is also load-bearing the day somebody flips it |
| **Delete the benchmark harness** | The numbers are the reason for the decision. Deleting them leaves a decision with no evidence, which is how it gets re-litigated in six months |
| **Reverse Q-67 to preserve the encoder** | Backwards. The encoder existed to serve a decision, not the other way round |

---

## 7. Conditions

1. **The datastore-saturation case is untested and is the strongest surviving
   argument against this decision.** `ST_AsMVT` puts every byte of tile cost
   inside PostgreSQL. On the benchmark machine PostgreSQL had headroom, so the
   comparison never saw the case where it does not — and ADR-019 makes the
   datastore mandatory and *shared by every service*, so that case is not
   hypothetical. Moving encode to a tier that scales horizontally is an argument
   no single-box benchmark can refute. **Before this server is recommended for a
   deployment where the datastore is the constraint, measure tile throughput
   against a saturated datastore.** If the encoder wins there, this ADR is
   reopened and `/benchmarks/harness` is where the implementation starts.
2. **Measured on one machine, one dataset, one city, one polygon table.** No line
   layers, no mixed geometry, no attribute-heavy table where the tag dictionary
   dominates the tile. Widen before treating the numbers as general.
3. ~~**Nobody has looked at a rendered tile.**~~ **DISCHARGED 2026-08-14**, the
   same day, by [tools/render-tile.py](../../tools/render-tile.py). z16/38030/24562
   in Istanbul, 1,258 features: the decoded `ST_AsMVT` output and the same extent
   read straight from PostGIS are the same picture — correct orientation, no
   y-flip, no scale error, interior rings present, and the six-feature difference
   is the boundary buffer.

   Two things about how it was checked. **The MVT decoder was written from the
   spec rather than taken from a library**, because a library that shares
   assumptions with the encoder agrees with it about any mistake they both make.
   And **the comparison is against the source, not against a reference image** —
   a picture on its own only proves something was drawn.

   The first attempt filled every polygon and produced two identical solid
   rectangles. That was finding 11 arriving as a picture: the Marmara sea
   feature covers the whole tile and painted over every building in it. Outlines
   instead of fills, largest ring first.

---

## 8. Assumptions

| ID | Assumption | Status |
|---|---|---|
| A-057 | `ST_AsMVT` output is acceptable to the ArcGIS and MapLibre clients we care about | `UNVALIDATED` — condition 3 is the test, and it has never been run |
| A-058 | The datastore has spare capacity for tile encoding in a typical 100–1,000 service deployment | `UNVALIDATED`, and condition 1 is exactly this. The benchmark machine had headroom; nothing establishes that a real one does |

---

## 9. Dissent

**Against, and it is not weak.** This hands the hottest, most format-specific
part of the tiling pipeline to the database, in a product whose stated identity
is that the server domain is ours. The measured case for doing so is *an absence
of advantage* for the alternative, not an advantage for this — read-once was not
beaten, it merely failed to win, on a contended machine where the ratio ranged
0.61 to 1.84. A cleaner machine might have separated them.

The counter is that the allocation and GC numbers do not come from a wall clock
and were stable across every run: 124–245× more allocation, and a GC pause
reaching 35.5% at concurrency 4 against 0.0%. Those did separate the paths, and
they separated them in the direction A-037 predicted before this benchmark was
written.

**Against, second.** Condition 1 is a real hole and this ADR is being accepted
with it open. The honest position is that the decision is right for the
deployments we can measure and untested for the one ADR-019 made mandatory.## 5a. The tile path transforms — 2026-08-15

**Owner direction:** *"the imported shapefiles need to stay in their own
projection. If we use a 3857 basemap, it shall be projected on the fly."*

This reverses two things that were true until today.

**Imports no longer transform.** Every hosted import used to be moved to Web
Mercator on the way in, and the response said *"EPSG:4326 to EPSG:3857 is a
closed formula with no datum shift, so nothing was lost"* — a sentence that is
true of 4326 and was printed over national-grid imports where it is false. A
layer uploaded as EPSG:5254 came back as 3857 with its survey coordinates
already gone, and the response reassured the uploader about it.

**The tile path refused non-Mercator layers.** That refusal was the fix for
Q-96's silent-empty-tile defect, and it fixed the silence by telling people to
republish their data in a different projection — which is asking somebody to
destroy their coordinates so a tile is cheaper to cut.

**What happens now.** `ST_TileEnvelope` still produces a Web Mercator box,
because the XYZ scheme is defined in Web Mercator; that is a property of tiling
and not a requirement on the data. So the box is transformed **once** into the
layer's own reference for the `&&` filter — which keeps the spatial index in
play, and is the whole reason this is affordable — and each surviving row is
transformed on the way out. Q-96 measured 74.6 ms against 21.6 ms on the same
tile, produced from a 4326 layer, and the cache pays it once.

**What this leaves open, and it is not small.** PROJ chooses the pipeline. When
the shift grids for an accurate path are missing it falls back to a ballpark
transformation *without failing*, and a national grid can land metres from where
it should — on exactly the data where metres are legally significant. Recorded
as **D-32**, and the FeatureServer's `outSR` has the identical exposure.

---


