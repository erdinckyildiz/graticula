# Architecture Assessment

**Status:** OUTLINE — no section written yet
**Phase:** 0 — Architecture Discovery
**Required by:** §70, §85

This is the primary deliverable of Phase 0. Production implementation does not
begin until this document is complete and has passed adversarial review (§85).

The 27 sections below are mandated by §70. Each is stubbed with the question it
must answer, so that a half-written section is visibly half-written rather than
quietly missing.

---

## 1. Enterprise GIS problem definition

*What problem does an enterprise GIS application server actually solve, stated
without reference to any existing product? If this section cannot be written
convincingly, nothing after it matters.*

> Not written. Depends on the `[OWNER]` items in
> [product-context.md](product-context.md).

## 2. ArcGIS Server — strengths

*What does it genuinely get right? Written charitably; a strawman here poisons
every comparison downstream.*

> Not written.

## 3. ArcGIS Server — weaknesses

*Which are inherent, and which are historical constraints that no longer apply
(§4)? The distinction is the whole point.*

> Not written.

## 4. SOM / SOC / ArcSOC architectural analysis

*The dedicated investigation required by §16. Must answer: what problems was
ArcSOC solving; which of those problems still exist; how should they be solved
today. Publicly documented behaviour only (§5).*

> **Research complete (first pass):**
> [research/arcgis-som-soc.md](research/arcgis-som-soc.md).
>
> Headline finding: the most useful evidence is not the model itself but Esri's
> own trajectory away from it. SOM/SOC removed at 10.1 (robustness, recovery,
> provisioning); shared instances added at 10.7 (memory at scale). The
> incumbent converged on the hybrid model of §19 under production pressure, and
> the arithmetic that forced it also answers our §24 explosion test in advance.
>
> This section still needs to be written as prose for the assessment. Feeds
> [ADR-007](adr/ADR-007-service-runtime.md) and
> [service-runtime.md](service-runtime.md).

## 5. GeoServer — strengths and weaknesses

> Not written.

## 6. MapServer — strengths and weaknesses

> Not written.

## 7. QGIS Server — strengths and weaknesses

> Not written.

## 8. PostGIS-centric architectures

*The thin-server model: pg_tileserv, pg_featureserv, Martin, Tegola. What do
they prove is unnecessary in a GIS server, and where do they stop being enough?*

> Not written.

## 9. Modern geospatial server patterns

> Not written.

## 10. Modern cloud-native geospatial patterns

*COG, STAC, PMTiles, range requests, static tile hosting. Which of these remove
the need for server components rather than adding to them?*

*Must include the client-side case (GeoLibre and equivalents): a full analysis
stack in WebAssembly against remote cloud-native formats, with no server at all.
This section answers Q-18 — where a server genuinely earns its cost — and it is
the section the Adversarial Reviewer should attack hardest.*

> Not written. See `research/client-side-platforms.md` and the standing
> challenge in [product-context.md](product-context.md).

## 11. Legacy patterns to avoid

*With the reason each is being avoided. "Old" is not a reason (§4).*

> Not written.

## 12. Language comparison

> Not written. Summarises [ADR-001](adr/ADR-001-core-language.md).

## 13. Geometry engine comparison

> Not written. Summarises [ADR-003](adr/ADR-003-geometry-engine.md).

## 14. Rendering engine comparison

> Not written. Summarises [ADR-004](adr/ADR-004-rendering-engine.md).

## 15. Raster architecture options

> Not written. Summarises [ADR-009](adr/ADR-009-raster-engine.md).

## 16. Runtime architecture alternatives

*All six models from §18, each modelled at the service counts in §24.*

> Not written. Summarises [ADR-007](adr/ADR-007-service-runtime.md).

## 17. Query architecture alternatives

> Not written. Summarises [ADR-008](adr/ADR-008-query-engine.md).

## 18. Proposed initial architecture

*The synthesis. Everything above is input to this section.*

> Not written.

## 19. Deployment models

*Developer laptop, single server, enterprise cluster, Kubernetes — in that order
of priority (§53). Kubernetes last.*

> Not written.

## 20. Security risks

*§54: injection, SSRF, path traversal, auth bypass, privilege escalation,
malicious geometry and raster, decompression bombs, oversized requests, denial
of service, unsafe plugins, secret leakage, dependency vulnerabilities.*

> Not written.

## 21. Performance risks

> Not written.

## 22. Operational risks

*Measured against the standing question: could a GIS administrator diagnose and
repair this at 2 AM?*

> Not written.

## 23. Licensing risks

> Not written. Depends on verification in
> [DEPENDENCY-LICENSES.md](../DEPENDENCY-LICENSES.md).

## 24. Assumptions

> Not written. Summarises
> [architecture-assumptions.md](architecture-assumptions.md).

## 25. Unresolved questions

> Not written. Summarises [open-questions.md](open-questions.md).

## 26. Recommended experiments

*Each entry names the specific architectural question it settles. An experiment
that does not decide anything does not get run.*

> Not written.

## 27. Implementation roadmap

*Phases 1–9 (§71–§79), with the evidence required to enter each one.*

> Not written.
