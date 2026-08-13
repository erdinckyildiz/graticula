# Protocol Surface — Engines and Faces

**Written 2026-08-13**, after the owner put full protocol parity with Honua in
scope ([capability matrix](research/honua-capability-matrix.md)).

The purpose of this document is to stop counting protocols. **A protocol count
is a marketing number. The engine count is the engineering number**, and the two
differ by a factor of three.

---

## 1. The reframing

Full parity adds sixteen protocols to the scope. It does **not** add sixteen
subsystems. Most of them are another face over an engine we had already decided
to build.

| Engine | Status | Faces |
|---|---|---|
| **Feature / query** — ADR-008 | decided | OGC API Features · WFS 1.0/1.1/2.0 + Transactional · ArcGIS FeatureServer · **OData v4** · **gRPC** · **MCP** |
| **Tile** — Q-67 | decided, measured ×3 | MVT + TileJSON · WMTS · ArcGIS VectorTileServer · **OGC API Tiles** · **PMTiles** |
| **Job + Python** — ADR-011, Q-17b | reopened | ArcGIS GPServer · **WPS 2.0** · **OGC API Processes** |
| **Raster** — ADR-009, Q-17c | reopened | ArcGIS ImageServer · **WCS 2.0.1** · **OGC API Coverages** |
| **Render** — ADR-004 | deferred | ArcGIS MapServer · WMS 1.1.1/1.3 · **OGC API Maps** |
| **Catalog** | exists, no protocol | **OGC API Records** · **STAC API** |
| **Geometry** — ADR-003 | decided | ArcGIS GeometryServer |
| **Style store** — Q-25 | decided | **OGC API Styles** |
| **Observation store** | **does not exist** | **SensorThings v1.1** · **OGC API EDR** |
| **3D / terrain** | **does not exist** | **3D Tiles** · **Terrain-RGB** · **elevation API** |

**Twenty-nine protocol faces over ten engines.** Eight of those engines are
already decided or in flight. **Two do not exist in any form** and are new
products rather than new endpoints — §4.

Bold entries are new scope from this decision.

---

## 2. What the parity directive actually costs, by tier

### Tier A — near-free. A standard surface over something already built.

| Protocol | Sits on | Note |
|---|---|---|
| **OGC API Tiles** | tile engine | The same bytes we already serve, behind the standard URL template and tileset metadata. Three benchmark rounds already banked |
| **OGC API Styles** | style store | Q-25 already stores and serves MapLibre styles. This is the standard face over that store |
| **PMTiles** | tile engine | A single-file archive of tiles we already generate, served by range request. Packaging, not capability |
| **OGC API Records** | catalog | The catalog exists; this is the standard surface over it. Needs a metadata mapping, not a subsystem |
| **MCP** | feature / query | A tool-description layer over operations we already expose. Small, and strategically the cheapest way to be reachable by agents |

### Tier B — real work, but on foundations already chosen.

| Protocol | Sits on | Note |
|---|---|---|
| **OData v4** with spatial | feature / query | Another dialect front-end onto ADR-008's Query AST. **This is the hardest available test of whether that AST is genuinely protocol-neutral** — see §3 |
| **gRPC** | feature / query | A transport, not a capability. Mechanical: `.proto` definitions and a parallel surface over the same operations |
| **STAC API** | catalog + raster | Sits directly on ADR-009's COG catalogue. Spatio-temporal item search over data we already register |
| **OGC API Processes** | job + Python | Gated on Q-17b's SDK. Once GPServer exists, this is the OGC face of the same engine |
| **WPS 2.0** | job + Python | Same engine again. Older and clunkier than Processes; included for parity |

### Tier C — gated on an engine that is reopened or deferred.

| Protocol | Blocked on | Note |
|---|---|---|
| **WCS 2.0.1** | raster engine (Q-17c, Q-77) | Coverage serving. Same engine as ImageServer's expensive half |
| **OGC API Coverages** | raster engine | The modern face of the same thing |
| **OGC API Maps** | render engine (ADR-004) | Needs the vector renderer the owner wants and deferred. Ships with MapServer and WMS or not at all |

