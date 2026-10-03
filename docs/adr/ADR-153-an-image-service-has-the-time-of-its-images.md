# ADR-153 — An image service has the time of its images

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `MEDIUM` — time is checked end to end; multidimensional imagery is not served |
| **Decided** | 2026-10-03 by owner decision (*"Tüm maddeleri yap"* — the imagery list's item 22) |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-152](ADR-152-a-mosaic-is-a-catalog.md) |


> **Amended 2026-10-03 by [ADR-157](ADR-157-imagery-in-other-formats-is-written-as-geotiff.md).** A NetCDF of time steps is uploaded as a dated image a step, so its time comes from its file.

---

## 1. Context

`time` was refused: an image service had no `timeInfo`. A mosaic of images taken on different days — the commonest
reason to have one — could not be put on a time slider.

## 2. Alternatives considered

### Alternative A — Each image's acquisition date, the catalog's field (chosen)

An image is dated by its owner — Studio's Settings › Images, or ArcGIS's `update` with `AcquisitionDate`. When any is,
the service has `timeInfo` over `AcquisitionDate`, its extent the earliest and latest dates. `time` on `exportImage`,
`identify`, `getSamples`, `computeStatisticsHistograms` and `query` keeps the images taken within two instants, or, for
one instant, those taken by then with the nearest on top; an undated image is in no time. A `mosaicRule` sent with
`time` orders what time keeps.

**Not this.** Multidimensional imagery — variables and dimensions in one raster, as a NetCDF carries them — is not
served: `multidimensionalDefinition` is refused, and the files that carry them are not read (item 27, a decision on
GDAL).

### Alternative B — Read the date from the file

**Against:** a GeoTIFF's `DateTime` tag is when the file was written, not when the image was taken; trusting it would put
images on the slider at the wrong time.

## 3. Counterarguments to the preferred option

- *Dating many images is by hand*, one row at a time, or by a script calling `update`.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| An image dated by `update` gives the service `timeInfo`; a day after it, `time` draws it; a day before, nothing | `MosaicCatalogConformanceTests` | this repository |

## 5. Decision

As §2.

## 6. Consequences

**Positive.** A mosaic of dated images plays on a time slider.

**Negative.** Dates are entered, not read.

**State.** In `coverage.images` (ADR-152).

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | An image's date is a day, not an interval | Stated here; ArcGIS's end-time field is not offered |

## 8. Dependencies

**Depends on:** ADR-152.

**Depended on by:** —

## 9. Revisit triggers

- Multidimensional imagery asked for.
- Images whose time is an interval.
