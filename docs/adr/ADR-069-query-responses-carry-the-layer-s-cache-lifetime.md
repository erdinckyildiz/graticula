# ADR-069 — Query responses carry the layer's cache lifetime

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-09-13, the owner's decision to make FeatureServer `query` responses cacheable, made alongside two other changes the same night — response compression (ADR-068) and GeoParquet vector tiles. **Which layers this applies to and what it uses as the freshness knob is `INFERRED`** and listed in §11 for confirmation. |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

The ArcGIS JS SDK draws a feature layer with a FeatureServer `query` GET per tile —
`f=json&geometry=…&maxAllowableOffset=…&resultType=tile&outSR=3857…` — the same way it draws a
vector tile layer with a `.pbf` GET per tile. The owner watched a map "go back and forth",
re-downloading tiles the browser had already drawn, on zooming out and back in.

**Measured against the live showcase, 2026-09-13**, `curl -D -` against a running query endpoint,
once for a PostGIS layer and once for a GeoParquet layer:

```
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8
```

Neither carried `Cache-Control`, `ETag` or `Last-Modified`. RFC 9111 gives a cache nothing to keep
a response by without at least one of these, so Chrome had no grounds to reuse any of them — every
tile, 0.6–4 MB, was re-fetched on every return to a zoom level already visited.

`VectorTileEndpoints.WriteTileAsync` already solves exactly this problem for the `.pbf` face,
built on one owner-facing setting: `PublishedLayer.CacheLifetime` — null for the server default,
zero for `no-store`, a value in seconds sent back as `Cache-Control: max-age` so every cache in
the chain agrees with this one (D-248 carries its own history). The question this ADR answers is
whether the `query` face should be put under the same knob, and what that costs to do safely.

## 2. Alternatives considered

### Alternative A — the layer's own `CacheLifetime`, the same number tiles already use *(chosen, `INFERRED`)*

**Argument for.** One knob, not two. An operator who has already decided how stale a cadastral
layer's map may be (D-25, A-028: only the administrator knows which layer that is) does not have
to make the same decision twice for the two faces that draw it. Nothing new to configure, nothing
new to explain on the admin surface, and the two faces already agree that null means the server
default and zero means never.

**Argument against.** `CacheLifetime` was designed and named for a rendered tile pyramid a
publish-time decision invalidates deliberately (`ITileCache` is told to drop entries on an edit). *(Corrected 2026-09-15: it was not. `Purge` ran on unpublish and schema change only, and a feature deleted through `applyEdits` stayed in cached tiles for the layer's lifetime. `LayerConnections.WriterFor` now hands every editing face a writer that empties the layer's tiles once an edit is kept — `TilePurgingWriter`.)*
A `query` response is not a tile: it can carry arbitrary `where`, `outFields`, statistics — surface
area the tile pyramid never had to reason about — and an editable layer's query answers change on
every edit the same way a hosted layer's tiles do, which the mechanism handles, but nothing today
asks an operator whether they want queries and tiles to expire on different schedules. This is the
half of the inference recorded in §11.

### Alternative B — a second, query-specific lifetime

**Argument for.** Correct in principle: tiles and queries are different responses with different
costs to regenerate, and conflating their staleness policy is exactly the kind of two-knobs-that-
should-be-one mistake this project's own tile design was written to avoid *elsewhere*.

**Argument against.** Nothing today gives an operator a reason to set them differently, no admin
screen offers the second setting, and adding one before anyone has asked for it is exactly what
§6 (anti-overengineering) asks *what concrete problem does this solve?* about. If an operator ever
wants a layer's queries fresher than its tiles — a reasonable request, since a `where` clause can
target a subset that changes faster than the layer as a whole — that is new configuration surface,
not a default this change should invent unasked.

### Alternative C — no caching, fix it with `resultType=tile` alone

**Argument for.** The SDK already sends `resultType=tile`, which narrows the query to a grid cell;
narrower requests are cheaper regardless of caching.

**Argument against.** It does not touch the measured defect. The SDK still re-requests the same
grid cell on every return to a zoom level, `resultType=tile` or not, because nothing tells the
browser it may keep the answer. This alternative optimises the query the server runs, not the
network round trip the owner watched happen for no reason.

## 3. Counterarguments to the preferred option

**A shared cache serving one user's private data to another is the worst failure mode a caching
change can have**, and query responses carry per-request attribute and geometry filters a tile
never had to — a CDN operator who trusted the `public` on a query response the way they trust it
on a tile is trusting a claim this endpoint has more ways to get wrong. §4 below is written to make
that the default a mistake cannot reach: everything defaults to `private` and only an anonymous
read of a genuinely public layer earns `public`.

