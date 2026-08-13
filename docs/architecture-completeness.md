# Architecture Completeness Matrix

Tracks how far each architectural area has actually been taken (§64). The point
is to make gaps visible — an area with a decision but no failure review is not
finished, however confident the ADR sounds.

**Legend:** `—` not started · `WIP` in progress · `✓` complete · `n/a` not applicable

---

| Area | Decision | ADR | Prototype | Benchmark | Security review | Ops review | Failure review | Status |
|---|---|---|---|---|---|---|---|---|
| Core language | **.NET** | `ACCEPTED` | n/a | — | — | — | — | Decided on paper analysis and secondary criteria. **Not validated by a language benchmark** — stated in ADR-001 §6. Effort moved to absolute measurement (A-019) |
| Primary data architecture | **decided, amended three times in one day** | `ACCEPTED WITH CONDITIONS` | — | — | — | — | — | **Platform store is PostgreSQL only** (Q-70). Four engines → two (Q-51) → one, in a single day. PostgreSQL is now a hard dependency of every deployment because the datastore is mandatory (Q-69) and PostGIS-only (Q-32); A-009 reinstated, A-018 superseded, Q-22 reversed. SQL Server and Oracle remain **providers only** — not platform stores, not datastores, not tile sources. Conditions in ADR-002 §9. |
| Provider architecture — multi-dialect | **widened sharply: six dialects (Q-80, Q-81)** | — | — | — | — | — | — | **Six** SQL dialects on the feature path: PostGIS, SQL Server, Oracle, **MySQL 8, MariaDB** (two providers, not one — their spatial implementations diverged) and **DuckDB as the file-format query engine, not a registered source** (Q-81). Tiles remain hosted-PostGIS only (Q-67). **The binding problem is no longer pushdown — it is Q-20**: six geometry engines evaluating our predicates, disagreeing at the edges, which never-degrade-silently cannot catch because every engine claims `intersects`. A-043 |
| Hosted datastore (**mandatory**) | **decided: v1, PostGIS only, managed appliance, required** | — | — | — | — | — | — | **Q-69 made it mandatory**, which via Q-32 made PostgreSQL mandatory (Q-70). It is now the only tile source (Q-67) and the platform store lives beside it. Storage model in [data-model.md](data-model.md). Q-71 asks how server, datastore and job-worker images are packaged and version-matched |
| Data model — storage and layer modes | **decided** | n/a | — | — | — | — | — | four lifecycles separated; datastore is a provider we own, on any of three spatial engines; editing is out of our API |
| Connection discipline / quiesce | — | — | — | — | — | — | — | new obligation: we must not block a DBA's DDL. Lands on ADR-007 §5b and the admin API |
| Schema drift detection | — | — | — | — | — | — | — | improvement over ArcGIS, which requires manual restart. A-023 |
| Geometry engine | engine settled | DRAFT | prototype | **measured x2** | — | — | — | .NET means NetTopologySuite in-runtime. The split is now empirically placed, not argued: clip and simplify are ours, topology stays NTS (ADR-003 §5a-§5b). Q-66 asks the next question — whether the tile path takes geometry objects at all |
| Rendering engine | rescoped | DEFERRED | n/a | — | — | — | — | `DEFERRED` — vector-first; only WMS-in-compatibility-layer remains |
| API architecture | **decided, scope widened sharply** | `ACCEPTED WITH CONDITIONS` | — | — | — | — | — | OGC API Features 1+2+3 native; capability report generated not hand-written. **Q-78 put full protocol parity in scope**: 29 faces over 10 engines — [protocol-surface.md](protocol-surface.md). Eight engines decided or in flight, two do not exist (Q-79). Six faces over the feature engine is now the hardest available test of ADR-005's protocol-neutral interface, which was asserted and never proven (A-026) |
| Plugin model | **`REOPENED`** | `REOPENED` | — | — | — | — | — | **The trigger fired, 2026-08-13, by the route ADR-006 predicted** — Q-17b puts a Python-based GPServer in scope, which is a third-party plugin system for geoprocessing. ADR-006 named geoprocessing as the first candidate because job workers already isolate. Providers, formats and API surfaces stay internal-only; only the geoprocessing axis reopened. Q-74 to Q-76 |
| Service runtime | **decided** | `ACCEPTED WITH CONDITIONS` | — | **partly measured** | — | — | — | **§4.1's worker sizing has no allocation term and needs one** — A-037 validated, ADR-007 §4.14. The ArcSOC question answered. Structure decided, numbers are conditions. Affinity routing (A-014) is the unproven part. Amended with context pinning (§4.12) as the dedicated-instance equivalent |
| Query engine | **decided** | `ACCEPTED WITH CONDITIONS` | — | — | — | — | — | structure decided, numbers not. Conditions in ADR-008 §10 |
| Raster engine | **`REOPENED`** | `REOPENED` | — | — | — | — | — | **Q-17c puts ImageServer in scope**, reversing serve-COG-and-let-the-client-render. Decomposed per operation in ADR-009 §0: `identify`, histograms, `download` and footprint query are near-free; `exportImage`, raster functions and dynamic mosaicking need a raster rendering engine and are **plausibly the largest single capability in the matrix**. Q-77 must draw the Tier 1/Tier 2 line first. COG storage, conversion at registration, GDAL on job workers and proxied delivery all survive |
| Caching | **decided** | `ACCEPTED WITH CONDITIONS` | — | — | — | — | — | tiles are the real cache; L2 optional permanently; coherence is best-effort for registered data and documented as such |
| Job system | **decided** | `ACCEPTED WITH CONDITIONS` | — | — | — | — | — | queue in the platform store, no broker; job classes with reserved capacity; every job declares its re-run behaviour |
| Clustering | deferred | DEFERRED | n/a | n/a | — | — | — | `DEFERRED` (§79) |
| Build vs adopt policy | ✓ | n/a | n/a | n/a | — | — | — | ACTIVE |
| Provider architecture (§27) | **resolved (Q-52)** | — | — | — | — | — | — | **Q-52 answered 2026-08-13.** Serving providers are databases only — PostGIS, SQL Server, Oracle, MySQL, MariaDB — plus DuckDB as the *file-query engine* (Q-81), which is what lets a file be queried without becoming a provider. Import sources are Shapefile, GeoPackage, FileGDB, KML, GPX, WKT, GeoJSON, FlatGeobuf, GeoParquet, converted at registration. Output formats add GeoArrow and MVT. **§27's error is now explicit: a format is not a provider.** Warehouses still declined (Q-53) |
| Feature services (§28) | — | — | — | — | — | — | — | read **and write**. A-026 asks whether OGC API Features plus additive extensions covers §28; A-027 covers concurrency against writes that bypass us |
| Vector tiles (§33) | **scope decided (Q-67): hosted data only** | — | prototype | **measured x3** | — | — | — | [benchmarks/mvt-generation/RESULTS.md](../benchmarks/mvt-generation/RESULTS.md). A-019 passes; own clipper 63x and own simplifier 47x faster than the adopted equivalents, at equal output. Geometry is no longer the cost — allocation is: **80.9% GC pause at 18% CPU utilisation** under load (A-037, validated). Database pushdown of clip is structural not optional (A-021, finding 11: a z16 tile read 201,580 vertices to emit 2,080). **Q-67 removed the multi-dialect tile problem entirely** — every tile source is now PostGIS, so the measurement gap closed by decision rather than by evidence. Open: Q-68 (do we keep our own encoder) and Q-69 (is the datastore still optional) |
| Glyph & sprite serving | — | — | — | — | — | — | — | not started — new requirement from the vector-first decision; must work air-gapped |
| Style document management | — | — | — | — | — | — | — | not started — storage and serving only, no evaluation |
| Publishing (§38) | — | — | — | — | — | — | — | not started — owns runtime schema evolution (publishing with a smaller blast radius) and registration, which runs as an interactive-class job |
| Admin API (§39) | — | — | — | — | — | — | — | not started — **elevated**: primary user is the GIS administrator |
| Compatibility layer (§51) | **scope set, amended twice in one day** | — | — | — | — | — | — | v1 is **WFS + WMTS-for-vector-tiles + full ArcGIS FeatureServer including edits + GeometryServer** (Q-17, Q-17a). **GeometryServer added and elevated to v1 core** — owner calls it crucial. Cheap to build, not cheap to operate: it publishes the general overlay run 1 measured at 438.6 ms, on caller-supplied geometry, with no special case to exploit (ADR-005 §3.3b, A-042). **GPServer in, Python-based, after the SDK** (Q-17b) — reopens ADR-006. **ImageServer in, decomposed per operation** (Q-17c) — reopens ADR-009. WMS and MapServer remain out of v1 (Q-47, ADR-004). Outside the core domain. |
| Feature service data model gaps | **identity, relationships, attachments decided** | `ACCEPTED WITH CONDITIONS` | — | — | **partial** | — | — | [ADR-013](adr/ADR-013-feature-service-data-model.md), 2026-08-13. Q-57, Q-58a, Q-58b answered; **all three ship in v1**. Identity is declared not inferred; relationships declared not reverse-engineered; attachments in the database, streamed, behind a separate bounded pool. Remaining: **Q-58c** — domains, subtypes, editor tracking. Opened A-040 and A-041. Originally: **Corroborated 2026-08-13**: Honua ships attachments and related records at Community tier ([research/honua-capability-matrix.md](research/honua-capability-matrix.md) §2), so these are table stakes for FeatureServer compatibility rather than refinements |
| Format support | **enumerated (Q-52)** | — | — | — | — | — | — | 9 import, 5 output. **A-038 confirmed**: File Geodatabase has no managed .NET reader, so GDAL is not avoidable and A-016's job-worker placement stands. **GeoParquet now has three independent justifications** — Q-74's Python SDK boundary, Q-81's DuckDB engine, and this list — having had none a week ago |
| Migration tooling | **scope set** | — | — | — | — | — | — | inventory plus definition import, free (Q-16). Needs a home — probably its own component rather than part of the compatibility layer |
| Self-service publishing | **scope confirmed** | — | — | — | — | — | — | second user type and second publishing path. Neither GeoServer nor Honua has this. Q-59 to Q-62 open |
| AuthZ (§42) | **partial** | n/a | — | — | — | — | — | model decided in [security.md](security.md) §2–3. Multi-tenant resource isolation still open (D-04) |
| AuthN (§41) | **decided** | `ACCEPTED WITH CONDITIONS` | — | — | **partial** | — | — | [ADR-015](adr/ADR-015-authentication.md), 2026-08-13. **Blocker B4 discharged.** Three principals including anonymous-as-a-principal; **opaque server-side tokens rather than JWT**, which Q-70's mandatory PostgreSQL made cheap and which buys real revocation; local accounts first-class because air-gapped sites have no IdP; OIDC, SAML and SCIM free where Honua gates them at Pro and Enterprise. Two hard constraints shaped it: the identity must map to a **database role** for RLS delegation (A-047, five engines), and ArcGIS compatibility forces credentials into URLs (bounded, not fixed). Conditions in §9 |
| **Protocol conformance testing** | — | **none** | — | — | — | — | — | **Absent from the architecture, and Q-78 makes that untenable.** Honua publishes 1,117 passing tests across 13 suites. Claiming 29 protocol faces without conformance evidence is a weaker position than claiming six with it. OGC publishes the suites; this is a CI decision to be taken early, not discovered late |
| **Geocoding** (GeocodeServer) | **in scope (Q-84)** | — | — | — | — | — | — | **New subsystem, not an endpoint.** Address parsing (locale-specific — a Turkish-built product has an advantage here that Anglocentric open geocoders lack), matching and scoring, reverse and batch. **Reference data is the real question**, and it is GPServer's toolbox problem again: a geocoder with no street centrelines is an empty shell. Recommended: build the engine, customer brings the data — the only option that survives Q-15 air-gapped |
| **Observation store** (SensorThings, EDR) | **IN SCOPE — PENDING CONFIRMATION (Q-79)** | — | — | — | — | — | — | **New product surface, not a new endpoint** — a different domain model with its own storage, temporal indexing and write path. Q-79 asks whether it was chosen or swept in |
| **3D and terrain** (3D Tiles, Terrain-RGB) | **IN SCOPE — PENDING CONFIRMATION (Q-79)** | — | — | — | — | — | — | **No foundation in anything decided** — not the vector tile pipeline, not the raster engine. Q-79 |
| Observability (§46) | — | — | — | — | — | — | — | not started |
| Backpressure (§48) | — | — | — | — | — | — | — | shape set by ADR-007 §4.9. Fairness still depends on N4's per-source limit and D-04's per-tenant limits, neither of which exists |
| Runtime supervisor (§21) | **designed** | feeds ADR-007 | — | — | — | — | — | [runtime-supervisor.md](runtime-supervisor.md). Severe gap closed. Q-63 to Q-65 remain: routing placement, memory-growth detection, adoption protocol |
| User-uploaded content | **policy written** | feeds ADR-013 | — | — | **partial** | — | — | [security.md](security.md), 2026-08-13. First surface accepting arbitrary user bytes. Open: virus scanning posture, and whether an attachment inherits its feature's row-level security or carries its own grant |
| **TLS / certificates** | **decided** | `ACCEPTED WITH CONDITIONS` | — | — | **partial** | — | — | [ADR-014](adr/ADR-014-tls-and-certificates.md), 2026-08-13. **Severe gap closed, blocker B3 discharged, N8 closed.** Four surfaces; we terminate by default because *run our container* must not become *also run nginx*. Rotation without restart is architectural, not courteous — a restart evicts the warm state ADR-007 §4.4 protects (A-044). Expiry becomes a supervisor duty and **supplies one of F5's three missing 2 AM scenarios**. Also fixed §6's unwritten SSRF hole. Conditions in §9 |
| Failure scenarios (§59) | **walked** | n/a | — | — | — | — | ✓ | [failure-scenarios.md](failure-scenarios.md) |
| Geometry & CRS policy | **written** | n/a | — | — | — | — | — | [geometry-crs-policy.md](geometry-crs-policy.md). Per-engine claims need `VERIFY` |
| Resource governance (§49) | — | — | — | — | — | — | — | not started |
| Deployment profiles (§53) | **decided** | `ACCEPTED WITH CONDITIONS` | — | — | — | **partial** | — | [ADR-016](adr/ADR-016-packaging-deployment-upgrade.md), 2026-08-13. **Blocker B5 discharged. Q-71, Q-13 and Q-76 answered; N9 closed.** Three images; version handshake **refuses rather than auto-migrates**; expand-and-contract; **rollback to exactly one version and only before contract**. Air-gapped delivery is a single verifiable bundle with nothing fetched at runtime, which is what decided Q-76 against pip. Developers run the same compose file — no second code path. Conditions in §10 |
| Licensing (§55) | WIP | n/a | n/a | n/a | — | — | n/a | see [DEPENDENCY-LICENSES.md](../DEPENDENCY-LICENSES.md) |
| Competitive position | **analysed** | n/a | — | — | — | — | — | [competitive-position.md](competitive-position.md). Q-49 attempted three times with no strong answer. GeoNode already does self-service publishing. What survives is a niche: the fully open ArcGIS Server exit path. **Needs validation with real GIS teams, which desk research cannot supply.** |

