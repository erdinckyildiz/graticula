# ADR-003 — Geometry Engine

| | |
|---|---|
| **Status** | **`ACCEPTED WITH CONDITIONS`** — single-provider scope. Cross-engine consistency is deferred, see §6d |
| **Confidence** | `HIGH` for the split, which is measured. `LOW` for cross-engine consistency, which is not |
| **Decided** | 2026-08-13 |
| **Answers** | Q-03 · blocker **B2a** |

---

> **Unblocked 2026-08-12.** [ADR-001](ADR-001-core-language.md) chose .NET, so
> the in-runtime engine is **NetTopologySuite** — a managed port of JTS, the
> reference implementation, debuggable in the same debugger as our own code.
> That is the strongest available position on the owner's defect-resolution
> requirement, and it settles the *which engine* half of this ADR.
>
> What remains open is the **split** (§2, Alternative B): which work goes to
> NTS, which is pushed to the provider, and which uses our own hot-path
> primitives. In-process MVT encoding being mandatory (Q-50a) makes that split
> more consequential than it was, because clip, quantise, simplify and
> tile-space transform are now on the critical path for two of three providers.

## 1. Context

The platform needs geometry operations at three very different intensities, and
conflating them has historically produced bad decisions:

1. **Hot-path primitives** — bounding-box tests, tile-space transforms, clipping
   to a tile envelope, coordinate quantisation, simplification. Thousands per
   tile. Numerically undemanding, latency-critical.
2. **Topology** — overlay (intersection, union, difference), buffer, validity
   checking and repair, precise predicates. Numerically delicate. Wrong answers
   here are silent data corruption, not crashes.
3. **Database-side** — operations PostGIS can execute better than we can,
   because the data is already there and indexed (§30).

The decision is not "which library" but **where each of these three executes**.

## 2. Alternatives considered

### Alternative A — Adopt one engine for everything

GEOS, JTS or NetTopologySuite (depending on ADR-001) behind a port, used for all
in-process geometry.

**For.** One implementation, one set of semantics, one correctness story.
Maximum maturity for the hard operations.

**Against.** Pays library overhead — object allocation, and FFI marshalling if
the engine is native — on the hot path, where the operations are individually
trivial. See A-004.

### Alternative B — Split: our own primitives, adopted topology

Own geometry representation and hot-path primitives written by us; overlay,
buffer, validity and precise predicates delegated to the adopted engine.

**For.** Removes per-operation overhead where volume is highest, while keeping
the numerically dangerous work in mature hands. Aligns with the tiering in
[build-vs-adopt-policy.md](../build-vs-adopt-policy.md) §4.

**Against.** Two geometry representations means a conversion boundary, and
conversion is itself a cost and a defect source. Where the split falls is not
obvious — topology-preserving simplification is genuinely hard and sits right
on the seam.

### Alternative C — Push everything to PostGIS

Let the database do geometry; the server orchestrates.

**For.** PostGIS is excellent and the data is already there. Least code.

**Against.** Binds the platform to one provider, contradicting the provider
abstraction (§27). Fails for file-based providers (GeoPackage, FlatGeobuf, COG)
where there is no database to push to. §30 explicitly warns against blindly
pushing everything down.

### Alternative D — Write our own engine, including topology

**For.** Total control, which is the project owner's stated instinct.

**Against.** Robust overlay is a twenty-year problem. Floating-point predicate
robustness, snap-rounding and the OverlayNG generation of algorithms exist
because the naive implementations were subtly, silently wrong. This alternative
is not dismissed by policy — but it carries the burden of proof, and the
conformance suite (§4 below) is the only credible way to discharge it.

## 3. Counterarguments to the preferred option

The current lean is **B**. Against it:

- The conversion boundary may cost more than the FFI calls it avoids. Unmeasured.
- If ADR-001 selects a language with an in-runtime engine (JTS or
  NetTopologySuite), there is no FFI boundary and the allocation overhead may be
  small enough that A is simply better and simpler.
- **B's benefit is therefore partly contingent on ADR-001.** That dependency must
  be respected, not assumed away.

