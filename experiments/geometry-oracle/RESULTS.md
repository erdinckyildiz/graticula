# Q-20's oracle — two engines, 448 comparisons, one divergence

**Run 2026-08-19** against the dev server and the experiment PostGIS. `oracle.py` is
the harness; `answers.json` holds every answer, agreeing or not.

[Q-20](../../docs/open-questions.md) is one of the five questions carried out of
Phase 0 as blocking. It asks **how many distinct geometry engines end up evaluating
our predicates, and how divergence is prevented**, and answers *six* — PostGIS's
GEOS, DuckDB's GEOS, MySQL's Boost.Geometry, MariaDB's, SQL Server's, Oracle's
SDO_GEOM, plus NetTopologySuite in our own process. It asks for
`experiments/geometry-oracle` and a conformance position per provider.

## What v1 actually has, which is two

Four of the six belong to providers [v1-scope](../../docs/v1-scope.md) does not
include — v1 is **PostGIS only** — so they defer with the decisions that would bring
them in. What is left is not a smaller version of the same question; it is a
different and sharper one, because **both remaining engines are reachable today**:

| Engine | Where it evaluates a predicate |
|---|---|
| **GEOS**, inside PostGIS | Every spatial filter on a FeatureServer query. `PostGisFeatureSource.Predicate` writes `st_intersects`, `st_contains`, `st_within`, `st_crosses`, `st_overlaps`, `st_touches`, `st_relate` or `st_dwithin` |
| **JTS**, as NetTopologySuite, in the overlay worker | `GeometryServer/relation`. `Graticula.Overlay.Worker`'s `Satisfies` calls JTS's `Disjoint`, `Intersects`, `Within`, `Touches`, `Crosses`, `Overlaps` |

**Six predicates are answerable by both surfaces.** A client can ask *do these two
geometries touch* of either one, and until this run nothing in the repository had
compared the answers beyond `intersects` on a corpus of real polygons
(`WorkerAgainstPostgisTests.Relation_picks_the_same_pairs_as_PostGIS`).

`st_dwithin` is the query path's alone. **`st_relate` is not, and the first version of
this document said it was** — which left the hardest comparison unmade for an
afternoon. `esriGeometryRelationRelation` carries a DE-9IM pattern in `relationParam`
and reaches `relation.Matches(pattern)` in the worker; `SpatialRelation.Relate`
reaches `st_relate(column, filter, @pattern)` in the provider. **So both engines answer
DE-9IM**, and it is the most intricate predicate either library has: nine characters
of `T`, `F`, `0`, `1`, `2` or `*` against the interior, boundary and exterior of each
geometry. Eight patterns are compared, each chosen because it separates two cases a
named predicate runs together — *the interiors meet in an area rather than a line*,
*the boundaries share a line rather than a point*, and so on.

## The cases, and why a corpus could not supply them

Real data almost never contains a self-intersecting bowtie, a pair of polygons a
nanometre apart, or a polygon sitting inside another's hole. Those are precisely
where GEOS and JTS are documented to be able to part company, so the sixteen cases
are written by hand, around the four edges Q-20 itself names — **validity,
precision, what touches, empty geometries**.

Both engines are handed the same numbers from one definition: each case is
coordinates, rendered to Esri JSON for our surface and to WKT for PostGIS. A case
written twice by hand is a case where a disagreement can be a typo.

## Every case is run twice, because floating point diverges with magnitude

The cases are written within coordinates 0–25 and web-Mercator metres run to 2×10⁷. At
an offset of **2×10⁶** the gap between representable doubles is about 5×10⁻¹⁰, so the
nanometre cases sit two representable steps above it — close enough that two libraries
could round in opposite directions, and far enough that the case is still a real gap.
**Checked, because a case that collapses at magnitude proves nothing:** at 2×10⁶ the
nanometre-apart pair is still `disjoint` and the nanometre-overlapping pair still
`overlaps`, so neither has snapped.

## Result: 392 of 448 agree, and the 56 that do not are all one thing

| Case | intersects | within | crosses | overlaps | touches | disjoint | Agree |
|---|---|---|---|---|---|---|---|
| shared edge | T | F | F | F | T | F | ✓ |
| shared vertex only | T | F | F | F | T | F | ✓ |
| identical | T | T | F | F | F | F | ✓ |
| point on the boundary | T | F | F | F | T | F | ✓ |
| point in the middle *(control)* | T | T | F | F | F | F | ✓ |
| line ending on the boundary | T | F | F | F | T | F | ✓ |
| line through | T | F | T | F | F | F | ✓ |
| line along the boundary | T | F | F | F | T | F | ✓ |
| **invalid bowtie** against a square | T | F | F | **T** | F | F | ✓ |
| **invalid bowtie** against itself | T | **F** | F | **T** | F | F | ✓ |
| a nanometre apart | F | F | F | F | F | T | ✓ |
| a nanometre overlapping | T | F | F | T | F | F | ✓ |
| collapsed sliver | T | T | F | F | F | F | ✓ |
| polygon inside a hole | F | F | F | F | F | T | ✓ |
| empty against a square | F | F | F | F | F | T | **✗** |
| empty against empty | F | F | F | F | F | T | **✗** |

