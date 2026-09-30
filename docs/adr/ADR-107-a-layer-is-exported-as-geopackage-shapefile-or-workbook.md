# ADR-107 — A layer is exported as a GeoPackage, a zipped shapefile or a workbook

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` — GDAL writes the files and is the same GDAL the import reads with; what is measured is the three formats on one layer and the reference carried in the `.prj` and the GeoPackage; not measured is a million-row layer's time and memory |
| **Decided** | 2026-10-01, by owner decision (*"2 devam sırayla"*, the third item of the ArcGIS reviewer's list); shipped as the first step of [ADR-106](ADR-106-a-layer-s-data-can-be-exported-as-a-file.md) by owner decision the same day, relayed through the session that drafted ADR-106 |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-102](ADR-102-the-studio-item-is-one-page-and-every-setting-has-one-home.md) §5.4 (*Export data* offers more than CSV and GeoJSON) |

---

## 0. Relation to ADR-106

**Two sessions built toward the same feature on the same day.** [ADR-106](ADR-106-a-layer-s-data-can-be-exported-as-a-file.md)
was drafted from the owner's answers of 2026-09-30 — a job that leaves a file downloadable for 24 hours, eight
formats, two million rows, Extract announced in capabilities. This ADR was built from the ArcGIS reviewer's list
without knowing of it: a synchronous request, three formats, a million rows. The owner decided the same day that
this goes first, as ADR-106's first step, with POST and the explicit-Extract rule; ADR-106 then builds the job, the
other five formats and Extract's announcement on top of it. Where the two disagree, ADR-106 is the decision.

## 1. Context

The item's *Export data* (ADR-102 step 8) wrote CSV and GeoJSON in the browser from paged queries, capped at 100,000
rows and always in WGS 84. The ArcGIS reviewer ranked the missing formats fourth: a Portal user exports a hosted
layer as a shapefile, a file geodatabase, a GeoPackage or Excel, and a Turkish publisher whose data is on TUREF
wants it back on TUREF, not reprojected to WGS 84.

## 2. Alternatives considered

### Alternative A — The server reads the rows, the import reader's GDAL writes the file (chosen)

**Argument for.** GDAL is already in the product, in the import reader's process and nowhere else (ADR-009 §2.2);
its GeoPackage, Shapefile and XLSX drivers are in the native payload (measured with the reader's `drivers`). The
host reads the layer through its own feature source in the stored reference and hands the reader a GeoJSON file
that names that reference; nothing is reprojected, and a shapefile's `.prj` says TUREF when the layer is TUREF.

**Argument against.** A temporary file of the whole layer on disk, and a child process per export.

### Alternative B — Write the formats ourselves

**Argument against.** A shapefile writer is a week and a GeoPackage writer is SQLite; both are what the build
policy puts in Tier 2 for exactly this reason, behind the port the reader already is.

### Alternative C — File geodatabase

**Argument against.** GDAL's OpenFileGDB driver writes, and the reader creates test archives with it; it is left
for a second step because Esri's own readers are the ones to measure it against (the D-95 caution).

## 3. Counterarguments to the preferred option

- *Synchronous, so a large layer holds a request.* Up to a million rows; past that it is refused with the sentence
  to filter or to take the tile packages. A job (as the geodatabase import is) is the revisit.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| GPKG, ESRI Shapefile and XLSX drivers are present | the reader's `drivers` answer, 2026-10-01 | src/Graticula.Import.Reader |
| TUREF survives: a TM30 GeoJSON exported as a shapefile carries `PROJCS["TUREF_TM30"…]` | measured by hand 2026-10-01 | — |
| Rows, reference and coordinates match the source | a GeoPackage of `ci_EarlyAlert_sites`: 6 rows, `srs_id` 3857, first point equal to the query's | measured 2026-10-01 |

## 5. Decision

5.1 `POST /admin/services/{service}/layers/{id}/export?folder=…&format=gpkg|shapefile|xlsx` answers the file:
`application/geopackage+sqlite3`, a zip of the shapefile's parts, or a workbook of the attributes. **Four more on the
same road, 2026-09-30 (ADR-106 §5.6):** `fgdb` — a zipped `.gdb` folder written by OpenFileGDB, in the layer's own
reference, the folder itself inside the zip as ArcGIS Online's is; `kml` (LIBKML), `geojson` (RFC 7946) and `csv` in
WGS 84 — a point layer's CSV with X and Y columns, any other geometry as one WKT column, and a byte-order mark; and
`esrijson`, written by the host's query writer rather than GDAL (which reads Esri JSON and does not write it): one
FeatureSet as `query?f=json` answers, in the layer's own reference, without `exceededTransferLimit`. The
console's CSV and GeoJSON stopped being built in the browser, and their 100,000-row cap went with it. Alternative C's
caution stands: Esri's own readers have not opened the File Geodatabase yet.

5.2 **Who may** — owner decision 2026-10-01, relayed through ADR-106: the owner and administrators always; another
signed-in reader only when the owner has *chosen* Extract in Settings › Feature layer (unchosen is off, and an
unset ceiling is not a yes); nobody anonymous.

5.2a **POST, and a cookie from another site is refused** (ADR-098 §5.7's `CrossSiteByCookie`), so an image tag on
another page cannot spend this server's time in a signed-in administrator's name. **One export at a time**, since
it runs in the request.

5.3 **The layer's own reference**, nothing reprojected; WGS 84 as CRS84 so longitude comes first.

5.4 At most a million rows; more is refused with the reason, nothing cut silently.

5.5 In Studio, *Export data* offers the three beside CSV and GeoJSON.

## 6. Consequences

**Positive.** The formats a desktop GIS user expects, in the reference they published in.

**Negative.** No file geodatabase yet; synchronous; a temporary copy of the layer on disk while it is written.

**State.** A scratch folder per export, removed when the answer is sent; nothing in the catalogue.

**Ports created.** None new: GDAL stays behind the import reader's process boundary.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | GDAL reads a GeoJSON file's legacy `crs` member as the layer's reference, in traditional GIS axis order | measured for EPSG:3857 and EPSG:5254; `INFERRED` for other geographic references |

## 8. Dependencies

**Depends on:** ADR-009 (GDAL in the reader only), ADR-102 (*Export data*).

**Depended on by:** —

## 9. Revisit triggers

- A layer larger than a million rows needs exporting — move it to a job.
- A file geodatabase is asked for.

## 10. Conditions

1. **The three formats are tested against the running server** by what each file is. **DISCHARGED 2026-10-01** —
   `LayerExportConformanceTests`.
2. **Studio's *Export data* writes a GeoPackage** through the server. **DISCHARGED 2026-10-01** —
   `ItemStructureTests.Export_data_writes_a_GeoPackage_made_by_the_server`.
3. **A layer in a geographic reference other than WGS 84 is exported and opened in QGIS or ArcGIS Pro with its
   points where they belong** — not yet.
