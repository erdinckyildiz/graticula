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
| **Symbology** | **Added 2026-08-17 by owner decision** — a service published from a table had no appearance at all, and the server had an opinion about none of its three faces. ~~One canonical MapLibre document per layer~~ — **the canonical document is a CIM renderer from 2026-09-03 by owner decision** ([ADR-052](adr/ADR-052-the-canonical-symbology-document-is-cim.md)); the FeatureServer's `drawingInfo` **and** the tile style's MapLibre both derive from it, and both report what they could not carry; an unstyled layer gets a deterministic generated default that is reported as generated. Authoring is in v1, **SLD is not** ([ADR-033](adr/ADR-033-symbology.md)). ~~Not server-side rendering — ADR-004 stays `DEFERRED`~~ — **corrected 2026-09-09, and it had been wrong for three weeks in the document that decides scope.** [ADR-004](adr/ADR-004-rendering-engine.md) has been `ACCEPTED` since 2026-08-20, un-deferred by [ADR-041](adr/ADR-041-the-map-renderer.md), and **server-side rendering is in v1** — the composition preview and the appearance preview both draw through the real renderer ([ADR-051](adr/ADR-051-an-appearance-is-chosen-by-looking-at-it.md), [ADR-057](adr/ADR-057-composing-and-publishing-a-service.md)), which is the whole reason an operator can see what they are publishing. **What stays out is the rendering *faces*** — WMS and ArcGIS MapServer, cut by §3b, which is unchanged and correct: those are built and out of scope, which is not a contradiction |
| **Folders** | **Added 2026-08-17 by owner decision.** A folder was a text column that existed only while something was in it; it is now a register (migration 18), so an empty one can be created and the directory lists what exists rather than two names typed into the host. **Hosted data always lands in `hosted`; a registered table may be published into a named folder** — owner rule: *"turkiye klasoru sadece reference registered olanlar için"* — and the URL follows: `/rest/services/turkiye/tr_il/FeatureServer`. `hosted`, `Utilities` and `System` are reserved |
| **OGC API Features writing** | **Added 2026-08-25 by owner decision** — Q-44. The read surface was already in ([ADR-042](adr/ADR-042-ogc-api-features.md)); the write half is `POST` to a collection's items and `PUT`, `PATCH`, `DELETE` to one item, which is the shape Part 4 defines and the only sensible mapping onto the addresses the read side publishes. **Every edit goes through the writer ArcGIS `applyEdits` uses**, so the two faces share a transaction story rather than each having one. **No Part 4 conformance class is advertised** until somebody checks the surface against the specification — the claim is separable from the capability, and CLAUDE.md §5 makes the specification the citation |
| **The rest of the FeatureServer data model** | **Added 2026-09-09 by owner decision** — [Q-58c](open-questions.md), recorded in [ADR-013](adr/ADR-013-feature-service-data-model.md) §5a. **Domains, subtypes and editor tracking**, which ADR-013 §1 had left in Q-58 and not decided. They are missing in two different ways and the 2026-08-27 inventory is what says which: `domain` is *present and always null* on every field of every layer document, while `subtypeField`, `subtypes`, `types`, `templates` and `editFieldsInfo` are **absent from the document entirely**. **Editor tracking is a repair rather than a feature** — it is the only reason [D-20](architecture-debt.md) exists, where `features:edit` is narrower here than in ArcGIS Portal because *change your own* is unenforceable while the server cannot tell whose feature is whose. Two conditions carry the risk: a domain or subtype the server reports is one it **enforces on write**, and editor tracking discharges by D-20 closing rather than by four columns existing. Like relationships and attachments before them, this **enlarges the first release rather than following it**, and that is recorded rather than absorbed. **NOT BUILT, measured 2026-09-10** — `subtypeField` and `CodedValue` appear nowhere in `src`, `subtypes` appears once in a comment naming this row, and `editFieldsInfo` appears once as a null on the **ImageServer** document, which is a different surface. So the 2026-08-27 inventory still describes the product exactly, four weeks and one scope decision later |
| **A field list that can carry labels** | **Added 2026-09-09 by owner decision** — [Q-36](open-questions.md), recorded in [ADR-063](adr/ADR-063-a-field-list-may-differ-from-the-table.md). A layer carries per-column overrides: a **display alias** and a **hidden** flag, and nothing else — **computed fields are refused**, with the reason. The field list is still read from the table and overrides are applied to it afterwards by column name, so an override naming a column that has gone is **inert** rather than broken. **It closes a gap the importer has been documenting**: `Graticula.Import.Reader` already reads `GetAlternativeName()` and `GetDomainName()` from every geodatabase field and drops both, under a comment saying our schema has nowhere to put them, while three writers send `alias = field.Name` to every ArcGIS client — which is not *no alias* but a wrong one. ~~**NOT BUILT, measured 2026-09-10** — `Hidden` and `Alias` appear in no source file; `FieldDescription` is still `(Name, Type, Nullable, MaxLength)` and `LayerDescription.Fields` is still whatever the table has. **All three of [ADR-063](adr/ADR-063-a-field-list-may-differ-from-the-table.md)'s conditions are open**, which is the honest reading: the decision is made and the feature is not~~ **BUILT 2026-09-11** — `layer.field_overrides` (migration 42), applied in one place so every face refuses a hidden column as it refuses an absent one, an admin read and write, and a Fields page in Studio; all three conditions discharged with tests. ~~**What is still missing is the piece this row named first**: the importer reads a geodatabase's aliases and still drops them~~ — **and the piece this row named first is built too**: a geodatabase import now gives the published layer the archive's own field aliases as its labels (ADR-063 §5a) |
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
önemli. o yüzden yığın büyüyecekse büyüyebilir."* ~~So **the Python runtime returns
to the job-worker image — for our own code**, because
[ADR-037](adr/ADR-037-job-workers-come-in-two-kinds.md) puts `pyogrio` there and
[Q-108](open-questions.md) established there is nothing GDAL-free to adopt for
.NET and that writing our own reader is the wrong project.~~

