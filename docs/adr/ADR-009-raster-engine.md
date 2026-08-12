# ADR-009 — Raster Engine

| | |
|---|---|
| **Status** | `DRAFT` |
| **Confidence** | — |
| **Decided** | — |

---

## 1. Context

**Rescoped 2026-08-12 by owner decision** — see
[product-context.md](../product-context.md), "Rendering posture".

**The server does not produce pixels.** Raster and imagery are catalogued,
validated and access-controlled; the client fetches COG over HTTP range requests
and renders. This removes dynamic tiling, mosaicking on read, raster functions
and server-side reprojection of imagery from scope.

What remains:

- **Catalog and metadata** — STAC, extents, CRS, band descriptions, overview
  structure. Read once at registration, not per request.
- **Validation at registration** — is this actually a valid COG with usable
  overviews? This is where GDAL is still needed, and it is the natural place to
  reject malformed input and decompression bombs (§54).
- **Access control and delivery** — the hard part; see §1a.
- **Possibly overview generation** as a registration-time or job-time service
  for uploaded imagery that is not already cloud-optimised.

## 1a. The delivery problem this creates

If the client fetches a COG directly from object storage, **per-layer
authorization stops working.** Our primary user is a GIS administrator (§41,
§42) for whom controlling who sees which layer is a requirement, not a feature.
A public bucket URL enforces nothing.

Three options, and this ADR must choose:

1. **Signed, expiring URLs.** Cheap, bytes never touch us. Weaknesses: expiry
   windows are a blunt instrument, revocation is not immediate, per-feature or
   row-level rules cannot be expressed, and it requires object storage that
   supports signing — which an air-gapped filesystem deployment may not.
2. **Range-request proxy.** We forward the range requests and enforce
   authorization per request. Authorization is exact and immediate; the cost is
   that imagery bandwidth flows through the server, which the vector-first
   decision had just removed. Terabyte working sets make this non-trivial.
3. **Hybrid** — proxy by default, signed URLs for layers explicitly marked
   public.

Recorded as Q-27. It is now the most consequential open question in this ADR,
and it is a security and operations question rather than a raster one.

*(Historical framing: raster as a first-class rendering subsystem per §35 —
dynamic imagery, mosaics, raster functions. Out of scope under the vector-first
decision.)*

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

**Largely defused by the vector-first decision (2026-08-12).** GDAL now runs at
registration and validation time, not per request, so the concurrency pressure
that made the following a live hazard mostly disappears. It is retained because
registration and overview generation still touch GDAL, and file-based *vector*
providers touch it constantly — where the same rule applies with none of the
relief.

**Original note** — a second deciding axis, from
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
