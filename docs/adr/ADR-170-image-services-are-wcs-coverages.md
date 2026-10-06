# ADR-170 — Image services are WCS 2.0.1 coverages

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` — each operation, trimming, scaling, reprojection and the switch are checked end to end; ~~OGC's WCS 2.0 test suite has not been run against it, and no WCS client has been watched reading it~~ OGC's WCS 2.0 suite passes it with no failure (condition 1); GDAL has been watched reading it and its values match `exportImage`'s, and QGIS's own WCS dialog has been watched failing to (condition 2) |
| **Decided** | 2026-10-03 by owner decision (*"Yap"*, in answer to the three OGC items left — SLD, WCS, INSPIRE — with WCS taken first) |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-166](ADR-166-ogc-faces-are-turned-off-one-by-one.md) (*WCS is not a face here at all*); [v1-scope](../v1-scope.md) §3b, where WCS was cut with rendering |
| **Amended by** | [ADR-173](ADR-173-wcs-1-0-0-for-qgis.md) — 1.0.0 is spoken too, so that QGIS's own WCS dialog opens a coverage |


> **Note 2026-10-03.** [competitive-position.md](../competitive-position.md) §6 listed WCS as *never planned* and was not updated when this was decided; corrected with ADR-171, which found it.
---

## 1. Context

ArcGIS Server turns WCS on for an image service — `…/ImageServer/WCSServer` — and GIS clients that do not speak ArcGIS
(QGIS, GDAL, OpenLayers' readers, the modelling tools an environmental agency runs) read a raster's *values* through it,
not a picture. This server already answers those values: the image service's raw export (ADR-127), `exportImage` with
`format=tiff`. There was no OGC way in.

## 2. Alternatives considered

### Alternative A — WCS 2.0.1 over the raw export (chosen)

- **At `/wcs` and at each image service's own `…/ImageServer/WCSServer`** (ADR-162's shape), KVP GET.
  `GetCapabilities`, `DescribeCoverage` and `GetCoverage`. The server's document lists every image service the caller
  may see, started, offering WCS; a service's own lists that one, with its description, tags, fees and access
  constraints (ADR-167).
- **A coverage's id is its qualified name with `__` for the slash** — `hosted__elevation` — because a coverage id is
  an XML name and a slash is not allowed in one.
- **DescribeCoverage** is a `RectifiedGridCoverage`: the envelope in the coverage's own reference and axis order
  (`Lat Long` for EPSG:4326, `E N` for a projected one), the grid's limits, its origin at the centre of the top-left
  cell, its two offset vectors, and a `swe:Quantity` a band with its no-data value.
- **GetCoverage is the raw export.** The same read, the same warp and the same GeoTIFF as `exportImage` with
  `format=tiff`, nearest-neighbour, no raster function. It supports `subset` trimming and slicing on the two axes (any
  of their names, `*` for open), `scalesize`, `scalefactor`, `subsettingcrs` and `outputcrs` as EPSG URIs. The answer
  is at the coverage's own resolution unless a scaling names another.
- **A coverage larger than the server's image ceiling is refused, with the size** and what to do: trim or scale. It is
  never silently shrunk. ArcGIS answers the same way.
- **Profiles declared:** core, GET-KVP, GeoTIFF, scaling, CRS. Interpolation, range subsetting, XML/POST and the
  GML/multipart encodings are not declared and are not served.
- **Refusals are OWS 2.0 exception reports** with WCS's codes (`NoSuchCoverage` 404, `InvalidAxisLabel`,
  `InvalidSubsetting`, `InvalidScaleFactor`, `InvalidExtent`, `OutputCrs-NotSupported`, …). A failure no endpoint
  caught is wrapped the same way.
- **The owner turns WCS off with the other faces** — ADR-166's list gains `WCS` (migration 81) and Studio's switch
  for image services gains a row. A coverage turned off leaves the documents and is `NoSuchCoverage` by id.
- `/wcs` is in the served list the routing audit reads. Usage is counted as kind `WCS`, and the service is named by
  `coverageId`.

### Alternative B — WCS 1.0/1.1

**Against:** 2.0.1 is what current clients ask for first and what OGC's maintained suite tests. A 1.x client is rarer
than a 1.1 WFS client was (ADR-168), and nothing has asked for one.

### Alternative C — OGC API Coverages only

**Against:** it is still a draft standard, and ArcGIS offers WCS, not it. The shops this server serves have WCS
clients.

## 3. Counterarguments to the preferred option

- *Nearest-neighbour only.* A client asking for a bilinear resample is not offered one. The interpolation extension is
  not declared, and that is honest rather than complete.
- *No range subsetting.* A multiband coverage is always answered whole. A client wanting band 3 reads three bands.
- *No time or other dimensions.* A multidimensional image service (ADR-158) answers its first slice.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| The server's and the service's documents list it; DescribeCoverage's grid is 8 × 8 with its origin at 40.995 30.005, latitude first; GetCoverage answers 8 × 8, trimmed 4 × 4, scaled 16 × 16, and Web Mercator; an unknown id is `NoSuchCoverage`, an unknown axis `InvalidAxisLabel`; turned off, it leaves the document and is refused | `WcsConformanceTests.An_image_service_is_a_coverage_described_trimmed_scaled_and_answered_as_geotiff` | this repository |

## 5. Decision

As §2, Alternative A.

## Conditions

1. OGC's WCS 2.0 test suite is run against a public coverage, or the declared profiles are narrowed to what it passes.
   **DISCHARGED 2026-10-04 by running it, and nothing was narrowed.** `ogccite/ets-wcs20` now runs nightly in
   `cite.yml` against a public image service. Its first run failed 5 of 75. All five were this server's and were fixed
   in the same commit: `InvalidAxisLabel` and `InvalidSubsetting` answered 400 where WCS 2.0.1's exception table gives
   404, a second subset on one axis was intersected instead of refused (requirement 31), and a bogus `mediaType` was
   ignored (requirement 29). The run after the fixes was **80 passed, 0 failed**, and the baseline is 0.
2. QGIS or GDAL reads a coverage through WCS, and its values match `exportImage`'s. **DISCHARGED 2026-10-04,
   through GDAL and not through QGIS's WCS menu.** GDAL 3.6's WCS driver opened `hosted/ci_imagery` with
   `WCS:…/wcs?version=2.0.1&coverage=hosted__ci_imagery`, asking DescribeCoverage and two GetCoverage requests, and
   read 256 × 192 bytes at origin 30, 41 with 0.01° pixels — the same grid, statistics and checksum (47616) as
   `exportImage` over the same box with nearest-neighbour. QGIS 3.28 opened the same string through its GDAL
   provider. **Its own WCS provider — the *Add WCS layer* dialog a QGIS user actually reaches for — did not open it
   at any version setting**: it asks `DescribeCoverage` in 1.0.0 or 1.1, and this server answers those
   `VersionNegotiationFailed`. That is [Q-162](../open-questions.md), and §7's assumption is corrected below.

## 6. Consequences

**Positive.** A raster's values reach every OGC client, under the same sharing and the same switch as the other
faces.

**Negative.** A sixth OGC face to keep in step with the image service's own.

**State.** Migration 81 widens `service_ogc_off_known`.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | WCS clients ask for 2.0.1 and accept GeoTIFF | ~~True of QGIS and GDAL~~ **True of GDAL, false of QGIS's WCS provider — measured 2026-10-04.** QGIS 3.28's *Add WCS layer* speaks 1.0.0 and 1.1 only and could not open a coverage here at any version setting; QGIS reaches it only through GDAL's driver. Condition 2 and [Q-162](../open-questions.md) |

## 8. Dependencies

**Depends on:** ADR-127, ADR-162, ADR-166, ADR-167.

**Depended on by:** —

## 9. Revisit triggers

- A client that needs interpolation, range subsetting or a time axis.
- OGC API Coverages becoming a standard ArcGIS offers.