## 4. The conformance suite comes first

Regardless of which alternative wins, `experiments/geometry-oracle` is built
early:

- our own correctness corpus, including degenerate and adversarial inputs
  (self-intersecting rings, slivers, collapsed segments, antimeridian crossings,
  extreme coordinate magnitudes, geometry bombs)
- the adopted engine used as an oracle
- differential testing between any two candidate implementations

This gives an independent correctness measure, protects against regressions when
a Tier 2 dependency is replaced, and is the precondition for taking Alternative D
seriously at any future point.

## 5. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Hot-path library overhead is material | **Yes, overwhelmingly.** `NTS.Intersection` was 79% of a tile request; our rectangle clipper reduced that stage 63x, from 438.6 ms to 7.0 ms | [benchmarks/mvt-generation/RESULTS.md](../../benchmarks/mvt-generation/RESULTS.md) |
| Our own primitives are cheap | Tile-space transform 1.4 ms, MVT encoder 3.8 ms, rectangle clip 7.0 ms — against 438.6 ms for the adopted general overlay | ibid. |
| Own clipper agrees with the adopted engine | Output 170,539 vs 170,557 bytes across 4,854 features; both decode with zero malformed geometries | ibid. §5 |
| Conversion boundary cost is lower than the overhead it avoids | — | still pending |
| Own primitives match the oracle across the full operation set | — | `experiments/geometry-oracle` — pending |

## 5a. Alternative B is validated by measurement

**2026-08-12.** The lean recorded in §3 now has evidence behind it, and the
boundary between own primitives and adopted topology is empirically placed
rather than argued.

`NTS.Intersection` runs general polygon-polygon overlay — robust predicates,
snap-rounding, the whole OverlayNG machinery — to clip against an axis-aligned
rectangle. It does not need any of that, and in a dense urban tile most features
are entirely *inside* the tile, where the correct answer is a bounding-box
comparison and no clipping at all.

**What is ours:** bbox accept/reject, rectangle clip, simplification for tiles,
tile-space transform and quantisation, MVT encoding.

**What stays adopted:** genuine topology — overlay, buffer, validity, precise
predicates. The Sutherland–Hodgman clipper can emit degenerate connecting edges
on concave polygons, which tile renderers tolerate and analytical overlay would
not. That is exactly the line
[build-vs-adopt-policy.md](../build-vs-adopt-policy.md) §4 drew, and the
measurement confirms where it belongs.

**The bottleneck moved, and was taken.** With the clip fixed,
`DouglasPeuckerSimplifier` was 55% of a z12 tile. Run 2 of the same benchmark
replaced it with `TileSimplify`: **363.9 ms to 7.8 ms on the stage**, 637 ms to
291 ms on the request, emitting **0.27% fewer vertices** — that is, it is not
faster by discarding more.

## 5b. Where the boundary actually falls, after two rounds

The hypothesis going into run 2 was that NTS's cost was topology repair —
`IsValid` on every simplified polygon and `Buffer(0)` on the failures.
**Measurement refuted it.** With `EnsureValidTopology` off, the simplifier was
307.5 ms against 363.9 ms; within the noise, nothing. The polygons are valid and
the repair never fires.

The cost is object churn. NTS `Coordinate` is a `class`, so a 556,728-vertex
tile is 556,728 heap objects before the first distance calculation. Measured
directly: **a z12 tile request allocates 404 MB with NTS simplification and
204 MB with ours**, with GC pauses of 18–153 ms landing inside the request.

That refines §3's lean in a way the earlier framing did not anticipate. The
question is not *which operations* to own — it is **where geometry stops being
objects**. Both primitives we have written are fast for the same reason: they
work on flat arrays. The largest remaining cost is WKB parsing building an NTS
geometry graph that exists only to be discarded one stage later.

That points at a decision this ADR does not currently contain: whether the
provider interface hands back geometry objects at all on the tile path, or hands
back coordinates. Raised as **Q-66** rather than decided here, because it
changes the provider contract and not just an implementation.

## 6. Decision

**Alternative B, refined by measurement into three tiers rather than two.**

