# D-295 — what a GeoTIFF read costs when it is made in a child process

**Run 2026-11-01** on the shared development PC (i7-11800H, 8 cores / 16 logical, Windows 11, .NET 9 Release), CPU
about 20 % busy with other sessions' editors and browsers before the runs. Disposable rig; `Program.cs` is all of it.

## The question

[D-295](../../docs/architecture-debt.md): every ImageServer tile, identify, export and WCS read decodes a stranger's
GeoTIFF in the serving process. Moving the reads behind a worker pool, as overlays are (`GeometryWorkerPool`), puts a
pipe on every image request. What does that pipe cost per request, and under load?

## What was measured

Each request is what the ImageServer face does today: **open the file, read one window at full resolution, close it**
(`ImageServerEndpoints` opens a reader per request), through the server's own `TiffCoverageReader`. Two files written by
the server's `TiffGridWriter` (256-pixel tiles, deflate): `float.tif` 4,096 × 4,096 × 1 band Real32 (58 MB) and
`rgb.tif` 4,096 × 4,096 × 3 bands byte (40 MB). Random windows from a fixed seed, 20 discarded, then 400 (256 × 256,
a tile) or 60 (1,024 × 1,024, an export). Four ways:

- **in-process** — the server's shape today.
- **worker, binary** — the same calls in a child process: a JSON request line in, the window back as a length-prefixed
  frame of float64s. **worker, native** — the frame at the samples' own width (1 byte for imagery, 4 for Real32).
- **worker, json** — the answer as one JSON line with the samples in base64: the wire the import reader and the overlay
  worker speak today.
- **×8** — eight callers at once: eight in-process threads against a pool of eight children, each caller holding one.
  Children run workstation GC, as `Graticula.Import.Reader` and `Graticula.Overlay.Worker` do.

Three repetitions, interleaved file by file. Two earlier rounds ran the children with server GC, which a worker does not
use; they are not in the tables below.

## Results — ms per request, and requests a second

### One request at a time

| file, window | in-process p50 / p95 | worker binary p50 / p95 | worker json p50 / p95 |
|---|---|---|---|
| float, 256² | 8.0–9.7 / 14.9–19.3 | 8.9–9.6 / 13.7–15.9 | 16.3–18.1 / 29.3–34.1 |
| rgb, 256² | 6.7–8.1 / 10.4–13.3 | 8.1–9.3 / 13.0–13.8 | 24.3–27.2 / 40.3–43.9 |
| rgb, 1,024² | 50.0–59.4 / 71.0–88.0 | 75.1–81.7 / 97.3–105.8 | 588–733 / 818–908 |

A child takes **105–130 ms** from start to its first answer (200–250 ms for the first 1,024² window).

### Eight callers at once

| file, window | in-process ×8 req/s, p50 / p95 | pool ×8 binary req/s, p50 / p95 | pool ÷ in-process |
|---|---|---|---|
| float, 256² | 558–614, 12.7–13.7 / 15.9–21.4 | 391–462, 16.2–18.6 / 26.0–33.1 | **0.70–0.75** |
| rgb, 256² | 574–688, 10.8–12.0 / 20.9–28.9 | 336–374, 20.0–22.2 / 34.7–41.6 | **0.54–0.62** |
| rgb, 1,024² | 81–84, 89–93 / 114–120 | 56–59, 127–132 / 167–190 | **0.68–0.72** |

The native-width frame changed the 256² rows by nothing outside the noise and the 1,024² rows by +15–20 % (67–69 req/s).

## What it says

1. **One at a time the pipe costs about a millisecond on a tile** (+0–20 %) — on a binary wire. On the wire the
   existing pools speak, JSON with base64, it is **2–3×** on a tile and **10–13×** on an export: a raster pool needs a
   wire of its own, which neither pool has.
2. **Under load it costs a quarter to almost half of the throughput**: eight children serve 54–75 % of what eight
   threads do, and p95 rises by half again to double. The window is copied out of the child and into the host, and each
   child decodes in its own heap.
3. **So moving the reads is a design with a measured price, not wiring**: a new pool with a binary frame, warm
   children (a cold one is 0.1 s), and 25–45 % less imagery throughput on this machine.

## What it does not say

- Not measured on Linux or on the arm64 showcase. Not measured with the reader kept open between requests in the child,
  which is what a real pool would do and the server does not do today in either place.
- Not a measurement of the risk itself. `BitMiracle.LibTiff.NET` is managed code: a defect in a block is an exception
  this request catches, not a corrupted process. What a child would still contain and the serving process cannot is a
  stack overflow or a decode that never ends — neither found by the door's bounds or by any file so far.
