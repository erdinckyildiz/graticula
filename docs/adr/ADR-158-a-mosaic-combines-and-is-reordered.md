# ADR-158 — A mosaic combines its images, is reordered in Studio, and a map chooses its bands

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `MEDIUM` — each operator and each control is checked end to end on samples; blending is this server's own weighting, not a reproduction of ArcGIS's |
| **Decided** | 2026-10-03 by owner decision (*"3 saat kadar yokum. hepsini yap. devam et"* — the imagery list's items 28, 30 and 31, the gaps left after items 20–27) |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-152](ADR-152-a-mosaic-is-a-catalog.md) (compositing operators refused; no reorder in Studio), [ADR-151](ADR-151-ndvi-band-arithmetic-and-a-choice-of-bands.md) (band functions chosen in Studio's display settings only) |


> **Amended 2026-10-03 by [ADR-161](ADR-161-a-seamline-is-the-halfway-line.md).** The seamline method cuts between images rather than stacking them, unless pixels are combined.

---

## 1. Context

ADR-152 made a mosaic a catalog whose `mosaicRule` chooses the image on top, and refused by name the operators that
combine overlapping pixels — `MT_MIN`, `MT_MAX`, `MT_MEAN`, `MT_BLEND`, `MT_SUM` — which ArcGIS clients send for
composites: the greenest pixel of a season, a mean of several passes, seams blended away. The order of a mosaic's
images could only be changed through ArcGIS's `mosaicRule`, per request; Studio showed the order and could not change
it. And ADR-151's band functions — band combination, NDVI, band arithmetic — were offered in the service's own display
settings and not in the Map Viewer, where a reader who cannot change the service chooses how a layer is shown.

## 2. Alternatives considered

### Alternative A — Combine in the mosaic reader; reorder by rewriting the VRT; band arguments in the layer (chosen)

- **Operators.** `mosaicOperation` accepts `MT_MIN`, `MT_MAX`, `MT_MEAN`, `MT_BLEND` and `MT_SUM` as well as `MT_FIRST`
  and `MT_LAST`. The mosaic reader keeps, per pixel and band, a running total, a weight, a least and a most over every
  image the rule keeps, a source's no-data counting for nothing; the order still decides which images, and stops
  mattering for the combined value. `MT_BLEND` weighs a pixel by its distance inside its own image, plus one, up to 64
  pixels, so an image's edge counts little against another's interior. An unknown operator is refused naming the seven.
- **Reorder.** `POST /admin/coverages/{name}/images/{id}/move` with `{"direction":"up"|"down"}` swaps an image with its
  neighbour in the mosaic's own order — the VRT is rewritten, the catalog's ids and names stay — so the image drawn on
  top without a rule changes for every client. Studio's Images page has Raise and Lower on each row of an uploaded
  mosaic, focus following the image moved.
- **Map Viewer.** A layer's *Shown as* offers, for an image of three bands or more, *Band combination* (red, green and
  blue each chosen from the bands; false colour by default on four), for four or more *NDVI* (red and near-infrared
  chosen, refused when the same), and for two or more *Band arithmetic* (an expression of `B1`…`Bn`), beside ADR-136's
  surface functions for one band. The choice is the layer's `renderingRule` with its `rasterFunctionArguments`, saved
  with the map, sent to the service as ArcGIS's clients send it; the service is not changed.

### Alternative B — Composite in the browser

**Against:** the browser receives a picture, not the values beneath it, and a combined value has to be the same for
`identify`, `getSamples`, statistics and export, which only the server can make so.

### Alternative C — Keep refusing the operators

**Against:** it was the last refusal ADR-152 left in `mosaicRule` short of seamlines, and the composites it blocks are
the ordinary use of a mosaic of passes.

## 3. Counterarguments to the preferred option

- *Combining reads every image the rule keeps for every pixel*, where the top image alone was enough; a rule over many
  overlapping images costs that many reads.
- *Blend's weighting is this server's.* ArcGIS documents what `MT_BLEND` is for, not the function it weighs by; a
  client comparing pixels against ArcGIS's will see differences near seams.
- *Raise and Lower move one place at a time.* A mosaic of hundreds of images is reordered by its rule, not by hand.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Two images of 100 and 200 over the same ground: `MT_MIN` 100, `MT_MAX` 200, `MT_MEAN` 150, `MT_SUM` 300, `MT_BLEND` 150; `MT_AVERAGE` refused naming `MT_MEAN`; moving the second image down puts the first on top, and back | `MosaicCatalogConformanceTests` | this repository |
| An unknown operator is refused naming those served | `MosaicRuleTests` | this repository |

## 5. Decision

As §2, Alternative A.

## 6. Consequences

**Positive.** Composites, a mosaic's order set once for every client, and band choices made by whoever reads the map.

**Negative.** Combined reads cost in proportion to the images kept.

**State.** None new: the order is the VRT's, the band choice the map's.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | A pixel a source marks no-data is absent from it, not a value to combine | As ADR-140 reads no-data |

## 8. Dependencies

**Depends on:** ADR-136, ADR-140, ADR-151, ADR-152.

**Depended on by:** —

## 9. Revisit triggers

- A composite over many images too slow to draw.
- A client that needs ArcGIS's own blend weights.