§2 framed this as *ours versus adopted*. Runs 1–3 added a tier above both, and
it is the one that matters most.

### 6a. The three tiers, in cost order

| Tier | Where | What belongs here |
|---|---|---|
| **1. Push down to the provider** | In the database, before anything crosses the wire | Bounding-box filter, **clip**, **simplify**, and any predicate the dialect supports |
| **2. Ours, on flat arrays** | In our process, no geometry objects | Rectangle clip, tile simplification, tile-space transform and quantisation, MVT encoding |
| **3. Adopted — NetTopologySuite** | In our process, on NTS geometry | Genuine topology: overlay, buffer, validity, precise predicates, convex hull, everything GeometryServer exposes |

**Tier 1 is first because it beats the other two by an order of magnitude, and
not for the reason we expected.** [benchmarks/mvt-generation/RESULTS.md](../../benchmarks/mvt-generation/RESULTS.md) finding 11: a z16 tile with 327
features and 12 KB of output was reading **201,580 vertices to emit 2,080**, because
four administrative polygons — Türkiye at 72,919 points, Marmara Denizi at 52,455 —
overlap every tile in the city. **A tile's cost floor is set by the largest
geometry overlapping it, not by its content.** Pushing clip and simplify into the
database cut latency 13x and allocation 15x at z16.

The rule that follows, and it should be read before the table above:

> **The cheapest geometry operation is the one that never crosses the wire.**

### 6b. What decides whether an operation is ours or adopted

Tier 2 versus tier 3 is not taste. Three conditions, all required:

1. **There is a special case we can exploit.** `RectClip` is 63x faster than
   `NTS.Intersection` because clipping to an axis-aligned rectangle is not
   general polygon overlay, and in a dense urban tile most features are entirely
   *inside* — where the right answer is a bounding-box comparison and no
   clipping at all. Where no such special case exists, tier 3.
2. **It is on the hot path**, so the win is worth the maintenance and the
   conformance burden.
3. **Renderable is a sufficient correctness bar.** Our clipper can emit
   degenerate connecting edges on concave polygons and `TileSimplify` can produce
   self-intersections; renderers tolerate both, analytical overlay would not.

**Where any of the three fails, the operation is adopted.** That is why
GeometryServer — which Q-17a made v1 core — is entirely tier 3: it publishes
general overlay on caller-supplied geometry, where none of the three conditions
holds.

### 6c. The mechanism, which is the transferable part

Both of our primitives won for the same reason, and it is not cleverness.

**NTS `Coordinate` is a `class`.** A 556,728-vertex tile is 556,728 heap objects
before the first distance calculation. Working on flat `double[]` removed them:
a z12 tile went from **404 MB allocated to 204 MB**, and gen0 collections halved.

That matters more than either primitive, because A-037 established that
**allocation, not CPU, is the ceiling** — 80.9% GC pause at 18% CPU utilisation
under concurrency. **A tier-2 candidate is therefore identified by allocation
behaviour, not by CPU profile**, and the profiler that only shows CPU will miss
every one of them.

It also refutes the hypothesis this ADR would otherwise have adopted. §5b records
it: we predicted NTS's simplification cost was topology repair — `IsValid` on
every polygon, `Buffer(0)` on failures. Measured with repair disabled: 307.5 ms
against 363.9 ms. Nothing. The cost was object churn all along.

### 6d. Scope of this decision, and what is deferred

**This decision is made for a single provider.** It is complete and sufficient
for the walking skeleton, which is PostGIS only.

**Deferred: the cross-engine consistency position.** Q-80 and Q-81 took the
provider count to six, so six geometry engines will evaluate our predicates —
PostGIS's GEOS, DuckDB's GEOS, MySQL 8's Boost.Geometry, MariaDB's, SQL Server's,
Oracle's SDO_GEOM, plus NetTopologySuite in-process. **They disagree at the edges**
on validity, precision, `touches` and empty-geometry results, and tier 1 pushes
work into exactly those engines.

That is a correctness problem rather than a performance one, and **ADR-008 §2's
never-degrade-silently does not cover it**: refusing an unsupported operation is
honest, quietly returning a different answer is not, and no capability report
catches it because every engine claims `intersects`.

