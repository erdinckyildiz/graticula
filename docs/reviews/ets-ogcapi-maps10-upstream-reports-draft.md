# Draft reports to `opengeospatial/ets-ogcapi-maps10` — ADR-175 condition 2

**Status: draft, not sent.** Filing an issue on somebody else's repository publishes text under this project's name,
so it waits for the owner. Written 2026-10-15 from the suite's source at the commit this repository pins
(`83d256077c8dc620c0945d84f44306cd6b7783a2`, `tools/cite-run-maps.sh`), which is still the suite's head. No open or
closed issue in that repository covered either point when this was written.

---

## Issue 1 — `conf/core` is recognised only in its https spelling, and the requirement class enum cannot match at all

**Where.** `src/main/java/org/opengis/cite/ogcapimaps10/conformance/core/MapConformanceSuccess.java`

```java
private static final String REQUIRED_CORE_URI = "https://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/core";
private static final String LEGACY_REQUIRED_CORE_URI = "https://www.opengis.net/spec/ogcapi-maps-1/1.0/req/core";
...
if (REQUIRED_CORE_URI.equals(conformanceClass) || LEGACY_REQUIRED_CORE_URI.equals(conformanceClass)) {
```

and the fallback `RequirementClass.byConformanceClass`, whose entries are written
`http://www.opengis.net/spec/ogcapi-Maps-1/1.0/conf/core` — with a capital `M` in `ogcapi-Maps-1`.

**What happens.** OGC API — Maps — Part 1 (OGC 20-058) gives its conformance classes as
`http://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/...`. A server declaring exactly that fails
`verifyMapConformanceSuccess` with *"The required conformance class 'conf/core' or legacy 'req/core' is not
declared."* The https comparison does not match it, and the enum's mixed-case path cannot match any lowercase
URI. A server passes only if it declares the https spelling, which the standard does not use.

**Suggested change.** Compare the scheme-less, lower-cased path (or accept both `http` and `https`), and correct the
enum's `ogcapi-Maps-1` to `ogcapi-maps-1`.

**Workaround we use meanwhile.** Declaring the https spelling beside the http one. That is a claim the standard
does not ask for, made only so the suite recognises core.

---

## Issue 2 — test classes run, and fail, against a server that does not declare their conformance class

**Where.**
- `conformance/cors/CorsSupport.java` — `verifyCorsSupport` (A.23.1, `/req/cors/cors`)
- `conformance/tiff/TIFFmapContent.java` — `verifyTiffContent` (A.19.1, `/req/tiff/content`)
- `conformance/mapTilesets/DescLinks.java` — `verifyTilesetsLink` (A.2.1, `/req/tilesets/desc-links`)

**What happens.** None of the three consults the server's `/conformance` before testing (compare the
`requireConformance(...)` guard in `conformance/background/*`), so each is run against every server. A server that
declares `core`, `png`, `background` and the subsetting classes, and not `cors`, `tiff` or tilesets, gets three
failures for requirements it never claimed. Those should be skips (or "not tested").

**Suggested change.** The same `requireConformance("/conf/cors")`, `("/conf/tiff")` and `("/conf/tilesets")`
guards the background tests already have, throwing `SkipException` when the class is not declared.

---

## Not reported, recorded for completeness

`BackgroundMapSuccess.verifyBackgroundMapSuccess` asks a fully transparent pixel to carry `bgcolor`'s RGB. A PNG
with premultiplied or straight alpha may legitimately write any RGB under alpha 0, so the assertion is arguably
stricter than the standard. It is left out of the reports above because whether OGC 20-058 intends it is a reading
of the standard rather than a defect in the suite, and that reading is the owner's to make.
