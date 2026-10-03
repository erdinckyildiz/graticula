# ADR-157 — Imagery in other formats is written as GeoTIFF on its way in

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `MEDIUM` — the formats are GDAL's and the conversion is checked end to end on samples; real NetCDF files vary in how they name time |
| **Decided** | 2026-10-03 by owner decision (*"Tüm maddeleri yap"* — the imagery list's item 27, flagged as needing a decision on GDAL and taken by that instruction) |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-123](ADR-123-imagery-comes-into-studio.md) (uploads are GeoTIFFs), [ADR-153](ADR-153-an-image-service-has-the-time-of-its-images.md) (multidimensional imagery) |


> **Amended 2026-10-03 by [ADR-159](ADR-159-a-multidimensional-file-is-a-service-of-slices.md).** Every variable of a NetCDF or HDF is read, and a NetCDF's dimensions besides time are kept on each image.

---

## 1. Context

An image service read GeoTIFFs only. Imagery arrives as JPEG 2000 (Sentinel-2, many orthophotos), NetCDF and HDF
(climate, ocean and Earth-observation products), ERDAS Imagine and ASCII grids. The decision this needed was whether
GDAL comes into the server — and it already had: ADR-037 §5a put GDAL in the image for geodatabases, in its own
process, `Graticula.Import.Reader`, so an untrusted file's parser never runs where public requests are served
(ADR-009 §2.2).

## 2. Alternatives considered

### Alternative A — Convert on the way in, in the import reader's process (chosen)

- An upload or an added image in JPEG 2000 (`.jp2`, `.j2k`), NetCDF (`.nc`, `.nc4`, `.cdf`), HDF5 and HDF-EOS5
  (`.h5`, `.hdf5`, `.he5`), HDF4 (`.hdf`), ERDAS Imagine (`.img`) or an ASCII grid (`.asc`) is written, by GDAL in the
  import reader, as a tiled, deflated GeoTIFF; from there it is what any upload is — pyramids, a mosaic, the catalog,
  Download gives back the GeoTIFF. GDAL never reads it in the serving process, and never again after it arrives.
- **Subdatasets.** A NetCDF or HDF's first variable is taken, named in the image's name.
- **Time.** A NetCDF whose bands are steps of `time` with CF units (`days since …`, hours, minutes, seconds) becomes
  a GeoTIFF a step, each dated (ADR-153) and named with its date — a mosaic a time slider plays. Other dimensions are
  not read: the multidimensional model itself is not served.
- **No coordinate system** is refused, except a grid of longitudes and latitudes, which is WGS 84.
- **MrSID and ECW are refused by name**: they are read only with their makers' SDKs, whose licences keep them out of
  an open build; the refusal says to export as GeoTIFF or JPEG 2000.
- Registering a file in place stays GeoTIFF: a registered file is read where it lies, and these are converted.

### Alternative B — Read these formats in place, through GDAL, on every request

**Against:** GDAL would parse files somebody chose inside the serving process on every tile, the thing ADR-009 §2.2
keeps out; and every format's overviews, mosaics and statistics would be a second reading path.

## 3. Counterarguments to the preferred option

- *A converted file takes its GeoTIFF's space*, beside nothing: the original is not kept.
- *A NetCDF's variable is its first*; choosing another is not offered in Studio.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| A JPEG 2000 is published with its values; a NetCDF of three monthly steps becomes three dated images, `timeInfo` across them and `time` choosing one; an ASCII grid with no reference is WGS 84; MrSID is refused naming its SDK | `ImageryFormatsConformanceTests` | this repository |

## 5. Decision

As §2.

## 6. Consequences

**Positive.** Sentinel-2 tiles and climate products publish without a desktop tool in between.

**Negative.** Conversion costs time and disk at upload, bounded by the reader's deadline and memory ceiling.

**State.** None new.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | NetCDF time is a dimension named `time` with CF units | True of CF-conventions files; others are converted without dates |

## 8. Dependencies

**Depends on:** ADR-009, ADR-037, ADR-123, ADR-153.

**Depended on by:** —

## 9. Revisit triggers

- A file whose time is named otherwise, or whose variable is not the first.
- An owner who needs MrSID or ECW and can supply the SDK.
