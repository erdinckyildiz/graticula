# ADR-299 — ArcGIS Pro publishes a map image layer by reference to a registered database

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-11-10, **by owner decision**: *"yap"* ("do it"), after asking whether Pro could publish with *Reference registered data* when Pro and the server use the same database with the same user. **2026-11-12**, by owner decision, the database user must match as well: *"veritabanı kullanıcısı ise çalışmaması lazım"* ("if it is the database user, it must not work [when it differs]"). **2026-11-13**, `REQUIRES PROTOTYPE` → `ACCEPTED WITH CONDITIONS` by this ADR's own first revisit trigger (§9), which named the move in advance: 00147 was passed. Not a new owner decision. **2026-11-13**, the remaining work built (§5.5, §5.6) after the owner's *"kalan işleri de tamamla"*; §5.6 is **INFERRED** from it. |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

Pro publishes to Graticula by copying data: a hosted feature layer
([ADR-191](ADR-191-arcgis-pro-publishes-hosted-feature-layers.md)) uploads the rows. ArcGIS has a second way. When the
data is in a database that the server already knows (a *registered* data store), Pro's *Share As Web Layer* with
**Reference registered data** sends no rows. It sends a service definition (`.sd`) that names the database connection
and the layer's query, and the server serves the layer from the database in place.

Graticula has registered data sources (`data_source`), so the owner asked for this path. This ADR records what Pro
asks, what Graticula answers, how a registered source is matched, and where the work stops today.

## 2. Alternatives considered

### Alternative A — answer the federated-server protocol Pro uses (chosen)

**Argument for.** It is the only road Pro takes. A reference publish runs through the federated server's
connection: admin REST, SOAP at `/arcgis/services`, and the `PublishingTools` GP service. Nothing else is offered to
a Pro user.

**Argument against.** The protocol is large and only partly documented. Every document Pro reads must be answered in
a shape Pro accepts, and the analyzer reports a failure without naming the call that caused it.

### Alternative B — a Graticula-side "register a layer from a database table" in Studio

**Argument for.** Already possible: a registered source's table becomes a service from Studio without Pro.

**Argument against.** It is not what was asked. The Pro user's map, symbology and query stay in Pro.

### Alternative C — match registered databases by full connection, with a second publisher connection

ArcGIS lets a registration carry two complete connections, one for the publisher and one for the server.

**Argument for.** It covers a publisher whose database name or user differs from the server's.

**Argument against.** Two stored credentials per source, and the second one is never used to read data by this
server. See §5 for what was chosen instead.

## 3. Counterarguments to the preferred option

- **The binary message format is not built.** Two real servers answer `GetMessageFormats` with
  `esriServiceCatalogMessageFormatSoapOrBin`. Graticula answers `…Soap` only, because the binary format is not
  publicly documented (CLAUDE.md §5). If Pro needs the binary format for a later step, this road is closed.
  Measured 2026-11-12: offered both, Pro switches `PublishingTools` to the binary format — so the SOAP answer is the
  one that keeps that job readable.
- **The server describes its own licence in ArcGIS words.** `admin/system/licenses` answers with a GIS Server
  edition and features so that Pro's analyzer accepts the server. This does not give anybody an Esri licence and
  does not license Pro ([ADR-285](ADR-285-graticula-does-not-license-arcgis-pro.md) stands): it describes what this
  server serves. A reader could still see it as a claim to be an Esri product. **INFERRED** — listed for the owner.
- ~~**It may never finish.** See §4: analyzer error 00147 is not solved.~~ **Passed 2026-11-13** (§4): it was this
  server's own routing bug, not a licence check. What remains is building the publish itself (Conditions).

## 4. Evidence

Measured with arcpy (Pro 3.7) against dev.graticula.com, and against two real ArcGIS Servers (esriqa.aecon.com 12.0,
read and write allowed; gis.blm.gov 11.5, read only) for the shapes of the answers.

