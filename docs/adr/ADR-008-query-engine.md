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

**Updated 2026-08-12 — alternative 2 is now excluded, and Q-21 is answered.**

The owner made Oracle Spatial and SQL Server Spatial first-class alongside
PostGIS ([product-context.md](../product-context.md)). Three spatial dialects of
unequal capability, from day one. See
[research/multi-database-consequences.md](../research/multi-database-consequences.md)
§3.

- **The Query AST targets multiple dialects from day one** (Q-21: yes). An
  abstraction exercised by one implementation is not an abstraction; it is a
  wrapper that will not survive the second.
- **Capability negotiation is core, not a refinement.** PostGIS is substantially
  more capable than the other two. The engine may neither ship a
  lowest-common-denominator design that wastes PostGIS, nor a PostGIS-shaped one
  with fallbacks bolted on afterwards.
- **`ST_AsMVT` exists only in PostGIS.** For SQL Server and Oracle, in-process
  MVT encoding is the *only* path. Tile generation therefore cannot be modelled
  as "push down to the database, with a fallback" — the fallback is the majority
  case in enterprise deployments. This is the single most consequential fact to
  come out of the decision.
- **The compute-layer question (Q-19) becomes load-bearing.** When a provider
  cannot execute part of a plan, the work has to happen somewhere, and today
  that somewhere is code we have not written. With three providers of unequal
  capability this stops being an interesting option and becomes a gap that must
  be filled by something.
- **Editing semantics differ across the three** — isolation levels, locking, what
  a conflict looks like. This produces provider-dependent *bugs* rather than
  provider-dependent features, and §28's optimistic concurrency design must
  account for it.

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
