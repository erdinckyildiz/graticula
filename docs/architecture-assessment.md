# Architecture Assessment

**Status:** FIRST COMPLETE DRAFT — every section written. Not yet reviewed.
**Phase:** 0 — Architecture Discovery
**Required by:** §70, §85
**Next:** adversarial review (§85), then a fresh-challenger review (§67)

This is the primary deliverable of Phase 0. It synthesises nine research notes
and nine decided ADRs. Where a section summarises, the detail is linked; the job
here is judgement, not repetition.

---

## 1. Enterprise GIS problem definition

Stated without reference to any product, because if this cannot be written
convincingly nothing after it matters.

An organisation holds spatial data in databases. Many applications and people
need it. Without something in between, every consumer connects to the database
directly, and five things go wrong:

1. **Credentials sprawl.** Every application holds database credentials, usually
   the same ones, usually over-privileged.
2. **There is no per-layer authorization.** Database grants are per table. "This
   team may see roads but not incidents" has nowhere to live.
3. **Every consumer reimplements the hard parts** — tiling, generalisation,
   reprojection, pagination — badly, and differently from each other.
4. **Nobody can see who uses what.** The DBA sees connections, not consumers. A
   schema change breaks applications nobody knew existed.
5. **There is no publication boundary.** A table is not a product. Something must
   decide what is published, in what form, with what guarantees.

**A GIS application server is a governed publication boundary between spatial
data and its consumers.** That is the problem. Rendering, tiling and format
conversion are consequences of it, not the point of it.

This definition also bounds us. Where those five problems do not exist — one
consumer, public data, static content — a server is not the right answer, and we
should say so. See §10.

### Correction after fresh-challenger review (G2)

**That definition is true of hosted data and largely false of registered data**,
which [data-model.md](data-model.md) §3 designates as the normal case. Checked
honestly against the five problems above:

| Claimed | On registered data |
|---|---|
| Per-layer authorization | Partly delegated to row-level security where the provider supports it — we govern by asking the thing we are meant to govern |
| Audit and visibility | Incomplete by design: writes can bypass us (A-027) |
| A publication boundary | The schema changes under us; we detect drift and follow |
| Consistent interpretation | Best-effort cache coherence, bounded by a poll interval |
| Credentials | **Genuinely solved.** Consumers hold ours, not the database's |

One of five holds unconditionally.

So the honest statement is two products, not one:

- **Hosted data gets a governed publication boundary.** All five hold.
- **Registered data gets a governed *access* layer** — a capable read and write
  proxy with a catalog, a uniform API and one credential boundary.

Both are worth having. They are not the same claim, and several decisions
justified by the word "governance" need re-examining under the weaker one. This
is not a wording problem: it is the difference between owning a data lifecycle
and observing someone else's.

## 2. ArcGIS Server — strengths

Written charitably, because a strawman here poisons every comparison downstream.

- **A coherent service model.** A service is a first-class, named, versioned,
  administrable thing with a lifecycle. Most alternatives publish tables.
- **The administrator is a designed-for user.** Instance settings, recycling,
  health, capability toggles — an operational surface built deliberately.
- **The geodatabase.** Versioning, editor tracking, domains, subtypes,
  relationships, attachments. Implemented as a schema ArcGIS controls *inside
  the customer's database*, which is how referenced data gets advanced
  capability without ArcGIS owning the server. This is genuinely clever and it
  is the thing we most conspicuously will not have
  ([research/arcgis-datastore-model.md](research/arcgis-datastore-model.md) §4).
- **Reference-versus-copy as a first-class distinction**, with clear semantics
  for each.
- **Runtime schema evolution** on hosted layers, asynchronous because DDL is slow
  ([research/runtime-schema-evolution.md](research/runtime-schema-evolution.md)).
- **They corrected themselves in public.** SOM/SOC removed at 10.1, shared
  instances added at 10.7. Both are documented admissions that the earlier
  design did not hold, and they are the most useful evidence available to this
  project.

