# Next-Generation Enterprise GIS Platform
## Architecture, Research and Implementation Master Prompt

---

# 1. Mission

Design and progressively implement a **next-generation enterprise GIS application server and geospatial platform** from first principles.

The objective is NOT to clone ArcGIS Server, GeoServer, MapServer, QGIS Server, or any other existing GIS product.

The objective is to answer:

> If we were designing an enterprise GIS application server from scratch today, using everything learned from ArcGIS Server, ArcSOC, GeoServer, PostGIS, modern distributed systems, cloud-native geospatial technologies, and current software engineering practices, what should it look like?

Study existing systems carefully.

Understand **why** they made particular architectural decisions.

Keep the ideas that still solve real problems.

Discard historical constraints that no longer apply.

Improve designs where modern technologies allow better solutions.

The resulting platform should combine the strongest architectural concepts from established GIS systems with modern software architecture.

The goal is not feature imitation.

The goal is architectural excellence.

---

# 2. Fundamental Principles

The platform should ultimately be:

- high performance
- reliable
- modular
- secure
- observable
- horizontally scalable
- vertically scalable
- standards-first
- vendor-neutral
- extensible
- container-friendly
- cloud-native where beneficial
- cloud-independent
- database-efficient
- operationally simple
- suitable for air-gapped environments
- suitable for enterprise deployments
- suitable for small deployments

It must be possible to operate it:

1. on a developer laptop
2. as a single server
3. on Linux
4. on Windows where technically reasonable
5. in Docker
6. in Kubernetes
7. on-premise
8. in private cloud
9. in public cloud
10. in air-gapped environments

Do not require Kubernetes, Redis, Kafka, service mesh, object storage, or other distributed infrastructure merely to run the basic platform.

The simplest useful deployment should ideally resemble:

```text
GIS Server
    |
PostgreSQL/PostGIS
```

Potentially even:

```text
./gis-server
```

with external infrastructure added only when justified.

---

# 3. Do Not Start Coding Immediately

Architecture and implementation must be evidence-driven.

Before significant production implementation:

1. research existing GIS architectures
2. identify the problems they solve
3. compare alternatives
4. identify assumptions
5. identify risks
6. create architecture proposals
7. challenge those proposals
8. prototype uncertain areas
9. benchmark performance-sensitive decisions
10. record decisions
11. reconsider earlier decisions when new evidence appears

Do not rush toward implementation.

Do not mistake code generation speed for engineering progress.

---

# 4. Existing Systems to Study

Study architectural ideas from systems and technologies including, but not limited to:

## Enterprise GIS

- ArcGIS Server
- ArcGIS Enterprise
- GeoServer
- MapServer
- QGIS Server

## Spatial Databases

- PostgreSQL/PostGIS
- Microsoft SQL Server Spatial
- Oracle Spatial
- SQLite / SpatiaLite

## Geospatial Libraries

- GDAL / OGR
- PROJ
- GEOS
- JTS
- NetTopologySuite
- GeoTools

## Modern Geospatial Servers

- pg_tileserv
- pg_featureserv
- Martin
- Tegola
- TiTiler

## Modern Formats and Standards

- GeoJSON
- MVT
- FlatGeobuf
- GeoParquet
- GeoPackage
- COG
- STAC
- PMTiles
- MBTiles

## Visualization / Rendering

- MapLibre ecosystem
- Skia
- Cairo
- Cesium ecosystem

Do not assume these systems are correct.

Do not assume older systems are obsolete.

For every important architectural idea ask:

> What problem was this design solving?

Then:

> Is that problem still relevant today?

Then:

> If we solved the same problem today from first principles, what would the solution look like?

---

# 5. Clean-Room Requirement

Use existing systems for architectural learning and public behavioral understanding only.

Do not reproduce:

- proprietary source code
- undocumented proprietary internals
- protected implementation details
- proprietary algorithms

