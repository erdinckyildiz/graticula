# ADR-299 — ArcGIS Pro publishes a map image layer by reference to a registered database

| | |
|---|---|
| **Status** | `REQUIRES PROTOTYPE` |
| **Confidence** | `LOW` |
| **Decided** | 2026-11-10, **by owner decision**: *"yap"* ("do it"), after asking whether Pro could publish with *Reference registered data* when Pro and the server use the same database with the same user. **2026-11-12**, by owner decision, the database user must match as well: *"veritabanı kullanıcısı ise çalışmaması lazım"* ("if it is the database user, it must not work [when it differs]"). |
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
- **The server describes its own licence in ArcGIS words.** `admin/system/licenses` answers with a GIS Server
  edition and features so that Pro's analyzer accepts the server. This does not give anybody an Esri licence and
  does not license Pro ([ADR-285](ADR-285-graticula-does-not-license-arcgis-pro.md) stands): it describes what this
  server serves. A reader could still see it as a claim to be an Esri product. **INFERRED** — listed for the owner.
- **It may never finish.** See §4: analyzer error 00147 is not solved.

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

## 6. Consequences

**Positive.** Pro gets through the connection and the registration check (00003 and 00290 are gone); a registered
database is offered to Pro with no password in the listing.

**Negative.**
- `/arcgis/rest/info` advertises a `soapUrl` whenever publishing from Pro is on, which is the default — the showcase
  included. **INFERRED** that this is acceptable; listed for the owner.
- Not built: reading the reference `.sd`, matching it, creating the service, the upload and publish calls, the
  publisher address in the Studio UI.
- 00147 is not solved.

**State.** In the catalogue: `data_source.publisher_address` (nullable text, migration 136), shared by every node.
At runtime: nothing kept between requests; the registration check reads the catalogue each time.

**Ports created.** None.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | Pro's reference publish does not need the binary SOAP format | UNVERIFIED |
| — | 00147 has a cause that a documented or observed answer can remove | UNVERIFIED |

## 8. Dependencies

**Depends on:** [ADR-191](ADR-191-arcgis-pro-publishes-hosted-feature-layers.md),
[ADR-040](ADR-040-the-portal-surface-is-how-arcgis-pro-connects.md) (the SOAP catalogue it chose not to build),
[ADR-285](ADR-285-graticula-does-not-license-arcgis-pro.md), [ADR-216](ADR-216-the-organisation-has-one-id.md) (the kept portal id).

**Depended on by:** —

## 9. Revisit triggers

- 00147 is passed: the status moves to `ACCEPTED WITH CONDITIONS`, with the unbuilt parts of §6 as conditions.
- Pro is seen asking for the binary message format: §3 first point decides it, and this ADR is reopened.
- A publisher's database name or user differs from the server's in a real deployment: Alternative C.

## 10. Dissent

None recorded.
