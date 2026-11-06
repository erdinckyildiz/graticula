# ADR-273 — The Maps SDK the console loads is 4.34

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `HIGH` |
| **Decided** | 2026-11-07. **The direction is the owner's**: [ADR-272](ADR-272-owner-decisions-on-3d-scope.md) §5.2 says to raise the SDK as its own change, with the console suite run against it. **The version is this ADR's choice** (§2). |
| **Supersedes** | The 4.29 pin in `HostSettings.DefaultMapSdkUrl` (ADR-020 §4's CDN choice is unchanged). |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

Scene Viewer's volume measurement, viewshed and dimensions, and a slide that keeps its analyses, legend and pop-up, are
not in the Maps SDK 4.29 this console pinned ([3d-parity-arcgis.md](../reviews/3d-parity-arcgis.md) §B, §C). Every page
that draws a map loads the SDK from `Graticula:MapSdkUrl`: the console, the map page, the web map viewer and the scene
viewer.

## 2. Alternatives considered

### Alternative A — 4.34 (chosen)

The last 4.x release. It still ships the AMD build (`init.js`, `require([...])`) that every page here loads, measured
2026-11-07: `js.arcgis.com/4.34/init.js` answers 200 and `…/4.35/init.js` answers 404. It has `DimensionAnalysis`,
`ViewshedAnalysis` and `VolumeMeasurementAnalysis`, and its `Slide` carries `elements` (analyses, legend, pop-up) and a
`layout` (caption, cover). This was read from its published typings, `@arcgis/core@4.34.0/interfaces.d.ts`.

### Alternative B — 5.x

ES modules only: `js.arcgis.com/5.0/init.js` answers 404. Every page's loader would change, and the widgets give way
to components. **Rejected for this change** because it is a second, larger migration. It is a revisit trigger.

### Alternative C — stay on 4.29

**Rejected** by the owner's decision.

## 3. Counterarguments to the preferred option

4.34 is the end of the 4.x line, so the next upgrade is the module migration anyway. That is true, and it is recorded
in §10. Taking it now would put the 3D tools behind a rewrite of every map page's loader.

## 4. Evidence

- The fixture server started with `Graticula__MapSdkUrl=https://js.arcgis.com/4.34/`. The pages loaded 4.34 (read back
  from `surface.js`).
- The 14 console test classes that load the SDK passed on it, **85 of 85** (2026-11-07): `AnalyzeDialogTests`, the
  `Scene*` classes, the `WebMap*` classes, `ViewerPageTests`, `UiSweepOf20261102Tests` and
  `TheServersGroundIsDrawnTests`.
- The rest of the console suite does not load the SDK. CI runs it whole.

## 5. Decision

`HostSettings.DefaultMapSdkUrl` is `https://js.arcgis.com/4.34/`. A deployment that sets `Graticula:MapSdkUrl` is
unchanged, and an air-gapped copy of 4.29 keeps working: nothing here requires 4.34 except the tools it adds (ADR-270
§5.18), which say so when the SDK lacks them.

## 6. Consequences

- The 3D tools 4.29 lacked can be built (ADR-270 §5.18).
- **State.** None.

## 7. Assumptions this decision rests on

- Esri keeps serving 4.34 from its CDN. An air-gapped deployment's own copy is the answer if not (Q-15).

## 8. Dependencies

ADR-020 (the SDK from Esri's CDN), ADR-272.

## 9. Conditions

None.

## 10. Revisit triggers

- A need only 5.x meets, or Esri stops serving 4.x: then the module migration, as its own ADR.
