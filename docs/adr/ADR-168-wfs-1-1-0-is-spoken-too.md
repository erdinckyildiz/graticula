# ADR-168 — WFS 1.1.0 is spoken beside 2.0.0

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` — each operation is checked end to end in 1.1.0's own terms; no 1.1.0-only client has been watched using it, and OGC's 1.1.0 test suite has not been run |
| **Decided** | 2026-10-03 — inferred from the owner's *"Devam et"* in answer to *should WFS 1.0/1.1 be done, given it is larger than estimated and 2.0 already serves QGIS and ArcGIS Pro?* |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-039](ADR-039-wfs-is-the-first-surface-after-v1.md) §5 (2.0.0 only; another version refused) |

---

## 1. Context

ArcGIS Server's WFS answers 1.0.0, 1.1.0 and 2.0.0, and FME, MapInfo, older ArcGIS releases and many scripts still
send 1.1.0. This server spoke 2.0.0 only and refused the others by name (ADR-039 §5), which is right against an
approximate answer and still a refusal.

## 2. Alternatives considered

### Alternative A — 1.1.0 in its own terms, through the same handlers (chosen)

- **A dialect, not a second surface.** `WfsDialect` holds what differs — WFS 1.1, GML 3.1.1, OWS 1.0 and OGC Filter
  1.1 namespaces, the GML media type `text/xml; subtype=gml/3.1.1`, the schemas — and the writers take it.
- **Negotiation.** Every operation takes `version=1.1.0` or `2.0.0`; `GetCapabilities` takes the first of its
  `AcceptVersions` this server speaks, or its `version`, or 2.0.0. 1.0.0 is refused naming the two spoken. The 2.0.0
  document lists both in `AcceptVersions`.
- **GetCapabilities 1.1.0**: OWS 1.0 identification (with ADR-167's metadata at a service's address), `DefaultSRS`,
  GML 3.1.1 as the output format, OGC Filter 1.1's capabilities.
- **GetFeature 1.1.0**: `wfs:FeatureCollection` in 1.1's namespace with `numberOfFeatures`, a feature a
  `gml:featureMember`, geometry in GML 3.1.1 (the same elements as 3.2 in their own namespace). KVP `typeName` and
  `maxFeatures`; XML POST in 1.1's namespaces.
- **DescribeFeatureType 1.1.0**: the schema imports GML 3.1.1, its elements in `gml:_Feature`'s substitution group.
- **Filters**: OGC Filter 1.1 is written as FES 2.0 before the one reader reads it — `PropertyName` to
  `ValueReference`, `FeatureId` and `GmlObjectId` to `ResourceId`, `escape` to `escapeChar`, GML 3.1.1 to 3.2.
- **Refusals** in OWS 1.0 for a 1.1.0 request. `GetPropertyValue` and the stored-query operations are 2.0.0's and are
  refused in 1.1.0.

### Alternative B — 1.0.0 as well

**Against, for now:** GML 2 is a different geometry encoding (`gml:coordinates`, `outerBoundaryIs`), and Filter 1.0 and
the 1.0.0 capabilities are a third shape; the clients still sending 1.0.0 also speak 1.1.0.

### Alternative C — Keep refusing

**Against:** the refusal is what an ArcGIS shop's older tools meet first.

## 3. Counterarguments to the preferred option

- *Axis order in 1.1.0 is a known trap.* Positions follow the `urn:ogc:def:crs` reference this server names in
  `DefaultSRS` and `srsName`, which is latitude first for EPSG:4326, as 2.0.0's do; clients that read 1.1.0's EPSG:4326
  as longitude first will swap them.
- *GML 2 `gml:Box` in a 1.1.0 filter* is not read.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| 1.1.0 capabilities in 1.1's namespace by `version` and by `AcceptVersions`; a GetFeature with `featureMember` and `numberOfFeatures`; an OGC Filter 1.1 `FeatureId` selects one; a 1.1.0 refusal is OWS 1.0; 1.0.0 is refused | `WfsConformanceTests.Version_1_1_0_is_answered_in_its_own_terms_and_1_0_0_is_refused` | this repository |
| An OGC Filter 1.1 with `PropertyIsLike`, `BBOX` over a GML 3.1.1 envelope and a `FeatureId` reads as its FES 2.0 | `WfsRequestTests.An_ogc_filter_1_1_is_read_as_the_fes_2_0_it_corresponds_to` | this repository |
| Comparison, BBOX and POST requests in 1.1.0 answer on the fixture | measured 2026-10-03 | this session |

## 5. Decision

As §2, Alternative A.

## Conditions

1. The owner confirms 1.1.0 is wanted (`INFERRED`, Q-161). **DISCHARGED 2026-10-03** — the owner: *"evet onayladım"*.
2. A 1.1.0 client — FME or an older QGIS forced to 1.1.0 — reads a layer and its filter.

## 6. Consequences

**Positive.** Older WFS clients read this server without a version change.

**Negative.** Two dialects for the writers to keep alike.

**State.** None.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | Clients sending 1.1.0 honour the `urn` reference's axis order | Unvalidated (condition 2) |

## 8. Dependencies

**Depends on:** ADR-039, ADR-162, ADR-167.

**Depended on by:** —

## 9. Revisit triggers

- A client that needs 1.0.0 or GML 2.
