# ADR-012 — Clustering

| | |
|---|---|
| **Status** | `REQUIRES PROTOTYPE` *(was `DEFERRED` until 2026-10-22, when the owner asked for the plan: "En son server clusteringi planla")* |
| **Confidence** | `MEDIUM` for the inventory, which is read from the code · `LOW` for every cost below, none of which is measured |
| **Decided** | 2026-10-22, the plan only — nothing in §5 is built. **The model is the owner's**: ArcGIS Server's multiple-machine site (§1c). **The purpose is the owner's**: *"İkinci düğüm work share için gerekli. Performans artırma için"* — a second node is for sharing the work, to raise throughput (§1a). The phases and their order are still INFERRED, for the owner to confirm. |

---

## 1. Context

Multi-node deployment (§52): shared state, coordination, request routing across nodes, rolling upgrades, split-brain behaviour. Deliberately deferred — §79 permits distributed infrastructure only once benchmarks and operational requirements demonstrate necessity, and the platform must work correctly on one node first. It is recorded now so that single-node decisions do not quietly make clustering impossible later.

### 1a. Why now, and what the problem actually is

The owner asked for the plan on 2026-10-22, after multi-tenancy (ADR-204) was built. §82's question first — *what
concrete problem does a second node solve?* ~~The first draft of this plan answered **not throughput** — one node
answered 942 requests a second at its measured peak (D-196) — and ordered the work by availability.~~ **The owner
answered it the same day: the second node is for sharing the work, to raise throughput** — *"İkinci düğüm work share
için gerekli. Performans artırma için."* Availability through a failure or an upgrade comes with it, as a by-product
rather than the reason.

**What that purpose makes measurable, and what it does not settle.** Two nodes over one PostgreSQL raise throughput
only where **the node** is what saturates — tile encoding, geometry work, serialising answers, TLS — and not where the
database is. D-196's 942 requests a second was a saturation test's peak and **does not say which of the two it hit**,
so condition 1 now measures that first. If PostgreSQL saturates first, a second node adds connections to a database
that is already the limit, and the store's capacity (a read replica, its own ADR) comes before any node.

The plan keeps **one PostgreSQL** as the only shared coordinator: no message bus, no cache cluster, no orchestrator
(§6 of the project rules).

### 1b. The inventory — what a second node would get wrong today

Read from the code on 2026-10-22 (`EveryLongLivedCacheIsBoundedTests` lists every long-lived structure; the hosted
services are `Program.cs`'s; the state folders are `Graticula:StatePath`'s). **No deployment has ever had two nodes**
(deployment.md §1.3), so every consequence below is reasoned, not observed.