**Corrected 2026-09-09, and the scope decision is untouched — only how it is built.**
The owner's *gdb import is important* stands and is what this bullet records.
[ADR-037](adr/ADR-037-job-workers-come-in-two-kinds.md) §5a **reversed the Python
worker within a day of this amendment being written**: there is **one kind of worker,
a .NET child process**, and GDAL is linked into it through `MaxRev.Gdal.Core` rather
than reached through Python — *"the Python runtime returns to being what v1-scope §3c
cut it as"*, in that ADR's own words about this very sentence. So the bullet above is
**not** struck after all: the runtime did not come back. Q-108's finding still holds —
nothing GDAL-free exists to adopt for .NET — and it was answered by linking GDAL
rather than by shipping an interpreter. **This document is the authoritative scope
(CLAUDE.md §1) and it described a reversed decision for twenty-two days**, which is
[D-130](architecture-debt.md)'s propagation shape in the worst possible file.

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

~~**OGC API Features**~~ **Tiles, Styles, Records, Processes, EDR · WFS · WMTS · WPS ·
SensorThings · OData · gRPC · MCP · STAC · PMTiles · 3D Tiles · Terrain-RGB ·
geocoding.**

> **OGC API Features struck 2026-09-09: it is in v1, and this file said so in §2 and
> the opposite here.** §2's own row records the read surface as *already in* and the
> owner adding the write half on 2026-08-25 (Q-44), and the server serves
> `/ogc/features/v1` with [ADR-042](adr/ADR-042-ogc-api-features.md)'s five conditions
> all discharged. **This document is the authority** — [CLAUDE.md](../CLAUDE.md) §1 says
> *where any other document disagrees, v1-scope wins* — so a contradiction **inside** it
> is worse than a stale sentence in a document that loses: it could be quoted to support
> either answer, and the rule that settles disagreements has nothing to settle them
> against. *Corrected rather than deleted, so the change of mind stays visible.*
> [D-130](architecture-debt.md).

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
core domain.** ~~v1 ships **ArcGIS only**, with OGC API Features in v2.~~

**Amended 2026-09-09, and the inversion is smaller than this section said.** v1 ships
ArcGIS **and OGC API Features, read and write** — §2 carries both, the second added by
owner decision on 2026-08-25. What v1 does not ship is **Part 3**, the filtering half,
which is [ADR-005](adr/ADR-005-api-architecture.md) condition 1's subject and still
live. So the inversion is of *primacy* rather than of presence: ADR-005 made OGC API
Features the native surface and ArcGIS the compatibility layer, and v1 builds both with
ArcGIS first. **ADR-005 stays `REOPENED`** — that has not changed and is not this
correction's to change.

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
