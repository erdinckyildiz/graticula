# ADR-098 — Vector tiles can be exported as a package

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` — both formats are written from their published specifications and read back in the tests by independent readers of the same specifications; `pmtiles verify` accepts an exported archive and MapLibre draws it (condition 2, after a header repair it found); no ArcGIS client has opened a VTPK this server wrote (condition 1) |
| **Decided** | 2026-09-29, by owner decision. The owner asked for the vector tile face's remaining gaps to be closed one at a time; this is item 11 on that list, and the owner opened it directly: **offline tile packages** — ArcGIS's `exportTiles`, so that Field Maps offline areas and ArcGIS Pro's *Download Map* can take tiles from this server — **together with PMTiles export**, moved here from item 12 by [ADR-097](ADR-097-vector-tiles-through-ogc-api-tiles-tilejson-and-wmts.md) §5.8. The owner also said how: a job that walks the grid through the seed's own machinery, VTPK in compact cache V2 bundles, PMTiles v3 for Web Mercator only, the capability off for existing services, a download that re-checks access. Every number, address and shape below is this session's design and is marked **INFERRED** where it matters (§12). |
| **Depends on** | [ADR-093](ADR-093-seeding-the-tile-cache.md), [ADR-096](ADR-096-a-vector-tile-service-may-be-tiled-in-another-reference.md), [ADR-097](ADR-097-vector-tiles-through-ogc-api-tiles-tilejson-and-wmts.md), [ADR-011](ADR-011-job-system.md), [ADR-017](ADR-017-admin-api.md), [ADR-021](ADR-021-tile-encoding.md), [ADR-010](ADR-010-caching.md), [ADR-031](ADR-031-service-capability-configuration.md), [ADR-075](ADR-075-a-layer-is-edited-by-its-owner.md) |
| **Amends** | [ADR-082](ADR-082-offline-sync-is-not-in-v1.md) — offline *basemaps* are in; replica sync is still out. [ADR-097](ADR-097-vector-tiles-through-ogc-api-tiles-tilejson-and-wmts.md) §5.8 — PMTiles is built, as an export. |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

Every VectorTileServer this server wrote said `exportTilesAllowed: false`. [ADR-082](ADR-082-offline-sync-is-not-in-v1.md)
put offline work outside v1, and the flag was the honest consequence: a client that read it did not offer an offline
area it could not have. The cost was that a field crew could not take even a basemap offline from this server —
Field Maps' offline map areas and Pro's *Download Map* both begin by asking the tile layer for a package, and both
look at that flag first.

ADR-082 was about **sync**: replicas, change sets, conflict rules — a contract with a device that may not come back
for weeks. A tile package is not that. It is a copy of tiles this server already cuts, written once into one file and
handed out; nothing comes back. The owner opened the tile half on 2026-09-29, with PMTiles beside it because a
PMTiles archive is the same act for the other family of clients — MapLibre with the `pmtiles://` protocol, the
`pmtiles` command-line tool, and anything that reads a single-file tileset by HTTP range.

What already existed made most of it a matter of joining pieces rather than building them:

- **The walk.** [ADR-093](ADR-093-seeding-the-tile-cache.md) walks a service's grid lowest level first, a batch at a
  time, under the same `ConnectionBudget` permit a map's cold tile takes, pausing on a source outage and learning of a
  cancel at its checkpoint — and every tile it touches is the tile the route would serve, under the key the route
  reads.
- **The grids.** [ADR-096](ADR-096-a-vector-tile-service-may-be-tiled-in-another-reference.md) gave a service a
  tiling scheme; Web Mercator and the TUREF zones are rows and columns counted down from a top-left origin, which is
  what a compact cache bundle addresses.
- **The documents.** The service document, the style the style route would serve, the sprite sheet and the glyph
  ranges are all served already; a package carries them.

## 2. Alternatives considered

### Alternative A — an export job that walks through the seed's machinery and stages the tiles *(chosen)*

A new job kind, `tile.export`, whose worker walks the plan with `TileSeedRun` and builds each tile with the seed's own
per-tile loop, keeps each tile's bytes in a staging file, and at the end writes the package from the staging file.

**Argument for.** One walker, one admission, one definition of a tile. The seed's order, pause, outage handling and
cancel come with it. A tile in the package is a tile the route serves — the same key, the same single-flight, the same
bytes — which is what makes the package's tiles checkable against the live service at all.