| State | Where it lives | With two nodes | Phase (§5) |
|---|---|---|---|
| Catalogue, sharing, sessions, identity, tenants' rows, audit, jobs | PostgreSQL | shared — already right | — |
| Documents, attachments, styles, sprites | PostgreSQL | shared — already right | — |
| **Uploaded imagery** (`StatePath/imagery`) | node's disk | an image published on A is missing on B | 1 |
| **Exports** (`StatePath/exports`) — feature and tile packages | node's disk | a download link answered by the other node is 404 | 1 |
| **Recycle bin files** (`StatePath/recycle`) | node's disk | restoring an image service on the other node loses its file | 1 |
| **Backups** (`StatePath/backups`) and `BackupScheduler` | node's disk, every node | each node takes its own backup on schedule | 0, 1 |
| Thumbnails, DuckDB extensions, logs | node's disk | rebuilt or kept per node — harmless | — |
| **Tile cache** | node's disk | each node warms its own; cold misses multiply — **and an edit empties only the answering node's tiles** (ADR-186), so the other keeps serving the shape before the edit: a correctness fault, not a cost | 1 |
| **The GeoParquet / DuckDB root** (`Graticula:GeoParquetRoot`) | node's disk | a folder layer registered on A is missing on B | 1 |
| **Settings that must agree** — `PinnedServiceLimit`, `PerSourceConcurrency`, `TileCacheBudgetMB`, publishing from Pro, and the rest of `Graticula:*` | each node's configuration | a node with a different value behaves differently and says nothing | 0 |
| **The server's process log** | node's disk | the log screen shows the node that answered | 0 |
| **The scheme and host the server writes into absolute addresses** | `Request.Scheme`, `Request.Host` | behind a proxy ending TLS the nodes see `http`, and `owningSystemUrl`, `tokenServicesUrl` and every link say `http://`; a proxy that rewrites `Host` sends every organisation to the default one or a 421 | 3 |
| **Pro publishing staging** (`Staged` and its scratch folder) | memory and disk | Pro's addItem, addPart, commit and publish spread over nodes fail | 2 |
| **OGC Processes job store**, **ArcGIS append uploads**, **export pictures** | memory | a poll or a second step on the other node finds nothing | 2 |
| `ServiceContexts`, `CatalogFallback`, `InitialExtents`, the `ItemAnnotations` snapshot | memory | stale until their timers; a change made on A is late on B | 0 |
| `TenantDirectory` | memory | a tenant made on A is unknown on B until B restarts | 0 |
| **Pinned services** (ADR-218) — the pin in PostgreSQL, the warming and *when last warmed* per node | store and memory | every node warms every pinned service, which is right; a pin made on A wakes A at once and B within 20 s; the screen's *last renewed* is whichever node answered | 0 |
| `BasicCredentials`, the members' home cache, breakers, admission (`ConnectionBudget`) | memory | per node by design — each node bounds its own work | — |
| **Singleton sweeps**: `LogRetention`, `HistoryRetention`, `AttachmentSweep`, the recycle bin's expiry, `CoveragePyramids`, the usage flush, the warmers | every node | they run twice and race | 0 |
| Job workers (`TileSeeder`, `TileExporter`, `FeatureExporter`, the geodatabase importer and inspector) | PostgreSQL leases | already safe across nodes (`for update skip locked`, leases) | — |
| The server's certificate (`ServerIdentity`, in the state path) | node's disk | two certificates for one name | 3 |
| The platform pool budget against `max_connections` | per node | two nodes hold twice the connections | 0 |

### 1c. The model is ArcGIS Server's site — owner decision 2026-10-22

*"arcgis site mantığını ve clustering yapısını incele. ona göre clustering bizde de benzer olmalı."* So the terms, the
shape and the administration below follow ArcGIS Server's multiple-machine site, as its **public documentation**
describes it (cited in §4; nothing here is read from its implementation). An ArcGIS administrator should recognise
every noun.

**What an ArcGIS site is** (documentation): one or more GIS server *machines* that share one **configuration store**
and one set of **server directories** — caches, output, jobs, system, uploads, input, KML, indexes — on a network
location every machine can reach; a load balancer or **Web Adaptor** in front, which notices machines added and removed
and stops forwarding to one that is unavailable. Every machine runs the same version under the same account. A machine
joins from its own Manager (*Join an existing site*) or is added from another's; each has a configured state,
`STARTED` or `STOPPED`, an `underMaintenance` flag, *Synchronize with site*, and is *unregistered* when it is gone for
good. **Clusters** — groups of machines with services assigned to them — were deprecated at 10.4–10.6 and removed at
10.7; *single cluster mode*, the default since 10.4, turned off routing between machines and left it to the external
load balancer. Esri's advice for the isolation clusters gave is separate sites by type or size.

**How it maps here:**

