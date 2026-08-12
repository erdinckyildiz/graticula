# Thin Servers — pg_tileserv, pg_featureserv, Martin, Tegola, TiTiler

**Status:** FIRST PASS — architectural argument solid; performance figures
marked `VERIFY`.
**Feeds:** assessment §8, §9, §10; **Q-18** (what justifies a server at all);
[ADR-005](../adr/ADR-005-api-architecture.md),
[ADR-008](../adr/ADR-008-query-engine.md),
[ADR-010](../adr/ADR-010-caching.md), [ADR-009](../adr/ADR-009-raster-engine.md)

---

## 1. The question these answer

The previous notes looked at heavyweight servers and asked how they manage
complexity. This one asks the opposite question:

> **How thin can a GIS server be, and where does thin stop being enough?**

The answer bounds our scope from below. Everything a thin server already does
well is something we must either match trivially or justify doing differently.

## 2. The thin-server thesis

pg_tileserv describes itself as "a very thin PostGIS-only tile server in Go.
Takes in HTTP tile requests, executes SQL, returns MVT tiles." Together with
pg_featureserv it forms "a spatial services architecture of stateless
microservices surrounding a PostgreSQL/PostGIS database cluster".

The design move worth studying is that **the constraint is the feature**:

> "By restricting itself to only using PostGIS as a data source, `pg_tileserv`
> gains the following features."

Explicitly, unlike Tegola, GeoServer and MapServer, it does *not* support
multiple data sources. From that single restriction it gets:

- **Automatic publishing.** "The server can discover and automatically publish
  as tiles sources all tables it has read access to: just point it at a
  PostgreSQL/PostGIS database." No publishing workflow, no service definitions,
  no catalog.
- **Authorization for free.** "You can restrict access to tables and functions
  using standard database access control", including row-level security for
  per-role filtering. No RBAC subsystem — the database already has one.
- **Arbitrary computation for free.** Function layers: "the server can run any
  SQL to generate tile outputs. Any data processing, feature filtering, or
  record aggregation that can be expressed in SQL, can be exposed as
  parameterized tile sources." No geoprocessing subsystem.
- **Tile encoding for free.** `ST_AsMVT()` in the database. No MVT encoder.

Four subsystems that we currently plan to build — publishing, authorization,
geoprocessing, tile encoding — deleted by one constraint.

**This is the most serious challenge to our scope in the research so far**, and
it deserves a real answer rather than "but we need providers".

## 3. Where thin stops

### 3.1 The moment a second data source appears

Every deletion in §2 depends on there being exactly one data source that happens
to be a capable database. Add a GeoPackage, a COG, or a second database and all
four come back at once: discovery has nothing to discover, database roles do not
cover a file, SQL functions cannot filter a COG, and `ST_AsMVT` is not available.

Our provider requirement (§27) is not a preference — it is the specific decision
that forfeits the thin-server dividend. It should be stated that plainly in the
assessment, because it is the largest single source of complexity in our design
and it needs to be paid for by a real requirement.

Given the confirmed migration goal, it is paid for: an organisation displacing
ArcGIS Server or GeoServer has data in more than one place. But the reasoning
must be written down, not assumed.

### 3.2 Caching and invalidation

`VERIFY` The comparison literature splits these tools cleanly:

- **Martin** (Rust, Actix) and **pg_tileserv** (Go) generate tiles on the fly
  using `ST_AsMVT`. `VERIFY` Martin is reported fastest, "two to three times
  faster than the second fastest server".
- **Tegola** (Go) does not use `ST_AsMVT` — it encodes in the server — and in
  exchange offers real cache infrastructure: filesystem caching with pluggable
  backends, plus **cache seeding** to populate before requests arrive.

`VERIFY` the claim that not using `ST_AsMVT` is why Tegola is slower; that is
asserted in secondary sources and is exactly the kind of thing our own benchmark
should settle rather than inherit.

The architectural reading: **on-the-fly generation and cache management are
alternative strategies, and the thin servers each pick one.** Tegola gives up
raw speed for seeding and invalidation; Martin and pg_tileserv give up cache
management for speed.

We cannot pick one. A managed platform with an administrator, editable data and
1,000 services needs both: fast dynamic generation *and* seeding, invalidation
and cache lifecycle. That is a real justification for being thicker — and it
lands squarely on [ADR-010](../adr/ADR-010-caching.md), where invalidation was
already flagged as the harder half.

### 3.3 Everything an administrator needs

Automatic publishing is excellent until someone must answer *why is this service
slow*, *who may see this layer*, *when was this cache invalidated*, *which
services broke when that column was dropped*. There is no service lifecycle to
observe (§23) because there are no services — only tables.

Our primary user is the GIS administrator
([product-context.md](../product-context.md)). The thin-server model is
essentially **an architecture with no administrator in it**: the DBA administers
the database, and the tile server is a stateless process nobody manages. That is
a coherent and attractive design. It is simply not the product we were asked to
build, and the gap is precisely our stated user.

### 3.4 The raster case — TiTiler

