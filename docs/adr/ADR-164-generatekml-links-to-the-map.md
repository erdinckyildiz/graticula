# ADR-164 — `generateKml` is a network link to the service's map

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` — the document and the request it makes are checked; Google Earth has not been watched opening it |
| **Decided** | 2026-10-03 — follows from the owner's *"Sonra da OGC tarafına geç"* |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | — |

---

## 1. Context

ArcGIS map services offer KML through `generateKml`: a KMZ for Google Earth, by default a *composite* picture that
refreshes with the view. This server wrote KML only as a layer export (ADR-106), a copy of the features at one moment.

## 2. Alternatives considered

### Alternative A — A ground overlay whose picture is the service's own WMS (chosen)

- `…/MapServer/generateKml` and `…/ImageServer/generateKml` answer a KMZ (or KML with `f=kml`) holding one
  `GroundOverlay` whose `Icon` is a WMS 1.1.1 `GetMap` at the service's own address (ADR-162), with
  `viewRefreshMode` `onStop` and a `viewFormat` that fills in the view's box and pixel size — so Google Earth asks
  for exactly the picture it is showing, styled and current.
- `layers` names layer ids, as ArcGIS's does; `docName` names the document.
- A service shared with fewer than everyone says in the document that Google Earth, which sends no credentials,
  draws it only where the server can tell who is asking.

### Alternative B — The features as KML placemarks

**Against:** unbounded for a large layer, a copy that goes stale, and styles KML cannot express; the export already
writes it for whoever wants the copy.

## 3. Counterarguments to the preferred option

- *No vector placemarks*: ArcGIS's `nonComposite` and `vectorOnly` layer options are not answered.
- *Private services draw blank* in Google Earth.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| `generateKml` answers a KMZ whose `doc.kml` points at the service's `WMSServer`, refreshing on stop, and the request Google Earth makes from it answers a PNG | `OgcServiceAddressConformanceTests.An_image_service_is_a_wms_layer_at_its_own_address_and_drawn` | this repository |

## 5. Decision

As §2, Alternative A.

## Conditions

1. Google Earth Pro opens a public service's KMZ and draws it as the view moves.

## 6. Consequences

**Positive.** Map and image services open in Google Earth, current and styled.

**Negative.** Only for services everyone may see, in practice.

**State.** None.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | Google Earth fills `viewFormat` as KML 2.2 specifies | Unvalidated (condition 1) |

## 8. Dependencies

**Depends on:** ADR-106, ADR-162.

**Depended on by:** —

## 9. Revisit triggers

- A need for vector placemarks.
