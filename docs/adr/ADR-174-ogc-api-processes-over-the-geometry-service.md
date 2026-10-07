# ADR-174 — OGC API Processes, over the geometry service

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` — each process is one geometry-engine operation under the geometry service's own bounds, its results are tested against the engine's, and OGC's executable suite runs nightly; no processing client has been pointed at it yet |
| **Decided** | 2026-10-06 by owner decision (*"hadi ogc yi de bitirelim"*, choosing Records, Maps, Styles and Processes); the design below is INFERRED where marked |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [v1-scope.md](../v1-scope.md) §3d, by owner decision — Processes was on its *not in v1* list |

---

## 1. Context

On 2026-10-06 the owner asked to finish the OGC faces and took OGC API Processes among the four new OGC APIs. Part 1:
Core (OGC 18-062r2) describes processes, executes them synchronously or as jobs, and lists, reads and dismisses jobs.

This server already computes: the geometry service (`Utilities/Geometry/GeometryServer`) buffers, unions, intersects,
differences, simplifies, generalizes and measures distance through one engine (`IGeometryEngine`) under a deadline, a
pre-flight on input size and a bounded wait for a slot. It has no general-purpose execution engine and v1 builds none
(§82).

## 2. Alternatives considered

### Alternative A — The geometry service's operations as processes (chosen)

**For.** Nothing new computes. A buffer asked for here and one asked for at `GeometryServer/buffer` are the same
engine call under the same limits, and the processes are governed by the geometry service's sharing and its
started/stopped state, so this face is not a door round a decision the owner already made about that service.

**Against.** Eight processes is a small catalogue for a processing API; a client expecting analysis (overlay of
layers, statistics) finds geometry arithmetic only.

### Alternative B — A process per layer operation (query, statistics, export)

**For.** Closer to what a processing client wants from a GIS server.

**Against.** Each would be a second entry into paths with their own limits, export jobs and audit; that is a design
of its own and nobody has asked for it.

### Alternative C — Not served

**Against.** The owner chose it.

## 3. Counterarguments to the preferred option

*Jobs held in memory are lost on restart and not shared between instances.* Yes, and that is the decision: a job
here is a geometry computation that finishes in at most the geometry service's deadline. A restart loses a result the
caller can recompute in the same time it would take to ask again. Persisting them would need a table, a sweeper and
cross-instance reads for no measured need.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| A buffer here is the engine's buffer | `ABufferIsTheGeometryServicesBuffer`: a 10-unit buffer of a point in EPSG:3857 spans exactly ±10 | `tests/Graticula.Conformance.Tests/OgcMapsAndProcessesConformanceTests.cs` |
| Sharing governs | `ProcessesFollowTheGeometryServicesSharing`: signed in, buffer and echo are listed; anonymously, with the geometry service shared with the organisation, none is, and executing echo is 404 | same |
| Jobs are the caller's | `AJobIsTheCallersAndCanBeDismissed`: `Prefer: respond-async` → 201 with `Location`; status reaches `successful`; results read; another caller gets 404; `DELETE` dismisses | same |
| OGC's suite | `ogccite/ets-ogcapi-processes10` runs nightly in `cite.yml` with the geometry service shared publicly for the run (the engine is anonymous). First run: **48 passed, 1 failed, 5 untested** — the process list ignored `limit`; both lists now page | `tools/cite-run.sh`, `tools/cite-baselines.json` |

## 5. Decision

OGC API Processes 1.0 Part 1 is served at `/ogc/processes/v1`: landing, `/conformance`, `/api` (OpenAPI 3.0.3),
`/processes`, `/processes/{id}`, `POST /processes/{id}/execution`, `/jobs`, `/jobs/{id}`, `/jobs/{id}/results` and
`DELETE /jobs/{id}`. Conformance claimed: `core`, `ogc-process-description`, `json`, `oas30`, `job-list`, `dismiss`.

- **Processes:** `buffer`, `union`, `intersection`, `difference`, `simplify`, `generalize`, `distance`, each one
  `EngineOperation`, and OGC's `echo`, which the suite requires and which computes nothing.
- **Access:** only when the system service `Geometry` is shared with and visible to the caller and started; otherwise
  the list is empty and execution is `no-such-process` (404). Hiding rather than refusing is the filtering every
  standard face uses.
- **Geometries** are GeoJSON in the reference the `crs` input names — an OGC URI, a URN or `EPSG:n` — and distances
  are in its units; without one it is CRS84, and a distance is in degrees. *(INFERRED: honest rather than convenient.)*
- **Execution** is synchronous unless `Prefer: respond-async`; the default response is raw (one output its value,
  several a `multipart/related`), `response: document` a JSON map. Only `value` transmission; `reference` is a 400.
- **Jobs** live in memory, at most 1,000, for an hour, each visible only to the caller who started it. *(INFERRED)*

## 6. Consequences

**Positive.** A processing client runs the geometry service's operations through a standard, under the limits and
sharing that already govern them.

**Negative.** Jobs do not survive a restart and are not shared between instances. The catalogue is geometry
arithmetic only.

**Anonymous callers share one job list.** A job belongs to the account that started it; an anonymous job belongs to
no account, so every anonymous caller can list every anonymous job and read its results. That is what OGC's suite
requires — its anonymous `getJobs` must list the jobs its own anonymous executions made (`/req/job-list/job-list-op`)
— and anonymous jobs exist only while the geometry service is shared publicly, whose inputs are then a stranger's
geometry submitted to a public service. A signed-in caller's jobs are never visible to anyone else. *(INFERRED that
this is acceptable — part of [Q-164](../open-questions.md).)*

**State.** No catalogue state. At runtime each node holds its own jobs in memory — at most 1,000, for an hour — and they are node-local: another instance cannot read them, and a restart loses them.

**Ports created.** None; `IGeometryEngine` already isolates the geometry library.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | A geometry computation finishes within the geometry service's deadline, so an in-memory job is enough | Held by the deadline itself |

## 8. Dependencies

**Depends on:** the geometry service and its bounds (ADR-021's operations, the 2026-08-17 start/stop decision).

**Depended on by:** —

## Conditions

1. A processing client — QGIS's OGC API Processes provider or OWSLib's `OGCAPIProcesses` — lists the processes and
   runs a buffer.
   **DISCHARGED 2026-10-07** with OWSLib 0.35's `owslib.ogcapi.processes.Processes`, signed in by a bearer header: it
   lists the eight processes, describes `buffer`'s inputs, and a 10-unit buffer of a point in EPSG:3857 comes back a
   polygon spanning 990 to 1010. (The OWSLib QGIS 3.28 ships, 0.25, predates the Processes client.)
2. The owner confirms that the processes follow the geometry service's sharing rather than having a sharing of their
   own (`INFERRED`).

## 9. Revisit triggers

- More than one server instance behind a balancer: a job started on one cannot be read on another.
- A request for a process that is not a geometry operation.

## 10. Dissent

None recorded.