**Argument against.** A package cannot be streamed while it is walked: both formats want every tile's size before
their first byte (a bundle begins with its index, an archive with its header), so the tiles are kept twice — in the
cache and in the staging file — until the package is written.

### Alternative B — a second walker that reads the cache directly and builds nothing

**Argument for.** Cheap when the cache is warm: no admission, no build, no source touched.

**Argument against.** A cold or expired tile would be missing, and the package would have holes exactly where the
cache had evicted, with no way to tell from inside it. Building the missing tiles means the seed's admission and
single-flight again — a second copy of the seed.

### Alternative C — write the package from the cache after a seed

**Argument for.** Seed, then zip.

**Argument against.** The cache's budget evicts; a seed larger than the budget has lost its top levels by the time the
zip starts ([ADR-093](ADR-093-seeding-the-tile-cache.md) §5.9). The export would be as large as the cache let it be,
not as large as it was asked to be.

### Alternative D — resumable exports

**Argument for.** A long export interrupted by a restart would go on where it stopped, as a seed does.

**Argument against.** A seed's durable state is the cache itself plus a cursor; an export's is a half-written zip or
archive, which has no cursor a second run can trust. Resuming would mean a durable, append-only staging file with a
checked tail and an index persisted beside it. Restarting instead is cheap for the reason Alternative A gives: every
tile the lost run built is in the cache, so the second walk is mostly reads. **Chosen against** — an export is
`Harmless` and restarts (§5.4).

### Alternative E — a pre-signed, time-limited download address

**Argument for.** ArcGIS Online answers `exportTiles` with a signed storage URL; a downloader needs no credential, and
a device can hand the URL to its own download manager.

**Argument against.** A signed URL keeps working after the service is unshared or its export turned off, until it
expires; and it needs a second secret with its own rotation. Re-checking the caller's access on every request — the
same `ServiceLookup` every tile route goes through — revokes at once and adds no secret. **Chosen against**; the
address still carries a 128-bit random token, so it is unguessable as well as governed (§5.7).

## 3. Counterarguments to the preferred option

- **Nothing has opened a package.** The bundle format is published and exact; the VTPK's own layout around the bundles
  is inferred (§4). A package whose tiles are right and whose `p12/root.json` or `esriinfo` is not what Pro expects is
  a file Pro refuses. Condition 1.
- **The export directory is per server** unless a deployment points every server at one volume. A download that lands
  on a server that did not write the package is a 404 that says so. [D-282](../architecture-debt.md).
- **A tile that fails fails the whole export.** One bad row in a hundred thousand tiles is a failed job with the first
  failure named, not a package with a hole. That is deliberate — a hole is found offline — and it is also a way for one
  bad tile to cost an hour.
- **The estimate is pessimistic.** Tiles are counted at their cached, uncompressed sizes (or ADR-093's default guess),
  and both formats store them gzip-compressed; a VTPK's bundle overhead is counted for every block the area touches.
  A package that would fit can be refused. The owner's rule for seeds — refuse what does not fit, say the numbers — is
  kept, and the numbers say they are an upper bound.
- **The console's download holds the whole file in the browser's memory**, because the console signs its requests
  with a bearer header a plain link cannot carry. A package of gigabytes is better fetched with `curl` and the same
  header. [D-282](../architecture-debt.md).
- **The service document now differs by caller.** `exportTilesAllowed` is true only for a caller the service lets
  export (§5.5); an anonymous caller of a service that exports only to signed-in callers is told `false`. A shared
  proxy cache that stored one caller's document for another would be wrong; the document is not sent with a public
  cache lifetime, and nothing measured shows one caching it.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| A compact cache V2 bundle is a 64-byte header (version 3, 16,384 records, largest tile, offset byte count 5, slack, file size, user header offset 40 and size 20 + 131,072, legacy 3/16/16,384/5, index size 131,072), then 128 × 128 eight-byte records row-major at `64 + 8 × (128 × row + column)`, offset in bits 0–39 and size in 40–63, size 0 meaning no tile, a four-byte size before each tile with the record pointing past it, named `R<rrrr>C<cccc>.bundle` in lowercase hexadecimal of the bundle's top-left row and column, in `L<nn>` folders | Esri, *Compact Cache V2*, `Esri/raster-tiles-compactcache`, `CompactCacheV2.md` (published on GitHub by Esri as the format's specification) | read 2026-09-29 |
