# NextGIS Web, compared — 2026-09-19

**Asked by the owner:** *"Githubtaki nextgis projesine bakarak farkları vs inceler misin?"* — look at
the NextGIS project on GitHub and examine the differences.

**What was read.** `github.com/nextgis/nextgisweb` at `de9825fa` (2026-09-19, version 5.6.0.dev9) and
`github.com/nextgis/nextgisweb_ce`: the README, `setup.py`, the component tree, and the source of the
tile path (`feature_layer/api_mvt.py`, `vector_layer/feature_query.py`), the tile cache
(`render/model.py`) and the permission model (`resource/model.py`). NextGIS Web is **GPL-3.0**; nothing
from it is copied into this repository, and the one place its behaviour was reconstructed — the tile
benchmark — says so in its own text ([CLAUDE.md](../../CLAUDE.md) §5).

## What it is

A web GIS server in Python (Pyramid, SQLAlchemy, GDAL) with a React front end, developed since 2013 by
NextGIS, who sell it as a hosted service and support it on premises. About 60,000 lines of Python and
90,000 of TypeScript. Its own description: *"geospatial data management, web map publishing, and
QGIS-centered collaborative workflows."* The community edition is one Docker image.

## Where the two are the same

- **PostGIS underneath, in both of this project's modes.** NextGIS Web's `vector_layer` keeps data in
  its own database, as a hosted layer here; its `postgis` component reads a connected database, as a
  registered layer here.
- **OGC faces.** WFS, OGC API – Features, WMS and vector tiles on both.
- A web administration interface, users and groups with per-resource permissions, attachments.

## Where they differ

| | NextGIS Web | Graticula |
|---|---|---|
| **The API clients speak** | Its own REST API. **No ArcGIS REST at all** — the word does not occur in its source | The ArcGIS REST API is the primary face: FeatureServer, VectorTileServer, GeometryServer, the portal |
| **The clients it is for** | QGIS, through its NextGIS Connect plugin; its own Android app; its own JavaScript libraries | ArcGIS Pro, the ArcGIS Maps SDK, Field Maps; QGIS through its ArcGIS REST connection |
| **Symbology** | QGIS styles, rendered by QGIS running headless on the server (a separate `nextgisweb_qgis` package); SLD | A CIM document ([ADR-052](../adr/ADR-052-the-canonical-symbology-document-is-cim.md)), drawn by our own renderer; SLD not in v1 |
| **Vector tiles** | Encoded per request in Python through GDAL's MVT writer | `ST_AsMVT` in the database ([ADR-021](../adr/ADR-021-tile-encoding.md)) |
| **Feature history** | Every version kept, with who and when; roll back | **None** — `historicMoment` was on the ignored list with *"there is no history"* |
| **A map for the people who use the data** | A large web map client: layer tree, search, filter, printing, measuring, sharing | `view.html` draws one service for an operator; nothing saved, nothing shared |
| **Connecting other servers' services** | TMS, WMS and WFS as layers | No |
| **Files as layers** | No | GeoParquet, DuckDB and MotherDuck read in place ([ADR-066](../adr/ADR-066-geoparquet-layers-read-by-duckdb.md), [ADR-067](../adr/ADR-067-duckdb-sources-beyond-a-local-folder.md)) |
| **Geometry service** | No | GeometryServer |
| **Licence** | GPL-3.0; the company sells the hosted service | Elastic License 2.0 ([ADR-047](../adr/ADR-047-the-outbound-licence-is-elastic-2.md)), which forbids exactly that to anybody else |

## What it means

1. **They are not competing for the same user.** NextGIS Web's answer is *a web server for a team that
   works in QGIS*. Graticula's is *replace the server and keep the ArcGIS clients*. The second user is not
   served by NextGIS Web at all, which is the v1 cut ([v1-scope](../v1-scope.md)) confirmed from outside.
2. **Two things are missing here that a user of either product would notice**: feature history and a web
   map. ArcGIS has both too — archiving with `historicMoment`, and Map Viewer with Web Map items.
3. **Whether `ST_AsMVT` is faster than NextGIS Web's GDAL path was a claim, and the owner asked for it to
   be measured.**

## What came of it

The owner's answer, the same day: *"2. Kapsama alalım 3. kontrol edelim"* — take the second into scope,
check the third.

- **Feature history** — [ADR-078](../adr/ADR-078-a-hosted-layer-can-keep-its-history.md): kept by the
  database with a trigger on a hosted layer, ArcGIS archiving's shape, answered through `historicMoment`
  and a History page in Studio.
- **Web maps** — [ADR-079](../adr/ADR-079-a-web-map-is-a-saved-document.md): an ArcGIS Web Map document
  saved as a portal item, and a viewer for it.
- **The tile claim** — [benchmarks/nextgis-mvt](../../benchmarks/nextgis-mvt/RESULTS.md): true, 2.2–3.2×
  on every tile measured. And a finding against ourselves on the way: at z12 over Istanbul our tile is
  2.1 MB and nothing bounds it — [Q-157](../open-questions.md).
