# ADR-097 — Vector tiles are served through OGC API Tiles, TileJSON and WMTS

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `MEDIUM` — the grids, documents and refusals are tested against numbers typed in from the standards, and the one tile path is shared by construction; no OGC API Tiles, TileJSON or WMTS client has been watched against this server (§6, §9) |
| **Decided** | 2026-09-29, by owner decision. The owner asked for the vector tile face's remaining gaps to be closed one at a time; this is item 12 on that list, and the owner stated it directly: **serve the vector tiles through the other standards — OGC API Tiles, TileJSON and WMTS — each serving the same tiles the VectorTileServer face serves.** The owner also moved **PMTiles out of this item**, to be built with offline packages (VTPK) as an *export* under item 11. Where each face lives, the tile matrix set chosen for Web Mercator, the collection id, where TileJSON is served and what is refused are this session's design and are marked **INFERRED** where they matter (§11). |
| **Depends on** | [ADR-005](ADR-005-api-architecture.md) §3.3, [ADR-042](ADR-042-ogc-api-features.md), [ADR-039](ADR-039-wfs-is-the-first-surface-after-v1.md), [ADR-096](ADR-096-a-vector-tile-service-may-be-tiled-in-another-reference.md), [ADR-093](ADR-093-seeding-the-tile-cache.md) §5.5, [ADR-018](ADR-018-authorization-and-roles.md), [ADR-031](ADR-031-service-capability-configuration.md), [ADR-049](ADR-049-a-face-refuses-in-its-own-vocabulary.md), [ADR-068](ADR-068-responses-are-compressed.md), [ADR-069](ADR-069-query-responses-carry-the-layer-s-cache-lifetime.md), [ADR-070](ADR-070-a-layer-has-a-visible-scale-range.md) |
| **Amends** | [ADR-005](ADR-005-api-architecture.md) §3.3 (WMTS is built, for vector tiles only); [ADR-096](ADR-096-a-vector-tile-service-may-be-tiled-in-another-reference.md) §9 (its *second face wanting the same grids* trigger fired, and is answered in §5.2); [v1-scope](../v1-scope.md) §3d (OGC API Tiles, TileJSON and WMTS leave the deferred list) |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

*Written from the published specifications only*: OGC 20-057 (OGC API – Tiles – Part 1: Core), OGC 17-083r4
(Two Dimensional Tile Matrix Set and Tile Set Metadata 2.0), OGC 07-057r7 (WMTS 1.0.0), OGC 06-121r9 (OWS
Common 1.1) and the TileJSON 3.0.0 specification. Nothing here was read from another server's source.

---

## 1. Context

Since ADR-021 this server has cut Mapbox Vector Tiles and served them one way: the ArcGIS VectorTileServer
face, `/rest/services/{folder}/{name}/VectorTileServer/tile/{z}/{y}/{x}.pbf`, with its service document and
style. That reaches the ArcGIS clients and MapLibre through the style. It does not reach a client that asks
for tiles by an OGC standard — OGC API Tiles, WMTS — or one that is handed a TileJSON document, which is how
most of the web vector tile ecosystem is configured.

[protocol-surface.md](../protocol-surface.md) §2 put OGC API Tiles in *Tier A — near-free: the same bytes we
already serve, behind the standard URL template and tileset metadata*, and [v1-scope](../v1-scope.md) §3d kept
it, WMTS and PMTiles outside v1. The owner opened them on 2026-09-29, and took PMTiles out to go with offline
packages as an export.

**The near-free claim has one condition, and it is the whole design.** The three faces are cheap only if they
serve the tiles the ArcGIS face serves — the same cache entry, the same bytes, the same admission — rather than
a second tile path that encodes, caches or admits differently. ADR-093 §5.5 met the same requirement for the
seed: *a second computation of the key anywhere else would be a seed that fills a cache nobody reads, and
nothing would fail.*

What already existed and was read before deciding:

- **`VectorTileEndpoints`** — the tile route, `LayerPartAsync`, `CachedOrBuiltAsync`, `KeyOf`, the D-277
  admission and `WriteTileAsync`'s `Cache-Control`, `Age`, weak ETag and 304.
- **`VectorTileScheme`** (ADR-096) — the grid a service is cut on: Web Mercator by default, fourteen built-in
  TUREF grids, or custom.
