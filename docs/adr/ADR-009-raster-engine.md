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

**Added 2026-08-12** — a default to argue against rather than toward
([research/postgis-thin-servers.md](../research/postgis-thin-servers.md) §3.4).
`VERIFY` TiTiler does dynamic raster tiling straight from COGs over HTTP range
requests — reading a few hundred kilobytes from a 50 GB file rather than the
file. **Dynamic tiling should therefore be the default and pre-generated raster
caches the exception needing justification**, which reverses the historical
assumption.

Note what TiTiler's model is, in our terms: GDAL in-process against
range-requested remote files. That is precisely the thread-affinity and
crash-containment problem of §5 in
[research/dependency-thread-safety.md](../research/dependency-thread-safety.md),
running in production at scale. How they handle worker isolation and failure is
worth studying before this ADR is decided.

Crash containment is the deciding axis here, not throughput. Malformed raster and
decompression bombs are explicit threats (§54, §59).

**Added 2026-08-12** — a second deciding axis, from
[research/dependency-thread-safety.md](../research/dependency-thread-safety.md)
§5: **GDAL datasets are thread-affine resources, not shareable cache entries.**
GDAL forbids concurrent calls on the same instance *or on instances related by
ownership* — two threads on two bands of one file, or two layers of one
GeoPackage, is unsafe. So the raster provider cannot simply cache an open
dataset per source. The options are per-source-per-thread caching, serialised
access behind a lock, or a checked-out dataset pool; each has a different memory
and latency profile, and this ADR must choose between them.

`VERIFY` GDAL 3.10 and later offer read-only raster thread safety
(`GDAL_OF_THREAD_SAFE`), which would remove the problem for the raster *read*
path at the cost of a hard minimum version (Q-24). Vector providers and every
write path are unaffected and still need one of the three options above.

This is the most likely site in the platform for a subtle, load-dependent
correctness bug: the naive design mostly works, and fails intermittently.

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
