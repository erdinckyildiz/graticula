# ADR-161 — A mosaic's seamline is the halfway line between its images

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `MEDIUM` — checked end to end on two overlapping images; ArcGIS's generated seamlines follow more than distance, and these do not |
| **Decided** | 2026-10-03 by owner decision (*"Seamline'ı yap, sonra 'yes'"* — asked how to close the image services' last gaps) |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-152](ADR-152-a-mosaic-is-a-catalog.md) (`esriMosaicSeamline` refused), [ADR-158](ADR-158-a-mosaic-combines-and-is-reordered.md) |

---

## 1. Context

`esriMosaicSeamline` draws each image of a mosaic only inside its seamline, so overlapping images meet along a cut
rather than one lying over the other. ArcGIS's mosaic datasets generate seamlines with Build Seamlines — by
footprint, by radiometry, by edge detection — and store them. ADR-152 refused the method because this server makes
no seamlines. With the owner's decision, it was the last mosaic method refused.

## 2. Alternatives considered

### Alternative A — The seam where two images' centres are equally near (chosen)

- Under `esriMosaicSeamline` each pixel takes the value of the image covering it whose centre is nearest: the
  seamlines are the halfway lines between image centres — a Voronoi partition of the centres, clipped to where each
  image has data — which is what ArcGIS's footprint method approximates. Computed while the mosaic is read, in the
  reader that already places every image (`MosaicOperation.Nearest`), so no seamline is stored and none goes stale
  when images are added, removed or reordered.
- An image's no-data is not drawn, so where the nearer image has none the next nearest shows.
- Given with a combining operator (`MT_MEAN`, …), the pixels are combined as ADR-158 combines them.
- `allowedMosaicMethods` lists `Seamline`.

### Alternative B — Generate and store seamline polygons

**Against:** a stored polygon set to keep in step with every edit of the catalog, for the same cut the halfway line
gives over footprints.

### Alternative C — Keep refusing

**Against:** it was the last refused method.

## 3. Counterarguments to the preferred option

- *Not radiometric.* ArcGIS can route a seam along roads and field edges so it does not show; this one is straight.
- *No feathering.* ArcGIS blends a width either side of the seam; here the cut is sharp unless `MT_BLEND` is asked.
- *Centres, not footprints.* Two images of very different sizes meet nearer the smaller one's centre than a
  footprint Voronoi would put it.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Two images overlapping by half: the last added is on top across the overlap; under the seamline each is drawn on its side of the halfway line, and alone where it alone covers; the service lists Seamline | `MosaicCatalogConformanceTests.A_seamline_draws_each_image_on_its_own_side_of_the_halfway_line` | this repository |
| Seamline cuts unless pixels are combined | `MosaicRuleTests` | this repository |

## 5. Decision

As §2, Alternative A.

## 6. Consequences

**Positive.** Every mosaic method ArcGIS clients send is applied.

**Negative.** Seams are geometric, and visible where neighbouring images differ in tone.

**State.** None.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | A client asking for the seamline method wants overlapping images cut rather than stacked, and does not depend on where exactly the cut falls | Unvalidated |

## 8. Dependencies

**Depends on:** ADR-140, ADR-152, ADR-158.

**Depended on by:** —

## 9. Revisit triggers

- An owner who needs seams that follow the ground, or a feathered seam.
