# Architecture Completeness Matrix

Tracks how far each architectural area has actually been taken (§64). The point
is to make gaps visible — an area with a decision but no failure review is not
finished, however confident the ADR sounds.

**Legend:** `—` not started · `WIP` in progress · `✓` complete · `n/a` not applicable

---

| Area | Decision | ADR | Prototype | Benchmark | Security review | Ops review | Failure review | Status |
|---|---|---|---|---|---|---|---|---|
| Core language | narrowed to Go vs .NET | DRAFT | specified | — | — | — | — | `REQUIRES PROTOTYPE` — paper round done, prototype spec written |
| Primary data architecture | **decided, amended twice** | `ACCEPTED WITH CONDITIONS` | — | — | — | — | — | Platform store is SQLite and PostgreSQL only (Q-51 narrowed it from four engines). SQL Server and Oracle remain providers and datastores. Conditions in ADR-002 §9. State inventory delivered for ADR-012. |
| Provider architecture — multi-dialect | — | — | — | — | — | — | — | **elevated**: three first-class spatial dialects. `ST_AsMVT` is PostGIS-only, so in-process MVT encoding is mandatory |
| Hosted datastore (optional) | **model confirmed** | — | — | — | — | — | — | storage model in [data-model.md](data-model.md); Q-32 now only decides v1 phasing |
| Data model — storage and layer modes | **decided** | n/a | — | — | — | — | — | four lifecycles separated; datastore is a provider we own, on any of three spatial engines; editing is out of our API |
| Connection discipline / quiesce | — | — | — | — | — | — | — | new obligation: we must not block a DBA's DDL. Lands on ADR-007 §5b and the admin API |
| Schema drift detection | — | — | — | — | — | — | — | improvement over ArcGIS, which requires manual restart. A-023 |
| Geometry engine | — | DRAFT | — | — | — | — | — | blocked on ADR-001 |
| Rendering engine | rescoped | DEFERRED | n/a | — | — | — | — | `DEFERRED` — vector-first; only WMS-in-compatibility-layer remains |
| API architecture | **decided** | `ACCEPTED WITH CONDITIONS` | — | — | — | — | — | OGC API Features 1+2+3 native; legacy protocols in the compatibility layer; capability report generated not hand-written |
| Plugin model | **decided** | `ACCEPTED WITH CONDITIONS` | n/a | n/a | — | — | — | internal extension points only; no third-party plugin system until a specific trigger fires |
| Service runtime | **decided** | `ACCEPTED WITH CONDITIONS` | — | — | — | — | — | the ArcSOC question answered. Structure decided, numbers are conditions. Affinity routing (A-014) is the unproven part. Amended with context pinning (§4.12) as the dedicated-instance equivalent |
| Query engine | **decided** | `ACCEPTED WITH CONDITIONS` | — | — | — | — | — | structure decided, numbers not. Conditions in ADR-008 §10 |
| Raster engine | **decided** | `ACCEPTED WITH CONDITIONS` | — | — | — | — | — | serve COG only, convert at registration; GDAL on job workers; delivery proxied by default (Q-27 answered) |
| Caching | **decided** | `ACCEPTED WITH CONDITIONS` | — | — | — | — | — | tiles are the real cache; L2 optional permanently; coherence is best-effort for registered data and documented as such |
| Job system | **decided** | `ACCEPTED WITH CONDITIONS` | — | — | — | — | — | queue in the platform store, no broker; job classes with reserved capacity; every job declares its re-run behaviour |
| Clustering | deferred | DEFERRED | n/a | n/a | — | — | — | `DEFERRED` (§79) |
| Build vs adopt policy | ✓ | n/a | n/a | n/a | — | — | — | ACTIVE |
| Provider architecture (§27) | scope under revision | — | — | — | — | — | — | Q-52 splits serving providers from import sources; §27's list wrongly includes interchange formats as providers. Q-53 recommends against warehouses. |
| Feature services (§28) | — | — | — | — | — | — | — | read **and write**. A-026 asks whether OGC API Features plus additive extensions covers §28; A-027 covers concurrency against writes that bypass us |
| Vector tiles (§33) | — | — | — | — | — | — | — | not started — **elevated**: the only tile format, and the source for WMS compatibility |
| Glyph & sprite serving | — | — | — | — | — | — | — | not started — new requirement from the vector-first decision; must work air-gapped |
| Style document management | — | — | — | — | — | — | — | not started — storage and serving only, no evaluation |
| Publishing (§38) | — | — | — | — | — | — | — | not started — owns runtime schema evolution (publishing with a smaller blast radius) and registration, which runs as an interactive-class job |
| Admin API (§39) | — | — | — | — | — | — | — | not started — **elevated**: primary user is the GIS administrator |
| Compatibility layer (§51) | **scope set** | — | — | — | — | — | — | v1 is **WFS + WMTS-for-vector-tiles + full ArcGIS FeatureServer including edits** (Q-17). WMS, MapServer, ImageServer out (Q-47). Outside the core domain. |
| Feature service data model gaps | — | — | — | — | — | — | — | **opened by Q-17**: stable `objectId` (Q-57), attachments, relationships, domains, subtypes, editor tracking (Q-58) |
| Migration tooling | **scope set** | — | — | — | — | — | — | inventory plus definition import, free (Q-16). Needs a home — probably its own component rather than part of the compatibility layer |
| AuthZ (§42) | **partial** | n/a | — | — | — | — | — | model decided in [security.md](security.md) §2–3. Multi-tenant resource isolation still open (D-04) |
| AuthN (§41) | — | — | — | — | — | — | — | not started — local accounts, JWT, OAuth 2.0, OIDC |
| Observability (§46) | — | — | — | — | — | — | — | not started |
| Backpressure (§48) | — | — | — | — | — | — | — | shape set by ADR-007 §4.9. Fairness still depends on N4's per-source limit and D-04's per-tenant limits, neither of which exists |
| **Runtime supervisor (§21)** | — | **none** | — | — | — | — | — | **severe gap** — ADR-007 depends on it and it has no design (Q-54) |
| **TLS / certificates** | — | **none** | — | — | — | — | — | **severe gap** — absent from the whole architecture (Q-55) |
| Failure scenarios (§59) | **walked** | n/a | — | — | — | — | ✓ | [failure-scenarios.md](failure-scenarios.md) |
| Resource governance (§49) | — | — | — | — | — | — | — | not started |
| Deployment profiles (§53) | — | — | — | — | — | — | — | not started |
| Licensing (§55) | WIP | n/a | n/a | n/a | — | — | n/a | see [DEPENDENCY-LICENSES.md](../DEPENDENCY-LICENSES.md) |
| Competitive position | — | — | — | — | — | — | — | **gap.** Q-49 unanswered; a direct peer now exists ([research/honua-server.md](research/honua-server.md)) |

