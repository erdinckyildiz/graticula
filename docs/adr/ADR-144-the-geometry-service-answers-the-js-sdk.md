# ADR-144 — The geometry service answers the JS SDK as it asks

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `HIGH` — every request is the SDK 4.30's own shape, read from its published source |
| **Decided** | 2026-10-03 by owner decision (*"geometry service'in … kalan işlerini tamamla"*) |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-022](ADR-022-geometry-server.md) (operand names, one answer per input, `unionResults`, `cutIndexes`), [ADR-015](ADR-015-authentication.md) (a token in a form body) |

---

## 1. Context

The ArcGIS reviewer's GeometryServer pass, 2026-10-03, sent each operation as the ArcGIS Maps SDK for JavaScript 4.30
sends it — read from the SDK's own `rest/geometryService` and `*Parameters.toJSON` — and found the service answering
this server's forms rather than the SDK:

- **E1** — buffer's reference arrives as `inSR`; areasAndLengths and labelPoints send `polygons`, lengths `polylines`;
  intersect, difference and distance wrap their second operand as `{geometryType, geometry}`; cut's `target` is a
  list. Each was a 400.
- **E3** — intersect, difference, simplify, buffer and cut combined their inputs into one before computing: two
  polygons simplified came back as one, a polygon inside another vanished, and a client mapping answers to its features
  by index mapped them wrongly. `unionResults` and `cutIndexes` did not exist.
- **E4** — past 2,000 characters the SDK moves a request to POST and its token into the body, which was not read: a
  real polygon arrived anonymous and was answered as a service that is not there. Server-wide: a large query and
  applyEdits too.
- **E11** — `project` answered PROJ's search paths, this server's directories and the account name among them.

## 2. Alternatives considered

### Alternative A — Read what the SDK sends, answer per input (chosen)

- **E1**: `inSR` where `sr` is absent; `polygons` and `polylines` beside `geometries`; a `{geometryType, geometry}`
  wrapper unwrapped; `target` read as a list or one geometry.
- **E3**: intersect, difference, simplify, cut and buffer answer each input alone, in order — against the other
  operand combined — in the input's dimension and empty where nothing is left; buffer merges only when `unionResults`
  is true; cut answers `cutIndexes`, each piece's target, a target the cutter misses being one piece. Union still
  combines, as it is for.
- **E4**: a `token` field in a `application/x-www-form-urlencoded` POST body is read, buffered and rewound, whatever
  `AcceptTokenInQueryString` says: a body is not written to request logs (D-120's cost), and like the query parameter it
  is put there by the caller, never attached by the browser, so it carries none of the cookie's forgery risk. Multipart
  bodies are not read.
- **E11**: the engine is named by its version alone.

### Alternative B — Document this server's names and leave clients to adapt

**Against:** a client is the SDK, Web AppBuilder or an app built on either; none of them can be told.

## 3. Counterarguments to the preferred option

- *An intersect of several inputs is several overlays*, each against the other operand combined. The pre-flight and the
  deadline bound the whole request as before.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| The SDK's shapes, the 400s, the merged answers, the anonymous POST and the leaked paths | the reviewer's requests | ArcGIS reviewer, 2026-10-03 |
| Each shape answered; three intersect inputs answer three, the outside one empty; two simplified stay two; buffer answers two unless `unionResults`; cut's pieces name their targets; a body token signs the request in | `GeometryServerClientRequestsTests` | this repository |
| The adversarial comb is still refused on its deadline, in 10 s | `GeometryServerConformanceTests` | this repository |

## 5. Decision

The geometry service reads the SDK's names and wrappers, answers per input as ArcGIS does, and every face reads a token
from a form-encoded POST body.

## 6. Consequences

**Positive.** `esri/rest/geometryService` and the widgets built on it work against this server for planar work.

**Negative.** Geodesic measurement is refused, not done (ADR-145).

**State.** None.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | The geometry service's remaining work is wanted | Stated by the owner, 2026-10-03 |

## 8. Dependencies

**Depends on:** ADR-015, ADR-022.

**Depended on by:** —

## 9. Revisit triggers

- A client measured sending a shape this does not read.

## 10. Conditions

1. **Units and geodesic measurement (E2)** are applied or refused by name, not answered planar with a 200 — the
   reviewer's next item. **DISCHARGED 2026-10-03 by [ADR-145](ADR-145-a-geometry-request-is-answered-in-its-units.md).**