### Tier D — no foundation exists. These are new products.

| Protocol | Why it is different |
|---|---|
| **SensorThings v1.1** | A **different domain model**, not another face. Things, Datastreams, Observations, FeaturesOfInterest, Sensors, ObservedProperties — an IoT observation store with its own storage shape, its own temporal indexing and its own write path. Nothing in the architecture touches it |
| **OGC API EDR** | Query by position, corridor, trajectory, cube — over coverages *and* observations. Partly rides the raster engine, partly needs the observation store above |
| **3D Tiles / Terrain-RGB / elevation** | 3D tiling, terrain mesh generation, quantised-mesh or Terrain-RGB encoding. **No foundation at all** — not the vector tile pipeline, not the raster engine |

---

## 3. The test this creates, and it is a good one

[ADR-005](adr/ADR-005-api-architecture.md) decided a **protocol-neutral internal
interface**, with protocols as adapters over it. That was asserted, not proven —
an abstraction exercised by one implementation is not an abstraction.

**Six faces over the feature engine is a real test of it.** OGC API Features,
WFS-T, ArcGIS FeatureServer, OData, gRPC and MCP have different query languages,
different identity models, different transaction semantics and different error
conventions. If ADR-005's interface is genuinely neutral, faces five and six
cost little. If it leaks, this directive will find out — and finding out is
worth more than the protocols.

`A-026` already asked whether OGC API Features plus extensions covers §28. **This
is the same question at three times the pressure**, and it should be recorded as
strengthening that assumption's importance rather than as a separate concern.

The same test applies to the tile engine at five faces, and to the job engine at
three.

---

## 4. The honest split in this decision

**Fourteen of the sixteen are faces.** They are real work — adapters, conformance
tests, documentation, error mapping — but they do not change the architecture.
They pressure-test it, which §3 argues is a benefit.

**Two are new products:**

- **The observation store** (SensorThings, and half of EDR) is a different domain
  model with its own storage, indexing and write path. It is a defensible thing
  to build and it is not a GIS server feature; it is an IoT platform feature that
  Honua chose to include.
- **3D and terrain** has no foundation in anything decided so far.

These two should be **scheduled as their own decisions**, not absorbed into a
parity sweep — the same discipline Q-17a's failure taught, where four services
travelled under one justification and it was false for two of them.

Recorded for confirmation rather than assumed: it is possible both were swept in
by pointing at a list rather than chosen individually. If they were chosen,
nothing changes; if not, this document is where they get separated.

---

## 5. Consequences for sequencing

The engine view reorders the work in a way the protocol view hides.

1. **Feature engine first**, and build its faces incrementally — the walking
   skeleton already targets OGC API Features, and each additional face is a
   test of ADR-005 rather than a new subsystem.
2. **Tile engine** is already measured; its remaining faces are packaging.
3. **Job + Python engine** unlocks three protocol faces at once. That makes the
   Python SDK (Q-74–Q-76) higher leverage than its single-question appearance
   suggests.
4. **Raster engine** unlocks three more (ImageServer, WCS, Coverages) and is
   gated on Q-77's Tier 1 line.
5. **Render engine** unlocks three more (MapServer, WMS, OGC API Maps) and is
   the owner's stated eventual want.
6. **Observation store and 3D** are last, because they share nothing with the
   rest.

**Four engine decisions — Python SDK, raster, render, and the two new stores —
account for twelve of the sixteen protocols.** Sequencing by engine turns a
sixteen-item list into four decisions and a stream of adapters.

---

## 6. What this does not change

- **§82 still applies to each face.** *What concrete problem does this solve?*
  Parity is a legitimate answer where a real client speaks the protocol. It is
  not an answer for a protocol nothing in the target market uses, and those
  should be identified rather than built by default.
- **Conformance is not optional now.** Honua publishes 1,117 passing tests across
  13 suites. Claiming twenty-nine protocol faces without conformance evidence is
  a weaker position than claiming six with it. This makes protocol conformance
  testing a CI decision that has to be taken early, and it is currently absent
  from the architecture entirely.