| A vector tile service stores its cache as `compactV2` with a packet size of 128, and says `tileCompression: gzip` when its tiles are gzip-compressed | ArcGIS REST API, *Vector Tile Service* resource, the documented example (`storageFormat`, `packetSize`, `tileCompression`) | read 2026-09-29 |
| A tile package is a zip of a `root.json`, an item description, a thumbnail and a `tile` folder of `.bundle` files by level | Esri, `Esri/tile-package-spec` (the `.tpkx` description) | read 2026-09-29. **Written for raster packages.** |
| The VTPK's layout: `p12/` holding the service tree (`root.json`, `resources/styles/root.json`, `resources/fonts/…`, `resources/sprites/…`, `resources/info/root.json`, `tile/L<nn>/…bundle`), and `esriinfo/iteminfo.xml` with `esriinfo/item.pkinfo` | — | **INFERRED** from the vector tile service's documented child resources (a package's `p12` is that tree, file for file) and from published descriptions of the format; no Esri document read for this states it whole. The XML elements of `iteminfo.xml` and `item.pkinfo` and the shape of `resources/info/root.json` are INFERRED too |
| Entries stored, not deflated | — | **INFERRED**: the tiles are already gzip-compressed, and a reader that maps a bundle needs its bytes as they are |
| `exportTiles` takes `levels` (a list or ranges, `1-4,7-9`), `exportExtent` (an envelope or `DEFAULT`), `polygon` and `f`; answers `{"jobId", "jobStatus": "esriJobSubmitted"}`; the job is at `jobs/{jobId}` with `results.out_service_url.paramUrl` `results/out_service_url`; a vector tile service that exports says `exportTilesAllowed: true` and `maxExportTilesCount`, whose default is 100,000 | ArcGIS REST API, *Export Tiles (Vector Tile Service)* and the *Vector Tile Service* resource | read 2026-09-29 |
| The result resource answers `{"paramName": "out_service_url", "dataType": "GPString", "value": "<url>"}`; the job carries `progress` and `messages` as the Geoprocessing job resource does; `jobStatus` values `esriJobExecuting`, `esriJobFailed`, `esriJobCancelled` | — | **INFERRED** from the Geoprocessing job resource; the export page shows the submitted and succeeded states and a partial job document |
| `estimateExportTilesSize` answers a job whose result `out_service_tile_estimates` is `{"totalSize", "totalTilesToExport"}` | — | **INFERRED** from the map service's operation of the same name; the vector tile service's reference page for it was not found (404 when read) |
| No word in `capabilities` is what a client checks: the documented example of a service that exports says `TilesOnly` with `exportTilesAllowed: true` | ArcGIS REST API, *Vector Tile Service* example | read 2026-09-29; **INFERRED** that Field Maps and Pro check `exportTilesAllowed` and nothing else (condition 1) |
| PMTiles v3: the 127-byte header and its fields, the compression and tile-type enumerations, the Hilbert tile id cumulative from level 0 (`0/0/0` → 0, `1/0/0` → 1, `1/1/0` → 4), a directory as five varint runs with offsets as `offset + 1` or `0` when contiguous, run length 0 for a leaf, offsets relative to the data or leaf section, the root in the first 16,384 bytes, *clustered*, `vector_layers` required in a vector tileset's metadata, media type `application/vnd.pmtiles` | Protomaps, `protomaps/PMTiles`, `spec/v3/spec.md` | read 2026-09-29 |
| The bundle writer's index offsets and sizes are the specification's, a full bundle round-trips, an empty position says size zero | `CompactCacheBundleTests` (8), against `TilePackageReaders`, written in the tests from the specification and not from the writer | this change |
| The PMTiles writer's header fields, varints, run-length and deduplicated entries, leaf directories, and tile lookup by z/x/y through an independently written Hilbert id agree with the specification | `PmTilesWriterTests` (14), including every tile of levels 0–6 and `12/3423/1763` against the reader's own `xy2d` | this change |
| The VTPK zip: paths, a bundle per level and block, every entry stored (compression method 0 in every local header), a tile read back by z/x/y and gunzipped | `VectorTilePackageTests`, `TileExportTests` (Host) | this change |
| The estimate's arithmetic; the capability gating; the service document advertised with the two documented properties and nothing else changed; ArcGIS's level lists; tokens and file names | `TileExportTests` (Host, 30 cases) | this change |
| The store: the budget checked with the insert, progress only from the holder, finishing with size and expiry in one statement, cancel only an export, removal marked once, a lost lease run again once then failed, the policy off until set | `TileExportStoreTests` (Platform.Postgres, 11) | this change. **Run 2026-09-29 against the VPS fixture's database: 11/11** (condition 3) |
| A cookie-only `exportTiles` is refused unless `Sec-Fetch-Site: same-origin`; a token in the query, a bearer header or the ArcGIS header is not asked; an anonymous request is not asked | `TileExportTests` (Host); `AnExportedTileIsAServedTileTests.A_cookie_alone_cannot_start_an_export_and_a_token_can` (conformance, not run) | this change |
| A tile in a VTPK made through `exportTiles`, and in a PMTiles archive made through the admin route, equals the served tile; the estimate counts the same tiles; a range is a 206; a caller who may not export cannot download, a guessed name is a 404, and turning the export off stops the ArcGIS address at once | `AnExportedTileIsAServedTileTests` (conformance, 4) | this change. **Run 2026-09-29 on the VPS fixture: 4/4** (condition 3) |
| The tile route's bytes did not change | `TilePipelineVersionTests`: the hash moved, `TilePipeline.Version` did not | this change |

## 5. Decision

### 5.1 An export is a job, and walks as a seed walks

- A new job kind, **`tile.export`** (`JobKind.TileExport`); migration 64 widens `job_kind_known`.
- `TileExporter` is the worker — a fourth poller beside the seeder, with the same claim, lease, sweep and one job at a
  time per process. Nothing runs on a request thread.
- The walk is **`TileSeedRun` over `TileSeedPlan`**, with the seed's concurrency (`Graticula:TileSeedConcurrency`),
  lowest level first, row by row; a source outage pauses it with the seed's backoff; a checkpoint every two seconds
  learns of a cancel.
- Each tile is **`TileSeeder.ServiceTileAsync`** — the seed's per-tile loop, split out so the export takes the bytes the
  seed throws away: every layer that draws at the level (ADR-070) from the cache or built under
  `LayerConnections.AdmitTileBuildAsync`, joined by the route's own `Concatenate`. A cached tile costs no permit; a cold
  one is built once, stored where the route will find it, and so an export also seeds the cache.
- **A level list, not only a range**: `TileSeedPlan.Keeping` takes the unasked levels out of a plan, so `1-4,7-9`
  walks, counts and checkpoints like any plan.

### 5.2 The formats

**VTPK** (every export through ArcGIS's `exportTiles`; the admin route's default):

- A zip, **every entry stored**, holding `esriinfo/item.pkinfo`, `esriinfo/iteminfo.xml`, `p12/root.json`,
  `p12/resources/styles/root.json`, `p12/resources/sprites/sprite{,@2x}.{json,png}`,
  `p12/resources/fonts/{stack}/{range}.pbf`, `p12/resources/info/root.json`, and
  `p12/tile/L<nn>/R<rrrr>C<cccc>.bundle`.
- **Bundles** are compact cache V2, one per level and 128 × 128 block that holds a tile; an empty tile is not stored.
  Row and column are the service's grid's — Web Mercator's, or the TUREF scheme's (ADR-096) — so both kinds of service
  export.
- **Tiles inside are gzip-compressed MVT**, and `p12/root.json` says `tileCompression: gzip` with
  `cacheInfo.storageInfo` `{packetSize: 128, storageFormat: compactV2}`; **the live service still says `none`**, which
  is true of the bytes it sends.
- `p12/root.json` is the service document the route serves, with `tileMap` removed, `capabilities` `TilesOnly`,
  `exportTilesAllowed` false, its levels ending at the highest exported, and `initialExtent` the exported area.
- The style is **the one the style route would serve** — the stored style while it fits (ADR-028 condition 3,
  ADR-092, ADR-094), else the generated one. The sprites are the sprite routes' answers, the empty sheet included. The
  glyphs are **every range the server has of every stack the style names** (a label with no `text-font` gets MapLibre's
  default stack); a stack the server lacks is answered with the one it has, under the name the style asks for, as the
  font route does. A style whose glyphs live on another host carries none.

**PMTiles v3** (the admin route, `format: "pmtiles"`):

- Header, root directory, metadata, leaf directories, tile data, in that order. **Directories and metadata gzip**
  (internal compression 2), **tiles gzip** (tile compression 2), tile type MVT.
- Tiles in tile-id order; **identical tiles stored once** (by SHA-256 of the stored bytes) and a run of consecutive ids
  with one content is one entry — so the archive is *clustered*. One level of leaf directories when the root would
  pass 16,384 bytes.
- Metadata: `name`, `description`, `type: overlay`, `format: pbf`, and `vector_layers` — each layer's name, its fields
  (`Number`, `Boolean` or `String` from the columns a tile carries) and the levels it draws at.
- **Web Mercator only.** A service on another grid is refused with the reason: the specification addresses z/x/y on
  the Web Mercator grid and has nowhere to state another.

### 5.3 Scope, the cap, the estimate

- One service, a set of levels, an area — the whole service, or an envelope in the grid's reference, Web Mercator or
  degrees, placed exactly as a seed's is (`AdminEndpoints.AreaOfAsync`, split out of the seed's start). ArcGIS's
  `polygon` is taken as its bounding box: a package holds whole tiles, and every tile the polygon touches is in it.