| ArcGIS site | Graticula | Note |
|---|---|---|
| Site | A deployment: every node over one platform store | — |
| Configuration store (shared folder, on a fault-tolerant file server) | **The platform store, PostgreSQL** | Already shared, transactional and replicable; the one place ArcGIS recommends hardening is the one thing Graticula already has |
| Machine | **Node** — the same image, the same `Graticula:SecretKey` | the key is ArcGIS's *same account*: a node with a different key cannot read the others' secrets, so it is refused at join |
| Join an existing site | A node started on the store **registers itself** in a `node` table | no separate join step; the store is the site |
| Machine `configuredState`, Start / Stop | Node **started / stopped** — stopped drains: `/healthz/ready` answers 503 and the proxy stops sending | phase 0 |
| `underMaintenance` | Node **under maintenance** — stopped, and said to be on purpose | phase 0 |
| Machine status (is it actually up) | **Heartbeat** — each node writes `last_seen` every 10 s; *down* past 30 s | phase 0 |
| Unregister | **Remove a node** that is gone for good, from the list | phase 0 |
| Synchronize with site | Changes announced over `LISTEN`/`NOTIFY` (phase 0 item 2), so there is nothing to synchronise by hand | — |
| Server directories, registered and shared — `arcgiscache` among them | **Server directories** — imagery, exports, recycle files, backups, **the tile cache**, the GeoParquet root — under one shared path, listed in Server › Site and **checked from every node** | phase 1 |
| The site's settings, in the configuration store | **Site settings in the store**: what must agree across nodes is read from the platform store, or each node's values are hashed into its `node` row and a node that differs is refused at start | phase 0 |
| The load balancer's health check, `/rest/info/healthcheck` | **Answered**, from the same readiness as `/healthz/ready`, so an F5 or ALB template written for ArcGIS works unchanged | phase 0 |
| Logs queried across the site in Manager | **The server log in the store**, or the log screen asking every node, so one screen shows the whole site | phase 0 |
| Web Adaptor / load balancer, aware of machines | **Caddy**, with health checks on `/healthz/ready`; with containers, `dynamic a` upstreams so a node added is found without editing the file | phase 3 |
| Single cluster mode: no routing between machines | **The same: nodes never forward to each other** | — |
| Clusters (removed at 10.7) | **None.** Isolation is a separate deployment, ArcGIS's own advice and ADR-204 §9's | — |
| `minInstancesPerNode` | A pin (ADR-218), honoured on every node | — |
| Statistics and logs per machine | The request log and the server log carry the **node** | phase 0 |
| Same version on every machine | A node records its version; mixed versions only inside a rolling upgrade's expand/contract window (ADR-016) | phase 4 |

**Where it deliberately differs.** ArcGIS shares its configuration store as files and recommends a separate
fault-tolerant file server for them; here that store is the database every node already needs, so *where is the
configuration store* has one answer. And an ArcGIS machine runs a process per service instance; a node here runs every
service in one process (ADR-007), so there is nothing per machine to size but the node itself.

## 2. Alternatives considered

### A — Active–active stateless nodes behind the reverse proxy, one PostgreSQL, shared files *(preferred)*

Every node is the same image with the same configuration; Caddy (already in front on the VPS) balances and
health-checks them; PostgreSQL is the one coordinator, through what it already offers — row locks, `skip locked`
leases, advisory locks for singletons, `LISTEN`/`NOTIFY` for invalidation; files live on one shared mount.

**For.** No new component: the store, the proxy and the leases exist. It gives availability and upgrades without
downtime, and scale out comes with it. **Against.** Every row of §1b marked 0–2 must be done before it is safe, and a
shared mount is a new way to fail.

### B — Active–passive: a warm standby that takes over

One node serves; a second waits and is promoted by the proxy's health check. **For.** Most of §1b does not matter —
only one node runs at a time — so phases 1 and 2 shrink to *the standby can read the files*. **Against.** No rolling
upgrade, no scale out, and a failover is a cold node: empty caches and a cold tile cache at the worst moment. **Rejected
by the owner's purpose (§1a):** a standby shares none of the work.

### C — Affinity routing: each service or tenant pinned to a node

**Against.** ADR-029 already took affinity out of the default, and A-003 — the assumption it rests on — has no
validation route. It is what ADR-204 §9's *physically separate tenant* becomes if that is ever needed, not the general
answer. **ArcGIS walked the same road**: clusters, services assigned to groups of machines, were deprecated and removed
at 10.7 (§1c), and Esri's answer for isolation is a separate site.

### D — An orchestrator (Kubernetes) with its own discovery and storage classes

**Against.** On the project rules' challenge list by name; it solves scheduling and discovery, which two to four nodes
behind one proxy do not need, and makes the small deployment pay for it (§60).