- **OGC API Features at `/ogc/features/v1`** (ADR-042), whose §5.1 versioned the path *"so it leaves room for
  `/ogc/tiles/v1` and `/ogc/styles/v1` beside it"*.
- **WFS at `/wfs` and WMS at `/wms`** (ADR-039, ADR-041), governed by listing what a caller may see.

## 2. Alternatives considered

### Alternative A — extend `/ogc/features/v1`'s collections with tile links *(not chosen)*

Each features collection gains a `tilesets-vector` link, as 20-057 §11 (*geodata tilesets*) describes, and the
tiles hang under it.

**Argument for.** One landing page, one conformance document, one collection per layer, the shape OGC API
Common expects of a server offering several building blocks.

**Argument against.** A features collection is a **layer** (ADR-042 §5.2) and a tile is a **service**: every
layer of the service, concatenated (`VectorTileEndpoints.Concatenate`), cached per layer and served per
service. A per-layer tileset would be tiles nobody builds and nobody has cached, with their own admission cost,
and a client drawing a three-layer service would ask for three tiles where the ArcGIS face asks for one. The
features face also lists a layer whose service has its tile face off, and would need a second visibility rule
for its tile links.

### Alternative B — `/ogc/tiles/v1`, a collection per vector tile service *(chosen)*

