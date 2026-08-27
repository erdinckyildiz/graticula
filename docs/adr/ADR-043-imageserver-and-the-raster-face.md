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

> **AMENDED 2026-08-21: the warp landed, so the cut grew by the thing that made it
> usable.** The paragraph below was written expecting requests only in the coverage's
> own reference, and that was how it first shipped — a client asking for Web Mercator
> was refused with a sentence naming the system that worked. Condition 2's measurement
> is what changed it: the interpolation error is 0.0223 pixels, which is small enough
> that refusing was the more expensive of the two answers.
>
> **`identify` still answers only in the coverage's own reference**, and that asymmetry
> is deliberate. Warping an image amortises one grid of control points over a million
> pixels; projecting a single point is a round trip for one answer, and the whole point
> of `identify` is that it is cheap.

> **AMENDED 2026-08-22: tiles are in, by
> [ADR-044](ADR-044-tiles-are-served-because-the-claim-had-to-be-true.md).** Not because
> the reasoning below was wrong — it was about keeping the increment reviewable, and it
> did that — but because ArcGIS Pro will not open an image service whose capabilities
> lack the word `Tilemap`, and the choice was between serving the operation and claiming
> it. `tileInfo` is populated, `singleFusedMapCache` stays false, and a tile is rendered
> when it is asked for.

`GET|POST /rest/services/{name}/ImageServer` — the service document.
`GET|POST .../ImageServer/exportImage` — pixels, rendered here.
`GET|POST .../ImageServer/identify` — the value under a point.
`GET .../ImageServer/tile/{level}/{row}/{column}` — one tile of the scheme.
`GET .../ImageServer/tilemap/{level}/{row}/{column}/{across}/{down}` — which of a block
this coverage has ground for.

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

**State.** *Catalogue*: **coverages** — a registered raster's path, the header facts
read once at registration, its sharing scope and its status. The pixels stay where they are and
are never copied, which is §3.3's decision, so what is stored is a reference and a description
rather than data. *Runtime*: the reader opens the file per request and holds nothing between
them.

## 5. Conditions

1. **A real ArcGIS client opens an ImageServer published here and draws it.** Not our
   own tests agreeing with each other — that is what Q-94 named as owed for the
   FeatureServer path and what ArcGIS Pro 3.6 paid on 2026-08-19. The same standard
   applies here and this condition is not discharged by a conformance suite.

   **DISCHARGED 2026-08-21.** The **ArcGIS Maps SDK for JavaScript 4.29** loaded
   `hosted/look_imagery` as an `ImageryLayer`, read the service document for its extent
   and reference, and asked for pixels **in Web Mercator** —
   `bboxSR=3857&imageSR=3857&size=1280,804&format=jpgpng` — which the warp answered. The
   raster draws registered against an OpenStreetMap basemap, in the right place and at
   the right size.

   **And it found two defects that no test of ours had.** `format=jpgpng` is the SDK's
   default and the parser refused it by name, so the first attempt framed correctly on
   empty ground: a request refused with a good reason, rendered as a blank map. That is
   the failure mode a conformance suite written against our own reading of the
   specification cannot find, which is the whole argument for this condition. The
   second was ours reading the SDK wrongly rather than the reverse — `layer.bandCount`
   is undefined because the SDK parses it into `rasterInfo` — and it printed *?-band*
   beside a picture that plainly had three.

   **ArcGIS Pro 3.6 was the other half and it was paid on 2026-08-22, at a price.**
   Driven through arcpy — the library Pro itself is built on, ArcInfo licensed —
   `MakeImageServerLayer` makes a layer, `Describe` reports 1 band, EPSG:4326 and the
   coverage's own extent, `arcpy.Raster` reports `U8`, and the raster draws.
   **Every step of a nine-step probe matches Esri's own `WorldElevation3D/Terrain3D`
   service, including the two steps that fail** — `CopyRaster` fails identically against
   both, because an image service that says `allowCopy: false` does not offer a download.
   `tools/pro-probe.py` runs the two side by side for exactly that reason: without the
   control column, a step no image service passes reads as a defect in this one, and it
   was misread that way twice before the control was added.

   **It cost four defects of ours, and none of them was findable from inside.** `bbox`
   arrives from Pro as an envelope object and this server read only the four-number
   spelling. `bboxSR` arrives as `{"wkid":102100,"latestWkid":3857}` — the retired Web
   Mercator code and the live one together — and the parser kept every digit after the
   first `wkid` and made 1021003857 of them. `format=None` is a client saying *your
   choice* and was refused by name, which is `format=jpgpng` above repeated one client
   later. And an unserved operation answered with an empty-bodied 404, which Pro asked
   for forty times in a single workflow. All four were found by replaying Pro's own
   request rather than by reading the code, which is the part that generalises: a parser
   that reads one documented spelling refuses the other with a correct sentence, and a
   test written from the same reading of the specification agrees with it.

   **The request came from a proxy, and the proxy was unnecessary** — the server's own log
   had every one of these requests, and the file that looked empty was a stale one at a
   path the development script computed wrongly. That was [D-138](../architecture-debt.md),
   written and withdrawn on the same day.

   **It was discharged with a hole in it for part of a day, and the hole is worth
   leaving in the record.** Pro's raster reader refuses an image service whose
   `capabilities` does not contain `Tilemap`, and this face had no route for that word; the
   probe above first ran with it temporarily typed in and nothing behind it, which is
   precisely the state condition 5 exists to keep out of a release. It was taken back out,
   ADR-044 was written and accepted, the scheme and the two routes were built, and the
   probe was run again against the served version. **That second run is the one this
   discharge rests on** — [ADR-044](ADR-044-tiles-are-served-because-the-claim-had-to-be-true.md)
   condition 6, and [Q-134](../open-questions.md) for the measured grid.