## 3. ArcGIS Server — weaknesses

Separating the inherent from the merely historical, which is the whole point
(§4).

**Inherent, or at least deeply structural:**

- **Per-service min/max instances as the primary control surface.** Guidance asks
  administrators to "pare down the number of running service instances" — a
  per-service manual task that does not scale past a few dozen services. Our
  A-008.
- **Instances *are* services**, so any configuration change means recycling
  processes. Our ADR-007 §4.6 avoids this by separating the context from the
  worker.
- **Shared instances are restricted** to map and image services with limited
  capabilities, geoprocessing excluded — which reveals that the sharing design
  was retrofitted onto a model that assumed dedication.

**Historical, and no longer applicable:**

- The SOM/SOC split and its separate machine roles and accounts. Esri removed it.
- Session-pinned non-pooled instances for stateful editing. That problem
  dissolved into database transactions and optimistic concurrency.
- Process-per-service memory cost as an unavoidable fact. It was a consequence of
  the era's runtime, not of the problem.

## 4. SOM / SOC / ArcSOC architectural analysis

The dedicated investigation required by §16. Full note:
[research/arcgis-som-soc.md](research/arcgis-som-soc.md).

§16 asks three questions. A fourth turned out to be more valuable and was
available free: **Esri changed this architecture twice — what and why?**

**At 10.1 the SOM/SOC split was removed** in favour of a site of peer machines,
citing robustness, fewer failure points and simpler provisioning and recovery. A
distinguished central manager was a liability. Our A-011.

**At 10.7 shared instances were added**, citing memory for sites hosting many
services. The incumbent converged on the hybrid model of §19 under production
pressure.

**The arithmetic behind that second change answers our §24 explosion test in
advance.** At `VERIFY` 100–200 MB per ArcSOC process and a minimum of one
instance per service, 1,000 services is roughly 150 GB resident merely to be
available. Process-per-service is excluded by arithmetic, not preference.

**The most useful finding is a reframe.** The documented limits on shared
instances — map and image only, geoprocessing excluded, `VERIFY` around 50
cached service contexts per instance — show that "shared versus dedicated" is
the wrong axis. The real one:

> How much per-service state must a worker hold, how cheaply does it bind and
> unbind, and does the workload tolerate a neighbour?

That question has different answers per workload class, which is where our
ADR-007 starts.

## 5. GeoServer — strengths and weaknesses

[research/runtime-models-compared.md](research/runtime-models-compared.md) §2.3,
§2.4.

A monolithic Spring servlet application, thread per request, one JVM. It caches
store connections, feature type definitions, external graphics, font definitions
and CRS definitions — **not data**. That list is a precise inventory of what
per-service warm state actually is, and it is small. Our A-015 comes from it.

**Strength: it works.** Maximum sharing, zero isolation, widely deployed,
successful. That is real evidence against assuming heavy isolation is necessary,
and it is why our A-007 is `CONTESTED` rather than assumed.

**Weakness: one heap, one GC, one leak away from taking down every service.** And
its file-based catalog does not survive multiple nodes — GeoServer Cloud needed
a message bus purely to keep catalog state consistent, which is the clearest
available argument for our ADR-002's database-backed platform store.

**GeoServer Cloud is a useful counter-example.** It decomposes by *protocol* — a
WMS service, a WFS service — motivated by cloud cost. Every pod still needs the
whole catalog and its own data access. Our §20 splits by *workload class*
instead, and GeoServer Cloud is the evidence for preferring that axis.

## 6. MapServer — strengths and weaknesses

Process per request under CGI; a daemon pool under FastCGI, described as
preserving memory caches and amortising "high startup costs (like heavy database
connections)".

`VERIFY` the reported gain is about 15 ms per request unless the installation has
latent components, "database connections, primarily".

