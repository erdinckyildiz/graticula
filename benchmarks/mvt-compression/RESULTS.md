# What does compression buy on protobuf? — ADR-068 §9

**Run 2026-09-28** on the arm64 showcase host (6 cores, .NET 9.0.20, the runtime the server
image runs), with the same encoders the server uses: `BrotliStream` and `GZipStream` at
`CompressionLevel.Fastest`. Each input is compressed 20 times and the mean is reported.
Scripts: [collect.py](collect.py) downloads the tiles, [Program.cs](Program.cs) measures them.
Raw output: [measured-arm64.txt](measured-arm64.txt). The same run on an x64 workstation
(.NET 10) gave the same ratios to two decimals and about 7 µs per KiB.

**ADR-068 left protobuf out because nobody had measured what compression does to a packed
binary structure.** The answer is: a lot, except when the tile is tiny.

## The corpus

- **274 tiles** from 12 showcase services: the tile at the centre of each service's full
  extent and its eight neighbours, at the zoom where the extent is about one tile, two, four
  and six zooms deeper, and at z14 and z16.
- The services cover polygons (buildings in Istanbul and New York, parcels, provinces,
  districts), lines (roads) and points (places), and hosted PostGIS, GeoParquet and DuckDB
  sources.
- Only non-empty `200` answers are kept, 2.8 MiB in all. None starts with the gzip magic
  bytes, so none was compressed already.
- **Three glyph ranges** (`DejaVu Sans`, 0-255, 256-511 and 1024-1279) and **four
  FeatureServer `f=pbf` answers** (up to 2,000 features each) are measured beside them,
  because they are served as `application/x-protobuf` and the same MIME type decides for them.

## Results

| What | n | raw | brotli | gzip | brotli, per item |
|---|---|---|---|---|---|
| **All tiles** | 274 | 2,874.7 KiB | 1,673.8 KiB (**1.72x**) | 1,843.5 KiB (1.56x) | median **21 µs**, max 3.4 ms |
| tiles < 1 KiB | 135 | 33.1 KiB | 30.1 KiB (1.10x) | 31.3 KiB (1.06x) | median 8 µs |
| tiles 1-16 KiB | 100 | 574.6 KiB | 345.2 KiB (1.66x) | 382.7 KiB (1.50x) | median 53 µs |
| tiles 16-64 KiB | 28 | 894.0 KiB | 523.5 KiB (1.71x) | 571.0 KiB (1.57x) | median 274 µs |
| tiles ≥ 64 KiB | 11 | 1,372.9 KiB | 775.0 KiB (1.77x) | 858.5 KiB (1.60x) | median 1.2 ms |
| **Glyph ranges** | 3 | 425.0 KiB | 76.6 KiB (**5.55x**) | 126.0 KiB (3.37x) | median 0.9 ms |
| **FeatureServer `f=pbf`** | 4 | 668.1 KiB | 287.8 KiB (**2.32x**) | 363.9 KiB (1.84x) | median 2.0 ms |

The largest tile, `istanbul_roads` z12, went from 270.1 KiB to 142.5 KiB in 3.4 ms.
Provinces and districts compress best (2.06x and 1.92x). The sparse tiles of small hosted
layers compress least (`tr_kara` 1.00x, `look_buildings` 1.03x).

**102 of 274 tiles grew under brotli**, every one of them under a kilobyte, by a few bytes
each: the encoder's framing is larger than what it can find to save in 200 bytes of varints.

## What it decides

**Both protobuf types go into `ResponseCompressionPolicy.MimeTypes`.**

- **What it saves:** about 42% of tile bytes on the wire. The glyph ranges a map downloads
  before it can draw its first label shrink by 82%.
- **What it costs:** about 11 µs of CPU per KiB. For the median tile that is 21 µs. For the
  worst tile in the corpus it is 3.4 ms, beside the tens of milliseconds a cold tile takes to
  build.
- **Why small tiles are not skipped:** ASP.NET's middleware has no minimum size, and a rule
  of our own would be a second gate to keep right for a saving of a few bytes. The growth is
  bounded and small, so it is accepted.

**`tileCompression` in the service document stays `"none"`.** That field describes the tile
bytes themselves, which are still plain protobuf. What changes is the HTTP
`Content-Encoding`, which the client's HTTP stack removes before a renderer sees anything.

## What this did not measure

- **Behaviour under load.** ADR-068 §9's third trigger still applies: if p95 latency at the
  measured peak rises, compression is the first thing to look at.
- **Tiles a client actually requests in a session.** The corpus is a spread of zooms around
  each extent's centre, not a replay of real traffic, so its share of tiny tiles is a guess.
