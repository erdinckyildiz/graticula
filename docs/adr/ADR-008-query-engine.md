# ADR-008 — Query Engine

| | |
|---|---|
| **Status** | `DRAFT` |
| **Confidence** | — |
| **Decided** | — |

---

## 1. Context

A Query AST and spatial query planner (§29), with parameterised execution and capability-aware providers (§27). Untrusted input must never reach SQL by concatenation. The planner decides what executes in the database and what executes in the server (§30) — pushing everything down binds us to one provider, pushing nothing down wastes the database.

## 2. Alternatives to evaluate

1. Query AST compiled per provider, with capability negotiation at plan time
2. Direct provider-specific query construction, no shared AST
3. Adopt an existing query or expression framework

Must also cover: CQL2 and OGC filter mapping, result streaming for millions of
features (§47), pagination and cursor stability, statistics and aggregation, and
result-size governance (§49).

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

**Depends on:** ADR-001, ADR-002, ADR-003

**Depended on by:** ADR-005, provider architecture (§27), feature services (§28)

## 9. Revisit triggers

To be defined with the decision. Triggers must be observable conditions, not
vague misgivings.

## 10. Dissent

To be recorded during the debate round.

---

*Structure follows [_template.md](_template.md). Expand every section before
moving this out of `DRAFT`.*
