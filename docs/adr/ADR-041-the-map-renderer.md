# ADR-041 — The map renderer, and the two faces that hang off it

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM-HIGH` for the placement and the library · `MEDIUM` for labels · **`HIGH` for throughput, measured 2026-08-20** — [benchmarks/map-rendering](../../benchmarks/map-rendering/RESULTS.md) |
| **Decided** | 2026-08-20 |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-004](ADR-004-rendering-engine.md) — un-defers it, and records that it is being un-deferred against a preference the owner stated in its own §0 |

---

## 0. This reverses something the owner said, and that is stated first

**2026-08-13, recorded in [ADR-004](ADR-004-rendering-engine.md) §0 and
[Q-47](../open-questions.md):**

> *"I hate WMS. Super slow. Prefer ArcGIS MapServer capability."*
> *"We can design a better symbology."*

WMS was not cut for scope. It was **rejected on its merits**, and ADR-004 §0 exists
specifically so that a future revisit would not default to *add WMS*.

**2026-08-20, owner decision:** *"wms'e başlayalım."* Asked directly whether that
displaced the recorded preference, the owner chose **both faces off one renderer**.

So the reversal is partial and the earlier statement survives inside it. The
preference was never *no rendering* — it was *not only WMS*. What is decided here
gives the WMS clients their surface and the ArcGIS-shaped rendered service the same
day, from the same pixels. The 2026-08-13 sentence stays in ADR-004 §0 rather than
being edited away, because a preference that changes is worth being able to see
change (CLAUDE.md §2).

## 1. Context

**We publish services and cannot draw them.** There is no rasterisation anywhere in
this repository — no PNG writer, no canvas, no glyph. Three protocol faces exist and
every one of them hands geometry to somebody else's renderer:

| Face | What it returns | Who draws it |
|---|---|---|
| FeatureServer | JSON features | the client |
| VectorTileServer | MVT, built by PostGIS `ST_AsMVT` | the client, via MapLibre |
| WFS | GML or GeoJSON | the client |

That is [ADR-004](ADR-004-rendering-engine.md)'s vector-first posture working as
designed, and it has a cost the same document names: **a client that cannot render
cannot use this server at all.** Desktop GIS pointed at a WMS URL, an old web
application with a WMS layer, a print pipeline, a thumbnail — none of them is served
by any face we have.

**What has changed since the deferral, and it is one fact rather than a mood.**
ADR-004 §1 costed server-side rendering as *"either MapLibre Native with its headless
GPU-context problem in containers — which collides directly with air-gapped
deployment — or writing our own style interpreter and rasteriser."* That is a true
statement about those two options and a false dichotomy about the problem. **A CPU
rasteriser is a third option**, it needs no GPU context, and §4 records a peer at
roughly 40× our size taking exactly it.

**And the symbology this needs already exists.** ADR-004 §0 said that if the ADR were
ever un-deferred *"the symbology model is the interesting part and belongs in it"* —
and [ADR-033](ADR-033-symbology.md) went and built it on 2026-08-17, for a different
reason. There is a canonical document per layer, stored, size-bounded by a check
constraint, readable from a MapLibre style or an Esri `drawingInfo`. **The
interesting part is done and this decision inherits it**, which is most of why this
is now a smaller decision than ADR-004 assumed.

## 2. The questions this answers

1. Do we render at all, and behind which faces?
2. What rasterises, and where does it live?
3. Which style language — and the answer is *the one we already have*, which needs
   defending rather than asserting.
4. Labels, or not.
5. Which WMS versions.

## 3. Alternatives

### Alternative A — stay deferred, and answer the WMS estate with *use vector tiles*

**Argument for.** It is the position this project held for eight days, for reasons
that were good: vector-first removes cross-tile label consistency (Q-26), removes
fonts and glyph packs from the air-gap checklist (Q-15), and keeps the CPU profile of
the serving process flat. [benchmarks/mvt-generation](../../benchmarks/mvt-generation/RESULTS.md)
run 3 measured **80.9% GC pause at 18% CPU** on a workload lighter than rendering,
and ADR-007 §4.14 records that worker sizing has no allocation term. Rendering is the
allocation-heaviest thing a GIS server does, and we would be adding it to a runtime
already measured misbehaving under less.

**Argument against.** *Use vector tiles* is not an answer to a client that speaks
WMS; it is a request that the client be replaced. The estate this product exists to
catch — [Q-49](../open-questions.md), the ones already falling off ArcGIS and the far
larger group who never could afford it — is full of tools whose only map protocol is
WMS. And the owner has now asked twice, in two directions, for the capability under
this ADR.

### Alternative B — MapLibre Native, headless

**Argument for.** Our canonical style *is* a MapLibre style ([ADR-033](ADR-033-symbology.md)).
The renderer that interprets it best is the one that defines it, and everything we
would otherwise implement — paint properties, expressions, sprite handling — arrives
finished and exactly consistent with what the vector-tile face already serves.

**Argument against, and it is decisive.** It needs a GL context. In a container
that means EGL, a software GL stack or a GPU, all three of which are a new line on
the air-gapped deployment checklist and one of them is a hardware requirement. It is
also a C++ build we would carry, for a platform matrix we do not control. ADR-004 §1
named this cost accurately and it has not moved.

### Alternative C — write the rasteriser ourselves

**Argument for.** No dependency at all. Complete control of the pixel.

**Argument against.** [build-vs-adopt-policy.md](../build-vs-adopt-policy.md) already
answers this: rasterisation is **Tier 2**, where established libraries are permitted
behind our own port. Writing a scanline rasteriser with anti-aliasing, dash patterns,
joins, caps and — the part nobody costs correctly — text shaping and font fallback is
months of work to arrive at something worse than a mature library, in a layer that is
not where this product's argument lives. The cartography *is* ours and stays ours;
the triangle filling is not.

### Alternative D — SkiaSharp behind our own port (chosen)

**Argument for.**

- **CPU, no GL context.** The objection that deferred ADR-004 does not apply.
- **`SkiaSharp.NativeAssets.Linux.NoDependencies`** carries its own font and graphics
  stack, so an air-gapped image needs no system packages. This is the specific
  question ADR-004 §0 raised under *fonts and glyph packs enter the air-gapped
  checklist* and could not answer.
- **MIT**, over Skia's BSD-3. Nothing propagates to anyone downstream. The
  alternative managed library, ImageSharp.Drawing, ships under the Six Labors Split
  License — free to us as an open-source project, **payable by a commercial user who
  redistributes us**, which would attach to this product a constraint
  ~~[CLAUDE.md](../../CLAUDE.md) §7 says it does not have~~ **§7 said it did not
  have — corrected 2026-08-25,
  [ADR-047](ADR-047-the-outbound-licence-is-elastic-2.md). The conclusion is
  unchanged and the argument is now stronger: this project is no longer open
  source, so the Six Labors free tier would not have applied to it at all.**
  SkiaSharp was chosen and ImageSharp.Drawing was not, so nothing is owed either
  way.
- **Measured in production by a peer at scale** — §4.

**Argument against.** A native binary, per-RID, in the process that answers public
requests — the first one this repository has admitted there. ADR-009 §2.2's rule for
GDAL is that an untrusted-file parser does not belong in the serving process, and a
reasonable person could extend that to any native code. The distinction being drawn
is that Skia here parses **our own** geometry and **our own** style document, never a
file a caller uploaded, and the alternative to it is not *no native code* but *a GL
context*.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| A peer implementation at ~40× our size chose SkiaSharp for exactly this | `SkiaSharp 3.119.2`, with both `NativeAssets.Linux` and `NativeAssets.Linux.NoDependencies`, in their central package versions. Read 2026-08-20 | The anonymised reference checkout, per [ADR-030](ADR-030-reading-the-reference-implementation.md); logged in [reference-reading-log.md](../research/reference-reading-log.md) |
| One renderer can feed every rendered face, so building it once is not an optimism | In that same checkout the library's types appear in WMS `GetMap`, WMS `GetLegendGraphic`, MapServer `Export`, OGC API `TileRenderer`, ImageServer legend, a print layout composer and a static-map handler — **file names only; no rendering source was opened** | as above |
| Their placement is what our policy forbids, so it is a contrast and not a template | Those types reach twenty files including the protocol handlers. [build-vs-adopt-policy.md](../build-vs-adopt-policy.md) §4: no library type in a Tier 1 signature | as above |
| The style model this needs already exists and is stored | [ADR-033](ADR-033-symbology.md); `SymbologyConversion` reads a MapLibre style or an Esri `drawingInfo` into one canonical form, bounded at 262,144 characters by migration 23 | this repository |
| The data path already exists | `FeatureQuery` carries `BoundingBox`, `OutSrid`, `MaxAllowableOffset` and `Fields` — extent, projection, simplification tolerance and the columns a style needs, which is precisely a render request | `src/Graticula.Core/Features/FeatureQuery.cs` |
| Rendering throughput and allocation on this runtime | **Absent.** §7 condition 1 | — |

**The last row is why the confidence line says `LOW` for throughput.** ADR-004's
strongest surviving objection is an allocation measurement, and this decision does
not answer it — it schedules answering it.

## 5. Decision

**Build one renderer, in our own cartographic vocabulary, and hang two protocol
faces off it.**

### 5.1 Placement

```
Graticula.Core/Cartography      Tier 1. The port and the cartography:
                                what a map request is, what a resolved symbol is,
                                where a label goes, what a legend entry contains.
                                No package references, ever.

