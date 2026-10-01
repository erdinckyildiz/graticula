# ADR-125 — An image service has a legend, key properties and statistics

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `MEDIUM` — the shapes are ArcGIS's documented ones; what Pro does with each is read from its requests, not from Pro |
| **Decided** | 2026-10-01, in [ADR-123](ADR-123-imagery-comes-into-studio.md)'s order — its scope is [Q-158](../open-questions.md), still `INFERRED` |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-043](ADR-043-imageserver-and-the-raster-face.md) (the operations the face serves) |

---

## 1. Context

The ArcGIS reviewer's second imagery pass, 2026-10-01: `legend`, `keyProperties` and `statistics` all answered *not an
operation this image service serves*. The JS SDK's Legend widget and Pro's contents pane ask for the legend, so the
ramp an owner chose in Studio's Display (ADR-123) appeared nowhere outside Studio; Pro's stretch dialog reads the
statistics; some clients ask for key properties before drawing.

## 2. Alternatives considered

### Alternative A — Serve the three from what the server already has (chosen)

The legend from the stored style — a single band as `Stretched`, its high and low values with the colours at the
ends of its ramp; three or more bands as `RGB Composite`. Key properties as `{}`, ArcGIS's own answer for an image with
no catalog. Statistics from the sample the default stretch is worked out from.

### Alternative B — Statistics computed over every pixel

**For:** exact. **Against:** a full read of a multi-gigabyte image on a request; the sampled figures are what the
stretch uses, so the two agree.

## 3. Counterarguments to the preferred option

- *Sampled statistics are not exact*, and `minValues` on the root says so less plainly than it might: a single
  pixel below the sampled minimum is clipped by a min–max stretch. Recorded rather than hidden; histograms remain
  unserved (`hasHistograms: false`).

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| A single-band image's legend is `Stretched` with its high and low values and swatches; key properties are `{}`; statistics carry the sampled range | `ImageryDisplayTests` | this repository |

## 5. Decision

`legend`, `keyProperties` and `statistics` are served on every image service, governed as its other operations are.
`supportsStatistics` is true.

## 6. Consequences

**Positive.** A client's legend shows the ramp the owner chose; Pro's stretch has numbers to start from.

**Negative.** None known.

**State.** None.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | Imagery is in scope | `INFERRED` — [Q-158](../open-questions.md) |

## 8. Dependencies

**Depends on:** ADR-043, ADR-123.

**Depended on by:** —

## 9. Revisit triggers

- A client asks for `histograms` and refuses to draw without them.
