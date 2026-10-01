# ADR-123 — Imagery comes into Studio, and an export refuses what it does not apply

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` — the refusal is measured; the scope it opens is inferred |
| **Decided** | 2026-10-01. The refusal by this ADR; **bringing imagery's publishing, styling and Map Viewer into Studio is `INFERRED`** from the owner's *"Başlayalım"*, said to a question that offered either the refusal alone or imagery taken into scope in order — listed as [Q-158](../open-questions.md) |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-043](ADR-043-imageserver-and-the-raster-face.md) (what an export reads), [v1-scope](../v1-scope.md) §3b (imagery's place) |

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
| — | The owner's *"Başlayalım"* takes imagery into scope in the order offered | `INFERRED` — [Q-158](../open-questions.md) |

## 8. Dependencies

**Depends on:** ADR-043 (ImageServer), ADR-044 (its tiles).

**Depended on by:** —

## 9. Revisit triggers

- The owner answers Q-158 otherwise.

## 10. Conditions

1. **An export refuses what it does not apply** — **DISCHARGED 2026-10-01**, `ImageServerConformanceTests`.
2. **A GeoTIFF or COG is uploaded and published from Studio.**
3. **An imagery layer's stretch and colours are set, and the default does not blacken 12-bit or whiten float data.**
4. **The Map Viewer adds and draws an imagery layer.**
5. **`identify` reads the point ArcGIS clients send.**