**Strength: architecturally the cleanest isolation available.** Nothing survives
a request, so nothing leaks, and no request can corrupt another.

**Weakness: it pays full initialisation every time**, and the cost is dominated
by whatever had to be warmed. Since we are database-centric by definition, that
cost is exactly the expensive kind. Excluded for a specific measured reason
rather than for being old.

## 7. QGIS Server — strengths and weaknesses

**The most instructive failure mode in the comparison set.**

`VERIFY` QGIS classes are not thread safe, so multiprocessing is mandatory. A
library-level constraint dictated an entire product's process architecture —
which is why we made dependency thread safety a blocking precondition for
ADR-007 rather than an implementation detail
([research/dependency-thread-safety.md](research/dependency-thread-safety.md)).

And then the documented consequence: `VERIFY` "each Apache FCGI process has its
own set of cache", with requests "assigned randomly", so a request may land on a
process that has not warmed. **Fragmented warm state plus a blind router.**

That observation is the origin of our affinity routing idea (ADR-007 §4.4). It
is the gap nobody in the comparison set fills.

## 8. PostGIS-centric architectures

[research/postgis-thin-servers.md](research/postgis-thin-servers.md).

pg_tileserv states the design move plainly: "By restricting itself to only using
PostGIS as a data source, `pg_tileserv` gains the following features."

**The constraint is the feature.** From it, four subsystems we plan to build
simply disappear:

| We plan to build | They get free from PostGIS |
|---|---|
| Publishing architecture | Auto-discovery of every readable table |
| RBAC | Database roles and row-level security |
| Geoprocessing | Parameterised SQL as function layers |
| MVT encoder | `ST_AsMVT` |

**This is the sharpest challenge to our scope in the entire assessment**, and it
deserves a direct answer rather than "but we need providers".

**Where it stops:** every one of those deletions depends on there being exactly
one data source that happens to be a capable database. Add a GeoPackage, an
Oracle, a COG, and all four return at once. **Our provider abstraction is
therefore not a preference — it is the decision that forfeits the dividend**, and
the migration goal is what pays for it.

Two further gaps: cache lifecycle, where Tegola and Martin sit on opposite sides
of a seeding-versus-speed trade that a managed platform cannot pick between; and
**administration — the thin-server model has no administrator in it at all**,
which is precisely our stated primary user.

**Taken forward:** auto-discovery as a first-class publishing mode; **row-level
security delegation as an opt-in provider capability** — demoted from a takeaway
on 2026-08-12, because [security.md](security.md) §2.1 found our own
authorization was always going to exist: file providers have no RLS, not every
database will grant role-switching, and per-layer authorization is a question
the database cannot answer; function layers; `ST_AsMVT` as the PostGIS fast
path.

## 9. Modern geospatial server patterns

Common to Martin, Tegola, pg_tileserv, TiTiler and the OGC API generation:

- **Stateless request handling**, state in the database or in a cache.
- **Auto-discovery over manual publication** where the source can describe
  itself.
- **JSON and REST over XML and RPC.** OGC API Features versus WFS is the same
  transition, ten years later.
- **OpenAPI as machine-readable capability description**, which we exploit for
  the capability report (ADR-005 §3.4).
- **Formats as protocol.** COG, PMTiles and GeoParquet move capability from
  server to format, which is §10's argument in compressed form.
- **The container is the unit of deployment**, not the machine.

The pattern we are *not* adopting: decomposition into microservices by default.
§15 says modular first, and GeoServer Cloud is what the alternative costs.

## 10. Modern cloud-native geospatial patterns

Including the case that argues against our existence, because it is the section
the adversarial review should attack hardest.

COG, STAC, PMTiles, FlatGeobuf and GeoParquet move capability into the file
format. Range requests over HTTP replace a query protocol. A static object store
plus a capable client is a complete architecture for a large class of workloads.

