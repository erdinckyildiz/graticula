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
| **Database** | **PostGIS only.** Both modes: hosted in our managed datastore (mandatory, Q-69) and **registered** in the customer's existing PostGIS, read/write where rights allow. **2026-08-14: hosted means the datastore holds it**, and there are two ways in — `POST /admin/hosted/import` turns a GeoJSON file into a feature class, `POST /admin/hosted/define` turns a designed schema into an empty one filled through `applyEdits`. Hosted services live under **`/rest/services/hosted`** and registered ones at the root; each redirects the other, so the split is a fact rather than a convention. Shapefile is [Q-98](open-questions.md) |
| **ArcGIS FeatureServer** | Query and `applyEdits`, attachments, related records (ADR-013). **The primary API surface** — see §4 |
| **ArcGIS VectorTileServer** | Vector tiles from hosted data (Q-67). **Built 2026-08-14** — service document, style, tile endpoint, hosted-only rule enforced, verified by rendering a served tile against its source. Encoding is `ST_AsMVT` ([ADR-021](adr/ADR-021-tile-encoding.md)) after four benchmark rounds |
| **ArcGIS GeometryServer** | Owner: crucial. A thin surface over PROJ and NetTopologySuite — with the caps A11 demands, see §6. **2026-08-14: the linear half is built** ([ADR-022](adr/ADR-022-geometry-server.md)) — `project`, `areasAndLengths`, `lengths`, `labelPoints`, with projection through the datastore's PROJ. **The overlay half is blocked on [Q-97](open-questions.md).** A-042 said caps on vertex count would make it safe and [measurement invalidated that](../benchmarks/geometry-overlay/RESULTS.md) — 6,408 adversarial vertices cost 153 s and 16.7 GB against 312 ms for a real 72,919-vertex outline. The linear-cost operations are unaffected |
| **Symbology** | **Added 2026-08-17 by owner decision** — a service published from a table had no appearance at all, and the server had an opinion about none of its three faces. One canonical MapLibre document per layer; the FeatureServer's `drawingInfo` and the tile style both derive from it; an unstyled layer gets a deterministic generated default that is reported as generated. Authoring is in v1, **SLD is not** ([ADR-033](adr/ADR-033-symbology.md)). Not server-side rendering — ADR-004 stays `DEFERRED` |
| **Folders** | **Added 2026-08-17 by owner decision.** A folder was a text column that existed only while something was in it; it is now a register (migration 18), so an empty one can be created and the directory lists what exists rather than two names typed into the host. **Hosted data always lands in `hosted`; a registered table may be published into a named folder** — owner rule: *"turkiye klasoru sadece reference registered olanlar için"* — and the URL follows: `/rest/services/turkiye/tr_il/FeatureServer`. `hosted`, `Utilities` and `System` are reserved |
| **Admin API** | ADR-017's shape. The GIS administrator is the primary user (Q-06a) |
| **TLS, authentication, packaging** | ADR-014, ADR-015, ADR-016 |
| **Migration tooling** | Inventory scan and definition import, free (Q-16) — the reason anyone switches |

## 3. Out of v1 — deferred, not cancelled

Grouped by what their removal buys, because that is the point.

### 3a. Every database except PostGIS

**Oracle, SQL Server, MySQL, MariaDB, DuckDB.**

