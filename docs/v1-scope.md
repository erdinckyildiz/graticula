# v1 Scope

**Decided by the project owner, 2026-08-13.** This document is the authoritative
statement of what v1 is. Where any other document disagrees, this wins until that
document is amended.

It exists because scope had been added in nine consecutive decisions and never
subtracted, and because
[independent review 3](reviews/independent-review-3-synthesis.md) found — from
three reviewers who could not see each other's work — that *"nothing in the
process converts an honest observation into a cut."* **This is the cut.**

---

## 1. v1 in one sentence

> **A PostGIS-backed GIS server that speaks ArcGIS: feature services, vector tile
> services and a geometry service, over data that is either hosted in our
> datastore or registered in the customer's own PostGIS.**

## 2. In

| | Detail |
|---|---|
| **Database** | **PostGIS only.** Both modes: hosted in our managed datastore (mandatory, Q-69) and **registered** in the customer's existing PostGIS, read/write where rights allow |
| **ArcGIS FeatureServer** | Query and `applyEdits`, attachments, related records (ADR-013). **The primary API surface** — see §4 |
| **ArcGIS VectorTileServer** | Vector tiles from hosted data (Q-67). **Built 2026-08-14** — service document, style, tile endpoint, hosted-only rule enforced, verified by rendering a served tile against its source. Encoding is `ST_AsMVT` ([ADR-021](adr/ADR-021-tile-encoding.md)) after four benchmark rounds |
| **ArcGIS GeometryServer** | Owner: crucial. A thin surface over PROJ and NetTopologySuite — with the caps A11 demands, see §6. **2026-08-14: the linear half is built** ([ADR-022](adr/ADR-022-geometry-server.md)) — `project`, `areasAndLengths`, `lengths`, `labelPoints`, with projection through the datastore's PROJ. **The overlay half is blocked on [Q-97](open-questions.md).** A-042 said caps on vertex count would make it safe and [measurement invalidated that](../benchmarks/geometry-overlay/RESULTS.md) — 6,408 adversarial vertices cost 153 s and 16.7 GB against 312 ms for a real 72,919-vertex outline. The linear-cost operations are unaffected |
| **Admin API** | ADR-017's shape. The GIS administrator is the primary user (Q-06a) |
| **TLS, authentication, packaging** | ADR-014, ADR-015, ADR-016 |
| **Migration tooling** | Inventory scan and definition import, free (Q-16) — the reason anyone switches |

## 3. Out of v1 — deferred, not cancelled

Grouped by what their removal buys, because that is the point.

### 3a. Every database except PostGIS

**Oracle, SQL Server, MySQL, MariaDB, DuckDB.**

This is the largest single simplification available to the project, and it
removes more unresolved risk than everything else on this list combined:

- **A-043 dies** — six geometry engines disagreeing at the edges on validity,
  precision, `touches` and empty geometries. With one engine there is nothing to
  disagree.
- **Q-20 dies** with it.
- **A-047 shrinks from five engines to one** — the RLS principal-to-database-role
  mapping that had different naming rules, length limits and case behaviour per
  vendor.
- **ADR-008's per-dialect pushdown table dissolves.** One dialect, and it is the
  one with `ST_AsMVT`, `ST_ClipByBox2D` and `ST_Simplify`.
- **A-027 shrinks** from three transaction models and three definitions of a
  conflict to one.
- **D-05 closes.** The debt was that the feature path is unmeasured on SQL Server
  and Oracle. There is no SQL Server or Oracle.
- **ADR-008 condition 1a and review finding P10 dissolve** — the second-dialect
  compiler was a forcing function against PostGIS-shaped assumptions. With one
  dialect by decision rather than by drift, there is nothing to force.
- **Review finding A9 dissolves** — ADR-003 and ADR-008 no longer schedule the
  same event on opposite sides of a gate.
- **Half the CI matrix**, and the Testcontainers work D-05 had queued.

**What it costs, stated plainly:** an ArcGIS shop running Oracle — which is many
of them — cannot use v1 without moving data. That is the migration story
narrowed to PostGIS estates, and it is the price of shipping.

### 3b. Rendering, and everything that needed it

**WMS, ArcGIS MapServer, ImageServer, OGC API Maps and Coverages, WCS.**

- **Q-85 dissolves.** ADR-004 stays `DEFERRED` and no longer contradicts an
  in-scope capability, which was review finding S2/A7.
- **ADR-009 can re-close.** ImageServer was what reopened it; the near-free
  operations (`identify`, histograms, footprint query) go with the rest and
  return when the raster engine does.
- **Q-77 defers** — the Tier 1 line between assembling pixels and colouring them.

### 3c. User-supplied code

**GPServer, the Python SDK, the sandbox, the curated wheel set.**

- **Q-17b, Q-74, Q-75, Q-76 all defer.**
- **ADR-006 can re-close** — the plugin model was reopened by exactly this.
- **Review finding O3 leaves v1** — arbitrary code execution against a server
  holding the organisation's spatial data, with no sandbox and a publisher role
  that Q-59 has not defined.