**GeoLibre is the working proof.** Tauri, MapLibre GL JS, DuckDB-WASM Spatial and
deck.gl, running a full analysis stack in the browser: "no server, no install,
and no data ever leaving your machine". It is not hypothetical, it ships, and its
nearest counterpart is QGIS Desktop rather than ArcGIS Server.

**So the burden of proof is ours.** From §1, a server earns its place when:

- data changes, so caches must be invalidated rather than regenerated;
- access must be controlled per user and per layer — a bucket cannot do that;
- many services are managed by someone on behalf of many consumers;
- the query exceeds what a client can pull, so computation runs next to the data;
- editing is required, with transactions and audit;
- the environment is air-gapped or governed, where public object storage is not
  an option.

**Where none of these hold, the honest recommendation is to publish PMTiles and
skip the server.** A platform that cannot say that about itself is being assumed
rather than designed.

Note that our own vector-first decision moves us *toward* the client-side model
on the presentation side while keeping the governed boundary on the data side.
That is a coherent position, not a contradiction: we are not competing with the
client-side stack, we are the governed layer beneath it.

## 11. Legacy patterns to avoid

With a reason each. "Old" is not a reason.

| Pattern | Why avoided |
|---|---|
| Process per service with a warm minimum | Arithmetically dead at 1,000 services (§4) |
| A distinguished central manager process | Esri removed theirs citing robustness and recovery (A-011) |
| Per-service min/max as the primary control surface | Does not scale to our service count or our user (A-008) |
| Session-pinned instances | The problem moved into database transactions |
| Scheduled recycling by default | Conceals the leak it mitigates |
| File-based catalog with a runtime write API | Requires inventing a synchronisation mechanism; GeoServer Cloud needed a message bus |
| Heavyweight XML protocols as the native API | WFS's cost with none of its compatibility benefit — hence the compatibility layer |
| Server-side raster tile pyramids | Superseded by COG and range requests |
| Silent capability degradation | Makes cost invisible to the operator (ADR-008 §2) |

## 12. Language comparison

Summarises [ADR-001](adr/ADR-001-core-language.md). **Not decided — prototype
required.**

Narrowed to **Go and C#/.NET** for the prototype, along one axis: whether the
managed-runtime cost is real at our scale or a reflex. Rust and Java remain live
under written escalation triggers.

Criteria were reweighted after features-first and vector-first: database driver
quality and streaming rose to critical, rasterisation dropped out entirely, and
geometry access dropped from high to medium — then partly recovered when Oracle
and SQL Server became first-class, since `ST_AsMVT` is PostGIS-only and
in-process MVT encoding became the primary tile path.

`experiments/lang-slice/` measures three endpoints. The comparison between the
`ST_AsMVT` path and the in-process path is the one that matters: it settles
A-001, and it measures what the platform costs on non-PostGIS providers.

## 13. Geometry engine comparison

[research/geometry-projection-libs.md](research/geometry-projection-libs.md).
ADR-003 remains `DRAFT`, blocked on the language.

The family tree matters more than feature lists: **JTS is the reference
implementation, GEOS and NetTopologySuite are ports of it, and Rust's
`geo`/`i_overlay` is an independent lineage.** For three of four candidates,
choosing a language means choosing a port of the same algorithms, so semantics
and correctness heritage largely carry across. The real question is port lag.

Our lean is to own the hot-path primitives — clip, quantise, simplify, tile-space
transform — and adopt topology. That became more important, not less, when
in-process MVT encoding became mandatory.

`experiments/geometry-oracle` is worth more than it first appeared, because
**predicates are already evaluated by more than one GEOS build today** depending
on whether they push down (Q-20).

## 14. Rendering engine comparison

Largely moot. Vector-first removed server-side cartography from the core; the
client renders ([ADR-004](adr/ADR-004-rendering-engine.md) is `DEFERRED`).

