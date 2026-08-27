# ADR-009 — Raster Engine

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` — re-closed 2026-08-15, see §0. Reopened 2026-08-13 by Q-17c and closed again when v1-scope cut ImageServer | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-08-12 |

---

## 0a. `AMENDED` 2026-08-21 — the trigger fired, and §2.1 is half reversed

> **[ADR-043](ADR-043-imageserver-and-the-raster-face.md) takes the decision this ADR
> parked.** §0 below says the reopening trigger is *ImageServer, or any part of the
> expensive group, returning to scope*. The owner returned it on 2026-08-21 and chose
> the widest cut: the catalogue plus `exportImage`, with imagery registered in place.
>
> **What reverses.** §2.1's *serve COG, let the client render* no longer describes the
> export path: the server decides what colour a pixel becomes. It still describes
> direct delivery, and §2.4's range-request proxy is what makes *registered in place*
> possible at all, so that section is load-bearing rather than superseded.
>
> **What this ADR got wrong, and it is worth reading before planning from §0's table.**
> The cost decomposition was written on 2026-08-12 against a design. Measured against
> the code on 2026-08-21, the *near-free* group is not near-free — there is no raster
> code in `src` at all, and `LayerDefinition` requires a table and a geometry column, so
> every layer this server can hold is a PostGIS table. And the *expensive* group is
> cheaper than stated, because ADR-041 shipped the delivery half. ADR-043 §2 carries the
> measurement.
>
> **The decomposition itself is not retracted and is still the right way in.** That was
> §0's own advice and this is what taking it looks like.

## 0. `REOPENED` 2026-08-13 — ImageServer is in scope

> **RE-CLOSED 2026-08-15. The trigger was removed before it was answered.**
>
> Q-17c put ArcGIS ImageServer in scope and that is what reopened this.
> **v1-scope cut ImageServer**, and says so directly: *"ADR-009 can re-close."*
> It never did, and three conditions of a decision nobody is implementing have
> been counted as outstanding work since.
>
> **So this reverts to what the ADR decided: serve COG, let the client render**,
> with raster imagery catalogued rather than rasterised. The decomposition below
> is not retracted and is the most useful thing in this document — whoever
> reopens this should start from the three groups rather than from *ImageServer
> support*, because that is the mistake Q-17a made and this table exists to avoid
> repeating.
>
> **The reopening trigger is ImageServer, or any part of the expensive group,
> returning to scope.** Q-77 still owns the Tier 1 line that has to be drawn
> first.
>
> Recorded as bookkeeping rather than as a decision: v1-scope is authoritative
> and had already stated this consequence.

This ADR decided **serve COG, let the client render**, and that raster imagery is
catalogued rather than rasterised. Q-17c puts ArcGIS **ImageServer** in scope,
which reverses the central decision.

**Decomposed per operation, applying Q-17a's lesson rather than repeating its
mistake.** ImageServer is not one capability; it is three groups at very
different cost, and accepting or declining it whole would bundle them the way
Q-17 bundled four services under one sentence.

| Group | Operations | Cost against what we already have |
|---|---|---|
| **Near-free** | `identify`, `getSamples`, `computeHistograms`, `computeStatisticsHistograms`, `download`, mosaic footprint `query` | A COG range read, a GDAL call we already make, or — for footprints — an ordinary feature query over the catalogue. Almost nothing new |
| **Medium** | Tiled access to already-processed imagery | A tiling scheme over rasters. No dynamic rendering if the pixels are pre-processed |
| **Expensive — the actual decision** | `exportImage`, raster function chains, dynamic mosaicking | Needs a **raster rendering engine**, a **processing-chain evaluator**, and a **mosaic dataset model** with footprints, overviews, priorities and seamlines |

**The third group is plausibly the largest single capability in the matrix,
larger than the vector renderer**, and it should be scheduled as its own decision
rather than absorbed as *ImageServer support*.

**Q-77 owns the line that has to be drawn first.** GDAL does much of the
mechanical work — warp, VRT-as-mosaic, overviews — and already lives in the job
worker under A-016, which makes it Tier 2 raster I/O. But choosing what colour a
pixel becomes is **cartographic logic, which the build-vs-adopt policy puts in
Tier 1**. The line probably falls between *assembling and warping pixels*
(adopted) and *deciding what colour they are* (ours). It needs stating before
either half is built.

**What survives unchanged:** COG as the storage format, conversion at
registration, GDAL on job workers, and proxied delivery (§2.4). Nothing below is
wrong — it is now incomplete.

## 1. Context

Rescoped by the vector-first decision
([product-context.md](../product-context.md)). **The server does not produce
pixels.** Raster and imagery are catalogued, validated and access-controlled;
the client fetches COG over HTTP range requests and renders.

That removes dynamic tiling, mosaicking on read, raster functions and
server-side reprojection of imagery from scope. What remains is smaller but not
trivial, and one part of it is a security decision rather than a raster one.

## 2. Decision

### 2.1 We serve COG. Everything else is converted or refused.

The client renders, so we can only serve what a client can read over range
requests. That makes the format contract simple and worth stating plainly:

- **COG is the served format.** One contract, one client expectation.
- **Other formats are converted at registration**, as a job
  ([ADR-011](ADR-011-job-system.md)). A plain GeoTIFF, a JPEG2000, an ECW — GDAL
  reads it once, at registration, and writes a COG.
- **What cannot be converted is refused**, with an explanation.

The alternative — serving arbitrary formats and hoping the client copes — pushes
our format problem onto every client and guarantees inconsistent behaviour. GDAL
reads hundreds of formats; browsers read approximately one.

**Added 2026-08-18, and it is a clarification rather than a change: a raster that is
already a COG needs no GDAL anywhere.** Serving one is HTTP range reads over a TIFF,
and §2.2 already keeps GDAL out of the serving artefact — so a deployment whose
imagery arrives as COG never loads it. The three bullets above are all true and
together they *imply* this, which is exactly why it is worth writing down: read
quickly, *"other formats are converted at registration … GDAL reads it once"* leaves
a reader believing raster requires GDAL. It requires GDAL to **ingest a
non-COG**, which is a different sentence and a much smaller claim.

**Why it came up.** The owner asked whether GDAL becomes mandatory once raster is in
scope — *"raster olsa gdal zorunlu mu?"* — and the honest answer needed this line to
exist. It is the same shape as
[ADR-037](ADR-037-job-workers-come-in-two-kinds.md)'s: the serving path is clean,
the ingest path is a job, and the worker is optional at the cost of exactly the
formats it converts.

~~**What is still not written, and is not decided here:** registering a COG **in
place** — catalogued and served from where it already lives, with no copy into a
writable store. §2.1's next paragraph says conversion needs somewhere to write and
that a read-only source holding a non-COG can be catalogued but not served; it does
not say what happens when that read-only source holds a COG, which is the case where
nothing needs writing at all. [Q-121](../open-questions.md) opens it.~~
**Decided 2026-08-21 by owner decision, and this paragraph outlived it by six days** —
[Q-121](../open-questions.md), recorded in
[ADR-043](ADR-043-imageserver-and-the-raster-face.md) §3.3: **yes, registered in place and
never copied.** Registration reads the header and stores a reference; the file stays where it
is. The bytes still travel through this server, because §2.4 already settled that a client
fetching a COG straight from object storage cannot be authorised per layer — so a
range-request proxy is what *in place* costs.
**And it is built.** `POST /admin/coverages` refuses an unreadable path with the sentence
*"Imagery is registered in place, so the path is read at every request rather than once at
upload"*, which is this decision speaking to an operator at the moment it applies. Measured
2026-08-27 against a running server, along with the paragraph above still saying it was
undecided. That is [D-130](../architecture-debt.md)'s shape exactly: a decision taken in one
place and left standing in every document that restated the question. **Derived from
reading the reference's public description** — ADR-030 condition 1 — which advertises
*"No GDAL required on the server"* and cloud-optimized GeoTIFFs *"registered in place
from S3/Azure"*; the properties of COG itself are cited from
[its own specification](https://cogeo.org/) rather than from them, per condition 3.

Conversion needs somewhere to write, which means a writable store
([data-model.md](../data-model.md) §2). A registered read-only source containing
a non-COG raster can be catalogued but not served. That is a real limitation and
it should be documented rather than discovered.

### 2.2 GDAL runs at registration, not per request — and not in the serving artefact

**Strengthened 2026-08-12** from a placement decision to an artefact rule, after
observing the same rule stated more cleanly elsewhere
([reference-reading-log.md](../research/reference-reading-log.md)):

> **The serving container ships no GDAL.** It exists only in the job worker
> image.

This is stronger than "GDAL runs on job workers", because it is checkable at
build time rather than depending on discipline. It answers Q-28 by construction,
makes the single-binary deployment real, removes an untrusted-file parser from
the process that serves public requests, and keeps GDAL driver data out of the
air-gapped checklist for the serving artefact.

**Amended 2026-08-19: the boundary is the process, not the image.**
[ADR-037](ADR-037-job-workers-come-in-two-kinds.md) §5a reversed the worker's
language on the same day it was decided, and GDAL now arrives as
`MaxRev.Gdal.Core` in this solution rather than as command-line tools in a
separate image. Read the block quote above as **"the serving process never loads
GDAL"**, which is still true and is the property every reason in the paragraph
above actually rests on: `Graticula.Import.Reader` is an executable referenced by
nothing, spawned as a child by `GeodatabaseInspector`, and the untrusted-file
parser is still not in the process answering public requests.

**What the amendment costs is stated rather than absorbed.** The sentence
strengthened this from a placement decision *to an artefact rule* precisely
because an image boundary is checkable and a placement is not — and the artefact
rule is what was spent. The replacement check is
`NativeDependencyTests`: a confined dependency is referenced by its one project
and no other, and the serving project cannot reach it through a project
reference either. That is narrower than *no reference anywhere* and it is
mechanical, which is the property worth keeping.
[D-88](../architecture-debt.md), ADR-037 condition 4.

It also gives a clean test for Q-52: **any provider that needs GDAL cannot be a
serving provider.** It is an import source.

Registration and conversion run on job workers, isolated
([ADR-007](ADR-007-service-runtime.md) §4.1). This is where GDAL's thread
affinity and crash risk are contained, and it is why they stopped being a hot
path problem ([research/dependency-thread-safety.md](../research/dependency-thread-safety.md)
§5).

Registration does the work that must not happen later:

- Is this a valid, readable raster with usable overviews?
- CRS, extent, band structure, data type, nodata.
- **Bomb checks** (§54): decompression ratio, declared versus actual dimensions,
  absurd band counts, pathological tiling. A malformed raster must fail at
  registration, in an isolated process, not at request time in a shared one.

### 2.3 STAC is the catalog model

`VERIFY` STAC as the metadata model for imagery collections. It is the
established answer, it composes with the wider ecosystem, and inventing our own
would be §82's exact prohibition.

Our service catalog references STAC items rather than duplicating them. A
collection of imagery is a collection, not a thousand services.

### 2.4 Delivery is proxied by default — Q-27 answered

This is the consequential decision, and it is about authorization, not raster.

If the client fetches a COG directly from object storage, **per-layer
authorization stops working.** Our primary user is a GIS administrator for whom
controlling who sees which layer is a requirement (§41, §42). A public bucket
URL enforces nothing.

| Option | Assessment |
|---|---|
| **Signed, expiring URLs** | Cheap, bytes bypass us. But expiry is blunt, revocation is not immediate, and it requires an object store that supports signing — which an air-gapped filesystem deployment does not have. |
| **Range-request proxy** | Authorization is exact and immediate. Works on every storage backend including a plain filesystem. Costs bandwidth through the server. |
| **Hybrid** | Proxy by default, signed URLs where explicitly enabled. |

**Decision: proxy by default, signed URLs as an optional optimisation.**

**A correction to an earlier assessment.** The proxy option was previously
described as putting "terabyte-scale bandwidth back through the server". That
overstated it. COG range requests are *view-proportional*: a client fetches the
overview blocks for what is on screen, typically a few hundred kilobytes, not
the dataset. Proxying that is comparable in cost to serving tiles, which we do
anyway.

Three further arguments the earlier assessment missed:

- **The proxy works everywhere.** Filesystem, air-gapped, any object store, no
  signing support required. Signed URLs work only where the backend offers them.
- **The proxy is a place to enforce things we want anyway**: request size
  limits, rate limiting, per-layer audit logging.
- **The proxy can cache.** Overview-level range requests are highly repeated
  across users, and they go straight into L3
  ([ADR-010](ADR-010-caching.md)). Signed URLs cache nowhere we control.

Signed URLs remain available for explicitly public layers and for deployments
where measured bandwidth makes them worthwhile. They are an optimisation with a
documented authorization weakness, not the default.

**Start dumb.** The proxy is a byte-range forwarder that knows nothing about COG
structure. A structure-aware proxy could validate ranges and pre-warm overview
blocks, and that is an optimisation to reach for only with evidence.

### 2.5 Overview generation is a job

For uploaded imagery that is not already cloud-optimised. Long, I/O heavy,
resumable, and subject to [ADR-011](ADR-011-job-system.md)'s rate limiting.

## 3. What this ADR no longer covers

Recorded so the reduction is deliberate rather than accidental: dynamic tiling
from COG, mosaicking on read, raster functions and algebra, server-side
reprojection of imagery, multidimensional raster, raster analysis.

Some of these could return as **geoprocessing jobs** producing new stored
outputs, which is a different and much cheaper shape than a per-request raster
pipeline. That path stays open through [ADR-011](ADR-011-job-system.md).

## 4. Counterarguments

- **Convert-or-refuse will annoy people.** An organisation with a directory of
  plain GeoTIFFs wants them served, not converted. The counter is that
  conversion happens once and the alternative is unpredictable client behaviour
  — but the first user to hit it will not enjoy it.
- **A read-only registered source containing non-COG raster cannot be served at
  all.** There is nowhere to write the conversion. This is a real gap with no
  good answer short of a writable store.
- **Proxying is still bandwidth we did not previously spend**, and the
  view-proportional argument is a reasoned estimate rather than a measurement.
  If real imagery workloads move far more data than tile workloads, §2.4 should
  be revisited.
- **STAC may be more than we need** for an organisation with twelve orthophotos.
  Adopting it should not make the simple case heavy.

## 5. Consequences

**Positive.** Vastly smaller than the original scope. GDAL is off the request
path, which removes the platform's most likely load-dependent correctness bug.
One served format means one client contract. Authorization works on every
storage backend.

**Negative.** Conversion at registration costs time and storage. Non-COG raster
in a read-only source is unservable. Proxy bandwidth is a new cost. We depend on
clients being able to render COG, which most modern web mapping stacks can and
older ones cannot.

## 6. Assumptions

| ID | Assumption | Status |
|---|---|---|
| A-032 | COG range-request traffic is view-proportional and therefore comparable to tile traffic, making proxying affordable | `UNVALIDATED` — §2.4 rests on it |
| A-033 | Target clients can render COG directly, so serving one format is sufficient | `UNVALIDATED` |

## 7. Dependencies

**Depends on:** [ADR-007](ADR-007-service-runtime.md) (job workers as the
isolation boundary), [ADR-011](ADR-011-job-system.md) (registration, conversion,
overviews), [ADR-010](ADR-010-caching.md) (proxied ranges are cacheable),
[data-model.md](../data-model.md) (conversion needs a writable store).

**Depended on by:** publishing (§38), security (§54), ADR-012.

## 8. Conditions

1. **Bomb checks run at registration, in an isolated process, before anything is
   catalogued.** This is the security condition and it is not optional.
2. **A-032 must be measured** before proxying is committed to at scale. If
   imagery traffic dwarfs tile traffic, the default flips.
3. **The read-only non-COG gap is documented**, not left to be discovered.

## 9. Revisit triggers

- Measured imagery bandwidth makes proxying untenable.
- A client ecosystem emerges that cannot render COG.
- Server-side raster analysis becomes a product requirement, which would bring
  back a pixel pipeline as geoprocessing rather than as a request path.

## 10. Dissent

**Refusing to produce pixels is a bigger product decision than an architecture
decision, and it is being made in an architecture document.** TiTiler exists and
is widely used precisely because dynamic server-side tiling from COG is useful:
it lets any client, however simple, consume imagery as ordinary map tiles.

By declining it we require clients capable of COG rendering. That is most modern
web mapping and very little else — no simple WMTS consumer, no older desktop
tool, no basic embed. Given migration is a goal, some of what we are displacing
will be exactly those clients.

The compatibility layer could serve imagery tiles the same way it serves WMS,
and that would close the gap. It is not in this ADR and it should be considered
when Q-17 is answered.
