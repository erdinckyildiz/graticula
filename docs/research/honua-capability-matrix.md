# Honua Server — Capability Matrix

**Compiled 2026-08-13** from the project `README.md` on `trunk` and the
dependency manifest. **Tier A and Tier B only** — published documentation and
build metadata. No source was read. See
[honua-server.md](honua-server.md) §1b for the tiering.

Everything below is a claim made by the project about itself. None of it has
been verified against a running instance, and the compliance figures are theirs.

---

## 1. The headline

**Their published surface is roughly five times ours, and the features that
matter most to a migrating ArcGIS shop are behind a paywall.**

Honua is **open core** under ELv2, with three tiers — Community, Pro,
Enterprise. The gating is not incidental to our positioning; it lands precisely
on it.

| Capability | Their tier | Ours |
|---|---|---|
| **FeatureServer `applyEdits`** | **Pro** | **Community-equivalent — free** (Q-17) |
| **Import ArcGIS REST services** | **Enterprise** | **free** (Q-16) |
| **Import GeoServer services** | **Enterprise** | **free** (Q-16) |
| OIDC SSO | **Pro** | free (§41) |
| SAML 2.0 / SCIM 2.0 | **Enterprise** | undecided |
| Output cache / Redis cache | **Pro** | free, and Redis optional permanently (ADR-010) |
| Open-protocol edits (OGC API Features, WFS-T, OData) | Community | free |

**This confirms Q-16 and Q-17 from their own documentation.** Our record said
migration tooling is *"free deliberately, against a competitor who charges for
the equivalent."* That competitor charges Enterprise for it. And their ArcGIS
edit path — the single thing that keeps an existing Esri client working through
a migration — is Pro.

The strategic reading is uncomfortable but useful: **we are not competing with
Honua's feature list. We are competing with their price list**, on the two
features an ArcGIS exit path is actually made of.

Note also: **no versioned release exists yet.** Nightly builds only, v1.0 not
cut, 4 stars. Ahead of us by a great distance in surface area; not ahead in
maturity in the way the surface suggests.

---

## 2. Protocols and APIs

| | Honua | Us | Reference |
|---|---|---|---|
| **OGC API Features** (CRUD, CQL2) | yes | **yes — native, Parts 1+2+3** | ADR-005 |
| ArcGIS **FeatureServer** | yes (edits Pro) | **yes, including edits, free** | Q-17 |
| **WFS** 1.0/1.1/2.0 + transactions | yes | **yes**, compatibility layer | Q-17, §51 |
| **WMTS** | yes | **yes**, for vector tiles | Q-17 |
| **Vector tiles / MVT + TileJSON** | yes | **yes**, hosted sources only | Q-67 |
| **WMS** 1.1.1 / 1.3 | yes | **no** — rejected on merits, not scope | Q-47, ADR-004 §0 |
| ArcGIS **MapServer** (export/identify/legend) | yes | **no in v1** — but this is the shape we eventually want | ADR-004 §0 |
| ArcGIS **ImageServer** | yes | **no** | Q-17 |
| ArcGIS **GeometryServer** | yes | **no** | Q-17 |
| ArcGIS **GPServer** | yes | **no** | Q-17 |
| ArcGIS **GeocodeServer** | yes | **no** — not considered | — |
| ArcGIS **VectorTileServer** | yes | implied by WMTS scope | Q-17 |
| ArcGIS NAServer, VersionManagementServer | Pro / Enterprise | **no** | — |
| **WCS** 2.0.1 | yes | **no** — vector-first, raster is catalogued COG | ADR-009 |
| **WPS** 2.0 | yes | **no** | — |
| OGC API **Maps** | yes | no — needs rendering | ADR-004 |
| OGC API **Tiles** | yes | not decided (we serve MVT, not necessarily via this) | — |
| OGC API **Coverages** | yes | **no** | ADR-009 |
| OGC API **Processes** | yes | **undecided** — we have a job system but no protocol | ADR-011 |
| OGC API **Records** | yes | **undecided** — catalog exists, no protocol chosen | — |
| OGC API **Styles** | yes | **undecided** — we store and serve styles | Q-25 |
| OGC API **EDR** | yes | **no** — not considered | — |
| **SensorThings** v1.1 | yes | **no** — not considered | — |
| **OData** v4 with spatial | yes | **no** — not considered | — |
| **STAC API** | yes | **undecided** — STAC is in our raster thinking | ADR-009 |
| **3D Tiles**, Terrain-RGB, elevation | yes | **no** | — |
| **PMTiles** | yes | **undecided** | — |
| **gRPC** | yes | **no** — not considered | — |
| **MCP** for AI agents | yes | **no** — not considered | — |
| Health probes, OpenAPI, API explorer | yes | assumed, not designed | §46 not started |
| **Admin API** | yes | **yes** — and it is our *primary user's* surface | §39, Q-06a |
| **Capability manifest endpoint** | yes | **yes** — convergent design | ADR-008 §2 |