What remains is rendering WMS images in the compatibility layer, from our own
vector tiles and our own MapLibre styles. **For that narrow job the earlier
objection to MapLibre Native inverts**: its lack of neutrality becomes the reason
to choose it, since a neutral rasteriser would require building the MapLibre
style interpreter that vector-first just removed. The headless X-server
requirement is now the main argument against.

## 15. Raster architecture options

[ADR-009](adr/ADR-009-raster-engine.md), decided.

**The server does not produce pixels.** Serve COG, convert other formats at
registration, refuse what cannot be converted. GDAL runs on isolated job workers
at registration, which is also where bomb checks belong.

Delivery is **proxied by default** with signed URLs as an optimisation, because
COG range requests are view-proportional and therefore affordable to proxy, and
because a proxy is the only option that works on every backend including a plain
filesystem.

The honest cost: this requires COG-capable clients, which excludes some of what
we intend to displace. Recorded as ADR-009's dissent.

## 16. Runtime architecture alternatives

[ADR-007](adr/ADR-007-service-runtime.md), decided. The headline:

> **Workers are sized to the machine, not to the catalogue.**

Two pools rather than §20's five classes — request workers, multi-tenant and
threaded; job workers, isolated. **Isolation on the workload axis, not the
service axis.**

Services do not start. Contexts bind lazily on first request and evict LRU under
a bounded budget, which dissolves §26's thundering herd rather than staging
around it, and makes idle services cost a table row instead of a process.

The unit of refresh is the context, not the worker — §17's layering finally
paying off concretely.

**Affinity routing is the new idea and the weakest part**, and it is marked as a
hypothesis with a prototype condition.

Modelled at 10 through 10,000 services, worker count, process count and
connection count are **flat**. What grows is catalog scale and monitoring
cardinality, which is the right place for the pressure.

## 17. Query architecture alternatives

[ADR-008](adr/ADR-008-query-engine.md), decided. The organising principle:

> **Never degrade silently.**

A domain AST with no SQL concepts, compiled per dialect, with capability
negotiation. Plan, negotiate, split into a pushed-down fragment and a residual —
and the residual is deliberately tiny, with everything else refused and
explained.

That defers the DuckDB compute layer, recorded as dissent rather than dressed up
as a conclusion: the deferral wins on discipline, not substance, and the evidence
that reopens it is a list of real refused queries.

Cancellation is a correctness requirement, because an abandoned query holds a
lock and a held lock is what blocks the DBA's DDL.

## 18. Proposed initial architecture

The synthesis.

```text
                        ┌──────────────────────────────┐
                        │ OGC API Features 1+2+3       │  native
                        │ + additive extensions        │
                        ├──────────────────────────────┤
                        │ Compatibility: WMS/WFS/WMTS  │  migration only
                        └──────────────┬───────────────┘
                                       │  protocol-neutral internal interface
                        ┌──────────────┴───────────────┐
                        │ Router — affinity aware      │
                        └──────────────┬───────────────┘
              ┌────────────────────────┴───────────────────┐
    ┌─────────┴──────────┐                      ┌──────────┴─────────┐
    │ REQUEST WORKERS    │                      │ JOB WORKERS        │
    │ multi-tenant       │                      │ isolated processes │
    │ threaded           │                      │                    │
    │ service contexts   │                      │ registration       │
    │  bound lazily      │                      │ validation (GDAL)  │
    │  evicted LRU       │                      │ seeding            │
    │  pinned if hot     │                      │ overviews          │
    │ L1 = context       │                      │ geoprocessing      │
    └─────────┬──────────┘                      └──────────┬─────────┘
              │            query engine                    │
              │   AST → plan → negotiate → split           │
              └────────────────────┬───────────────────────┘
                                   │
   ┌───────────────┬───────────────┼───────────────┬──────────────────┐
   │ PLATFORM      │ DATASTORE     │ REGISTERED    │ DERIVED          │
   │ STORE         │ hosted data   │ SOURCES       │ ARTEFACTS        │
   │ SQLite | PG   │ PostGIS /     │ PostGIS /     │ tiles, caches,   │
   │ metadata only │ MSSQL /Oracle │ MSSQL /Oracle │ generalisation   │
   │               │ we own schema │ / files       │ regenerable      │
   └───────────────┴───────────────┴───────────────┴──────────────────┘
```

