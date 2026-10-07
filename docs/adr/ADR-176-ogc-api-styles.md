# ADR-176 — OGC API Styles, read-only, over the styles the tile faces already serve

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` — the documents, the negotiation and SLD 1.0 beside 1.1 are unit-tested, the face passes its own conformance cases against a server, and its SLD validates against both schemas; the specification is an unapproved draft in two disagreeing versions, no OGC executable test suite exists for it, and no client has yet read a stylesheet from this face |
| **Decided** | 2026-10-06 by owner decision (*"hadi ogc yi de bitirelim"*, choosing Records, Maps, Styles and Processes); the design below is INFERRED where marked |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | — |

---

## 1. Context

On 2026-10-06 the owner asked to finish the OGC faces (*"hadi ogc yi de bitirelim"*) and named OGC API Styles among
the four still missing. Styles already exist on this server in three places, none of them an OGC API:

- **Stored Mapbox styles.** A vector tile service carries up to twenty named styles, one of them the default
  (ADR-094). They are written through `/admin/services/{name}/styles/{style}` and served at
  `VectorTileServer/resources/styles/{name}.json` and `root.json`.
- **The generated style.** It is served at `root.json` when no stored style is the default. It is derived from each
  layer's CIM symbology (ADR-033 §5a).
- **SLD 1.1.** WMS `GetStyles` writes it from the same symbology, through `drawingInfo` (ADR-171).

OGC API – Styles (OGC 20-009) is the standard way for a client to find these styles and fetch them in an encoding it
can read. It is **not approved**. The repository's `1.0.0-draft.1` (May 2021) and `1.0.0-draft.2` (May 2026, the
editor's draft read on 2026-10-06) disagree:

- **The metadata resource.** Draft 1 puts `/styles/{styleId}/metadata` in `core`. Draft 2 removes it.
- **SLD conformance classes.** Draft 1 has the classes `sld-10` and `sld-11`. Draft 2 merges them into `sld-se`.
- **The `f` values.** Draft 1 spells them `mapbox`, `sld10` and `sld11`. Draft 2 spells them `mbgl` and `sld`.
- **The list.** Draft 2 adds an optional `default` member to the list.

## 2. Alternatives considered

### Alternative A — A read-only face over the existing styles, at `/ogc/styles/v1` (chosen)

**For.** Nothing new is stored, and every stylesheet is one the server already serves at another address. The
service is the unit, and services are found the way OGC API Tiles and WMTS find them (`TileFaces`), so a style is
listed exactly when the tiles it draws are served to the same caller.

**Against.** Only vector tile services have styles here. A feature service whose tile face is off has no entry, even
though WMS writes SLD for its layers.

### Alternative B — Styles as sub-resources of OGC API Tiles' or Features' collections

20-009 allows `{baseResource}` to be a collection: `/collections/{id}/styles`.

**For.** A client that already has a collection finds its styles one link away.

**Against.** No conformance class requires it. It would put the same style at two addresses on two faces, and the
features face's unit is a layer while a style's unit is the service. Not done. It can be added later without moving
anything (§9).

### Alternative C — Also implement `manage-styles`

**For.** It makes the face symmetrical, and a draft-2 Mapbox class depends on it.

**Against.** Styles are already written through `/admin/services/{name}/styles`, behind ADR-075's ownership check,
the 20-style bound and ADR-028's validation. A second write path to the same rows would be a second set of rules for
who may restyle a service. **Out of scope** for this decision; it would need its own ADR.

## 3. Counterarguments to the preferred option

- **No conformance for the encodings.** A read-only face cannot claim `mapbox-styles`, `sld-10`, `sld-11` or
  `sld-se` (§5.3). A client that picks a server by those classes passes over this one. It must read the `type` of
  each `stylesheet` link instead.
- **The symbology style is not one style.** Its Mapbox and SLD stylesheets are two derivations, and each loses
  something different. Each names its losses inside itself (ADR-171 for SLD). The metadata marks both `native: false`.
  A reader who expects two encodings of one thing will still see differences.
- **A stale stored style.** A stored style that no longer fits its service is answered with the generated style, as
  `resources/styles/{name}.json` already does. Under its own id that is a substitution. The header
  `Graticula-Style-Stale` says so; the JSON body does not.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| There is no OGC executable test suite for OGC API Styles | `gh api search/repositories?q=org:opengeospatial+ets-ogcapi` lists features, processes, EDR, maps, coverages and tiles, and nothing for styles; `repos/opengeospatial/ets-ogcapi-styles` is a 404; Docker Hub `ogccite` lists 54 images and none for styles. Both checked 2026-10-06 | GitHub, Docker Hub |
| The draft's own abstract test suite is not usable | Annex A of draft 2 is commented out with *"The ATS needs to be updated…"*. Draft 1's `/conf/core/1` was used as the test method for the list, the stylesheets and the metadata | `opengeospatial/ogcapi-styles@9f80cb0` `standard/annex-a.adoc` |
| Every requirement of the encoding classes is about POST or PUT | Draft 1 `/req/sld-10/*`, `/req/sld-11/*` and `/req/mapbox-style/*`, and draft 2 `/req/sld-se/*` and `/req/mapbox-style/*`, each begin *"Every POST or PUT operation of the server that accepts a stylesheet…"*. Draft 2's Mapbox class depends on `manage-styles` | `standard/clause_10_style_encodings.adoc`, tag `part1-1.0.0-draft.1` |
| Id, negotiation, address resolution, documents, and SLD 1.0 against 1.1 | `OgcStylesTests`, 44 tests passed with the existing WMS and SLD tests (Debug, 2026-10-06) | this repository |
| The list, the stylesheets by `Accept`, the private service, the 404 problem | `OgcStylesConformanceTests`, 6 of 6 passed against the fixture server (port 18493, database `gisportal`), 2026-10-06 | this repository |
| The SLD 1.0 and 1.1 documents are schema-valid | lxml 6.0.2 against `schemas.opengis.net/sld/1.0.0` and `/1.1.0`: the symbology SLDs of the fixture's four services (simple renderers), and a scratch service given a unique-value renderer (with `ElseFilter`) and then a class-breaks renderer. All valid, 2026-10-06 | this session |
| A Mapbox stylesheet's absolute addresses answer | The generated style's source, `glyphs` (filled in) and `sprite.json` answered 200 from the fixture server, 2026-10-06 | this session |

## 5. Decision

### 5.1 Resources

The face is read-only, at `/ogc/styles/v1`:

- `/` — the landing page, with links `service-desc`, `service-doc`, `conformance` and `…/rel/ogc/1.0/styles`. The
  last is also given in draft 1's capitalised spelling, `…/rel/OGC/1.0/styles`.
- `/conformance`
- `/api` — an OpenAPI 3.0 document written by hand.
- `/styles` — the styles of the services the caller may see.
- `/styles/{styleId}` — the stylesheet.
- `/styles/{styleId}/metadata` — draft 1's style metadata.

Every JSON answer also has an HTML form, chosen by `f=html` or by `Accept`. A refusal is an RFC 7807 problem:

- **404** — a style that is absent, or forbidden to this caller. Both get the same answer (ADR-018).
- **400** — an `f` this face does not know.
- **406** — an encoding this style is not offered in.
- **503** — the catalogue cannot say.

### 5.2 Which styles there are (INFERRED)

Each vector tile service the caller may see as tiles has these styles:

- **`{collectionId}._symbology`.** The style its layers' own symbology draws. It is offered as Mapbox (the generated
  `root.json`) and as SLD 1.1 and SLD 1.0 (WMS `GetStyles`' document).
- **`{collectionId}.{name}`.** One for each stored style. It is offered as Mapbox only, as written. A stored style
  is never translated into SLD. That would be a converter between styling languages whose losses nobody has
  measured, presented as the author's style.

The service is found by `TileFaces`, which is also how a single style is found. A style of a service the caller may
not see is therefore absent from the list, and every address of it answers 404.

The list carries no `default` member: the base resource is the whole server, which has no default style. Each style
carries `graticula:default`, which is true for the style `root.json` serves. Draft 1 and draft 2 are both read:

- **Metadata.** It is served, as draft 1 requires. Draft 2 does not forbid it.
- **`f` values.** Draft 1's values are written in links. Both drafts' values are read.

### 5.3 What is claimed

The face claims these classes, each proved by `OgcStylesConformanceTests` in both directions:

- From OGC API Common: `core`, `landing-page`, `json`, `html` and `oas30`.
- From OGC API Styles: `core` and `html`.

Draft 1's HTML class has a Testbed-15 URI in its requirements table. The `conf/html` form is claimed instead
(INFERRED).

The encoding classes are **not claimed**: `mapbox-styles`, `sld-10`, `sld-11` and `sld-se`. Their requirements
constrain only writes (§4), so a read-only server would meet them vacuously, and claiming them would tell a client it
may upload SLD here. `manage-styles`, `style-validation`, `resources` and `manage-resources` are not implemented.

### 5.4 The style id (INFERRED)

The id is the service's id on the tile faces (`folder.name`), a dot, and the style's name. It is read back by its
last dot. This works for three reasons:

- **The split is safe.** A style name never has a dot in it (`StyleNames`).
- **The symbology suffix cannot collide.** `_symbology` cannot be a stored name, because a stored name starts with a
  letter or digit. A stored style called `symbology` or `default` therefore never changes what the id means.
- **It is URL-safe and stable.** Nothing needs escaping. The id changes only when the service's folder or name or the
  style's name changes, which also moves its ArcGIS address.

### 5.5 Negotiation (INFERRED)

- **`f`.** It wins over `Accept`. Both drafts' spellings are read: `mapbox` and `mbgl`; `sld11` and `sld`;
  `sld10`; and `html`. Draft 2's single `sld` means SLD 1.1, this server's SLD (ADR-171).
- **`Accept`.** Without `f`, the face takes the most preferred acceptable type the style has, by `q` and then by
  order:
  - `application/json` gives Mapbox.
  - `application/xml` and `text/xml` give SLD 1.1.
  - An SLD type with no `version` gives SLD 1.1.
  - `text/html` gives the metadata page.
  - A wildcard, or no `Accept` header at all, gives the native stylesheet, Mapbox. A MapLibre client given the bare
    address therefore gets a style it can draw.

### 5.6 A Mapbox stylesheet's addresses (INFERRED)

A style for a VectorTileServer names its source `../../`, and its glyphs and sprite `../fonts/…` and `../sprites/…`.
Read from `/ogc/styles/v1/styles/{id}`, those addresses lead nowhere.

Each address a style fetches from is therefore made absolute against the service's
`VectorTileServer/resources/styles/root.json`:

- a source's `url`, `tiles` and `data`
- the `sprite`, either a string or an array
- the `glyphs`

The client placeholders in braces are kept. Nothing else in the document is touched. A style with nothing relative
in it is served byte for byte, which keeps ADR-028's rule.

### 5.7 SLD

The SLD has one `NamedLayer` for each layer. Each is named as the layer is named in a tile and in WMS, which is what
20-009's `/req/core/style-feature-type` matches. The `UserStyle` is named with the style id, and each
`FeatureTypeStyle` names its layer (`/rec/sld-se/style-names`).

SLD 1.0 is produced from the 1.1 tree:

- elements move into the `sld` namespace
- `SvgParameter` becomes `CssParameter`
- the `Description` wrapper is unwrapped, leaving `Title` and `Abstract` in place

It is not written by a second writer.

WMS `GetStyles` is unchanged. The new parameters default to what it wrote before, and the layer-to-SLD derivation was
moved into `WmsEndpoints.SldLayerOf` so that both faces call one function.

## 6. Consequences

**Positive.**

- A standards client can find every style of the services it may see and fetch each in a declared encoding.
- The layers' symbology is available as SLD 1.0 for older SLD readers.
- Nothing new is stored.

**Negative.**

- A fourth address for the same Mapbox style.
- The face exists only for vector tile services.
- The list costs one extra catalogue query, which returns names only (`PostgresLayerCatalog.ListStyleNamesAsync`).
- While the store is unreachable, the list answers 503 rather than a partial list. Draft 1's
  `/req/core/styles-success` C requires every style to be listed, and the stored styles have no remembered copy.

**Ports created.** None. No Tier 2 dependency.

**State.** None. The only new query reads migration 62's tables.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | A style name never contains a dot and never starts with `_` | Holds by `StyleNames.TryValidate`; unit-tested |
| — | 20-009 keeps `{baseResource}/styles` and `/styles/{styleId}` when it is approved | Unverified; it is a draft |

## 8. Dependencies

**Depends on:** ADR-018, ADR-028, ADR-033, ADR-094, ADR-097, ADR-171.

**Depended on by:** —

## 9. Revisit triggers

- 20-009 is approved, or a draft changes `core`'s resources or the encoding classes' requirements.
- OGC publishes an executable test suite for OGC API Styles.
- A client is found that chooses a server by the encoding conformance classes and passes this one over.
- An owner asks for styles of feature services whose tile face is off, or for styles at `/collections/{id}/styles`.

## 10. Conditions

1. **Run the full solution build with 0 warnings and 0 errors, in Debug and in Release.** On 2026-10-06 drive C: ran
   out of space during the Debug solution build. Every project compiled, but copying native runtimes into
   `Graticula.Conformance.Tests/bin` failed. That project was then built alone (0 warnings, 0 errors). The Release
   build was left to integration at the coordinator's instruction. **DISCHARGED 2026-10-06 at integration:** with
   Records, Maps and Processes beside it, `graticula.sln` builds with 0 warnings and 0 errors in Debug and in Release,
   and the architecture tests pass (47 of 47).
2. **No OGC executable test suite exists** (§4), so the claim rests on the specification's requirements and on the
   tests written here. When one is published it is run, and its findings replace this condition.
3. **A client reads a stylesheet from this face**: a MapLibre page pointed at `/styles/{id}?f=mapbox` that draws the
   tiles, and QGIS or another SLD reader that loads `?f=sld11` or `?f=sld10`. No client has done either yet.
   **DISCHARGED 2026-10-07, and the MapLibre half found a defect first.** MapLibre GL JS 4.7 in headless Chrome,
   given `hosted.ci_parcels._symbology?f=mapbox`, asked for `tile/14/6203/9688.pbf` and drew nothing: the style's
   source was the absolute VectorTileServer address, whose document names its tiles relatively — ArcGIS's JavaScript
   API resolves that against the document, MapLibre does not. A source that is the service's own VectorTileServer is
   now pointed at the service's OGC API Tiles TileJSON when it is tiled on WebMercatorQuad, whose tile addresses are
   absolute; MapLibre then draws the parcels (9 rendered features). QGIS 3.28 loads both `?f=sld11` and `?f=sld10`
   with `loadSldStyle` and draws the layer with them.
4. **Schema validation of the SLD in the test suite.** Validation against the schemas was done by hand (§4). No
   test repeats it, because the suite does not fetch schemas from the network. **PARTLY DISCHARGED** 2026-10-06 by
   the manual run.

## 11. Dissent

None recorded. The scope choices — read-only, vector tile services only, no translation of stored styles into SLD —
are INFERRED and are listed for the owner to confirm.
