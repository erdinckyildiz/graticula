# Hosted Datastore and the Tile Path

**Status:** PROPOSAL — written 2026-08-12 from an owner question.
**Question:** `ST_AsMVT` is PostGIS-only. Rather than leaning on it, should we
build something else — for example an ArcGIS-Data-Store-style managed relational
datastore, with vector tiles requiring data to be hosted there?
**Feeds:** [ADR-008](../adr/ADR-008-query-engine.md),
[ADR-002](../adr/ADR-002-primary-data-architecture.md),
[ADR-010](../adr/ADR-010-caching.md), publishing (§38), Q-31.

---

## 1. Three separate things

The question bundles three decisions that have different answers. Separating
them is most of the work.

1. **Do we write our own MVT encoder?**
2. **How do we absorb the performance difference between providers?**
3. **Do we run a managed datastore we control?**

## 2. Own MVT encoder — yes, unavoidable

If Oracle Spatial or SQL Server Spatial can serve tiles at all, we encode them.
There is no alternative and no way to defer it.

The cost is smaller than it sounds. MVT is a fully specified protobuf format,
already placed in Tier 1 by
[build-vs-adopt-policy.md](../build-vs-adopt-policy.md). Encoding is not the
hard part. The hard part is preparing geometry for a tile: clipping to the tile
envelope, simplifying for the zoom level, transforming to tile coordinate space,
and quantising.

### What can be pushed down, per dialect

This matters more than the encoder itself, because it decides how much data
crosses the wire.

| Step | PostGIS | SQL Server Spatial | Oracle Spatial |
|---|---|---|---|
| Bounding-box filter | yes | yes | yes |
| Clip to tile envelope | `ST_Intersection` | `STIntersection` | `SDO_GEOM.SDO_INTERSECTION` |
| Simplify | `ST_Simplify` | `Reduce` | `SDO_UTIL.SIMPLIFY` |
| Transform to tile grid | `ST_AsMVTGeom` | — | — |
| Protobuf encode | `ST_AsMVT` | — | — |

`VERIFY` every cell in this table before ADR-008 is decided. It is written from
general knowledge and the exact semantics — especially what each `simplify` does
to topology, and whether each `intersection` is affordable at tile rates — are
what actually determine the design.

The shape is already visible: **the first three steps are portable, the last two
are not.** So the portable design is that the database filters, clips and
simplifies where it can, returns WKB, and we do tile-space transform,
quantisation and encoding. `ST_AsMVT` then becomes an optional fast path on
PostGIS rather than the architecture.

That is exactly the capability gradient ADR-008 already wants, arrived at from a
different direction.

### The two paths must agree

If PostGIS uses `ST_AsMVT` and everything else uses our encoder, **the same
layer will look different depending on where it is stored** unless the two paths
produce equivalent output. Different simplification, different quantisation,
different handling of degenerate geometry.

This needs a conformance test in the style of `experiments/geometry-oracle`:
same source data, both paths, compare the decoded tiles. Registered as a
requirement on ADR-008, not an implementation detail.

It is also an argument for **not** using `ST_AsMVT` at all — one path is easier
to keep correct than two. Tegola made that trade deliberately and pays for it in
speed ([postgis-thin-servers.md](postgis-thin-servers.md) §3.2).
`benchmarks/mvt-generation/` decides whether the speed is worth the second path.

## 3. The real equaliser is the cache, not the encoder

The framing worth changing: provider performance differences do not have to be
paid per request.

If a tile is expensive to build from Oracle, we build it once and serve it from
cache. Seeding moves the cost to a time when nobody is waiting. Tegola's
existence is the evidence — it gives up `ST_AsMVT` speed in exchange for
filesystem caching and cache seeding, and remains widely used.

This reorders our priorities:

1. **Cache and seeding must be strong.** This is where the provider gap is
   absorbed. Already flagged as one of the few defensible answers to Q-18.
2. **The encoder must be good enough**, not exceptional.
3. **`ST_AsMVT` is an optimisation**, not a dependency.

It also means the tile path's worst case is not "Oracle is slow per request" but
"seeding 1,000 services takes how long, and what happens when data changes".
That is a cache-lifecycle problem, and it belongs to
[ADR-010](../adr/ADR-010-caching.md).

## 4. The managed datastore