- `exportBy` is level ID only; a resolution or scale list is refused with the reason.
- **The cap** is `maxExportTilesCount`: **100,000 by default** (`Graticula:TileExportMaximumTiles`, ArcGIS's documented
  default), which a service may lower and never raise. Over it: 400, `details: ["tooManyTiles"]`, the count, the cap and
  the highest level that fits.
- **The estimate** is ADR-093's per-level average of the service's cached tiles (or its default guess), counting every
  tile rather than only the missing ones, plus the format's overhead: a VTPK's 131,136-byte header and index per block
  and four bytes per tile, eight megabytes for documents; a PMTiles archive's ~16 bytes of directory per tile. It errs
  high.

### 5.4 Restart, cancel, failure

- **`JobRerun.Harmless`.** A lost lease queues the export once more and a second loss fails it. A second run walks from
  the first tile and writes the package from nothing; what the first run built is cached, so it is quick.
- **Cancel** (the admin `DELETE`) marks the job `cancelled` — the second kind that can be — and the worker deletes its
  staging and partial files. What it leaves is cached tiles, each the route's.
- **A tile that fails fails the export**, with the count and the first failure named, and the partial files go.
- The staging file is opened delete-on-close; `<token>.part` is renamed to `<token>.vtpk|.pmtiles` only when complete,
  so no reader ever sees half a package under its real name.