2. **The warp's error is measured and stated, not assumed.** Control-point
   interpolation is an approximation; the condition is a number in
   `benchmarks/` saying how far a pixel can land from where it belongs, at the
   grid density shipped.

   **DISCHARGED 2026-08-21.**
   [benchmarks/raster-warp/RESULTS.md](../../benchmarks/raster-warp/RESULTS.md):
   at the shipped density the worst pixel lands **0.0223 coverage pixels** from
   where it belongs, and the error falls by four for every doubling of the grid —
   which is what bilinear interpolation of a smooth function does, and is the
   check that the arithmetic is right rather than merely small.

   **Measured against the closed form of EPSG:3857 rather than against PostGIS**,
   deliberately: measuring against the projection library would mix its own datum
   handling into the number this condition is about. And the harness is a test
   rather than a script, so the claim fails the build when it stops being true —
   [D-30](../architecture-debt.md) is a debt row about benchmarks nobody re-runs.

   **What it does not cover is stated in the results**: other projections curve
   harder than Mercator, and the nearest-neighbour resampler's own half-pixel
   dominates this figure anyway.

3. **`exportImage` is bounded the way `GetMap` is.** The same image ceiling, the same
   connection budget, the same deadline. A raster request can be arbitrarily more
   expensive than a vector one at identical pixel dimensions, and
   [D-128](../architecture-debt.md) already records that the budget does not shed load
   on every path.

   **DISCHARGED 2026-08-21, and it inherits D-128 rather than escaping it.** The image
   ceiling is `MaximumImageWidth`/`Height`, named in the refusal rather than clamped
   silently. The deadline is the pipeline's, so it applies without this face doing
   anything. The budget is `ConnectionBudget`, entered on a key of
   `coverage:{path}` — that class is named for what it first bounded, and what it is
   is an admission gate with a per-source and a per-worker limit; a coverage is a
   source. Taken **before** the canvas is allocated, because a 4096² canvas is 64 MB
   and admitting a request to then refuse it would have paid the largest single cost
   of serving it.

   **What it does not do is refuse, and that is D-128 exactly.** Measured: a 2048²
   export costs 0.39 s alone, 0.98 s at concurrency 24 and 2.38 s at 48 — every one a
   200, because slots free inside the five-second wait. The gate bounds the wait for a
   slot and not the hold after admission, which is the finding performance gate 2 made
   about the query faces and which is now confirmed on this one. **The condition asked
   for the same bounds `GetMap` has and this face has them, weakness included**;
   fixing the weakness is D-128's job and needs its own decision.

