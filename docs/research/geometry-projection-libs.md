# Geometry and Projection Libraries

**Status:** FIRST PASS — claims marked `VERIFY` need confirmation against
current releases.
**Feeds:** [ADR-001](../adr/ADR-001-core-language.md) criterion C1,
[ADR-003](../adr/ADR-003-geometry-engine.md),
[build-vs-adopt-policy.md](../build-vs-adopt-policy.md)

---

## 1. The family tree matters more than the feature lists

Almost every serious geometry engine in this space is the same algorithms in a
different language:

```text
JTS (Java)  ← the reference implementation
  ├── GEOS (C++ port)  ← used by PostGIS, GDAL/OGR, QGIS, Shapely, DuckDB spatial
  └── NetTopologySuite (.NET port)

geo / i_overlay (Rust)  ← independent lineage
```

This is unusually good news for us. **Choosing a language does not mean choosing
a different algorithm family** — for three of the four candidates it means
choosing a different port of the same one. Semantics, edge-case behaviour and
correctness heritage largely carry across.

It also means the port-lag question is the real one, not the feature-parity
question.

## 2. Port lag — who gets fixes first

`VERIFY` OverlayNG is the case study. It is "a complete rewrite of the overlay
module", supported by Crunchy Data, using snap-rounding to allow "full support
for specifying the output precision model independently for each overlay call"
and "fully valid precision reduction for geometries".

Overlay is the operation that matters most here: it is where naive
implementations are silently wrong, and OverlayNG exists because the previous
generation was not good enough.

`VERIFY` The reported ordering: **JTS is the reference and gets fixes first.**
NetTopologySuite tracks it as a port (with its own synchronisation bugs — one
documented case where OverlayNG did not engage even when configured). Secondary
sources suggest GEOS carries more open overlay issues than JTS.

**Do not over-read this.** GEOS is the single most battle-tested geometry
implementation in the world by deployment count — it is inside PostGIS, GDAL,
QGIS, Shapely and DuckDB spatial. "More issues reported" partly reflects
"vastly more users". The honest statement is: *fixes land in JTS first and
propagate*, not *GEOS is less correct*.

**Consequence for us.** Whichever we pick, `experiments/geometry-oracle` becomes
more valuable, not less: it is how we detect that our engine's port lag has
produced a behaviour difference on a case we care about. Q-20 (multiple GEOS
builds in one system) is the same problem seen from another angle.

## 3. Per-language reality

### Java — JTS

Native, and it is the reference implementation. No FFI, no marshalling, fixes
first. Strongest possible position on ADR-001's C1 criterion.

### .NET — NetTopologySuite

Managed port of JTS. No FFI. Debuggable in the same debugger as our own code —
which is precisely what the owner's in-house-defect-resolution requirement asks
for. `VERIFY` Port lag behind JTS is real but appears modest, and OverlayNG is
present.

### Rust — `geo` with `i_overlay`

`VERIFY` An **independent lineage**, not a JTS port. `i_overlay` is described as
the core overlay engine used by `geo`, offering union, intersection, difference
and exclusion, integer and floating-point APIs, "OGC-valid output… when strict
topology is required", and predicates with early-exit optimisation.

Independence cuts both ways. It is not inheriting JTS's twenty years of
edge-case fixes — but neither is it inheriting JTS's architecture, and the
integer-coordinate API is genuinely interesting for the tile hot path, where
coordinates are quantised anyway.

This is the candidate where our own oracle suite would earn its cost fastest,
because we would be validating against a *different* implementation family
rather than a port of the same one. That is a real correctness asset and a real
risk at the same time.

### Go — the awkward case

Confirmed in detail, and it is worse for us than the general framing suggested:

- **`go-geos`** wraps GEOS via cgo. Its own documentation is candid: GEOS is
  "extremely mature… with a rich feature set", but go-geos "uses cgo, with all
  the disadvantages that that entails, notably expensive function call overhead,
  more complex memory management and trickier cross-compilation."
- Its stated fit: "a good fit if your program is short-lived (meaning you can
  ignore memory management)". **Our program is a long-running server.** The
  documentation goes on to recommend go-geom "for long-running processes with
  less stringent geometry function requirements."
- **`go-geom`** is pure Go with "a cache-friendly coordinate layout which is
  generally faster than GEOS for many operations" — but with weaker topology
  coverage.
