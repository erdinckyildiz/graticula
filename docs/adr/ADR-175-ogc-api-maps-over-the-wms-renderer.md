# ADR-175 — OGC API Maps, over the WMS renderer

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` — a map drawn here is byte-identical to the WMS GetMap for the same box, size and reference (asserted by a conformance test), and OGC's executable suite runs nightly with every remaining failure named; the standard is approved (OGC 20-058, 2025) but no client has yet been pointed at this face |
| **Decided** | 2026-10-06 by owner decision (*"hadi ogc yi de bitirelim"*, choosing Records, Maps, Styles and Processes); the design below is INFERRED where marked |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [v1-scope.md](../v1-scope.md) §3d, by owner decision |

---

## 1. Context

On 2026-10-06 the owner asked to finish the OGC faces (*"hadi ogc yi de bitirelim"*) and, offered the choice, took
the measured gaps together with the four OGC APIs not yet served: Records, Maps, Styles and Processes. This ADR is
Maps.

OGC API – Maps 1.0 (OGC 20-058) is WMS's GetMap rewritten as resources: a collection has a `/map`, the dataset has a
`/map`, and the request is `bbox`, `bbox-crs`, `crs`, `width`, `height`, `bgcolor`, `transparent` and `f`. This server
already draws every layer it serves through WMS — vector layers by their symbology, image services by their
rendering rule (ADR-162) — with sharing, scale ranges, time and the image limits applied there.

## 2. Alternatives considered

### Alternative A — A second grammar over WMS's own GetMap (chosen)

**For.** A map request is read in OGC API's terms, refused in OGC API's terms (RFC 7807 problems), and then handed to
WMS as a 1.3.0 GetMap with the same limits (`WmsEndpoints.LimitsFor`). The collections are the layers WMS's
capabilities list (`WmsEndpoints.PublishedAsync`). So the two faces cannot disagree about what may be drawn, or how:
a symbology change, a scale range, a stopped service or a sharing change reaches both at once.

**Against.** Whatever WMS cannot draw, this face cannot either — an antimeridian-crossing box, a CRS with void areas,
TIFF output.

### Alternative B — A renderer of its own for this face

**For.** Free of WMS's 1.3.0 vocabulary and axis-order rules.

**Against.** Two renderers drift. The project has paid for that shape repeatedly (D-130's propagation failures);
nothing in the standard needs a picture WMS could not draw.

## 3. Counterarguments to the preferred option

*A face that only re-spells another adds a surface without adding a capability.* True of the pixels, and the client
side is thin: GDAL's `OGCAPI` driver reads 1.0's `map` links (read in GDAL's source on 2026-10-06; QGIS opens a raster
through it), while GDAL 3.6 — the one OSGeo4W ships with QGIS 3.28 here — implements a pre-1.0 draft whose map
resource is a JSON document of styles, and does not open this face. What it costs is one file and a shared helper,
because the renderer is not duplicated; what it buys is a standard that is approved and a suite that measures it.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| A map here is WMS's map | `AMapIsTheSamePictureWmsDraws`: the PNG for a collection, box and size equals the WMS 1.3.0 GetMap's byte for byte | `tests/Graticula.Conformance.Tests/OgcMapsAndProcessesConformanceTests.cs` |
| OGC's suite passes what is claimed | `ets-ogcapi-maps10` at `83d2560`, run as its own jar: **13 passed, 4 failed, 7 untested** of 24 methods | `tools/cite-run-maps.sh`, `tools/cite-baselines.json` |
| The four failures are not this face's claims | `verifyTilesetsLink`, `verifyTiffContent` and `verifyCorsSupport` test classes not declared (the suite runs them regardless); `verifyBackgroundMapSuccess` asks a fully transparent pixel to carry `bgcolor`'s RGB, which a premultiplied PNG cannot | the suite's own source, read 2026-10-06 |
| No TEAM Engine image exists | Docker Hub `ogccite` lists no maps image; the repository has no Dockerfile | measured 2026-10-06 |

The first run against the face as first written was 8 passed, 9 failed. Five were this server's and were fixed:
`Content-Crs` was written in Features Part 2's angle brackets; the landing page had no `extent`
(`/req/dataset-map/desc-extent`); `collections=` did not take a collection's full address; `bgcolor` did not take W3C
colour names; and the `bbox` refusal checked the corners after `Envelope` had already reordered them, so
`3,2,1,0` drew `1,0,3,2`.

## 5. Decision

OGC API Maps 1.0 is served at `/ogc/maps/v1` — landing page, `/conformance`, `/api` (OpenAPI 3.0.3),
`/collections`, `/collections/{id}`, `/collections/{id}/map` and `/map` — governed by filtering like every standard
face. Conformance claimed: `core`, `dataset-map`, `collection-map`, `scaling`, `spatial-subsetting`, `crs`,
`background`, `png`, `jpeg`, and Common's `core` and `collections`.

- **Collections** are WMS's published layers, named as OGC API Features names them; an image service is named by its
  WCS coverage id (`hosted__name`). *(INFERRED)*
- **Defaults** *(INFERRED)*: no `bbox` is the collection's (or dataset's) whole extent; no size is 1024 pixels on the
  longer side, the other following the box; no `crs` is CRS84; PNG is transparent unless a `bgcolor` is given, which is
  the suite's rule and OGC 20-058's recommendation, and the opposite of WMS's own default.
- **`bbox-crs`** follows its reference's axis order (EPSG:4326 latitude first, CRS84 longitude first); the box is
  reprojected into `crs` by the server's projector.
- **`Content-Crs`** is the bare URI and **`Content-Bbox`** the box in the map's reference and axis order.
- **The https spelling of `conf/core`** is declared beside the http one, because OGC's suite recognises core only in
  https. Recorded as a workaround for the suite, not a claim.
- **Not claimed:** map tilesets, styled maps, TIFF, CORS, API operations, void areas.

## 6. Consequences

**Positive.** OGC API Maps clients read every layer WMS serves, under the same sharing and limits, with no renderer
added.

**Negative.** An antimeridian-crossing box is refused rather than drawn. The face inherits every WMS limit, including
the image size ceiling. A fourth ETS-run suite must be maintained, and this one is built from source on every run.

**State.** None. Nothing is stored; collections are read from WMS's listing and every map is drawn on request.

**Ports created.** None.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | WMS GetMap is the one place a map is drawn | Held by `AMapIsTheSamePictureWmsDraws` |

## 8. Dependencies

**Depends on:** ADR-039 (WMS/WFS), ADR-162 (image services in WMS), ADR-097 (the OGC API faces' shape).

**Depended on by:** —

## Conditions

1. A client that speaks OGC API Maps 1.0 — GDAL 3.8 or later through its `OGCAPI` driver, or QGIS on such a GDAL —
   opens a collection and draws it. Not yet met: the only GDAL here is 3.6, which reads a pre-1.0 draft and refuses
   (*API MAP requested, but not available*), measured 2026-10-06.
2. The suite's remaining failures are reported upstream where they are the suite's (classes run without being
   declared, `conf/core` recognised only in https), or the baseline is lowered when the suite changes.

## 9. Revisit triggers

- OGC publishes a TEAM Engine image for `ets-ogcapi-maps10`: run it through `cite-run.sh` like the others and retire
  the jar runner.
- A client asks for map tilesets, styled maps or TIFF.

## 10. Dissent

None recorded.
