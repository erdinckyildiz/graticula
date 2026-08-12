# Runtime Models Compared — GeoServer, MapServer, QGIS Server, GeoServer Cloud

**Status:** FIRST PASS — architectural argument solid; figures marked `VERIFY`.
**Companion to:** [arcgis-som-soc.md](arcgis-som-soc.md)
**Feeds:** [ADR-007](../adr/ADR-007-service-runtime.md),
[ADR-010](../adr/ADR-010-caching.md), assessment §5–§7, §16
**Clean room:** publicly documented behaviour only (§5). Sources at the end.

---

## 1. Why compare these together

The ArcSOC investigation looked at one system in depth. This note looks across
four more, because the interesting result is not any single design — it is that
**all of them are answering the same question, badly, in different directions.**

## 2. The four models

### 2.1 MapServer — process per request (CGI), or a daemon pool (FastCGI)

Classic `mapserv` CGI spawns a process per request. Nothing survives between
requests: no warm connections, no parsed mapfile, no cached anything.

FastCGI was the retrofit: "a protocol for keeping cgi-bin style web applications
running as a daemon to take advantage of preserving memory caches, and
amortizing other high startup costs (like heavy database connections) over many
requests."

`VERIFY` The reported gain is modest — "worth about 15 ms per request" — *unless*
the installation has latent components, "database connections, primarily."

**What this tells us:** process-per-request is architecturally clean (perfect
isolation, no shared state, no leaks that outlive a request) and its cost is
dominated by *what you had to warm up*. With no database, 15 ms. With database
connections, much worse. Since our platform is database-centric by definition,
the CGI model is excluded — but for a specific, measurable reason, not because
it is old.

### 2.2 QGIS Server — mandatory multiprocessing, fragmented cache

`VERIFY` "QGIS server classes are not thread safe, so a multiprocessing model
should be used when building scalable applications."

A library-level thread-safety constraint dictates the entire process
architecture. With `spawn-fcgi` the process persists between requests; with
`fcgiwrap` a new process starts per request, re-reading and re-parsing the QGIS
project file every time.

And then the consequence that matters most to us:

> `VERIFY` "each Apache FCGI process has its own set of cache, so if you have
> 5-10 parallel Apache threads or processes each thread/process has its own
> cache" — with incoming requests "assigned randomly", so a request may land on
> a process that has not yet populated its cache.

**Two lessons.** First: **a Tier 2 dependency's thread-safety can dictate our
entire runtime model.** GDAL, GEOS and PROJ thread-safety must be verified
explicitly before ADR-007 is decided — this is not a detail to discover during
implementation. Second, see §3.

### 2.3 GeoServer — one JVM, thread per request

"A traditional, Spring Framework based, monolithic servlet application." Each
request gets its own thread, with a configurable `ThreadPoolExecutor` (core pool
size, max pool size, keep-alive).

Notably: "GeoServer does not cache data, but it does cache connections to
stores, feature type definitions, external graphics, font definitions, and CRS
definitions."

That cache list is worth reading closely — it is a precise inventory of what
*per-service warm state* actually consists of in a GIS server. Not data.
Connections, schema, symbology resources, fonts, projections. We will have the
same list, and it is small enough to be interesting: if warm state is mostly
these, binding and unbinding it may be far cheaper than ArcGIS's
process-per-service model assumed.

**Trade-off:** maximum sharing, zero isolation. One heap, one GC, one leak away
from taking down every service. The exact opposite of ArcSOC, and it is worth
noting that GeoServer is widely deployed and successful with this model — which
argues that A-007 (crashes really happen) should be tested rather than assumed.

### 2.4 GeoServer Cloud — decomposition by protocol

Each OWS service, the Web UI and the REST API become "self-contained,
individually deployable, and scalable microservices", synchronised by a
`RemoteGeoServerEventBridge` over a message bus (`VERIFY` RabbitMQ in the
Docker Compose deployment).

The stated motivation is deployment economics: monolithic GeoServer "does not
leave much room for scalability or redundancy", and fixed instance counts cause
"over-provisioning and inefficient budget use on public cloud platforms."

**Three observations, and the third is a design decision for us:**

1. The motivation is **cloud cost**, not architecture. That is a legitimate
   reason, but it is a different problem from ours — our baseline is one server,
   not a public-cloud bill.
2. It requires a **message bus for catalog synchronisation**. §82 names exactly
   this as the kind of infrastructure that must justify itself. Here it is not
   solving a GIS problem — it is repairing the consistency that decomposition
   broke.
3. **The decomposition axis is protocol** — a WMS service, a WFS service. That
   is the wrong axis, and it is a useful counter-example for us. WMS and WFS
   over the same data have genuinely different resource profiles, but splitting
   by protocol means every pod still needs the whole catalog and its own data
   access. **Our §20 proposal splits by workload class** — feature, tile,
   render, raster, geoprocessing — which tracks the actual resource pressure.
   GeoServer Cloud is evidence for preferring the workload axis, and this
   belongs in the assessment.

## 3. The pattern nobody solves — where warm state lives

Line the five systems up, including ArcGIS, and one question runs through all of
them:

> **Where does warm per-service state live, and how does a request find it?**

| System | Where warm state lives | Failure mode |
|---|---|---|
| MapServer CGI | Nowhere | Every request pays full initialisation |
| MapServer FastCGI | A daemon pool | Which daemon holds what is uncontrolled |
| QGIS Server | Per worker process | **Fragmented; random routing misses it** — documented |
| GeoServer | One shared heap | No fragmentation, no isolation; one leak or GC pause hits everyone |
| ArcGIS dedicated | Instances bound to a service | Memory: ~150 GB at 1,000 services |
| ArcGIS shared | Bounded per-instance context cache (`VERIFY` ~50) | The same fragmentation problem, merely bounded |

