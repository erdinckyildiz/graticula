# ADR-004 — Rendering Engine

| | |
|---|---|
| **Status** | `DRAFT` |
| **Confidence** | — |
| **Decided** | — |

---

## 1. Context

Server-side cartography (§34): symbols, labels, fonts, decluttering, layer compositing, output rasterisation. Distinct from tile generation — this produces rendered map images (WMS, OGC API Maps). The cartographic logic is Tier 1 and ours; the rasterisation backend is Tier 2.

## 2. Alternatives to evaluate

1. Skia
2. Cairo
3. MapLibre Native (server-side)
4. AGG-style or language-native rasteriser

Note the layering: whichever backend wins, style evaluation, label placement and
decluttering remain ours. Adopting a backend must not mean adopting its styling
model.

## 3. Counterarguments to the preferred option

Not yet written — no option is preferred yet.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| | | |

## 5. Decision

Pending.

## 6. Consequences

Pending. If this ADR adopts a Tier 2 dependency, it must name the port
interface that isolates it — see
[build-vs-adopt-policy.md](../build-vs-adopt-policy.md).

## 7. Assumptions

To be registered in
[architecture-assumptions.md](../architecture-assumptions.md).

## 8. Dependencies

**Depends on:** ADR-001, ADR-003

**Depended on by:** Tile pipeline, ADR-010 (render caching)

## 9. Revisit triggers

To be defined with the decision. Triggers must be observable conditions, not
vague misgivings.

## 10. Dissent

To be recorded during the debate round.

---

*Structure follows [_template.md](_template.md). Expand every section before
moving this out of `DRAFT`.*