Its own landing page, conformance and collections; one collection per service the ArcGIS face would serve
tiles for, with the service's layers listed in its tileset's `layers`, which is the shape 17-083r4's
`TileSetMetadata` gives a multi-layer vector tileset (Annex H's own example has two layers in one tileset).

**Argument for.** The unit is the one the tile path already has, so a tile here *is* a tile there. ADR-042 §5.1
reserved the address.

**Argument against.** A second OGC collections document whose ids are not the features face's (§5.7).

### Alternative C — a WebMercatorQuad declared at 512 cells *(not chosen)*

The tiles are drawn at 512 pixels (`VectorTileScheme.TileSize`); say so in the set.

**Argument against.** 17-083r4 §6.1.1 derives a tile's extent from its matrix: *cellSize = scaleDenominator
× 0.28 × 10⁻³ / metersPerUnit* and *tileSpan = tileWidth × cellSize*. Keep the registered scales and write
512, and the set describes tiles twice the width of the ones served; halve the scales to keep the extents, and
it is not `WebMercatorQuad` — yet it would carry that id and URI, which 20-057 `/req/tileset/description` C
tells a client to trust. Either way a client that reads the set is told something false.

### Alternative D — a custom set `WebMercatorQuad512` beside or instead of the registered one *(not chosen)*

Honest arithmetic: 512 cells, cells half the registered size, scales at 0.28 mm.

**Argument for.** It says what the ArcGIS `tileInfo` says.

**Argument against.** It is the registered grid under another name. Its levels, matrix sizes and tile
boundaries are `WebMercatorQuad`'s to the digit, so a client that recognises Web Mercator by URI — the case
20-057 Recommendation 6 makes the interoperable one — would not recognise it, and one that offers both would be
offering the same tiles twice. And the premise is weaker than it looks: **a vector tile has no cells.** Its
coordinates are a 4096-unit grid (ADR-021); "256" and "512" are two conventions for the size a client *draws*
it at, and the tile is the same bytes either way.

### Alternative E — the registered `WebMercatorQuad`, verbatim *(chosen)*

256 cells, Table C.4's scales and cells, the registered URI and well-known scale set. Level *z*, row *r*, column
*c* is the ArcGIS face's `tile/z/r/c`. The 512-pixel convention stays where it means something — the ArcGIS
`tileInfo` and the style's zooms — and 20-057 itself says a TileJSON document *usually implies a WebMercatorQuad
TileMatrixSet*.

### Alternative F — TileJSON as its own resource under the ArcGIS path *(not chosen)*

`…/VectorTileServer/tilejson.json`, beside `root.json`.

**Argument for.** Next to the style, which already names the tile URL.

**Argument against.** It puts a non-ArcGIS resource in the ArcGIS namespace for a second time (ADR-094 did it
for named styles, because ArcGIS has no address for them), and it would template the ArcGIS `{z}/{y}/{x}` URL
under a document that belongs to no ArcGIS client. 17-083r4 Annex H's example gives TileJSON as the
`alternate` representation of a tileset — `…/tiles/WebMercatorQuad?f=tilejson` — which is where this server
already describes the same tiles in a standard's words.

### Alternative G — WMTS by KVP only *(not chosen)*

**Argument against.** 07-057r7 §10 defines a RESTful binding whose `ResourceURL` template is what most WMTS
clients prefer when it is offered, and it costs one route that reads its path into the same KVP parser.

## 3. Counterarguments to the preferred option

**Nobody has pointed a client at any of the three.** The documents are written to the specifications and
checked against numbers typed in from them; what a real OpenLayers, GDAL, QGIS or MapLibre does with them is
unobserved (§6, §9). **WMTS for vector tiles is the weakest of the three**: 07-057r7 was written for images,
nothing in it forbids `application/vnd.mapbox-vector-tile`, and client support is thin — QGIS's WMTS reader
and ArcGIS's WMTS layer expect images. It is built because the owner named it and because it costs one parser
over the one tile path, not because a client is known to need it.

**The 256-cell set invites a client to draw a tile at 256 pixels.** Such a client shows level *z* one level
coarser on screen than an ArcGIS or MapLibre client does, and a layer's visible range (ADR-070), which is
evaluated at the ArcGIS 512-pixel scales, then switches on and off at a screen scale half the one an operator
set. The tiles are correct; which level a client asks for at a given screen scale is the client's choice, and
it is the convention the registered set states.

**The collection id is not collision-free** (§5.7).

**Every listing reads the whole catalogue.** A collections or capabilities document lists every service with its
layers, as WFS, WMS and OGC API Features already do — and ADR-042's measurement stands: WMS capabilities
extrapolate to about 3.3 s cold at 1,000 services. A tile request does not list: it reads the one or two services
its id can name, by name, as the ArcGIS route does (§5.5).

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| `WebMercatorQuad`'s numbers: level 0 at 559,082,264.0287178 and 156,543.0339280410 m, level 15 at 4.777314267823516 m; `cellSize = scaleDenominator × 0.28 mm` at every level | Typed in from 17-083r4 Table C.4 and asserted | `StandardTileFacesTests.WebMercatorQuad_is_the_registered_definition_with_its_0_28_mm_scales` |
| The 256-cell set and the 512-pixel ArcGIS grid are one grid: the OGC cell at *z* is twice this server's pixel at *z*; 295,828,763.795777 (96 dpi, 512) and 559,082,264.0287178 (0.28 mm, 256) state the same ground per tile | Asserted for levels 0–22 | `StandardTileFacesTests.The_OGC_and_ArcGIS_scales_describe_one_grid_by_two_conventions` |
| A tile's box by 17-083r4 §6.1.1's arithmetic is the box the tile path builds, for Web Mercator, TM30 and a Gauss-Krüger zone | Asserted at five levels and three tiles each | `StandardTileFacesTests.A_tile_is_where_the_standard_s_arithmetic_puts_it` |
| Row is `TileAddress.Y`, column is `TileAddress.X`, and the set carries the service's own scheme object | Asserted | `StandardTileFacesTests.Row_is_y_and_column_is_x_and_the_grid_is_the_service_s_own` |
| WMTS capabilities are well formed, in the WMTS 1.0 and OWS 1.1 namespaces, with TM30's corner northing first | Parsed and walked | `StandardTileFacesTests.The_capabilities_are_WMTS_1_0_0_with_each_layer_s_set` |
| KVP refusals carry 07-057r7 Table 28's codes and statuses | Nine cases | `StandardTileFacesTests.A_wrong_request_is_refused_as_WMTS_refuses_it` |
| A tile through OGC API Tiles, WMTS KVP and WMTS REST is the ArcGIS face's bytes and ETag, and a `HIT` after the ArcGIS request | Needs a running server and `GRATICULA_TEST_TILE_SERVICE` | `StandardTileFacesConformanceTests.A_tile_is_the_same_bytes_and_the_same_cache_entry_on_every_face` |
| A private service is absent on all three faces to an anonymous caller and present to its reader | Needs a running server | `StandardTileFacesConformanceTests.A_private_service_is_invisible_to_an_anonymous_caller_on_every_face` |
| Every conformance class claimed has an observation, and nothing observed is unclaimed | Needs a running server | `StandardTileFacesConformanceTests.Every_conformance_claim_is_proven_and_every_proof_is_claimed` |
| The TileJSON template, filled in, is the ArcGIS face's tile | Needs a running server | `StandardTileFacesConformanceTests.The_TileJSON_template_resolves_to_a_real_tile` |
| No tile's bytes or key moved | `TilePipelineVersionTests`' hash moved with a dated note for the split of `ServeTileAsync`; `TilePipeline.Version` did not | `VectorTileEndpoints.ServeTileAsync` |
| Not measured: any client drawing any of the three faces | — | §9 |

## 5. Decision

**A vector tile service is also served as an OGC API Tiles collection at `/ogc/tiles/v1`, as a WMTS 1.0.0 layer
at `/wmts`, and — on Web Mercator — as a TileJSON 3.0.0 document, the alternate of its OGC tileset. Each face
finds the service by the ArcGIS tile face's rules, reads its address into the service's own grid, and serves the
tile through `VectorTileEndpoints.ServeTileAsync`, the ArcGIS route's own body: one cache entry, one set of
bytes, one admission and one set of caching headers for the same tile on every face.**

### 5.1 OGC API Tiles at `/ogc/tiles/v1`

| Resource | Path |
|---|---|
| Landing page | `/ogc/tiles/v1` |
| Conformance | `/ogc/tiles/v1/conformance` |
| Collections (one per vector tile service) | `/ogc/tiles/v1/collections`, `/collections/{collectionId}` |
| Tilesets list | `/collections/{collectionId}/tiles` |
| Tileset metadata; `?f=tilejson` for TileJSON | `/collections/{collectionId}/tiles/{tileMatrixSetId}` |
| A tile, `application/vnd.mapbox-vector-tile` | `/collections/{collectionId}/tiles/{tileMatrixSetId}/{tileMatrix}/{tileRow}/{tileCol}` |
| Tile matrix sets | `/ogc/tiles/v1/tileMatrixSets`, `/tileMatrixSets/{tileMatrixSetId}` |

- **Claimed**: Tiles `core`, `tileset`, `tilesets-list`, `geodata-tilesets`, `mvt`; TMS `tilematrixset`,
  `json-tilematrixset`, `tilesetmetadata`, `json-tilesetmetadata`. Each is bound to an observation in the
  conformance suite in both directions, as ADR-005 condition 2 was met for the features face.
- **Links** carry the full OGC relation URIs the requirements name: `tilesets-vector` on a collection,
  `tiling-scheme` on a tileset and in a tilesets list, `tiling-schemes`, `conformance` and `data` on the
  landing page; the tile template is an `item` link with `templated: true` and the MVT media type; a tileset on
  the registered set gives `tileMatrixSetURI`.
- **A tileset lists the service's layers** with `geometryDimension`, `minTileMatrix`/`maxTileMatrix` — the
  levels `VectorTileScheme.Draws` puts the layer in a tile at (ADR-070) — and a `propertiesSchema` of the
  attributes the tile *carries* (`VectorTileEndpoints.AttributesOf`, at most twelve), not the table's columns.
- **HTML** by `f=html` or `Accept`, through the features face's generic page, as on ADR-042's face.
- **Refusals** are RFC 7807 problems with truthful statuses (ADR-042 §5.6): 404 for a collection, tileset or
  set the caller cannot see or that does not exist — one answer (ADR-018) — 400 for an address outside the
  grid, 406 for TileJSON of a grid it cannot describe, 503 when the catalogue can say nothing. An empty tile is
  **204** (`/req/core/tc-error` B allows it), as on the ArcGIS face.

### 5.2 The tile matrix sets — 256 against 512, and 0.28 mm against 96 dpi

- **Web Mercator is the registered `WebMercatorQuad`, verbatim** (§2 Alternative E): 256 cells, 23 levels,
  Table C.4's scales and cells, computed from PostGIS's half-width (`TileAddress.WebMercatorHalfExtent`, which
  the register rounds to 20037508.3427892), `orderedAxes` `["X","Y"]`, well-known scale set
  `GoogleMapsCompatible`.
- **Every other grid is a set this server defines, at 512 cells.** No register holds the TUREF grids, so there is
  no convention to keep: `cellSize` is the scheme's resolution exactly — the number the admin API and the ArcGIS
  `tileInfo` state — `tileWidth` 512, `matrixWidth` the scheme's tile count. A built-in grid is named by its id
  (`turef-tm30`) **only when the service's stored numbers are the built-in's** (ADR-096 §5.1: a service keeps
  the numbers it was given); anything else is `custom-` plus the grid's key.
- **The scale is 0.28 mm, the OGC standardized rendering pixel** (17-083r4 §6.1.1, WMTS 1.0 §6.1), not the
  96 dpi ArcGIS uses: TM30's level 0 is 3,400.390625 m a pixel and 12,144,252.23… here (1,173.828125 and 4,192,243.30… before
  ADR-096's D-288 amendment). The two faces state two
  scale columns for one grid, each right in its standard, and neither is copied into the other.
- **Axis order is the reference's authority's** (ADR-060): `pointOfOrigin` and WMTS `TopLeftCorner` are
  northing first for every TUREF grid, and `orderedAxes` says `["N","E"]`.
- **`tileMatrixSetLimits` are not published.** `/req/core/tc-error` makes a tile outside a tileset's limits a
  400 or 404, and the ArcGIS face answers the same address 204. The extent is in `boundingBox` instead.
- **ADR-096 §9's trigger — *a second face wanting the same grids* — is answered without merging types**:
  `TileMatrixSet` *describes* a `VectorTileScheme` and carries it; it computes no envelope and places no tile.

### 5.3 TileJSON 3.0.0

`/ogc/tiles/v1/collections/{id}/tiles/WebMercatorQuad?f=tilejson`, served `application/json` and linked from the
tileset as `alternate` with Annex H's `application/json+vnd.mapbox.tilejson`. `tiles` is the OGC tile route
templated `{z}/{y}/{x}`; `vector_layers` gives each layer's id, the fields the tile carries and the levels it is
drawn at (a layer drawn at no level is left out); `minzoom`/`maxzoom` are the service's (ADR-070's
`ServiceRange`), `bounds` its extent in WGS 84 clamped to the Mercator band, `center` the middle of the data at
the coarsest level that holds it, inside the zoom range. **Web Mercator only**: a TUREF tileset offers no
alternate and `?f=tilejson` on it is a 406 saying TileJSON has no field for another grid. **The template never
carries a token**, even when the request did; a client reading a private service attaches its own header.