**Convergent, worth noting.** Their capability manifest and our
never-degrade-silently capability report are the same idea reached
independently. Likewise their *bounded database admission* and *adaptive
concurrency tuning* against our ADR-007 admission control and ADR-008 refusal
model. Two projects arriving at the same answer is weak evidence the answer is
right.

---

## 3. Data providers

| Provider | Honua | Us | Reference |
|---|---|---|---|
| **PostGIS** | full read/write | **full read/write** | ADR-008 |
| **SQL Server** | **read/query only** | **full read/write** | Q-50a |
| **Oracle** | **read/query only** | **full read/write** | Q-50a |
| MySQL / MariaDB | read-only | **no** | — |
| DuckDB | read-only, embedded | **undecided**, leaning no | Q-53 |
| Redshift / Snowflake / Databricks | read-only | **no — recommended against** | Q-53 |

**This is the one axis where we are ahead of them by design.** Their Oracle and
SQL Server are read-only; ours are fully editable through the API. That is
Q-50a, taken deliberately as the most expensive of three options, and it is
what makes an Oracle-backed ArcGIS estate migratable without moving data.

It is also why their architecture never faced our problem: with PostGIS
required and everything else read-only, their tile path can always use
`ST_AsMVT`. See [honua-server.md](honua-server.md) §1c.

---

## 4. Formats

| | Honua | Us |
|---|---|---|
| **Output** | JSON, GeoJSON, PBF, FlatGeobuf, GeoParquet, GeoArrow | GeoJSON, MVT. Others **undecided** |
| **Import** | GeoJSON, Shapefile, GeoPackage, GPX, KML, WKT, FlatGeobuf, **File Geodatabase (`.gdb.zip`)**, GeoParquet | **undecided** — Q-52 split serving providers from import sources and never listed them |

### This settles A-038, and not the way it was leaning

**A-038** asked whether GDAL is needed at all, after their dependency manifest
showed managed .NET readers for Shapefile, GeoPackage, GeoJSON, FlatGeobuf and
GeoParquet and **no GDAL binding anywhere**.

The import list adds **File Geodatabase**. There is no managed .NET FileGDB
reader; the practical routes are GDAL's `OpenFileGDB` driver or Esri's own SDK.
And for the ArcGIS exit path specifically, **`.gdb` import is close to
mandatory** — it is the format an Esri shop's data actually arrives in.

So the honest answer to A-038 is: **managed readers cover most formats, GDAL
covers the one that matters most to our chosen positioning.** A-016's placement
of GDAL in the job-worker image is right, and the dependency is not avoidable.

---

## 5. Operations and deployment

