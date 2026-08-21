# ADR-043 — ImageServer, and where the raster Tier 1 line falls

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` for the decomposition and the Tier 1 line · `LOW` for the first cut's completeness, which is stated below rather than implied |
| **Decided** | 2026-08-21, by owner decision |
| **Amends** | [ADR-009](ADR-009-raster-engine.md) — reverses §2.1's central decision for the export path and keeps it for direct delivery |
| **Answers** | [Q-77](../open-questions.md) (the Tier 1 line), [Q-121](../open-questions.md) (registered in place), and the expensive third of [Q-17c](../open-questions.md) |

---

## 1. What was asked, and what the register already held

The owner asked on 2026-08-21 to take raster serving seriously, then narrowed it to
**ImageServer**, then chose the widest of three offered cuts: **catalogue plus
`exportImage`**, with imagery **registered in place**.

**The register was not empty on this and its content is most of this decision.**
[ADR-009](ADR-009-raster-engine.md) decided *serve COG, let the client render*.
Q-17c put ImageServer in scope on 2026-08-13; v1-scope §3b cut it the same day; ADR-009
re-closed on 2026-08-15 with a note saying the reopening trigger is *ImageServer, or any
part of the expensive group, returning to scope*. **That trigger has fired.**

**ADR-009 §0's decomposition is inherited whole and is the reason this decision is
tractable.** ImageServer is three groups at very different cost, not one capability:

| Group | Operations |
|---|---|
| Near-free | `identify`, `getSamples`, `computeHistograms`, `computeStatisticsHistograms`, `download`, mosaic footprint `query` |
| Medium | Tiled access to already-processed imagery |
| Expensive | `exportImage`, raster function chains, dynamic mosaicking |

## 2. What ADR-009's cost table got wrong, measured against the code

**It was written on 2026-08-12, against a design.** There is code now, and the code
disagrees in both directions. This section exists because the table is the thing a
reader would plan from.

**The near-free group is not near-free here.** ADR-009 called it *a COG range read or a
GDAL call we already make*. Measured 2026-08-21: **`src` contains no raster code at
all** — no COG reader, no raster layer kind, and not one GDAL call, because
[A-016](../architecture-assumptions.md) deliberately confines GDAL to the job worker and
out of the serving process. `LayerDefinition` requires a schema, a table, a geometry
column and an identity column, so **every layer this server can hold is a PostGIS
table**, and a COG on a disk is not one. The cheapest operation in the cheapest group
still needs a catalogue model, a reader and a registration path.

**The expensive group is cheaper than it was.** ADR-009 §0 called `exportImage`
*plausibly the largest single capability in the matrix, larger than the vector
renderer*. Since then [ADR-041](ADR-041-the-map-renderer.md) has shipped the canvas
port, the pixel transform, the scale model, format encoding, the image-size ceiling, an
export endpoint shape and a CITE-tested WMS. **The delivery half exists.** What
`exportImage` adds is: read pixels, warp them, decide their colour, composite.

**So the balance inverted and the sequencing follows from that**, not from the original
table.

## 3. Decision

### 3.1 ImageServer is built, outside v1

**v1 does not grow.** This is the fifth surface built beside it rather than inside it,
after WFS, the portal, WMS/MapServer and OGC API Features — the pattern
[ADR-039](ADR-039-wfs-is-the-first-surface-after-v1.md) §1 recorded and the owner has
now applied five times. v1-scope §3b's ImageServer bullet stays as written and stays
true about v1.

### 3.2 The first cut is the catalogue and `exportImage`

`GET|POST /rest/services/{name}/ImageServer` — the service document.
`GET|POST .../ImageServer/exportImage` — pixels, rendered here.
`GET|POST .../ImageServer/identify` — the value under a point.

**Raster function chains and dynamic mosaicking are not in the first cut** and this
document does not pretend otherwise. One raster, one rendering rule, one request. A
mosaic is a second decision with a dataset model behind it, and bundling it here would
repeat the mistake ADR-009 §0 exists to warn about.

### 3.3 Imagery is registered in place — Q-121 answered

**A COG stays where it is and is never copied.** Registration reads its header, records
its extent, band count, data type, overviews and CRS, and stores a reference. This is
the owner's decision of 2026-08-21 and it settles the half of Q-121 that was open.

**The bytes still travel through us.** ADR-009 §2.4 already decided this and the
reasoning is unchanged: a client fetching a COG straight from object storage cannot be
authorised per layer, and per-layer authorisation is the requirement this product exists
to serve. So a range-request proxy, not a signed URL, and that is what *in place*
costs.

### 3.4 The Tier 1 line — Q-77 answered

Q-77 asked where the line falls and said it *needs stating before either half is built*.
It is stated here:

| Side | What | Why |
|---|---|---|
| **Tier 2, adopted** | Decoding a TIFF, walking its tiles and overviews, decompressing a strip, resampling, warping between reference systems | `build-vs-adopt-policy.md` names *raster I/O* Tier 2 in as many words. None of it is a decision about a map |
| **Tier 1, ours** | Which overview level answers this request, how values become colours — the stretch, the ramp, the no-data rule — and how the raster composites with the vector layers above it | Choosing what colour a pixel becomes is cartographic logic, which the policy puts in Tier 1, and it is the same judgement `SymbologyPlan` already makes for vectors |

**The line is the same one ADR-041 drew and it is drawn the same way**: the adopted
library fills triangles, we decide what to fill. Here the adopted library hands us
numbers, we decide what colour they are.

### 3.5 The adopted library is `BitMiracle.LibTiff.NET`, behind our own port

BSD-3-Clause, verified from the project's own licence page cited by its nuspec, with
bundled IJG JPEG and libtiff notices that go into `NOTICE`. **One project may name it —
`Graticula.Raster.Tiff` — and an architecture test enforces that**, exactly as
`NativeDependencyTests` does for SkiaSharp. No library type appears in a Tier 1
signature.

**Not GDAL, and the reason is A-016 rather than preference.** GDAL is the obvious
answer and it is deliberately not in the serving process; putting it there to serve one
surface would reverse an assumption that four other decisions rest on. A TIFF reader is
the smaller adoption that fits the shape already chosen.

### 3.6 The canvas learns to draw an image

`IMapCanvas` gains one method. It has `FillArea`, `DrawLine`, `DrawMarker`, `DrawLabel`
and `Encode`; it cannot place a bitmap, so a raster cannot appear under a vector map
today. **That one method is what makes raster a layer rather than a separate product**,
and it is why WMS and MapServer get raster for free once this lands.

## 4. Consequences

- **A layer is no longer always a PostGIS table.** This is the largest structural
  consequence and it reaches the catalogue, the sharing check, the directory, the
  console and every capabilities document. Each of those asks *what layers are there*
  and every one of them assumes the answer has a geometry column.
- **Warping needs per-pixel inverse projection**, and `IProjector` batches geometries.
  The usual answer is to project a coarse grid of control points and interpolate
  between them; that is an approximation with a stated error and it needs measuring
  rather than assuming.
- **Q-15 grows again.** An air-gapped deployment now also needs whatever the reader
  needs. The TIFF library is managed code with no native asset, which is the reason it
  is a smaller answer than GDAL.
- **The tile cache has a second kind of tenant.** ADR-010's cache is keyed for vector
  tiles; a rendered raster is expensive in a different way and its invalidation is not
  the same question.

## 5. Conditions

1. **A real ArcGIS client opens an ImageServer published here and draws it.** Not our
   own tests agreeing with each other — that is what Q-94 named as owed for the
   FeatureServer path and what ArcGIS Pro 3.6 paid on 2026-08-19. The same standard
   applies here and this condition is not discharged by a conformance suite.

2. **The warp's error is measured and stated, not assumed.** Control-point
   interpolation is an approximation; the condition is a number in
   `benchmarks/` saying how far a pixel can land from where it belongs, at the
   grid density shipped.

3. **`exportImage` is bounded the way `GetMap` is.** The same image ceiling, the same
   connection budget, the same deadline. A raster request can be arbitrarily more
   expensive than a vector one at identical pixel dimensions, and
   [D-128](../architecture-debt.md) already records that the budget does not shed load
   on every path.

4. **The architecture test covers the new adapter before the adapter has a second
   caller.** SkiaSharp's containment is enforced; the TIFF reader's must be enforced the
   same way and on the same day, because a port with one implementation and no test is
   a port by intention only.

5. **The near-free operations are not claimed until they are served.** ADR-009's table
   listed six of them as almost free and §2 above shows they are not. The service
   document must not advertise a capability this face does not answer — which is
   correctness gate 2's finding 5, and it cost that gate a `Map,Query,Data` string that
   was untrue.

6. **`NOTICE` carries the three attributions** — BitMiracle, IJG and libtiff — before
   anything ships that links the reader.

## 6. What this does not decide

Raster function chains, dynamic mosaicking, the mosaic dataset model, WCS, OGC API
Coverages, and whether STAC (ADR-009 §2.3) becomes the catalogue model for collections
of imagery rather than for single rasters. **Each is its own decision** and ADR-009 §0's
warning applies to all of them: decomposed per operation, not accepted whole.