### 5.4 WMTS 1.0.0 at `/wmts`

- **KVP** at `/wmts`: `GetCapabilities` (negotiated by `AcceptVersions`) and `GetTile` with all nine
  parameters required, names without case and values with it (OWS Common §11.5.2). `GetFeatureInfo` is
  `OperationNotSupported`.
- **RESTful**: `/wmts/1.0.0/WMTSCapabilities.xml`, and
  `/wmts/1.0.0/{layer}/{style}/{tileMatrixSet}/{tileMatrix}/{tileRow}/{tileCol}.pbf`, read into the same KVP
  parser so it cannot accept what KVP refuses.
- **Capabilities**: each service a `Layer` — identifier the OGC collection id, `ows:WGS84BoundingBox`, one
  `Style` `default`, one `Format` `application/vnd.mapbox-vector-tile`, one `TileMatrixSetLink` and a
  `ResourceURL` template; each set once in `Contents`, `WebMercatorQuad` with the
  `urn:ogc:def:wkss:OGC:1.0:GoogleMapsCompatible` scale set and 256 cells, TUREF and custom sets at 512 with
  their corners in the reference's axis order. Both bindings are described in `OperationsMetadata`.
- **Refusals** are OWS 1.1 `ows:ExceptionReport`s with Table 28's code, locator and status — 400, 501, 500 —
  not WMS's 200. A layer the caller may not see is `InvalidParameterValue` on `LAYER` with the wording of one
  that does not exist. A row or column outside the matrix is `TileOutOfRange`.