**The ETag design trades exactness for not buffering**, and that trade is real, not free. For a
PostGIS layer specifically, this ADR ships *no* validator at all rather than a wrong one — see §5.

**Reusing `CacheLifetime` couples two faces an operator has never been asked whether they want
coupled**, which is Alternative A's own argument against. If the owner wants them independent, this
ADR's design is one field short of that and the migration is additive, not a rewrite.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Query responses carried no caching headers | `curl -D -` against a running PostGIS layer and a GeoParquet layer's `query`, 2026-09-13 | This ADR §1 |
| The ArcGIS JS SDK re-requests a drawn tile's `query` on returning to a zoom level | Owner's report, "gidip geliyor" | This ADR §1 |
| `VectorTileEndpoints` already ships this exact header shape for `.pbf` | `src/Graticula.Host/VectorTileEndpoints.cs`, `WriteTileAsync` | D-248 |
| A GeoParquet layer's file version is available without a query — a dictionary lookup and, at most, a filesystem stat `GeoParquetFolder` already performs | `GeoParquetFeatureSource.CacheValidatorAsync`, exercised by `CacheValidatorAsync_agrees_with_VersionOfAsync_and_changes_when_the_file_is_replaced` | `tests/Graticula.Providers.DuckDb.Tests/GeoParquetFeatureSourceTests.cs` |
| A registered PostGIS layer has no comparably cheap answer; the nearest primitive (`xmin`) is a per-row round trip, not a per-table one | `PostGisFeatureSource.VersionOfAsync` | `src/Graticula.Providers.PostGis/PostGisFeatureSource.cs` |
| `query` is streamed and buffering it once already broke time-to-first-byte | ADR-062's own measurement: 0 bytes on the wire at t=0.20s before the fix | `src/Graticula.Host/Program.cs`, `QueryAsync` remarks |

## 5. Decision

A FeatureServer `query` GET that answers with features (`shape == QueryShape.Features`, not HTML,
after every existing refusal has already had its turn to say no) is answered with `Cache-Control`
built from the layer's own `CacheLifetime` — null falling back to the server's tile default,
`HostSettings.TileCacheLifetime` — exactly as `VectorTileEndpoints` already computes it for a tile.
Zero means `no-store`; anything else is `max-age=<seconds>`, and `public` only when the caller is
anonymous *and* the layer's sharing scope is `Public` — every other case, including a signed-in
caller reading a service that happens to be public, is `private`, because a shared cache cannot
distinguish "public content read by anyone" from "content this reader happens to be allowed" from
the response alone, and the two must never be confused in the direction that leaks.

Where the resolved source implements a new, optional interface — `ISourceCacheValidator`,
mirroring the shape `IFeatureVersions` already established for a different question — its cheap,
pre-query answer becomes a **weak** `ETag`, built from the layer's id, the sorted query string,
that validator, **the server build** and **a fingerprint of the layer's configuration**, so a caller
holding a matching tag is answered `304` **before the query runs at all**. *(Revised at
integration, 2026-09-13: the first version was strong and carried only the first three.)* Weak,
because ADR-068's compression sends one representation brotli, gzip or identity under the same tag
and RFC 9110 §8.8.1 reserves a strong tag for identical bytes. The build, because a release can
change the body without changing the data — v1.0.47 began applying `maxAllowableOffset` to these
very layers, and a tag without the build would have revalidated every pre-upgrade answer. The
fingerprint, because republishing an unchanged file (an alias, a hidden field, a served reference,
a record ceiling) changes the body too; every public property of `PublishedLayer` and
`LayerDefinition` is either fingerprinted or excluded by name with a reason, and a test fails when
a new one is neither — falsified by dropping `FieldOverrides`, which failed both that test and the
republish test. `GeoParquetFeatureSource` implements it, from the same file stat `GeoParquetFolder` already
performs to decide whether to reopen a file — no DuckDB query, no connection-budget permit taken.
`PostGisFeatureSource` does not: there is no cheap table-wide version PostgreSQL maintains the way
it maintains `xmin` per row, and this design does not fall back to hashing the streamed JSON body
to manufacture one — see §3 and the evidence row above. A PostGIS-backed layer is therefore cached
by `max-age` alone; it becomes fresh again by full re-fetch once that expires, exactly as it did
before this change, and simply for a bounded window rather than never.

`GET` only. `query` answers `POST` with the same handler and the same parameters (D-139), and RFC
9111 §3 never treats `POST` as cacheable regardless of what a response claims; this design does not
call any of the header logic for a `POST` request. The three alternate shapes —
`returnCountOnly`, `returnIdsOnly`, `statistics` — are explicitly **not covered by this change**;
see §9.