`VERIFY` TiTiler (FastAPI + rio-tiler + GDAL) does dynamic raster tiling
straight from COGs over HTTP range requests: read by range request, decode the
relevant internal tiles, resample to the requested XYZ tile, apply
rescale/colormap/nodata, encode. "Instead of downloading a 50 GB GeoTIFF file to
render a map tile, TiTiler may only need to download a few hundred kilobytes."
TiTiler-PgSTAC extends this to dynamic mosaics from a STAC database.

This is the same thesis on the raster side, and it is a direct input to
[ADR-009](../adr/ADR-009-raster-engine.md): **dynamic tiling from COG is the
modern default, and pre-generated raster caches are the exception needing
justification** — the reverse of the historical assumption.

Note the honest consequence for us: TiTiler's model *is* GDAL-in-process on
range-requested remote files. It is exactly the thread-affinity and
crash-containment problem from
[dependency-thread-safety.md](dependency-thread-safety.md) §5, in production,
at scale. Worth studying how they handle worker isolation and failure.

## 4. What to take

1. **Auto-discovery as a first-class publishing mode.** The strongest single
   idea here. Our publishing architecture (§38) should support "point it at a
   schema and publish what you can read" as a genuine mode, not merely a bulk
   import wizard. With 1,000 services and a GIS administrator, the ability to
   avoid defining 1,000 services by hand is worth a great deal. It also fits the
   migration goal, where data already exists elsewhere.
2. **Delegating authorization to the database where the database can do it.**
   Row-level security is real, tested, and already deployed in customer
   environments. Our RBAC (§42) should be able to *defer* to it for PostGIS
   providers rather than always layering a second authorization model on top.
   Two authorization systems disagreeing is a security defect waiting to happen.
3. **Function layers.** Parameterised SQL exposed as a tile or feature source is
   a remarkably cheap way to cover a long tail of requirements that would
   otherwise each need a feature. Worth taking, with the obvious caveat that
   §29's rule stands absolutely: parameterised, never concatenated.
4. **`ST_AsMVT` as the default path for PostGIS**, with our own encoder as the
   fallback for providers that cannot. This is the capability-negotiation
   gradient from ADR-008 in its most concrete form — and
   `benchmarks/mvt-generation/` is already registered to test exactly this
   trade-off.
5. **Dynamic-first raster.** Cache raster only where measurement justifies it.

## 5. What this changes

- **Q-18 gains its sharpest formulation.** Not "server versus static files" but:
  *what does a managed platform add over a stateless thin server plus a capable
  database?* The defensible answers so far are multi-provider access, cache
  lifecycle and invalidation, service-level observability and administration,
  managed publishing with validation and rollback, and a compatibility surface
  for migration. Each of those must be individually defensible, because each is
  a subsystem the thin servers deleted.
- **Auto-discovery becomes a design requirement**, not a nice-to-have. Added to
  the publishing work (§38).
- **ADR-010 gains a sharper framing**: Tegola versus Martin is the
  seeding-versus-dynamic trade-off in the wild, and we need both.
- **ADR-009 gains a default**: dynamic tiling from COG, with pre-generation as
  the exception.
- **The provider abstraction now has a stated price.** §3.1 is the honest
  accounting of what §27 costs us, and the migration goal is what pays for it.

## 6. Still to investigate

- `VERIFY` the Martin/Tegola/pg_tileserv performance claims independently. They
  come from secondary comparisons and one benchmark repository; our own
  `benchmarks/mvt-generation/` should settle the `ST_AsMVT`-versus-server-side
  question with our data rather than inheriting someone else's conclusion.
- How Martin and pg_tileserv handle connection pooling against PostgreSQL —
  directly relevant to the §25 connection budget, and these are the systems that
  have actually operated at high tile rates.
- Whether any of them offers cache invalidation on data change, or whether
  seeding is always manual.
- How TiTiler manages GDAL worker isolation and failure under load.
- Whether auto-discovery has a documented failure mode at large table counts —
  1,000 tables discovered automatically is our target scale arriving through the
  back door.

## Sources

- [pg_tileserv on GitHub](https://github.com/CrunchyData/pg_tileserv)
- [pg_tileserv introduction](https://github.com/CrunchyData/pg_tileserv/blob/master/hugo/content/introduction/_index.md)
- [About pg_featureserv](https://access.crunchydata.com/documentation/pg_featureserv/latest/introduction/)
- [pg_tileserv documentation](https://access.crunchydata.com/documentation/pg_tileserv/1.0.11/introduction/)
- [Comparing PostGIS-backend vector tile servers: tegola / martin / pg_tileserv](https://dev.to/mierune/comparing-postgis-backend-vectortile-servers-tegolamartinpgtileserv-5c6n)
- [vector-tiles-benchmark](https://github.com/FabianRechsteiner/vector-tiles-benchmark)
- [Serving vector tiles, fast — Spatialists](https://spatialists.ch/posts/2025/04/05-serving-vector-tiles-fast/)
- [TiTiler](https://developmentseed.org/titiler/)
- [rio-tiler](https://github.com/cogeotiff/rio-tiler)
- [Dynamic map tiling with Cloud-Optimized GeoTIFFs — Kyle Barron](https://kylebarron.dev/blog/cog-mosaic/overview/)