- **An empty tile is 204**, as on the other faces; WMTS does not say what a tile with nothing in it is.

### 5.5 One tile path, and one cache

`VectorTileEndpoints.TileAsync`'s body after its address check became `ServeTileAsync`, unchanged, and all four
faces call it. The address each face reads is `TileAddress(z = tileMatrix, x = tileCol, y = tileRow)` in the
service's own grid, checked by the set and then by `VectorTileScheme.Rejection`, so no face can admit a tile the
ArcGIS route would refuse. Keys (`KeyOf`), single-flight, D-277's admission, `Cache-Control` from the layers'
lifetimes (ADR-069), `Age`, the weak ETag and 304, and `X-Tile-Cache` are therefore the ArcGIS face's. A tile
request finds its service **by name, not by listing**: the id itself at the root and each `folder.name` split of
it, one catalogue read each, as the ArcGIS route reads one.

### 5.6 Governance

- **Sharing and the tile face's own checks, in the ArcGIS face's order**: a service is served on these faces
  exactly when `/rest/services/…/VectorTileServer` would serve it — running (even for an administrator, as the
  tile face refuses a stopped service to everybody), its tile face on (`Limits.AllowsTiles`, and off is *absent*,
  ADR-031 condition 2), at least one layer, every layer tileable, a readable grid, and readable by the caller
  (`LayerAccess`). **While the catalogue is unreachable only public services survive**, `ServiceLookup`'s rule.
  An administrator's override is audited when used (ADR-018 condition 3), and the service's request deadline is
  applied, as `ServiceLookup` does.