Graticula.Render.Skia           Tier 2 adapter. The ONLY project in this repository
                                that may reference SkiaSharp. Implements the port.

Graticula.Api.Wms               WMS vocabulary: request parsing, capabilities,
                                service exceptions, GetFeatureInfo documents.

Graticula.Api.ArcGis            MapServer export and identify, in the existing
                                ArcGIS project.

Graticula.Host                  Routes, and the only place the three meet.
```

**No Skia type crosses into Tier 1**, and that is asserted rather than intended: the
architecture suite gains a rule confining `SkiaSharp*` to one named project, in the
shape `NativeDependencyTests` already uses for GDAL and NetTopologySuite. It is a
*different* rule from those two, because those are confined to a child process and
this one must be reachable from the host — so it is a second list rather than a
third entry in the first.

### 5.2 The style is ADR-033's canonical document, and there is no second one

**No SLD, no SE, no CSS, no YSLD.** [competitive-position.md](../competitive-position.md)
§6 lists them under *genuinely absent, genuinely deliberate*, and ADR-004 §0's *"we
can design a better symbology"* is a decision that has already been executed. A
`STYLES` parameter naming anything other than the empty string or the layer's default
is **refused**, not approximated — the same rule WFS applies to a version it does not
speak, and for the same reason: a client that asks for a named style and receives the
default cannot tell that apart from a server that ignored it.

### 5.3 The data path is the one we already have

A render request becomes a `FeatureQuery`: `BoundingBox` from the requested extent
**buffered** by the largest symbol the style can produce, `OutSrid` from the requested
CRS, `MaxAllowableOffset` set to one pixel in map units, `Fields` restricted to the
columns the style and the labels actually read. Nothing new is queried and nothing
new is cached.

**The buffer is not a detail.** A symbol whose centre is outside the extent still
paints inside it, and a renderer that queries the bare extent draws a map with
objects missing along every edge — visible only when the client tiles its requests,
which is how every WMS client works.

### 5.4 WMS 1.3.0 and 1.1.1, and the axis order between them

Both, by owner decision. They differ in the thing most likely to be got wrong:

| | 1.3.0 | 1.1.1 |
|---|---|---|
| CRS parameter | `CRS` | `SRS` |
| `EPSG:4326` axis order | **latitude first** | longitude first |
| Exception document | `ServiceExceptionReport` 1.3.0 | `ServiceExceptionReport` 1.1.1 |

This is the same trap WFS 2.0 already carries — `urn:ogc:def:crs:EPSG::4326` is
latitude-first there — so the rule is shared rather than reimplemented: **one place
decides whether a CRS is latitude-first, and both surfaces ask it.**

Operations: `GetCapabilities`, `GetMap`, `GetFeatureInfo`, `GetLegendGraphic`. The
last is not in the WMS core — it arrives from the SLD profile — and it is built
anyway, because every client that draws a legend asks for it and a server without it
looks broken rather than minimal.

### 5.5 The ArcGIS face

`MapServer/export`, `MapServer/identify` and `MapServer/legend`, plus the service and
layer documents the directory already knows how to render. This is the shape the
owner preferred on 2026-08-13 and it costs almost nothing once the renderer exists:
a different spelling of extent, size, format and layer list.

**Built 2026-08-20, the same day as WMS**, and the two faces share the fetch and the
draw — `WmsEndpoints.DrawLayerAsync` — rather than each having their own. Two
rendered faces that both fetched and drew would eventually draw differently, and the
person who found out would be a user comparing them. `Export_and_wms_draw_the_same_map`
asserts they are byte-identical.

**Two things this face does that WMS does not have to.** `export` answers `f=json`
with an address rather than an image, because that is what the JavaScript API places
an element from — and the address has to be one the same client can fetch. And
`legend` returns its swatches inline as base64, because a client drawing a table of
contents otherwise makes one request per layer for a few hundred bytes each.

**Three refusals are deliberate.** `layers=hide:`, `include:` and `exclude:` are
refused rather than read for their ids: each means something different about which
features are drawn, and a server that took the numbers and dropped the verb would
draw the exact opposite of what `hide` asked for. `bboxSR` differing from `imageSR`
is refused rather than reprojected, because an extent transformed without saying so
produces an image of somewhere near where it was asked for. And `geometryType` other
than a point is refused rather than reduced to its centre.

**This does not reopen [Q-17](../open-questions.md)'s exclusion of ImageServer.**
MapServer draws features we already serve; ImageServer serves raster data
([ADR-009](ADR-009-raster-engine.md)) and nothing here brings raster into scope.

### 5.6 Labels are in, by owner decision, and the honest limit is stated

Labels are placed **within one request**: a greedy pass in draw order, each candidate
rejected if its box overlaps one already placed.

**What that does not give.** Two clients requesting adjacent extents get two
independent placements, so a client tiling its WMS requests will see a label appear
in one tile and not its neighbour, or drawn twice at a seam. That is [Q-26](../open-questions.md)
returning — ADR-004 §0 predicted it would — and it is **not solved here**. The buffer
in §5.3 narrows it; it does not close it. Recorded as a limit rather than discovered
as a bug.

## 6. Consequences

**Positive.** The first face on this server that a WMS-only client can use, which is
most of the estate GeoServer holds. [competitive-position.md](../competitive-position.md)
§6a's claim that this capability is what would make *"better capabilities than
GeoServer"* true stops being hypothetical. ADR-033's symbology becomes load-bearing
rather than descriptive — until now nothing in this repository ever *drew* from it,
so its fidelity was asserted and never tested. And the ArcGIS MapServer face arrives
with it.

**Negative.** A native dependency in the serving process, for the first time. An
allocation profile on a runtime already measured at 80.9% GC pause under lighter
load. Q-26 reopens. Fonts enter the air-gap checklist (Q-15), narrowed but not
removed by the `NoDependencies` package. And **v2 grows a third item while v1's
carried debts are open** — the same cost [ADR-039](ADR-039-wfs-is-the-first-surface-after-v1.md)
§1 and [ADR-040](ADR-040-the-portal-surface-is-how-arcgis-pro-connects.md) §5
recorded, taken a third time, and recorded rather than absorbed.

**Ports created.** One: the drawing surface, in `Graticula.Core/Cartography`.

## 7. Conditions

1. **DISCHARGED 2026-08-20.** [benchmarks/map-rendering](../../benchmarks/map-rendering/RESULTS.md):
   GetMap at 1024² and 4096² over two real layers, at one and at eight concurrent
   requests, with the server's own runtime counters read either side.

   **ADR-004's objection was 80.9% GC pause. The measurement is 0.1% to 2.3%** — off
   by a factor of forty, in the direction that makes this decision safe.

   **And the benchmark found something the ADR had backwards.** §5 assumed rendering
   would be the expensive half and built the pipeline to avoid allocating per feature
   for that reason. Drawing `tr_ilce` as a 1024² map costs **426 ms and 4.7 MB**;
   serving the *same features* as FeatureServer JSON costs **808 ms and 11.8 MB**. The
   map is cheaper than the face this server already had, because it never serialises a
   coordinate to text. Sixteen times the pixels costs 2.6× the time, so the rasteriser
   is not the bottleneck — the query is, and always was.

   The buffers were the right call for the wrong reason, which is recorded rather than
   quietly enjoyed.
2. **DISCHARGED 2026-08-20. `WmsConformanceTests`, eighteen tests against the live
   process.** Every published layer draws; both versions' documents carry what their
   own version requires; a non-square extent is transposed between 1.3.0 and 1.1.1
   and produces byte-identical images; and a refusal of each kind returns the
   exception code that names it.

   **It earned its place before it was written.** Three defects were found on the day
   this surface was built and not one of them is visible in a unit test of the writer
   that produced it:

   - **Every 1.3.0 refusal answered HTTP 500.** The exception report opened its root
     element in no namespace and then declared one as an attribute, which `XmlWriter`
     refuses. Found by sending one bad `TIME` value at a running server; nothing had
     ever exercised a refusal path, so the surface looked healthy from every angle
     anyone had looked from.
   - **Every capabilities document declared `encoding="utf-16"`.** `XmlWriter` over a
     `StringBuilder` reports the buffer's encoding, not the wire's.
   - **`GetFeatureInfo` returned features with no attributes.**
     `FeatureQuery.Fields` empty means identity and geometry only, and an empty list
     is what a caller gets by not thinking about it.

   **And a fourth, found by the suite itself:** two layers here are horizontal lines,
   so their extent has zero height, and a client using the published bounding box as
   its `BBOX` was refused for a request it had every reason to believe in. Extents are
   padded now.
3. **DISCHARGED 2026-08-20, and by pixels rather than by eye.** The condition asked
   for a visual comparison; `A_drawn_map_uses_the_colour_the_tile_face_publishes`
   reads the fill colour the **tile face** publishes for a layer and requires the
   **drawn map** to contain pixels of it. The same question, with an answer that
   survives being run again.

   **It failed twice before it passed, and neither failure was the renderer.** The
   first version drew one layer at 128² over the whole country and found nothing —
   correctly, because that layer's fifty polygons are sub-pixel at that scale and
   anti-alias away to nothing. The second walked every service and was failed by a
   registered service that has no tile face at all. Both are recorded because the
   pattern keeps recurring here: a test that can fail for a reason unrelated to what
   it asserts is a test its reader learns to ignore.
4. **DISCHARGED 2026-08-20.** `NativeDependencyTests.A_ported_library_is_referenced_by_its_adapter_and_no_other`
   asserts both directions: no other project references `SkiaSharp*`, and
   `Graticula.Render.Skia` still does — so the rule cannot pass by the adapter having
   been renamed away. It is a **second list** beside the confined dependencies rather
   than a third row in the first, because GDAL and NetTopologySuite are additionally
   kept out of the serving process and a rasteriser cannot be.
5. **DISCHARGED 2026-08-20**, and asserted from both sides.
   `A_transparent_png_is_transparent_and_a_jpeg_is_a_jpeg` asks for an extent with no
   data and requires a 200 with a transparent PNG;
   `A_bad_parameter_is_refused_with_the_code_that_names_it` asks for seven kinds of
   broken request and requires the exception code that names each. **The second half
   was not academic**: every 1.3.0 refusal was answering HTTP 500 when that test was
   written, so the failure mode this condition exists to prevent was present and
   invisible.
6. **DISCHARGED 2026-08-20 by running it. `ogccite/ets-wms13`: 187 passed, 6
   failed.** The result is recorded here whichever way it went, which is what the
   condition asked for.

   **One failure was ours and is fixed.** `capability-onlineresource`: WMS 1.3.0
   (OGC 06-042) §6.3.3 requires an HTTP GET `OnlineResource` to be a **URL prefix** —
   it must end in `?` or `&` so a client concatenates its parameters onto it without
   deciding whether a separator is needed. This server published a bare
   `https://host/wms`, which works with every client that adds the `?` itself and
   produces `/wmsservice=WMS` in one that does not. It ends in `?` now, and the count
   went from 184/7 to 187/6.

   **The remaining six are one cause, and it is not a defect here.** `transparent-true`,
   `no-bgcolor`, `blue-bgcolor`, `bbox-pixel-interpretation`, `bbox-exponential` and
   `std-data-queryable` all send `LAYERS=` **empty**, because the CTL suite names the
   CITE reference dataset's own layers — `streams`, `lakes`, `ponds`, `bridges`,
   `road-segments`, `divided-routes`, `buildings`, `map-neatline` — and this server
   publishes none of them. Read out of the suite's own `basic.xml` and confirmed in
   two run logs rather than inferred. **The behaviours those six test were checked by
   hand and are right**: no `BGCOLOR` gives white, `BGCOLOR=0x0000FF` gives blue, and
   `TRANSPARENT=TRUE` gives alpha 0.

   **So `areCoreConformanceClassesPassed` is false and the honest reading is *not
   yet*, not *no*.** Publishing the CITE dataset and re-running is what turns six
   unrun assertions into a real number, and it is the next thing this condition wants
   rather than something it excuses.

   **Two things the run needed that are worth writing down**, because both cost time
   and neither is in any documentation: the container resolves the host only with
   `--add-host DESKTOP-M804G0L:host-gateway`, and the CTL suite uses Java's default
   trust store — unlike `ets-wfs20` — so the development certificate has to be
   imported with `keytool -importcert -cacerts` or every test dies on a PKIX error
   while reporting *0 tests run, core conformance passed*.

   **Re-run 2026-08-23 with the recommended profile as well, which the 2026-08-20 run
   left out: 191 of 202, and five of the eleven failures were new information.**
   `recommended=recommended` adds assertions the specification recommends rather than
   requires — service keywords, contact information, a layer abstract, a layer keyword
   list and a `MetadataURL` on every named layer — and this document carried none of
   them. **Not running the recommended profile is how a document comes to be missing
   five things nobody had noticed**, and it costs one query-string parameter.

   **Three were free, because the server already knew the answer.** 191 became 194:

   - **Service keywords.** `WMS`, `GetMap`, `GetFeatureInfo`, `vector`, `PostGIS`. The
     temptation is *GIS, maps, spatial* — words true of every server of this kind and
     therefore useless for telling one from another. These name operations a client can
     call.
   - **A layer abstract**, derived rather than typed: *Polygon features held in
     EPSG:3857, drawn with this layer's own stored symbology. GetFeatureInfo answers for
     this layer.* **The point is not the assertion, it is a layer picker**: a title of
     `tr_il` tells a person nothing, and the geometry, the reference system and whether
     it has a time dimension tell them whether it is the layer they want. Every clause is
     read off the layer, so it cannot become false without the layer changing.
   - **A layer keyword list**, from the same facts.

   **Contact information is a deployment's, so it is configuration and it is absent by
   default.** `Graticula:WmsContactPerson` and its four siblings. **The cheap way to pass
   that assertion is to write something plausible, and that is worse than failing it** — a
   client that reads an address and finds nobody there has been actively misled, while one
   that finds no address knows to look elsewhere. **Measured both ways rather than argued:
   with the settings supplied the same run reports 195 of 202**, so what fails by default
   is a choice and not an omission.

   **`MetadataURL` is not implemented and the reason is the same reason, stated once.**
   The element carries a mandatory `type` of `ISO19115:2003` or `FGDC:1998`, and this
   server has no document of either kind to point at. Pointing `DescribeFeatureType` or a
   REST page at it and declaring a standard it does not follow would satisfy a suite and
   lie to a harvester. **It stays failed until there is something true to put there.**

   **The six dataset failures are unchanged and are now proven rather than read.** The
   2026-08-20 note inferred the cause from the suite's `basic.xml`; the 2026-08-23 run's
   own request records show it — `LaYeRs=` empty for `no-bgcolor`, `LaYeRs=,,,,,,,` for
   `bbox-exponential`, eight empty names because the suite substituted its own eight layer
   names and found none of them. This server answered with a `ServiceExceptionReport`,
   correctly, and the suite's image parser reported *No image handlers available for the
   data stream*. **So the failure is upstream of anything this server does**, and
   `std-data-queryable` has no message at all because it depends on those.

   **`WmsCapabilitiesRecommendationTests` asserts the three that were implemented**,
   including that a layer's abstract names that layer's own reference system — the
   assertion that stops an abstract from becoming one sentence repeated, which would pass
   a presence check and help nobody. Evidence:
   [cite-wms13-2026-08-23.rdf](../reviews/cite-wms13-2026-08-23.rdf).

