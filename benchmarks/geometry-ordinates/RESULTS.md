# Geometry ordinates — Results

**Run:** 2026-09-16. **Settles:** ADR-077 §4 — whether carrying Z and M in the geometry model costs a
flat geometry anything.
**Harness:** [`Program.cs`](Program.cs), built against a checkout's `Graticula.Core` and
`Graticula.Api.ArcGis` Release assemblies: `dotnet run -c Release -p:Repo=<checkout> [-- points]`.

## What is measured, and why this path

ADR-074 §5 said step 2 would *re-run the tile benchmark*. **That premise was wrong**: production vector
tiles are encoded by PostGIS (`PostGisMvtEncoder`, `ST_AsMVT`), and the C# tile harness in
[`../harness`](../harness) measures experiment code that was never promoted. `XySequence` carries load
on the **query** path — WKB from PostgreSQL decoded by `WkbReader`, written as ArcGIS JSON by
`ArcGisGeometryWriter` — so that is what this measures.

- **Polygons:** 50,000 two-ring polygons (31 + 12 vertices), ISO WKB, ~2 million coordinates, 81 MB JSON.
- **Points:** 1,000,000 points, ISO WKB, 92 MB JSON.
- Three warm-up runs, then nine measured; **allocated bytes** (`GC.GetAllocatedBytesForCurrentThread`,
  identical run to run) is the figure that decides, because allocation is this server's binding
  constraint (A-037). Time is reported and is noisy on this machine.

## Results

| Build | Polygons allocated | Points allocated | Polygons min / median ms |
|---|---|---|---|
| Before (`3f362d7`) | **53.79 MB** | **83.92 MB** | 541–635 / 669–933 |
| First version: a second reference in `XySequence`, an array reference on `Point` | 54.55 MB (+1.4%) | 91.55 MB (**+9.1%**) | 561–598 / 762–856 |
| `Point` subclass for Z/M, `XySequence` unchanged | 54.55 MB | 83.92 MB | 559–605 / 709–874 |
| **Final: one field in `XySequence`, and the reader's keep path** | **53.79 MB** | **83.92 MB** | 590–614 / 751–805 |

**The first version cost exactly what its layout predicted**: eight bytes per ring (100,000 rings ×
8 = 0.76 MB) and eight bytes per point (1,000,000 × 8 = 7.63 MB). The final version costs a flat
geometry nothing that can be measured in allocation, and its times overlap the baseline's.

## Environment

Windows 11, .NET 9.0 Release, tiered PGO, workstation GC, one thread. Not the production host; the
ratios are the result, not the milliseconds.
