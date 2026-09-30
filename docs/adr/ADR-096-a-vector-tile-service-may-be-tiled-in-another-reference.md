# ADR-096 — A vector tile service may be tiled in another reference

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `MEDIUM` — the grid arithmetic is tested by hand-worked numbers; what an ArcGIS client does with a VectorTileServer in another reference has not been watched (§6) |
| **Decided** | 2026-09-29, by owner decision. The owner asked for the vector tile face's remaining gaps to be closed one at a time; this is item 10 on that list, and the owner stated the decision directly: **a service may be tiled in a scheme other than Web Mercator**, the motivating case being the Turkish national grids — TUREF TM zones, EPSG:5253 to 5259, and the ITRF96-based ones — used as ArcGIS Pro and ArcGIS Maps SDK basemaps in a local projection. The owner also described how a TUREF grid should be found (its origin at the zone's upper-left bound from the reference's area of use, resolutions halving from a level-0 resolution that fits the zone in one tile). Which zones are built in, the rounding, the level count, the storage, the API's shapes, what is refused and the console's control are this session's design and are marked **INFERRED** where they matter (§5, §11). |
| **Depends on** | [ADR-021](ADR-021-tile-encoding.md), [ADR-010](ADR-010-caching.md), [ADR-057](ADR-057-composing-and-publishing-a-service.md) §5c, [ADR-070](ADR-070-a-layer-has-a-visible-scale-range.md), [ADR-085](ADR-085-a-tile-leaves-out-what-it-cannot-draw.md), [ADR-093](ADR-093-seeding-the-tile-cache.md), [ADR-095](ADR-095-registered-postgis-layers-serve-vector-tiles.md), [ADR-066](ADR-066-geoparquet-layers-read-by-duckdb.md) §9 |
| **Amends** | [ADR-021](ADR-021-tile-encoding.md) §5a (*the tiles themselves are Web Mercator only*); [ADR-085](ADR-085-a-tile-leaves-out-what-it-cannot-draw.md) §5.2 (simplification is keyed by the pixel's size, not the level number); [ADR-093](ADR-093-seeding-the-tile-cache.md) §5.2 and §5.3 (a seed's area and levels are the service's grid's); [ADR-070](ADR-070-a-layer-has-a-visible-scale-range.md) §5 (a tile's scale is its own grid's) |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

Every vector tile this server has cut was cut on Web Mercator. `ST_TileEnvelope(z, x, y)` with its default
bounds decided where a tile is, `VectorTileServerMetadataWriter` wrote a `tileInfo` of 102100 with 512-pixel
levels 0 to 22 and refused an extent in any other reference (D-49), and `VectorTileEndpoints.WebMercator`
was a constant. Since 0eaf635 a layer keeps its own reference and the tile path moves it into Mercator per
cold tile ([ADR-021](ADR-021-tile-encoding.md) §5a) — so a TUREF / TM30 layer *draws* on a Mercator map, but
it cannot be a basemap in TM30.

That is the case the owner named. A Turkish municipality or ministry works in TUREF / TM30 (or TM33, …) in
ArcGIS Pro, and a basemap in that grid is what its maps are built on: a Web Mercator basemap under a TM30 map
is reprojected on every draw by the client, which a raster basemap tolerates and a vector tile basemap does not
— the ArcGIS clients draw a vector tile layer only in its own tiling scheme's reference. Without this, a
service holding the city's parcels in TM30 can be a feature service in TM30 and a tile service only in
Mercator.

What already existed and was read before deciding:

- **`TilingScheme`** (`src/Graticula.Core/Cartography/TilingScheme.cs`) — ImageServer's grid (ADR-044):
  256-pixel tiles, Web Mercator and WGS 84 by the published tables, anything else derived from the coverage's
  own extent. It is a raster face's grid and is not reused here (§2, Alternative E).
- **The service's reference** (ADR-057 §5c, `PUT /admin/services/{name}/srid`) — the reference its *queries*
  answer in. A different question from the grid its *tiles* are cut on (§2, Alternative A).
