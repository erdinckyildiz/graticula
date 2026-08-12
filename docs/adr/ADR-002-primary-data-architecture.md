# ADR-002 — Primary Data Architecture

| | |
|---|---|
| **Status** | `DRAFT` |
| **Confidence** | — |
| **Decided** | — |

---

## 1. Context

How the platform stores its own state — service definitions, catalog, users, roles, jobs, cache metadata — and how that relates to the spatial data it serves. Sections 80.29 and 80.30 demand an explicit split between persistent and ephemeral state. The baseline deployment is a single PostgreSQL/PostGIS instance (§2); whether platform metadata shares that database, uses a separate one, or lives in files is the decision.

## 2. Alternatives to evaluate

1. Platform metadata in the same PostgreSQL instance as spatial data
2. Platform metadata in a dedicated database
3. File-based configuration, database-free (git-ops style)
4. Embedded store (SQLite) for single-node, database for clustered

Configuration-as-files is attractive for air-gapped and reproducible deployments
and hostile to a runtime administrative API (§39). That tension is the crux.

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

**Depends on:** ADR-001

**Depended on by:** ADR-007, ADR-011, ADR-012, publishing architecture (§38), admin API (§39)

## 9. Revisit triggers

To be defined with the decision. Triggers must be observable conditions, not
vague misgivings.

## 10. Dissent

To be recorded during the debate round.

---

*Structure follows [_template.md](_template.md). Expand every section before
moving this out of `DRAFT`.*
