# Raster warp — what the control grid costs, in pixels

**Measured 2026-08-21.** This is
[ADR-043](../../docs/adr/ADR-043-imageserver-and-the-raster-face.md)'s **condition 2**,
which asks for *a number in `benchmarks/` saying how far a pixel can land from where it
belongs, at the grid density shipped* — rather than for a claim that the approximation
is small.

**The harness is `CoverageWarpTests` in `Graticula.Core.Tests`**, and it runs on every
build. A benchmark nobody runs is a number that goes stale, which
[D-30](../../docs/architecture-debt.md) is a debt row about; this one fails the suite if
the claim stops being true.

## Why an ImageServer warps at all

A coverage is stored in whatever reference it was captured in. Every web client asks for
Web Mercator. So the common request is *draw this 4326 imagery into a 3857 canvas*, and
the only way to do that is to know, for each canvas pixel, where on the ground it lands.

**Projecting all of them would be exact.** A 1024×768 request is 786,432 pixel centres,
and this server's projection engine is PostGIS — that is a round trip carrying three
quarters of a million coordinates, per request. So the canvas is divided into a grid,
the grid's corners are projected, and every pixel between them is interpolated. That is
what every raster warp does, and it is an approximation.

**This measures the approximation and nothing else.**

## Method

**Web Mercator to WGS 84, because that pair has a closed form.** Measuring against
PostGIS would mix the interpolation error this condition is about with whatever the
projection library does about datums and grids. EPSG:3857 is defined as a sphere with no
datum shift, so the formula is exact to the last bit and the difference it reveals is
the interpolation alone.

- Request: 2,200 km by 100 km in Web Mercator, over Turkey, drawn at **1024×768**. Wide
  enough that Mercator's curvature is doing real work across it.
- Coverage: degrees, at roughly 2048 pixels over 20°, so one coverage pixel is about
  what one canvas pixel wants — the case where a positional error is most visible.
- Sampled every 7th pixel in both directions; the figure is the **worst** of them, not
  the mean.

## Result

| Grid steps | Control points | Worst error (degrees) | Worst error (coverage pixels) |
|---:|---:|---:|---:|
| 2 | 9 | 0.01388512 | **1.4218** |
| 4 | 25 | 0.00348272 | 0.3566 |
| 8 | 81 | 0.00087188 | 0.0893 |
| **16 — shipped for this size** | **289** | **0.00021755** | **0.0223** |
| 32 | 1,089 | 0.00005447 | 0.0056 |
| 64 | 4,225 | 0.00001354 | 0.0014 |

**At the density that ships, the worst pixel lands 0.0223 coverage pixels from where it
belongs.** That is two hundredths of a pixel, and the assertion in the test is a tenth.

**The error falls by four for every doubling**, which is what bilinear interpolation of a
smooth function does and is the sanity check that the arithmetic is right rather than
merely small. `A_denser_grid_is_never_worse` asserts the monotonicity as a property,
because a table alone cannot tell an approximation from a mistake.

## The density rule, and why it is expressed in pixels

`CoverageWarp.StepsFor` divides the longer side by 64 and clamps to 2–64, so a cell is
about sixty-four pixels across whatever the request size.

**The grid is spaced in canvas pixels rather than on the ground**, and that is the
decision the table justifies. How linear a projection is between two points depends on
how far apart they are *on the screen*, which is what stays constant as a client zooms.
A ground-spaced grid would be dense where the map is zoomed out and sparse where it is
zoomed in, which is backwards.

**Sixty-four is chosen from this table.** Thirty-two pixels per cell would cost 1,089
points to buy 0.0056 — four times the round trip for an error already two hundredths of
a pixel below anything visible. A hundred and twenty-eight would drop to 81 points and
0.0893, still under a tenth of a pixel and with less margin than is comfortable for a
projection more curved than Mercator.

## What this does not measure

- **Other projections.** Mercator is smooth and conformal. A polar stereographic or an
  oblique aspect curves harder, and a coverage in one would carry more error at the same
  grid density. Nothing here says how much.
- **The resampling.** This is about *where* a pixel lands, not *which* colour it gets.
  Nearest neighbour is what the resampler uses and its own error is up to half a
  coverage pixel by construction, which dominates everything in the table above.
  That trade is argued in `CoverageWarp.Resample` and is a decision rather than an
  approximation: averaging categorical values invents classes that do not exist.
- **The round trip.** How long PostGIS takes to project 289 points is not measured here.
  The performance gate is where that belongs, and [D-128](../../docs/architecture-debt.md)
  already records that the connection budget does not shed load on every path.