> **AMENDED 2026-08-18 — this is a deferral, and this section was written as a
> removal.** Owner decision, in their words: *"Şimdilik postgis ile gideceğiz. Sonra
> diğer db'ler eklenecek. V1'de sadece Postgis olarak kalabiliriz."* — PostGIS for now,
> the other databases added afterwards, and v1 may stay PostGIS only.
>
> **What changes: nothing about v1, and one thing about everything else.** The scope
> below stands exactly as written. What was wrong was the *tense* the consequences were
> written in. This section says *"A-043 dies"*, *"Q-20 dies"*, *"ADR-008's per-dialect
> pushdown table dissolves"* — and a reader repairing the ADRs against it would delete
> the multi-engine reasoning as obsolete. It is not obsolete; it is **dormant**, and it
> is what the second engine will be built from. Every assumption, question and design
> in that list sleeps until the engine that needs it arrives, and it wakes with it.
>
> **One bullet below is now wrong rather than merely mis-tensed**, and it is the one
> that matters most. *"ADR-008 condition 1a and review finding P10 dissolve — the
> second-dialect compiler was a forcing function against PostGIS-shaped assumptions.
> With one dialect by decision rather than by drift, there is nothing to force."* That
> argument holds only if there is never a second dialect. There will be one. So the
> forcing function was not made unnecessary, it was **switched off while the thing it
> guards against carries on happening** — and it has already happened once, visibly:
> [ADR-008](adr/ADR-008-query-engine.md) §4a-i records `FeatureQuery.Where` carrying
> ready-made SQL text into the domain model, which §4.1 forbids precisely so that a
> non-database provider stays possible. §4a-i's own reasoning for recording it rather
> than repairing it was that *"the only consumer is hypothetical"*. After this decision
> the consumer is **scheduled**, which is a different word. It is still not urgent —
> §82's question is what concrete problem an abstraction solves *today* — but the
> revisit trigger is now a date on somebody's plan rather than a hypothesis, and the
> cost of every further PostGIS-shaped assumption is paid later by whoever adds Oracle.
>
> **This is the missing decision [D-27](architecture-debt.md) was waiting for.** That
> debt says twelve of twenty-two ADRs still describe a three-database product and that
> it is *"not repairable by a sweep: some of those paragraphs are deferred rather than
> wrong, and deciding which is which per paragraph is the owner's."* The rule is now
> decided and it is a default: **deferred unless it claims to be current.** A paragraph
> that designs for several engines stays and says when it applies; a paragraph that
> tells the reader v1 serves Oracle today is corrected.

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

- **Q-85 dissolves.** ~~ADR-004 stays `DEFERRED`~~ and no longer contradicts an
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
- ~~The job-worker image loses the Python runtime, which shrinks the air-gapped
  bundle and Q-76's maintenance burden.~~

**Amended 2026-08-18 by owner decision, and only this last bullet moves.** Asked
whether File Geodatabase import was worth the cost, the owner said *"gdb import
önemli. o yüzden yığın büyüyecekse büyüyebilir."* So **the Python runtime returns
to the job-worker image — for our own code**, because
[ADR-037](adr/ADR-037-job-workers-come-in-two-kinds.md) puts `pyogrio` there and
[Q-108](open-questions.md) established there is nothing GDAL-free to adopt for
.NET and that writing our own reader is the wrong project.

**Everything else in this section stays cut, and the line is one sentence wide.**
GPServer, the Python SDK, the sandbox and the *user* wheel set remain out;
[Q-75](open-questions.md) — how user-supplied Python is sandboxed, *"the largest
security surface in the product by a wide margin"* — is not reopened, and neither
is Q-76. **Our script against our pinned wheels is a packaging cost; their tool
against our server is arbitrary code execution.** ADR-037 condition 3 makes that
a build-time check rather than a sentence, because a sentence is not a guard.

**The cost this concedes, stated rather than absorbed:** the air-gapped bundle
grows again, the wheel set becomes ours earlier than
[ADR-016](adr/ADR-016-packaging-deployment-upgrade.md) §7 planned, and
[A-049](architecture-assumptions.md) — that a curated set can cover realistic
work without pip at runtime — is `UNVALIDATED` and now load-bearing sooner. That
is the bullet above being given up on purpose, not overlooked.

### 3d. The rest of the protocol surface

**OGC API Features, Tiles, Styles, Records, Processes, EDR · WFS · WMTS · WPS ·
SensorThings · OData · gRPC · MCP · STAC · PMTiles · 3D Tiles · Terrain-RGB ·
geocoding.**

> **Amended 2026-08-19, and v1 does not change.** Owner decision: **WFS is the first surface built after v1** ([ADR-039](adr/ADR-039-wfs-is-the-first-surface-after-v1.md)), ahead of OGC API Features, which [Q-94](open-questions.md) had recommended for that place. **It is not moved into v1 and this section is not amended to include it.** The list above stands exactly as written; what is now known is the order things leave it in, and the first one is leaving while v1's own carried debts are open. That ordering is the owner's and is recorded in ADR-039 §1 rather than by editing the cut — because this document is the only one in the repository that ever subtracted anything, and *working outside v1* must not become *widening v1* by the same edit.

