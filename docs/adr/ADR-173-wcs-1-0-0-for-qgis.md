# ADR-173 — WCS 1.0.0 is spoken too, so that QGIS opens an image service

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` — QGIS 3.28's own WCS provider opens a coverage through it, its documents validate against OGC's 1.0.0 schemas, and its GetCoverage returns the bytes 2.0.1 returns; OGC's 1.0.0 suite exists but cannot be run headless, so no OGC suite measures it |
| **Decided** | 2026-10-06 by owner decision — [Q-162](../open-questions.md), answered *"Evet, 1.0.0 ekle"* |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-170](ADR-170-image-services-are-wcs-coverages.md) — WCS was 2.0.1 only |

---

## 1. Context

[ADR-170](ADR-170-image-services-are-wcs-coverages.md) made every image service a WCS 2.0.1 coverage. Its second
condition, measured on 2026-10-04, found that QGIS 3.28's *Add WCS layer* does not open one: its WCS provider asks
`DescribeCoverage` in 1.0.0 or 1.1 whatever version it is given, and answers *Cannot describe coverage*. The same
coverage opened through GDAL's driver, which few QGIS users reach. ArcGIS Server speaks 1.0.0, 1.1.x and 2.0.1.
[Q-162](../open-questions.md) asked the owner whether to add 1.0.0; on 2026-10-06 they answered yes.

## 2. Alternatives considered

### Alternative A — 1.0.0 as a second vocabulary over the same GetCoverage (chosen)

**For.** 1.0.0's `COVERAGE`, `CRS`, `BBOX`, `WIDTH`/`HEIGHT` or `RESX`/`RESY`, `RESPONSE_CRS`, `INTERPOLATION` and
`FORMAT` are read into the request 2.0.1 already answers (`RawAsync`), so the two versions cannot return different
pixels. Its capabilities and DescribeCoverage are written from the same coverage catalogue.

**Against.** A third, older document shape (`WCS_Capabilities`, `CoverageOffering`, `ServiceExceptionReport` 1.2.0)
to keep valid.

### Alternative B — 1.1 as well

**Against.** QGIS tries 1.0.0 first and opens on it; nothing has asked for 1.1, and 1.1's GridCRS model is a third
grid vocabulary. Not done.

## 3. Counterarguments to the preferred option

*No OGC suite will ever check it.* `ets-wcs10` exists, but it is a CTL suite with no REST controller (read in its
source, 2026-10-04), so TEAM Engine runs it only from its web form. A face no suite checks drifts. The answer here is the
conformance test that compares 1.0.0's GetCoverage bytes with 2.0.1's, which catches the drift that matters — the
pixels — and schema validation of the three documents.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| QGIS opens it | QGIS 3.28.0's `wcs` provider, given the server's `/wcs` and a coverage id, with and without `version=1.0.0`: valid, 256 × 192, 3 bands, EPSG:4326, extent 30.00,39.08 : 32.56,41.00 — the same size, bands and extent GDAL's 2.0.1 driver reads | QGIS's Python API against a local server, by hand, 2026-10-06 |
| The same pixels | 1.0.0 GetCoverage's body equals 2.0.1's for the same box and size | `WcsConformanceTests` (the 1.0.0 block) |
| The documents are 1.0.0 | Capabilities, DescribeCoverage and the exception report validate against `schemas.opengis.net/wcs/1.0.0` | `lxml` against OGC's published schemas, by hand, 2026-10-06 |
| No headless suite | `ets-wcs10` has no REST controller | read in the suite's source, 2026-10-04; [D-290](../architecture-debt.md) |

**Measured and not explained:** QGIS's band statistics through its WCS provider (min 0, mean 126.1 on band 1) differ
slightly from those through GDAL's 2.0.1 driver (min 1, mean 126.6). The server returns the same bytes to both
versions (row 2), so the difference is in how each client reads or samples them; it was not chased.

## 5. Decision

WCS 1.0.0 is answered at every WCS address — `/wcs` and each image service's `ImageServer/WCSServer` — when a request
says `VERSION=1.0.0`, or when a GetCapabilities asks for a version below 2.0. GetCapabilities, DescribeCoverage and
GetCoverage are served; GetCoverage requires `COVERAGE`, `CRS`, `BBOX` and `FORMAT` and one of `WIDTH`/`HEIGHT` or
`RESX`/`RESY`, offers GeoTIFF, nearest-neighbour interpolation, and the native reference, EPSG:4326 and EPSG:3857.
Refusals are a `ServiceExceptionReport` 1.2.0, `application/vnd.ogc.se_xml`, status 400. 1.1 is not spoken.

## 6. Consequences

**Positive.** QGIS's *Add WCS layer* works against this server without the user typing a GDAL string.

**Negative.** A third document vocabulary for WCS, checked by this repository's own tests and schemas rather than an
OGC suite.

**State.** None. Nothing is stored; the documents are written from the coverage catalogue on each request.

**Ports created.** None.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | QGIS keeps asking 1.0.0 first | Measured on 3.28 only |

## 8. Dependencies

**Depends on:** [ADR-170](ADR-170-image-services-are-wcs-coverages.md).

**Depended on by:** —

## Conditions

1. OGC's `ets-wcs10` is run against a public coverage — through TEAM Engine's web form, by hand, if no headless route
   appears — and its failures are fixed or recorded.
   **DISCHARGED 2026-10-07, headless after all.** The image carries the suite and TEAM Engine's own classes, and the
   console (`com.occamlab.te.Test -test=wcs1-0-0:main`, the form's fields as the main test's parameters) runs it with
   no web form; `tools/cite-run-wcs10.sh` does that in `cite.yml`. First run: **38 passed, 9 failed**, all but one this
   server's and fixed — `requestResponseCRSs` written as one space-separated element (the suite then sent
   `RESPONSE_CRS=EPSG:4326 EPSG:3857`), a 1.0.0 request with no or an unknown VERSION answered in 2.0's report, and
   the `Band` axis ignored rather than honoured and its unknown values refused. After: **46 passed, 1 failed**. The one
   is the suite's arithmetic: `bbox-inside` moves each side of the coverage's envelope 1 unit inwards, which inverts
   a box less than 2 units high — this coverage is 1.92 degrees — and expects content for it. It is the baseline.

## 9. Revisit triggers

- `ets-wcs10` gains a REST controller: add it to `cite.yml`.
- A client asks for 1.1.

## 10. Dissent

None recorded.
