# ADR-171 — SLD at the WMS boundary

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` — a style sent with GetMap is drawn and forgotten, GetStyles' document draws the layer's own colour when sent back, refusals are named; no SLD from QGIS or GeoServer has been sent to it, and the documents have not been validated against the SLD 1.1 schema |
| **Decided** | 2026-10-03 by owner decision (*"Yap"*, in answer to the three OGC items left — SLD, WCS, INSPIRE — after the owner had said on 2026-08-17 *"ben sld sevmiyorum"*) |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-033](ADR-033-symbology.md) §2E (SLD not implemented); [ADR-041](ADR-041-the-map-renderer.md) §5.2 (*no SLD*) |

---

## 1. Context

ArcGIS Server's WMS takes `SLD` and `SLD_BODY` on GetMap and answers `GetStyles`, from OGC's SLD profile of WMS. A
WMS client that restyles a layer for one map, such as a print service, a thematic viewer or a QGIS user who wants
other colours without owning the service, sends one. ADR-033 §2E rejected SLD **as an authoring format** on the
owner's preference. It also said where SLD would have to go if it ever came: *"a serialisation at the boundary and
never the canonical form."* The owner has now asked for it, and this is that boundary.

## 2. Alternatives considered

### Alternative A — SLD in and out through `drawingInfo`, for the request only (chosen)

- **GetMap's `SLD_BODY`** carries an SLD 1.0 or 1.1 document. Each `NamedLayer` it names is drawn with its
  `UserStyle` for this request and for no other. Nothing is stored. A `NamedStyle` of `default` is the layer's own
  style. A layer it does not name keeps its own style. With `SLD_BODY`, `LAYERS` may be left out and the layers are
  the SLD's, in its order; `STYLES` may be left out too.
- **Read into an Esri `drawingInfo`, then into CIM** by the reading every pasted ArcGIS renderer takes (ADR-052). So
  SLD gets no translator of its own into the canonical model, and its losses are the ones that reading already names.
  Rules map to renderers:
  - With no filter, a rule is one symbol (`simple`).
  - `PropertyIsEqualTo` on one property gives unique values, and an `ElseFilter` rule becomes the default symbol.
  - `PropertyIsLessThan[OrEqualTo]`, `PropertyIsGreaterThan[OrEqualTo]` and `PropertyIsBetween` on one numeric
    property, alone or joined by `And`, give class breaks read by their upper bounds.
  - Polygon, line and point symbolizers become `esriSFS`, `esriSLS` and `esriSMS`. Widths and sizes go from pixels to
    points. A polygon with no `Fill` is unfilled, not grey.
- **Refused by name** rather than drawn as though it were not there: any other filter, rules filtering by two
  properties, equality mixed with ranges, filtered rules beside an unfiltered one, a `UserLayer`, a rule with no
  symbolizer, and a document over 64 KB. The code is `StyleNotDefined`, at `SLD_BODY`.
- **Drawn with what is lost reported:** a scale range is ignored and the rule draws at every scale. A text or raster
  symbolizer is skipped. A marker other than a circle is drawn as a circle, because that is the one marker shape this
  server draws.
- **`SLD`, a URL, is refused.** Fetching an address a caller names is this server making requests to wherever a
  stranger points it. `SLD_BODY` carries the same document, and the refusal says so.
- **`GetStyles`** answers each layer's style as SLD 1.1.0 with Symbology Encoding. It is derived from the CIM document
  through the same `drawingInfo` the FeatureServer publishes. What did not survive the trip is written in the style's
  `se:Abstract`. A renderer with no SLD form (a heat map, dot density, charts) is answered as a `NamedStyle` of
  `default` with the reason beside it. Sent back as `SLD_BODY`, it draws the layer's own colours.
- **The capabilities say so.** 1.3.0 adds `sld:UserDefinedSymbolization` (`UserStyle="1"`, `UserLayer="0"`, no
  remote WFS or WCS, no inline features) and lists `sld:GetLegendGraphic` and `sld:GetStyles`, with the SLD 1.1
  capabilities schema. 1.1.1 uses its DTD's own `UserDefinedSymbolization`, `GetLegendGraphic` and `GetStyles`.

### Alternative B — SLD to CIM directly

**Against:** it is a second translation into the canonical model beside the `drawingInfo` reading, and it would draw
nothing more. The server draws what CIM projects to (`CimFill`, `CimStroke`, a circular `CimMarker`), and
`drawingInfo` already carries all of that.

### Alternative C — Store an SLD as a layer's style

**Against:** ADR-033 §2E, and the owner's own words. A layer's style is CIM. An SLD is something a client sends.

## 3. Counterarguments to the preferred option

- *Dash arrays become a dash.* `drawingInfo`'s line styles are named, and an SLD's `stroke-dasharray` is drawn as
  `esriSLSDash`.
- *The owner does not like SLD.* That is recorded in ADR-033 and is not overturned: nothing here stores, edits or
  authors one, and Studio does not show it.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| A magenta `SLD_BODY` draws magenta and none of the layer's colour; the next map is the layer's own; GetStyles answers SLD 1.1 whose polygon symbolizer, sent back, draws the layer's own colour; `PropertyIsLike` is `StyleNotDefined`; `SLD=` is `InvalidParameterValue`; both capabilities documents declare it | `WmsConformanceTests.A_style_sent_as_sld_body_draws_for_that_request_and_get_styles_answers_one_that_draws_the_same` | this repository |

## 5. Decision

As §2, Alternative A.

## Conditions

1. OGC's WMS 1.3.0 suite still passes with the SLD capabilities in the document.
2. GetStyles' documents validate against the SLD 1.1.0 schema, and an SLD exported from QGIS draws.

## 6. Consequences

**Positive.** A WMS client can restyle a layer for one map without being able to change it, as with ArcGIS.

**Negative.** A third symbology vocabulary at the edge, after `drawingInfo` and MapLibre. It goes through the first.

**State.** None.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | SLD sent to a WMS classifies by one property with equality or ranges | True of what QGIS and GeoServer write for categorised and graduated styles |

## 8. Dependencies

**Depends on:** ADR-033, ADR-041, ADR-052.

**Depended on by:** —

## 9. Revisit triggers

- A client that sends `UserLayer` with inline features.
- A request to store an SLD as a style. That would be ADR-033's decision to reopen, not this one's.