Use open standards and clean-room architecture.

Any compatibility layer must remain isolated from the core architecture.

---

# 6. Multi-Agent Architecture Council

Architecture must NOT be designed from the perspective of one agent.

Create a **Multi-Agent Architecture Council**.

Different agents must independently investigate and challenge major architectural decisions.

At minimum establish the following roles.

## Agent 1 — Chief GIS Architect

Responsible for overall GIS architecture, GIS service model, publishing, layer model, service lifecycle, enterprise GIS workflows, GIS interoperability, and ArcGIS / GeoServer / MapServer / QGIS comparison.

## Agent 2 — Distributed Systems Architect

Responsible for concurrency, process architecture, clustering, horizontal scaling, failure recovery, request routing, distributed state, service discovery, backpressure, messaging, and consistency.

This agent must actively challenge unnecessary distributed-system complexity.

## Agent 3 — Performance Engineer

Responsible for throughput, latency, memory, CPU, allocations, context switching, serialization, network overhead, DB pressure, cache performance, map rendering, raster performance, vector tile generation, and spatial queries.

Frequently ask:

> Where is the benchmark proving this assumption?

## Agent 4 — Spatial Database Architect

Responsible for PostGIS, SQL Server Spatial, Oracle Spatial, query planning, spatial indexing, provider abstraction, SQL generation, transactions, connection pooling, data versioning, and large result sets.

This agent should prevent unnecessary movement of spatial computation out of databases when databases can execute it more efficiently.

## Agent 5 — GIS Runtime / Systems Architect

Responsible for studying and redesigning concepts historically addressed by Server Object Manager, SOC, ArcSOC, service instances, instance pooling, shared instances, dedicated instances, process isolation, process recycling, crash containment, and service lifecycle.

## Agent 6 — Security Architect

Responsible for authentication, authorization, RBAC, ABAC, SQL injection, SSRF, path traversal, secrets, malicious files, geometry bombs, decompression bombs, denial of service, plugin security, dependency security, and multi-tenant isolation.

## Agent 7 — Platform / Operations Architect

Responsible for installation, deployment, configuration, upgrades, rollback, backup, disaster recovery, monitoring, troubleshooting, Linux, Windows, Docker, Kubernetes, and air-gapped operation.

Frequently ask:

> Could a GIS administrator realistically diagnose and repair this system at 2 AM?

## Agent 8 — API / Standards Architect

Responsible for OGC API Features, OGC API Tiles, OGC API Maps, OGC API Processes, WMS, WMTS, WFS, GeoJSON, MVT, and STAC.

External protocols must not dictate internal domain architecture.

## Agent 9 — Rendering Architect

Responsible for server-side cartography, symbols, labels, fonts, decluttering, rendering pipelines, Skia, Cairo, MapLibre, GPU possibilities, CPU rendering, and rendering caches.

## Agent 10 — Raster / Imagery Architect

Responsible for GDAL, COG, STAC, imagery, mosaics, overviews, multidimensional raster, raster functions, dynamic imagery, object storage, range requests, and reprojection.

## Agent 11 — Developer Experience / SDK Architect

Responsible for plugin architecture, provider SDK, extension SDK, client SDK, configuration, debugging, local development, documentation, and testing.

## Agent 12 — Adversarial Architecture Reviewer

This agent's job is to attack the architecture.

It should ask:

- Why is this wrong?
- What assumption are we hiding?
- Where will this fail?
- What happens at 10× load?
- What happens at 100× data?
- What happens with 5,000 services?
- What happens when a worker crashes?
- What happens when the DB is slow?
- What happens when the cache disappears?
- What happens when a plugin leaks memory?
- What happens during partial network failure?
- What happens during upgrades?
- What happens without Kubernetes?
- What happens on one server?
- What happens in an air-gapped deployment?

Do not optimize this agent for agreement.

Optimize it for finding flaws.

---

# 7. Independent Reasoning Before Debate

