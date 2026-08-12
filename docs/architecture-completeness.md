# Architecture Completeness Matrix

Tracks how far each architectural area has actually been taken (§64). The point
is to make gaps visible — an area with a decision but no failure review is not
finished, however confident the ADR sounds.

**Legend:** `—` not started · `WIP` in progress · `✓` complete · `n/a` not applicable

---

| Area | Decision | ADR | Prototype | Benchmark | Security review | Ops review | Failure review | Status |
|---|---|---|---|---|---|---|---|---|
| Core language | narrowed to Go vs .NET | DRAFT | specified | — | — | — | — | `REQUIRES PROTOTYPE` — paper round done, prototype spec written |
| Primary data architecture | **decided, then amended** | `ACCEPTED WITH CONDITIONS` | — | — | — | — | — | Reopened and re-decided same day: platform store portable across SQLite/PostgreSQL/SQL Server/Oracle. Conditions in ADR-002 §9. State inventory delivered for ADR-012. |
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
| Provider architecture (§27) | — | — | — | — | — | — | — | not started |
| Feature services (§28) | — | — | — | — | — | — | — | read **and write**. A-026 asks whether OGC API Features plus additive extensions covers §28; A-027 covers concurrency against writes that bypass us |
| Vector tiles (§33) | — | — | — | — | — | — | — | not started — **elevated**: the only tile format, and the source for WMS compatibility |
| Glyph & sprite serving | — | — | — | — | — | — | — | not started — new requirement from the vector-first decision; must work air-gapped |
| Style document management | — | — | — | — | — | — | — | not started — storage and serving only, no evaluation |
| Publishing (§38) | — | — | — | — | — | — | — | not started — owns runtime schema evolution (publishing with a smaller blast radius) and registration, which runs as an interactive-class job |
| Admin API (§39) | — | — | — | — | — | — | — | not started — **elevated**: primary user is the GIS administrator |
| Compatibility layer (§51) | — | — | — | — | — | — | — | not started — **required**, not optional (migration is a goal). Outside the core domain. |
| AuthN / AuthZ (§41, §42) | — | — | — | — | — | — | — | not started |
| Observability (§46) | — | — | — | — | — | — | — | not started |
| Backpressure (§48) | — | — | — | — | — | — | — | shape set by ADR-007 §4.9: bounded queues, admission control, immediate rejection with retry signal |
| Resource governance (§49) | — | — | — | — | — | — | — | not started |
| Deployment profiles (§53) | — | — | — | — | — | — | — | not started |
| Licensing (§55) | WIP | n/a | n/a | n/a | — | — | n/a | see [DEPENDENCY-LICENSES.md](../DEPENDENCY-LICENSES.md) |

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

- [ ] `architecture-assessment.md` complete — all 27 required sections (§70)
- [ ] Initial ADRs written, none still `DRAFT` without a stated reason
- [ ] Critical questions (§80, all 40) answered or explicitly deferred with cause
- [ ] Load-bearing assumptions validated — at minimum A-003, A-004, A-007
- [ ] High-risk decisions have prototypes
- [ ] Performance-sensitive decisions have benchmarks
- [ ] Contradiction sweep clean (§63)
- [ ] Adversarial review complete, every material criticism resolved or recorded
- [ ] Fresh-challenger review complete (§67)
- [ ] Licensing implications understood
- [ ] No blocking open question remains