It is `A-043`, it is deferred, and **the trigger is the second provider, not a
date.** The validation mechanism is `experiments/geometry-oracle`: one corpus of
adversarial geometries, all six engines, diff the answers. Until it runs, tier-1
pushdown is proven on PostGIS and assumed nowhere else.

### 6e. Precision

The in-process factory uses a **floating** precision model. Tile-space
quantisation to the 4096-unit integer grid is a **deliberate, lossy, terminal**
transformation: quantised geometry is rendered and discarded, never written back
and never used for analysis. This is the same rule
[ADR-008](ADR-008-query-engine.md) §4.5a already states as *lossy on read means
not writable*, arrived at from the other direction.

## 7. Assumptions

| ID | Assumption | Status |
|---|---|---|
| A-004 | Hot-path geometry overhead is material enough to justify our own primitives | **`VALIDATED` 2026-08-12** — twice, on clipping and on simplification |
| A-006 | A single internal geometry representation can serve both vector and tile paths without a second conversion | **`CONTESTED` 2026-08-13** — evidence points both ways, which per the register's own definition means it is stated at the wrong granularity. **Against it:** the tile path measured ~139 bytes allocated per vertex read, three to four copies of every coordinate, and both tier-2 primitives won by *leaving* the shared representation. **For it:** topology (tier 3) genuinely needs geometry objects, and GeometryServer is v1 core. The likely resolution is two representations with an explicit conversion where topology is required — which is **Q-66**, and is not decided here |
| A-043 | Six geometry engines can be made to agree closely enough that the same query returns the same answer on any provider | `UNVALIDATED` — deferred with §6d. Gates provider two, not the skeleton |

## 8. Dependencies

**Depends on:** ADR-001 (language determines which engines are in-runtime),
ADR-002 (data architecture determines how much can be pushed down).

**Depended on by:** ADR-004 (rendering), ADR-008 (query engine), ADR-009 (raster,
for vector/raster interaction), the tiling pipeline.

## 9. Revisit triggers

- The oracle suite shows the adopted engine failing cases we care about.
- Benchmarks show the conversion boundary dominating the saved overhead.
- The adopted engine's licence or maintenance status changes materially.
- **A second provider ships** — §6d's deferral expires by event, not by date.
- **Q-66 resolves toward coordinates rather than geometry objects**, which would
  move the tier 2/3 boundary rather than merely optimise behind it.
- **A tier-2 candidate is proposed that fails any of §6b's three conditions.**
  The temptation will recur, and the conditions exist to be applied rather than
  admired.

## 9a. Conditions

1. **Tier-1 pushdown is verified per dialect before that provider ships**, not
   assumed from PostGIS. §6d.
2. **Our two primitives keep a conformance test against NTS** on the cases where
   both are valid — run 2 measured 292,716 output vertices against NTS's
   293,508, and that agreement is a property to hold, not a coincidence to note
   once.
3. **The renderable-correctness bar in §6b.3 is enforced by construction**: tier
   2 output must not be reachable by any analytical path. ADR-008 §4.5a is the
   existing mechanism.

## 10. Dissent

**Against tier 2 existing at all.** Alternative A — adopt NTS for everything —
is simpler, has no conformance burden and no second implementation to keep
correct. The measurement is decisive against it on the hot path (63x and 47x),
but the honest form of the objection is not *is it faster*; it is **how many
primitives will we end up owning?** Each is a maintenance and correctness
liability, and §6b's three conditions exist precisely because the answer must
not be *as many as we feel like*.

**Against tier 1 being first.** Pushing clip and simplify into the provider makes
our output depend on six vendors' geometry implementations, which is §6d's
deferred problem and A-043's risk. Doing the work in-process would give one
answer everywhere. The counter is finding 11: without pushdown the fallback path
reads two orders of magnitude more geometry than it emits, and no amount of
in-process cleverness recovers that. **We have chosen consistency risk over a
cost we measured, and §6d says so plainly rather than filing it as an
optimisation.**
