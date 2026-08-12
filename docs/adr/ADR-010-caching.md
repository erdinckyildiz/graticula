# ADR-010 — Caching

| | |
|---|---|
| **Status** | `DRAFT` |
| **Confidence** | — |
| **Decided** | — |

---

## 1. Context

Three candidate layers (§44): L1 process memory, L2 distributed cache, L3 disk or object storage. The binding constraint is that Redis must not be mandatory for small installations. Cache invalidation (§80.24) is the harder half of this ADR and must not be deferred to a later one.

## 2. Alternatives to evaluate

1. L1 plus L3 only; L2 optional and off by default
2. L1 plus L2 mandatory
3. No L1; a single shared cache tier

L1 lifetime is coupled to worker lifetime (ADR-007): aggressive worker recycling
destroys L1 value, so the two decisions must be made together rather than
sequentially.

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

**Depends on:** ADR-007

**Depended on by:** Tile pipeline, ADR-004, ADR-009, ADR-012

## 9. Revisit triggers

To be defined with the decision. Triggers must be observable conditions, not
vague misgivings.

## 10. Dissent

To be recorded during the debate round.

---

*Structure follows [_template.md](_template.md). Expand every section before
moving this out of `DRAFT`.*