- **`IProjector.DomainOfAsync`** — a reference's area of use in degrees, from PostGIS 3.4's `postgis_srs`.
- **Q-141's datum notice** — a transform that crosses a datum is reported once per layer and target reference.

## 2. Alternatives considered

### Alternative A — the tiling scheme follows the service's reference *(not chosen)*

A service whose `srid` is 5254 is cut on TM30; one with no `srid` stays Mercator.

**Argument for.** One setting, and the two can never disagree.

**Argument against.** The two already mean different things to different people, and the ordinary case is
that they differ: a service that answers its queries in TM30 — because its users measure in it — and is drawn
over a Web Mercator basemap on the web. Every such service that exists would have had its tiles moved to TM30
the day this shipped, changing the bytes and the addresses of tiles clients hold, which the owner's brief
forbids. And a reference is not a grid: 5254 names no origin and no resolutions.

### Alternative B — a tiling scheme per service, separate from its reference, Web Mercator by default *(chosen)*

A service carries a scheme — a reference, an origin, 512-pixel tiles and a list of resolutions — or none, which
is Web Mercator. Built-in schemes for the TUREF zones; custom ones by their numbers or derived from a
reference's area of use.

**Argument for.** Nothing that exists changes. A tile carries every layer of its service, so the service is
the unit a grid can belong to, and it is the unit a seed, the service document and the cache already use.

**Argument against.** One more thing to configure, and one more way for the service document and a client's
cached copy of it to disagree after a change (§6).

### Alternative C — a tiling scheme per layer *(not chosen)*

**Argument against.** A tile is one address with every layer of the service in it
(`VectorTileEndpoints.Concatenate`); two layers on two grids have no common address. It is the service's.

### Alternative D — derive the TUREF grids at startup from the deployment's PROJ *(not chosen)*

**Argument for.** No numbers written down in this repository; the register is the authority.

**Argument against.** A tile address a client has cached would then depend on which PROJ a server was
installed with. A register update that moved an area of use by a hundredth of a degree would rarely move a
kilometre-rounded origin — but rarely is not never, and when it did every tile of every TUREF service would move
under its clients. The numbers are derived once, written down with their derivation (§5.2), and checked against
PostGIS by a test; a service stores the numbers it was given, not a built-in's name.

### Alternative E — reuse `TilingScheme` *(not chosen)*

**Argument against.** Its Web Mercator is the ArcGIS-published half-width 20037508.342787, where the vector
path has used PostGIS's 20037508.342789244 since `TileAddress.WebMercatorHalfExtent` was written, precisely so
a computed envelope agrees with `ST_TileEnvelope` to the last digit. Reusing it would move every Mercator tile
envelope by two millimetres at the edge of the world, and make an image service's 256-pixel grid part of a
vector tile's cache identity. `VectorTileScheme` is a separate type in `Graticula.Core/Tiles`, and its Web
Mercator delegates to the code that answered before it existed.

### Alternative F — `ST_TileEnvelope(z, x, y, bounds)` for every scheme *(not chosen)*

**Argument for.** PostGIS decides where every tile is, for every scheme, as it does for Mercator.

**Argument against.** It cuts a square power-of-two grid from the bounds given, and a custom scheme's
resolutions need not halve (§5.3). The envelope is computed once, by `VectorTileScheme.Envelope`, and bound as
four numbers to `ST_MakeEnvelope`, so one side still decides; for Web Mercator the statement still asks
`ST_TileEnvelope(@z, @x, @y)`, unchanged.

## 3. Counterarguments to the preferred option

**A client that loaded the document before a change draws the new grid's tiles in the old grid's places.**
True for any grid change and not preventable from the server: a tile address means a different piece of ground
after the change. The PUT says so, the console says so, and the change is an administrator's deliberate act.

**Nobody has watched ArcGIS Pro or the ArcGIS Maps SDK draw one of these.** The document follows the published
VectorTileServer contract (`tileInfo` with its own `spatialReference`, `origin` and `lods`; `fullExtent` and
`initialExtent` in the same reference; `minLOD`/`maxLOD`), and the conformance test walks it as a client would;
whether a client accepts it is the first thing to measure (§9).

