# Mobile geodatabase — disposable probes for ADR-288

**Never promoted** (CLAUDE.md §1). The production writer is written fresh in C#; these scripts are the
specification it is measured against. The format they write is described in
[docs/research/mobile-geodatabase-format.md](../../docs/research/mobile-geodatabase-format.md), and how it was
learned — by observation, with the sources — is that note's §0.

No file Esri wrote is in this folder. Two scripts read one at run time, from outside the repository:

| Script | What it shows | Needs |
|---|---|---|
| `make_samples*.py` | Writes geometries this project chose into a mobile geodatabase with ArcGIS Pro's `arcpy`, so the blob can be read against known input | ArcGIS Pro's Python |
| `stgeom.py` | Encodes and decodes the geometry blob. 50 of 50 captured blobs re-encode byte for byte | — |
| `rebuild.py` | Rebuilds a captured file with plain SQLite — Esri-module virtual tables written as schema rows, triggers last, every geometry re-encoded | a captured file |
| `writer_proto.py` | **Writes a whole file from a service's REST description and rows**, snapshot or registered replica, generating every per-layer statement and registry row. It takes four format constants from a captured file: the item-type and relationship-type tables, the system tables' `CREATE` statements and their column registry | a captured file for the constants |
| `gen_schema_constants.py` | Writes `src/Graticula.Offline.Sqlite/MobileGeodatabaseSchema.cs`, the format constants the production writer carries: system tables and indexes, item and relationship types, the system column registry, the delta tables | a captured replica and a captured delta |
| `read_arcpy.py`, `edit_arcpy.py` | Pro reads every table and geometry; Pro inserts, updates and deletes through its own editor | ArcGIS Pro's Python |

**Measured 2026-11-10:** Pro read both files `writer_proto.py` wrote from scratch for a four-layer service (point Z,
line, polygon with a hole, table, attachments), and edited the registered one, archiving included — an update closed
the old version and opened a new one, a delete closed the row, an insert took its object id from the
`GDB_RowidGenerators` row the writer set.
