# ADR-085 — A tile leaves out what it cannot draw

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `HIGH` |
| **Decided** | 2026-09-23, by owner decision — [Q-157](../open-questions.md), *"Zoom'a göre sadeleştir"*: at a low zoom the tile statement simplifies geometry and drops polygons smaller than a pixel, measured before and after. The thresholds below are the measurement's, 2026-09-24. |
| **Supersedes** | — (amends [ADR-021](ADR-021-tile-encoding.md), whose statement generalised nothing) |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

The tile statement ([ADR-021](ADR-021-tile-encoding.md)) clipped and quantised and nothing else, so a
low-zoom tile over dense data carried every feature at full detail. Measured on 927,350 Istanbul polygons: a
z12 tile of 2.1 MB and 48,862 features, and a z10 tile of **16.6 MB** that took **12–14 s** to build and that
GDAL's own MVT reader will not open. [ADR-070](ADR-070-a-layer-has-a-visible-scale-range.md)'s visible range
hides such a layer when zoomed out, but only once somebody sets one, and it hides all of it.

## 2. Alternatives considered

### Alternative A — generalise in the tile statement, by zoom *(chosen by the owner)*

**Argument for.** No copy of the data, every layer — hosted, registered, GeoParquet — at once, and nothing to
refresh on an edit.

**Argument against.** Every cold tile pays for it, including the ones it barely changes.

### Alternative B — pre-generalised tables in the datastore

**Argument for.** The fastest tiles.

**Argument against.** Hosted data only, refreshed on every edit, and a second copy to keep true. Offered to
the owner and not chosen.

### Alternative C — ADR-070's visible range alone

**Argument for.** Nothing to build.

**Argument against.** It hides a layer rather than lightening it. Offered and not chosen.

## 3. Counterarguments to the preferred option

- **Features disappear from low-zoom tiles.** Only ones smaller than a pixel both ways, which a client
  draws, at most, as one pixel. A point is never left out.
- **High-zoom tiles pay for a rule that rarely applies.** 3–4 ms a cold tile at z15 and z16, measured, and
  simplification — the part that costs more there — stops after z14.

## 4. Evidence

[benchmarks/tile-generalisation/RESULTS.md](../../benchmarks/tile-generalisation/RESULTS.md):

| Tile | Before | After |
|---|---|---|
| z10 | 16,598,611 bytes, 12,254 ms | 918,157 bytes, 1,764 ms |
| z12 | 2,164,480 bytes, 1,585 ms | 1,260,397 bytes, 1,047 ms |
| z13 | 223,145 bytes, 174 ms | 195,474 bytes, 160 ms |
| z14 | 218,790 bytes, 165 ms | 205,805 bytes, 171 ms |
| z15 | 26,659 bytes, 24 ms | 26,665 bytes, 28 ms |
| z16 | 14,515 bytes, 15 ms | 14,518 bytes, 18 ms |

No invalid geometry on any tile. Simplifying at the grid instead of half a pixel bought 4 % at z12 and cost
80 % at z16; simplifying at half a pixel above z14 cost 9–14 ms a tile for a few hundred bytes.

## 5. Decision

5.1 **A line or polygon whose box is smaller than a pixel both ways is left out of the tile**, at every zoom.
A pixel is the tile's width over 512 — the size MapLibre and the ArcGIS Maps SDK draw a vector tile at —
measured on the geometry in Web Mercator. A point is never left out.

5.2 **Through z14, the geometry is simplified at half a pixel** (`ST_Simplify`, the tile's width over 1,024,
collapsed shapes preserved) before `ST_AsMVTGeom`; above z14 it is not.

5.3 **One implementation.** `PostGisTileSource.LargeEnough` and `PostGisTileSource.Generalised` write the two
rules, and both statements use them: the table's, and `PostGisMvtEncoder`'s for a GeoParquet layer's rows.

5.4 **`TilePipeline.Version` is 2**, so every tile cached before is unreachable; the two GeoParquet tile files
joined the list the version check watches, which they had been missing.

## 6. Consequences

**Positive.** A dense layer is usable zoomed out without a visible range; the worst tile measured became 18×
smaller and 7× faster.

**Negative.** Every tile in every deployment is rebuilt once after the upgrade. A cold z15/z16 tile costs a
few milliseconds more.

**Ports created.** None.

**State.** None new; the tile cache's keys carry the new version.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | Clients draw a vector tile 512 pixels across | MapLibre's and the ArcGIS Maps SDK's default |

## 8. Dependencies

**Depends on:** [ADR-021](ADR-021-tile-encoding.md) (encoding in the statement),
[ADR-066](ADR-066-geoparquet-layers-read-by-duckdb.md) (a GeoParquet layer's tiles).

**Depended on by:** —

## 9. Revisit triggers

- A client measured drawing vector tiles at 256 pixels, where a pixel is twice as wide and this rule leaves
  out less than it could.
- A layer of lines or points in bulk measured slower with the rule than without.

## 10. Dissent

None recorded.

## 11. Conditions

1. **A GeoParquet layer's tile agrees with a table's** under the rule — `GeoParquetTileOracleTests`, passing
   locally 2026-09-24; discharged by the first CI run that includes this change.
2. **Measured on the showcase** — a zoomed-out view of `istanbul_buildings` before and after the upgrade that
   ships this — since the numbers above are one Windows machine's.