For every major architecture question, relevant agents must first analyze it independently.

Avoid groupthink.

---

# 8. Architecture Debate

After independent analysis, conduct structured debate.

Evaluate alternatives across correctness, performance, latency, memory, scalability, simplicity, reliability, failure isolation, operational complexity, security, maintainability, extensibility, portability, developer experience, licensing, and cost.

Record meaningful disagreement.

Do not manufacture consensus.

---

# 9. Architecture Judge

Use a separate Architecture Judge.

Possible decision states:

```text
ACCEPTED
ACCEPTED WITH CONDITIONS
REQUIRES PROTOTYPE
REQUIRES BENCHMARK
REJECTED
DEFERRED
REOPENED
```

Every decision should have a confidence level:

```text
HIGH
MEDIUM
LOW
```

---

# 10. Architecture Is Reversible

No architectural decision is sacred.

This includes programming language, database, geometry engine, rendering engine, process model, worker architecture, API architecture, cache technology, plugin architecture, messaging, and deployment architecture.

Avoid sunk-cost reasoning.

---

# 11. Architecture Assumption Register

Maintain:

`docs/architecture-assumptions.md`

Statuses:

```text
UNVALIDATED
VALIDATING
VALIDATED
INVALIDATED
SUPERSEDED
```

Invalidating an assumption must trigger review of dependent ADRs.

---

# 12. Architecture Dependency Graph

Track relationships between architectural decisions.

If an upstream decision changes, review all downstream decisions.

---

# 13. Architecture Decision Records

Maintain ADRs under:

```text
/docs/adr
```

At minimum investigate:

```text
ADR-001-core-language.md
ADR-002-primary-data-architecture.md
ADR-003-geometry-engine.md
ADR-004-rendering-engine.md
ADR-005-api-architecture.md
ADR-006-plugin-model.md
ADR-007-service-runtime.md
ADR-008-query-engine.md
ADR-009-raster-engine.md
ADR-010-caching.md
ADR-011-job-system.md
ADR-012-clustering.md
```

Each ADR must contain context, alternatives, arguments, counterarguments, evidence, decision, consequences, confidence, assumptions, dependencies, and revisit triggers.

---

# 14. Programming Language Must Be Chosen, Not Assumed

Evaluate at minimum Go, Rust, C# / modern .NET, Java, TypeScript / Node.js, and Python.

Evaluate specifically for GIS server workloads and produce:

`ADR-001-core-language.md`

---

# 15. Architecture Philosophy — Modular First

Do not begin with dozens of microservices.

Prefer a well-structured modular architecture initially.

Components should later become independently deployable only when evidence shows independent scaling or isolation is beneficial.

---

# 16. ArcGIS Server SOM / SOC / ArcSOC Deep Investigation

Perform a dedicated investigation of the historical ArcGIS Server runtime architecture.

Study publicly understood concepts including Server Object Manager, Server Object Container, ArcSOC, service instances, minimum/maximum instances, instance pooling, dedicated/shared instances, process isolation, recycling, worker health, crash containment, startup, and shutdown.

Do NOT clone ArcSOC.

Answer:

> What problems was ArcSOC solving?

> Which of those problems still exist today?

> How should those problems be solved today?

---

# 17. Service ≠ Process

Separate:

```text
Service Definition
        |
        v
Service Runtime
        |
        v
Runtime Pool
        |
        v
Worker
```

A service definition is persistent configuration.

A worker is disposable execution infrastructure.

---

# 18. GIS Service Runtime

Evaluate:

1. fully in-process execution
2. thread/task isolation
3. shared worker processes
4. dedicated worker processes
5. container-per-service
6. hybrid worker architecture

Produce:

`docs/service-runtime.md`

and:

`ADR-007-service-runtime.md`

---

# 19. Hybrid Worker Architecture

Strongly investigate hybrid shared/dedicated runtime models.

