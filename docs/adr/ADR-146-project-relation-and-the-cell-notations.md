# ADR-146 — project takes extents and WKT, relation reads RELATE, and GARS and GEOREF are converted

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `HIGH` — each is checked on an answer known in advance |
| **Decided** | 2026-10-03 by owner decision (*"geometry service'in … kalan işlerini tamamla"*) |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-022](ADR-022-geometry-server.md) (what `project`, `relation` and the coordinate notations accept) |

---

## 1. Context

The ArcGIS reviewer's GeometryServer pass found, beside ADR-144's and ADR-145's items:

- **E5** — `project` refused an envelope, which is how the JS SDK sends a map's extent and the commonest thing projected;
  refused an `outSR` given as WKT; and dropped a point's `z` without a word.
- **E6** — `transformation` was ignored: three different transformations asked for gave one answer.
- **E8** — `relation` refused `RELATE(G1, G2, 'T*F**F***')`, the form Esri documents, and leaked the topology
  library's message doing it.
- **E10** — GARS and GEOREF, two of ArcGIS's eight notations, were refused; `f=pjson` was not indented.

## 2. Alternatives considered

### Alternative A — Accept what ArcGIS accepts, refuse what cannot be honoured (chosen)

- **project**: an envelope is projected as a polygon whose edges carry 24 points each, and answered as the box around
  it — a projected rectangle's edges curve, and its corners alone undercount it. An `outSR` of `{"wkt":…}` is projected
  to by its definition, the answer carrying the same WKT. A point's `z` is kept: a horizontal projection does not
  change it. A named `transformation` is refused — PROJ chooses, and the answer says which engine did — rather than
  answered with another than the one asked for.
- **relation**: `RELATE(G1, G2, '<pattern>')` is read, and anything that is neither it nor a bare DE-9IM pattern is
  refused in words.
- **GARS and GEOREF** are written and read: both are angular cell grids, so no projection. A string names a cell, and
  is read as its centre.
- **f=pjson** is indented.

### Alternative B — Pin transformations

**Against:** Q-100's design, not done here; refusing is honest until it is.

## 3. Counterarguments to the preferred option

- *A client that sends `transformation` routinely is refused* where it used to be answered by PROJ's choice. It now
  learns that its choice was never applied.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Türkiye's extent projects to the box of its curved edges; a point keeps z = 120; a WKT outSR equals its code's answer; a named transformation is refused | `GeometryServerClientRequestsTests` | this repository |
| RELATE(G1, G2, 'T*F**F***') finds the inner square within the outer; a sentence is refused without the library's words | the same | this repository |
| GARS 006AG35 and 419LY37, GEOREF PJQM0000 and PJQM30001500 computed from the definitions; every string reads back within half its cell; malformed ones are refused | `GeoCoordinateCellTests`, `GeometryServerClientRequestsTests` | this repository |

## 5. Decision

As §2.

## 6. Consequences

**Positive.** A map's extent, a custom projection, Esri's RELATE form and all eight notations work as in ArcGIS.

**Negative.** A named transformation is refused until pinning is designed.

**State.** None.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | The geometry service's remaining work is wanted | Stated by the owner, 2026-10-03 |

## 8. Dependencies

**Depends on:** ADR-022, ADR-144.

**Depended on by:** —

## 9. Revisit triggers

- Q-100 answered: transformation pinning designed.
