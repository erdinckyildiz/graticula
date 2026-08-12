# Architecture Assumption Register

Every assumption an architectural decision rests on is recorded here (§11).

**Statuses:** `UNVALIDATED` · `VALIDATING` · `VALIDATED` · `INVALIDATED` · `SUPERSEDED`

**The rule that gives this register teeth:** invalidating an assumption triggers
review of every ADR listed in its *Depended on by* column. An assumption that no
ADR depends on is either mislabelled or unnecessary.

---

## Open assumptions

| ID | Assumption | Status | How it gets validated | Depended on by |
|---|---|---|---|---|
| A-001 | The tile and render paths are CPU-bound enough that language performance materially affects capacity | `UNVALIDATED` | `experiments/lang-slice` benchmark | ADR-001 |
| A-002 | Single-binary distribution is genuinely valuable for air-gapped installs, not just aesthetically pleasing | `UNVALIDATED` | Ask the owner; check real air-gapped install constraints | ADR-001 |
| A-003 | Most published services are idle most of the time, making shared workers viable | `UNVALIDATED` | Workload modelling; any real deployment telemetry we can obtain | ADR-007, ADR-010 |
| A-004 | Hot-path geometry overhead (allocation and/or FFI) is material enough to justify our own primitives | `UNVALIDATED` | `benchmarks/geometry-hotpath` | ADR-003, tile pipeline |
| A-005 | Geometry running in the same runtime meaningfully reduces defect resolution time versus FFI | `UNVALIDATED` | Judgement plus prototype experience; record honestly in `experiments/lang-slice` | ADR-001, build-vs-adopt policy |
| A-006 | One internal geometry representation can serve both the feature path and the tile path without a second conversion | `UNVALIDATED` | Prototype | ADR-003 |
| A-007 | Crash containment is required in practice, not merely in principle — workers really do die (GDAL on malformed input, plugins, OOM) | `UNVALIDATED` | Failure scenario review (§59); fault injection | ADR-007, ADR-009, ADR-006 |
| A-008 | Administrators will not correctly hand-tune per-service worker settings, so defaults must be good and adaptive | `UNVALIDATED` | Operational review (§7 role); prior-art behaviour of ArcGIS Server instance tuning | ADR-007 |
| A-009 | PostgreSQL/PostGIS is an acceptable hard dependency for the baseline deployment | `UNVALIDATED` | Owner decision plus deployment review | ADR-002, ADR-011 |
| A-010 | The 100–1,000 service target will not shift upward by an order of magnitude after launch | `UNVALIDATED` | Owner confirmation; revisit at each phase gate | ADR-007, ADR-012 |

A-003 and A-007 carry the most weight. A-003 is the load-bearing assumption
under the entire shared-worker model. A-007 decides whether process isolation is
a real requirement or a reflex inherited from ArcSOC-era thinking — and getting
that wrong in either direction is expensive.

## Validated

*(none yet)*

## Invalidated / superseded

*(none yet)*

---

## Recording rules

1. An assumption enters this register the moment an ADR relies on it — not later.
2. Every row names a concrete validation method. "We will see" is not a method.
3. Status changes are made here first, then propagated to the dependent ADRs.
4. Do not delete invalidated assumptions. Move them down and record what
   replaced them; the history is how we avoid rediscovering the same mistake.
