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
  draws, at most, as one pixel. A point is never left out. *(Amended 2026-09-29 — §5.1: and a line is
  never left out either, because a line under a pixel was often one piece of a longer line, and leaving the
  pieces out drew the whole as a row of dashes.)*
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

*(Amended 2026-09-29, after the owner's measurement in ArcGIS Pro — [D-284](../architecture-debt.md).
**A line is no longer left out for being smaller than a pixel; only a polygon is.** The showcase's `hosted/tr_il`
holds 81 provinces' boundaries as 5,433 short lines — Ankara alone 248 — and at 1:10.7 million (about z5–z6)
Pro drew many of them dotted, because every piece under a pixel was left out. A polygon under a pixel is a speck,
and losing it loses a speck; a line piece under a pixel is a link, and losing it breaks the line. Q-157's answer
named polygons — *"drops polygons smaller than a pixel"* — and the 2026-09-24 implementation extended the rule
to lines without a line layer in any measurement; this puts the rule back to what was answered. **The owner
reported the defect; that the fix is to exempt lines rather than, say, to merge pieces before tiling is
`INFERRED` from the owner's words and the Q-157 answer, and is listed for confirmation.** A line is still
simplified by 5.2, with collapsed shapes preserved, so a piece shorter than the tolerance arrives as its two ends,
which join its neighbours'. `ST_AsMVTGeom` then drops only a line whose two ends snap to one cell of the tile's
4,096 grid — checked on PostGIS 3.6.2 on 2026-09-29: a line 0.3 of a cell long is dropped, 0.8 and 2 cells are
kept — and such a piece is invisible, and its neighbours meet in that cell. Points are unchanged. The condition
is `ST_Dimension(g) < 2 or <the box test>`, so a collection holding a polygon is still judged as a polygon.*

*Measured the same day on a synthetic boundary layer, since `tr_il` is not in any local database: 81 Voronoi
provinces over Turkey's extent, their 240 shared edges warped by a continuous function and cut into 6,284
connected pieces (median 2.9 km, mean 5.7 km, 24 % shorter than a z6 pixel), every tile over the extent from z4
to z8 and one in seven at z9–z10, the two statements side by side on PostGIS 3.6.2:*

| Level | Tiles | Bytes before | Bytes after | Change | Largest after | Pieces before → after | ms before → after (sum, warm) |
|---|---|---|---|---|---|---|---|
| z4 | 4 | 39,429 | 145,012 | +268 % | 110,412 | 1,615 → 6,035 | 19 → 25 |
| z5 | 6 | 76,456 | 153,672 | +101 % | 67,016 | 3,031 → 6,262 | 21 → 24 |
| z6 | 15 | 117,454 | 161,548 | +38 % | 32,093 | 4,531 → 6,377 | 37 → 37 |
| z7 | 32 | 170,125 | 188,622 | +11 % | 11,548 | 5,676 → 6,452 | 48 → 42 |
| z8 | 105 | 204,609 | 209,724 | +2.5 % | 4,611 | 6,383 → 6,606 | 57 → 48 |
| z9 | 54 | 36,585 | 36,815 | +0.6 % | 2,210 | 966 → 976 | 11 → 10 |
| z10 | 192 | 51,949 | 52,041 | +0.2 % | 1,078 | 1,163 → 1,167 | 19 → 17 |

*The bytes a line layer's low-zoom tiles gained are the pieces the old rule dropped — at z4 it had kept 27 % of
them — and the largest tile is 110 KB. A line layer dense enough for this to matter at z4 is the case ADR-070's
visible range is for; §9's second trigger is the one to watch. `TilePipeline.Version` is **3** (§5.4).)*

5.2 **Through z14, the geometry is simplified at half a pixel** (`ST_Simplify`, the tile's width over 1,024,
collapsed shapes preserved) before `ST_AsMVTGeom`; above z14 it is not.

*(Amended 2026-09-29 — [ADR-096](ADR-096-a-vector-tile-service-may-be-tiled-in-another-reference.md) §5.7. A service may be cut on a grid other than Web Mercator, whose
level numbers mean other pixel sizes, so the switch is read as the ground it stands for: **a level is simplified
when its pixel is at least Web Mercator's z14 pixel, 4.777 m.** For Web Mercator that is exactly z0–z14 and the
statement still says `@z <= 14`, so no Mercator tile changed; on TUREF / TM30 it is levels 0–7. 5.1's pixel was
always the tile's own width over 512 and needed nothing: on another grid it is measured in that grid's metres.)*

5.3 **One implementation.** `PostGisTileSource.LargeEnough` and `PostGisTileSource.Generalised` write the two
rules, and both statements use them: the table's, and `PostGisMvtEncoder`'s for a GeoParquet layer's rows.

5.4 **`TilePipeline.Version` is 2**, so every tile cached before is unreachable; the two GeoParquet tile files
joined the list the version check watches, which they had been missing. *(Amended 2026-09-29: **3**, for §5.1's
amendment. Seeding, the tile cache and both export formats key a tile by the version through
`VectorTileEndpoints.KeyOf` and `TileCacheKey.Path`, so each rebuilds rather than reads a version-2 tile; a
package already exported is a file and keeps the tiles it was written with.)*

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
- A line layer whose low-zoom tiles, now that no line is left out, are measured too large for a client to open
  — the size that made GDAL refuse a z10 tile in §1. The synthetic boundary layer above peaked at 110 KB.

## 10. Dissent

None recorded.

## 11. Conditions

1. **A GeoParquet layer's tile agrees with a table's** under the rule — `GeoParquetTileOracleTests`, passing
   locally 2026-09-24; discharged by the first CI run that includes this change. **DISCHARGED 2026-09-24**:
   CI on 14079f6 (v1.0.162) green, the oracle test in it.
2. **Measured on the showcase** — a zoomed-out view of `istanbul_buildings` before and after the upgrade that
   ships this — since the numbers above are one Windows machine's. **PARTLY DISCHARGED 2026-09-24**, on
   v1.0.161 → v1.0.162, nine cold tiles around each layer's centre at the first zoom its visible range opens,
   one sample each:

   | Layer | Before | After |
   |---|---|---|
   | `hosted/tr_ilce` z7 | 825,945 bytes, 3,470 ms | 348,405 bytes, 3,432 ms |
   | `hosted/tr_yol` z8 | 214,066 bytes, 2,382 ms | 81,332 bytes, 2,067 ms |
   | `geoparquet/istanbul_roads` z12 | 723,331 bytes, 3,233 ms | 561,112 bytes, 3,996 ms |

   Bytes fell 22–62 %. **What is still open:** the showcase has no `istanbul_buildings` — the 927,350-polygon
   layer measured locally is not published there, so the case this ADR was written for is not on the showcase
   at all — and the GeoParquet layer's time **rose 24 %** in a single cold sample, which is either the
   simplification's cost on DuckDB's rows or one sample's noise; it needs repeated samples before it is
   either.
3. **The dashed boundaries are gone in ArcGIS Pro** — `hosted/tr_il` at 1:10.7 million, on the release that
   ships §5.1's amendment — since the defect was seen there and the tests above prove only that the pieces are in
   the tile. Added 2026-09-29 with the amendment; open.
