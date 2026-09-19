# ADR-080 — Hosted tables keep the Z and M their data carries

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `HIGH` |
| **Decided** | 2026-09-19. Step 4 of [ADR-074](ADR-074-z-and-m-ordinates.md) §5. **The owner's decision**: asked *"barındırılan tablolar 3B tanımlanabilsin mi, içe aktarma Z'yi saklasın mı?"*, the answer was *"yap"* — both. **The rule for a layer whose features disagree (§5.2), and the report wording, are this ADR's.** |
| **Supersedes** | [D-107](../architecture-debt.md)'s *v1 stores 2D and says so* |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

Every hosted table was two-dimensional. An import read each geometry's Z and M, counted them and threw them
away (D-107). Six of the eight layers in the owner's smallest archive are `25D`; in one of them 3,659 of
3,659 features lost their elevation. The loss was reported, but the elevations were still gone.

Steps 1 to 3 ([ADR-074](ADR-074-z-and-m-ordinates.md), [ADR-077](ADR-077-z-and-m-ride-beside-x-and-y.md))
made everything above the table able to carry Z and M: the geometry model, the WKB reader and writer, ArcGIS
`query` and `applyEdits`, OGC API Features and WFS. The table itself was the one place left that could not
hold them.

## 2. Alternatives considered

### Alternative A — keep what every feature carries *(chosen)*

**Argument for.** A typed PostGIS column (`geometry(PointZ, 2952)`) holds one dimensionality, so the
column's type is decided by what all of the rows can fill. A file where every feature has Z gets a Z table
and loses nothing. A file where only some features have Z keeps x and y, and the importer counts and reports
the features that lost their Z.

**Argument against.** In a mixed file some real elevations are still dropped.

### Alternative B — keep what any feature carries, and pad the rest

**Argument for.** Nothing that was sent is lost.

**Argument against — and it decides.** Every feature that had no Z would get a number nobody measured.
Zero metres is a real height, and nothing could tell it apart from a surveyed one. The owner's rule in
ADR-077 §5 lets a new vertex get its Z by interpolation *along an edge that has one*; a feature with no Z at
all has no such edge.

### Alternative C — a bare `geometry` column that holds whatever arrives

**Argument for.** Every row keeps exactly what it had.

**Argument against.** The layer document's `hasZ` is read from the column's declaration (ADR-074 §4.1).
A bare column declares nothing, so a layer full of elevations would report `hasZ: false`, and the reader
would never hand them to a client.

## 3. Counterarguments to the preferred option

A 25D feature class that holds one 2D feature — say one edited by a tool that dropped Z — loses the
elevation of every other feature. That is the right choice rather than a padded one, but it costs a lot for
one bad row. The count in the report is what lets an operator find the row and re-import. Refusing the
import instead would block every other layer in the archive over something the operator can fix at the
source.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| A file whose features all carry Z makes a `PointZ` table, and the row keeps its elevation | `POINT Z (28.97 41 120.5)` read back from the column | `AnImportKeepsWhatEveryFeatureCarriesTests` |
| A file where only some features carry Z makes a flat table and counts the rest | `Stored = None`, `Flattened = 1` | same |
| A defined layer declares what it was asked for | `MultiLineStringZ`, `M` and `ZM` | same |
| Every surface above the table already reads and writes Z and M | ADR-077 condition 3, discharged 2026-09-19 | `AThreeDimensionalRowComesBackWholeTests` |

## 5. Decision

1. **An import keeps Z and M.** The shapefile and geodatabase readers read WKB with both ordinates, and the
   GeoJSON import keeps a position's third element.
2. **The column declares what every feature carries** (`Ordinates.Common`). A feature that carries more has
   the extra removed (`Ordinates.Keep`) and is counted as `Flattened`. No ordinate is ever made up.
3. **A defined layer may declare one.** `POST /admin/hosted/define` accepts `PointZ`, `MultiLineStringZM`
   and the like, and an empty feature class in a geodatabase declared `wkbMultiPolygon25D` becomes a
   `MultiPolygonZ` table.
4. **What was kept is reported.** The import answers `hasZ` and `hasM` and warns only when something was
   flattened. The geodatabase job reports `kept` (`Z`, `M`, `ZM`) per layer beside `flattened`, and the
   console's Elevation column says *Z kept*.
5. **Nothing is transformed.** Imported geometry is written through the same binary COPY and
   `ST_GeomFromWKB`, which read ISO Z and M WKB (ADR-077 §10.4).

## 6. Consequences

**Positive.** D-107 is closed. An archive's elevations reach the table, the layer document and every surface
that carries them.

**Negative.**
- A mixed layer still drops the ordinate that not every feature has. This is counted, not hidden.
- Vector tiles, map images and GeoParquet layers stay two-dimensional (ADR-077 §5.4).
- Every hosted 3D layer depends on how the editing path treats it: ADR-077 §10 refuses a flat edit on a Z
  row. A 3D hosted layer therefore cannot be edited by a client that sends only x and y, and that client is
  told why in the edit's own result. *(Corrected the same day: this said "a client like the console's own
  map" — the console, the map viewer and the HTML service pages edit no feature geometry at all, measured by
  searching `wwwroot` and the page writers for `applyEdits`, `addFeatures` and `updateFeatures`.)*

**Ports created.** None.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | GDAL hands a 25D layer's Z to the reader process in its WKB, as the `features` wire already does | Measured by the reader's own comment (`Graticula.Import.Reader`, *keep their Z here*); unmeasured end to end on the owner's archive — condition 1 |

## 8. Dependencies

**Depends on:** [ADR-074](ADR-074-z-and-m-ordinates.md), [ADR-077](ADR-077-z-and-m-ride-beside-x-and-y.md).

**Depended on by:** —

## 9. Conditions

1. **The owner's archive is imported again and the 25D layers arrive with their elevations.** Its
   `OHN_Watercourse` layer (3,659 features) is the case this decision was taken for. *(Open — the archive is the
   owner's and is not in this repository.)*
2. **The console's map editor says why it cannot edit the geometry of a 3D hosted layer**, rather than
   showing the writer's refusal as a failed save. **DISCHARGED** 2026-09-19, by the premise failing rather
   than by work: there is no such editor. Nothing this server serves sends a feature geometry — no
   `applyEdits`, `addFeatures` or `updateFeatures` in `wwwroot` or in any HTML page writer — so the only
   clients that can send a flat edit are external, and they get the writer's sentence in the edit result.

**State.** None.