**Measured 2026-09-30: ArcGIS Pro draws one, in its own grid and in the right place.** The owner switched the
showcase's `turkiye/tr_ref` (a registered PostGIS layer) to TUREF / TM30 from the console — 2,327 cached tiles purged —
and added `…/turkiye/tr_ref/VectorTileServer` to an empty map with *Add Data From Path*. Pro took the map's reference
from the layer (*Map Properties › Coordinate Systems*: TUREF TM30, EPSG:5254) and drew the tiles; with the same
layer's FeatureServer added over it, projected by Pro itself, a polygon near Eskişehir (x ≈ 630,000) lay on the tile
polygon with no visible offset at 1:50,982. **What it showed besides:** the grid is the level-0 tile, 512 pixels at
1,173.83 m, so x 364,000–965,000 and y 3,992,000–4,593,000 — the zone and some 3.5° east of it. Data outside that
square is on no tile (a tile address outside it is a 400), so Thrace and İzmir, west of the origin, and everything
east of about 35°E were not drawn while `fullExtent` still named them (D-288). **And a guess keyed on the level number:** asked to
export levels 0–8 of the same service with its cache just purged, the console estimated **85.34 GB** and refused it.
The seed's size guess (`TileSeedEstimate.DefaultPartBytes`, ADR-093) is a curve over Mercator levels and read TM30's
level 8 as Mercator's, 1 MB a tile; its pixel, 4.59 m, is Mercator's level 14, 64 KB. The guess now takes
`VectorTileScheme.MercatorLevelOf`, the rule §5.7 already applies to generalisation.

**A style's zooms on another grid are INFERRED.** `minzoom` and `maxzoom` narrowed from a visible range are
written in the scheme's own levels (level *n* is the level-0 scale over 2^*n*). That is how the ArcGIS Maps SDK
is documented to read a style against a non-Mercator service; it has not been watched.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| TM30's area of use, and the other six zones' | 28.5–31.5°E, 36.06–41.46°N; the seven zones' areas read from the EPSG register v12.013 (`proj.db`, table `extent`), 2026-09-29; TM30's matches what PostGIS answered on 2026-08-26 | `PostGisProjector.DomainOfAsync`'s remarks; `VectorTileSchemes` |
| The derivation gives the grid the tests assert, worked by hand | TM30 (since D-288, 2026-09-30): origin (97,000, 4,921,000), 3,460.9375 m at level 0, 19 levels; a level-2 tile, a level-3 point at unit (3,356, 3,689) | `VectorTileSchemeTests`, `ATileSchemeIsCutInItsOwnReferenceTests` (needs `GRATICULA_TEST_PG`) |
| A Web Mercator service is unchanged: its envelopes, keys, simplification, visible-range tests, seed counts and service document | Asserted against the pre-existing code for the same inputs | `VectorTileSchemeTests`, `TilingSchemeTests`, `VectorTileServerSchemeDocumentTests` |
| The Mercator statement's text did not change | Every SQL helper answers the pre-ADR-096 text for Web Mercator; `TilePipelineVersionTests`' hash moved and `TilePipeline.Version` did not | `PostGisTileSource.BoundsSql`, `SimplifyWhen`, `FilterBox` |
| A TUREF service's document, tiles and cache follow the grid, and switching back restores the Mercator bytes | `ATilingSchemeIsTheServiceSGridTests` (needs a running server and `GRATICULA_TEST_TILE_SERVICE`) | this ADR |
| Not measured: any ArcGIS client drawing a VectorTileServer in TUREF; tile build time on TM30 against Mercator | — | §9 |

## 5. Decision

**A vector tile service is cut on Web Mercator unless an administrator sets it to another tiling scheme. A
scheme is a projected reference, an origin, 512-pixel tiles and a list of resolutions; fourteen TUREF schemes
are built in, and any other is given by its numbers or derived from its reference's area of use. A service on
another scheme is served, encoded, cached, seeded and described on that grid, and a Web Mercator service is
served exactly as before — no Mercator tile's bytes or key moved.**

### 5.1 A service carries a scheme — storage and rollback

