# ADR-006 — Plugin Model

| | |
|---|---|
| **Status** | `DRAFT` |
| **Confidence** | — |
| **Decided** | — |

---

## 1. Context

Versioned extension contracts for data providers, geoprocessing operations, renderers and authentication backends (§43). Two questions dominate: what the contract looks like, and what happens when a plugin misbehaves — leaks memory, blocks, crashes, or is hostile (§54, §59).

## 2. Alternatives to evaluate

1. In-process plugins, same runtime, no isolation
2. In-process with resource accounting and quotas
3. Out-of-process plugins over a local protocol
4. WASM sandbox
5. No third-party plugins initially — internal extension points only

Option 5 deserves genuine consideration under §82. A plugin system nobody has
asked for yet is a large permanent cost.

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

**Depends on:** ADR-001, ADR-007

**Depended on by:** Provider architecture (§27), ADR-011

## 9. Revisit triggers

To be defined with the decision. Triggers must be observable conditions, not
vague misgivings.

## 10. Dissent

To be recorded during the debate round.

---

*Structure follows [_template.md](_template.md). Expand every section before
moving this out of `DRAFT`.*
