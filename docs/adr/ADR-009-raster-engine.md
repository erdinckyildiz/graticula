# ADR-009 — Raster Engine

| | |
|---|---|
| **Status** | `DRAFT` |
| **Confidence** | — |
| **Decided** | — |

---

## 1. Context

Raster and imagery as a first-class subsystem (§35): GDAL, COG, STAC, overviews, mosaics, range requests against object storage, reprojection, dynamic imagery and raster functions. Two properties make raster architecturally distinctive: it is native code processing untrusted files, and its working set is measured in terabytes.

## 2. Alternatives to evaluate

1. GDAL in-process
2. GDAL in a dedicated worker class, isolated by process
3. Mixed — in-process for trusted registered sources, isolated for user-supplied files

Crash containment is the deciding axis here, not throughput. Malformed raster and
decompression bombs are explicit threats (§54, §59).

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

**Depended on by:** ADR-010, ADR-011, ADR-012

## 9. Revisit triggers

To be defined with the decision. Triggers must be observable conditions, not
vague misgivings.

## 10. Dissent

To be recorded during the debate round.

---

*Structure follows [_template.md](_template.md). Expand every section before
moving this out of `DRAFT`.*
