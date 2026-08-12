# Build vs Adopt Policy

**Status:** ACTIVE — binding on all ADRs
**Owner decision date:** 2026-08-12
**Revisit trigger:** any proposal to place a component in a different tier

---

## 1. Why this policy exists

Two failure modes bracket this project.

Adopt too much and the platform becomes an integration of other people's
architectures — which contradicts the entire mission (§1). Adopt too little and
we reimplement twenty years of numerically delicate geometry and geodesy, badly,
and ship silently wrong coordinates.

This policy fixes the line once so that every ADR does not relitigate it.

## 2. The project owner's requirement

> I want to build the whole structure myself. I do not want to take in GEOS or
> MapServer, because if a bug appears I want it fixed in-house. Strong
> foundations like GDAL and GEOS may still be used.

The requirement behind this is **control over defect resolution**, not
authorship for its own sake. This policy satisfies that requirement through
three mechanisms (§5 below) rather than through wholesale reimplementation,
because reimplementation does not reduce defect risk — it relocates defects from
*known, tracked, patchable upstream* to *unknown and ours*.

## 3. The three tiers

### Tier 1 — Built by us. No exceptions.

This is the product. There is no meaningful off-the-shelf option for any of it,
and adopting one would mean adopting its architecture.

| Area | Includes |
|---|---|
| Service model | Service definition, catalog, stable IDs, publishing, validation, rollback |
| Runtime | Worker supervision, pooling, recycling, draining, crash containment, health |
| Request path | Routing, concurrency control, backpressure, resource governance, timeouts |
| Query | Query AST, spatial query planner, provider capability negotiation, SQL generation, result streaming |
| Providers | The provider abstraction itself and every provider implementation |
| API | OGC API Features/Tiles/Maps/Processes, WMS/WMTS/WFS surfaces, content negotiation |
| Tiling | Tile pipeline, tile addressing, MVT encoding, cache keys, invalidation |
| Cartography | Symbology model, style evaluation, label placement, decluttering, layer compositing |
| Platform | Configuration, secrets handling, migrations, admin API, RBAC, observability, job system |

MVT encoding sits in Tier 1 deliberately: it is a small, fully specified
protobuf format. Owning it costs little and removes a dependency from the
hottest path in the system.

### Tier 2 — Adopted, but always behind our own port.

Numerically delicate or enormous in scope. Adopting is the correct engineering
call; the mitigation is isolation, not avoidance.

| Capability | Candidate implementations | Why not ours |
|---|---|---|
| Geometry topology (overlay, buffer, predicates, validity) | GEOS, JTS, NetTopologySuite, `geo` (Rust), PostGIS-side | Floating-point robustness, snap-rounding, OverlayNG — two decades of edge cases. A hand-rolled overlay returns *wrong answers*, not crashes. |
| Coordinate transformation | PROJ | Not just maths: geodetic grid shifts (NTv2 and friends). Errors are silent metre-scale offsets — the worst defect class in GIS. |
| Raster I/O and format support | GDAL | Hundreds of formats and driver quirks. No realistic alternative exists in any language. |
| Rasterization backend | Skia, Cairo, AGG-style engines | Anti-aliasing, glyph rendering, path filling. Commodity, well-tested. |
| Text shaping / fonts | HarfBuzz, FreeType (usually via the rasterizer) | Complex scripts. Never reimplement. |
| Image codecs, protobuf, compression | Ecosystem standard | Commodity. |

**The binding rule for Tier 2:**

> No Tier 2 library type, function, or error may appear in a Tier 1 signature.

Tier 1 code calls `IGeometryEngine.Intersects(a, b)` — never `GEOSIntersects`.
It calls `ICrsTransformer.Transform(...)` — never a PROJ handle. Geometry
crossing the boundary uses **our** geometry representation, not the library's.

This is what makes §10 ("architecture is reversible") real rather than
aspirational: any Tier 2 choice can be replaced without touching Tier 1.

### Tier 3 — Never adopted.

Finished GIS server products: **MapServer, GeoServer, QGIS Server, ArcGIS
Server**, in whole or in part, as embedded engines, forks, or vendored
subsystems.

These are not libraries. Adopting one means inheriting its service model,
configuration model, threading model and extension model — precisely the
architectures this project exists to reconsider. They remain valuable as
**objects of study** (§4, §16) and as behavioural references for standards
conformance. Nothing more.

## 4. The exception: our own lightweight geometry primitives

There is one area where writing our own is likely correct, and it is not a
compromise — it is a performance argument.

The tile-generation hot path performs, per tile, thousands of cheap operations:
bounding-box tests, tile-space affine transforms, clipping to a tile envelope,
coordinate quantisation, and simplification. Crossing an FFI boundary (or
allocating library geometry objects) thousands of times per tile is likely
indefensible overhead for operations that are individually trivial and
numerically undemanding.

**Proposal, not conclusion:** implement these primitives ourselves against our
own geometry representation, and delegate only genuinely hard topology to
Tier 2. This is recorded as a hypothesis in
[architecture-assumptions.md](architecture-assumptions.md) (`A-004`) and must be
settled by benchmark before it is relied upon.

Simplification is the boundary case worth watching: Douglas–Peucker is easy,
*topology-preserving* simplification is not. The split may land mid-way through
this list.

## 5. How defect control is actually achieved

The owner's requirement is met by three mechanisms, all of which are cheaper and
safer than reimplementation.

1. **We build the dependency from source and hold the patch.** GEOS is LGPL,
   GDAL and PROJ are permissive; copyleft is acceptable for this project
   (see [DEPENDENCY-LICENSES.md](../DEPENDENCY-LICENSES.md), where every claim
   here must be verified before it is relied upon). If a defect blocks us, we
   patch our build and upstream it afterwards. We never wait on someone else's
   release cycle.
2. **The port layer makes replacement a bounded job.** A Tier 2 component can be
   swapped — including for an implementation of our own — without a rewrite.
3. **We own the conformance suite.** `experiments/geometry-oracle` builds our own
   correctness corpus and uses the adopted engine as an oracle. This gives us an
   independent measure of correctness, protects against regressions on
   replacement, and is the precondition for ever writing our own engine
   credibly.

Mechanism 3 is what converts "write our own geometry engine" from a wish into a
decidable question. It is deliberately built early.

## 6. Consequences for other decisions

- **[ADR-001](adr/ADR-001-core-language.md) gains a criterion.** If in-house
  debuggability is a first-class goal, a language whose geometry stack runs in
  the same runtime (Java/JTS, C#/NetTopologySuite, Rust/`geo`) is materially
  easier to diagnose than one that reaches C++ across FFI. This is a genuine
  input to the language decision and is recorded as such. It cuts against Go,
  where the common path is cgo to GEOS.
  Raster is different: GDAL is a native-code dependency in every language, so it
  does not discriminate between candidates.
- **[ADR-003](adr/ADR-003-geometry-engine.md) must keep "our own engine" on the
  table** as an explicit alternative, to be accepted or rejected on evidence —
  not dismissed by this policy. This policy states a default, not a verdict.
- **Every Tier 2 adoption needs a named port interface in its ADR.** An ADR that
  adopts a library without defining the seam is incomplete.