4. **The architecture test covers the new adapter before the adapter has a second
   caller.** SkiaSharp's containment is enforced; the TIFF reader's must be enforced the
   same way and on the same day, because a port with one implementation and no test is
   a port by intention only.

   **DISCHARGED 2026-08-21**, the same day the adapter landed.
   `NativeDependencyTests` carries `BitMiracle.LibTiff` beside `SkiaSharp` and checks
   it in both directions — no other project references the package, and
   `Graticula.Raster.Tiff` still does.

5. **The near-free operations are not claimed until they are served.** ADR-009's table
   listed six of them as almost free and §2 above shows they are not. The service
   document must not advertise a capability this face does not answer — which is
   correctness gate 2's finding 5, and it cost that gate a `Map,Query,Data` string that
   was untrue.

   **DISCHARGED 2026-08-21, and the discharge was tested on 2026-08-22 by something
   wanting to be claimed.** The capabilities string is `Image` and nothing else, and the
   flags beside it say what they mean: `allowRasterFunction` false, `supportsStatistics`
   false, `supportsAdvancedQueries` false. `hasColormap` and `hasMultidimensions`
   likewise. **Asserted from outside rather than by reading the code** —
   `ImageServerConformanceTests` reads the document a client reads.

   **`allowAnalysis` changed from false to true and that is not a weakening of this
   condition.** It reads as *this server performs analysis* and it means *this service
   may be used as input to analysis*: its pixels can be read for an arbitrary extent,
   size and reference, which is what `exportImage` does. False was a cautious guess about
   somebody else's vocabulary. `allowRasterFunction` stays false and is the real limit,
   because that is the flag that would claim server-side function chains.

   **`Tilemap` is claimed now and the condition is why it took a day longer than typing
   it.** ArcGIS Pro's raster reader gates on that word ([Q-134](../open-questions.md)) and
   never calls the operation, so claiming it would have worked and nobody would have
   caught it. This face had no `tilemap` route, no tiling scheme and `tileInfo: null` — a
   client reading the claim would have had no scheme in which to name a tile, which is not
   merely unserved but unaddressable. **The operation is served instead**:
   [ADR-044](ADR-044-tiles-are-served-because-the-claim-had-to-be-true.md), the owner's
   decision, amending §3.2's first cut.

   **The test was tightened twice on the way and the second time is the one that
   matters.** First from `Contains("Image")` to `Assert.Equal("Image", claimed)`, because
   the old assertion would have passed `Image,Tilemap` without noticing — a condition
   whose test tolerates the thing it forbids is not discharged, it is unmeasured. Then to
   a second test that splits the string and asks each word to answer over HTTP: `Image`
   must return a PNG, `Tilemap` must have a populated `tileInfo` and answer a `tilemap`
   request, **and a word the test does not recognise fails it.** Reading a claim out of a
   document and comparing it with a string proves the claim was made, not that it was
   true, and that gap is exactly the one `Map,Query,Data` stood in.

6. **`NOTICE` carries the three attributions** — BitMiracle, IJG and libtiff — before
   anything ships that links the reader.

   **DISCHARGED 2026-08-21, with the notices reproduced rather than named.** A BSD-3
   clause requires the copyright notice, the conditions and the disclaimer to appear in
   the materials provided with a binary distribution; pointing at
   `DEPENDENCY-LICENSES.md`, which records *which* licence applies, discharges none of
   that. `NOTICE` now carries all three in full, and SkiaSharp's MIT text with them for
   the same reason.

   **And it was still called `gis-server`.** The product was renamed on 2026-08-17 and
   [ADR-032](ADR-032-the-product-is-named-graticula.md) §5 lists the two places the old
   name survives — the `GisServer:*` configuration keys and the `gisserver` default
   schema. `NOTICE` is neither: it is the product's own identity, in the file that
   travels with every copy. Four days, and it took a licensing condition to notice.

## 6. What this does not decide

Raster function chains, dynamic mosaicking, the mosaic dataset model, WCS, OGC API
Coverages, and whether STAC (ADR-009 §2.3) becomes the catalogue model for collections
of imagery rather than for single rasters. **Each is its own decision** and ADR-009 §0's
warning applies to all of them: decomposed per operation, not accepted whole.
