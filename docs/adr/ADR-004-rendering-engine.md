# ADR-004 — Rendering Engine

| | |
|---|---|
| **Status** | `DEFERRED` — confirmed 2026-08-12, WMS is out of v1 |
| **Confidence** | — |
| **Decided** | — |

---

## 1. Context

**Rescoped 2026-08-12 by owner decision** — see
[product-context.md](../product-context.md), "Rendering posture".

The platform is vector-first. There is no server-side raster tile generation and
no server-side cartography in the core: the client renders, using MapLibre and
the vector tiles we serve. Label placement, decluttering and cross-tile label
consistency — the hard parts, and the reason this ADR looked large — are all
client-side concerns now.

**Confirmed deferred, 2026-08-12 (Q-47).** Adversarial review F7 found a
contradiction: this ADR was deferred while the compatibility layer — a product
requirement — needed WMS, which needs a rasteriser. A deferred ADR was a
precondition for a required deliverable.

**The owner resolved it by removing the requirement, not by un-deferring this
ADR. WMS is out of v1.**

The reasoning that makes this affordable: WMS costs either MapLibre Native with
its headless GPU-context problem in containers — which collides directly with
air-gapped deployment — or writing our own style interpreter and rasteriser,
which is exactly the Tier 1 cartographic work vector-first removed. Neither is
small, and neither is worth it for a compatibility surface.

**The cost is real and must be documented rather than discovered:** WMS-client
migration is unsupported at v1. Desktop GIS, older web applications and
third-party tools that speak only WMS cannot move to us initially, and much of
the GeoServer estate is consumed that way.

The owner noted that a rendered map service in the style of an ArcGIS MapService
may be worth building one day. That would reopen this ADR as a **product
capability** rather than as a compatibility adapter — a different and more
honest framing than rendering purely to support legacy clients.

*What follows is the narrow compatibility-rendering analysis, retained because it
is the shape the reopening would take.* See §2.

*(Historical framing, retained for context: server-side cartography per §34 —
symbols, labels, fonts, decluttering, compositing, rasterisation. This is what
the ADR would have covered had the platform been render-first.)*

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

  **This objection inverted on 2026-08-12.** Under the vector-first decision we
  adopt the MapLibre style spec deliberately as our style format. For the only
  remaining job — rendering *our own* vector tiles with *our own* MapLibre
  styles into WMS images — MapLibre Native's lack of neutrality stops being a
  liability and becomes the reason to choose it. It already speaks the language
  we chose. A neutral rasteriser like Skia would require us to build a MapLibre
  style interpreter to feed it, which is exactly the Tier 1 cartographic work
  the vector-first decision just removed.

  The headless-rendering and X-server caveat below still applies and is now the
  main argument against.
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