**The decisions that define it:**

1. Workers sized to the machine, not the catalogue.
2. Isolation on the workload axis: request workers shared, job workers isolated.
3. Service definitions are durable state; contexts are disposable and bound
   lazily.
4. A domain query AST over capability-negotiating providers, refusing rather
   than degrading.
5. Vector-first: the client renders; the server serves tiles, features and COG.
6. A relational platform store, portable across four engines, files as an
   export format.
7. Three first-class spatial dialects; PostGIS is the fast path, not the
   assumption.
8. Tiles are the cache; coherence is best-effort for data we do not control, and
   documented as such.
9. Jobs in the platform store, no broker, with reserved capacity for interactive
   work.
10. Internal extension points, no plugin system yet.

**What is deliberately absent:** message broker, distributed cache as a
requirement, Kubernetes, service mesh, microservice decomposition, server-side
cartography, raster pixel pipeline, third-party plugin SDK. Each was considered
and each is recorded with the reason.

## 19. Deployment models

In priority order (§53). Kubernetes last, after the platform works without it
(§79).

| Profile | Shape |
|---|---|
| **Developer** | One binary, SQLite platform store, a local PostGIS or file providers. No datastore required. |
| **Single enterprise server** | One node, N request workers, job workers, platform store and datastore co-located in one database instance. **This is the target we design for.** |
| **Enterprise cluster** | Several peer nodes, shared platform store, no distinguished node. Cache bytes node-local by default; a cross-node miss costs a rebuild. Deferred to ADR-012. |
| **Kubernetes** | A packaging of the cluster profile. Nothing in the architecture requires it. |

**Air-gapped operation is a cross-cutting requirement, not a profile.** It needs
a concrete checklist — offline PROJ grids, GDAL driver data, bundled fonts and
glyphs, no telemetry, offline licence verification. Currently Q-15 and still a
slogan rather than a specification.

## 20. Security risks

| Risk | Mitigation | Where |
|---|---|---|
| SQL injection | Parameterise always; identifiers whitelisted from the service definition, never passed through | ADR-008 §4.6 |
| Filter parser abuse | Bounded nesting depth, term count, literal size — a parser without limits is a DoS primitive | ADR-008 §4.6 |
| Geometry bombs | Filter geometry bounded by vertex count and extent before reaching a provider | ADR-008 §4.6 |
| Malicious raster, decompression bombs | Validated at registration, in an isolated job worker, before cataloguing | ADR-009 §2.2 |
| Unauthorized imagery access | Proxied delivery by default; signed URLs only where explicitly enabled | ADR-009 §2.4 |
| Credential exposure | Secrets encrypted at rest, key supplied externally | ADR-002 §4.7 |
| Privilege escalation via two authorization models | Defer to row-level security where the provider supports it, rather than layering a second model that can disagree | §8 |
| Stale permissions in cache | Permission change is a *wrong*-class invalidation, purged rather than aged out | ADR-010 §5.1 |
| Plugin compromise | No third-party plugins | ADR-006 |
| Dependency vulnerabilities | Tier 2 ports allow replacement; licences and versions tracked | build-vs-adopt policy |

**The known gap:** the multi-node invalidation window (ADR-010 §7) means a
permission change can take up to the poll interval to propagate. That is a
documented disclosure window and it needs a number.

## 21. Performance risks

