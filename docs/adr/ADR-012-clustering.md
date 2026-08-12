# ADR-012 — Clustering

| | |
|---|---|
| **Status** | `DEFERRED` |
| **Confidence** | — |
| **Decided** | — |

---

## 1. Context

Multi-node deployment (§52): shared state, coordination, request routing across nodes, rolling upgrades, split-brain behaviour. Deliberately deferred — §79 permits distributed infrastructure only once benchmarks and operational requirements demonstrate necessity, and the platform must work correctly on one node first. It is recorded now so that single-node decisions do not quietly make clustering impossible later.

## 2. Alternatives to evaluate

To be enumerated when this is reopened.

What this ADR requires now, while deferred: every other ADR must state which of
its state is node-local and which is shared. That inventory is the real
precondition for clustering, and collecting it late is expensive.

**First instalment delivered 2026-08-12** —
[ADR-002](ADR-002-primary-data-architecture.md) §5 inventories platform state.
Two findings matter here:

- Platform durable state is in PostgreSQL, so **clustering needs no new
  synchronisation mechanism for it.** The GeoServer Cloud problem — a message bus
  introduced purely to keep a file-based catalog consistent across nodes — does
  not arise for us.
- **Cache bytes are the awkward case.** The cache index is shared; the contents
  (tiles, glyphs, sprites) are node-local unless placed on shared storage. A
  multi-node deployment must share storage, replicate, or accept that a tile
  cached on one node is a miss on another. This is the main unresolved item this
  ADR inherits, and it belongs jointly with ADR-010.

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

**Depends on:** ADR-002, ADR-007, ADR-010, ADR-011

**Depended on by:** Deployment profiles (§53), high availability (§52)

## 9. Revisit triggers

To be defined with the decision. Triggers must be observable conditions, not
vague misgivings.

## 10. Dissent

To be recorded during the debate round.

---

*Structure follows [_template.md](_template.md). Expand every section before
moving this out of `DRAFT`.*
