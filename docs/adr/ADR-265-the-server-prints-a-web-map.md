# ADR-265 — The server prints a web map, as ArcGIS's print service does

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` — the wire shapes are the public reference's and were driven by the ArcGIS API for Python; the page is this server's own composition, raster only, and its cost is not yet measured |
| **Decided** | 2026-11-04, by owner decision: item #13 of the ArcGIS veteran's review of v1.0.499, *a print service clients can call*, taken overnight on the owner's instruction. The choices marked **INFERRED** are this session's and wait for the owner. |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-133](ADR-133-the-map-viewer-prints.md) (Alternative B, which it did not choose, is now built beside its choice) |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

[ADR-133](ADR-133-the-map-viewer-prints.md) made Studio's Map Viewer print in the browser: the view drawn again at the
page's pixels and composed with a title, legend, scale bar and north arrow. It recorded a server-side print service —
ArcGIS's *Export Web Map Task* — as Alternative B, not chosen because it means a renderer of every layer kind on the
server, and set the revisit trigger *"an ArcGIS client is measured calling a print service on this server"*.

That trigger is the veteran's #13. Every ArcGIS client that prints asks the **server** to: Map Viewer and Experience
Builder's Print widget, the Maps SDK's `print`, ArcGIS Pro's sharing of a print service and the ArcGIS API for Python
all call `…/Utilities/PrintingTools/GPServer/Export Web Map Task`, found from `portals/self`
`helperServices.printTask.url`. With none there, none of them can print from this portal; the browser print of ADR-133
serves Studio's own viewer only.

## 2. Alternatives considered

### Alternative A — Keep ADR-133's browser print alone

**For:** nothing new to bound. **Against:** every ArcGIS client's print stays broken, which is the review's finding.
Not chosen.

### Alternative B — Fetch each web-map layer over HTTP and composite, as a general print service does

**For:** draws any layer from anywhere, the basemap from ArcGIS Online included. **Against:** a server that fetches the
addresses a caller writes into a JSON parameter is an SSRF door with a renderer attached — every hop, redirect and DNS
answer would need the egress checks of `EgressProxy` and a fetch budget; and for this server's own layers it would be a
second, slower drawing of what the renderer already draws. Not chosen (§5.3).

### Alternative C — Draw this server's own layers through its own renderer; leave the rest out and say so *(chosen)*

## 3. Counterarguments to the preferred option

- **A printed map without its basemap.** A web map over ArcGIS Online's basemaps prints with the operational layers on
  white. The result's messages name each layer left out, and a basemap this server serves (ADR-086) prints.
- **Raster PDF.** The page is one JPEG inside a PDF: text is pixels and does not select; ADR-133 had the same limit.
- **The page is ours, not ArcGIS's.** The template names are ArcGIS's so the Print widget's list works; the arrangement
  is not, and a shop with its own `.pagx` layouts gets this server's layout under their names only for the eight
  defaults.
- **Drawn with the service's own symbols unless the web map sends a renderer** for a single feature layer; a map
  service's dynamic layer renderers and `layerDefinition.drawingInfo` on a map service sublayer are not applied.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| The service, its two tasks, `execute`, `submitJob`, the job and result resources answer in the documented shapes, and `portals/self` names it | `ThePrintServiceDrawsAWebMapTests.The_service_its_tasks_and_its_layouts_answer_and_the_portal_names_it` | this repository |
| A `MAP_ONLY` PNG32 is a PNG of the asked size with the layer drawn; a job's result is the same kind of file | `…A_map_only_png_is_a_png_of_the_asked_size_with_the_layer_drawn` | this repository |
| An A4 Landscape PDF is a PDF of 841.68 × 595.44 points holding the page | `…An_a4_landscape_pdf_is_a_pdf_of_the_page`, `PrintPageCompositionTests` | this repository |
| A layer at another address — another host, a link-local metadata address — is not drawn and the result says so; the file equals a print of nothing | `…A_layer_at_another_address_is_not_fetched_and_the_result_says_so` | this repository |
| A private layer printed by an anonymous caller is not drawn (the file equals a print of nothing), and is drawn for its owner | `…A_private_layer_is_not_drawn_for_an_anonymous_caller` | this repository |
| `Web_Map_as_JSON` is read as the specification writes it | `WebMapPrintRequestTests` | ExportWebMap specification, ArcGIS REST API reference |
| The ArcGIS API for Python 2.4.3 finds the service from `helperServices.printTask`, imports it as a toolbox, runs `export_web_map_task` to a PDF it downloads, and reads `get_layout_templates_info_task` | run against the local fixture, 2026-11-04 | `arcgis.geoprocessing.import_toolbox`; its `Item.create_thumbnail` fails in the client itself (`property 'session' of 'Toolbox' object has no setter`) before any request |
| The page's scale bar and printed scale agree: 1 km drawn 135 px at 96 dpi on a page printed 1:27,920 | A4 Portrait over the fixture's buildings | this repository |

## 5. Decision

1. **The service.** `Utilities/PrintingTools/GPServer` with `Export Web Map Task` and `Get Layout Templates Info Task`,
   synchronous as ArcGIS's default print service is: `execute` answers `Output_File` (`GPDataFile`, `{url}`) and the
   templates task `Output_JSON`. `submitJob` is also answered, drawing the print under the same bounds before it answers
   and keeping the finished job for ten minutes (`jobs/{id}`, `results/{param}`), handed between a site's nodes as the
   output directory's files are. `portals/self` names it in `helperServices.printTask.url`; the `Utilities` folder lists
   it as a `GPServer`.
2. **The request.** `Web_Map_as_JSON` (`mapOptions.extent`, `spatialReference`, `scale`; `operationalLayers` and
   `baseMap.baseMapLayers` with `url`, `visibility`, `visibleLayers`, sublayer and layer `definitionExpression`, a single
   feature layer's `drawingInfo`; `exportOptions.outputSize` and `dpi`; `layoutOptions` title, author, copyright,
   `scaleBarOptions`, `legendOptions`), `Format` and `Layout_Template` (`WebMapPrintRequest`).
3. **Only this server's own layers are drawn, and nothing is fetched** — **INFERRED**, the conservative reading of
   *"a layer at another origin is SSRF"*. A layer is this server's when its URL has this request's host and port (or no
   host), under `/rest/services` after the `/arcgis` and `/server/rest` addresses are mapped: a MapServer, FeatureServer
   (or one of its layers), VectorTileServer (drawn from its layers) or ImageServer. It is resolved from the catalogue and
   drawn by `WmsEndpoints.DrawLayerAsync` / `ImageServerEndpoints.DrawIntoMapAsync`, the code MapServer `export` and WMS
   draw with. Any other address is left out with a warning in the result's messages; so is a feature collection or
   graphics. No admin allowlist is offered.
4. **As the caller, never as the server.** Each layer is drawn only if its service is visible to the caller and running
   (`ServiceLookup.ReadableQuietlyAsync`, the doors' own check) and its face answers, and only the sublayers that answer
   `Query` (ADR-031 ceilings). A token inside the web map is ignored (**INFERRED**). Absent and not shared are one
   message, so a print says nothing about what exists. The service itself is open to any caller, as its tasks hold no
   data (**INFERRED**; ArcGIS's print service is usually public).
5. **The page** (`PageComposer`, `PageTemplate`, Tier 1). `MAP_ONLY` is the map alone at `outputSize`; A3, A4, Letter
   ANSI A and Tabloid ANSI B, each Landscape and Portrait, are pages with a title, the map in a frame, a legend (right of a
   landscape map, under a portrait one; per class of the layer's symbology, as MapServer `legend` draws it, filtered by
   `legendOptions`), a metric and a non-metric scale bar in `scaleBarOptions`' units, the printed scale, a north arrow,
   the author and the copyright text. The map is drawn through a `PlacedCanvas`, which places the renderer's screen
   pixels on the page at `dpi / 96`, so symbols and labels keep their size at any dpi and visible ranges are judged at
   the true scale; text is the renderer's bundled faces (ADR-100). The scale bar measures the ground between two points a
   few pixels apart at the frame's centre, carried into EPSG:4326 — ADR-133's measure — and is left off, said, when that
   fails.
6. **Formats.** `PDF` (one page of the paper's size holding the page as a JPEG, written by `PdfPage`), `PNG32`, `PNG8`
   (a full-colour PNG, as MapServer `export` answers `png8`) and `JPG`. **GIF, EPS, SVG and SVGZ are refused by name.**
7. **The file** is kept in the output directory MapServer `export` already uses (`ExportOutputs`,
   `/rest/directories/arcgisoutput/{128-bit name}`), ten minutes, at most 1,024 files and 256 MB, handed between nodes —
   **not** a new store under the state path, which would be a second bounded file store doing what that one does.
8. **Bounds.** Two prints at once per node; a third waits ConnectionBudget's five seconds and is refused with its
   503 and `Retry-After` (`ConnectionBudgetFullException`, ADR-046). Sixty seconds per print. At most 300 dpi, 64 layers,
   20 million page pixels (A3 at 300 dpi and a little over) and, for `MAP_ONLY`, the server's `MaximumImageWidth` ×
   `MaximumImageHeight`. Each layer's reads go through the per-source admission every read takes.

## 6. Consequences

**Positive.** ArcGIS clients print from this portal; ADR-133's browser print stays for Studio's own viewer, which draws
what it already holds and needs no round trip.

**Negative.** Not done, each refused or said rather than faked: `mapOptions.rotation` (the map prints north up and the
result says so); a vector or layered PDF; GIF, EPS, SVG, SVGZ; custom layout files and `customTextElements`; a
legend for an image service or a basemap; layer `opacity`; `drawingInfo` on map-service sublayers; time
(`mapOptions.time`); layers at other addresses, ArcGIS Online basemaps among them. A scale given in a feet-based
reference is read as metres for the frame (the printed scale bar is still measured on the ground).

**Ports created.** None: the page is drawn through `IMapCanvas`.

- **State.** None persistent: finished files and jobs are held in memory for ten minutes (and in the site handoff
  directory on a site of several nodes), bounded by count and bytes.

## 7. Conditions

1. **A browser client prints through it**: the Maps SDK's `print` or Map Viewer's / Experience Builder's Print widget
   against a fixture, the PDF opened. Only the ArcGIS API for Python has been run (§4).
2. **The cost is measured**: an A3 page at 300 dpi over the fixture's largest layer — time, peak memory, file size —
   against the sixty-second deadline and the two-print bound, which are this session's guesses.
3. **The owner confirms the INFERRED choices**: that a layer at another address is left out rather than fetched through
   an allowlist (§5.3), that a token inside the web map is ignored (§5.4), and that the service is open to anonymous
   callers (§5.4). **DISCHARGED 2026-11-04, by owner decision** (*"Dışarıda kalsın"* and *"Hepsi önerildiği gibi"*): all three confirmed as built, and the
   Utilities folder's sign-in hint for an anonymous reader with them.

## 8. Dependencies

**Depends on:** [ADR-133](ADR-133-the-map-viewer-prints.md) (the page it composes, measured the same way),
[ADR-041](ADR-041-the-map-renderer.md) (the renderer), [ADR-100](ADR-100-labels-in-more-scripts.md) (the faces),
[ADR-046](ADR-046-admission-control-bounds-the-queue-not-the-wait.md) (the refusal it reuses),
[ADR-222](ADR-222-geoprocessing-is-built-in-analysis-tools.md) (the GP resource shapes it shares).

**Depended on by:** —

## 9. Revisit triggers

- An owner who wants another origin's layers printed (§5.3, condition 3): then an allowlist with every hop checked.
- A client measured sending `rotation`, SVG or a custom layout that a shop depends on.
- A print measured past the deadline at the default dpi.

## 10. Dissent

None recorded.