`service.tiling_scheme jsonb`, **migration 63**, `Expand`: null is Web Mercator, which every existing service is.
The whole grid is stored — `{"id","wkid","origin":{"x","y"},"tileSize":512,"resolutions":[…]}` — never only a
built-in's name, so a later build that changed a built-in would not move a service already set to it.
`PublishedService.TileScheme` carries it (default `VectorTileScheme.WebMercator`); a stored grid this build
cannot read is carried as `TileSchemeUnreadable` and the tile face answers 500 naming it, rather than serving
Web Mercator in its place.

**Rollback (INFERRED acceptable).** A build before this one never reads the column, so it serves *every*
service in Web Mercator — those set to another scheme included — with the Mercator statement and the Mercator
keys it always used. Those keys never collide with another grid's (§5.5). A client holding the other grid's
`tileInfo` draws nothing until it reloads the service document. So a service on another scheme is not
unavailable after a rollback; it is served on Web Mercator.

### 5.2 Built-in schemes — the TUREF zones, and the arithmetic

`VectorTileSchemes.BuiltIn`: `turef-tm27` … `turef-tm45` (EPSG:5253–5259, false easting 500 km) and
`turef-gk9` … `turef-gk15` (EPSG:5269–5275, the register's 3-degree Gauss-Krüger codes for the same zones, the
zone number in front of the easting). *INFERRED*: the owner named *TUREF TM zones and ITRF96-based ones*; TUREF
is Turkey's realisation of ITRF96, and these two families are every TUREF projected zone the register has.
ED50 / TM30 (EPSG:2320) and anything else is a custom scheme.

**Amended 2026-09-30 (D-288): each zone's grid is derived from the whole country, not the zone.** As first built,
each grid came from the zone's own area of use, so TM30's was one 601-km tile from 28.5°E, and ArcGIS Pro showed
what that means: Thrace, İzmir and everything east of about 35°E were on no tile while the service's extent still
named them (§3, *Measured 2026-09-30*). ArcGIS's own tiling schemes for a projected reference put the origin far
outside the data for this reason. The ground each grid is derived from is now `VectorTileSchemes.Country` —
TUREF's own area of use as the register states it for EPSG:5252, Türkiye onshore and offshore, 25.62–44.83°E and
34.42–43.45°N — projected into the zone by PostGIS, edges segmentized at 0.05°, frozen beside the zone's own area
of use (`BuiltInTileScheme.CoversProjected`) and checked against PostGIS again, box and register both, by
`Every_built_in_s_country_projects_where_its_numbers_say`. **Offshore, by the owner's decision the same day:** the
first cut of this amendment used the seven zones' onshore union (35.81–42.15°N), and 13 of the showcase layer's
60 polygons, in the Black Sea, were still off the grid. How far from its
meridian a map is drawn is its publisher's choice; the grid no longer makes it for them. The owner's rule — the
origin at the covered area's upper-left corner, one tile at level 0, resolutions halving — is kept; only the area
it is applied to moved. A reference named by its EPSG code alone that is a built-in's gets the built-in's grid
under the custom id, so `wkid: 5254` and `turef-tm30` are still one grid. **A service already set to a zone keeps
the numbers it was given** (§5.1); it moves to the new grid when its scheme is set again.

Each is derived (`VectorTileScheme.Derive`) from the country so projected, by four steps:

1. **Origin**: the area's top-left corner, rounded outward to the kilometre (west down, north up).
2. **Span**: the area's longer side measured from that origin, rounded up to the kilometre — level 0 is one
   tile of it.
3. **Level-0 resolution**: span / 512, exact in binary because 512 is a power of two.
4. **Levels** halve until a pixel is no bigger than Web Mercator's at z22 (0.0186614 m).