## Adversarial reviews (§85, §67)

| Round | Date | Findings | Status |
|---|---|---|---|
| 1 — adversarial | 2026-08-12 | 12 (3 severe) | [adversarial-review-1.md](reviews/adversarial-review-1.md) — all dispositions applied |
| 2 — fresh challenger (§67) | 2026-08-12 | 8 (3 severe) | [fresh-challenger-review-2.md](reviews/fresh-challenger-review-2.md) — **written by the same agent, which §67 forbids.** Findings are real; coverage is suspect. A genuine independent review is still owed. |
| 3 — genuinely independent | — | — | **Still owed.** §67 requires a reviewer who did not participate. Rounds 1 and 2 were both self-review. |
| **Contradiction sweep 1 (§63)** | 2026-08-13 | **11 (3 severe)** | [contradiction-sweep-1.md](reviews/contradiction-sweep-1.md) — blocker **B1 discharged**. S1 vector-first superseded by accumulation without ever being reversed; S2 in-scope capabilities whose ADR is `DEFERRED`; S3 **§82 applied to a list rather than per capability**, which is a binding rule that stopped being followed under scope pressure. Eight dispositions applied, three raised as Q-85 to Q-87 |

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

**Assessment 2026-08-13:** [phase-0-exit-plan.md](phase-0-exit-plan.md). Four of
sixteen met, but the count is misleading in both directions. Several criteria
below cannot be met by Phase 0 at all — eight ADRs carry conditions that are
*numbers* only a running system produces. Five items genuinely block the first
production line, and they are not the five with the biggest boxes.



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
- [x] **Geometry and CRS reality pass** — fresh-challenger review G4.
      [geometry-crs-policy.md](geometry-crs-policy.md), 2026-08-12. Found that
      five separate problems are one problem: **lossy on read means not
      writable**, now enforced by the write path in ADR-008 §4.5a. Answered Q-56
      and gave Q-36 its first concrete requirement. Per-engine behaviour still
      needs verification.