### 5.5 The capability, and who may export

- **A policy per service, off by default**: whether `exportTiles` is offered, whether a caller who is not signed in may
  use it, and a lower `maxExportTilesCount`. Three columns on `service` (migration 64), read with the service as
  `ServiceCapabilityLimits.Export`, written by their own route — so the capabilities `PUT`, which replaces every column
  it knows, cannot clear them.
- **Why off.** The ADR-031 ceiling is null-means-unset: a service offers what its data supports. An export is not
  something data supports; it is a bulk copy of every tile in an area, written to this server's disk and handed out.
  A service published before this existed was published by somebody who never agreed to that, so every existing
  service goes on saying `exportTilesAllowed: false` until its owner turns it on — as ArcGIS makes *allow clients to
  export* an option the publisher enables.
- **Who may export:** a caller who may read the service (its sharing, through `ServiceLookup`, exactly as a tile), on a
  service whose tile face is on and whose policy offers exports — and, if the caller is not signed in, whose policy
  also offers them to anonymous callers. **INFERRED**: anonymous export is off unless the policy says so, even on a
  public service; a public map is not thereby a service anybody may make the server package gigabytes for.
- **The service document** says `exportTilesAllowed: true` and `maxExportTilesCount` **only to a caller who may
  export**, and is otherwise the document it was, byte for byte. `capabilities` is untouched.
- **The owner exports regardless.** The policy governs what readers may take; the admin route is for the service's
  owner or an administrator (ADR-075, with `content:publishTiles`), who may already read every tile — and it is how an
  owner tries an export before offering it.

### 5.6 Storage, retention, the disk budget