- The job-worker image loses the Python runtime, which shrinks the air-gapped
  bundle and Q-76's maintenance burden.

### 3d. The rest of the protocol surface

**OGC API Features, Tiles, Styles, Records, Processes, EDR · WFS · WMTS · WPS ·
SensorThings · OData · gRPC · MCP · STAC · PMTiles · 3D Tiles · Terrain-RGB ·
geocoding.**

- **Q-79 is answered by omission**: SensorThings and 3D Tiles were swept in by
  pointing at a list, and they are out. The observation store and the 3D/terrain
  pipeline — the two engines `protocol-surface.md` said *"do not exist in any
  form"* — are not built.
- **Q-84 defers** with the geocoder, including the reference-data question.
- **Q-86's §82 debt shrinks to the handful of things actually in v1.**

### 3e. Formats

Import narrows to what a PostGIS-backed product needs on day one. **File
Geodatabase stays in scope for migration** — it is the format an Esri estate's
data arrives in, and A-038 established there is no managed .NET reader, so GDAL
stays in the job worker.

---

## 4. The consequence that needs its own section: this inverts ADR-005

[ADR-005](adr/ADR-005-api-architecture.md) decided **OGC API Features 1+2+3 as
the native surface, with legacy protocols in a compatibility layer outside the
core domain.** v1 ships **ArcGIS only**, with OGC API Features in v2.

**That is an inversion, not an amendment.** ADR-005 is `REOPENED`.

Three things follow, and the third is the uncomfortable one.

**It resolves review finding A10 by choosing it.** A10 observed that the
compatibility layer had become more capable than the product it wraps — by
accretion, in four places, without a decision. It is now the product surface **by
decision**, which is a defensible position and a much better one than drift.
§51's *outside the core domain* boundary must be amended to match, or deleted for
v1.

**It is consistent with why the product exists.** Q-49's answer is *the ArcGIS
Server exit path*. If that is the thesis, then speaking ArcGIS natively is the
thesis executed, not a compromise of it. `VERIFY`: implementing a publicly
documented REST API is ordinarily permissible, and this is the same clean-room
position the compatibility layer already held — making it primary changes
emphasis, not legality. CLAUDE.md §5 still forbids reproducing proprietary
source or undocumented internals.

**And it makes ADR-005's protocol-neutral internal interface speculative.** The
interface was justified by carrying many faces; §3.2a said six faces would be
*"the hardest available test"* of whether it is genuinely neutral. **v1 has one
face.** An abstraction exercised by a single implementation is not an abstraction
— ADR-005's own words, about a different subject. So either:

- build the ArcGIS surface directly and extract the interface when OGC arrives in
  v2, accepting a refactor; or
- build the interface now on faith, which is §82's *what concrete problem does
  this solve?* answered with *a problem we will have later*.

**Recommendation: the first.** Recorded as **Q-89** rather than decided here,
because it is ADR-005's to answer when it re-closes. **A-026 stops being
load-bearing** either way, since v1 no longer asks whether OGC API Features can
express §28.

---

## 5. What survives that people might expect to have gone

- **Registered sources.** Data may live in the customer's own PostGIS. So A-017
  (foreign, possibly read-only sources), schema drift detection (A-023), quiesce
  discipline (ADR-007 §5b) and the connection budget (Q-04) are all still real
  problems — for one dialect. **Q-08's per-layer lifecycle test survives intact.**
- **Editing.** `applyEdits`, attachments and related records are in (ADR-013), so
  A-027's concurrency-against-unseen-writes remains load-bearing.
- **The datastore is still mandatory** (Q-69), so PostgreSQL is still a hard
  dependency (Q-70).
- **GDAL** stays in the job worker for File Geodatabase import (A-038, A-016).

## 6. What this scope does *not* fix

Cutting scope does not discharge the review's findings about v1 itself. Still
open and still owed:

| | |
|---|---|
| **O1, O2** | ADR-016's version handshake contradicts its own rollback, and backup has no design. Both apply to a PostGIS-only product exactly as written |
| **O4, O5, O6, O7** | Break-glass gated on attacker-inducible state; no data-plane rate limiting; revocation cached in pinned service contexts; the secret-encryption key missing from the state inventory. All in v1 |
| **A11, A-042** | GeometryServer is in, so the caps it names and never numbers are on the critical path |
| **A1, P7** | ADR-001's status honesty is unaffected by scope |
| **A2, A5, A6, P11, P14** | The propagation debt. Cutting scope does not un-stale a document |
| **P4** | Dissolving Q-49's criterion removed the validation path for A-003 and five others. A-003 is the load-bearing assumption under ADR-007, and ADR-007 is in v1 |

## 7. Why this is the right shape

Not because it is smaller. Because it is **one database, one API family, one
tenant model**, and every remaining hard problem is a problem we can actually
measure on a machine we have. The three-round tile benchmark already ran against
this exact configuration.

The reviewers' verdict on the previous scope was that it was unachievable and
that no document said who would build it. That objection does not disappear here
— it becomes answerable.