The table above is the six named predicates at the written magnitude. The full run is
**448 comparisons** — 16 cases × (6 predicates + 8 DE-9IM patterns) × 2 magnitudes —
and it decomposes:

| Slice | Comparisons | Agree |
|---|---|---|
| Six named predicates | 192 | 168 |
| **Eight DE-9IM patterns** | **256** | **224** |
| At the written magnitude | 224 | 196 |
| **At an offset of 2×10⁶** | **224** | **196** |
| **Everything** | **448** | **392** |

**Every disagreement in every slice is the empty-geometry case** — 2 cases × 14
comparisons × 2 magnitudes = 56. Filter those out and the count is **392 of 392**.

**Every case that can be expressed through the product agrees, on all six named
predicates, on all eight DE-9IM patterns, and at both magnitudes.** Including the ones
chosen because they should be hardest:

- **The invalid bowtie does not throw on either side, and both give the same
  answers** — `overlaps` true, `touches` false, and against *itself* `within` is
  **false**. A valid geometry is always within itself; an invalid one is not, and
  both engines say so identically. Neither repairs it silently.
- **Precision is exact in both directions at 1e-9.** Neither engine snaps: a gap of
  a nanometre is disjoint and an overlap of a nanometre overlaps.
- **The polygon inside a hole is `disjoint`, not `within`** — the case where a
  bounding-box answer and a topological answer part company. Both are topological.
- **A nanometre-tall sliver sharing the square's base is `within` it.** Both agree.

### The one divergence is a refusal, not a wrong answer

An empty polygon: PostGIS answers every predicate (`disjoint` true, the rest false),
and our surface **refuses the request** — `'rings' must be a non-empty array`.

**And it is not reachable through the product.** `ArcGisGeometryReader` is shared by
both of our surfaces, so a query filter of `{"rings":[]}` is refused by the same
sentence — measured:

```
GET /rest/services/hosted/look_buildings/FeatureServer/0/query
    ?geometry={"rings":[],"spatialReference":{"wkid":3857}}
→ 400 'rings' must be a non-empty array.
```

So there is no path in which an empty geometry reaches one engine and not the other.
The asymmetry is between *our engine* and *PostGIS driven directly*, which is a
comparison no caller can make.

## What this settles, and what it does not

**Settled:**

- The v1 engine count is **two**, not six, and the other four defer with the
  providers they belong to.
- On every case expressible through the product, the two agree. Q-20's *how do we
  prevent divergence* has an answer for v1 that is not a policy: **there is none
  measured, across sixteen adversarial cases, fourteen predicates and two orders of
  magnitude.**
- The empty-geometry gap is closed at the door by a shared reader, with one message.
- **The cases are now a test** —
  `WorkerAgainstPostgisTests.Both_engines_agree_on_the_case_engines_disagree_about`,
  15 cases × 14 comparisons × 2 magnitudes = **420 in six seconds**. Falsified by
  making `Touch` mean `Crosses` in the worker, which failed it on three cases. So an
  NTS or GEOS upgrade that moves an answer fails the build, which is the only way this
  agreement can be lost.
- **Coordinate magnitude is measured, not assumed.** Shifting every case to 2×10⁶
  changes nothing on either side, and the nanometre cases keep their distinction there
  rather than collapsing.

**Not settled, and named:**

- **Beyond 2×10⁶.** The far edge of web Mercator is 2×10⁷, an order of magnitude
  further, and nothing was run there. The step between doubles is 4×10⁻⁹ at that
  distance, which is *larger* than the nanometre cases — so those cases would become
  vacuous rather than harder, and a different set would be needed.
- **Curves and Z.** Both refused by both engines (ADR-005 §3.3c; `WkbReader` drops
  Z), so there is nothing to compare — but that is an argument, not a measurement.
- **`st_dwithin`** has one engine, so no divergence and no oracle. A distance
  predicate is the one place a projected unit and a degree could be confused, and
  nothing checks ours against anything.
- **Multi-geometry beyond one case.** One multipolygon pair is in the test and none in
  the experiment's own list. Mixed dimensions — a multipolygon against a multiline —
  are not covered at all.
- **The four deferred engines.** Nothing here says anything about DuckDB, MySQL,
  MariaDB, SQL Server or Oracle. Q-20's original count is right for the product
  those decisions would create; it is wrong for the product v1 is.

## The harness was wrong first, as usual

The first run reported **0 of 96 agreeing**, twice, for two different reasons: the
request was sent as JSON where the GeometryServer takes form encoding, and then every
polygon was refused because the harness reversed the outer ring on the assumption
that the rings were written counter-clockwise. They are written clockwise. Both times
the server's own refusal named the problem precisely enough to fix in one step, which
is the argument for refusals that say what they mean. The winding is now computed
rather than assumed.
