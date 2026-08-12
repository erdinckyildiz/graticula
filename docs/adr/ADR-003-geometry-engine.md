# ADR-003 — Geometry Engine

| | |
|---|---|
| **Status** | `DRAFT` — **unblocked 2026-08-12**, ADR-001 chose .NET |
| **Confidence** | — |
| **Decided** | — |

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

Pending. Blocked on ADR-001.

## 7. Assumptions

| ID | Assumption | Status |
|---|---|---|
| A-004 | Hot-path geometry overhead is material enough to justify our own primitives | **`VALIDATED` 2026-08-12** — twice, on clipping and on simplification |
| A-006 | A single internal geometry representation can serve both vector and tile paths without a second conversion | `UNVALIDATED` |

## 8. Dependencies

**Depends on:** ADR-001 (language determines which engines are in-runtime),
ADR-002 (data architecture determines how much can be pushed down).

**Depended on by:** ADR-004 (rendering), ADR-008 (query engine), ADR-009 (raster,
for vector/raster interaction), the tiling pipeline.

## 9. Revisit triggers

- The oracle suite shows the adopted engine failing cases we care about.
- Benchmarks show the conversion boundary dominating the saved overhead.
- The adopted engine's licence or maintenance status changes materially.

## 10. Dissent

To be recorded during the debate round.
