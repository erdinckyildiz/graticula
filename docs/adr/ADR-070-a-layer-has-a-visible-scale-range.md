# ADR-070 — A layer has a visible scale range, and publishing measures one from the data

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-09-14, by owner decision. Shown that a zoomed-out map over a city's buildings made the showcase build a 33 MB tile in 46 seconds and time out at the next level, the owner said Esri filters such data above a scale and that a default exists; the Esri documentation was read (§4), and the owner answered: *"biz de koyalım onları. max 2000 feature mesela."* Shown what 2,000, 10,000 and 30,000 features per vector tile meant in scale on two cities' buildings (§4), the owner chose **10,000**. **That layers get a visible range, a default measured from the data, and the figure 10,000 per tile are the owner's. How the default is measured, which faces enforce the range, and that the deployment's record ceiling stays where it is are `INFERRED`** and listed in §11 |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

Every metadata writer stated `minScale = 0` and `maxScale = 0`, and every tile and map was built from
every feature its box matched. [ADR-066](ADR-066-geoparquet-layers-read-by-duckdb.md)'s tile source
deliberately stopped truncating at 50,000 rows, because a tile cannot say it is partial. The cost of
that choice arrived with the first large layers: two cities' Overture buildings (1.27 and 1.66 million)
published on the showcase on 2026-09-14.

Nothing let a publisher say *this layer is not meant to be seen from further out than a district*, and
nothing in the server would have said it for them.

## 2. Alternatives considered

### Alternative A — a visible range on the layer, ArcGIS's numbers, with a measured default *(chosen)*

ArcGIS's `minScale`/`maxScale` on the layer: advertised in the documents so ArcGIS clients stop asking,
and enforced on the faces the server draws itself — vector tiles and drawn maps — for clients that do
not. Publishing sets the zoomed-out limit from a count of the data.

### Alternative B — thin or cap features per tile

Drop features past a count, or simplify, at coarse levels. Rejected for now: a silent cap is the defect
ADR-066 removed, and choosing *which* features survive is a cartographic decision per layer. It stays
the better answer for a layer that must be seen whole from far out — §9.

### Alternative C — lower the deployment's record ceiling to 2,000

`Graticula__MaximumRecordCount` already exists (2026-08-19). Rejected as the answer to this: tiles do not
read through it, so it does not touch the measured cost; and it *does* cap MapServer export and WMS
GetMap, which would draw the first 2,000 features of a map and look complete. §6.

## 3. Counterarguments to the preferred option

- **A range hides data.** A user zoomed out sees nothing and may think the layer is empty. ArcGIS
  clients grey the layer out of range; a WMS client shows nothing and is told only through capabilities
  (§6, condition 2).
- **The default is an estimate.** The search follows the densest tiles it finds; a dense area under a
  parent that was not among the densest can be missed, giving a limit one level too far out.
- **A range fixed at publish drifts.** A table that grows tenfold keeps the limit it was given.

## 4. Evidence