## 6. Consequences

**Positive.** The defect measured in §1 is fixed for the case it was reported in: a GeoParquet
layer's repeated tile re-fetch stops entirely (a matching `If-None-Match` short-circuits before the
query runs), and a PostGIS layer's repeated re-fetch stops for `max-age` seconds at a time. One
operator-facing setting governs staleness for both faces a layer is drawn through.

**Negative.** A PostGIS-backed layer's query responses have no validator, so revalidation after
`max-age` expires is a full re-fetch, not a `304` — this is a real, stated gap rather than a
correctness compromise (§3, §5). Reusing `CacheLifetime` means an operator cannot, today, set a
different staleness tolerance for a layer's queries than for its tiles (Alternative B). And this
change covers one query face among several that draw maps from this server (§9); the others are
unchanged tonight.

**Ports created.** None — `ISourceCacheValidator` is a Tier 1 interface in `Graticula.Core`
(`Graticula.Features` namespace), matching `IFeatureVersions`'s shape and reasoning; no Tier 2
library type appears in it.

**State.** *Catalogue*: nothing new — the lifetime read is the layer's existing `cache_seconds` (the tile lifetime), and the fallback is the existing server tile lifetime. *Runtime*: nothing held by the server; the tag is recomputed per request from the layer, the query string, the build and the GeoParquet file's version, which `GeoParquetFolder` already keeps per node. What is cached lives in browsers and in any shared cache in front of the server, and the server has no view of it — which is why an edit cannot purge it and `max-age` is the whole bound on staleness for a layer without a validator.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| A-028 | Only the administrator knows which layer needs a short cache lifetime and which needs a long one | Held — carried from the tile design, unchanged by this ADR |

## 8. Dependencies

**Depends on** (upstream ADRs — if they change, this is reviewed): the tile caching design D-248
records; ADR-062 (the query face streams), which is why this design does not hash a buffered body;
ADR-066 (GeoParquet layers), whose `GeoParquetFolder`/`GeoParquetTable.Version` this reuses.

**Depended on by** (downstream ADRs — if this changes, review these): none yet.

## 9. Revisit triggers

- **If an operator asks for a query-specific lifetime independent of a layer's tile lifetime** —
  Alternative B becomes the design, and `CacheLifetime` gains a sibling rather than being reused.
- **If OGC API Features `items`, WFS `GetFeature` or MapServer `query` are found to be re-fetched
  the same way** — this design generalises directly (`QueryResponseCaching.ApplyAsync` takes a
  `PublishedLayer` and an `IFeatureSource`, not anything ArcGIS-specific), but each face has to be
  wired to it explicitly; none is today. Checked and left undone in this pass:
  - `OgcFeaturesEndpoints` (`items`) — same streaming shape, same PostGIS/GeoParquet split; not
    wired.
  - `Graticula.Api.Wfs` (`GetFeature`) — same shape; not wired.
  - `MapServerEndpoints` (`query`, ArcGIS map service rather than feature service) — not wired.
  - `returnCountOnly` / `returnIdsOnly` / `statistics` on the FeatureServer face itself — these are
    read through `AlternateShapeAsync`/`ShapedAsync`, are already single-round-trip and small, and
    were left out of this pass on time rather than on a safety argument; they are not less safe to
    cache than `Features`, only unexamined.
- **If a cheap, table-wide validator becomes available for a registered PostGIS layer** — a
  trigger-maintained version column, or a materialised "last write" the platform store already
  tracks for some other reason — `ISourceCacheValidator` is implemented for `PostGisFeatureSource`
  and the `max-age`-only gap in §6 closes.
- **If measurement shows hashing a query response's body is cheap enough not to defeat streaming**
  — Alternative to the current §5 fallback: a validator computed from the bytes rather than from
  inputs, for the PostGIS case specifically. Nothing here measures that; it is deferred rather than
  ruled out.

## 10. Dissent

None recorded — this is the implementing agent's design against the owner's stated instruction,
not a debated decision with a recorded second voice.

## 11. INFERRED — for the owner to confirm

1. **That FeatureServer `query` should share a layer's tile `CacheLifetime` rather than get its
   own setting.** The owner asked to make query responses cacheable, alongside two other changes,
   and did not specify the policy for an editable layer. Alternative A above is what this ADR
   implements; Alternative B is the fallback if the coupling turns out to be wrong.
2. **That a signed-in caller reading a public layer should still get `private`.** Nothing the owner
   said addresses this directly; it is the safer of two readings of "a response to an authenticated
   request … must be private" from the task that started this change, applied even where the
   layer's own sharing would have allowed `public`.
