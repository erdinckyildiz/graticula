# ADR-159 — A multidimensional file is a service of slices

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `MEDIUM` — checked end to end on a NetCDF of two variables over time and depth; real files name their dimensions in more ways than one sample shows |
| **Decided** | 2026-10-03 by owner decision (*"3 saat kadar yokum. hepsini yap. devam et"* — the imagery list's item 29) |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-153](ADR-153-an-image-service-has-the-time-of-its-images.md) (multidimensional imagery not served), [ADR-157](ADR-157-imagery-in-other-formats-is-written-as-geotiff.md) (a NetCDF's first variable only; dimensions other than time not read), [ADR-152](ADR-152-a-mosaic-is-a-catalog.md) (`multidimensionalDefinition` refused) |

---

## 1. Context

Climate and ocean products — temperature and salinity over months and depths, wind over pressure levels — come as
NetCDF and HDF files of several variables, each over time and one or more other dimensions. ArcGIS serves these as a
multidimensional image service: `hasMultidimensions`, `multidimensionalInfo` listing each variable's dimensions and
their values, and a `mosaicRule`'s `multidimensionalDefinition` choosing a variable and a slice. ADR-157 took a file's
first variable and its time only, and ADR-153 refused the definition.

## 2. Alternatives considered

### Alternative A — Every slice an image of the mosaic, carrying its variable and its dimensions (chosen)

- **On the way in** (ADR-157's reader): every variable with two dimensions of space, or the one named, is read; a
  variable whose bands are steps along `NETCDF_DIM_EXTRA` becomes a GeoTIFF a band, dated when one dimension is time
  (named `time`, or with CF units `… since …`), and carrying its variable and its values along the others — a depth, a
  level. The variable is the band's own `NETCDF_VARNAME`.
- **The catalog** (ADR-152) keeps each image's variable and dimensions; `query` has `Variable` and `Dimensions` fields
  a `where` can filter on.
- **`multidimensionalDefinition`** is a list of `{variableName, dimensionName, values}`: values are numbers or `[from,
  to]` ranges; `StdTime` is time in milliseconds since 1970, other dimensions go by the file's own names — and `StdZ`,
  ArcGIS's vertical dimension, is a service's one dimension besides time when it has exactly one. It keeps the
  variables named — or the first — and, for each dimension, the values named; a dimension the definition leaves out is
  held at its first value, so a pixel has one answer. Time is left to `time` and to the drawing order, which puts the
  latest on top as ADR-153 drew a series. A variable or dimension the service does not have is refused, naming those
  it has; a definition sent to a service without variables is refused as before.
- **The service** says `hasMultidimensions: true` and answers `multidimensionalInfo`; its tiles and its exported
  pictures without a definition are the default slice.
- **Studio's Images page** says the service is multidimensional and shows each image's slice.

### Alternative B — One image per variable, its other dimensions as bands

**Against:** a band is a measurement at the same moment and place; making months and depths into bands would answer
`identify` with a list nobody asked for, and break NDVI, stretches and statistics, which read bands as bands.

### Alternative C — Read the file in place through GDAL's multidimensional API

**Against:** ADR-157 §2 Alternative B, unchanged: GDAL parses untrusted files outside the serving process, once.

## 3. Counterarguments to the preferred option

- *A file of many slices is many GeoTIFFs.* A year of daily data at ten depths is 3,650 images, each with its
  overviews; the catalog and the VRT hold them all.
- *The service's statistics and histograms describe every slice together*, not the one drawn by default.
- *Dimension names are the file's.* ArcGIS normalises a vertical dimension to `StdZ`; this server keeps `depth`,
  `lev` or whatever the file says, lists them so in `multidimensionalInfo`, and reads `StdZ` only where there is one
  such dimension to mean.
- *No units or descriptions* of variables and dimensions are kept; `multidimensionalInfo` gives names and values.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| A NetCDF of `temp` and `salt` over two months and two depths becomes eight images; `hasMultidimensions`; `multidimensionalInfo` lists both with `StdTime` and `depth`; no definition draws temp at depth 0, latest month; a definition chooses salt at 100 m, and February; `time` chooses January; `where Variable = 'salt'` counts four; an unknown variable or dimension is refused naming those it has | `MultidimensionalConformanceTests`, corpus `ocean.nc` (written byte by byte as classic NetCDF) | this repository |
| The first variable, its first depth, every month; ranges inclusive; refusals name what there is | `DimensionSliceTests` | this repository |

## 5. Decision

As §2, Alternative A.

## 6. Consequences

**Positive.** Climate and ocean products publish as ArcGIS clients expect them, with the slice chosen per request.

**Negative.** Disk in proportion to slices; statistics over all of them.

**State.** Each catalog entry's `variable` and `dimensions`, in the coverage's `images` JSON — no migration, the column
is ADR-152's.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | GDAL reports a NetCDF's extra dimensions in `NETCDF_DIM_EXTRA` and each band's values in `NETCDF_DIM_<name>` | True of the netCDF driver; HDF files without it are read as one image a variable |

## 8. Dependencies

**Depends on:** ADR-037, ADR-152, ADR-153, ADR-157.

**Depended on by:** —

## 9. Revisit triggers

- A file whose slices number in the thousands.
- A file with two dimensions besides time, asked for by `StdZ`.
- An owner who needs the slice drawn by default to be other than the first.