| Claim | Evidence | Source |
|---|---|---|
| Pro asks `GET /arcgis/admin/generateToken` with the portal token in a header, and a real server answers `{"ssl":{…}}`, not a token | wire log; real answer | 2026-11-10 |
| Without a SOAP `GetMessageVersion` answer, the analyzer stops at 00290 | four POSTs, nothing after them | 2026-11-10 |
| Pro then reads: admin root, `system/directories`, `services/properties`, the MapServer type document, `services/types/extensions`, `machines`, `system/licenses`, `data/config`, `data/findItems` (four kinds), `services/canCreateService`; and SOAP `PublishingTools` (`GetExecutionType`, `SubmitJob` AvailableFonts, status, messages, result) | wire log | 2026-11-10, 2026-11-12 |
| `data/findItems?parentPath=/enterpriseDatabases` is the registration check | real answer lists the registered egdb with its connection string | esriqa.aecon.com |
| The `.sd` carries the connection (`SERVER`, `DATABASE`, `USER`) and the layer's SQL | `manifest.xml`, the map's CIM | staged drafts |
| A PostGIS table in a database that is not a geodatabase is always a query layer | Pro behaviour | 2026-11-10 |
| Analyzer 00147 "GIS server role is not licensed" stays on the query layer after licences, machines and the MapServer type were answered with copies of a real server's answers, and with the registration removed | stage results | 2026-11-10 |
| Pro asks `portals/{id}/subscriptionInfo` with the origin-hash id from before ADR-216 and got 400 | wire log; hash of `https://dev.graticula.com/studio` | 2026-11-12 |
| A real portal lists its server's `adminUrl` as the server base, and answers `subscriptionInfo` with `orgCapabilities: []` | real answers | esriqa.aecon.com, 2026-11-12 |
| **00147 is not about references and not about query layers.** It stays when the same query layer is staged with `copyDataToServer=True`, and a file-geodatabase point layer staged as a map image layer (data copied) gets it too: Pro does not accept this server for any map image layer | stage results | 2026-11-12 |
| 00147 stays with every call answered 200 (the `PublishingTools` fonts job included), with the portal fixes of 2026-11-12, with the machine's `platform` as `Windows`, with eight GIS/Image Server feature names in `system/licenses` — **and with `system/licenses` answered `{}`.** The licence document's contents do not decide it | stage results, wire log | 2026-11-12 |
| Offered `…SoapOrBin`, Pro speaks the binary format (`application/octet-stream`) to `PublishingTools` and nothing else changes; 00147 stays | wire log, recorded call | 2026-11-12 |
| Pro's binaries do not contain `isServerLicensed`; the licence is read only through `system/licenses` (string search, not disassembly) | Pro 3.7 install | 2026-11-12 |
| Aecon's real 12.0 server answers `canCreateService` for MapServer with "not supported with your current ArcGIS Server license" (403), so the licence answers copied from it may be an unlicensed server's | real answer | esriqa.aecon.com, 2026-11-10 |
| **Knock-out sweep** (each answer replaced in turn by an ArcGIS error body, dev's recorded-answer tools): of the admin documents only the root matters, and it turns 00147 into 00290; `system/directories`, `services/properties`, the MapServer type and its extensions, `services/types/extensions`, `machines` and the machine, `system/licenses`, `data/config`, `data/findItems`, `canCreateService`, `subscriptionInfo`, `sharing/rest/info` and `PublishingTools` leave 00147 standing | stage results | 2026-11-12 |
| **00147 is not raised at all** — the draft stages — when any one of three portal documents is unreadable: `portals/self/servers`, `/sharing/rest` (only when it has no `currentVersion`: any value from 7.1 to 2026.1 keeps 00147), or `community/users/{me}`. So the check runs only for a known portal, a listed server and a known user; these three gate it, they do not decide it | stage results | 2026-11-12 |
| Variants that keep 00147: the server entry's `serverRole` (HOSTING, FEDERATED, restricted), `serverFunction` (absent, `""`, `GISServer`, `GeneralPurposeServer`), `isHosted`, `name` case; `rest/info` naming the portal as `owningSystemUrl` and its `generateToken`; the user with `portal:publisher:publishServerServices`, with every privilege a real 12.0 administrator has, and as `GISProfessionalAdvUT`; licence `version` 12.0/11.5/11.0 and `canExpire` true | stage results | 2026-11-12 |
| Pro reads four fields of a server entry: `url`, `name`, `serverFunction`, `serverRole` (strings in `AGSClient.dll`) | Pro 3.7 install | 2026-11-12 |
| **The rest of the publish, measured past 00147** by staging with `/sharing/rest` unreadable and uploading: `admin/services/` → `admin/services/{name}.MapServer` (404 = free) → `POST admin/uploads/upload` twice, the second carrying the `.sd` → SOAP `SubmitJob` on `PublishingTools`, `ToolName=PublishServiceDefinition`, values: the uploaded item id, an empty configuration override, `{"buildInitialCache":false}` → job status, messages, result → `admin/services/{name}.MapServer` again → `/arcgis/rest` | wire log, recorded envelope, the kept `.sd` | 2026-11-12 |
| **Pro (the application, not arcpy) refuses earlier, with 002901 "the target portal for publishing does not match the currently active portal"**: the draft records `PORTALURL=https://dev.graticula.com/` from `/arcgis/rest/info`'s `owningSystemUrl`, and Pro's active portal is `…/studio/`. With `owningSystemUrl` and `tokenServicesUrl` on `/studio` (and `portals/self` saying `portalHostname: dev.graticula.com/studio`, `customBaseUrl: studio`) the draft says `/studio/` and Pro reaches 00147 like arcpy. **So the desktop application cannot publish a map image layer to this server at all today, independently of 00147** | in-Pro stage (Python window, Diagnostic Monitor on), sddraft | 2026-11-12 |
| **The licence document's contents are not used.** `edition: svrbasic` with a Basic feature — which Pro must refuse map image layers for, by its own message *"not supported based on the server's license edition"* — gives exactly the same 00147 as Advanced, as `{}` and as an error body | stage results | 2026-11-12 |
| No managed exception is raised in Pro during the analysis (dotnet-trace, `DotNETRuntime:0x8000`): the decision is native and silent; the Diagnostic Monitor at debug level logs only `AGSServerConnection2::GetDiscoveryRestInfo` and `CDiscoveryClientJSONHelper::GetDataBaseUrl` | trace, `ArcGISProLog-*.xml` | 2026-11-12 |
| **Disclosure.** On 2026-11-12 strings were searched inside Pro's installed DLLs (names such as `DiscoveryServerLicense`, `svrbasic`, `FedServerNoRequiredLicenseForMapImage`), which a recorded working rule forbids ("do not open Pro's binaries", §5). Nothing was disassembled or decompiled and no code was reproduced; the rows above that rest on it say so. It stopped when noticed | this ADR | 2026-11-12 |
| **The cause of 00147, 2026-11-13: `admin/services/canCreateService` was read as a folder name.** The admin route took any `services/{name}` without a dot for a folder, so `canCreateService` (and `services/properties`) answered `{"status":"error","messages":["Folder 'canCreateService' does not exist."],"code":404}` with HTTP 200, and the `{"status":"success"}` written for it in the reference-publish answers was never reached. Pro reads that body as the server refusing a MapServer and reports it as 00147. Serving `{"status":"success"}` there **alone**, every other answer at baseline, stages the file-geodatabase layer; after the routing fix (commit `be3ec869`), with **nothing** canned on dev, both the file-geodatabase layer and the by-reference query layer (`copyDataToServer=False`) stage | stage results, wire log | 2026-11-13 |
| **Correction to the knock-out row above:** knocking `canCreateService` out replaced one error body with another, so it could not show anything; the row's conclusion for that call was wrong. The three gating portal documents (row after it) are the ones without which Pro does not reach the `canCreateService` reading at all | this ADR | 2026-11-13 |
| **How it was found: a real federated server that accepts the layer.** A production ArcGIS Enterprise 12.1 the owner has access to, read-only, at the owner's instruction that night (*"… üzerinden yap"*, "do it through …"): its admin documents, `rest/info`, SOAP `GetMessageVersion`/`GetMessageFormats`, the `PublishingTools` fonts job and its portal's documents were read, and the same file-geodatabase layer was **staged only** against it (no upload, no service) — it stages. Then each of its answers was served from dev in Pro's place (hosts rewritten, licence identifiers, people's details and the organisation's name removed): with every admin document from it, Pro's draft switches the map service's `provider` from `ArcObjects11` to `DMaps` (the shared-instance pool) but 00147 stays; with every portal document and the real fonts list too, 00147 stays; the remaining difference, the one dev never answered as written, was `canCreateService` | stage results, sddraft diff, wire log | 2026-11-13 |
| Pro's web requests for sharing are not made through .NET: an EventPipe trace of `System.Net.Http` on the desktop application while it stages carries no request at all, so a managed trace cannot show what Pro asks a server; the wire log on dev remains the instrument | trace | 2026-11-13 |
| **The publish, built and run end to end, 2026-11-13.** With the upload kept and the `PublishServiceDefinition` job answered (§5.5), arcpy's `UploadServiceDefinition` of the by-reference query layer succeeds: the job's own messages say *"Published ProRef_Dev by reference to 1 registered database"*, and the service draws (`export` 200 PNG), queries (20 rows from the registered table on the server, not the publisher's copy) and lists the one column Pro's query selected besides the geometry — the other two are hidden | arcpy messages, REST answers | 2026-11-13 |
| Pro asks the job's result by name: `GetJobResult` with `ParameterNames` `out_soap_svc_url` and `out_rest_svc_url`. Answered with the service's two addresses, `UploadServiceDefinition` reports them as its first two outputs and fills a third with the layer list (`cities\|0`) it read from the service itself; its item-id outputs stay empty | recorded envelope, arcpy outputs | 2026-11-13 |
| Before the publish job Pro submits `AvailableFonts` on the same GP service; the SOAP fault it gets is tolerated (the draft stages and the publish runs) | recorded envelope | 2026-11-13 |
| A second upload under the same name is refused by Pro itself (001398 *"Service name … already exists"*, from `admin/services/{name}.MapServer`); with `overwriteExistingService` Pro sends the same `PublishServiceDefinition` job, no delete first, so overwriting is the publish route's replacement of the caller's own service | arcpy, wire log | 2026-11-13 |
| **Pro compares the database user too.** Connected to the same database as another user, Pro finds the database not registered and — with `copyDataToServer` off — copies the rows into the definition under `cd/` and points the layer at that copy; the job refuses it in those words (§5.1 holds on both sides) | staged `.sd`, job messages | 2026-11-13 |
| **The desktop application publishes.** With `owningSystemUrl` naming `/studio` for Pro (§5.6), ArcGIS Pro 3.7 itself — a notebook in the open project, its active portal `…/studio/` — staged and published the reference layer as `ProRef_Desktop`; 002901 is gone. The *Share › Web Layer* pane was not clicked through: the Windows session was locked, so mouse and keyboard input could not reach Pro (UI Automation could, which is how the notebook was driven) | in-Pro output | 2026-11-13 |
| **Pro's symbology is carried.** A reference definition has no `servicedescriptions/` (no feature access), so the `drawingInfo` a copied publish reads (ADR-191 §5.7) is not there; each layer's CIM `renderer` in the `.mapx` is. Stored as written and read with `SymbologyConversion.Read`, Pro's default point symbol converts with no loss, and a single symbol set in arcpy to red, 9 pt comes back from `MapServer/0` as `esriSMS` circle `[200,30,30,255]`, size 9, and draws red. Unique-value and class-breaks renderers were not published end to end — the local table has no attribute column to classify on — and rest on the CIM converter's own tests | arcpy, REST answers, export PNG | 2026-11-13 |
| Esri: 00147 is also raised when Pro cannot reach the server's administration URL | [KB 000018287](https://support.esri.com/en-us/knowledge-base/problem-error-00147-when-the-server-role-is-authorized-000018287) | public |

## 5. Decision

Graticula answers the federated-server calls Pro makes for a reference publish, in the shapes real servers give,
and only the calls that have been seen asked. Anything else is recorded (dev only) and refused.

1. **Matching.** A connection in the `.sd` matches a registered PostgreSQL source when host, port and database agree
   (`SourceQuiesce.DatabaseKey`) **and the database user is the same** (compared without case). A side with no user
   matches nothing. Owner decision 2026-11-12. The user that reads the data is the registered source's user.
2. **Publisher address.** A registered source may carry a `publisher_address` (`host` or `host,port`, migration 136):
   the address Pro uses for the same database when it differs from the server's (a container name against
   `localhost,55499`). The registration check lists the source at that address. Only the address is stored; the
   database, user and password stay the registered source's. **INFERRED** from ArcGIS's publisher connection — the
   owner did not object on 2026-11-11, but did not choose it in so many words; Alternative C is the fuller form.
3. **SOAP only.** `GetMessageVersion` and `GetMessageFormats` are answered; the format is SOAP (§3).
4. **The portal's own answers follow a real portal's**: the server's `adminUrl` is its base, `subscriptionInfo` has
   `orgCapabilities: []`, and the default organisation also answers to the id it had before ADR-216.
5. **The publish is `POST /admin/publish`, run as the caller** (2026-11-13). `admin/uploads/upload` keeps the `.sd` for
   an hour on the node that took it, answered only to its uploader. `PublishServiceDefinition` reads the definition's
   CIM map (the `.mapx`; the binary `.msd` and the manifest's encrypted passwords are never opened), matches every
   layer by §5.1, finds its table with the source's own probe, and hands the composition to the publish route's own
   handler in-process — so every privilege, address rule, replacement rule and audit line of a Studio publish applies.
   A query layer is accepted only as a plain `select <columns|*> from [db.]schema.table`; the columns it leaves out are
   hidden through the field-override route. A filter, a join, an expression, a definition query, a raster layer or a
   layer Pro copied are refused, each in its own words. Group layers are flattened. Each layer's CIM `renderer` is
   stored as its symbology (ADR-052); what the conversion cannot carry is a job warning, and a renderer it cannot read
   leaves the generated appearance with a warning (2026-11-13). Labels are not carried: this server draws none.
6. **`owningSystemUrl` names `/studio` for ArcGIS Pro only** (2026-11-13), and its `tokenServicesUrl` the portal's
   `generateToken` under it; every other client is answered as before. Chosen by the name Pro gives in every request
   (`ArcGIS Pro 3.7.0 (…) - ArcGISPro`). **INFERRED** from the owner's *"kalan işleri de tamamla"* ("finish the
   remaining work too"), given in answer to the question whether to make this change; the Pro-only scope is this
   ADR's, so that Field Maps and the SDKs, which sign in at the root, see nothing change. Listed for the owner under [Q-228](../open-questions.md), which asks the same question for every client.

## 6. Consequences

**Positive.** Pro gets through the connection and the registration check (00003 and 00290 are gone); a registered
database is offered to Pro with no password in the listing; and a map image layer shared from Pro with *Reference
registered data* becomes a service over the registered table, made by the same route and rules as a Studio publish.

**Negative.**
- `/arcgis/rest/info` advertises a `soapUrl` whenever publishing from Pro is on, which is the default — the showcase
  included. **INFERRED** that this is acceptable; listed for the owner.
- ~~Not built: reading the reference `.sd`, matching it, creating the service, the upload and publish calls, the
  publisher address in the Studio UI.~~ **Built 2026-11-13** (§5.5, §5.6, Conditions).
- ~~00147 is not solved~~ — **passed 2026-11-13** (§4): a routing bug that answered `canCreateService` with a folder
  error. It had blocked every map image layer from Pro to this server, not only a reference one.
- `/arcgis/rest/info` answers differently by caller (§5.6): a Pro user who added the *root* portal — which cannot
  publish anyway (Esri 000031246) — is told the server is `/studio`'s. **INFERRED** acceptable; listed for the owner.
- A map image layer is published here as this server's composition: ~~Pro's map symbology is not carried~~ — **carried
  2026-11-13** (§5.5), each layer's CIM renderer, with losses reported to Pro — but its labels are not, and the only
  map image layer Pro can share is a reference one: a layer Pro copied is refused rather than imported.
- Uploads and finished jobs are held in memory on the node that took them (see *State*); a node restarted between
  Pro's upload and its polls loses the job, and Pro reports the publish failed, which it was.

## Conditions

1. **The publish job is built:** `PublishServiceDefinition` on `PublishingTools` reads the uploaded `.sd`, matches
   every connection in it by §5.1, and creates the map service (and its feature service) over the registered
   source — or refuses with the analyzer's own words. The upload (`admin/uploads/upload`) is answered outside the dev
   measuring tools. **DISCHARGED 2026-11-13** — §5.5; measured end to end (§4), tests in
   `ReferenceServiceDefinitionTests`.
2. **The desktop application reaches the analyzer:** 002901 is removed by an owner decision on `owningSystemUrl`
   (and `tokenServicesUrl`) for Pro publishing — **INFERRED** that `/studio` is the right value; listed for the owner.
   **DISCHARGED 2026-11-13** — §5.6, Pro only; the desktop application staged and published (§4). The decision is
   INFERRED and stays listed for the owner.
3. **A real publish, end to end, from the desktop application** — Share › Web Layer with *Reference registered data* —
   draws on this server from the registered table, and a second user whose database user differs is refused (§5.1).
   **PARTLY DISCHARGED 2026-11-13:** the desktop application published the reference layer and it draws from the
   registered table, and another database user is refused (§4) — but from a notebook inside Pro and from arcpy, not by
   clicking the *Share › Web Layer* pane, because the session was locked. What remains is that pane, once, by hand.
4. The publisher address is set in Studio, not only through the admin API. **DISCHARGED 2026-11-13** — Server › Data
   sources › ⋯ › *Publisher address…*, and the row says where Pro reaches the database.

**State.** In the catalogue: `data_source.publisher_address` (nullable text, migration 136), shared by every node.
At runtime, per node and for an hour at most: an uploaded `.sd` on the node's temporary disk, and a finished
publishing job's outcome in memory, each under a random id and answered only to the member who made it. The
registration check reads the catalogue each time.

**Ports created.** None.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | Pro's reference publish does not need the binary SOAP format | VERIFIED 2026-11-13 — the upload, the job and its polls ran in SOAP (§4) |
| — | 00147 has a cause that a documented or observed answer can remove | VERIFIED 2026-11-13 — the cause was this server's routing (§4) |

## 8. Dependencies

**Depends on:** [ADR-191](ADR-191-arcgis-pro-publishes-hosted-feature-layers.md),
[ADR-040](ADR-040-the-portal-surface-is-how-arcgis-pro-connects.md) (the SOAP catalogue it chose not to build),
[ADR-285](ADR-285-graticula-does-not-license-arcgis-pro.md), [ADR-216](ADR-216-the-organisation-has-one-id.md) (the kept portal id).

**Depended on by:** —

## 9. Revisit triggers

- ~~00147 is passed: the status moves to `ACCEPTED WITH CONDITIONS`, with the unbuilt parts of §6 as conditions.~~ **Fired 2026-11-13.**
- Pro is seen asking for the binary message format: §3 first point decides it, and this ADR is reopened.
- A publisher's database name or user differs from the server's in a real deployment: Alternative C.

## 10. Dissent

None recorded.
