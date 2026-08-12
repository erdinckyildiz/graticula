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
| Hosted datastore (optional) | proposed | — | — | — | — | — | — | proposal written; Q-32 decides v1 scope |
| Geometry engine | — | DRAFT | — | — | — | — | — | blocked on ADR-001 |
| Rendering engine | rescoped | DEFERRED | n/a | — | — | — | — | `DEFERRED` — vector-first; only WMS-in-compatibility-layer remains |
| API architecture | — | DRAFT | — | — | — | — | — | not started |
| Plugin model | — | DRAFT | — | — | — | — | — | not started |
| Service runtime | — | DRAFT | — | — | — | — | — | not started |
| Query engine | — | DRAFT | — | — | — | — | — | not started |
| Raster engine | rescoped | DRAFT | — | — | — | — | — | catalog + access control only; no pixel production. Q-27 is now the deciding question |
| Caching | — | DRAFT | — | — | — | — | — | not started |
| Job system | — | DRAFT | — | — | — | — | — | not started |
| Clustering | deferred | DEFERRED | n/a | n/a | — | — | — | `DEFERRED` (§79) |
| Build vs adopt policy | ✓ | n/a | n/a | n/a | — | — | — | ACTIVE |
| Provider architecture (§27) | — | — | — | — | — | — | — | not started |
| Feature services (§28) | — | — | — | — | — | — | — | not started |
| Vector tiles (§33) | — | — | — | — | — | — | — | not started — **elevated**: the only tile format, and the source for WMS compatibility |
| Glyph & sprite serving | — | — | — | — | — | — | — | not started — new requirement from the vector-first decision; must work air-gapped |
| Style document management | — | — | — | — | — | — | — | not started — storage and serving only, no evaluation |
| Publishing (§38) | — | — | — | — | — | — | — | not started — now also owns runtime schema evolution, which is publishing with a smaller blast radius |
| Admin API (§39) | — | — | — | — | — | — | — | not started — **elevated**: primary user is the GIS administrator |
| Compatibility layer (§51) | — | — | — | — | — | — | — | not started — **required**, not optional (migration is a goal). Outside the core domain. |
| AuthN / AuthZ (§41, §42) | — | — | — | — | — | — | — | not started |
| Observability (§46) | — | — | — | — | — | — | — | not started |
| Backpressure (§48) | — | — | — | — | — | — | — | not started |
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
