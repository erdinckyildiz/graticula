# ADR-NNN — <Title>

| | |
|---|---|
| **Status** | `DRAFT` |
| **Confidence** | — |
| **Decided** | — |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

What problem forces a decision here? What breaks if we do not decide? Which
product requirement (see [product-context.md](../product-context.md)) does this
serve?

## 2. Alternatives considered

One subsection per alternative. Each must be stated in its strongest form — an
alternative described weakly has not been considered.

### Alternative A — <name>

**Argument for.**

**Argument against.**

## 3. Counterarguments to the preferred option

The case *against* what we are about to choose, written by someone trying to
win it. If this section is thin, the decision is not ready.

## 4. Evidence

Benchmarks, prototypes, measured numbers, cited public documentation. Link to
`/experiments` and `/benchmarks` artefacts.

If this section is empty and the question is measurable, the status must be
`REQUIRES BENCHMARK` or `REQUIRES PROTOTYPE` — not `ACCEPTED`.

| Claim | Evidence | Source |
|---|---|---|
| | | |

## 5. Decision

The decision, stated in one paragraph, unambiguously.

## 6. Consequences

**Positive.**

**Negative.** Every decision has costs. Listing none means they have not been
found yet.

**Ports created.** If this ADR adopts a Tier 2 dependency
([build-vs-adopt-policy.md](../build-vs-adopt-policy.md)), name the interface
that isolates it. Adoption without a named seam is an incomplete ADR.

## 7. Assumptions this decision rests on

Reference IDs from
[architecture-assumptions.md](../architecture-assumptions.md). If an assumption
listed here is invalidated, this ADR must be reviewed.

| ID | Assumption | Status |
|---|---|---|
| | | |

## 8. Dependencies

**Depends on** (upstream ADRs — if they change, this is reviewed):

**Depended on by** (downstream ADRs — if this changes, review these):

## 9. Revisit triggers

Concrete, observable conditions that reopen this decision. "If it turns out to
be slow" is not a trigger. "If p95 tile latency exceeds 150 ms at 1,000
services" is.

## 10. Dissent

Recorded disagreement that survived the debate. Do not manufacture consensus
(§8). Absence of dissent must mean nobody disagreed, not that disagreement was
edited out.
