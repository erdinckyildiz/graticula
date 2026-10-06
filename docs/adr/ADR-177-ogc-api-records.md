# ADR-177 — OGC API Records is a searchable catalogue of the portal's items

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` — every claimed class has a test against a running server, and OWSLib 0.25 and QGIS 3.28's MetaSearch backend read it; no OGC executable test suite exists to hold it to the standard, and the record's vocabulary is this server's rather than a community's |
| **Decided** | 2026-10-06 — the owner asked for it: *"hadi ogc yi de bitirelim"*, choosing Records, Maps, Styles and Processes among the OGC APIs still missing. Every design choice below marked `INFERRED` is this ADR's reading, not the owner's words |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

The server answers OGC API Features (ADR-042) and Tiles (ADR-097), WMS, WFS, WMTS and WCS, and its portal surface
(ADR-040) lists what it publishes to ArcGIS clients. A client that speaks OGC rather than ArcGIS had no way to *find*
what is published: it had to be given each address. OGC API – Records – Part 1: Core (OGC 20-004r1, 1.0) is the
standard way to search a server's holdings — QGIS's MetaSearch and OWSLib speak it — and the owner asked for it on
2026-10-06.

The question is not whether to add it but what a record is, where the list of records comes from, and what may be
claimed.

## 2. Alternatives considered

### Alternative A — One searchable catalogue whose records are the portal's items (chosen)

- **At `/ogc/records/v1`**, beside `/ogc/features/v1` and `/ogc/tiles/v1` (ADR-042 §5.1's versioned path). Landing page,
  `/conformance`, `/api` (OpenAPI 3.0), `/collections`, `/collections/catalog`, `/collections/catalog/items` and
  `/collections/catalog/items/{recordId}`, JSON and HTML, refusals as RFC 7807 problems — the shape of the other two
  OGC API faces.
- **A record is a portal item.** The listing is `PortalEndpoints.ListAsync`, which is the body the portal's
  `/sharing/rest/search` already ran, moved so that both read it: the same services (one item per FeatureServer,
  MapServer and VectorTileServer face, as the portal lists them since 2026-09-15), saved web maps and image services,
  under the same `LayerAccess` sharing rule, for the same caller. A record exists exactly when the portal would show its
  item to that caller, and its id is the item's id — nothing is stored.
- **What a record carries** and where it comes from: `type`, `title`, `description` (else the summary), `keywords`
  (the item's tags), `created` and `updated` — all read from the portal item object itself (`PortalQuery.Field`), so
  the two cannot disagree; `rights` from the owner's OGC access constraints (ADR-167); `geometry` and
  `properties.extent` from the item's WGS 84 extent, as the portal's item document computes it; `formats` one per
  address; `time` null. No `contacts` (the portal withholds owners from strangers, Q-127), no `license` (nothing records
  one), no `themes` (tags are free words, and a theme names a vocabulary this server does not have), no `externalIds`.
- **Links.** `describes` to each address the item answers at: its ArcGIS REST address; for a feature service its
  `FeatureServer/WFSServer` and each layer's OGC API Features collection; for a map service its `MapServer/WMSServer`;
  for a vector tile service its OGC API Tiles collection; for an image service its `WMSServer`, `WMTS` and `WCSServer`;
  for a web map its Web Map JSON. Each OGC address is linked only when the face's own rule would answer it — the WMS,
  WFS and OGC API Features rules moved into `ServiceFaces` and those three faces now read them from there, the tiles
  rule is `TileFaces.Serves`, the image rules `PublishedCoverage.OffersOgc` — and only while the service runs. Also
  `related` to the portal item and the map viewer, `preview` to the thumbnail.
- **Query parameters** — all of 20-004r1 Table 12 that name something a record has: `bbox` (CRS84), `datetime`, `limit`
  with `offset` and `next`/`prev` links, `q`, `type`, `ids`, and `externalIds` (which matches nothing, because no
  record carries one). `bbox`, `datetime`, `limit` and `offset` are read by the features face's own parsers, made
  public for it. An unknown parameter is a 400, as on the features face.
- **Conformance claimed:** Features Part 1 `core`, and Records `record-core`, `record-collection`,
  `record-core-query-parameters`, `records-api`, `searchable-catalog`, `json`, `html`, `oas30`, `autodiscovery`. Not
  claimed: `sorting`, `filtering`/CQL2, the profile parameter, crawlable and local-resources catalogues.

### Alternative B — A record per service, not per item

**For:** one service is one thing to a person; three records named `roads` (feature, map, vector tile) look like
duplicates. **Against:** it would be a second listing that disagrees with the portal about what exists, and the three
faces are different resources with different addresses and clients — ArcGIS lists them as three items for that reason.

### Alternative C — A catalogue per item type (`services`, `webmaps`, `images`)

**For:** a client could open only the kind it wants. **Against:** `type` already does that in one request, a search
across kinds would become five, and nothing in the standard or in the clients prefers it.

### Alternative D — Records stored in a table, written when an item changes

**For:** paging and search in SQL; ISO-style metadata could be added. **Against:** a second copy of the catalogue with
its own way to go stale — the thing ADR-040 and ADR-079 condition 3 say this listing must not grow — for a scale
(100–1,000 services) a listing in memory answers.

## 3. Counterarguments to the preferred option

- *The vocabulary is the server's.* `type` is `Feature Service`, `Map Service`, `Vector Tile Service`, `Image Service`
  or `Web Map` — the portal's item types — not Annex C's URIs or ISO 19115 scope codes. A catalogue harvester that
  expects a community vocabulary learns nothing from it. (`INFERRED`: the portal's words were chosen because they are
  what an item *is* here and what ArcGIS clients already filter by; Annex C is informative and names protocols, and one
  item here is reachable through several.)
- *`datetime` tests the record's change time, not the data's.* 20-004r1 Table 12 speaks of *the temporal extent of the
  record*, and Features Part 1 lets the server choose the temporal property; a service's data may have no time at all.
  A client wanting *data from 2025* is answered *items changed in 2025*. (`INFERRED`.)
- *A `bbox` search reads every candidate's extent.* A service's extent comes from each layer's described shape, which a
  cold cache reads from the source — the reason the portal leaves it off a search. Here it is read for every candidate
  that survives the other predicates when `bbox` is given, and otherwise only for the page. At 1,000 services with a
  cold cache the first `bbox` search pays a describe per layer.
- *Three records per service* (Alternative B's case) is real noise in a result list.
- *No MetaSearch in QGIS 3.22 LTR*: its MetaSearch speaks CSW only.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Every claimed class has a test and every test's class is claimed | `OgcRecordsConformanceTests.No_class_is_claimed_without_a_case_that_proves_it` | this repository |
| Landing, conformance, OpenAPI 3.0, the catalogue as `application/ogc-catalog+json` with `itemType: record`, autodiscovery link | `OgcRecordsConformanceTests.The_landing_page_conformance_api_and_catalogue_link_each_other` | this repository |
| Private: absent for a stranger and a member (search and by id, 404); organisation: the member sees it and the stranger does not; public: everybody; the portal lists the same item to the owner | `OgcRecordsConformanceTests.A_service_is_a_record_only_for_those_it_is_shared_with` | this repository |
| Turning WFS off removes the WFS link from the record | same test | this repository |
| Every link of every anonymous record answers 200 (135 distinct links on the fixture of 21 records) | `OgcRecordsConformanceTests.A_record_is_a_feature_with_its_links_and_they_answer`; measured 2026-10-06 | this repository |
| `q`, `type`, `ids`, `externalIds`, `bbox`, `datetime`, `limit` and `next`/`prev` select as Table 12 says; walking `next` returns every record once | `OgcRecordsConformanceTests.The_query_parameters_select_as_table_12_says`, `OgcRecordsTests` (18 unit tests) | this repository |
| Every resource has an HTML page carrying its links | `OgcRecordsConformanceTests.Every_resource_has_an_html_page_with_its_links` | this repository |
| OWSLib 0.25 (QGIS 3.28's) reads landing, conformance, API, catalogue, items, one record, `q` and `bbox`; QGIS 3.28 MetaSearch's own `OARecSearch` backend searches it and shows each record's type, title, box and links | a scratch script (not committed) run under `C:\OSGeo4W\bin\python-qgis.bat`, 2026-10-06 | this session |
| No OGC executable test suite for OGC API Records: none in the `opengeospatial` GitHub organisation (`ets-ogcapi-*` covers features, processes, edr, maps, coverages, tiles), none on Docker Hub `ogccite`; `ets-cat30` there is CSW 3.0, a different standard | GitHub search and `hub.docker.com/v2/repositories/ogccite`, 2026-10-06 | public |
| The requirements and their identifiers | OGC 20-004r1, *OGC API – Records – Part 1: Core* | https://docs.ogc.org/is/20-004r1/20-004r1.html |

## 5. Decision

As §2, Alternative A: OGC API Records at `/ogc/records/v1`, one searchable catalogue `catalog` whose records are the
portal's items for the caller, read from the portal's own listing, linked to every address the item answers at by the
rules those faces read, with the Table 12 parameters, claiming only the classes listed and tested. Written from the
published standard (20-004r1) and the clients' observable requests only.

## Conditions

1. **There is no OGC executable test suite for OGC API Records** (verified 2026-10-06, §4), so the conformance claim
   rests on the specification's requirements and the tests in this repository. When OGC publishes one, it is run and
   its findings are fixed or the classes it fails are withdrawn.
2. **A records client reads it in practice**: QGIS 3.28's MetaSearch is pointed at
   `…/ogc/records/v1/collections/catalog` in the QGIS interface (not only its backend from Python), a record is opened
   and a WMS or WFS link added to a map. **PARTLY DISCHARGED 2026-10-06:** OWSLib 0.25 and MetaSearch's `OARecSearch`
   backend read it from Python; the QGIS dialog has not been driven.
3. The owner confirms the `INFERRED` choices: a record per portal item (§2 A, against B), the portal's item types as
   `type` (§3), `datetime` against the record's change time (§3), and `rights` from the OGC access constraints.

## 6. Consequences

**Positive.** An OGC client can find what the server publishes without being told each address, and each record says
how to bind to it in ArcGIS and in OGC terms. The portal search and the catalogue are one listing, so they cannot drift;
three faces' visibility rules now live in one place.

**Negative.** Another reader of the portal's item objects by reflection (`PortalQuery.Field`), so renaming a field on
the anonymous item breaks the record silently to the compiler — the unit test `A_record_says_what_its_portal_item_says`
is what notices. Records are not sortable or CQL2-filterable. `bbox` costs a describe per layer on a cold cache. The
face is outside the response compression allowlist (paging links echo the request's parameters, a `token` among them).

**Ports created.** None; no dependency is adopted.

**State.** None. Nothing is stored.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | A catalogue listed in memory per request is fast enough at 100–1,000 services | Unmeasured; the portal's search already does the same listing |
| — | QGIS MetaSearch and OWSLib read `properties.extent` and `properties.type` as 3.28 does | Checked against 3.28 / OWSLib 0.25 only |

## 8. Dependencies

**Depends on:** ADR-040 (the portal surface), ADR-042 (OGC API Features, the versioned path and its parsers), ADR-079
(saved web maps as items), ADR-097 (OGC API Tiles), ADR-162 (OGC at a service's address), ADR-163, ADR-166 (faces turned
off one by one), ADR-167 (OGC access constraints), ADR-170 (WCS).

**Depended on by:** —

## 9. Revisit triggers

- OGC publishes an executable test suite for OGC API Records.
- A new item kind is added to the portal listing — it becomes a record and needs its links here.
- A records search with `bbox` exceeds 2 s at the stated scale on a cold cache.
- A catalogue client is found that needs a community `type` vocabulary or sorting.

## 10. Dissent

None recorded. Alternative B (one record per service) is the strongest case against and is the first thing condition 3
asks the owner.
