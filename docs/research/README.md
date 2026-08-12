# Research Notes

**Status:** STUB — not written
**Required by:** §4, §16

---

Raw research output. Findings live here before they are distilled into
[../architecture-assessment.md](../architecture-assessment.md).

Planned topics, in priority order:

| File | Topic |
|---|---|
| `arcgis-som-soc.md` | ArcGIS Server SOM/SOC/ArcSOC runtime model (§16) — **written, first pass** |
| `runtime-models-compared.md` | GeoServer, MapServer, QGIS Server, GeoServer Cloud runtime models — **written, first pass** |
| `geoserver.md` | GeoServer beyond the runtime model: catalog, styling, extensions |
| `mapserver-qgis.md` | MapServer and QGIS Server beyond the runtime model |
| `postgis-thin-servers.md` | pg_tileserv, pg_featureserv, Martin, Tegola, TiTiler — **written, first pass** |
| `cloud-native-formats.md` | COG, STAC, PMTiles, FlatGeobuf, GeoParquet |
| `client-side-platforms.md` | GeoLibre and the serverless GIS argument — see below |
| `dependency-thread-safety.md` | GDAL, GEOS, PROJ threading rules — **written, resolves A-013** |
| `geometry-projection-libs.md` | GEOS, JTS, NTS, PROJ — maturity, binding quality — **written, first pass** |
| `duckdb-geoparquet.md` | DuckDB as a compute layer, GeoParquet as a provider format — **written, first pass** |
| `rendering-engines.md` | Skia, Cairo, MapLibre Native — **written, first pass, deliberately thin** |

Every topic answers the three questions from §4:

1. What problem was this design solving?
2. Is that problem still relevant today?
3. Solved from first principles today, what would it look like?

## Note on `client-side-platforms.md`

This topic is not a peer comparison — it is an adversarial one.

**GeoLibre** (opengeos, MIT) is a lightweight cloud-native GIS platform built on
Tauri v2, React, TypeScript, MapLibre GL JS, DuckDB-WASM Spatial and deck.gl. It
runs in the browser, on desktop, on Android and inside Jupyter. It streams
remote GeoParquet, builds vector tiles on demand, and runs its geoprocessing
entirely in WebAssembly — its own description is "no server, no install, and no
data ever leaving your machine".

Architecturally it is close to the opposite of this project: single-user and
local-first, with no service model, no publishing, no multi-user administration
and no server-side runtime. Its nearest counterpart is QGIS Desktop, not ArcGIS
Server. It does not belong in the comparison set alongside GeoServer.

It is worth researching for two reasons:

1. **Format-first architecture as working proof.** Remote GeoParquet streaming,
   on-demand tiling, lazy DuckDB spatial loading, PMTiles and COG over HTTP
   range requests — evidence for how far cloud-native formats have come, and
   directly relevant to §33 and §35.
2. **It is the strongest available argument that we may not be needed.** For a
   large class of published-data workloads, a static object store plus a capable
   client is the correct architecture. This is §82 pointed at the product
   itself, and it feeds Q-18 and the standing challenge in
   [../product-context.md](../product-context.md).

Research output should answer: for which workload classes does the client-side
model genuinely win, and what is the honest boundary where a server starts
earning its cost?

Sources: <https://github.com/opengeos/GeoLibre>, <https://geolibre.app/>

**Clean-room constraint (§5):** publicly documented behaviour and published
architectural reasoning only. Cite sources. Do not reproduce proprietary source
or undocumented internals.