- Packages are written to **`Graticula:TileExportPath`**, by default **`exports` under `Graticula:StatePath`** — the
  owner's direction: a package is something a person was handed an address for and has to survive a restart. The cost is
  that a backup of the state volume carries the live packages; the budget bounds them, and a deployment may move the
  directory.
- **Retention**: a written package is kept **24 hours** (`Graticula:TileExportRetentionHours`), then deleted by the
  worker's sweep, which also deletes the files of exports that failed or were cancelled, and — hourly — files in the
  directory named like a package whose token no live export owns. A file is marked removed only once it is gone, so a
  package a download still holds open is tried again on the next sweep.
- **The budget**: every live package's size, and every queued or running export's estimate, count against
  **10 GB** (`Graticula:TileExportBudgetMB`). The check and the insert are one transaction under one server-wide
  advisory lock. An export that does not fit is a 400, `details: ["exceedsExportBudget"]`, naming the estimate, what is
  held and the budget. Unlike a seed's there is no `force`: the budget is disk that nothing else will clear.

### 5.7 Downloads

- **The address is unguessable and governed.** A package's name is a 128-bit token from the operating system's
  generator, 32 lowercase hexadecimal characters, checked where it is made, by the store's check constraint and again
  before it becomes a path. The ArcGIS address is
  `…/VectorTileServer/jobs/{jobId}/package/{token}.vtpk`; the request's file name is compared with the stored one in
  constant time and never becomes a path. The admin address is `/admin/services/{name}/exports/{id}/download?folder=`.
- **Access is re-checked at download time**: the ArcGIS address resolves the service through `ServiceLookup` and asks
  §5.5's question again, so unsharing the service or turning its export off stops the download at once (Alternative E).
  The admin address asks `content:publishTiles` and that the caller may read the service.
- **The response** is the file with `Content-Length`, `Accept-Ranges` and ranges (206), a validator, `Cache-Control:
  private, no-store`, a `Content-Disposition` naming `<service>.vtpk|.pmtiles`, and `application/vnd.pmtiles` for
  PMTiles — the specification's recommendation — or **`application/octet-stream`** for a VTPK, which has no registered
  or documented type (**INFERRED**).
- **`exportTiles` is a `GET` that writes, and the session cookie may not start one from elsewhere.** The cookie
  authenticates reads only (`Authentication.CookieToken`), which is how every other write here is kept from a forged
  request; ArcGIS documents this write as a `GET`, so the rule is applied by the route instead of by the method. A
  request signed in by the cookie alone — no bearer header, no `X-Esri-Authorization`, no `token=` the server reads —
  is refused 403 unless the browser says `Sec-Fetch-Site: same-origin`, the check the session exchange already uses
  (`AuthEndpoints.FromThisOrigin`); the sentence says to send a token or use the admin route. A token-signed request,
  which is how ArcGIS clients send it, is not asked; nor is an anonymous one, which acts in nobody's name.
  `estimateExportTilesSize`, the job and the download are reads. The admin routes that start, delete or change
  anything are `POST`, `PUT` and `DELETE`, which the cookie never signs.
- **Audited**: `service.tiles.export` (start, with origin, levels, count, estimate), `service.tiles.export.download`
  (with any range), `service.tiles.export.delete`, `service.tiles.export.policy` (before and after).

### 5.8 The routes

ArcGIS, at the root and in any folder, each governed by the service's sharing:

| Route | Answer |
|---|---|
| `GET/POST …/VectorTileServer/exportTiles?levels=&exportExtent=&polygon=&exportBy=&f=json` | `{"jobId", "jobStatus": "esriJobSubmitted"}`; 403 where the caller may not export; 400 over the cap or the budget |
| `GET/POST …/VectorTileServer/estimateExportTilesSize?…` | `{"jobId", "jobStatus": "esriJobSucceeded"}` — answered at once; the id carries the numbers, so any server answers its status and nothing is stored |
| `GET …/VectorTileServer/jobs/{jobId}` | `jobStatus` (`esriJobSubmitted`, `esriJobExecuting`, `esriJobSucceeded`, `esriJobFailed`, `esriJobCancelled`), `progress`, `results.out_service_url.paramUrl` once written, `inputs`, `messages` |
| `GET …/VectorTileServer/jobs/{jobId}/results/out_service_url` | `{"paramName", "dataType": "GPString", "value": "<absolute url>", "downloadUrl", "size", "expires"}` |
| `GET …/VectorTileServer/jobs/{jobId}/results/out_service_tile_estimates` | `{"paramName", "dataType": "GPString", "value": {"totalSize", "totalTilesToExport"}}` |
| `GET …/VectorTileServer/jobs/{jobId}/package/{token}.vtpk` | the package |