- **Q-79 is answered by omission**: SensorThings and 3D Tiles were swept in by
  pointing at a list, and they are out. The observation store and the 3D/terrain
  pipeline — the two engines `protocol-surface.md` said *"do not exist in any
  form"* — are not built.
- **Q-84 defers** with the geocoder, including the reference-data question.
- **Q-86's §82 debt shrinks to the handful of things actually in v1.**

### 3e. Formats

Import narrows to what a PostGIS-backed product needs on day one. **File
Geodatabase stays in scope for migration** — it is the format an Esri estate's
data arrives in, ~~and A-038 established there is no managed .NET reader, so GDAL
stays in the job worker.~~

**Amended 2026-08-16.** The conclusion stands and its stated reason does not:
[A-038](architecture-assumptions.md) is `INVALIDATED` — a managed .NET File
Geodatabase reader demonstrably can exist, because a peer has one and declares no
GDAL dependency anywhere in its solution. So GDAL stays in the job worker **by
decision rather than by necessity**: writing our own reader is possible and is not
v1 work ([Q-108](open-questions.md)). The practical position is unchanged; what
changed is that it is now a cost trade with a recorded recommendation instead of a
constraint nobody could question.

**Surveyed 2026-08-18**, and the library question has an answer: GDAL's
`OpenFileGDB` driver, which carries **no proprietary dependency**, reads ArcGIS
9.x, and writes 10+ since GDAL 3.6 — with relationships, domains, curves and
raster layers. No public managed .NET reader was found; the two .NET routes that
exist depend on Esri's closed SDK or are commercial. Details and the two format
limits — **SDC and CDF compressed geodatabases cannot be read at all** — are in
[Q-108](open-questions.md).

~~**What is still missing is one sentence in the product**, not a decision: a
`.gdb.zip` uploaded today is sniffed as a ZIP, enters the shapefile path, and is
refused with *"no shapefile in this archive"*. The refusal is correct and the
sentence is wrong — nothing says *this format is not imported yet*.~~

**Built 2026-08-19.** A `.gdb.zip` is now recognised, kept, read by a separate
process, and published: one archive becomes **one service holding N layers**, which
is [ADR-038](adr/ADR-038-how-a-geodatabase-becomes-a-service.md) and the owner's
rule — *"servis ve katman ayrı şeyler. bir serviste n katman olabilir."* All three
of the owner's archives round-tripped into PostGIS the same day; the numbers, and
the two layers that were refused for reasons in the data, are in
[file-geodatabase-readers.md](research/file-geodatabase-readers.md) §8g. What is
**not** built is anything past import: no writing a geodatabase, no SDC or CDF
(unreadable by any open route), no schema-only publish of an empty feature class
(D-106), and the 2.5D geometries these archives are full of are stored as 2D with
the loss counted rather than carried (D-107).

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
| **P4** | Dissolving Q-49's criterion removed the validation path for A-003 and five others — **and that half is still true and still owed.** ~~A-003 is the load-bearing assumption under ADR-007, and ADR-007 is in v1~~ — **corrected 2026-08-24: A-003 was downgraded to informational on 2026-08-15**, when [ADR-029](adr/ADR-029-affinity-routing-is-not-the-default.md) took affinity routing out of the default. It holds nothing up, so a missing validation route for it costs nothing today and costs everything the day affinity is reconsidered. **What the dissolution really left exposed is the other five**, which this row names and does not list |

## 7. Why this is the right shape

Not because it is smaller. Because it is **one database, one API family, one
tenant model**, and every remaining hard problem is a problem we can actually
measure on a machine we have. The three-round tile benchmark already ran against
this exact configuration.

The reviewers' verdict on the previous scope was that it was unachievable and
that no document said who would build it. That objection does not disappear here
— it becomes answerable.