Do not assume this is the final answer.

Benchmark it.

---

# 20. Specialized Workers

Investigate workload-specific workers:

```text
Feature Worker
Map Rendering Worker
Vector Tile Worker
Raster Worker
Geoprocessing Worker
```

One runtime policy may not fit all workloads.

---

# 21. Runtime Supervisor

Design a runtime supervisor capable of worker startup/shutdown, health monitoring, crash detection, restart, draining, recycling, memory monitoring, CPU monitoring, stuck-request detection, concurrency enforcement, and resource governance.

Worker failure must not crash the complete GIS platform.

---

# 22. Worker Recycling

Evaluate recycling based on lifetime, request count, memory, detected memory growth, configuration changes, deployment changes, and administrator request.

---

# 23. Service Lifecycle

Define formal service states such as:

```text
CREATED
CONFIGURED
STARTING
RUNNING
DEGRADED
DRAINING
STOPPED
FAILED
RESTARTING
```

State transitions must be observable.

---

# 24. Service Explosion Test

Explicitly model:

```text
10 services
100 services
1,000 services
5,000 services
10,000 services
```

Estimate workers, OS processes, memory, CPU, DB connections, startup time, recovery time, cache footprint, and monitoring cardinality.

---

# 25. Database Connection Explosion

For every runtime model calculate:

```text
nodes × workers × connection pool = potential DB connections
```

Reject uncontrolled multiplicative designs.

---

# 26. Thundering-Herd Startup

Analyze mass service restart and design staged startup or lazy initialization where beneficial.

---

# 27. Data Provider Architecture

Support extensible providers including PostgreSQL/PostGIS, SQL Server Spatial, Oracle Spatial, GeoPackage, FlatGeobuf, GeoJSON, Shapefile, GeoParquet, COG, filesystem, and object storage.

Use capability-aware providers.

---

# 28. Feature Services

Support metadata, fields, geometry, CRS, extent, query, filtering, pagination, sorting, statistics, aggregation, CRUD, batch editing, attachments, relationships, domains, subtypes, editor tracking, and optimistic concurrency.

---

# 29. Spatial Query Engine

Build a safe spatial query architecture using a Query AST and Spatial Query Planner.

Use parameterized queries.

Never concatenate untrusted input into SQL.

---

# 30. Push Computation Down Intelligently

Prefer database-side processing where beneficial, but do not blindly push everything to the database.

---

# 31. Geometry Engine

Evaluate GEOS, JTS, NetTopologySuite, PostGIS, Rust geometry libraries, and other mature alternatives.

Do not reinvent computational geometry.

---

# 32. Coordinate Systems

Use established projection technology such as PROJ.

Do not implement projection mathematics unnecessarily.

---

# 33. Vector Tiles

Treat vector tiles as first-class.

Support MVT, dynamic tiles, cached tiles, PostGIS ST_AsMVT where appropriate, tile invalidation, PMTiles, and MBTiles.

---

# 34. Map Rendering

Design serious server-side cartography.

Evaluate Skia, MapLibre Native, Cairo, and other mature engines.

---

# 35. Raster / Imagery

Design raster architecture as a first-class subsystem using modern concepts including GDAL, COG, STAC, overviews, range requests, and dynamic imagery.

---

# 36. Geoprocessing

Long-running operations must not block normal request workers.

Use an asynchronous job architecture.

---

# 37. Service Catalog

Design a hierarchical service catalog with stable IDs independent of display names.

---

# 38. Publishing Architecture

Publishing should follow validation, registration, runtime initialization, and safe rollback on failure.

---

# 39. Administrative API

Everything available in administration UI should be automatable through a separate administrative API.

---

# 40. Administration UI

Eventually provide a modern management UI, but backend architecture comes first.

---

# 41. Authentication

Support local accounts, JWT, OAuth 2.0, and OpenID Connect.

Future integrations may include Microsoft Entra ID, Keycloak, LDAP, and Active Directory.