| Zone | Origin (m) | Span | Level 0 (m/px) | Levels |
|---|---|---|---|---|
| TM27 (5253) | 373,000 / 4,970,000 | 1,776 km | 3,468.75 | 19 |
| TM30 (5254) | 97,000 / 4,921,000 | 1,772 km | 3,460.9375 | 19 |
| TM33 (5255) | −180,000 / 4,882,000 | 1,771 km | 3,458.984375 | 19 |
| TM36 (5256) | −457,000 / 4,866,000 | 1,770 km | 3,457.03125 | 19 |
| TM39 (5257) | −735,000 / 4,901,000 | 1,772 km | 3,460.9375 | 19 |
| TM42 (5258) | −1,014,000 / 4,945,000 | 1,775 km | 3,466.796875 | 19 |
| TM45 (5259) | −1,294,000 / 4,999,000 | 1,781 km | 3,478.515625 | 19 |

(Until 2026-09-30, from each zone's own area: TM30 was 364,000 / 4,593,000, 601 km, 1,173.828125 m, 17 levels.)

The Gauss-Krüger zone *n* is the same grid with *n* × 1,000,000 added to the origin's easting. *INFERRED*: the
kilometre rounding and the z22 floor are this session's; the owner's description fixed the origin's corner and
one tile at level 0.

### 5.3 Custom schemes

A projected EPSG code with an origin and either `level0Resolution` + `levels` (halving) or an explicit
`resolutions` list, coarsest first, 1 to 30 levels, each finer than the last. Level 0 is one tile; a level's tile
count is level 0's span over its own, rounded up, so a list that does not halve is a grid too. **A code alone**
is derived from its area of use by §5.2's rule — `DomainOfAsync`, then the area moved into the reference on a
16-cell grid a side — and stored as numbers (*INFERRED*: the owner described the derivation for TUREF; it is
offered for any projected reference). Refused: Web Mercator's codes (it is the default, not a custom grid), a
geographic reference (scales and generalisation are in metres), a code the projector does not know, a request
mixing a name and numbers. **Metres are assumed**: a reference in US feet is tiled correctly with its scales
3.28 times too large — named, not refused, because nothing detects it cheaply and nothing asked for it.

### 5.4 The admin API — separate from the service's reference

- `GET /admin/tiling-schemes` — Web Mercator and the built-ins, each with its grid.
- `GET /admin/services/{name}/tiling?folder=` — the service's grid, whether the stored one is unreadable, and
  the built-ins laid out in a reference its layers are stored in (`suggested`).
- `PUT /admin/services/{name}/tiling?folder=` — `{"scheme":"turef-tm30"}`, `{"scheme":"webmercator"}` (or an
  empty body), `{"wkid":5254}`, or `{"wkid":…, "origin":{…}, "level0Resolution":…, "levels":…}` /
  `"resolutions":[…]`. **200** with the grid, `changed`, `tilesPurged` and `seedsCancelled`; **400** naming what
  is wrong; **404** for no such service at that folder; **409** when the service is the map ground (§5.9).

**Its own route, not `/srid`'s** (§2 Alternative A). **The same privilege, addressing and audit as `/srid` and
`/capabilities`**: `admin:manageServer` — which grid a service is served on is a serving decision; the service
by folder and name, `?folder=` absent meaning the root and never *any folder* (D-275); `service.tiling` in the
audit log with both grids and the counts.

### 5.5 Cache key, purge and seeding

- **The key carries the grid.** `TileCacheKey.FingerprintOf` gained a last, optional `grid`; `KeyOf` passes
  `VectorTileScheme.Fingerprint` — every number that places a tile, canonically written — and null for Web
  Mercator, which appends nothing. So no Mercator key moved, and a service switched between grids, or between
  two definitions of one, never reads the other's tiles — on this node or on one the switch's purge did not reach.
- **A change purges** the service's layers' tiles on this node and **cancels** its queued and running seeds.
- **A seed is counted on the service's grid.** `TileSeedPlan.For(scheme, area, min, max)` counts each level's
  rectangle over the scheme's own tiles (Web Mercator goes to the old `For`, unchanged). The area is kept in the
  scheme's reference; one given in 3857 or 4326 is moved into it. The seed's job detail records the grid's key
  (`scheme`, only for another grid, so a Mercator seed's detail is what it was), and the worker fails a seed whose
  service is on a different grid when it starts or resumes. The size estimate is unchanged except that its levels
  are the scheme's.

### 5.6 Encoding

The same statement (ADR-021), with three things taken from the scheme: the envelope
(`ST_MakeEnvelope(@minx, @miny, @maxx, @maxy, srid)` from `VectorTileScheme.Envelope`), the reference rows are
moved into (the scheme's, not 3857), and the simplification switch (§5.7). Extent 4096 and buffer 64 are
unchanged. The `&&` box is moved into the layer's reference **densified to sixteen points an edge** for another
scheme, because a transverse Mercator square bows by about 1.2 km over a 600 km level-0 tile when moved into
degrees; the Mercator statement keeps its four corners. A layer in the scheme's own reference is not moved at
all. The datum notice (Q-141) names the pair actually crossed — a TUREF layer on a TUREF grid crosses none.

**GeoParquet and DuckDB layers are supported, not refused**: their read asks for the tile's box in the scheme's
reference (densified likewise) and `IMvtEncoder` gained a form taking the scheme, whose default refuses
anything but Web Mercator loudly so an encoder written before this cannot cut a TUREF tile on a Mercator grid.
**PostGIS 3.0 or later** is still the requirement (for the Mercator statement's `ST_TileEnvelope`; the other
statement uses only `ST_MakeEnvelope`, `ST_Segmentize` and `ST_Transform`), and deriving from a code alone needs
3.4's `postgis_srs`.

### 5.7 Generalisation keyed by the pixel — ADR-085 amended

ADR-085's tolerance was always a pixel's size read off the tile's own width, so it needed nothing. Its switch —
*simplify through z14* — was a level number. For another scheme a level is simplified when its pixel is at least
Web Mercator's z14 pixel, 4.777314 m (`VectorTileScheme.SimplifiedDownToResolution`), decided in C# and bound as
`@simplify`; for Web Mercator the statement still says `@z <= 14`, which is the same rule. TM30 is simplified at
levels 0–7 (9.17 m) and not from level 8 (4.59 m).

### 5.8 Visible range and style zooms — ADR-070

A level's tile is on screen from its own scale (resolution × 96 × 39.37) down to the next level's; a layer is in
it when that interval meets its range (`VisibleScaleRange.CarriesTileBetween`, split out of `CarriesVectorTile`
unchanged). For Web Mercator the scales are `VectorTileScale(z)` and half of it, as before. A style's `minzoom`
and `maxzoom` are narrowed on the scheme's own levels — log2 of its level-0 scale over the range's (*INFERRED*,
§3).

### 5.9 Documents, tile map and what else changes

- **The service document** of a scheme states it everywhere a reference is read: `tileInfo` (origin, 102100
  replaced by the scheme's wkid, `lods` with the scheme's resolutions and scales), `fullExtent` and
  `initialExtent` in the scheme's reference (each layer's extent moved there, sampled along its edges), and
  `minLOD`/`maxLOD`. An unknown extent is the scheme's frame. A Web Mercator document is byte for byte the old one.
- **`tile/{z}/{y}/{x}` and `tilemap`** address the scheme's grid, with its level range and tile counts.
- **`root.json` and the named styles** are unchanged except for the narrowed zooms (§5.8).
- **The map ground (ADR-086) stays Web Mercator.** A service on another grid is not offered as a ground
  candidate, is refused as one, and a ground service is refused another grid (409) — the ground is drawn beneath
  Web Mercator maps and a client cannot move a vector tile between grids.
- **The console** offers the choice on the service's Capabilities page, under the tile face: Web Mercator, then
  the built-ins in a reference the service's layers are stored in, then the rest; saved with the page, and only
  when it moved. Its preview map is Web Mercator, so a service on another grid previews from its features, not its
  tiles.

### 5.10 The tile pipeline's generation

`TilePipeline.Version` stays at 2: no Mercator tile's bytes or key moved. `TilePipelineVersionTests`' hash was
recorded again with a dated note, and `VectorTileScheme.cs` joined its list, because it now decides where a tile
is and when it is simplified.

## 6. Consequences

**Positive.** A service in TUREF can be a basemap in TUREF; the grid is the service's own, cached, seeded and
described consistently; nothing that exists changed.

**Negative.**

- **Not watched against a client.** Whether ArcGIS Pro and the ArcGIS Maps SDK accept the document and draw the
  tiles, how they read the style's zooms, and whether they want anything further (a `tileMap` of their own, a
  specific `minLOD`) is unknown until measured (§9).
- A grid change makes every client's cached service document wrong until it reloads.
- A custom scheme in a foot-based reference states wrong scales (§5.3).
- The console preview and the map ground cannot draw a service's tiles on another grid; they show its features.
- `GET /admin/services/{name}/cache` reads back levels last seeded on a grid the service has since left against
  the new grid; the switch emptied their tiles, so they read back as not cached, and a level the new grid does not
  have is left out.

**Ports created.** None. `IMvtEncoder` gained a second member with a default body; `ITileSource` is unchanged in
shape, and its two implementations take the scheme in their constructors.

**State.** *Catalogue*: `service.tiling_scheme` (migration 63), and the grid's key in a seed's job detail.
*Runtime*: none new. The tiles are ADR-010's cache, node-local on disk; a grid change's purge reaches this node
only, and the grid in the key is what holds on the others. The built-in grids are constants in the build.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | None in the register. The unverified claim — that ArcGIS clients draw a VectorTileServer whose `tileInfo` is in a projected reference other than Web Mercator — is recorded in §3 and §9 rather than as an assumption, because it is the next thing to measure, not something a decision rests on quietly | — |

## 8. Dependencies

**Depends on:** [ADR-021](ADR-021-tile-encoding.md) (the statement), [ADR-085](ADR-085-a-tile-leaves-out-what-it-cannot-draw.md),
[ADR-070](ADR-070-a-layer-has-a-visible-scale-range.md), [ADR-093](ADR-093-seeding-the-tile-cache.md),
[ADR-010](ADR-010-caching.md), [ADR-057](ADR-057-composing-and-publishing-a-service.md) §5c (kept separate).

**Depended on by:** the map ground ([ADR-086](ADR-086-the-operator-chooses-the-map-ground.md)), which refuses
another grid.

## 9. Revisit triggers

- **The first time an ArcGIS client is pointed at a TUREF service**: if it refuses the document, draws tiles in
  the wrong place, or reads the style's zooms differently from §5.8, this ADR is reopened on what it did.
- The EPSG register changes a TUREF zone's area of use (a `proj.db` upgrade): `ATileSchemeIsCutInItsOwnReferenceTests`
  compares every built-in's area of use and its projection with PostGIS's and fails first. Existing services keep
  their stored numbers either way.
- A request for a geographic (4326) vector tile scheme, or a foot-based one.
- A second face (ImageServer tiles, WMTS) wanting the same grids — the moment to decide whether `TilingScheme`
  and `VectorTileScheme` become one type. *Fired 2026-09-29 by [ADR-097](ADR-097-vector-tiles-through-ogc-api-tiles-tilejson-and-wmts.md)
  (OGC API Tiles and WMTS), and answered without merging: its `TileMatrixSet` describes a `VectorTileScheme` in
  17-083r4's words and carries it, placing no tile of its own, and ImageServer's `TilingScheme` is untouched.*

## 10. Dissent

None recorded. The closest to it is §2 Alternative A: one setting is simpler to explain, and an administrator
may be surprised that choosing a service's reference does not move its tiles. The answer is the console putting
the two controls on the service's pages and suggesting the grid of the data's own reference.

## 11. INFERRED, for confirmation

1. The fourteen built-ins (TM and Gauss-Krüger families), and ED50 left to custom schemes (§5.2).
2. The kilometre rounding and the z22 floor on the level count (§5.2).
3. A code alone derived by the same rule for any projected reference (§5.3).
4. Rollback serves a non-Mercator service in Web Mercator, and that is acceptable (§5.1).
5. The tiling scheme is separate from the service's reference (§5.4).
6. Style zooms counted on the scheme's own levels (§5.8).
7. The map ground stays Web Mercator and refuses another grid (§5.9).
8. `admin:manageServer` for the route, as `/srid` and `/capabilities` (§5.4).
