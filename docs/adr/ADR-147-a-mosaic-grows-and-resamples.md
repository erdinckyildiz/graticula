# ADR-147 — A mosaic grows, and images that do not fit its grid are put on it

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `MEDIUM` — the resampling is the raw export's, measured there; the growth is checked end to end |
| **Decided** | 2026-10-03 by owner decision, asked directly ([Q-160](../open-questions.md): *"İkisi de"* — add images later, and resample) |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-140](ADR-140-several-images-are-one-mosaic.md) (§2's narrowings: aligned tiles only, made in one upload) |


> **Amended 2026-10-03 by [ADR-152](ADR-152-a-mosaic-is-a-catalog.md).** Images are also removed, renamed and dated, and keep their object ids.

---

## 1. Context

ADR-140 made a mosaic of aligned tiles in one upload and listed both narrowings for the owner, who chose both
extensions: a mosaic is added to after it is made, and images of another resolution or reference are taken in.

## 2. Alternatives considered

### Alternative A — Put each misfit on the grid, once, as a file of its own (chosen)

- **Resampling.** An image in another reference, at another pixel size or off the grid is projected and resampled onto
  the mosaic's grid — the grid of the image most of the others share — and written as a GeoTIFF of its own, tiled and
  deflated, a block of 256 pixels at a time, through the same read the raw export uses (ADR-127's warp, ADR-142's
  resampling: bilinear for floating point, nearest for integers). Its empty edge is no-data — NaN for floating point,
  0 for integers that declare none — so it does not cover its neighbour. The file sent is replaced by the conformed one.
  Other bands or another sample type cannot be made to fit and are refused, each named.
- **Growth.** `POST /admin/coverages/{name}/images` adds images to an uploaded image service: one image becomes a
  mosaic, a mosaic grows. New images are measured against its first image's grid, conformed where they do not fit, given
  their pyramids, and drawn over the ones it had; a new virtual raster replaces the old. The service keeps its name,
  sharing, style and Download setting. Studio's Overview offers *Add images*, with a bar, Stop, and the outcome left
  on the page naming each image resampled. Bands and sample type are measured before the reference, so an image
  with both wrong is refused by name rather than resampled and then failing when the mosaic is written — the first
  version answered that with a 500, found by the ux review.

### Alternative B — A warped virtual raster (GDAL's VRTWarpedDataset)

**Against:** a second reader of a second format to read every request; conforming once costs the disk once.

## 3. Counterarguments to the preferred option

- *A conformed image is a copy*, at the mosaic's resolution. A coarse image put on a fine grid takes the fine grid's
  space. At most 60,000 pixels a side.
- *Images added are measured against the first image's grid*, not the new majority's, so a mosaic's grid never moves
  under the images it already has.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| A tile at 0.004° among tiles at 0.00213° is resampled, published and answers values where it lies; a two-band tile is refused naming it | `ImageryDisplayTests` | this repository |
| An image becomes a mosaic when a Web Mercator tile is added: two images, one resampled, the extent grown to 31.18° E, values in the added tile | the same | this repository |

## 5. Decision

As §2.

## 6. Consequences

**Positive.** Imagery from several sources is one service, and grows.

**Negative.** Conformed images are copies.

**State.** None new.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | Both extensions are wanted | Stated by the owner, 2026-10-03 — [Q-160](../open-questions.md) |

## 8. Dependencies

**Depends on:** ADR-127, ADR-139, ADR-140, ADR-142.

**Depended on by:** —

## 9. Revisit triggers

- Owners asking to remove an image from a mosaic, or to reorder images.
