# A parcel fabric of thousands of parcels in grapy — ADR-250 condition 3

**Run 2026-11-01.** **Owed by:** [ADR-250](../../docs/adr/ADR-250-grapy-holds-parcel-fabrics.md) condition 3, which
asks for a fabric of some thousands of parcels to be built and adjusted and its time recorded, against
[ADR-223](../../docs/adr/ADR-223-user-tools-run-sandboxed-on-grapy.md)'s limits for a user tool: **2 GB of memory, 10
minutes of CPU, 30 minutes of wall clock**. **Script:** [`measure.py`](measure.py). **Raw:** `measured-*.json`.

## The answer

**A fabric of 3,000 parcels builds and adjusts in 31 s of CPU and 309 MB; 10,000 parcels in 222 s and 795 MB.** Both
fit the limits. Before this work neither did: 900 parcels took 70 s of CPU to build and **810 s of CPU and 1,155 MB**
to adjust, over the CPU limit at less than a third of the smaller fabric.

| Parcels | Lines | Points | Build (CPU s) | Adjust (CPU s) | Apply (CPU s) | All steps (CPU s) | Peak memory |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 900, before | 1,860 | 961 | 70.2 | **809.9** | 1.0 | 881.8 | 1,155 MB |
| 3,000 | 6,110 | 3,111 | 4.6 | 19.8 | 3.5 | 30.9 | 309 MB |
| 10,000 | 20,200 | 10,201 | 12.1 | 194.2 | 10.1 | 221.6 | 795 MB |

The adjustment is a weighted one with four XYZ-constrained corners: 12,230 measurements and 6,214 unknowns at 3,000
parcels (40,400 and 20,394 at 10,000), converged, standard error 0.014.

## The fabric

A grid of lots 20 m by 30 m in UTM 32N, so the adjustment runs on the ellipsoid with a scale that is not 1; every
corner moved up to 0.2 m at random (seeded), so the drawn fabric disagrees with its dimensions; one record per row of
lots. Steps: create the fabric and insert the lots, `BuildParcelFabric` (lines and points from the polygons), write
each line's Distance and Direction from the undisturbed grid, constrain the four corners,
`AnalyzeParcelsByLeastSquaresAdjustment` (weighted), `ApplyParcelLeastSquaresAdjustment`. CPU time is the process's,
all threads (`time.process_time`); memory the peak working set.

## What was slow, and what changed

- **The build searched every node for every edge of every ring** (`_parcel_build._pieces`, the "pure-Python node
  search" the condition named): 705,600 point-on-segment tests through shapely for 400 parcels, quadratic in the
  fabric. The nodes are now indexed once (an `STRtree`, queried for each ring's segments within the tolerance). 900
  parcels: 54 s to 1.9 s of CPU.
- **The adjustment was dense**: the design matrix, an SVD of it every iteration to find a datum defect, and the
  normal matrix's full inverse for the statistics. It is now sparse (scipy): the normal equations factored by SuperLU,
  a datum defect found as a vanishing pivot, and only the cofactors the report needs — each point's 2 × 2 block and
  each reported measurement's quadratic form — solved for a block of 512 unit columns at a time. The single largest
  cost left is those solves (7.5 of 18 s at 2,500 parcels).
- **The Arcade rules on points** the condition also named cost 1.8 s in a profiled run of 2,500 parcels
  (`_rules.feature`, inside the build's and the adjustment's writes); not changed.

## What this does not settle

- One machine (Windows 11, 16 cores, numpy's BLAS free to use them), not the fenced Linux sandbox; the 3,000-parcel
  run shared the machine with another agent's load measurement (its wall clock 40.6 s against 30.9 s of CPU).
- A grid of rectangular lots. A fabric of irregular parcels with curves has more points per parcel; the time grows
  with points and lines rather than parcels.