| Claim | Evidence |
|---|---|
| The cost is real and grows as the map zooms out | Showcase 1.0.54, `istanbul_buildings_parquet`, one tile each over the city: level 14 0.3 MB in 0.7 s, 12 3.9 MB in 1.5 s, **10 33 MB in 46 s, 8 a 503 after 75 s** |
| ArcGIS Online sets a default | *"When publishing from a file or a feature collection, ArcGIS Online automatically sets the optimal visible range based on the data."* — [Publish hosted feature layers](https://doc.arcgis.com/en/arcgis-online/manage-data/publish-features.htm). How is not documented |
| ArcGIS Pro does not | *"All layers draw at all scales by default."* — [Display layers at certain scales](https://doc.esri.com/en/arcgis-pro/latest/help/mapping/layer-properties/display-layers-at-certain-scales.html); Online honours a range authored there |
| ArcGIS also caps queries | `standardMaxRecordCount`, `tileMaxRecordCount` and `maxRecordCount` — [Feature Layer (REST)](https://developers.arcgis.com/rest/services-reference/online/feature-layer.htm); the client warns *layer did not draw completely* |
| The arithmetic agrees across faces | `VisibleScaleRangeTests`: level 0 of the 512-pixel scheme is 1:295,828,763.795777; a range stored at a level's scale carries that level and not the one above; a range between levels keeps the level that is on screen across it; the WMS denominator of one metre per pixel is 1/0.00028 |
| The default follows density, not the extent | `VisibleRangeSuggestionTests`: 25,000 points filling one level-16 tile with two outliers a continent apart give level 17, in fewer than 200 counts |
| What a per-tile figure means in scale | Fixture, 2026-09-14, densest 512-pixel tile per level found by following the three densest from a 7×7 block, Istanbul / New York: level 13 (to 1:18,056) 26,229 / 40,496; **14 (to 1:9,028) 8,953 / 11,834**; 15 (to 1:4,514) 2,840 / 3,438; 16 1,102 / 1,146. At 2,000 per tile both cities draw only from 1:4,514 — the first measurement, taken with 2,000, stored exactly that for both, its search agreeing with this one on 1,102 — at 10,000 Istanbul from 1:18,056 and New York from 1:9,028 |
| The faces honour it, end to end | `range-e2e.py`, fixture, linux-arm64, one GeoParquet, one DuckDB-file and one MotherDuck layer republished: each given a range at publish; the FeatureServer service and layer documents, the VectorTileServer document and the style's `minzoom` all state it; tiles below it 204 in 19–33 ms at levels 8, 10 and the level before (level 10 had been 33 MB in 46 s); tiles inside it drawn; MapServer export answers outside and inside; WMS 1.3.0 capabilities carry `MaxScaleDenominator`; the suggestion endpoint agrees with publish; a range that never draws refused — **all pass** |

## 5. Decision

### 5.1 The range

`layer.min_scale` and `layer.max_scale` (migration 48), ArcGIS's meaning: a scale denominator at 96 dpi
and 39.37 inches to the metre, zero or null for no limit on that side, the zoomed-in limit smaller.
Refused otherwise, in the endpoint and by a check constraint.

- **Documents.** The FeatureServer service and layer documents and the MapServer service, layer and
  legend documents carry the layer's numbers. The VectorTileServer document carries the service's: its
  widest layer's, so one limited layer does not hide its siblings. The generated and derived style
  narrows each style layer's `minzoom`/`maxzoom` to it, never widens.
- **Tiles.** A layer is left out of a vector tile when no scale the tile is on screen at — from the
  scale at which it fills 512 pixels to half of that — is inside the range. Checked before the
  describe, the cache and the build.
- **Drawn maps.** MapServer export and WMS GetMap leave the layer out when the map's scale is outside.
  A thumbnail and a symbology preview do not ask, because a blank thumbnail describes nothing.
- **Queries are unaffected.** ArcGIS applies a range on the client and on drawn maps, and a query
  answers the same rows at any scale; the range is not part of the query cache's fingerprint.

### 5.2 The default `INFERRED`

At publish — both routes — the layer is measured and given the zoomed-out limit at which **no vector
tile holds more than 10,000 features**. A layer of 10,000 or fewer gets no limit. The search starts at the
level where the extent fits in one or two tiles and at each level counts the children of the three
densest tiles above the figure, through the layer's own reader, with a 30-second deadline. The limit stored
is the scale of the first level whose densest counted tile is within the figure.

A failure — no extent, the deadline, an unreachable source — publishes the layer without a range and
says why in the response. `POST /admin/layers/{name}/visible-range/suggestion` measures again without
storing; `PUT …/visible-range` stores; the layer page in the console has both.

## 6. Consequences

- **State.** Two nullable columns on `layer` in the platform catalogue, shared by every node; nothing new
  at runtime — the range rides on the `PublishedLayer` each node already reads, and a change is picked up
  when that read is forgotten, as every other layer setting is.

- **The deployment's record ceiling is still 50,000 by default** and still caps drawn maps; lowering it
  is an operator's setting with that side effect, not this decision.
- WMS 1.3.0 capabilities state the range as `MinScaleDenominator`/`MaxScaleDenominator`; 1.1.1's
  `ScaleHint` is a different quantity and is not written.
- Layers published before this build have no range until someone measures or sets one.
- A layer inside its range at a dense spot still builds a full tile: the default bounds the densest
  tile *counted*, not every tile.

## 7. Assumptions this decision rests on

| Assumption | If wrong |
|---|---|
| 10,000 features per 512-pixel tile is a tile a browser and this server handle comfortably | Change `FeaturesPerTile`; the rest stands |
| ArcGIS clients act on `minScale` in the layer and service documents | Only the server-side enforcement protects the server; still correct |
| Three branches find the densest area of real data | The limit is one level too far out for that layer; an administrator can set it |

## 8. Dependencies

ADR-066 (tile source), ADR-052 (derived style), ADR-031 (cost ceilings), ADR-033 (style layers).

## 9. Revisit triggers

- **A layer must be seen whole from far out** — thinning or aggregation per level (Alternative B).
- **A layer's data grows past its measured range** — re-measure on a schedule, or at refresh.

## 10. Dissent

None recorded.

## 11. Conditions

1. **The owner confirms the `INFERRED` choices**: enforcing on tiles and drawn maps and not on queries;
   leaving the record ceiling's default; the three-branch search.
2. **The WMS capabilities document states each layer's range** as `MinScaleDenominator` and
   `MaxScaleDenominator`, converted with `VisibleScaleRange.WmsDenominator`. **DISCHARGED 2026-09-14** for
   1.3.0, after `Style` where its schema puts them, the names the WMS way round —
   `WmsCapabilitiesStateTheVisibleRangeTests`.
3. **The default is measured on real layers on the fixture** — the two cities in all three kinds —
   and the time the measurement adds to a publish is recorded here. **DISCHARGED 2026-09-14**: at 10,000,
   Istanbul's buildings from GeoParquet and from MotherDuck both 1:18,056 (level 14, densest counted tile
   8,953, 89 counts), New York's from the DuckDB file 1:9,028 (level 15, 71 counts). A publish took 11.3–13.6 s
   for the file kinds and 4.3 s for MotherDuck, against 24–32 ms for the same publishes before this
   (`cities-bench.py`); the suggestion endpoint alone 5.7 s. The level-14 tile over Kadıköy, now the first
   drawn, is 452 KB in 556 ms. A publish that slow is a cost this pays once per layer, and the reason the
   search has a deadline.
4. **The layer page's new controls go through the UX review** the owner requires of every screen.
   **DISCHARGED 2026-09-14**, from headless-Chrome screenshots at 1280 and 400 px on the fixture, a layer
   with a range and one published before this. Repaired: focus fell to the page after measuring (the pressed
   button now takes it back); the saved range was overwritten by the measured one in the same sentence, so a
   proposal looked live (two lines now, *Saved:* and *Measured, not saved:*); *No limit* saved at once from
   beside *Measure from the data* (moved to the far end, with a title saying so); a trailing `1:` read as
   *beyond 1: no limit* (beside the box now); ArcGIS's *minimum scale* and *maximum scale* named. Not
   repaired, as older than this change: the console's layout at 400 px.