- **Every route is marked `ByFiltering`** in the `/admin/routes` audit, as the other standard faces are, and
  `/ogc/tiles` and `/wmts` joined `AdminEndpoints.Served`.
- **Tokens** are the server's middleware's: `Authorization: Bearer`, `X-Esri-Authorization`, the session cookie
  on GET, `?token=` where the deployment allows it. As on `/ogc/features`, `/wfs` and `/wms`, an invalid token
  is an anonymous caller rather than a 498, which is an ArcGIS REST convention.
- **CORS** is the global policy (ADR-072); these paths are not among the closed ones.
- **Compression — ADR-068**: `/ogc/tiles/v1` and `/wmts` are added to `ResponseCompressionPolicy.IsAllowed` by
  name. They carry no secret: a tile is the bytes `/rest/services` already compresses, the documents describe
  what the caller may read, and nothing echoes a token. `OgcNames.Base` is `/ogc/features/v1` exactly, so the new
  face was *not* covered until it was named — the allowlist doing what §4 meant it to.
- **The request log** files a request under the service it named (`RequestFacts`): the collection segment on
  OGC API Tiles, `layer` or the RESTful segment on WMTS, face `WMTS`. An unhandled failure on `/wmts` is an OWS
  exception report (`ErrorResponse`), never ArcGIS JSON.

### 5.7 The collection id

`name` at the root, `folder.name` in a folder — one path segment, because the standard paths put it in one and
a `%2F` inside a segment is refused by Apache by default and decoded by several proxies. Neither a folder nor a
service name may contain `/ \ ? # %`, so no character is free to be a separator, and the dot is the one a reader
takes as *inside*. **Two readable services with one id** — a root service `hosted.parcels` beside `parcels` in
`hosted` — **are both refused under it**, with a sentence saying why, rather than one being served for the
other. WMTS uses the same id as its layer identifier. *INFERRED* acceptable (§11).

### 5.8 What is not built

- ~~**PMTiles** — by owner decision, with offline packages (VTPK) as an export (item 11).~~ **Built 2026-09-29 as an
  export — [ADR-098](ADR-098-vector-tiles-can-be-exported-as-a-package.md):** a PMTiles v3 archive of a Web Mercator service's tiles, written by a job
  and downloaded, beside the VTPK. It is still not a served face.
- **Dataset tilesets** (`/tiles` at the root): a tile is one service's, and a server-wide tileset would mix grids.
- **`collections`, `datetime` and `subset` parameters**, **OpenAPI** (`oas30`), **XML** tileset metadata, and
  **map (raster) tiles**. None is claimed.
- **WMTS `GetFeatureInfo`**, and styles other than `default`: a vector tile is drawn by the client, and the
  ArcGIS face's named styles (ADR-094) are documents, not tiles this face could vary.
- **A tile matrix set per service beside Web Mercator**: a service is cut on one grid (ADR-096), and offering
  another would be offering tiles this server does not cut.

### 5.9 Why each face (§82 — the line [Q-86](../open-questions.md) says is owed when a surface returns)

- **OGC API Tiles**: the standard way an OGC client asks for tiles by link rather than by a known URL shape;
  GDAL's OGC API driver and OpenLayers' OGC vector tile source read it. Cost: documents over the one path.
- **TileJSON**: how MapLibre, deck.gl and QGIS's vector tile connection are handed an XYZ source without
  writing a style — the most-used configuration in the web vector tile ecosystem.
- **WMTS**: parity, owner-named. No known client in the target market draws vector tiles from WMTS (§3); the
  line is the weaker, honest one Q-86 predicted some faces would get.

## 6. Consequences

