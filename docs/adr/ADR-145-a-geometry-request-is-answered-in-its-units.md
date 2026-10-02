# ADR-145 — A geometry request is answered in the units it names, or refused

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `HIGH` — each conversion is checked on a shape whose answer is known, and each refusal names its parameter |
| **Decided** | 2026-10-03 by owner decision (*"geometry service'in … kalan işlerini tamamla"*); discharges [ADR-144](ADR-144-the-geometry-service-answers-the-js-sdk.md) condition 1 |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-022](ADR-022-geometry-server.md) (§5: geodesic was "not offered rather than offered wrongly", and was answered planar with a 200) |

---

## 1. Context

E2 of the ArcGIS reviewer's GeometryServer pass: `unit`, `lengthUnit`, `areaUnit`, `distanceUnit`, `offsetUnit`,
`deviationUnit`, `geodesic` and `calculationType` were never read, and the planar answer in the reference's own units
came back with a 200. A 1,000-metre buffer in EPSG:4326 was 1,000 degrees wide; an 84-kilometre distance was `1`; an
area asked in square kilometres came back in square metres. ADR-022 §5 meant geodesic to be refused; nothing refused it.

## 2. Alternatives considered

### Alternative A — Convert what can be converted, refuse what cannot (chosen)

The reference's own unit is read from its definition in `spatial_ref_sys` — the last `UNIT` of a projected reference,
degrees for a geographic one — so a reference in US survey feet is measured in them.

- **In a projected reference**, a distance named in metres, kilometres, feet, US survey feet, miles, US survey miles,
  nautical miles, yards or centimetres (Esri's codes or names) is converted to the reference's units before buffer,
  offset, generalize and densify; distance, lengths and areasAndLengths answer in the `distanceUnit`, `lengthUnit` and
  `areaUnit` asked (square metres to square miles, hectares, acres).
- **Refused by name**: a linear unit in a geographic reference (a distance in metres there is geodesic); `geodesic=true`
  and a `calculationType` other than `planar`; a `bufferSR` or `outSR` other than the request's own; an unknown unit.
  Each refusal says what to send instead — a projected reference suited to the area, such as a UTM zone or TM30.

### Alternative B — Geodesic measurement

**For:** what a client in EPSG:4326 asks. **Against:** a week's work — geography buffers, ellipsoidal areas and
densifies — that changes what this service is; the refusal is honest now and the measurement can follow.

## 3. Counterarguments to the preferred option

- *A client that sends geodesic by default is refused* where it used to get a wrong number. It now learns why.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| The reviewer's wrong answers, each with a 200 | the reviewer's requests | ArcGIS reviewer, 2026-10-03 |
| A 1-km buffer in 3857 is 1,000 m each way; 3,000 by 4,000 m apart is 5 km; a 1,000-m square is 1 km² and 4 km round; metres in 4326, geodesic, `calculationType=geodesic` and another `bufferSR` are refused, naming the parameter | `GeometryServerClientRequestsTests` | this repository |
| The decorator in front of the projector did not pass the new question on, so every unit was refused as unknown until it did | the first request that named one | local fixture, 2026-10-03 |

## 5. Decision

The geometry service converts the units a request names in a projected reference, and refuses by name what it cannot
honour.

## 6. Consequences

**Positive.** A measurement or buffer asked in metres is in metres, or says why it cannot be.

**Negative.** Geodesic work is refused, not done.

**State.** None.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | The geometry service's remaining work is wanted | Stated by the owner, 2026-10-03 |

## 8. Dependencies

**Depends on:** ADR-022, ADR-144.

**Depended on by:** —

## 9. Revisit triggers

- Owners asking for geodesic measurement — the client sending `geodesic=true` against a geographic reference.