---

# 42. Authorization

Implement RBAC initially and allow future ABAC.

---

# 43. Plugin Architecture

Use versioned extension contracts and analyze plugin failure/security isolation.

---

# 44. Caching

Evaluate L1 process memory, L2 distributed cache, and L3 disk/object storage.

Redis must not be mandatory for small installations.

---

# 45. Event Architecture

Define internal domain events and keep external messaging optional until justified.

---

# 46. Observability

Implement structured logging, request IDs, metrics, tracing, health, readiness, and liveness from the beginning.

Evaluate OpenTelemetry.

---

# 47. Performance Model

Assume millions of features, billions of vertices, and multi-terabyte raster collections.

Never assume data fits in RAM.

---

# 48. Backpressure

Queues must be bounded.

When capacity is exhausted, fail predictably rather than exhausting server resources.

---

# 49. Resource Governance

Allow per-service limits for workers, concurrency, memory, CPU, timeout, maximum features, response size, geometry complexity, and raster size.

---

# 50. Standards

Evaluate and support OGC API Features, OGC API Tiles, OGC API Maps, OGC API Processes, WMS, WMTS, WFS, GeoJSON, MVT, and STAC where appropriate.

---

# 51. Optional Compatibility Layer

Investigate optional migration adapters for commonly used GIS APIs where technically and legally appropriate.

Keep them outside the core domain.

---

# 52. High Availability

Design future multi-node deployment while keeping shared state explicit.

---

# 53. Deployment Profiles

Define developer, single enterprise server, enterprise cluster, and Kubernetes profiles.

Kubernetes comes after the platform works correctly without Kubernetes.

---

# 54. Security

Analyze SQL injection, SSRF, path traversal, auth bypass, privilege escalation, malicious geometry/raster, decompression bombs, oversized requests, denial of service, unsafe plugins, secret leakage, and dependency vulnerabilities.

---

# 55. Licensing

Create:

`DEPENDENCY-LICENSES.md`

Classify MIT, Apache-2.0, BSD, LGPL, GPL, AGPL, and proprietary/commercial dependencies.

Flag commercial distribution consequences.

---

# 56. Architecture Experiments

Maintain:

```text
/experiments
```

Use experiments to answer architecture questions, not as production code.

---

# 57. Benchmark-Driven Decisions

When agents disagree about measurable questions, benchmark alternatives.

Prefer evidence over architectural taste.

---

# 58. Benchmark Suite

Create realistic benchmarks for feature queries, spatial queries, vector tiles, rendering, and raster.

Measure throughput, p50, p95, p99, CPU, memory, and recovery where relevant.

---

# 59. Failure Scenario Council

Explicitly investigate worker crash, server crash, database outage, slow database, cache outage, network partition, full disk, out of memory, CPU saturation, huge geometry, invalid geometry, malformed raster, broken plugin, certificate expiry, configuration corruption, partial upgrade, and rolling deployment failure.

---

# 60. Scale Review

Evaluate small, medium, and large deployments.

Do not make small deployments unbearable merely to optimize for hypothetical huge deployments.

---

# 61. Open Questions Register

Maintain:

`docs/open-questions.md`

Do not hide uncertainty.

---

# 62. Architecture Debt Register

Maintain:

`docs/architecture-debt.md`

Temporary architecture must not silently become permanent.

---

# 63. Contradiction Detection

Continuously inspect architecture documents for contradictions and resolve them before proceeding.

---

# 64. Architecture Completeness Matrix

Maintain:

`docs/architecture-completeness.md`

Track Decision, ADR, Prototype, Benchmark, Security Review, Operations Review, Failure Review, and Status.

---

# 65. Periodic Full Architecture Reassessment

After every major phase, review the architecture from the beginning.

Ask:

> Knowing what we know now, would we still make the same original decisions?

If not, reopen them.

---

