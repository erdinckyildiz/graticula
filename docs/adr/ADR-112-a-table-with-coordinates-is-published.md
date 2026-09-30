# ADR-112 — A CSV or Excel table with coordinates is published and added to a layer

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `HIGH` — GDAL's CSV driver does the reading, and the path after it is the shapefile's |
| **Decided** | 2026-10-01, by owner decision (*"Tamamı"*, the first of five questions from the ArcGIS review's second pass) |
| **Supersedes** | — |
| **Superseded by** | — |

---

## 1. Context

The ArcGIS reviewer's second pass named this as the most common thing a Portal user does that Studio cannot:
*"Add item → CSV with lat/long columns → publish as a hosted feature layer"*, and *Update data* from a spreadsheet.
A field team's list of sites arrives as a spreadsheet far more often than as a shapefile. Until now the upload took
a zipped shapefile, a zipped File Geodatabase or GeoJSON, and a CSV was refused as *neither a ZIP nor valid JSON*.

## 2. Alternatives considered

### Alternative A — The import reader turns the table into GeoJSON, and the shapefile's path reads that (chosen)

**Argument for.** Parsing an untrusted file stays in the child process (ADR-037 §5a, D-113). GDAL's CSV driver
already knows how to find X and Y columns by a list of names, read a WKT column, and tell a number from text
(`AUTODETECT_TYPE`). The dataset that comes back goes through the limits, the type inference and the importer every
other upload does, so publishing, appending, overwriting, field mapping and ArcGIS's `append` take a table with no
further change.

### Alternative B — Parse the CSV in the host

**Argument against.** It is a second CSV reader with its own quoting, encoding and number rules — and one more
untrusted-file parser in the process that serves public requests, which is what D-113 removed.

### Alternative C — A geocoding step for tables of addresses

**Argument against, for now.** Portal can publish a table of addresses by geocoding it, which needs a geocoder this
product does not have. Out of v1; a table without coordinates is refused and told what it needed.

## 3. Counterarguments to the preferred option

- *Two child-process calls for one upload* — `tabular`, then `layers` and `features` on what it wrote. A table big
  enough for that to matter is past the import limits anyway.
- *The file is recognised by its name.* The bytes still have to agree: a `.xlsx` must be a ZIP and a `.csv` must not.
  A ZIP named `.csv` goes the shapefile's way and is refused there.
- *A workbook is a ZIP and could unpack to far more than was uploaded.* Its central directory is read first and a
  workbook declaring more than 1 GB is refused; the reader's deadline bounds the rest.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| GDAL's CSV driver takes `X_POSSIBLE_NAMES`, `Y_POSSIBLE_NAMES`, `GEOM_POSSIBLE_NAMES`, `KEEP_GEOM_COLUMNS` and `AUTODETECT_TYPE` | GDAL CSV driver documentation | publicly documented |
| Portal publishes a CSV with coordinate columns as a hosted feature layer, and appends one | ArcGIS Enterprise *Publish hosted feature layers* | publicly documented |
| A Turkish-named CSV and a minimal workbook are read, typed and appended | `TablesArePublishedTests` | this repository |

## 5. Decision

5.1 The import reader gains `tabular` (`in`, `out`, optional `x` and `y`): a `.csv` is opened by GDAL's CSV driver;
a `.xlsx` is written to CSV from its first sheet and then opened the same way. The X columns looked for are the
caller's, then `x`, `lon`, `lng`, `long`, `longitude`, `boylam`, `easting`, `doğu`; the Y columns the caller's, then
`y`, `lat`, `latitude`, `enlem`, `northing`, `kuzey`; a shape column `wkt`, `geometry`, `geom`, `the_geom` or `shape`.
The coordinate columns are not kept as attributes. The result is written as GeoJSON with the coordinates untouched. A
table with none of these is refused, saying what it needed.

5.2 The host recognises a table before the ZIP sniff, in both publishing (`/admin/hosted/import`) and *Update data*
(append, overwrite, the dry run that offers the column mapping, and ArcGIS's `append`): `.csv` and `.txt` that are
not a ZIP, `.xlsx` that is one. The form's `srid` says which system the coordinates are in and is **4326 when
absent**, because a spreadsheet of places is longitude and latitude far more often than anything else — except
when the X column found is `easting` or `doğu`, which are metres, and then a missing `srid` is refused rather than
read as degrees (design review 2026-10-01). `x` and `y` name the columns when their names are not in the list.

5.3 Studio's upload screens accept `.csv`, `.txt` and `.xlsx`. Publishing shows X and Y column fields and a note on
what is found by itself when a table is chosen; *Update data* offers the coordinate system and the X and Y columns for a
table, and sends them on the dry run that reads its columns as well as on the write.

## 6. Consequences

**Positive.** The most common Portal upload works, and every path after the upload is shared with the formats before.

**Negative.** A table of addresses still cannot be published. Excel reads the first sheet only.

**State.** None — no schema change.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | A table without an `srid` holds WGS 84 longitude and latitude | `INFERRED` — Portal's own default for a CSV of locations |

## 8. Dependencies

**Depends on:** ADR-037 (the reader in a child process), ADR-103 (Update data), ADR-105 (ArcGIS's `append`).

**Depended on by:** —

## 9. Revisit triggers

- A geocoder is added, and a table of addresses can be given coordinates.
- Somebody needs a sheet other than the first.

## 10. Conditions

1. **A CSV is published and a workbook appended, typed and placed** — **DISCHARGED 2026-10-01**,
   `TablesArePublishedTests`.
2. **Studio offers tables on both upload screens** — **DISCHARGED 2026-10-01**,
   `ImportFormTests.A_table_upload_asks_for_its_columns`.