ArcGIS Enterprise runs a managed relational data store on PostgreSQL, and
distinguishes **hosted** content (copied into it) from **registered** content
(referenced in place, in the customer's own database). Publicly documented
behaviour, and legitimate prior art (§4, §5).

### What it actually buys — and it is not `ST_AsMVT`

The valuable thing is far more basic:

> **A place where we have write permission.**

In a registered Oracle database we can do nothing but read. No tables, no
indexes, no materialised anything. A datastore gives us somewhere to put:

- uploaded data, where a user pushes a file and it must live somewhere;
- tile cache contents, closing the ADR-002 §5 gap where cache bytes had no home;
- pre-generalised geometry per zoom level, which is the real fix for slow tiles
  rather than a faster encoder;
- our own editing, versioning and concurrency model, unconstrained by what the
  customer's DBA allows;
- data copied in during migration from Oracle or SQL Server.

### The strongest single example

Runtime schema evolution. ArcGIS lets an administrator add fields, add indexes
and change some properties on a live layer, **and only on hosted layers**. See
[runtime-schema-evolution.md](runtime-schema-evolution.md).

This is not a clever feature. It is a direct consequence of owning the store,
and it is unavailable on registered data for precisely the reason this section
gives. If Q-32 needs a concrete answer to "what does the datastore actually buy
an administrator", this is it.

### It also answers Q-31 as a product question

Q-31 asked whether we expose provider capability differences or hide them. The
hosted/registered split answers it with a distinction an administrator can hold
in their head:

> **Hosted data gets full capability. Registered data gets whatever its provider
> supports.**

That is a much better promise than publishing a capability matrix per provider,
and it is honest rather than silently degrading.

### But it must not be mandatory

Here is the tension, stated plainly.

Two decisions ago we removed mandatory PostgreSQL, for a good reason: an
organisation running Oracle Spatial does not want to install and operate
PostgreSQL because of us
([multi-database-consequences.md](multi-database-consequences.md) §1). Making
the datastore mandatory brings that barrier straight back under a different
name.

**Proposal: the datastore is an optional, recommended component.**

| Deployment | Result |
|---|---|
| Datastore installed | Hosted layers, fast tiles, editing, full capability. The platform store lives in it by default, so this is **one PostgreSQL**, not two. |
| No datastore | Registered layers only. Tiles built by our encoder and cached. Platform store in SQLite or the customer's own RDBMS. Reduced capability, clearly documented. |

This is more flexible than ArcGIS, where the relational data store is
effectively required for hosted content.

The cost is honest and should be recorded: **two deployment shapes to test.**
The same cost the four platform stores already carry, and the same discipline
applies — an untested shape is a broken shape.

### Do not make "vector tiles require hosted data" a rule

The owner raised this as a possible policy. Recommendation: **no.**

It is not technically necessary, because we are writing the encoder anyway. As a
rule it would make us unable to do something GeoServer can do — serve vector
tiles from any supported store — which is a competitive weakness in front of the
exact migration target we are chasing.

Document the performance difference instead and let the administrator decide.
That fits the primary user, who can read a number and make a choice.

There is one genuine caveat: a read-only registered Oracle gives us nowhere to
put generalised geometry or extra indexes, so its tiles may be *structurally*
slower rather than just slower. Caching mitigates it. The honest position is
that this is a documented characteristic, not a blocked feature.

## 5. Proposed shape

1. **Own MVT encoder.** Tier 1, mandatory, the default path.
2. **Per-dialect pushdown** of filter, clip and simplify, negotiated by the
   query engine's capability model.
3. **`ST_AsMVT` as an optional PostGIS fast path**, adopted only if
   `benchmarks/mvt-generation/` shows the gain is worth a second code path, and
   only behind a conformance test proving output equivalence.
4. **Cache and seeding as the primary answer** to provider performance
   differences.
5. **Optional managed datastore** on PostgreSQL, providing hosted layers, a home
   for cache bytes and generalised geometry, and the capability floor.
6. **Hosted versus registered** as the product-level capability distinction,
   answering Q-31.

## 6. New questions

| # | Question |
|---|---|
| Q-32 | Is the managed datastore in scope for v1, or a later phase? It is the single largest addition here, and it overlaps with publishing (§38). |
| Q-33 | If a datastore exists, does hosting copy data or replicate it continuously? Copy is simple and goes stale; replication is a synchronisation product nobody asked for. Copy, with explicit refresh, is the likely answer — but it must be chosen, not defaulted into. |
| Q-34 | Are pre-generalised geometry tables per zoom level a datastore feature, or something we also attempt on registered sources where we happen to have write access? |
| Q-31 | **Answered in principle:** hosted gets full capability, registered gets provider capability. Confirm in ADR-005 and ADR-008. |