- **`orb`** is pure Go, good for OSM and MVT, but "only supports 2D geometries"
  with "a heavy focus on the commonly-used EPSG:4326 and EPSG:3857 projections".
  Fine for tiles; not a general geometry engine for a platform that must handle
  arbitrary CRS.

**One correction to an earlier assumption.** `VERIFY` go-geos "is
concurrency-safe, using GEOS's threadsafe `*_r` functions under the hood, with
locking to ensure safety, even when used across multiple goroutines." So Go does
not have a *thread-safety* problem here — it has a **call-overhead, memory
management and cross-compilation** problem, plus a library whose own docs steer
long-running servers away from it.

That is a narrower objection than "Go can't do geometry", and ADR-001 should
state it in this narrower, accurate form. The cgo call overhead point is
directly measurable and belongs in `benchmarks/geometry-hotpath/`, where it
becomes a number rather than an opinion.

### Python — not evaluated further

Shapely wraps GEOS competently, but Python is already implausible as the core
request-serving language ([ADR-001](../adr/ADR-001-core-language.md) §2) for
reasons unrelated to geometry.

## 4. PROJ has no competition

There is no meaningful alternative. PROJ is the projection implementation for
essentially the entire open-source geospatial world, and it is not just
mathematics — it carries geodetic transformation grids whose absence or
mishandling produces silently wrong coordinates.

Every candidate language reaches PROJ as a native dependency. **This criterion
does not discriminate between languages**, exactly like GDAL. Threading rules
are covered in [dependency-thread-safety.md](dependency-thread-safety.md) §4,
including the still-open Q-23.

The only real decision left around PROJ is operational rather than
architectural: which grids ship, and how they are updated in an air-gapped
install (Q-15).

## 5. What this means for ADR-001

Sharpening criterion C1 with actual evidence:

| Language | Geometry position | Reading |
|---|---|---|
| Java | JTS, native, reference implementation | Strongest |
| .NET | NTS, native managed port, modest lag | Very strong |
| Rust | `geo`/`i_overlay`, native, independent lineage | Strong, different risk profile |
| Go | cgo→GEOS with documented overhead, or weaker pure-Go options | Weakest, for narrow and specific reasons |

**The C1 criterion should be restated.** Not "is the geometry stack in the same
runtime" — that framing is too crude, since go-geos proves FFI can be made both
thread-safe and complete. The accurate criterion is:

> Can we reach a mature, complete geometry engine **without paying per-call FFI
> overhead on the tile hot path**, and can we debug it in the same process and
> debugger as our own code?

That is measurable rather than aesthetic, and `benchmarks/geometry-hotpath/`
measures it.

**And a caution against over-weighting it.** C1 is one criterion of twelve. The
tile hot path may not be geometry-call-bound at all — A-004 is still
`UNVALIDATED`. If the hot path turns out to be dominated by database time or
serialisation, C1's weight collapses and the language decision turns on
something else entirely. The prototype must measure this before the weighting is
fixed, or we will have chosen the weights to fit a preferred answer.

## 6. Still to investigate

- `VERIFY` current NTS-versus-JTS lag against release notes, rather than from
  one historical issue.
- `VERIFY` `geo`/`i_overlay` robustness claims independently — an independent
  lineage claiming robustness deserves the oracle treatment before it is
  trusted, and this is a genuinely good use of `experiments/geometry-oracle`.
- Measure cgo call overhead concretely if Go survives the paper round.
- Licence verification for all of these, into
  [DEPENDENCY-LICENSES.md](../../DEPENDENCY-LICENSES.md), which is still entirely
  `UNVERIFIED`.
- Prepared geometry support and spatial indexing quality per implementation —
  these matter as much as overlay for our workload and are not covered here.

## Sources

- [JTS Overlay — the Next Generation (Lin.ear th.inking)](http://lin-ear-th-inking.blogspot.com/2020/05/jts-overlay-next-generation.html)
- [NetTopologySuite — OverlayNG namespace](https://nettopologysuite.github.io/NetTopologySuite/api/NetTopologySuite.Operation.OverlayNG.html)
- [locationtech/jts issue 1000 — Summary: OverlayNG failures](https://github.com/locationtech/jts/issues/1000)
- [georust/geo](https://github.com/georust/geo)
- [iOverlay](https://github.com/iShape-Rust/iOverlay)
- [go-geos package documentation](https://pkg.go.dev/github.com/twpayne/go-geos)
- [orb package documentation](https://pkg.go.dev/github.com/paulmach/orb)
- [go-spatial/geom package documentation](https://pkg.go.dev/github.com/go-spatial/geom)