- [~] **Air-gapped checklist — written 2026-08-13, not yet tested.** [ADR-016](adr/ADR-016-packaging-deployment-upgrade.md) §7 makes delivery a single verifiable bundle and forbids runtime fetching: no pip (Q-76), no ACME, no PROJ grid downloads, no GDAL driver fetch, soft-fail revocation. **What remains is whatever the rendering engine needs**, since ADR-004 is deferred — fonts and glyph packs. The criterion says *and tested*, and §10 condition 3 is the test: install on a machine with no network route. Original: **Air-gapped checklist written and tested** — adversarial review F11.
      Q-15. Offline PROJ grids, GDAL driver data, fonts, MapLibre glyph packs and
      sprites, COG-capable clients, and any rasteriser's display requirements.
      Tested by attempting an install with no network.
- [ ] High-risk decisions have prototypes
- [ ] Performance-sensitive decisions have benchmarks — **tile path done three times**: [benchmarks/mvt-generation/RESULTS.md](benchmarks/mvt-generation/RESULTS.md) settles A-019, A-004, A-001, A-037 and A-021-on-PostGIS, and opened A-039. Remaining: connection budget, worker model, affinity routing, seeding, feature-query streaming, and a capacity number on hardware that is not capped and contended. **The same tile path on SQL Server and Oracle is deferred as [D-05](architecture-debt.md)** — accepted debt, not an oversight
- [x] **Contradiction sweep run** (§63) — [contradiction-sweep-1.md](reviews/contradiction-sweep-1.md), 2026-08-13. **Run, not clean**: 11 findings, 3 severe, 8 dispositions applied and 3 open as Q-85 to Q-87. The criterion says *clean*, so it stays unticked until those three close. **The finding to carry forward is S3** — §82 stopped being applied under scope pressure, and the next sweep cannot catch that, because a missing justification looks exactly like one nobody wrote down
- [ ] Adversarial review complete, every material criticism resolved or recorded
- [ ] Fresh-challenger review complete (§67)
- [ ] Licensing implications understood
- [ ] No blocking open question remains
- [x] **Q-49 answered by the owner, 2026-08-13** — *"I will give this to the world."* **The requirement to test it with real GIS teams is dissolved rather than met**: it assumed a commercial-style justification a gift does not owe. The positioning was sharpened at the same time, from *better capabilities than GeoServer* (measurably false) to *the ArcGIS Server exit path* — [competitive-position.md](competitive-position.md) §6. What survives is a **prioritisation** risk, not an existential one: nobody outside the project has confirmed the exit path matters, so we may build the right thing in the wrong order. Original criterion: **Q-49 tested with real GIS teams.** [competitive-position.md](competitive-position.md)
      concludes that desk research cannot answer why this product should exist.
      Three conversations with organisations running ArcGIS Server would settle
      it, and every architectural decision after this one is cheaper to make with
      an answer than without.
