# ADR-141 — An image service answers about an area and along a line

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `HIGH` — the statistics agree with numpy on the same file to the last digit, and a hole is left out exactly |
| **Decided** | 2026-10-03 by owner decision (*"devam et … image layers'ın kalan işlerini tamamla"*, the imagery list's next item) |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-043](ADR-043-imageserver-and-the-raster-face.md) (the operations an image service answers) |

---

## 1. Context

`identify` answered one pixel. ArcGIS's image service answers about an area — `computeStatisticsHistograms`, the
statistics and histogram of the pixels inside a polygon, which Pro's and the JS SDK's measurement tools and an
elevation profile's summary ask for — and along a line or at many points — `getSamples`, which a profile tool draws
from. Both were refused as operations this service does not serve.

## 2. Alternatives considered

### Alternative A — Read the area's window once, from the right level (chosen)

The geometry — Esri JSON polygon, envelope, polyline, multipoint or point, in its own `spatialReference` or `sr` — is
projected into the image's reference. The window it covers is read once, from the finest level of the image's pyramid
(ADR-139) at which it is at most four million pixels and not finer than a `pixelSize` asked for, so an area the size of
a country answers from an overview, and says the cell size it used (`pixelSize`, a field of ours). It is read through
the raster function asked for or the service's default, as `identify` is: the slope inside a polygon is what an
elevation model is uploaded to answer.

- **computeStatisticsHistograms**: the cells whose centres are inside the polygon (even-odd, so a hole is out; a row at
  a time between the rings' crossings), no-data and NaN left out — count, min, max, sum, mean, standard deviation and
  median for each band, and a 256-bin histogram (an 8-bit band's bins are its values). A point or a line is refused.
- **getSamples**: a point is its pixel; a multipoint, each point; a line, evenly along it, its ends included, by
  `sampleCount` or `sampleDistance`; an area, cell centres inside it, as many as `sampleCount` allows — at most 5,000.
  Each sample is a location in the image's reference, its value as `identify` spells it, and the resolution read at.

### Alternative B — Read every cell at full resolution

**Against:** an area as large as the image would read all of it for one number.

## 3. Counterarguments to the preferred option

- *An area larger than four million cells is answered from an overview*: its statistics are an overview's — its mean
  the same, its extremes softened for floats (ADR-139 averages them). The cell size used is in the answer.
- *`interpolation` is nearest for samples*: a sample's value is a value the image holds.
- *`outFields`, `returnFirstValueOnly` and `mosaicRule`* have one image to answer about; attributes are empty.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| 160,000 cells of a 2000-pixel float model: count, min, max, mean and median equal numpy's on the same file; a square hole removes exactly its 40,000 | requests against the fixture beside a numpy computation | local fixture, 2026-10-03 |
| A point answers its pixel; a line's five samples include both ends, the last on the image's edge; a polygon in Web Mercator through Slope answers slopes | the same | local fixture, 2026-10-03 |
| The whole 4,000,000-cell image summarised in 0.77 s on a Debug build | the same | local fixture, 2026-10-03 |
| Statistics of a 10 × 10 block, an envelope, a refused point; samples at a point, along a line, across an area | `ImageryDisplayTests` | this repository |

## 5. Decision

An image service answers `computeStatisticsHistograms` for a polygon or envelope and `getSamples` for a point,
multipoint, line or area, as §2 describes.

## 6. Consequences

**Positive.** Measurement and profile tools in ArcGIS clients work against an image service here.

**Negative.** None known.

**State.** None.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | The imagery list is worked through in order | Stated by the owner, 2026-10-03 |

## 8. Dependencies

**Depends on:** ADR-043, ADR-136, ADR-139.

**Depended on by:** —

## 9. Revisit triggers

- A client measured sending `getSamples` with a mosaic rule or attributes it expects filled.
