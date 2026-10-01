# ADR-120 — A GeoPackage or KML is imported as a geodatabase is

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `HIGH` — the geodatabase path, measured reading both formats through `/vsizip/` |
| **Decided** | 2026-10-01, by owner decision (*"Sırayla git"*, the ArcGIS reviewer's second-pass item 1) |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-024](ADR-024-shapefile-import.md) condition 3 (the two formats it left refused) |

---

## 1. Context

The reviewer: *"GeoPackage and KML are refused by name … The server now exports GPKG, KML, CSV and XLSX but cannot
import any of them."* ADR-112 brought CSV and Excel. ADR-024 condition 3 kept GeoPackage-in-a-zip and KMZ refused
until each had a decision of its own, so that *"we already decompress"* would not be the argument.

## 2. Alternatives considered

### Alternative A — The File Geodatabase's path (chosen)

**Argument for.** Both can hold several layers, as a geodatabase does, and the geodatabase path already lists an
upload's layers in a job, lets the operator choose, and publishes what was chosen — in the child process, under
ADR-037 §5b's bounds, with GDAL reading inside the archive through `/vsizip/` and nothing unpacked. It needed one
change in the reader: finding a `.gpkg` or `.kml` in the archive when there is no `.gdb`. Measured: a GeoPackage
inside a zip and a KMZ's `doc.kml` both list their layers and publish.

### Alternative B — The shapefile's exception

**Argument against.** It is the one ADR-024 condition 3 refuses, and its bounds were drawn for a shapefile.

## 3. Counterarguments to the preferred option

- *A lone `.gpkg` or `.kml` is not an archive.* It is written into a one-entry zip in the scratch directory, without
  compression, and read as the same file in an archive would be; the copy is deleted when the request ends.
- *Update data still refuses them*, as it refuses a geodatabase: a layer is updated from one layer's file, and these
  can hold many.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| A GeoPackage on its own and zipped, a KMZ and a KML are listed and published | `GeoPackageAndKmlImportTests` | this repository |
| Sixty-four bytes under those names end their inspection with the reader's reason | `ArchiveFormatRefusalTests` | this repository |

## 5. Decision

5.1 The import reader opens a `.gpkg` or `.kml` inside an archive when it holds no `.gdb`, the one nearest its root.

5.2 An upload recognised as a GeoPackage or KML (a KMZ is a zipped KML) opens the geodatabase's inspection job; a
lone `.gpkg` or `.kml` is wrapped first. Without the reader they are refused for that reason, as a geodatabase is.

5.3 Studio's upload screens accept `.gpkg`, `.kml` and `.kmz`, and the inspection screen speaks of layers rather
than of a geodatabase.

## 6. Consequences

**Positive.** The formats this server exports, it now imports.

**Negative.** Not through Update data.

**State.** None.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | ADR-037 §5b's bounds, drawn for geodatabases, suit GeoPackages | `INFERRED` — same reader, same limits; no large GeoPackage measured |

## 8. Dependencies

**Depends on:** ADR-024 (condition 3), ADR-037 (the reader and its bounds), ADR-038 (publishing an inspected file).

**Depended on by:** —

## 9. Revisit triggers

- A GeoPackage larger than the geodatabase bounds is refused in practice.

## 10. Conditions

1. **GeoPackage and KML/KMZ are listed and published** — **DISCHARGED 2026-10-01**, `GeoPackageAndKmlImportTests`.