## Adversarial reviews (§85, §67)

| Round | Date | Findings | Status |
|---|---|---|---|
| 1 — adversarial | 2026-08-12 | 12 (3 severe) | [adversarial-review-1.md](reviews/adversarial-review-1.md) — all dispositions applied |
| 2 — fresh challenger (§67) | 2026-08-12 | 8 (3 severe) | [fresh-challenger-review-2.md](reviews/fresh-challenger-review-2.md) — **written by the same agent, which §67 forbids.** Findings are real; coverage is suspect. A genuine independent review is still owed. |
| 3 — genuinely independent | — | — | **Still owed.** §67 requires a reviewer who did not participate. Rounds 1 and 2 were both self-review. |

## Review gates (§66)

None run yet. Each gate is run against the whole architecture, not per-ADR, and
a failure reopens the relevant decisions rather than being noted and passed over.

| Gate | Run | Result |
|---|---|---|
| Correctness | — | — |
| Simplicity | — | — |
| Performance | — | — |
| Failure | — | — |
| Operations | — | — |
| Security | — | — |
| Extensibility | — | — |
| Licensing | — | — |
| Consistency | — | — |

## Phase 0 exit criteria (§81)

- [x] `architecture-assessment.md` complete — all 27 required sections (§70).
      **First complete draft, 2026-08-12. Not yet reviewed.**
- [x] Initial ADRs written, none still `DRAFT` without a stated reason.
      ADR-003 is the only remaining `DRAFT` and its reason is stated: it is
      blocked on ADR-001.
- [ ] Critical questions (§80, all 40) answered or explicitly deferred with cause
- [ ] Load-bearing assumptions validated — at minimum A-003, A-004, A-007
- [x] **Failure scenario pass complete** (§59) — adversarial review F9.
      [failure-scenarios.md](failure-scenarios.md), 2026-08-12. **Found ten gaps,
      three severe**: no runtime supervisor (N5), no cache size budget (N6), TLS
      absent from the architecture (N8). Two constraints written back into
      existing decisions, three additions, two decisions we had not realised we
      needed. Network partition between nodes is not walked, since clustering is
      deferred.
- [ ] **Three 2 AM scenarios written end to end** — adversarial review F5. Stale
      tiles, a slow service, a failed registration. What does the administrator
      see, from which endpoint, composed from what? If it cannot be written, the
      observability model is missing rather than deferred.
- [ ] **Geometry and CRS reality pass** — fresh-challenger review G4. A written
      policy, per provider, for invalid geometry, wrong or missing SRID, datum
      transformation selection, Z and M coordinates, curve geometry, oversized
      single features, mixed geometry types, and collation. Nine ADRs currently
      do not mention any of them, and invalid geometry is the most common
      real-world GIS problem there is.
- [ ] **Air-gapped checklist written and tested** — adversarial review F11.
      Q-15. Offline PROJ grids, GDAL driver data, fonts, MapLibre glyph packs and
      sprites, COG-capable clients, and any rasteriser's display requirements.
      Tested by attempting an install with no network.
- [ ] High-risk decisions have prototypes
- [ ] Performance-sensitive decisions have benchmarks
- [ ] Contradiction sweep clean (§63)
- [ ] Adversarial review complete, every material criticism resolved or recorded
- [ ] Fresh-challenger review complete (§67)
- [ ] Licensing implications understood
- [ ] No blocking open question remains