Every design is a different answer to the same question, and **each answer
trades cache locality against isolation.** Nobody gets both.

### What is missing from all of them

**Routing that knows which worker is warm for which service.**

QGIS Server's documented problem is not that caches are per-process — that is
inherent to multiprocessing. The problem is that "incoming requests are assigned
randomly". The cache is fragmented *and* the router is blind to the
fragmentation. ArcGIS's shared instance pool has the same shape: a bounded
context cache per instance, with no documented mechanism for preferring the
instance that already holds a given service's context.

If the router knows which workers hold which service contexts, cache
fragmentation stops being a defect and becomes a **managed resource**:

- route a request for service X preferentially to a worker already warm for X;
- fall back to any worker with capacity, accepting a cold bind;
- treat "service contexts per worker" as an explicit, bounded budget — the thing
  ArcGIS's ~50 number is, but with routing that respects it;
- let the observed warm-set drive escalation to a dedicated worker, rather than
  an administrator's guess at publish time.

This directly answers §80.10 ("how does request routing work?") and it is not a
question ADR-007 was previously asking with enough force. It is now the most
promising specific idea to come out of the research phase, and it needs to be
prototyped and measured rather than believed.

**Caveat before anyone gets attached to it:** affinity routing trades load
balance against cache locality. Under skew — one very hot service — affinity
concentrates load on the warm workers, which is the opposite of what a load
balancer should do. The policy has to degrade to plain balancing under pressure,
and that boundary is exactly the kind of thing that looks obvious on a
whiteboard and behaves badly in production. Benchmark it.

## 4. The isolation question, reconsidered

Placing the five systems on an isolation axis:

```text
MapServer CGI        QGIS Server      ArcGIS         GeoServer
process/request  →   process/worker → pool/service → one heap
   most isolated                                    least isolated
   coldest                                          warmest
```

GeoServer sits at the least-isolated extreme and is nonetheless widely deployed
and successful. That is real evidence against assuming heavy isolation is
necessary, and it should make us test A-007 rather than accept it.

The reconciliation that fits all the evidence: **isolation should be targeted at
what actually faults.** GeoServer is pure JVM code — memory-safe, no segfaults,
and a leak shows up as an OOM rather than a corrupted process. ArcGIS and QGIS
Server run large native stacks. Our platform will have both: managed code for
most paths, and native code (GDAL especially) on the raster path, processing
untrusted files.

That argues for a **heterogeneous** answer — shared workers for managed-code
workloads, process isolation specifically for the native-code paths — which is
the §20 worker-class hypothesis reached from a third independent direction.

## 5. Consequences

**For [ADR-007](../adr/ADR-007-service-runtime.md):**

- Add affinity routing as an explicit design element, with the per-worker
  service-context budget as a named parameter (§3 above).
- Add thread-safety verification of every Tier 2 dependency as a *precondition*
  of the decision. QGIS Server's entire process model is a consequence of one
  library not being thread safe; we must not discover our equivalent late.
- A-007 (crashes really happen) is now genuinely contested: GeoServer's success
  is evidence against it for managed-code paths, and ArcGIS/QGIS are evidence
  for it on native paths. The resolution is per-path, not global.

**For [ADR-010](../adr/ADR-010-caching.md):** GeoServer's cache inventory —
store connections, feature type definitions, external graphics, font
definitions, CRS definitions — is a concrete starting list for what L1 actually
holds. Combined with §3, L1 design and routing design are the same problem and
cannot be decided separately.

**For the assessment:** GeoServer Cloud is the counter-example that justifies
splitting by workload class rather than by protocol (§2.4), and it is a live
demonstration of §82 — decomposition created a consistency problem that then
required a message bus to repair.

## 6. Still to investigate

- `VERIFY` GDAL, GEOS and PROJ thread-safety guarantees, precisely and per API.
  This is now a blocking item for ADR-007, not a footnote.
- MapServer's FastCGI process management guidance — maximum requests per
  process, leak handling. `mapserver.org` returned 403 to direct fetches; find
  another route.
- How GeoServer handles a single misbehaving layer or store — is there any
  containment at all, or does one bad store take the JVM?
- Whether any of these systems implements warmth-aware routing already. If one
  does, §3 is less novel than it looks and we should learn from their
  implementation instead of inventing it.

## Sources

- [GeoServer 2.28 User Manual — Status](https://docs.geoserver.org/stable/en/user/configuration/status.html)
- [GeoServer About & Status — a practical guide](https://geoserver.org/tutorials/2024/01/17/geospatial-techno.html)
- [GeoServer performance workshop — JVM tuning](https://github.com/planetfederal/workshops/blob/master/workshops/geoserver/performance/source/jvm/index.rst)
- [GeoServer Cloud documentation](https://geoserver.org/geoserver-cloud/)
- [GeoServer Cloud — Event Bus](https://geoserver.org/geoserver-cloud/developer-guide/event-bus/)
- [geoserver/geoserver-cloud on GitHub](https://github.com/geoserver/geoserver-cloud)
- [MapServer — FastCGI optimization](https://mapserver.org/optimization/fastcgi.html) (fetch returned 403; summarised from search results)
- [mapserver-users — Using FastCGI to alleviate poor performance](https://mapserver-users.osgeo.narkive.com/5p8Bakxa/using-fast-cgi-with-mapserver-to-alleviate-poor-performance)
- [QGIS Server manual — Getting started](https://docs.qgis.org/3.44/en/docs/server_manual/getting_started.html)
- [qgis-developer — Server performance questions](https://lists.osgeo.org/pipermail/qgis-developer/2016-February/041538.html)