| | Honua | Us | Reference |
|---|---|---|---|
| Container-first, stateless | yes | yes, but **three images** and packaging undecided | Q-71 |
| **Kubernetes / Helm** | first-class | **explicitly not required** | §6 |
| **Redis** | **required for durable jobs** | **never required** — queue in the platform store, no broker | ADR-011 |
| Caching | Redis / output cache, **Pro** | L1 in-process, L2 optional **permanently** | ADR-010 |
| OpenTelemetry | yes | **not started** | §46 |
| Async job runtime, DAG workflows, cron | yes | job classes, reserved capacity, no DAG | ADR-011 |
| Auth | API key; OIDC **Pro**; SAML/SCIM **Enterprise** | local, JWT, OAuth2, OIDC — **not started** | §41 |
| mTLS | experimental, off | **TLS entirely absent from the architecture** | Q-55 |
| Conformance suites | **1,117 tests across 13 suites** | **none** | — |

**Two entries here should be uncomfortable.** They publish a conformance record
and we have no test strategy for protocol conformance at all. And they ship
experimental mTLS while TLS does not appear anywhere in our architecture (Q-55,
B3 in the [exit plan](../phase-0-exit-plan.md)).

---

## 6. What we deliberately will not build

Not gaps. Decisions, each with a reference.

| | Why |
|---|---|
| WMS | Rejected on merits, not scope. A rendered map service in the ArcGIS MapService style is the preferred eventual shape; WMS then becomes a thin adapter over it (ADR-004 §0) |
| Server-side raster rendering in v1 | Vector-first; raster is catalogued as COG and the client renders (ADR-009) |
| ImageServer, GeometryServer, GPServer | Produce rendered images or general geoprocessing surfaces we chose not to expose (Q-17) |
| Warehouse providers | Recommended against (Q-53) — they are analytical stores, not serving stores |
| Third-party plugin system | Internal extension points only, with a stated reopening trigger (ADR-006) |
| Required Kubernetes or Redis | Both on the §6 challenge list. The baseline is one machine |
| Tiles from registered databases | Q-67 |

---

## 7. Genuinely open — the gap list

Things Honua does that we have neither chosen nor rejected. This is the useful
output of the exercise.

| Gap | Note |
|---|---|
| **Attachments and related records** | Already opened as **Q-58** by Q-17. Their support confirms it is table stakes for FeatureServer compatibility, not a nicety |
| **Import format list** | Q-52 separated import sources from serving providers and never enumerated them. Their list is a reasonable starting set |
| **Output formats** — FlatGeobuf, GeoParquet, GeoArrow | Cheap to add, and GeoParquet was an early owner interest |
| **OGC API Processes** | We have a job system with no public protocol. This is the standard one |
| **OGC API Records** | We have a catalog with no public protocol |
| **OGC API Styles** | We store and serve styles already (Q-25); this is the standard surface for it |
| **STAC API** | Sits directly on ADR-009's COG catalogue |
| **Protocol conformance testing** | We have no strategy. OGC provides the suites; this should be a CI decision, not a later discovery |
| **Geocoding** | Never considered. Probably out, but should be recorded as out rather than absent |
| **gRPC, OData, SensorThings, EDR, 3D Tiles, PMTiles, MCP** | Never considered. Most are probably correct to decline, but §82 requires the question be asked and answered rather than left silent |

---

## 8. Honest summary

**Where we are ahead:** writable Oracle and SQL Server; free ArcGIS edits and
free migration tooling against their Pro and Enterprise tiers; a service runtime
with warmth-aware affinity routing that nobody else has; a deliberately small
deployment with no required broker or orchestrator.

**Where we are behind:** everything else, by a wide margin. Protocol surface,
format coverage, conformance evidence, auth, observability, packaging, and the
existence of a running product.

**Where the comparison misleads:** surface area is the easiest thing to add and
the least interesting thing to have. Their breadth is real but their depth on
the two features an ArcGIS migration needs is gated behind a licence. Ours is
not gated because there is no licence to gate it with — which is a positioning
advantage created entirely by Q-49's answer, not by engineering.
