# ADR-011 — Job System

| | |
|---|---|
| **Status** | `DRAFT` |
| **Confidence** | — |
| **Decided** | — |

---

## 1. Context

Asynchronous execution for long-running work — geoprocessing (§36), cache seeding, publishing, imports. The hard requirement is that long operations never block request workers. OGC API Processes defines the external contract; the internal execution model is open.

## 2. Alternatives to evaluate

1. In-database job queue (PostgreSQL SKIP LOCKED)
2. In-process scheduler with database-persisted state
3. External broker (Kafka, RabbitMQ, NATS)

Option 3 is on the §82 challenge list and needs a concrete justification to
survive, given that the baseline deployment already has PostgreSQL.

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

**Depends on:** ADR-002, ADR-007

**Depended on by:** ADR-012, geoprocessing (§36), admin API (§39)

## 9. Revisit triggers

To be defined with the decision. Triggers must be observable conditions, not
vague misgivings.

## 10. Dissent

To be recorded during the debate round.

---

*Structure follows [_template.md](_template.md). Expand every section before
moving this out of `DRAFT`.*
