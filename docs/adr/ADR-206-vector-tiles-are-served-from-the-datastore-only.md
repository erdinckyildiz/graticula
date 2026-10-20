# ADR-206 — Vector tiles are served from the datastore only

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `HIGH` |
| **Decided** | 2026-10-20, by owner decision, stated directly: *"reference registered tüm dblerde kapalı olsun. sadece builtin datastore'da aktif olsun"*, and to the question whether registered PostGIS ([ADR-095](ADR-095-registered-postgis-layers-serve-vector-tiles.md)) goes too: *"Evet, o da kapansın"*. The reasoning given: a registered database does not have to serve vector tiles, its performance there is unknown, the datastore exists for this, and ArcGIS works the same way. |
| **Reverses** | [ADR-095](ADR-095-registered-postgis-layers-serve-vector-tiles.md) (registered PostGIS layers serve vector tiles); [ADR-066](ADR-066-geoparquet-layers-read-by-duckdb.md) §9 as amended 2026-09-13 (GeoParquet layers serve vector tiles), and with it ADR-067's tiles for DuckDB and MotherDuck sources; [ADR-205](ADR-205-a-registered-oracle-database-is-served-like-postgis.md) §5c (Oracle tiles through the datastore) |
| **Restores** | [Q-67](../open-questions.md)'s rule of 2026-08-12: tiles come only from hosted data |
| **Supersedes** | [ADR-095](ADR-095-registered-postgis-layers-serve-vector-tiles.md) |
| **Superseded by** | — |

---

## 1. Context

Q-67 made the datastore the only source of vector tiles on 2026-08-12. Three decisions widened it since: ADR-066 §9
(GeoParquet files, 2026-09-13, rows read by DuckDB and encoded by `ST_AsMVT` in the datastore), ADR-095 (registered
PostGIS databases, 2026-09-29, encoded in the registered database itself) and ADR-205 §5c (Oracle, 2026-10-20, the
GeoParquet route). ADR-205 condition 4 then asked for an Oracle tile to be timed against the same layer in PostGIS,
which needed 1.27 million buildings loaded into Oracle on a VPS shared with the showcase — and the owner asked why a
registered database should serve tiles at all.

**The premise checks out against ArcGIS.** In ArcGIS Enterprise a vector tile layer is made from a hosted feature
layer or from a tile package built in Pro; data registered by reference is served as a feature service (and a map
service), not as on-the-fly vector tiles. A client draws a registered layer by querying it — which is what the
registered paths here already answer, measured end to end for PostGIS, GeoParquet and Oracle.

## 2. Alternatives considered

### Alternative A — keep tiles on registered sources, off by default, per layer *(not chosen)*

Offered to the owner; not chosen. It keeps every tile path alive — three encoders' worth of code, their tests and
their cache-coherence rules (`best-effort`, five-minute lifetimes, D-279) — for a feature nobody uses: on the
showcase, **no web map draws a registered service's tiles and none has a style of its own** (measured below).

### Alternative B — the datastore only *(chosen)*

## 3. Counterarguments to the preferred option

- **A large registered layer drawn by query is slower than by tiles.** The showcase's million-building GeoParquet
  layer is the case. The query path generalises by `maxAllowableOffset` (ADR-066's 2026-09-13 repair, made for exactly
  the ArcGIS SDK drawing that layer), and a layer that must draw fast at every scale is what the datastore is for —
  importing it is the answer ArcGIS gives too.
- **ADR-095 was an owner decision three weeks old.** It is reversed by the same owner, directly, with a reason.

## 4. Evidence

| Claim | Measurement | Status |
|---|---|---|
| Nothing on the showcase depends on a registered layer's tiles | `gisserver` on the showcase, 2026-10-20: 15 registered layers in 12 services — PostGIS 6 in 3, GeoParquet 5 in 5, DuckDB 2 in 2, MotherDuck 2 in 2 — against 16 hosted in 13. Web maps naming any `VectorTileServer`: 1; naming a registered service's: **0**. Registered services with a custom style: **0** | measured |
| A layer without tiles still previews | `console.js` `show()` draws a `FeatureLayer` when `VectorTileServer?f=json` does not answer | read |
| The single gate | Every face asks `TileSources.Tiled` — the service list, the tile routes, the seeder, the basemap gallery, the ground layer, the catalogue's `tileable` | read |

## 5. Decision

**A layer serves vector tiles only when its data is in the datastore.** `TileSources.Tiled` answers by `hosted`
alone; a registered PostGIS, Oracle, GeoParquet, DuckDB or MotherDuck layer has no `VectorTileServer`, and its tile
routes refuse it as they did under Q-67. Its FeatureServer, OGC API Features, WFS and WMS faces are unchanged.

**What goes with it** — the code that existed only to tile other engines' rows: `ReadTileSource` and its reader
branches in `LayerConnections.TileSourceFor`, and `IMvtEncoder` and `PostGisMvtEncoder` (the statement that encoded
rows sent across). `TileSourceFor` refuses a registered layer by name rather than hand its locator to Npgsql.
`PostGisTileSource` stays: it is the datastore's tile source.

**What stays, and why — found while removing it.** `TileSources.RegisteredLifetime`, `DefaultLifetimeOf` and
`CoherenceOf` were written for ADR-095's tiles, but the query face sends its answers with the same lifetime
(ADR-069, `QueryResponseCaching.LifetimeOf`): a registered PostGIS or Oracle table's query answers are kept five
minutes, not the datastore's hour, because other tools write to it. Removing them would have turned a tile decision
into an hour-old feature answer, which nobody decided. They stay, and now describe query answers.

## 6. Consequences

- **State.** None new. The catalogue keeps what it held; a registered layer's cached tiles stay on each node's disk, unreachable, until the cache budget evicts them.
- The 15 registered layers on the showcase lose their `VectorTileServer` at the release that carries this; their
  previews draw as feature layers. Their cached tiles are unreachable and age out of the budget.
- ADR-205 condition 4 is deferred with this decision; ADR-095's open conditions likewise.
- [D-279](../architecture-debt.md) keeps its subject: a MotherDuck layer's *query answers* still take the server's
  default lifetime, for the reason above.
- The console's per-layer lifetime control sits under the tile panel, so for a registered layer it is no longer
  shown although its query answers still keep that lifetime; `PUT /admin/layers/{name}/cache` still sets it.
  [D-308](../architecture-debt.md).
- Q-34's *registered half has no subject* is true again, as it was written.

## 7. Assumptions this decision rests on

- Clients draw a registered layer through its feature service, as ArcGIS clients do (Pro, the Maps SDK's
  `FeatureLayer`) — measured for Pro against Oracle on 2026-10-20 (ADR-205 condition 5).

## 8. Dependencies

[Q-67](../open-questions.md), [ADR-021](ADR-021-tile-encoding.md), [ADR-010](ADR-010-caching.md).

## 9. Revisit triggers

- A customer needs a registered layer drawn at tile speed **and** cannot import it into the datastore — then tiles
  return for that source as an explicit, per-layer choice (Alternative A), not by default.

## 10. Dissent

None recorded.