# 66. Architecture Review Gates

Run gates for Correctness, Simplicity, Performance, Failure, Operations, Security, Extensibility, Licensing, and Consistency.

Failure must reopen relevant decisions.

---

# 67. Fresh Architecture Challenger

Before declaring the architecture stable, create a fresh reviewer that did not participate in previous discussions.

Ask it to find every serious architectural mistake, hidden assumption, GIS-specific omission, scalability issue, operational weakness, security problem, and unnecessary complexity.

Do not defend the architecture.

Investigate the findings.

---

# 68. Repository Documentation

Maintain at minimum:

```text
/docs
    architecture.md
    architecture-assessment.md
    architecture-assumptions.md
    architecture-completeness.md
    architecture-debt.md
    open-questions.md
    service-runtime.md
    data-model.md
    query-engine.md
    rendering.md
    raster.md
    security.md
    deployment.md
    performance.md

/docs/adr
    ADR-001-core-language.md
    ADR-002-primary-data-architecture.md
    ADR-003-geometry-engine.md
    ADR-004-rendering-engine.md
    ADR-005-api-architecture.md
    ADR-006-plugin-model.md
    ADR-007-service-runtime.md
    ADR-008-query-engine.md
    ADR-009-raster-engine.md
    ADR-010-caching.md
    ADR-011-job-system.md
    ADR-012-clustering.md

/experiments
/benchmarks
```

---

# 69. Development Method

For each major capability:

```text
Research
   ↓
Independent Agent Analysis
   ↓
Architecture Debate
   ↓
Decision / Hypothesis
   ↓
Prototype if necessary
   ↓
Benchmark if necessary
   ↓
ADR
   ↓
Implementation
   ↓
Testing
   ↓
Performance Validation
   ↓
Architecture Reassessment
```

Do not leave the main branch knowingly broken.

---

# 70. Phase 0 — Architecture Discovery

Do NOT begin production implementation.

First produce:

`docs/architecture-assessment.md`

It must include:

1. enterprise GIS problem definition
2. ArcGIS Server strengths
3. ArcGIS Server weaknesses
4. SOM/SOC/ArcSOC architectural analysis
5. GeoServer strengths and weaknesses
6. MapServer strengths and weaknesses
7. QGIS Server strengths and weaknesses
8. PostGIS-centric architectures
9. modern geospatial server patterns
10. modern cloud-native geospatial patterns
11. legacy patterns to avoid
12. language comparison
13. geometry engine comparison
14. rendering engine comparison
15. raster architecture options
16. runtime architecture alternatives
17. query architecture alternatives
18. proposed initial architecture
19. deployment models
20. security risks
21. performance risks
22. operational risks
23. licensing risks
24. assumptions
25. unresolved questions
26. recommended experiments
27. implementation roadmap

Then create the initial ADRs.

Do not implement production code yet.

---

# 71. Phase 1 — Walking Skeleton

Only after Phase 0 passes architecture review.

Build a minimal PostGIS → GIS Runtime → HTTP API vertical slice.

---

# 72. Phase 2 — Standards

Implement OGC API Features appropriately.

---

# 73. Phase 3 — Vector Tiles

Implement and benchmark MVT.

---

# 74. Phase 4 — Rendering

Add server-side rendering after validating the rendering architecture.

---

# 75. Phase 5 — Raster

Add GDAL-backed raster capabilities.

---

# 76. Phase 6 — Administration

Implement administrative API and service lifecycle management.

---

# 77. Phase 7 — Security

Introduce enterprise authentication and authorization.

---

# 78. Phase 8 — Processing

Introduce asynchronous geoprocessing jobs.

---

# 79. Phase 9 — Distributed Runtime

Only separate modules or introduce distributed infrastructure when benchmarks and operational requirements demonstrate that it is necessary.

---

# 80. Critical Questions That Must Eventually Be Answered

The Architecture Council must explicitly answer:

