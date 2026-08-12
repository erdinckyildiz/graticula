# ADR-003 — Geometry Engine

| | |
|---|---|
| **Status** | `DRAFT` |
| **Confidence** | — |
| **Decided** | — |

---

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
| Hot-path library overhead is material | — | `benchmarks/` — pending |
| Conversion boundary cost is lower than the overhead it avoids | — | pending |
| Own primitives match the oracle on the hot-path operation set | — | `experiments/geometry-oracle` — pending |

Status stays `DRAFT` until these rows are filled.

## 6. Decision

Pending. Blocked on ADR-001.

## 7. Assumptions

| ID | Assumption | Status |
|---|---|---|
| A-004 | Hot-path geometry overhead is material enough to justify our own primitives | `UNVALIDATED` |
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
