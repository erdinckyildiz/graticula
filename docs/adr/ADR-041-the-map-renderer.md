# ADR-041 — The map renderer, and the two faces that hang off it

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM-HIGH` for the placement and the library · `MEDIUM` for labels · `LOW` for throughput until §4's benchmark exists |
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
  [CLAUDE.md](../../CLAUDE.md) §7 says it does not have.
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

`MapServer/export` and `MapServer/identify`, plus the `MapServer` service and layer
documents the directory already knows how to render. This is the shape the owner
preferred in 2026-08-13 and it costs almost nothing once the renderer exists: it is
a different spelling of extent, size, format and layer list.

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

1. **A benchmark, under `/benchmarks`, before the confidence line moves.** GetMap at
   1024×1024 over a real layer: wall time, allocated bytes, GC pause fraction, at
   one and at eight concurrent requests. **ADR-004's surviving objection is a
   measurement and it deserves a measurement back.** Until it exists this ADR's
   throughput confidence is `LOW` and stays written that way.
2. **A conformance suite that drives the live server**, in the shape
   `WfsConformanceTests` now has: every published layer renders, the image is a valid
   PNG of the requested size, an unknown layer is a service exception rather than a
   blank image, and both versions' axis orders are asserted against a layer whose
   extent is not square — because a square extent passes with the axes swapped.
3. **A drawn map compared against the vector-tile face of the same layer**, by eye
   and recorded. ADR-033 derives two faces from one document and this is the first
   time anything has drawn either; a difference here is a defect in the derivation,
   not in the renderer.
4. **`SkiaSharp*` is confined to one project by an architecture test**, and the test
   asserts both directions — nobody else references it, and that project still does.
5. **The blank-image failure mode is refused explicitly.** A GetMap that matched no
   features returns a transparent image and 200; a GetMap that *failed* returns a
   service exception. A renderer that answers both with an empty PNG is the single
   most common way a broken map server looks healthy.
6. **OGC CITE for WMS is run and its result recorded**, pass or fail, the way
   [ADR-039](ADR-039-wfs-is-the-first-surface-after-v1.md) condition 3 was.

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