1. What is the core programming language?
2. Should the platform be polyglot?
3. What constitutes a GIS service?
4. What constitutes a GIS worker?
5. What is the modern equivalent of ArcSOC?
6. Which services require process isolation?
7. Should workers be shared or dedicated?
8. How are workers supervised?
9. How are workers recycled?
10. How does request routing work?
11. How is backpressure implemented?
12. How are DB connections budgeted?
13. Which geometry engine is used?
14. Which rendering engine is used?
15. How is GDAL integrated?
16. How are CRS transformations performed?
17. How are data providers abstracted?
18. How are provider-specific capabilities exposed?
19. How are large query results streamed?
20. Where does spatial computation execute?
21. How does MVT generation work?
22. How does raster processing work?
23. How does caching work?
24. How does cache invalidation work?
25. How does publishing work?
26. How does service startup work?
27. How does service recovery work?
28. How does clustering work?
29. Which state is persistent?
30. Which state is ephemeral?
31. How is configuration stored?
32. How are secrets stored?
33. How do plugins work?
34. How are plugins isolated?
35. How are upgrades performed?
36. How are rollbacks performed?
37. How are DB schema migrations performed?
38. How is backward compatibility handled?
39. How does the system behave during partial failure?
40. How does the system remain easy to operate?

Do not consider architecture mature while critical questions remain unanswered.

---

# 81. Definition of Architecture Ready

Architecture is ready for serious implementation when major architectural questions are answered, important assumptions are validated, high-risk decisions have prototypes, performance-sensitive decisions have benchmarks, major contradictions are resolved, runtime/data/failure/security/operational models are credible, licensing implications are understood, no critical open question remains, and fresh adversarial review has been completed.

Architecture still remains revisable after this point.

---

# 82. Anti-Overengineering Rule

For every proposed technology ask:

> What concrete problem does this solve?

If the answer is unclear, do not introduce it.

Especially challenge Kubernetes, Kafka, RabbitMQ, NATS, Redis, service mesh, distributed databases, event sourcing, CQRS, dozens of microservices, container-per-service, custom binary protocols, and custom geometry engines.

Use them only when evidence demonstrates value.

---

# 83. Engineering Rule

Prefer:

```text
Simple + Measured + Replaceable
```

over:

```text
Sophisticated + Hypothetical + Permanent
```

---

# 84. Autonomy

Do not ask the human operator to resolve normal technical disagreements.

Agents should investigate, debate, prototype, benchmark, and decide.

Escalate only when the choice is fundamentally a business decision, licensing/commercial implications require human acceptance, requirements conflict, alternatives remain genuinely equivalent after investigation, or significant cost/product-direction trade-offs require human judgment.

---

# 85. First Execution Instruction

Start with **architecture discovery only**.

Do not create production implementation yet.

Perform the multi-agent analysis described above.

The first major deliverable is:

`docs/architecture-assessment.md`

Then create:

`docs/architecture-assumptions.md`

`docs/open-questions.md`

and the initial ADRs required to support the architecture assessment.

The Architecture Council must explicitly investigate the ArcGIS Server SOM/SOC/ArcSOC model and determine what a modern equivalent should look like.

After the first architecture proposal exists, run the Adversarial Architecture Reviewer against it.

Resolve or document every material criticism.

Then perform a second architecture review.

Only after that, present the recommended architecture.

---

# 86. Final Guiding Question

Throughout the entire project continuously return to this question:

> If ArcGIS Server, ArcSOC, GeoServer, MapServer, QGIS Server and the modern geospatial ecosystem had never existed as products, but we possessed all the lessons learned from them, how would we design the best enterprise GIS application server today?

Do not optimize for similarity to existing products.

Do not optimize for novelty.

Optimize for the technically strongest, simplest, most maintainable and operationally credible solution that satisfies the real requirements.

And whenever new evidence changes the answer:

**go back and reconsider the architecture.**
