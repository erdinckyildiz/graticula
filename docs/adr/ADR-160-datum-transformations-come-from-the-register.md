# ADR-160 — Datum transformations are listed from the register and applied as named

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `MEDIUM` — listing and applying are checked end to end; the ranking without an area of interest is this server's, not a reproduction of ArcGIS's default |
| **Decided** | 2026-10-03 by owner decision (*"Evet, yap"* — asked whether to build Q-100's fifth route so the geometry service answers its twenty-second operation) |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-022](ADR-022-geometry-server.md) (`findTransformations` refused), [ADR-146](ADR-146-project-relation-and-the-cell-notations.md) (E6: a `transformation` in `project` refused) |

---

## 1. Context

`findTransformations` lists the datum transformations between two references, ranked, and a client then names one
in `project`'s `transformation`. The geometry service refused both: the list lives in PROJ's `proj.db`, which the
serving process does not carry (ADR-009 §2.2), and PostGIS has no SQL that enumerates the candidate operations
between two references ([Q-100](../open-questions.md), measured 2026-08-26). Q-100's fifth route — answered as to
mechanism on 2026-09-09 by ADR-060's example — was to generate the list from `proj.db` at build time; what was left
was whether it was worth doing. The owner said yes.

## 2. Alternatives considered

### Alternative A — A register generated at build time; the datastore applies the one named (chosen)

- **`tools/datum-transformations.py`** reads `proj.db` — the same file `tools/axis-order.py` reads, from the same
  PostGIS image, so the two registers are one vintage — and writes `TransformationRegister.cs`: every
  non-deprecated EPSG and ESRI transformation between two geographic 2D references, 1,874 in EPSG v10.008, with its
  accuracy, its area of use and whether it reads a grid (373 do). 137 KB of source.
  `TheTransformationRegisterIsNotStaleTests` regenerates it and compares, as ADR-060 condition 2 does for axis order.
- **Paths.** From `inSR`'s geographic reference to `outSR`'s — read from the datastore's own definition of each, so
  a projected reference transforms through its own datum: each transformation between them either way round
  (`transformForward`), and, when neither is WGS 84, each pair through it as ArcGIS's `geoTransforms`.
- **Ranking.** Given `extentOfInterest`, those whose area of use touches it, most accurate first. Without one, the
  widest area of use first, then the most accurate: a grid for Catalonia is the most accurate ED50 transformation in
  the register and wrong everywhere else, and a caller who gave no place has not said they are in Catalonia.
  Unknown accuracy ranks last.
- **What the datastore cannot run is not offered.** A transformation that reads a grid is tried once at a point in
  its area of use and the answer remembered; the datastore without the grid fails it, and it is left out — a list of
  what cannot be applied does not answer *which can I use* (D-32 from the other side).
- **`project` applies the one named**: a wkid with `transformForward`, `{"wkid":…}`, or `{"geoTransforms":[…]}`,
  checked to lead from the input's geographic reference to the output's, then `ST_Transform` to the input's datum,
  `ST_TransformPipeline` or `ST_InverseTransformPipeline` for each step, `ST_Transform` to the output — one
  statement. The answer's `transformation` says which was applied and its register accuracy. One that leads
  elsewhere is refused, saying where it goes.
- **Answer shape.** `numOfResults=1`, the default, answers the transformation itself; more answer
  `{"transformations":[…]}`, as ArcGIS does.

### Alternative B — Read `proj.db` at runtime in the serving process

**Against:** Q-100 and ADR-060: 9 MB and SQLite in a process ADR-009 §2.2 keeps PROJ out of, for a question whose
answer changes only when the register does.

### Alternative C — Keep refusing

**Against:** it was the last operation of twenty-two, and `project` had to refuse a pinned transformation with it,
which is the half of geometry-crs-policy §3 that matters for survey work.

## 3. Counterarguments to the preferred option

- *The register is a copy, and it is the datastore's vintage, not today's EPSG.* The staleness test catches drift
  from the datastore's register, not EPSG moving — the same bound ADR-060 accepts.
- *Names are the register's.* EPSG's own names (`ED50 to WGS 84 (1)`) and Esri's for ESRI codes; ArcGIS shows its
  own spelling of EPSG names (`ED_1950_To_WGS_1984_1`), and a client comparing names rather than wkids will differ.
- *The no-area ranking is a choice.* ArcGIS documents what the default is for, not how it is chosen.
- *A transformation PROJ needs a 3D or geocentric step for* is not in the register: only geographic 2D to
  geographic 2D.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| ED50 → WGS 84 with no area lists 1133 first; in Turkey's extent no Catalonia grid; 4230 → 4269 is joined through WGS 84; 1133 used backwards is said so | `TransformationRegisterTests` | this repository |
| `findTransformations` answers; `project` with 1133 moves a point by 1133 and says so with accuracy 10 m; `{"wkid":1133,"transformForward":false}` goes back; 1133 forward from WGS 84 is refused naming where it goes | `GeometryServerConformanceTests.Transformations_are_listed_and_the_one_named_is_applied` | this repository |
| The register matches the generator's output from the CI image's `proj.db` | `TheTransformationRegisterIsNotStaleTests` | this repository |
| A grid transformation whose grid is absent fails in `ST_TransformPipeline` rather than passing a point through | measured on PostGIS 3.6 / PROJ 8.2.1: EPSG 1241 (NADCON) refused, ESRI 108359 (Catalonia NTv2, present) applied | 2026-10-03 |

## 5. Decision

As §2, Alternative A.

## 6. Consequences

**Positive.** The geometry service answers all twenty-two operations, and a surveyor can pin the transformation.

**Negative.** A second generated register to keep current, with the same staleness bound as the first.

**State.** A process-lifetime record of which grid transformations the datastore can run, at most one entry a
grid transformation in the register.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | PostGIS 3.4 or later, for `ST_TransformPipeline` | The datastore image is PostGIS 3.4 |
| — | The EPSG dataset's terms allow its data to be carried in the product with acknowledgement | Recorded in DEPENDENCY-LICENSES.md for verification, as the axis register already relied on |

## 8. Dependencies

**Depends on:** ADR-009, ADR-022, ADR-060, ADR-146.

**Depended on by:** —

## 9. Revisit triggers

- The datastore image's PROJ moving to a newer EPSG register.
- A client that compares transformation names rather than wkids.
