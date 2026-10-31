# ADR-222 — Geoprocessing is built-in analysis tools, served as ArcGIS's Spatial Analysis Service

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` — the protocol is publicly documented and copied as documented; which clients find it, and how, is measured per client in the conditions, and three of the five places it could be found are UNCONFIRMED in public documents |
| **Decided** | 2026-10-29, by owner direction — *"konu kalmazsa geoprocessing konusuna başlayabilirsin. arcgis'ten referans alabilirsin"*, then *"pardon geoprocessing, geocoding de önemli de öncelikli değil"*. ~~**The scope this ADR draws — built-in tools only, no user-supplied code — is INFERRED**~~ and listed for confirmation in [Q-192](../open-questions.md), with four smaller inferences beside it (§5.8). **Answered 2026-10-29: user tools come into scope too** — this ADR is the built-in half; see Alternative D. |
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

***Reversed 2026-10-29 by the owner** ([Q-192](../open-questions.md)): *"Kullanıcı araçları da gelsin"*. User tools come into scope; they are not built by this ADR, whose tools stand as the built-in half, and they need their own decision — the sandbox (Q-75), the SDK and how data crosses (Q-74), who may publish one (ADR-015 §7: administrators by default). Nothing user-supplied runs until that decision exists.*

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
5. **What a tool does not do it refuses by name**, never approximates: ~~`sideType` other than Full, `endType` Flat, a
   feature collection input, a result without `outputName`~~ — all four built on 2026-10-29 (conditions 3 and 4); what
   is refused by name now is a registered layer or another server's as an input, `recordsToMatch.groupByFields`, an
   overwrite of a layer that keeps attachments or a history, and a grouped summary written over an existing layer.
6. **The result is a new hosted feature layer** in a new service named by `outputName.serviceProperties.name`, owned
   by the caller, private, in their root folder, and the task's result parameter answers
   `{"paramName": …, "dataType": "GPString", "value": {"url": "<layer url>", "itemId": "<item id>"}}` — the documented
   shape, with the item id beside the url as the gp-research §3.2 gap allows.
7. **A job is the caller's**: another member's job id answers as no job, as every job route here does (ADR-037).
8. **INFERRED, for confirmation in Q-192:** ~~(a) the scope — built-in tools, no user code;~~ *(answered 2026-10-29 the other way: user tools come into scope too, by their own decision — Alternative D);* (b) the privilege — running an
   analysis takes `content:publishFeatures`, since its output is a hosted layer, and holding it is what the portal
   reports as `premium:user:spatialanalysis`, with no privilege of its own; (c) geodesic buffers for every reference,
   where ArcGIS documents geodesic for geographic data; (d) the output service private and in the root folder.
9. ~~**INFERRED on 2026-10-29, for confirmation in [Q-193](../open-questions.md)**~~ **Confirmed 2026-10-29 by owner decision in Q-193:** *"Kalsın"*. — choices ArcGIS's documents leave open:
   (a) the output field names, lower case as every hosted column here: `point_count` for points and `count` for lines and
   polygons, `sum_length_<unit>` and `sum_area_<unit>`, `<statistic>_<field>`, `join_id`, `majority_<field>` and
   `minority_<field>`, `percent_points` and `percent_shape`; (b) a grouped summary's table published in the result's
   service as `<name>_groups`; (c) bins drawn in Web Mercator scaled to the ground at the middle of the features, a
   hexagon's size the distance between its flat sides as documented; (d) `outputType` Line and Point taken from the
   layers' linework, a polygon by its boundary; (e) a result returned unsaved, and a collection sent in, at most 50,000
   features; (f) a summary drawn by its count in up to five equal classes from the start; (g) `JoinFeatures` one-to-one
   without summaries keeps the first match by `recordsToMatch`'s order or the join layer's id.

## Conditions

1. **The protocol answers as documented** — service, task, submitJob, job, result, input and cancel resources, with
   the documented status words and shapes — tested against a running server. **DISCHARGED 2026-10-29** by
   `SpatialAnalysisServiceTests`: the service and task resources, `portals/self`'s `helperServices.analysis` and the
   `premium:user:spatialanalysis` word, `execute` refused as asynchronous, an unknown job id answered as no job,
   `submitJob`'s `{jobId, jobStatus: esriJobSubmitted}` with a `j` and 32 hex digits, the job's `results` as
   `paramUrl`s, the result as `GPString` with `{url, itemId}`, and `cancel` refused for a job that has ended.
2. **`CreateBuffers` and `DissolveBoundaries` make a hosted layer from a hosted layer** with the documented fields,
   tested end to end through `submitJob`, and the result readable through its FeatureServer. **DISCHARGED 2026-10-29** by
   `SpatialAnalysisServiceTests`: two distances over the suite's polygon layer make two buffers per feature with
   `buff_dist`, `orig_fid` and `analysisarea`; dissolved by `buff_dist` they make one feature per distance whose `count`
   is the number of features and whose `sum_analysisarea` is above zero; both read back through the new layers'
   FeatureServer. Built as SQL in the datastore (`PostGisImporter.Analysis.cs`), filled by a parameterised insert into a
   table made empty with the source's own column types, since PostgreSQL binds no parameters in `create table … as`.
   **Field names are lower case** — `buff_dist`, not ArcGIS's `BUFF_DIST` — as every hosted column here is; a client
   that reads them case-sensitively will not find them, and aliases are the repair if one does.
3. **`OverlayLayers`, `MergeLayers`, `AggregatePoints`, `SummarizeWithin`, `JoinFeatures` and `FindCentroids`** are
   built under the same rules. **PARTLY DISCHARGED 2026-10-29** — the first three, tested in `SpatialAnalysisServiceTests`
   by relations the fixture cannot move: each building meets its own buffer in an intersection, and its fields sit
   beside the buffer's with a clash renamed `_1`; a buffer less the buildings leaves one ring per buffer; the union of
   the two holds at least a piece per building and per ring; two polygon layers merged hold both counts; and each
   point's own buffer counts at least that point. `OverlayLayers` takes `Intersect`, `Union` (polygons only) and `Erase`,
   `outputType` `Input` only; `MergeLayers` takes `Remove`, `Rename` and `Match` and refuses text matched into a number,
   as ArcGIS does; `AggregatePoints` takes a polygon layer and `keepBoundariesWithNoPoints`, and refuses bins and a
   group-by field by name. `FindCentroids` the same night: a point per line or polygon, on it with `pointLocation`, stored as a point as ArcGIS's are, its feature's fields kept. ~~**Open:** `SummarizeWithin`, `JoinFeatures`, bins.~~
   **DISCHARGED 2026-10-29, the rest of the six and every option they document** — `SpatialAnalysisSummariesTests`, by
   relations the fixture cannot move. **Bins** (Square and Hexagon, a size and unit) in `AggregatePoints` and
   `SummarizeWithin`: every point in exactly one bin, no empty bin kept. **`SummarizeWithin`**: points counted; lines'
   length and polygons' area inside each area, in any documented unit; summary fields weighted by the part inside, as
   documented — every parcel kept, and no more building area inside one than it covers. **`JoinFeatures`**: the seven
   relationships, `WithinDistance` on the ground, equal attributes, one-to-one with a count, summaries or the first match,
   one-to-many, Inner and Left — a one-to-many join has one row per pair the one-to-one join counted, a left join keeps
   every target. **`groupByField`** in both summaries, with `minorityMajority` and `percentPoints`/`percentShape`: the
   group table's counts add up to the points, each area's shares to 100, the majority is the value with the most. **The
   options this condition had refused by name**: `sideType` Left and Right of a line and Outside a polygon, `endType`
   Flat, for a single distance as documented — one side is about half the whole, flat ends less than round, outside less
   than the whole buffer; `outputType` Line and Point of an intersection; `snapToInput` and `tolerance`. Each summary is
   drawn by its count from the start (design review). The ArcGIS API for Python runs `aggregate_points` into hexagons,
   `summarize_within` and `join_features` too (`tools/sdk-probe/python-analysis.py`, ten tools counted). **Open:
   `ExtractData`**, which §5.3 lists with these: its result is an item holding a file, which is a feature export
   (ADR-106) feeding a document item (ADR-188) — two job systems chained, not an analysis — and it is not built.
4. **Inputs beyond this server's hosted layers** — a feature collection, a registered PostGIS table — or the reason
   they are not. **DISCHARGED 2026-10-29.** **A feature collection** — `{layerDefinition, featureSet}`, with or without a
   layer definition, a `filter` beside it — is read when the job is submitted, so a malformed one is refused before it is
   queued, and written to a table of the run's own, which every tool reads exactly as it reads a hosted layer and which
   is dropped when the run ends however it ends. **And out**: a request without `outputName` gets its result back as a
   feature collection and keeps nothing, as ArcGIS documents — a grouped summary's table as a table collection beside
   it. **A registered PostGIS table, or another server's layer, is refused by name, and that is the reason:** the tools
   run as SQL in the datastore where hosted tables are; a registered table is in another database, and reading it in
   would be an import the caller did not ask for, under no quota. A client that has such a layer sends its features as
   a collection, which is what Map Viewer does with a layer it cannot name by URL. Also built the same day:
   **`outputName`'s overwrite** (`itemProperties.itemId`, `serviceURL` or `name` with `overwrite: true`) puts the run's
   table in place of the layer's in one transaction — the service, layer and item stay; refused for another's layer, a
   layer of another kind, a service of more than one layer, and a layer keeping attachments or a history.
5. **Map Viewer's *Analysis* pane runs `CreateBuffers` against this server**, measured in the browser; what it reads
   before showing the pane is recorded. *(Not measurable as written, found 2026-10-29: Map Viewer is ArcGIS Online's and
   ArcGIS Enterprise's own application, served by their portal, and it runs analysis through **its** portal's
   `helperServices.analysis` — there is no way to point it at this server's without hosting Esri's application, which
   §5 of CLAUDE.md and Tier 3 forbid. The client that can be pointed here and runs the same service is **ArcGIS Pro's
   *Standard Feature Analysis* toolbox (`arcpy.sfa`)**, present on the owner's machine (Pro 3.7) with `CreateBuffers`,
   `SummarizeWithin` and the rest; it uses Pro's **active portal**, which is the owner's working portal and cannot be
   restored without their credentials, so the measurement waits for the owner's word. Open.)* **Measured 2026-10-29 with the owner's word, and the answer is a wall.** A standalone arcpy process's `SignInToPortal` changes the active portal for that process only: the owner's portal was the active one again when it exited. Pro refuses a plain-HTTP local portal (`GetPortalDescription` answers *is invalid*), so the showcase was used over HTTPS. There `arcpy.sfa.CreateBuffers` signs in, reads `helperServices.analysis`, and then imports the service as a **SOAP** toolbox: `<portal>/services;System/SpatialAnalysisTools`. The server's log shows `GET /studio/services?f=json` answered and `POST /studio/services`, the SOAP catalog, answered 404. Pro's script is `geoanalyticssoap.py`. **Pro's analysis toolbox needs ArcGIS Server's SOAP interface, which this server does not serve**, and v1-scope names REST faces only. Whether to build a SOAP face is a scope question for the owner. *(Deferred 2026-10-29 by owner decision, "Şimdilik hayır": the scope stays REST. Pro's analysis toolbox is a stated limit, and the ArcGIS API for Python runs the same tools over REST (condition 6). This wakes if a SOAP face is ever decided.)*
6. **The ArcGIS API for Python's `create_buffers` runs against this server**, measured. **DISCHARGED 2026-10-29**, and
   wider than asked: `tools/sdk-probe/python-analysis.py` signs in with a name and password, lets the API find the
   service through `helperServices.analysis`, and runs `create_buffers`, `dissolve_boundaries`, `overlay_layers`,
   `merge_layers` and `aggregate_points` with the API's own defaults — five hosted layers, counted (8, 1, 6, 20 and 12
   features on the fixture, API 2.4.3). **It found two faults first, both fixed the same hour.** The API reads every
   item by `POST content/items/<id>`, which this server answered 405 — a gap older than analysis, since
   `gis.content.get` takes the same path — so every portal resource now takes POST as ArcGIS's do. And it builds each
   function from the task resource's parameter list, so `overlay_layers` and `aggregate_points` were refused in Python
   before a request was sent while `snapToInput`, `tolerance` and the bin parameters were missing from it; every
   documented parameter is listed now, and a value asking for what is not built is refused by name.
   `SpatialAnalysisServiceTests` holds both.
7. **Studio offers the tools** on a layer's page and in the map, designed with the ux-designer agent. **PARTLY
   DISCHARGED 2026-10-29 — the layer's page.** *Analyze…* in a hosted layer's More menu, for a member who may publish
   hosted features: the tools that fit the layer, in Map Viewer's order (Aggregate points, Create buffers, Dissolve
   boundaries, Merge layers, Overlay layers), each with its own questions; a second-layer list holding only the kind its
   tool can use, read from each candidate's own document; the result's name under the tools, checked on its field; Run
   that keeps focus while busy; Stop, which cancels the job; closing mid-run said in a toast, and its end in another;
   Done as a link to the new layer that takes focus. Two ux-designer passes: the first sent it back (no layout, a
   polygon list whose first entry was a point layer so every Aggregate failed, Cancel that silently let the layer be
   made, focus lost on every run, the server's parameter names in its sentences), the second shipped it with fixes, all
   taken. `AnalyzeDialogTests`. ~~**Open: in the map** — Studio's map has no analysis pane yet.~~
   **DISCHARGED 2026-10-29 — the rest, in the map.** *(Marker re-spelled 2026-10-31: written as* The rest DISCHARGED *it was invisible to `tools/conditions.py`, which reads a discharge only where the emphasis opens on the word, so the register counted this condition open for two days after it was met.)* The map's tool rail has **Analysis** beside Measure: one of the map's
   hosted layers, the same tools, and the result added to the map as a layer, with a link to it in Content. The tools are
   one script, `analysis.js`, that the item's dialog and the map both run, each saying only how it asks the server,
   how it tells the reader and what a result becomes — a second copy would be D-130's shape. The dialog gained Join
   features, Summarize within and bins the same day. Design review, two passes: the first sent it back (a tool list that
   filled the first screen, form borders at 1.23:1, a footer divider drawn with a colour the page never defines, unaligned
   rows, a live region rebuilt with its text, relationships in words no GIS user uses, one flat colour for a summary);
   the second (see the review record) passed it. `AnalyzeDialogTests`, `WebMapAnalysisTests`.

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

*The owner did say otherwise, on 2026-10-29: the inference was narrower than the intent. Six tools were built on it by then; none of them is wrong under the wider scope, which adds user tools beside them rather than replacing them.*
