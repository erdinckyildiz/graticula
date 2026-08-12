# Rendering Engines

**Status:** LARGELY SUPERSEDED, 2026-08-12 — kept for the record.

The owner decided the platform is vector-first: no server-side raster tiles, the
client renders ([product-context.md](../product-context.md), "Rendering
posture"). Server-side cartography left the core, so most of this note now
describes a decision we are not making.

Two things survive and are worth reading: §2's assessment of MapLibre Native —
whose central objection has since **inverted**, see
[ADR-004](../adr/ADR-004-rendering-engine.md) — and the headless/X-server
operational caveat, which is now the main argument against the only rendering
job we have left.

The §3 list of "what is not commodity and is therefore ours" is now a list of
things that are **not ours at all**. That is the clearest measure of how much
this decision removed.

*Original status: FIRST PASS — thinner than the other notes, marked `VERIFY`
throughout.*
**Feeds:** [ADR-004](../adr/ADR-004-rendering-engine.md),
[ADR-001](../adr/ADR-001-core-language.md) criterion C3

---

## 1. Separating two things that get conflated

"Rendering engine" hides two decisions with different answers:

1. **The cartographic layer** — style evaluation, symbol construction, label
   placement, decluttering, layer compositing. **Tier 1, ours, not negotiable.**
   This is where server-side cartography is actually won or lost, and no library
   provides it in a form we could adopt without adopting its styling model too.
2. **The rasterisation backend** — turning paths, fills, strokes and glyphs into
   pixels. **Tier 2, adopted.** Commodity, numerically demanding, well solved.

Most comparisons in the wild conflate them, which is why they are less useful
than they look. [ADR-004](../adr/ADR-004-rendering-engine.md) is only about
decision 2.

## 2. The candidates

### Skia

The mainstream choice: the rasterisation engine behind Chrome and Android,
BSD-licensed, actively developed, excellent text and anti-aliasing quality.

Costs are real and practical rather than technical: it is a large C++ build, the
distribution artefact is substantial, and binding quality varies sharply by
language. For an air-gapped, single-binary-friendly product (§2), build and
distribution weight is a first-class concern, not a footnote.

### Cairo

Older, smaller, simpler, widely packaged by every Linux distribution — which
matters more than it sounds for air-gapped and distro-packaged installs, where
"already in the base repository" beats "vendored 200 MB dependency". Lower
ceiling on quality and performance than Skia. `VERIFY` licensing is dual
LGPL/MPL.

### MapLibre Native

A different kind of candidate, and the one needing the most care.

`VERIFY` MapLibre Native supports server-side rendering of MapLibre styles to
raster tiles and static images, available as `@maplibre/maplibre-gl-native`, and
the Martin tile server roadmap describes CPU-based rendering with optional GPU
acceleration.

**But it is not a rasterisation backend — it is a whole renderer with its own
styling model.** Adopting it means adopting the MapLibre style specification as
our cartographic model. That is a Tier 1 decision wearing Tier 2 clothing, and
the build-vs-adopt policy says to refuse it in that form.

That said, MapLibre GL Style Spec as *a* supported style format is a completely
different and much more attractive proposition — it is a widely implemented
open specification, and client-side consumers already speak it. The distinction:
supporting a style format is interoperability; adopting a rendering engine's
styling model as our internal one is architecture. Recorded as Q-25.

One concrete operational warning: `VERIFY` running MapLibre Native rendering "inside
Docker or on a remote headless server" needs "an X server simulation… to do any
rendering", and there is a documented issue titled "Headless rendering context
management needs improvements". For a server product that must run in
containers and air-gapped environments, a GPU-context dependency is an
operational liability measured against the 2 AM test (§7).

### Language-native rasterisers

Whatever the chosen language offers natively. Weakest on quality, strongest on
build simplicity and debuggability. Should not be dismissed before we know what
quality bar we actually need — but a serious cartographic product will likely
outgrow it.

## 3. What is not commodity, and is therefore ours

Worth naming explicitly, because it is where the real work is and it is easy to
assume a library will provide it:

- **Label placement and decluttering.** The hardest problem in server-side
  cartography, with no adoptable solution. Point, line and area label placement,
  collision detection, priority, and the fact that a tiled renderer must make
  labels agree across tile boundaries — a stateless per-tile renderer produces
  labels that collide at seams.
- **Style evaluation.** Data-driven styling, expressions, zoom-dependent rules,
  scale denominators.
- **Symbol construction.** Composite symbols, offsets, dash patterns, hatching.
- **Cross-tile consistency.** The generalisation of the label problem: any
  decision made per-tile that should be made per-map.

Text *shaping* is different and must be adopted — HarfBuzz and FreeType, usually
pulled in by the rasteriser. Complex scripts are never reimplemented.

## 4. The consequence for ADR-001

Criterion C3 is weaker than it looks. Skia and Cairo are both C/C++ and reached
via FFI from every candidate language, so — like GDAL and PROJ — this criterion
does not strongly discriminate. What differs is binding maturity and build
ergonomics, which are real but second-order.

The exception is a language with a credible native rasteriser, which would trade
quality for build simplicity. Worth noting in ADR-001, not worth weighting
heavily.

## 5. Honest assessment of this note

This is the weakest research note so far, and deliberately marked as such.
Public comparative evidence for *server-side* map rendering is thin: most
benchmarks compare UI toolkits, and most map-rendering comparisons conflate the
cartographic layer with the rasterisation layer.

Rendering is also deferrable — features first, then tiles
([product-context.md](../product-context.md)) — so ADR-004 does not need to be
decided soon. **It should not be decided from this note.** The right sequence is
to defer it until the tile pipeline exists, then measure candidates on our own
workload, which is what `benchmarks/rendering/` is registered for.

Saying so plainly here is cheaper than discovering later that a decision rested
on secondary sources.

## 6. Still to investigate

- Whether MapLibre Native's headless rendering can run without GPU context in a
  container reliably, and what that costs in throughput.
- How existing servers solve cross-tile label consistency. This is the single
  most valuable open rendering question and it is not addressed above.
- Font handling in air-gapped deployments: bundling, licensing, fallback chains
  for scripts we do not anticipate (Q-15).
- Rasterisation backend licence verification into
  [DEPENDENCY-LICENSES.md](../../DEPENDENCY-LICENSES.md).

## Sources

- [MapLibre roadmap — Style rendering (Martin tile server)](https://maplibre.org/roadmap/martin-tile-server/style-rendering/)
- [MapLibre Native — Linux platform documentation](https://maplibre.org/maplibre-native/docs/book/platforms/linux/index.html)
- [maplibre-native issue 1067 — Headless rendering context management needs improvements](https://github.com/maplibre/maplibre-native/issues/1067)
- [maplibre-native architecture overview](https://deepwiki.com/maplibre/maplibre-native)