**Positive.** Three standards over the tiles this server already cuts, with no second encoder, cache key or
admission rule; the Web Mercator grid is the registered one, the TUREF grids are described honestly, and a tile
fetched through any face is the same cache entry.

**Negative.**

- **No client has been watched against any of the three** (§9). *Expected* to work, not verified: OpenLayers
  (`OGCVectorTile` against the tileset, `VectorTile` with the TileJSON template), GDAL's OGC API driver,
  MapLibre GL JS with the TileJSON as a `vector` source's `url`, QGIS's vector tile connection with the TileJSON
  template. *Not expected*: QGIS's and ArcGIS's WMTS readers drawing MVT.
- A 256-pixel client switches ranged layers at half the screen scale an operator set (§3).
- Two standard OGC collections documents whose ids differ — a layer name on `/ogc/features/v1`, a service on
  `/ogc/tiles/v1`.
- Collections and capabilities list the whole catalogue per request, as the other standard faces do.
- A service's TileJSON has no token in its template, so a private service needs a client that sends a header.

**Ports created.** None. `Graticula.Api.Tiles` is a compatibility adapter in its own project (ADR-005 §3.3,
§51): it references `Graticula.Core` and nothing in `Graticula.Core` references it. No dependency is adopted.

**State.** *Catalogue*: none — no column, table or migration; the faces read the service, its layers and its
grid as the ArcGIS face does. *Runtime*: none new — the tiles are ADR-010's node-local cache under the keys the
ArcGIS face already uses, and the built-in tile matrix sets are constants of the build.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | None in the register. What is unverified — how each client reads these documents — is recorded in §3, §6 and §9 as the next thing to watch, not as something the decision rests on quietly | — |

## 8. Dependencies

**Depends on:** [ADR-096](ADR-096-a-vector-tile-service-may-be-tiled-in-another-reference.md) (the grids),
[ADR-093](ADR-093-seeding-the-tile-cache.md) §5.5 (one tile path), [ADR-042](ADR-042-ogc-api-features.md) (the
OGC face's conventions), [ADR-018](ADR-018-authorization-and-roles.md) and [ADR-031](ADR-031-service-capability-configuration.md)
(what may be seen), [ADR-049](ADR-049-a-face-refuses-in-its-own-vocabulary.md), [ADR-068](ADR-068-responses-are-compressed.md),
[ADR-069](ADR-069-query-responses-carry-the-layer-s-cache-lifetime.md), [ADR-070](ADR-070-a-layer-has-a-visible-scale-range.md).

**Depended on by:** [ADR-098](ADR-098-vector-tiles-can-be-exported-as-a-package.md) — PMTiles and VTPK as an export — which packages the tiles
from the same path.

## 9. Revisit triggers

- **The first time a client is pointed at each face.** If OpenLayers, GDAL or MapLibre misplaces a tile, reads
  the set differently from §5.2 or refuses a document, this ADR is reopened on what it did.
- **A client that needs 512 cells declared for Web Mercator** — the case for §2 Alternative D, as a second set
  and not a relabelled first.
- **A deployment whose ids collide** (§5.7) — the moment to choose a separator that cannot occur, such as an
  escaped slash, and pay its proxy cost.
- A request for dataset tilesets, `collections` selection, `datetime`, or map tiles.
- Collections or capabilities measured slow at the scale target — the moment for a listing cache the four
  standard faces share.

## 10. Dissent

None recorded. The closest is §2 Alternative D: a reader comparing the ArcGIS `tileInfo` (512) with the OGC set
(256) sees two tile sizes for one service. §5.2 and the set's own remarks say why that is one grid stated by two
conventions.

## 11. INFERRED, for confirmation

1. `/ogc/tiles/v1` with a collection per service, separate from `/ogc/features/v1` (§2 A/B).
2. The registered `WebMercatorQuad` at 256 cells, and 512-cell sets for TUREF and custom grids (§5.2).
3. TileJSON as the Web Mercator tileset's `?f=tilejson` alternate, and 406 for any other grid (§5.3).
4. The `folder.name` collection id, and refusing both services on a collision (§5.7).
5. WMTS with both bindings, one style `default`, `.pbf` as the RESTful extension (§5.4).
6. An empty tile is 204 on all three faces (§5.1, §5.4).
7. No `tileMatrixSetLimits`, so no face answers a tile differently from the ArcGIS face (§5.2).