| Risk | Status |
|---|---|
| In-process MVT encoding too slow for SQL Server and Oracle | **A-019, critical.** If it fails, the multi-database promise is hollow |
| Affinity routing does not work, or degrades badly under skew | **A-014.** Fallback is blind routing — acceptable but not the design |
| Warm state is not small, so lazy binding is a latency problem | **A-015, load-bearing** |
| Connection budget breaks at scale on some provider | Q-04, benchmark registered |
| Seeding cannot absorb the provider gap | **A-020** |
| Shrink-to-zero pools cost too much cold latency | Benchmark registered |
| Cross-service interference in multi-tenant workers | Mitigated by per-service limits and timeouts, not eliminated |

Every one of these has a benchmark or experiment registered. **None has a
number.** That is the honest state of Phase 0.

## 22. Operational risks

Measured against the standing question: could a GIS administrator diagnose and
repair this at 2 AM?

- **"Observed, not configured" is the biggest risk.** Adaptive escalation is
  right for 1,000 services and wrong for an administrator asking why yesterday
  differed. The manual override is what keeps it honest (ADR-007 condition 4).
- **Four platform stores, three spatial dialects, two deployment shapes** is a
  large test matrix. An untested combination is a broken combination.
- **Best-effort cache coherence** will be read as a bug the first time someone
  edits a table directly and sees old tiles. Documentation is the only defence.
- **Registration as a job** makes the first five minutes worse for every new
  user (Q-46).
- **Monitoring cardinality** becomes the binding constraint before the runtime
  does (Q-12).
- **We can block a DBA's DDL** if the connection discipline is not right
  (ADR-007 §4.8, §5b).

## 23. Licensing risks

[DEPENDENCY-LICENSES.md](../DEPENDENCY-LICENSES.md) — **every entry is still
`UNVERIFIED`.** That is itself the largest licensing risk.

- **Oracle client libraries** are the highest-risk row. Historically restricted
  for redistribution. The customer's database licence is theirs; our right to
  ship a driver is ours to verify.
- **GDAL is a bill of materials, not a licence.** Drivers carry their own terms,
  some copyleft, some patent-encumbered. Must be enumerated per build.
- **PROJ grids, EPSG data and fonts** carry terms separate from the code.
- **Our own licence is not chosen.** Copyleft is acceptable to the owner; AGPL's
  network clause is the one that matters most for a server product.

## 24. Assumptions

[architecture-assumptions.md](architecture-assumptions.md). Thirty-four recorded; one invalidated (A-009, PostgreSQL as a hard dependency); one validated
(A-013, dependency thread safety); three supported by prior art; the rest open.

**The load-bearing ones:**

| ID | Assumption | If wrong |
|---|---|---|
| A-019 | In-process MVT encoding meets latency targets | Non-PostGIS providers cannot serve tiles; the multi-database promise is hollow |
| A-015 | Warm per-service state is small | Lazy binding and LRU eviction both become expensive; ADR-007 weakens substantially |
| A-014 | Affinity routing works | We become GeoServer with extra steps |
| A-027 | Concurrency can be correct against writes we never see | Silent lost updates — the worst defect class an editing API can have |
| A-024 | Refusal plus a capability report is acceptable to users | ADR-008's central choice is user-hostile |

## 25. Unresolved questions

[open-questions.md](open-questions.md). Forty-six recorded, sixteen answered.

**Still blocking, and owner-owned:**

- Q-29 — which platform stores ship in v1
- Q-17 — whether an ArcGIS-compatible REST surface is offered
- Q-16 — whether migration tooling is in scope or API compatibility only
- Q-32 — whether the datastore is implemented in v1 or later
- Q-08 — the data ownership model

**Still blocking, and ours:**

- Q-01 — the language, pending the prototype
- Q-03 — where each class of geometry work executes
- Q-04 — the connection budget with real numbers
- Q-44 — the write surface, given Part 4 is a draft

## 26. Recommended experiments

Each names the decision it settles. An experiment that decides nothing is not
run.

