# ADR-138 — A renderer sent as a rendering rule is drawn

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `HIGH` — every chain is the one the SDK was measured sending, and each is drawn where it was blank |
| **Decided** | 2026-10-02 by owner decision (*"Devam et"*, the imagery work going on; the gap was measured in [ADR-137](ADR-137-an-image-service-answers-lerc.md) §6 and named there as the next decision) |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-123](ADR-123-imagery-comes-into-studio.md) (§5.1: `Stretch`, `Colormap` and `Remap` are no longer refused), [ADR-137](ADR-137-an-image-service-answers-lerc.md) (its negative consequence) |


> **Amended 2026-10-03 by [ADR-151](ADR-151-ndvi-band-arithmetic-and-a-choice-of-bands.md).** A rule may be laid over a raster function — Stretch over NDVI — and draws the function, stretched by its statistics.

---

## 1. Context

ADR-137's probe set renderers on the ArcGIS Maps SDK for JavaScript 4.31's `ImageryLayer` and recorded what it sent.
A renderer on a layer drawn on the server — the default, `format=jpgpng` — is not drawn by the SDK; it is sent as a
`renderingRule` chain, and this server refused every one, so the map was blank:

| Renderer | Sent as |
|---|---|
| stretch, no ramp | `Stretch` |
| stretch with a colour ramp | `Colormap(Stretch)` with a `colorRamp` |
| class breaks | `Colormap(Remap)` with a `Colormap` of values |

`Stretch` carried `StretchType` 0, 3, 4, 5 or 6, `DRA`, `UseGamma` and `Gamma`, `NumberOfStandardDeviations`,
`MinPercent`/`MaxPercent` and, for a custom range, `Statistics`. A ramp was `algorithmic` (CIELab or HSV) or
`multipart`. A colour-map and a unique-value renderer were not sent at all, and a shaded relief renderer is drawn on the
client from LERC (ADR-137).

## 2. Alternatives considered

### Alternative A — Read the chains the SDK sends, and draw them (chosen)

A display rule (`DisplayRule`) is read when the outermost function is `Stretch`, `Colormap` or `Remap`; any other is
left to the raster functions of ADR-136.

- **Stretch** spreads each band to 0–255: none (the values as they are), minimum–maximum, standard deviations either
  side of the mean, percent clip, histogram equalization, then a gamma when asked. The statistics are the service's
  own — the sample `statistics` and `histograms` describe it by, so adjacent tiles agree — or the rule's `Statistics`,
  or with `DRA` the drawn window's own, as ArcGIS does. A colour image is stretched band by band.
- **Colormap over a stretch** lays its ramp over the stretched levels: algorithmic ramps in CIELab, LCh or HSV,
  multipart ramps as equal parts.
- **Colormap over Remap**, or over the values, colours each class or value; a value no range or entry names, and a
  `NoDataRanges` value, is clear.

Anything else in the chain is refused by name: another function inside it, a stretch type, a ramp type or algorithm
this server does not draw, `ComputeGamma`, an output range other than 0–255, `AllowUnmatched`, and a colour map on a
colour image. A display rule chooses a picture's colours, so it is refused with `format=tiff` or `lerc`.

### Alternative B — Draw the service's own style and ignore the rule

**Against:** ADR-123's principle — a client is never answered with a picture it did not ask for and no word that it
was not.

### Alternative C — Tell clients to render on the client

**Against:** it is the SDK's choice, not the user's: a renderer set in a web app or Map Viewer goes to the server
unless the layer was made to ask LERC.

## 3. Counterarguments to the preferred option

- *The statistics are a sample's*, the coarsest overview at most 512 pixels a side, read again for each picture whose
  rule needs them. That is what `statistics` already answers; storing them is a decision not yet taken — the note on `MeasureAsync` says why it belongs in a change of its own.
- *HSV is interpolated straight from one hue to the other.* What ArcGIS does at the wrap is not published.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| The chains in §1, verbatim; drawn blank while refused | the 4.31 probe, twelve renderers | local run, 2026-10-02 |
| After: minimum–maximum, standard deviation, percent clip, none, DRA, a custom range, class breaks fill the frame; gamma and histogram equalization draw the image (38,464 and 38,790 of 40,000 pixels — its own no-data is clear); shaded relief draws from LERC | the same probe | local run, 2026-10-02 |
| Every chain the SDK sends is read; others are refused by name; each stretch, ramp, gamma, class and band-by-band colour lands where it should | `DisplayRuleTests` (29) | this repository |
| A ramp over the stretched heights runs blue to red; two classes are coloured by their ranges; a rule with `format=tiff` is refused | `ImageryDisplayTests` | this repository |

## 5. Decision

`exportImage` draws a `renderingRule` of `Stretch`, `Colormap` over `Stretch` with a colour ramp, and `Colormap` over
`Remap` or over the values, as §2 describes; other chains are refused by name.

## 6. Consequences

**Positive.** A renderer set on an imagery layer in an ArcGIS web app is drawn by this server.

**Negative.** None known.

**State.** None.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | The imagery work goes on, after LERC | Stated by the owner, 2026-10-02 |

## 8. Dependencies

**Depends on:** ADR-123, ADR-125, ADR-128, ADR-136, ADR-137.

**Depended on by:** —

## 9. Revisit triggers

- A client measured sending a chain this refuses — a band extraction under a stretch, a colour map on an RGB image.
- Statistics stored with the coverage.
