# ADR-123 — Imagery comes into Studio, and an export refuses what it does not apply

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` — the refusal is measured; the scope it opens was inferred and is confirmed |
| **Decided** | 2026-10-01. The refusal by this ADR; **bringing imagery's publishing, styling and Map Viewer into Studio** was inferred from the owner's *"Başlayalım"*, said to a question that offered either the refusal alone or imagery taken into scope in order — listed as [Q-158](../open-questions.md) — **confirmed by the owner 2026-10-01** (*"1 ve 2 onayladım"*) |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-043](ADR-043-imageserver-and-the-raster-face.md) (what an export reads), [v1-scope](../v1-scope.md) §3b (imagery's place) |

> **Amended 2026-10-01 by [ADR-124](ADR-124-an-image-service-is-shared-to-a-group.md).** §5.1's refusal now covers `identify` too, and an image service is shared to a group.


> **Amended 2026-10-02 by [ADR-136](ADR-136-an-elevation-model-is-shaded-and-sloped.md).** Hillshade, Slope and Aspect are applied by `renderingRule`; any other function is still refused by name.

> **Amended 2026-10-02 by [ADR-138](ADR-138-a-renderer-sent-as-a-rule-is-drawn.md).** A `renderingRule` of `Stretch`, `Colormap` over `Stretch` and `Colormap` over `Remap` — the chains the JS SDK sends for a renderer — is drawn; other chains are still refused by name.

> **Amended 2026-10-02 by [ADR-139](ADR-139-an-uploaded-image-is-given-its-overviews.md).** An image uploaded without overviews is given them beside it (`.ovr`), during the upload.

> **Amended 2026-10-02 by [ADR-140](ADR-140-several-images-are-one-mosaic.md).** Several GeoTIFFs uploaded together, on one grid, are published as one mosaic.

> **Amended 2026-10-03 by [ADR-142](ADR-142-an-image-is-read-between-its-cells-as-asked.md)** (`interpolation` is applied) **and [ADR-143](ADR-143-an-uploaded-image-is-given-back.md)** (an uploaded file can be downloaded by its owner).


> **Amended 2026-10-03 by [ADR-157](ADR-157-imagery-in-other-formats-is-written-as-geotiff.md).** JPEG 2000, NetCDF, HDF, ERDAS Imagine and ASCII grids are uploaded too, written as GeoTIFF on the way in.

---

## 1. Context

The ArcGIS reviewer's imagery pass, 2026-10-01: ImageServer serves one GeoTIFF or COG, and ArcGIS Pro and the JS SDK
open it — but a Portal user's *Imagery layer* is a skeleton here. Nothing uploads or publishes it from Studio; its
style cannot be set and the default stretch turns 12-bit imagery black and a float DEM white; the Map Viewer does not
add it; a pixel pop-up from an ArcGIS client is very likely refused. And `exportImage` **read and ignored**
`renderingRule`, `bandIds`, `mosaicRule` and `time`, answering a request for a hillshade with the plain image — the
*accepted and not applied* shape [D-125](../architecture-debt.md) names.

ImageServer is outside v1 (§3b) and was built beside it by ADR-043.

## 2. Alternatives considered

### Alternative A — Refuse what is not applied now, and bring imagery into Studio in order (chosen)

The refusal first, because it is a defect whatever the scope; then, in the reviewer's order: uploading and publishing
a GeoTIFF or COG from Studio, setting its stretch and colours, the Map Viewer drawing it, and `identify` reading the
point ArcGIS clients send.

### Alternative B — The refusal only

**Argument for.** It keeps v1's scope as written. **Against:** the owner said *"Başlayalım"* to the larger choice.

## 3. Counterarguments to the preferred option

- *Three parameters are still read and not applied.* ArcGIS Pro sends `noData=0,0,0`, `interpolation=RSP_NearestNeighbor`
  and `pixelType=U8` on every draw — the replayed request in `ImageServerConformanceTests` — so refusing them refuses
  Pro. They are hints about edges and resampling rather than a different picture, and they are named here rather than
  hidden.
- *Imagery enlarges the release*, as relationships and attachments did; recorded rather than absorbed.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| A raster function, a band order, a mosaic rule and a time are refused; an empty rule and the identity band order are drawn; Pro's own request still is | `ImageServerConformanceTests.What_this_server_does_not_apply_is_refused_not_drawn_as_the_default` and the replayed Pro test | this repository |

## 5. Decision

5.1 `exportImage` refuses, with what it does instead, a `renderingRule` other than none, a `bandIds` other than the
bands in order, a non-empty `mosaicRule` and a `time`. It reads and does not apply `noData`, `interpolation` and
`pixelType` (§3).

5.2 Imagery comes into Studio in the order of the conditions below, each its own release.

## 6. Consequences

**Positive.** No export answers a question it did not answer. Imagery becomes publishable and usable from the console.

**Negative.** The release grows by the imagery work.

**State.** None for 5.1.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | The owner's *"Başlayalım"* takes imagery into scope in the order offered | Confirmed by the owner 2026-10-01 — [Q-158](../open-questions.md) |

## 8. Dependencies

**Depends on:** ADR-043 (ImageServer), ADR-044 (its tiles).

**Depended on by:** —

## 9. Revisit triggers

- The owner answers Q-158 otherwise.

## 10. Conditions

1. **An export refuses what it does not apply** — **DISCHARGED 2026-10-01**, `ImageServerConformanceTests`.
2. **A GeoTIFF or COG is uploaded and published from Studio** — **DISCHARGED 2026-10-01**, `ImageryUploadTests` and
   `ImportFormTests.A_GeoTIFF_is_offered_its_own_form_and_uploaded_from_it`. Kept under the state volume's `imagery/`
   directory (it is primary data and must survive a container replacement), streamed to disk, at most 4 GB, and
   deleted with its service; a file registered in place is still never touched.
3. **An imagery layer's stretch and colours are set, and the default does not blacken 12-bit or whiten float data** —
   **DISCHARGED 2026-10-01**, `ImageryDisplayTests` (a float elevation model drawn 0–255 by default where the format's
   range drew it all 255) and `ImportFormTests.An_image_services_display_is_set_on_its_page`. Eight-bit data keeps the
   full range; wider data is stretched over its sampled values — one band between its minimum and maximum, colour two
   standard deviations about the mean — fixed, so tiles agree. `stretch:auto|full|lo,hi` and `;ramp:<name>` in the
   stored style; five named ramps for one or two bands.
   The Display page's picture is drawn under the controls before they are saved — `GET /admin/coverages/{name}/preview`,
   its owner's only — with the ramp and the range it runs over beneath it (design review 2026-10-01).
4. **The Map Viewer adds and draws an imagery layer** — **DISCHARGED 2026-10-01**,
   `WebMapImageryTests.An_image_service_is_added_to_a_map_drawn_and_its_pixel_identified`. Listed under *Imagery* in
   Add layer, saved as `ArcGISImageServiceLayer`, drawn from `exportImage` as PNG so its no-data shows the map beneath,
   and a click on it answers the pixel's value beside any features there.
5. **`identify` reads the point ArcGIS clients send** — **DISCHARGED 2026-10-01**, `ImageryDisplayTests`: an Esri JSON
   point in Web Mercator is read and projected into the coverage's reference, where only `x,y` in the coverage's own
   was read before.
