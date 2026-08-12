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

**Added 2026-08-12** — the thin servers show this trade-off live
([research/postgis-thin-servers.md](../research/postgis-thin-servers.md) §3.2).
`VERIFY` Martin and pg_tileserv generate tiles on the fly with `ST_AsMVT` and
are reported fastest; Tegola encodes server-side, is slower, and in exchange
offers filesystem caching with pluggable backends **and cache seeding**. Each
picks one strategy.

We cannot pick one. A managed platform with an administrator, editable data and
1,000 services needs fast dynamic generation *and* seeding, invalidation and
cache lifecycle. That combination is one of the few defensible answers to Q-18 —
it is something neither a thin server nor static publishing provides — which
also means it has to actually work, not merely be listed.

L1 lifetime is coupled to worker lifetime (ADR-007): aggressive worker recycling
destroys L1 value, so the two decisions must be made together rather than
sequentially.

**Updated 2026-08-12** — the coupling is tighter than that.
[research/runtime-models-compared.md](../research/runtime-models-compared.md) §3
shows that L1 design and *request routing* are the same problem: warm state
fragments across workers, and if the router is blind to which worker holds what,
L1 hit rate collapses. QGIS Server documents exactly this. L1 cannot be designed
independently of ADR-007's routing decision.

A concrete starting inventory for what L1 holds, taken from GeoServer's
documented cache: store connections, feature type definitions, external
graphics, font definitions, CRS definitions. Notably **not** data. If that list
is broadly right, per-service warm state is small enough that binding and
unbinding it may be cheap — which would change the whole shared-versus-dedicated
calculation.

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