| Experiment | Settles | Priority |
|---|---|---|
| `experiments/lang-slice` | ADR-001, A-001, A-019 | **First.** Everything downstream waits on it |
| `benchmarks/mvt-generation` | A-019, A-021 — and what a tile costs on Oracle | **First.** Can share the lang-slice harness |
| `experiments/geometry-oracle` | ADR-003, Q-20 — and it already matters, since predicates hit two GEOS builds today | Second |
| `benchmarks/connection-budget` | Q-04, ADR-007 §4.8, including cold-pool cost | Second |
| `experiments/affinity-routing` | A-014 — ADR-007's weakest point | Third |
| `benchmarks/worker-model` | A-015, cross-service interference | Third |
| `benchmarks/tile-seeding` | A-020 | Fourth |
| `benchmarks/feature-query` | Streaming at scale, per dialect | Fourth |

**A dataset is a precondition for all of them.** Real data, not synthetic:
polygon, point and line layers at million-feature scale with realistic vertex
distributions. Synthetic uniform data makes every implementation look good and
hides exactly what we need to see.

## 27. Implementation roadmap

Phases from §71–§79, with the evidence required to enter each.

| Phase | Content | Entry requirement |
|---|---|---|
| **0** | This document, ADRs, experiments | Adversarial review (§85), fresh-challenger review (§67) |
| **1** | Walking skeleton: PostGIS → provider → query engine → OGC API Features, **plus a minimal second dialect compiler in CI** (F1) | ADR-001 decided by prototype. ADR-003 decided. |
| **2** | Standards: Parts 1+2+3 properly, capability report, admin API foundations | Phase 1 stable |
| **3** | Vector tiles: in-process encoder, cache, seeding | A-019 and A-021 measured |
| **4** | ~~Rendering~~ → **Second provider.** Bring SQL Server or Oracle online | The abstraction is only real when a second implementation exercises it |
| **5** | Raster: STAC catalog, registration, COG proxy | Q-27 implemented; A-032 measured |
| — | **Not in v1: WMS.** Rendering raster map images stays out (Q-47). The compatibility layer is WFS, plus WMTS carrying vector tiles. | Reopens only as a product capability, not a legacy adapter |
| **6** | Administration: full admin API, service lifecycle, observability | — |
| **7** | Security: authentication, authorization, RBAC | — |
| **8** | Processing: geoprocessing jobs over the existing job engine | — |
| **9** | Distributed runtime | Benchmarks and operational requirements demonstrate necessity (§79) |

**Two deliberate departures from §71–§79.**

**Phase 4 is not rendering.** Vector-first removed it, and the slot is better
spent bringing a second spatial provider online as a *supported* provider.

**Amended after adversarial review (F1).** "As early as it can" was not a
commitment, and the whole multi-database decision rests on it. So a **minimal
second dialect compiler exists in Phase 1** — not a supported provider, a
forcing function: enough to compile the walking skeleton's queries against SQL
Server and run them in CI.

The rule adopted: **no query engine feature is complete until it compiles on two
dialects.** Without it, everything written about capability negotiation is
unfalsifiable until Phase 4, by which time the engine has a year of
PostGIS-shaped assumptions and nothing pushing back.

**Editing is not a phase.** It appears in §28's requirements and is in scope, but
it has no phase of its own in §71–§79. It should land with Phase 2 or Phase 4,
and A-027 — concurrency correct against writes we never see — must be settled
before any of it ships.

---

## What this assessment does not yet contain

Stated plainly, because a document that hides its gaps is worse than one that has
them.

- **No numbers.** Every performance claim is a hypothesis with a registered
  benchmark and no result. §21 is a list of risks, not findings.
- **No adversarial review.** §85 requires one before this is presentable, and §67
  requires a second from someone uninvolved.
- **No entity model.** [data-model.md](data-model.md) covers where things live,
  not what they are. Service and layer definitions, stable identity, fields,
  domains, subtypes and relationships are unwritten.
- **No security review of the whole**, only per-decision mitigations (§20).
- **No verified licences.**
- **The write path is a known gap in ADR-008.**
