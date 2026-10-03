# ADR-140 — Several images are one mosaic

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` — the reading is measured and checked against GDAL's format; what a mosaic should be is inferred, and listed for the owner |
| **Decided** | 2026-10-02. That mosaics come next by owner decision (*"sıra ile devam et"*, over a list naming "mosaic: several images served as one image service"); **the three choices in §2 are INFERRED** and listed as [Q-160](../open-questions.md) |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-123](ADR-123-imagery-comes-into-studio.md) (condition 2: an upload may carry several images), [ADR-139](ADR-139-an-uploaded-image-is-given-its-overviews.md) (each image of a mosaic is given its overviews) |

> **Amended 2026-10-03 by [ADR-147](ADR-147-a-mosaic-grows-and-resamples.md).** Images that do not fit the grid are resampled onto it, and a mosaic is added to after it is made.

---

## 1. Context

Imagery arrives in tiles: an orthophoto delivered as a hundred GeoTIFFs, an elevation model as a grid of sheets. Served
one service a tile, a map needs a hundred layers. ArcGIS answers this with the mosaic dataset — a catalog of rasters
drawn as one image service, with resampling, ordering methods, footprints and overviews of its own. That is a large
product; the common case of it is tiles of one image, on one grid, drawn as one.

## 2. Alternatives considered

### Alternative A — Tiles of one image, as a GDAL virtual raster (chosen)

Several GeoTIFFs uploaded together are published as one image service. The server writes a GDAL virtual raster (`.vrt`)
beside them — one grid, each file placed whole at a whole-pixel offset — and serves it through the same reader port as a
single file, so every operation (export, tiles, identify, raster functions, LERC, display rules) works on a mosaic
unchanged. A `.vrt` written elsewhere, of the same shape, can be registered in place; GDAL, QGIS and ArcGIS read the one
this server writes.

- **INFERRED: aligned tiles only.** The images must share the coordinate system, bands, sample type and pixel size, and
  sit on one grid. Anything else is refused, naming the file and the difference; nothing is resampled.
- **INFERRED: the later over the earlier**, by file name (Studio sends them sorted, so the order is one a person can
  see and set), where they overlap — except where the
  later has no data, which is transparent, so a tile's empty edge does not cover its neighbour.
- **INFERRED: made in one upload** of up to 500 images, and not added to afterwards; deleting the service deletes them.
- **Each image is given its overviews** (ADR-139); the mosaic has as many levels as the image with the fewest.

### Alternative B — ArcGIS's mosaic dataset

**For:** what an ArcGIS administrator knows. **Against:** resampling across resolutions and systems, mosaic methods,
footprints, seamlines and incremental loading are each a decision of their own, and none is needed for the tiles of one
product. They extend A rather than replace it.

### Alternative C — Merge the tiles into one file on upload

**Against:** a full rewrite of every pixel, twice the disk while it runs, and the files the owner sent are no longer the
files kept.

## 3. Counterarguments to the preferred option

- *A mosaic of small tiles is slow to see whole.* Its overviews are its tiles' own, so a mosaic of 500-pixel tiles has
  one level, and a whole view of a hundred of them reads a hundred tiles. ArcGIS builds overviews of the mosaic itself
  for this; so would a later step here — §9.
- *Two tiles meet a pixel apart at an overview*, where each one's offset is halved and rounded. Never at full
  resolution.
- *One request carries every tile*, under the upload's 4 GB ceiling.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Four tiles read as one grid at full resolution and from their overviews; the later is drawn over the earlier except where it has no data; tiles that are not one grid are refused, saying why; a VRT that crops or resamples is refused by name; a VRT written by GDAL, with a WKT reference, is read | `VrtMosaicReaderTests` | this repository |
| Two images uploaded together are one service with the union's extent, each half answering its own values, drawn whole; a tile of another pixel size is refused naming the file, and nothing is published or left on disk; deleting the mosaic deletes its images, their overviews and the VRT | `ImageryDisplayTests`, and the fixture's imagery directory read after | this repository, local fixture |
| Two 2000 × 2000 tiles: published in 1.2 s with three overview levels, drawn whole in 0.16 s | an upload and an export on the fixture | local fixture, 2026-10-02 |
| Several GeoTIFFs dropped on New item go to the imagery form together and are sent in one upload | `ImportFormTests` | this repository |
| The ux review, first pass: with 50 tiles one misfit was found only after the whole upload and every pyramid, and only the first was named; "Your device" took one file; a mixed drop silently lost files; the form did not say it took tiles; pixel sizes had no unit and no next step; the name defaulted to one tile's | fixed: the fit is checked from headers before any pyramid and every misfit is named with what to do; the rest in Studio | ux review 2026-10-02, `VrtMosaicReaderTests`, `ImportFormTests` |

## 5. Decision

Several GeoTIFFs uploaded together, on one grid, are published as one image service over a GDAL virtual raster that
places them, the later over the earlier; the reader reads such a `.vrt` wherever it comes from.

## 6. Consequences

**Positive.** Tiled imagery is one layer, in Studio and in every ArcGIS client.

**Negative.** No resampling, ordering method or later additions — the INFERRED narrowings of §2.

**State.** A `.vrt` beside the images; no schema change.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | Mosaics are next | Stated by the owner, 2026-10-02 |
| — | Aligned tiles, later over earlier, made in one upload | INFERRED — [Q-160](../open-questions.md) |

## 8. Dependencies

**Depends on:** ADR-043, ADR-123, ADR-139.

**Depended on by:** —

## 9. Revisit triggers

- The owner answers Q-160 otherwise.
- A mosaic measured slow to see whole because its tiles are small — overviews of the mosaic itself.

## 10. Conditions

1. **The owner confirms or corrects the three INFERRED choices of §2** ([Q-160](../open-questions.md)). **DISCHARGED 2026-10-03**: the owner chose both extensions — [ADR-147](ADR-147-a-mosaic-grows-and-resamples.md); later over earlier stands.
