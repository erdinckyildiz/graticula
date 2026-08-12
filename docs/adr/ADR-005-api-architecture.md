# ADR-005 — API Architecture

| | |
|---|---|
| **Status** | `DRAFT` |
| **Confidence** | — |
| **Decided** | — |

---

## 1. Context

How OGC API Features/Tiles/Maps/Processes, WMS/WMTS/WFS and any compatibility surface map onto the internal domain. Section 8 states the governing rule: external protocols must not dictate internal domain architecture. The risk here is the reverse of the usual one — OGC specifications are detailed enough to leak into the core if the seam is not deliberate.

## 2. Alternatives to evaluate

1. Protocol adapters over a protocol-neutral internal service interface
2. Native OGC API core with legacy protocols as adapters
3. Internal model shaped directly by OGC resource semantics

Also to decide: versioning strategy, content negotiation, error model, and where
the compatibility layer (§51) sits relative to the core.

**Updated 2026-08-12 — the compatibility layer is now a requirement.** Displacing
existing ArcGIS Server and GeoServer deployments is a confirmed product goal
(see [product-context.md](../product-context.md)), which promotes §51 from
optional investigation to deliverable. This raises the stakes on option 1: with a
required compatibility surface, an internal model shaped by any external protocol
(option 3) becomes actively dangerous. Which surface to target is Q-17 and is
still open.

Clean room applies with full force here (§5): published protocol behaviour only.

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

**Depends on:** ADR-007, ADR-008

**Depended on by:** ADR-006, compatibility layer (§51), admin API (§39)

## 9. Revisit triggers

To be defined with the decision. Triggers must be observable conditions, not
vague misgivings.

## 10. Dissent

To be recorded during the debate round.

---

*Structure follows [_template.md](_template.md). Expand every section before
moving this out of `DRAFT`.*
