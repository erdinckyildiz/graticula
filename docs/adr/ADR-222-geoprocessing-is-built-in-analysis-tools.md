# ADR-222 — Geoprocessing is built-in analysis tools, served as ArcGIS's Spatial Analysis Service

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` — the protocol is publicly documented and copied as documented; which clients find it, and how, is measured per client in the conditions, and three of the five places it could be found are UNCONFIRMED in public documents |
| **Decided** | 2026-10-29, by owner direction — *"konu kalmazsa geoprocessing konusuna başlayabilirsin. arcgis'ten referans alabilirsin"*, then *"pardon geoprocessing, geocoding de önemli de öncelikli değil"*. **The scope this ADR draws — built-in tools only, no user-supplied code — is INFERRED** and listed for confirmation in [Q-192](../open-questions.md), with four smaller inferences beside it (§5.8). |
| **Supersedes** | [v1-scope](../v1-scope.md) §3c's *"GPServer … remain out"*, for GPServer only and for tools this server writes; the rest of §3c stands |
| **Superseded by** | — |

---

## 1. Context

The README's limits say *"No geoprocessing and no geocoding. No GPServer, no web tools, no Python toolboxes."* v1-scope
§3c cut GPServer together with the Python SDK, the sandbox and the user wheel set, under one heading — *user-supplied
code* — and for one reason: review finding O3, *arbitrary code execution against a server holding the organisation's
spatial data*, with [Q-75](../open-questions.md)'s sandbox *"the largest security surface in the product by a wide
margin"*.

That reason is about **whose code runs**, not about the protocol. A GPServer is a REST shape — a service, its tasks,
`execute`, `submitJob`, a job to poll and results to fetch — and ArcGIS Enterprise ships one that runs no user code at
all: the portal's **Spatial Analysis Service**, `System/SpatialAnalysisTools/GPServer`, whose tasks (`CreateBuffers`,
`DissolveBoundaries`, `OverlayLayers`, …) have fixed, documented parameters, run asynchronously over hosted feature
layers, and write their result as a new hosted feature layer. Map Viewer's *Analysis* pane, the ArcGIS API for Python's
`arcgis.features.analysis` and Esri's own REST walk-through all reach it through `portals/self` →
`helperServices.analysis.url`. The research behind this ADR, every fact with its public URL and every gap marked, is
[gp-research.md](../research/gp-research.md).

This server already holds everything that service needs: hosted layers in its own PostGIS, a job record with claims and
leases (ADR-011's first increment, ADR-037), a catalogue that publishes a datastore table as a hosted layer
(`IAdminCatalog.PublishLayerAsync`, the geodatabase import's path), a where-clause parser that emits parameterised
PostgreSQL (ADR-008), and the portal document a client reads first.

## 2. Alternatives considered

### Alternative A — The Spatial Analysis Service's own shape, tools written here (chosen)

The service at the address Esri documents, with its task names, parameter names, result names and result shapes, each
tool written here as SQL over the datastore. A client written for ArcGIS Enterprise's analysis needs no change.

### Alternative B — A generic GPServer whose tasks are ours under our own names

The same protocol with tasks named for this server. Every client would need to be told where it is and what the tasks
take; Map Viewer and the Python API's analysis functions would find nothing, since they look for the analysis helper
by its documented role and call its tasks by their documented names.

### Alternative C — OGC API – Processes

ADR-011 names it as the job system's public face. It is the open standard for the same idea, and no ArcGIS client
speaks it. It remains open as a second face over the same runner; it is not the first, because the owner's reference is
ArcGIS and the clients this server is judged by are ArcGIS's.

### Alternative D — User toolboxes (Python), sandboxed

What ArcGIS calls web tools. Rejected for v1 as v1-scope §3c rejected it, for the same reason, which this ADR does
not reopen.

## 3. Counterarguments to the preferred option

- **Copying a product's tool names copies its tool semantics.** A `CreateBuffers` that buffers differently from
  ArcGIS's — planar where ArcGIS is geodesic, a different `AnalysisArea` unit — is worse than a tool named differently,
  because a user will not think to check. Answer: the semantics are documented per parameter and per output field
  ([gp-research.md](../research/gp-research.md) §3.4–3.9); each tool is tested against those documents, and where this
  server does less it refuses by name rather than doing something else (§5.5).
- **Analysis in the serving database competes with serving.** A dissolve over a national layer is a long statement on
  the database the maps read. Answer: it runs as a job, one at a time per server, with the statement timeout every job
  has, and the connection comes from the datastore's own pool; ADR-011's fair-share scheduler is the remedy when two
  kinds of long work contend, and this is the evidence ADR-011's increment said it would wait for.
- **SQL built from parameters.** Every field name is checked against the layer's described fields and quoted; every
  value is a parameter; a `filter` is parsed by the same SQL-92 parser `query` uses. No text from the request is
  pasted into a statement.

## 4. Evidence

- The protocol and the tasks: [gp-research.md](../research/gp-research.md), from developers.arcgis.com's REST reference
  and the analysis task pages, read 2026-10-29.
- What cannot be confirmed from documents and must be measured against the client: the task JSON a real
  `SpatialAnalysisTools` answers with, what Map Viewer reads before showing *Analysis*, how Pro finds portal tools, and
  which URL the Python API calls (gp-research.md §6). Each is a condition below rather than a claim.

## 5. Decision

1. **A GPServer at `/rest/services/System/SpatialAnalysisTools/GPServer`**, answering the documented service, task,
   `submitJob`, `jobs/<jobId>`, `jobs/<jobId>/results/<param>`, `jobs/<jobId>/inputs/<param>` and
   `jobs/<jobId>/cancel` resources, as JSON and as `pjson`. Every task is asynchronous, as the analysis service's are;
   `execute` answers that the task is asynchronous, in the GP error shape.
2. **`portals/self` names it** as `helperServices.analysis.url`, beside the geometry service it already names.
3. **Tools are this server's code** — Tier 1, the query engine's — and run as SQL in the datastore, where hosted layers
   are. Built first: **`CreateBuffers`** (distances or a field, units, `dissolveType`, `ringType` Disks and Rings,
   geodesic for every reference, the documented output fields `BUFF_DIST`, `ORIG_FID`, `AnalysisArea`) and
   **`DissolveBoundaries`** (`dissolveFields`, `summaryFields`, `multiPartFeatures`, a `Count` field). The rest of the
   self-contained set — `OverlayLayers`, `MergeLayers`, `AggregatePoints`, `SummarizeWithin`, `JoinFeatures`,
   `FindCentroids`, `ExtractData` — follow under the conditions.
4. **Inputs are this server's hosted layers**, named by URL with an optional `filter`, read only by a caller who may
   read them. A registered database's layer, another server's URL and an inline feature collection are refused by
   name, for now (condition 4).
5. **What a tool does not do it refuses by name**, never approximates: `sideType` other than Full, `endType` Flat, a
   feature collection input, a result without `outputName`.
6. **The result is a new hosted feature layer** in a new service named by `outputName.serviceProperties.name`, owned
   by the caller, private, in their root folder, and the task's result parameter answers
   `{"paramName": …, "dataType": "GPString", "value": {"url": "<layer url>", "itemId": "<item id>"}}` — the documented
   shape, with the item id beside the url as the gp-research §3.2 gap allows.
7. **A job is the caller's**: another member's job id answers as no job, as every job route here does (ADR-037).
8. **INFERRED, for confirmation in Q-192:** (a) the scope — built-in tools, no user code; (b) the privilege — running an
   analysis takes `content:publishFeatures`, since its output is a hosted layer, and holding it is what the portal
   reports as `premium:user:spatialanalysis`, with no privilege of its own; (c) geodesic buffers for every reference,
   where ArcGIS documents geodesic for geographic data; (d) the output service private and in the root folder.

## Conditions

1. **The protocol answers as documented** — service, task, submitJob, job, result, input and cancel resources, with
   the documented status words and shapes — tested against a running server.
2. **`CreateBuffers` and `DissolveBoundaries` make a hosted layer from a hosted layer** with the documented fields,
   tested end to end through `submitJob`, and the result readable through its FeatureServer.
3. **`OverlayLayers`, `MergeLayers`, `AggregatePoints`, `SummarizeWithin`, `JoinFeatures` and `FindCentroids`** are
   built under the same rules.
4. **Inputs beyond this server's hosted layers** — a feature collection, a registered PostGIS table — or the reason
   they are not.
5. **Map Viewer's *Analysis* pane runs `CreateBuffers` against this server**, measured in the browser; what it reads
   before showing the pane is recorded.
6. **The ArcGIS API for Python's `create_buffers` runs against this server**, measured.
7. **Studio offers the tools** on a layer's page and in the map, designed with the ux-designer agent.

## 6. Consequences

- The README's *no geoprocessing* becomes *built-in analysis, no user tools* the day condition 2 is met.
- A new job kind (`analysis.run`) and a table for each run's parameters, results and messages, behind the tenant
  floor as every job table is.
- Long analyses run on the database that serves maps; ADR-011's scheduler is now owed when the two contend.
- **State.** In the catalogue: a `job` row per run and an `analysis_job` row with its task, its parameters as sent,
  its results and its messages, shared by every node; the result layer is an ordinary hosted layer, service and item.
  At runtime: nothing beyond the run in progress, held by the node that claimed it under the job lease.

## 7. Assumptions this decision rests on

- That clients reach analysis through `helperServices.analysis.url` (documented for the REST walk-through; UNCONFIRMED
  for Map Viewer, Pro and the Python API — conditions 5 and 6).
- That this server's datastore is PostGIS 3.x with geography buffers (ADR-002).

## 8. Dependencies

ADR-011 and ADR-037 (jobs), ADR-008 (where clauses), ADR-018 (reading), ADR-040 (the portal document), ADR-204 (the
tenant floor on every job table).

## 9. Revisit triggers

- A customer whose analysis is user code: that is Q-75 and ADR-006, not this ADR.
- Two kinds of long job contending on one server: ADR-011's scheduler.
- A client that finds the service somewhere other than `helperServices.analysis.url`.

## 10. Dissent

None recorded. The scope is an inference from a two-word instruction, and Q-192 exists so that the owner can say
otherwise before it is built further than its first two tools.