## 8. Assumptions

| ID | Assumption | Status |
|---|---|---|
| A-076 | A CPU rasteriser meets the latency a WMS client expects at typical sizes on this runtime, without the allocation profile that ADR-007 §4.14's worker sizing cannot account for | `UNVALIDATED` — condition 1 |
| A-077 | ADR-033's canonical document carries enough to draw a map that a cartographer would accept, rather than enough to describe one | `UNVALIDATED` — condition 3. It was designed to be *derived into* two protocol documents, never to be *executed* |

## 9. Revisit triggers

- **Condition 1's benchmark comes back bad.** Then the question is not the library
  but the placement: rendering moves to a worker, which is ADR-037 territory and a
  different decision.
- **Somebody asks for raster layers in a map.** That is ADR-009, still closed, and
  this ADR must not be stretched to cover it quietly.
- **A client asks for a named style.** §5.2 refuses today. The moment that refusal
  costs a real user something, the question of a second style language is open again
  and it should be reopened deliberately.

## 10. Dissent

**This is the third protocol surface in three days, on a product whose §66 review
gates still stand at FAIL and whose Phase 0 debts are carried rather than closed.**
[CLAUDE.md](../../CLAUDE.md) §1 says Phase 1 does not end with any of them open, and
each new surface makes the pile they sit under larger. WFS and the portal face were
each defensible alone; the pattern of *build the next surface* three times running is
not obviously the same thing as the sum of three defensible decisions.

**And the technical objection is not disposed of, only outvoted.** ADR-004's
allocation argument is a measured number, this ADR answers it with a scheduled
benchmark, and the code will be written before the number arrives. That order is
backwards by [CLAUDE.md](../../CLAUDE.md) §3's own standard — *where is the benchmark
proving this assumption* — and the only honest defence is that the benchmark needs
the renderer to exist before it can measure one. That defence is real and it is also
exactly what every unmeasured decision says.
