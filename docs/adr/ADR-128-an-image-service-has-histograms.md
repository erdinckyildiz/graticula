# ADR-128 — An image service has histograms, and NaN is not a measurement

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `MEDIUM` — the shape is ArcGIS's documented one; what Pro draws from it is not measured here |
| **Decided** | 2026-10-01, in [ADR-123](ADR-123-imagery-comes-into-studio.md)'s order — its scope is [Q-158](../open-questions.md), confirmed by the owner 2026-10-01 |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-125](ADR-125-an-image-service-has-a-legend-and-statistics.md) (`hasHistograms`, and what statistics leave out) |

---

## 1. Context

ADR-125 served statistics and left `histograms` refused, with `hasHistograms: false`. ArcGIS Pro's stretch dialog draws
a band's histogram and its percent-clip and standard-deviation stretches are computed from it.

Writing the histogram found a defect in the statistics underneath it: a float image whose absent pixels are NaN —
the usual way a float model marks a hole, and one no declared no-data value catches, since NaN equals nothing —
fed NaN into the minimum, maximum and mean. The service document carries those as `minValues` and `maxValues`, and
JSON has no NaN, so such an image's service document could not be written at all.

## 2. Alternatives considered

### Alternative A — Histograms from the statistics' own sample, NaN left out of both (chosen)

256 bins over the sampled range; an 8-bit band's bins are its 256 values (−0.5 to 255.5, as ArcGIS bins them).

### Alternative B — Histograms over every pixel

**Against:** the same as ADR-125's statistics — a full read on a request — and a histogram that disagreed with the
statistics beside it.

## 3. Counterarguments to the preferred option

- A sample can miss a rare value, so a percent-clip from it is approximate. So is ArcGIS's own on an approximate
  statistics pass; stated.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| A float model with a NaN hole answers statistics and a 256-bin histogram counting every measured pixel and not the hole | `ImageryDisplayTests` | this repository |

## 5. Decision

`histograms` is served, from the sample `statistics` reads; `hasHistograms` is true. NaN is left out of both, as a
declared no-data value is.

## 6. Consequences

**Positive.** Pro's stretch has a histogram; a float model with NaN holes has a service document.

**Negative.** None known.

**State.** None.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | Imagery is in scope | Confirmed by the owner 2026-10-01 — [Q-158](../open-questions.md) |

## 8. Dependencies

**Depends on:** ADR-125.

**Depended on by:** —

## 9. Revisit triggers

- A client is measured asking `computeHistograms` over an area and refusing a server without it.
