# ADR-261 — The hosting server's Administrator API starts, stops and reads the logs

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `MEDIUM` — the doors are this server's own acts behind the Administrator API's addresses; the log mapping is a judgement this server makes because it keeps no level on a line |
| **Decided** | 2026-11-03, by owner decision. The ArcGIS veteran's review of v1.0.499 listed it as #7, *server admin doors (start/stop/logs via /arcgis/admin)*; asked which of the review's open items to take, the owner chose all four, this one among them. |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

`/arcgis/admin` is the hosting server's Administrator API as Pro and the ArcGIS API for Python reach it: the address
portals/self gives as the server's `adminUrl`. Since 2026-10-17 it has answered what Pro reads before it publishes — the
root, `info`, `system/directories` and the services listings — and refused everything else by name, *"'…' is not answered
by this server."*

An ArcGIS administrator's scripts do three more things there every day: read a service's status, stop and start it, and
query the logs. `gis.admin.servers.list()[0].services.list()` and then `service.stop()`; `server.logs.query(...)`. Each
was refused, while Server's own screens did all three.

## 2. Alternatives considered

### Alternative A — Leave the Administrator API read-only

The scripts keep failing, and the reason given is not one: the acts exist, under the same privilege, a click away.
Not chosen.

### Alternative B — The Administrator API's whole service model (instances, clusters, machines)

There are no instances or clusters here to report; inventing their numbers would answer questions with figures nothing
measured. Not chosen.

### Alternative C — The three doors, onto this server's own acts *(chosen)*

## 3. Decision

1. **A service** — `services/[folder/]name.type` — answers its document (`serviceName`, `type`, `description`,
   `configuredState`, and nothing it does not have), its `status` as `configuredState` and `realTimeState`, which are
   always equal here because a stop takes effect when it is written, and `start` and `stop`.
2. **`start` and `stop` are POSTs and ask `admin:manageServer`**, as Server's Start and Stop do
   ([ADR-020](ADR-020-admin-console-and-service-status.md) §3). They write the same status: a feature service's through its first layer,
   as Server's own route does, an image service's through its coverage, a system service's through `system_service`. A
   service is started or stopped whole, whichever face the path named. Each is audited as `service.start` or
   `service.stop`, noting that it came through `arcgis/admin`. A service the caller may not see is not found.
3. **`logs/query` asks `admin:manageServer`**, as the Logs screen does, and reads the request log and the audit trail. It
   takes `startTime` (the newer bound), `endTime` (the older), `level`, `filter` (`services`, the first one named) and
   `pageSize` (1,000 unless said, 10,000 at most), and answers `hasMore`, `startTime`, `endTime` and `logMessages`.
4. **A level is read from what is kept**, since nothing here writes a level on a line: a request that answered 500 or
   more is `SEVERE`, 400 or more `WARNING`, an administrative act `INFO`, any other request `FINE`. A level includes every
   level more severe than itself, as the Administrator API's does.
5. **Everything else stays refused by name.** `machines`, `clusters`, `security`, `data`, `uploads` and `mode` are
   listed in the root's resources because Pro reads the list, and are not answered.

## 4. Consequences

- **State.** None of its own: a start or stop writes the service status the catalogue already holds, and the log query reads the request log and audit trail.
- `Service.start()`, `Service.stop()`, `Service.status` and `Server.logs.query()` work from the Python API against this
  server, and so does an ArcPy or `requests` script written for an ArcGIS Server.
- A log line here is not an ArcGIS Server log line. `process` is this server's, `thread` and `methodName` are empty, and
  `code` is the HTTP status rather than an ArcGIS message code. A script that matches on ArcGIS's message codes finds
  none; one that matches on level, time, source or user works.
- `/arcgis/admin` is still on only while publishing from Pro is ([ArcGisAddresses](../../src/Graticula.Host/ArcGisAddresses.cs)).

## 5. Conditions

None.

## 6. Dependencies

- [ADR-020](ADR-020-admin-console-and-service-status.md) — start and stop, and their privilege.
- [ADR-181](ADR-181-the-rest-api-version-is-12-1.md) — the version the Administrator API says.
- [ADR-191](ADR-191-arcgis-pro-publishes-hosted-feature-layers.md) — why `/arcgis/admin` exists at all.
