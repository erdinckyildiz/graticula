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

**Added 2026-08-12** —
[research/rendering-engines.md](../research/rendering-engines.md).

- **MapLibre Native is not a rasterisation backend.** It is a complete renderer
  carrying its own styling model, so adopting it means adopting the MapLibre
  style specification as our cartographic model — a Tier 1 decision wearing
  Tier 2 clothing. Supporting the MapLibre style spec as *a style format* is a
  different and attractive proposition (Q-25); adopting its engine as our
  cartographic layer is not.
- `VERIFY` Operational warning: MapLibre Native headless rendering in Docker or
  on a remote server reportedly needs X server simulation, with a documented
  open issue on headless context management. A GPU-context dependency is a real
  liability against the 2 AM test (§7).
- **This ADR should not be decided yet, and not from that note.** Rendering is
  deferrable — features first, then tiles — and public comparative evidence for
  *server-side* rendering is thin. Defer until the tile pipeline exists, then
  measure on our own workload via `benchmarks/rendering/`.
- The hard part is not the backend. Label placement, decluttering and
  **cross-tile label consistency** are Tier 1, unadoptable, and where
  server-side cartography is actually won or lost.

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