## 3. Counterarguments to the preferred option

- **"Two nodes over one database add nothing if the database is the limit."** True, and it is the one argument that
  could make the whole plan buy nothing. Condition 1 measures which saturates first before phase 0 starts.
- **"Each node warms its own tile cache, so cold misses double."** ~~Answered by a proxy cache in phase 3.~~ Answered
  by sharing the cache instead, as ArcGIS shares `arcgiscache` (phase 1): one cache, warmed once and emptied once.
- **"A shared mount trades one failure for another."** True: a mount that hangs hangs every node's imagery reads. The
  file port of phase 1 keeps a local adapter, so a one-node deployment never mounts anything.
- **"PostgreSQL is still a single point of failure."** True, and out of this ADR's reach: replicating the store is the
  database's availability, decided separately. Two Graticula nodes over one PostgreSQL make the server tier available,
  not the data.
- **"Advisory locks and NOTIFY are distributed coordination by another name."** They are the smallest kind there is —
  no new process, released with the session — and the alternative, every sweep run once per node, is worse.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Durable platform state is in PostgreSQL | ADR-002 §5's inventory; §1b re-read from the code | this ADR |
| Job workers are safe across nodes | `PostgresJobStore.ClaimAsync`: `for update skip locked`, leases renewed and swept | code |
| Nothing coordinates the singleton sweeps | no `pg_try_advisory_lock` anywhere in `src/` | code, 2026-10-22 |
| The files that must be shared are under the state path | `imagery`, `exports`, `recycle`, `backups` | `Program.cs` |
| One node is fast enough for the scale target | 942 requests a second at the peak | D-196 |
| An ArcGIS site shares one configuration store and one set of server directories; a Web Adaptor tracks machines | public documentation | [Inside an ArcGIS Server site (10.6)](https://enterprise.arcgis.com/en/server/10.6/administer/windows/inside-an-arcgis-server-site.htm), [Configure a multiple-machine deployment (11.3)](https://enterprise.arcgis.com/en/server/11.3/deploy/windows/configuring-a-multiple-machine-deployment.htm) |
| Clusters were deprecated and removed at 10.7; single cluster mode leaves routing to the load balancer | public documentation | [Upgrade a multiple-cluster site (10.9)](https://enterprise.arcgis.com/en/server/10.9/deploy/windows/upgrade-a-multiple-cluster-site.htm), [About single cluster mode (10.6)](https://enterprise.arcgis.com/en/server/10.6/administer/linux/about-single-cluster-mode.htm) |
| A machine has a configured state, a maintenance flag, start, stop, unregister | public documentation | [Machine — ArcGIS REST APIs](https://developers.arcgis.com/rest/enterprise-administration/server/machine/) |
| **A restart's downtime, two nodes' behaviour, the tile fan-out's cost** | **not measured** | conditions 1–2 |

## 5. Decision — the plan

**Alternative A, in phases, each of which leaves a one-node deployment exactly as it was.** Nothing is built yet.

**Phase 0 — a node that is ready to have a sibling** (no new dependency)

1. **Singletons by advisory lock.** Each sweep of §1b takes `pg_try_advisory_lock(<its key>)` on a dedicated connection
   and runs only while it holds it; another node's sweep finds it held and sleeps. The backup schedule above all.
2. **Changes announced.** `TenantDirectory`, `ServiceContexts`, `ItemAnnotations` and `CatalogFallback` listen on the
   existing `graticula_grants` channel, or a sibling, for tenant, service and item changes and reload; their timers stay
   as the backstop. **A pin is one of the changes** (ADR-218): every node's `PinnedServices` wakes on it, rather than
   only the node the administrator happened to reach.
3. **The connection budget counts nodes.** A `Graticula:Nodes` setting (default 1) multiplies the startup check of
   ADR-204 condition 14, so two nodes on a store that cannot hold them are told so — **including the connection each
   node keeps open for every data source that serves a pinned service** (ADR-218).
4. **Ready means ready.** `/healthz/ready` answers 503 while a node drains and before its first tenant and catalogue
   read, so the proxy stops sending it requests first. **And before its pinned services are warm** (ADR-218): a node
   back from an upgrade is otherwise cold for exactly the services an administrator said must not be. Before it says it
   is ready, the node also sends itself one request and one query of each pinned service, which pays the ~240 ms every
   first request costs a new process and the query path's first run (ADR-218 condition 1) — the warm-up a single node
   cannot hide and a cluster can, because the proxy is still sending the other node the traffic.
5. **When each node last warmed a pinned service is kept per node.** Each node writes its own time to the store, and
   the screen says *kept warm on 2 of 2 nodes* rather than the time of whichever node answered — the case it has to
   show is one node unable to reach a table the other can.
6. **The site's machines** (§1c). A `node` table every node writes itself into at start — name, address, version,
   started at, `last_seen` every 10 s — with an administrator's *configured state*: started, stopped (drained: not
   ready), under maintenance; and *remove* for a node gone for good. **Server › Site › Machines** lists them with their
   real state (up, or down since its last heartbeat) beside the configured one, as ArcGIS Server Manager's Machines
   page does; the request and server logs carry the node, so a request is traced to the machine that answered it. A
   node started with a different `Graticula:SecretKey` from the site's is refused at start, ArcGIS's *same account on
   every machine*. On one node this is a list of one, which is still the right thing to show. *INFERRED:* whether
   the ArcGIS administration endpoints answer `/admin/machines` too, so scripts that list a site's machines see these.

**Phase 1 — server directories** (our code; no library adopted)

7. An `IFileStore` port with a local-disk adapter — today's behaviour — that also serves a shared mount; imagery,
   exports, recycle files and backups go through it. An object-store adapter (S3-compatible) only if a shared mount is
   not available somewhere it is needed — then a Tier 2 choice with its own ADR. They are ArcGIS's *server
   directories* (§1c): listed with their paths in Server › Site, and **checked from every node** — each writes a file
   the others must read — so a node whose mount is missing says so at start instead of losing an upload later.
   **The tile cache is one of them, as `arcgiscache` is in ArcGIS** — decided 2026-10-22 after the veteran's review
   found the plan said both: per node, an edit's emptying (ADR-186) reaches one node and the other serves the old
   tiles. Shared, there is one cache to empty. *INFERRED; revisit if a shared mount measures too slow for tile reads,
   and then the emptying is announced to every node over `NOTIFY` instead.*

**Phase 0, continued — what the veteran's review of 2026-10-22 added**

11. **Settings that must agree are the site's** (§1b): read from the store, or hashed per node and a differing node
    refused at start.
12. **`/rest/info/healthcheck` answers** `{"success": true}` from readiness — ArcGIS's documented load-balancer check.
13. **The server log is the site's**: kept in the store with its node, or gathered from every node by the log screen.

**Phase 2 — no state that spans requests in memory**

8. Pro's staging, the ArcGIS append uploads and the OGC Processes job store move to the job table and the file store,
   keyed by id, so any node answers the next step. The export pictures likewise.

**Phase 3 — two nodes behind Caddy**

9. Two containers of the same image, Caddy's `reverse_proxy` with `health_uri /healthz/ready`, and TLS at Caddy, so
   the nodes' own certificates no longer face clients. Caddy is the **Web Adaptor** (§1c): with containers its upstreams
   are `dynamic a` — the compose service's name — so a node added is found and one that stops answering is skipped
   without editing the Caddyfile; nodes never forward to each other, ArcGIS's single cluster mode. **The proxy says what
   it ended**: the nodes honour `X-Forwarded-Proto` and `X-Forwarded-Host` from the proxies the deployment names, and
   the proxy keeps `Host`, so absolute addresses stay `https://` and each organisation's host still reaches it — read
   nowhere in `src/` today. *Stickiness by client address for the multi-step paths of phase 2 (Pro's staging, append
   uploads, OGC Processes) is the bridge if phase 3 is wanted before phase 2 is done — INFERRED, not the plan.*

**Phase 4 — rolling upgrade**

10. Migrations are already expand/contract with a minimum reader (ADR-016 §4a). An upgrade drains one node, starts the
   new build on it — which migrates under its advisory lock — returns it, then does the other. A contract migration
   waits until no node older than its minimum reader runs; the store records each node's version when it starts.

## Conditions

1. **Measure where one node saturates**, on a mix of feature queries, uncached vector tiles and an export: the
   node's CPU against PostgreSQL's CPU and connections at the peak, on hardware where the two do not share cores. If
   the node saturates first, a second one is the answer and phase 0 starts; if PostgreSQL does, the store's capacity
   comes first and this plan waits for it. *(Rewritten 2026-10-22 when the owner set the purpose. It first asked how
   long a restart takes, the question for availability; that was measured on this PC — **2.41, 2.25 and 2.28 s** from
   a killed host to its first catalogue answer — and is kept as a record, no longer as the gate.)*
   **Measured 2026-10-22 on this PC, and the answer is "it depends on the work"**
   ([node-saturation](../../benchmarks/node-saturation/RESULTS.md)): three physical cores each, 840,596 buildings.
   Feature queries (1,389 req/s) and uncached vector tiles (1,851) fill **PostgreSQL** first — a tile is made in the
   store; map images (143 req/s, 41 ms of node CPU each) fill **the node**; a mix of the three fills both together,
   the node needing about 1.2 times the store's CPU. So a second node pays for rendering and is wasted on queries and
   tiles until the store grows with it. **Phase 0 starts on the owner's word**, with that ratio as the sizing rule.
   Not measured: a server-class machine, image and geometry services, a cached tile workload.
2. **A two-process prototype on one machine** — two hosts on two ports over one store — runs deployment.md §1.3's three
   checks (a sharing change, a revoked session, the tile fan-out) and §1b's expected failures (an image published on
   one and read on the other, a Pro publish split across them, a tenant made on one), so the table is observed rather
   than reasoned — **and measures condition 1's mix through both against one**, which is the number the owner's purpose
   is judged by.
3. **Each phase lands with a test that fails without it**, run against two processes, before the next starts.
4. **The owner confirms** the order of the phases and that PostgreSQL's own availability and capacity are decided
   separately. *(Alternative A over B is settled by the purpose the owner gave, 2026-10-22.)*

## 6. Consequences

**Positive.** A pinned service is warm on every node, and with phase 0's readiness an upgrade never sends it a
cold request. The work is shared, so throughput grows with nodes for as long as the node is the limit; upgrades and
a crashed process stop being outages, and the organisations of ADR-204 stop sharing one fate, as a by-product. Phases 0–2 are worth having on one node: singletons and a file port make one node's own
behaviour clearer.

**Negative.** Four phases of work before two nodes are safe; a shared mount to operate; Caddy's configuration grows; a
second node doubles the connections the store must allow (ADR-204 condition 14 already raised it to 300).

**State.** This ADR is about state: §1b is its statement of it.

## 7. Assumptions

| ID | Assumption | Status |
|---|---|---|
| — | One node saturates before PostgreSQL does, so a second node adds throughput | UNVALIDATED — condition 1 |
| A-086 | A deployment holds tens of tenants, not thousands | UNVALIDATED (ADR-204) |

## 8. Dependencies

**Depends on:** ADR-002, ADR-007, ADR-010, ADR-011, ADR-016 (expand/contract upgrades), ADR-026 (serving through a
store outage), ADR-029 (affinity not the default), ADR-204 (tenants, the connection budget)

**Depended on by:** Deployment profiles (§53), high availability (§52)

## 9. Revisit triggers

- Condition 1 finds PostgreSQL saturates first: the plan waits for the store's capacity, decided in its own ADR.
- A deployment needs a tenant physically separate (ADR-204 §9): that is a separate deployment, not a node.
- A shared mount is unavailable where it is needed: phase 1's object-store adapter, with its own ADR.

## 10. Dissent

None recorded. The case for B — simpler, most of §1b irrelevant — was real while the purpose was availability; the
owner's purpose, sharing the work, removed it.

---


