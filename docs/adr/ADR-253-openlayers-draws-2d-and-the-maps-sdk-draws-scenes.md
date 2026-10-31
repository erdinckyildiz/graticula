# ADR-253 — OpenLayers draws every 2D map, and the ArcGIS Maps SDK draws image views and scenes

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-10-31, by owner decision: *"2B OpenLayers, sahne SDK"*, chosen over *"Her yerde SDK"* and *"Sahne için açık motor"* when the coordinator asked which engine [ADR-101](ADR-101-studio-is-walked-the-way-a-portal-user-walks-it.md) condition 4 should settle on. |
| **Supersedes** | — |
| **Superseded by** | — |

---

## 1. Context

ADR-101 condition 4 asked for the structural decisions of the Studio and portal redesign, and one of them, the map
engine, was the only one still open on 2026-10-31. The design review had proposed MapLibre. The code holds none of
that: it holds **two engines**.

- **OpenLayers** draws every 2D map: the Map Viewer (`webmap.js`), the public viewer (`view.html`) and the console's
  maps. `ol.js` is served by this server.
- **The ArcGIS Maps SDK for JavaScript**, loaded at run time from `js.arcgis.com`, draws `map.html`, which Studio links
  for image-service views and 3D scenes.

Leaving it undecided keeps a recorded condition open and lets a third engine arrive by accident, which is how the
review found three places drawing maps three ways.

## 2. Alternatives considered

### Alternative A — OpenLayers for 2D, the SDK for image views and scenes (the code as it stands)

**Argument for.** Nothing is rewritten. Every 2D map, the surface people use most, depends on nothing outside the
server. The SDK draws what OpenLayers cannot: scenes, elevation as ground, and image services the way ArcGIS clients
draw them. ADR-182 condition 2 proved the SDK reads this server's elevation items as ground.

**Argument against.** Two engines are two sets of behaviour to keep alike: a symbol, a pop-up or a label drawn in 2D
may differ from the same thing in a scene. The SDK is Esri's proprietary script, fetched from Esri at run time, so a
scene needs Esri's CDN to be reachable and accepts Esri's terms for that script.

### Alternative B — the SDK everywhere

**Argument for.** One engine; maps look the way ArcGIS users expect, and the SDK's renderers read ArcGIS JSON
directly.

**Argument against.** Every map, 2D included, would depend on a proprietary script from a remote CDN. A deployment on
a closed network would draw no maps at all, and the product's most-used surface would sit on a dependency it does not
ship and cannot pin.

### Alternative C — an open engine for scenes (CesiumJS or similar), the SDK removed

**Argument for.** No proprietary or remote script anywhere; one licence posture for every map.

**Argument against.** A large rewrite of everything `map.html` does, for a scene surface that is not the product's
centre, and an open engine reads none of ArcGIS's scene and renderer JSON natively, so each would be translated here.

## 3. Counterarguments to the preferred option

Two engines will drift. The day a renderer is fixed in `webmap.js` and not in `map.js`, a user sees the same layer
drawn two ways, and nothing but discipline keeps them together. And a closed-network deployment loses scenes and
image views silently unless the page says why.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Every 2D map is OpenLayers, served locally | `webmap.js`, `view.html`, the console's maps load `ol.js` from this server | `src/Graticula.Host/wwwroot/` |
| Image views and scenes are the SDK, from Esri's CDN | `map.html` and `map.js` load `js.arcgis.com` | `src/Graticula.Host/wwwroot/map.html` |
| MapLibre is used nowhere | grep of `src/` | ADR-101 condition 4, 2026-10-31 |
| The SDK reads this server's elevation item as ground | ADR-182 condition 2, discharged 2026-10-31 | `tools/sdk-probe/elevation-item.html` |

## 5. Decision

OpenLayers draws every 2D map and is served by this server; the ArcGIS Maps SDK for JavaScript, loaded from
`js.arcgis.com`, draws image-service views and 3D scenes. No third engine is adopted, and MapLibre is not. Scenes and
image views not working where `js.arcgis.com` cannot be reached is accepted as this decision's cost.

## 6. Consequences

**Positive.** Nothing to rewrite; 2D maps work on a closed network; scenes get the engine that reads ArcGIS's scene
JSON natively.

**State.** None: the decision chooses which script draws a map; it puts nothing in the catalogue and holds nothing at runtime.

**Negative.** Two engines to keep alike. Scenes and image views depend on Esri's CDN and terms. A closed-network
deployment has no scene viewer.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | Esri keeps serving the SDK from `js.arcgis.com` on terms that allow this use | Unverified beyond today's use |

## 8. Dependencies

**Depends on:** ADR-101 (condition 4), ADR-102, ADR-182.

**Depended on by:** every Studio map surface.

## 9. Revisit triggers

- A deployment that must draw scenes without reaching `js.arcgis.com`.
- Esri changing the SDK's terms or its CDN so that this use is no longer allowed.
- A renderer, pop-up or label recorded as drawn differently in 2D and in a scene.

## 10. Dissent

None recorded. The design review's proposal (MapLibre) was not taken, and nothing in the code had followed it.