Admin, by folder and name ([D-275](../architecture-debt.md)):

| Route | Answer |
|---|---|
| `GET /admin/services/{name}/exports?folder=` | the policy, the formats the grid allows, the defaults, the cap, the budget and what is held, the retention, and the last twenty exports |
| `PUT /admin/services/{name}/exports/policy?folder=` `{allowed, anonymous, maxExportTilesCount}` | the policy and what it now means |
| `POST /admin/services/{name}/exports?folder=` `{format, minZoom, maxZoom \| levels, extent}` | **202** with the job, where to watch it, and the export; `?dryRun=true` answers the count, the estimate and whether it fits |
| `GET /admin/services/{name}/exports/{id}?folder=` | one export: status, counts, sizes, expiry, failure, pause, and its download address once written |
| `DELETE /admin/services/{name}/exports/{id}?folder=` | cancels a running export, deletes the package, marks it removed |
| `GET /admin/services/{name}/exports/{id}/download?folder=` | the package |

### 5.9 The console

The layer's *Caching* page, under *Seed the cache*, has **Export tiles**: the policy's three settings and *Save*; the
format (PMTiles offered only on Web Mercator), the levels, the area (the whole service or the map's current extent),
*Estimate size* (the dry run) and *Export*; and the service's exports with their progress — polled every two seconds
while one runs — size and expiry, *Download* and *Delete* or *Cancel*. Every request sends the folder.

## 6. Consequences

**Positive.**
- Field Maps and Pro can be offered an offline basemap from this server, and MapLibre a single file — both made of the
  tiles the service serves.
- The seed's walker, admission and cancel serve a second kind of job unchanged; the per-tile loop is one method both
  call.
- `exportTilesAllowed` stops being a constant, and stays false everywhere nobody chose otherwise.

**Negative.**
- Nothing that reads these packages has read one yet (conditions 1 and 2).
- Packages are per server unless the directory is shared (D-282).
- The console's download buffers the file in the browser (D-282).
- A package holds its tiles twice while it is written: in the cache and in the staging file.
- One export at a time per server, across all services, behind nothing else — a seed and an export run side by side,
  each with its own concurrency against the same source budget.

**Ports created.** `ITileExportStore` (platform store), beside `ITileSeedStore`.

**State.** *Catalogue*: `tile_export` (one row per export — service, format, levels, area, counts, estimate, size, token,
grid, origin, expiry, removal, pause), deleted with its job and with its service; three columns on `service`
(`export_tiles_allowed`, `export_tiles_anonymous`, `max_export_tiles`). *Disk*: the export directory — packages, and
while an export runs its `.staging` and `.part` files. *Runtime*: the worker's map of the export it is running.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | Field Maps and Pro decide on `exportTilesAllowed`, submit `exportTiles` with level IDs, poll `jobs/{id}` and fetch `results/out_service_url`'s value | `UNVALIDATED` — condition 1 |
| — | A VTPK laid out as §5.2 says opens in Pro and in the ArcGIS Maps SDKs | `UNVALIDATED` — condition 1 |
| — | 100,000 tiles, 10 GB and 24 hours are useful defaults | Reasoned (the first is ArcGIS's); not asked of an operator |
| A-020 | Seeding absorbs the provider performance gap | `UNVALIDATED`, unchanged — an export walks at a seed's speed |

## 8. Dependencies

**Depends on:** [ADR-093](ADR-093-seeding-the-tile-cache.md) (the walk, the admission, the estimate, the cancel);
[ADR-096](ADR-096-a-vector-tile-service-may-be-tiled-in-another-reference.md) (the grids);
[ADR-011](ADR-011-job-system.md) (claim, lease, re-run declaration); [ADR-017](ADR-017-admin-api.md) (202 and a job);
[ADR-031](ADR-031-service-capability-configuration.md) (a ceiling on the service);
[ADR-075](ADR-075-a-layer-is-edited-by-its-owner.md) (who may start and stop);
[ADR-028](ADR-028-style-documents.md), [ADR-092](ADR-092-sprites-are-uploaded-per-service.md),
[ADR-094](ADR-094-several-styles-and-allowed-origins.md) (the documents a package carries).

**Depended on by:** —

## 9. Revisit triggers

- A client opens a package and refuses it, or opens it and draws it wrong — the layout's INFERRED parts are wrong.
- A deployment runs several servers against one catalogue and exports — D-282's trigger.
- An export of a realistic estate is measured, and the estimate is found far from the package's size.
- An operator asks for resumable exports, or for exports of several services at once on one server.

## 10. Dissent

None recorded.

## 11. Conditions

1. **An ArcGIS client takes a package.** Field Maps (an offline map area over a map whose basemap is a service of this
   server) or ArcGIS Pro (*Download Map*, or adding a downloaded `.vtpk`) is watched against a running server: that it
   reads `exportTilesAllowed`, what it sends to `exportTiles`, whether it polls `jobs/{id}` and reads
   `results/out_service_url`, and whether it opens the package and draws it. What it does differently from §5.8 or §5.2
   is written back here, and the INFERRED rows of §4 are marked with what was seen. Web Mercator and a TUREF service
   both.
2. **A PMTiles reader takes an archive.** `pmtiles verify` and `pmtiles show` (the Protomaps command-line tool) accept an
   exported archive, and MapLibre with the `pmtiles://` protocol draws it. **DISCHARGED 2026-09-29, after a repair it
   found.** `pmtiles` 1.31.2 (`protomaps/go-pmtiles`) first **refused** an archive of the fixture's `hosted/ci_parcels`
   exported over levels 0-14: *header MinZoom=0 does not match min tile z 10*. The parcels vanish below level 10, an
   empty tile is left out as §5.2 says, and the header still stated the levels asked for, with the centre at 0.
   `PmTiles.Plan` now states the levels that have tiles and draws the centre level into them
   (`PmTilesWriterTests.The_header_states_the_levels_that_have_tiles_not_the_levels_asked_for`), and the conformance
   test that had asserted the asked-for range now asserts the stated one lies inside it. After that, `pmtiles verify`
   passes and `pmtiles show` reads levels 10-14, centre 10, ten addressed tiles and the `ci_parcels` layer; MapLibre GL
   JS 5 with `pmtiles` 4's protocol, in headless Chrome, drew the archive and `queryRenderedFeatures` found 12 distinct
   parcels — the layer's whole count.
3. **The Postgres and conformance suites written with this ADR are run** against a database and a server with
   `GRATICULA_TEST_TILE_SERVICE` set — `TileExportStoreTests` and `AnExportedTileIsAServedTileTests` — and pass. **DISCHARGED
   2026-09-29.** On the VPS fixture with `GRATICULA_TEST_TILE_SERVICE=hosted/ci_parcels`: `TileExportStoreTests` 11/11
   against its database and `AnExportedTileIsAServedTileTests` 4/4 against its server, before and after the repair
   under condition 2.

## 12. INFERRED, for confirmation

- The VTPK layout around the bundles, the XML of `iteminfo.xml` and `item.pkinfo`, `resources/info/root.json`'s shape,
  where `cacheInfo.storageInfo` sits in `p12/root.json`, that the lods end at the highest exported level (§4, §5.2).
- Every entry stored rather than deflated (§5.2).
- The job resource beyond the documented fields, the result's `dataType`, and `estimateExportTilesSize`'s shape (§4).
- That a client checks `exportTilesAllowed` and no word in `capabilities` (§4, §5.5).
- Anonymous export off unless the policy says so, even on a public service (§5.5).
- The owner exporting through the admin route whatever the policy says (§5.5).
- `polygon` taken as its bounding box; `exportBy` level IDs only (§5.3).
- The defaults: 100,000 tiles, 10 GB, 24 hours, the export directory under `StatePath` (§5.3, §5.6).
- 400 rather than 413 or 507 for an export that does not fit; no `force` (§5.6).
- `application/octet-stream` for a VTPK (§5.7).
- 403 for a caller who may read the service and may not export it (§5.8).
- A failed tile failing the export (§5.4).
- Restart rather than resume (§5.4).
