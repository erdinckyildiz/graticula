# ADR-154 — A classified image names its classes: ArcGIS's raster attribute table

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `HIGH` for the table and its reading · `MEDIUM` for ArcGIS's shape of `identify` with a table, which is read from its documentation |
| **Decided** | 2026-10-03 by owner decision (*"Tüm maddeleri yap"* — the imagery list's item 25) |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-125](ADR-125-an-image-service-has-a-legend-and-statistics.md) (the legend of a classified image) |

---

## 1. Context

A land cover is a band of class numbers. ArcGIS names them with a raster attribute table — value, class name, colour —
and draws, identifies and lists the classes by it. This server answered `hasRasterAttributeTable: false` and drew a
classified image as a grey stretch.

## 2. Alternatives considered

### Alternative A — The owner's classes, else the table beside the file (chosen)

- **Where the table comes from.** Its owner names the classes in Studio's Settings › Classes — a value, a name, a
  colour; *Add the image's values* fills the rows from the values its coarsest sample holds — and they are kept beside
  the coverage (migration 78). Without them, a single-band image's `image.tif.aux.xml`, as GDAL and ArcGIS write one, is
  read: fields by GDAL's usage codes, else by ArcGIS's names (`Value`, `Count`, `ClassName` or `Class_Name`, `Red`,
  `Green`, `Blue`), colours from 0 to 255 or as fractions. Never written: a registered file keeps what its owner's
  tools made.
- **What it does.** `hasRasterAttributeTable` is true; `rasterAttributeTable` answers the rows (`OBJECTID`, `Value`,
  `Count` where known, `ClassName`, `Red`, `Green`, `Blue`); `identify` adds `attributes` with `Value` and `ClassName`;
  the legend is the classes, as *Unique Values*; the picture is drawn in the classes' colours, read at the nearest cell,
  a value with no class transparent. A raster function or a client's renderer is drawn as asked, over the values.
- **Studio** offers the page only for one band of whole numbers — a measurement has no classes.

### Alternative B — A colormap only

**Against:** a colormap colours values and names none; identify and the legend would still say numbers.

## 3. Counterarguments to the preferred option

- *Counts are the file's own, when it has them*; classes named in Studio carry none.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Classes are painted by value, a value with none transparent; colours round-trip as #rrggbb | `RasterAttributeTableTests` | this repository |
| GDAL's table is read by usage with fractional colours, ArcGIS's by name; no sidecar is no table | `PamAttributeTableTests` | this repository |
| An image's values are offered; saved classes make `hasRasterAttributeTable`, the table's rows, `identify`'s `ClassName` and the legend; a colour that is not one is refused | `ClassesAndMensurationConformanceTests` | this repository |

## 5. Decision

As §2.

## 6. Consequences

**Positive.** A land cover reads as its classes everywhere a client looks.

**Negative.** None known.

**State.** `coverage.classes` (migration 78).

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | `identify`'s `attributes` is where clients look for a class name | Read from ArcGIS's documentation, not measured against a client |

## 8. Dependencies

**Depends on:** ADR-125.

**Depended on by:** —

## 9. Revisit triggers

- A table with fields beyond these that a client needs.
