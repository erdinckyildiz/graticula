# A geodatabase layer past every import ceiling, held and streamed

ADR-038 condition 1: *an import of a layer larger than `ImportLimits.Default`'s 50 MB proves the ceiling has moved
for this path.* D-108 recorded why it had not: the reader's pipe streamed, and `GeodatabaseImporter` then collected
every feature into an `ImportedDataset` for `PostGisImporter`, so the bound was this process's memory.

## What was measured

`stream.py`, 2026-10-13, on the local fixture (Windows 11, portable PostgreSQL 16 + PostGIS on localhost, Debug build).

- **The archive:** 1,200,000 points with four attributes (`code`, `name`, `height`, `kind`), written as GeoJSON
  (221.9 MB) and turned into a File Geodatabase by the import reader's own `export` — **82.7 MB on disk, 31.2 MB
  zipped**. Past both of `ImportLimits.Default`'s ceilings: a million features and 50 MB.
- **The path:** upload through `/admin/hosted/import`, inspection, publish through `/admin/hosted/geodatabase`, the
  row count asked of the published layer, the service dropped.
- **The memory:** the server process's peak working set, read from outside, with the server restarted before each
  run so the peak is that run's.
- **Before** is the same commit with `GeodatabaseImporter`, `GeodatabaseReader` and `PostGisImporter` put back to
  v1.0.350 — the importer that held the layer. **After** is the streamed import. Both include the upload fix below,
  without which neither could receive the archive.

## Results

| Build | Rows landed | Publish | Server peak working set |
|---|---:|---:|---:|
| Held (v1.0.350's importer) | 1,200,000 | 30.7 s | **2,152 MB** |
| Streamed, run 1 | 1,200,000 | 35.6 s | **175 MB** |
| Streamed, run 2 | 1,200,000 | 35.5 s | **154 MB** |

The server starts at about 140 MB. **Holding the layer cost two gigabytes for 31 MB of archive; streaming it cost
nothing measurable above the server's own footprint.** Every run's layer answered `count: 1200000`.

**The price is the second read**: about five seconds, 16 %, for reading the layer once to learn the table's shape —
every column's type and the geometry column's dimensionality — and again to copy it. That is the trade the importer's
own note describes; the alternative, trusting the archive's declared field types, would change what columns an import
creates and was not taken.

One run of the held build, because the difference it is compared against is twelvefold; two of the streamed build,
to see the timing's noise, which was a tenth of a second.

## Found on the way

**The 64 MB upload limit was 30 MB.** The first attempt was refused `413` by Kestrel: the three routes that read a data
file (`/admin/hosted/import`, the overwrite and append route, and the ArcGIS `FeatureServer/uploads/upload` that `append` reads from) never raised the server's
30,000,000-byte default, so an archive between 30 and 64 MB — this one, at 31.2 MB — never reached the handler that
quotes 64. `HostedDataEndpoints.AllowUpload` raises it, and `AnUploadUpToTheLimitReachesTheServerTests` sends 40 MB and
70 MB and checks which side of the handler each refusal came from.

**What bounds this path now** is that upload: 64 MB of zipped archive. This one compressed 2.7 : 1, so the largest
layer the route accepts is in the region of 170 MB of geodatabase — a bound on the request, stated where the request is
refused, rather than on this process's memory.
