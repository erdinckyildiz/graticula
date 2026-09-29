# ADR-100 — Labels in more scripts

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `MEDIUM` — the glyph ranges are generated, decoded and pinned by tests, and the raster face's shaping is read back from measurements; **nothing here has yet been drawn by a live MapLibre, JavaScript SDK or ArcGIS Pro client**, and what each of them does with Arabic and Devanagari is taken from their documentation or inferred (§5.4) |
| **Decided** | 2026-09-29, by owner decision. The owner asked for label coverage beyond Latin, Greek and Cyrillic, named the fonts (Google Noto under the SIL Open Font License: Noto Sans, Arabic, Hebrew, Armenian, Georgian, Devanagari — Bengali and Tamil if cheap — Thai, and CJK), the rule (a composite stack, DejaVu first so that every existing Latin, Greek and Cyrillic glyph is byte-identical, then Noto Sans, then the script fonts; a range still not covered is still not substituted), the size gate (CJK opt-in if it adds more than about 40 MB to the image), and the honesty requirement (claim per script only what clients actually draw). Two refinements are this session's and are **INFERRED** (§5.2, §5.3) |
| **Depends on** | [ADR-027](ADR-027-glyphs-and-sprites.md), [ADR-041](ADR-041-the-map-renderer.md), [ADR-016](ADR-016-packaging-deployment-upgrade.md) |
| **Amends** | [ADR-027](ADR-027-glyphs-and-sprites.md) §5 and §6 (one typeface → a composite stack) and its condition 2 (the stated absence of scripts becomes a statement per script); [D-161](../architecture-debt.md)'s *one font, both faces* becomes *one stack, both faces* |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

**The vector tile face labelled in Latin, Greek and Cyrillic and nothing else**, because
[ADR-027](ADR-027-glyphs-and-sprites.md) generated its glyph ranges from one font, DejaVu Sans.
The README said so beside the scope statement (ADR-027 condition 2), and
[A-069](../architecture-assumptions.md) recorded the assumption that this served the product's
audience as *known to be false for a CJK deployment*. The owner decided on 2026-09-29 that it
should not stay that way.

**The raster faces had the same limit by a different route.** Since
[D-161](../architecture-debt.md) the Skia renderer embeds the same DejaVu file and asks the
machine's font manager for anything DejaVu cannot draw; on the air-gapped image this product is
built for the machine has nothing, so an Arabic or Hindi label was named in the log and drawn as
boxes. D-161's principle — a WMS map and a vector tile of the same layer labelled in the same
typeface — means the two faces have to move together.

**Reading the ranges to plan this found three defects in what ADR-027 shipped**, all older than
this decision and all in the generator:

1. **3,800 of the 7,720 "glyphs" were boxes.** The generator decided a codepoint existed by
   whether Pillow drew ink for it, and Pillow draws the font's `.notdef` box for a codepoint the
   font does not have. So `2304-2559` (Devanagari) answered 200 with 256 boxes, as did the ranges
   for Gurmukhi through Sinhala and Tibetan, and `0-255` carried boxes for the 65 C0 and C1
   controls. ADR-027 §5's rule — *a range the font does not cover is not substituted* — was
   therefore not true: those ranges were substituted with boxes, which a client draws. ADR-027's
   IoU 1.000 evidence could not see it, because it compared each glyph with the font's own
   rasterisation, and the font's own rasterisation of a missing codepoint is the same box.
2. **No range carried a space.** The same test asked for an ink mask of size `(0, 0)`; Pillow
   gives a space a mask as wide as its advance and zero pixels high, the next check threw it away,
   and U+0020, U+00A0 and every other space character were absent. A client with no glyph has no
   advance, and MapLibre's shaper skips a codepoint it has no glyph for — so, **INFERRED from the
   shaper rather than seen on a map**, a multi-word label ran its words together. ADR-027 §4's
   hand-composed *"İstanbul Büyükşehir — parsel 1907"* was composed by the author's own reader,
   not by a client.
3. **The Latin ligatures were never served.** The block list ended at U+FB4F and the generator
   named the file for the block, `64256-64335.pbf`; clients only ever ask for `start-(start+255)`,
   and `GlyphStore` refuses anything else, so the file shipped and nothing could fetch it.

All three are repaired here (§5.1), because the composite needs a correct answer to *does this
font have this codepoint* before it can take a codepoint from the first font that does.

## 2. Alternatives considered

### Alternative A — one pan-Unicode family (Noto Sans alone, or GNU Unifont)

**Argument for.** One font, one licence, one look; no rule about which font answers.

**Argument against.** No such family exists as one file: Noto is a hundred families, and Unifont
is a bitmap font that draws a map label as a 1990s terminal would. Replacing DejaVu would also
change every Latin label every existing style draws, which the owner ruled out.

### Alternative B — a composite stack under the existing name (chosen)

**Argument for.** Every stored style asks for `DejaVu Sans Regular`, and every unknown stack is
substituted to it (ADR-027 §5), so extending that one stack extends every style without an edit.
Serving stays a file read.

**Argument against.** The stack is named after one font and is several; §3.

### Alternative C — one stack per script, selected by the style

**Argument for.** Honest names (`Noto Sans Arabic Regular`), and a style chooses what it wants.

**Argument against.** Every existing style would need editing to gain anything, a label mixing
two scripts needs a style expression per script, and a style naming a font we do not have still
lands on DejaVu and still cannot draw Arabic. Not excluded for later: the composite does not
prevent adding per-family stacks beside it.

### Alternative D — CJK in the default image

**Argument for.** One image, and a Japanese deployment works on `docker compose up`.

**Argument against.** It is measured at 53 MB uncompressed on disk (§4), over the owner's
threshold, and ArcGIS Enterprise's own audience here labels in Turkish.

### Alternative E — generate the ranges at runtime from whatever fonts are mounted

Rejected by ADR-027 §2 Alternative B for reasons that have not changed: a font parser on the
request path, and a rasteriser licence the outbound licence cannot carry. Its dissent (ADR-027
§11) stands.

## 3. Counterarguments to the preferred option

**The stack's name now lies.** `DejaVu Sans Regular` answers Arabic from Noto Sans Arabic. A
style author reading the name expects DejaVu metrics everywhere. The alternative is renaming the
stack, which breaks every stored style (§2 C). The name is kept, the composite is stated in
`GlyphStore.Fallback`'s documentation, in `stack.json` and here, and `X-Font-Stack` still says
which stack answered.

**Mixed fonts in one label.** Where DejaVu lacks a Latin letter Noto Sans draws it, in a slightly
different design; a bilingual label mixes two families by construction. That is what any font
fallback does and what a browser does with the same text.

**The glyph ranges cannot shape.** A glyph range is indexed by codepoint, and a Devanagari
conjunct is a glyph with no codepoint. No client drawing from these ranges can draw
*क्ष* correctly, whatever this server does. The decision serves the codepoints and says so per
script (§5.4) rather than claiming coverage it cannot deliver.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Every Latin, Greek and Cyrillic glyph is byte-identical | The **3,563** glyph records DejaVu drew before this change outside the blocks a script font now claims — decoded from the ranges at `2c9f4ee` and filtered by DejaVu's own `cmap` — hashed in codepoint order: `9dca2463…`. The regenerated ranges give the same digest. Flipping one byte of `256-511.pbf` fails the test | `GlyphCompositeTests.The_glyphs_DejaVu_drew_before_are_byte_identical` |
| The ranges are the generator's output | `tools/glyphs-check.py` both layers: 48 ranges and 10 fonts match `provenance.json`, and regenerating reproduces every byte. **On Windows with the four pinned versions, not yet in the pinned container** — the container run is the parent's (§6) | local run 2026-09-29 |
| Default build | **48 ranges, 4,210,615 bytes** (was 31 ranges and 4.3 MB, 3,800 of whose glyphs were boxes). 7,172 glyphs: DejaVu 4,500 · Noto Sans 590 · Arabic 1,211 · Hebrew 134 · Armenian 96 · Georgian 174 · Devanagari 211 · Bengali 97 · Tamil 72 · Thai 87 | `make-glyphs.py` output |
| Size per script group | In the CJK build's ranges: CJK **35,600 KB** · DejaVu 2,553 · Arabic 846 · Noto Sans 257 · Devanagari 95 · Georgian 93 · Tamil 54 · Armenian 50 · Bengali 48 · Hebrew 46 · Thai 37 | glyph records summed by source font |
| CJK build | **207 ranges, 40,809,614 bytes** (41,258 CJK glyphs), 13 s to generate. Image delta **+36.6 MB** of ranges and **+16.4 MB** for the font the raster face draws from: **+53 MB uncompressed**. Compressed as a layer: ranges 0.60 → 6.67 MB (`tar czf`), font 13.6 MB gzipped: **about +20 MB to pull** | measured 2026-09-29 |
| Fonts added to the repository | 1.47 MB of Noto `.ttf`, embedded in `Graticula.Render.Skia.dll` (2.26 MB) | file sizes |
| The raster face shapes | lam-alef measures narrower than lam and alef apart; क्ष narrower than its three parts; a rendered probe of twelve labels read by eye: Arabic joined and right to left with its number on the left, Devanagari conjuncts formed | `LabelScriptsTests`; probe image, not committed |
| HarfBuzz adds 5.5 MB to the image | `libHarfBuzzSharp.so`: 2.8 MB linux-x64, 2.7 MB linux-arm64; ELF `NEEDED` is libc, libm, libpthread and the loader only | read from the package |
| The publish carried 513 MB of native libraries the image never loads | A portable publish copies SkiaSharp and HarfBuzz for sixteen runtime identifiers; everything outside `linux-x64` and `linux-arm64` sums to **512,927,032 bytes**, of which HarfBuzz is about 98 MB and Skia — since ADR-041 — about 415 MB | `find … -delete` run on a local publish |
| The JavaScript SDK draws Arabic right to left, joined | Esri's documentation: vector tile layers support right-to-left labels *"with contextual shaping"* since 4.2; the console map loads 4.29 | [Esri release notes 4.2](https://developers.arcgis.com/javascript/latest/4.2/index.html) — **not seen on a map** |

## 5. Decision

### 5.1 The fonts, and a generator that asks the font

**Nine Noto families are checked in beside DejaVu** in `tools/fonts/`: Noto Sans 2.015, Noto Sans
Arabic 2.013, Hebrew 3.001, Armenian 2.008, Georgian 2.005, Devanagari 2.007, Bengali 3.011, Tamil
2.004 and Thai 2.002, the hinted TTFs from `notofonts/notofonts.github.io` at commit `3ec599d8`,
each under OFL-1.1 as its own `name` table states, with `OFL.txt` beside them.
[tools/fonts/stack.json](../../tools/fonts/stack.json) lists them in order with each file's SHA-256
and source URL, and the generator refuses a file whose hash differs.

**The generator reads each font's `cmap`** (fontTools, pinned in `tools/glyphs.Dockerfile`)
instead of inferring presence from ink, walks every 256-codepoint range of the Basic Multilingual
Plane on the grid clients ask on, skips controls, surrogates, private use and noncharacters, and
keeps an inkless glyph with an advance — so the three defects of §1 are gone. `provenance.json`
records every font's hash and which font drew which codepoints.

### 5.2 The composite rule

**A codepoint is drawn by the first font in the stack that claims its block and has it, else by
the first font that has it.** The order is DejaVu Sans, Noto Sans, then the script fonts; only
the script fonts claim blocks. So DejaVu answers first for everything it answered before, except
in a script font's own blocks — Arabic (including both presentation-form blocks), Hebrew,
Armenian, Georgian, Devanagari, Bengali, Tamil, Thai — where that font answers first and DejaVu
only fills what it lacks.

**The claims are INFERRED from the owner's rule**, whose stated purpose was that Latin, Greek and
Cyrillic stay byte-identical. DejaVu has 165 of the 256 Arabic codepoints, half of Hebrew and most
of Armenian and Georgian; taken literally, DejaVu-first would build an Arabic word from two
typefaces whose joining strokes do not meet. None of those DejaVu glyphs was ever promised — the
README said *Latin, Greek and Cyrillic only*. **Listed for the owner's confirmation.**

**The stack keeps the name `DejaVu Sans Regular`**, so every stored style and every substituted
stack gains the scripts with no edit. **A range no font in the stack covers is still not written
and still answers 404** — Gurmukhi, Telugu, Kannada and Han in a default build among them — so the
ADR-027 rule against Latin-for-Japanese holds, and now holds for the ranges that used to answer
with boxes.

### 5.3 CJK is opt-in: `GLYPHS_CJK=1`

**It is over the gate.** +53 MB uncompressed, more than every other glyph together; the owner's
threshold was about 40 MB. **Which measure the threshold meant is INFERRED** — compressed, the
delta is about 20 MB and would have been under it. Uncompressed was taken because it is what
`docker images` reports and what the air-gapped bundle's disk holds once loaded; the owner may
read it the other way, and then the fix is one default.

**How an operator adds it:**
`docker build -f deploy/server.Dockerfile --build-arg GLYPHS_CJK=1 -t graticula:cjk .` — on a
machine with network, since the font is fetched during the build; the resulting image is as
air-gapped as the default one. The glyph stage regenerates the whole composite with Noto Sans CJK
appended, in the same pinned base and versions as `tools/glyphs.Dockerfile`, so every range the
default build has keeps its bytes and only the CJK font's contributions are new; it also places
the `.otf` in `/app/fonts`, where the raster face finds it. The release workflow publishes the
default image only.

**The file is Noto Sans CJK SC Regular 2.004** (`notofonts/noto-cjk`, tag `Sans2.004`, pinned by
SHA-256, not checked in). It carries every Unified Ideograph, kana and Hangul syllable the four
regional files do; what differs between them is which *form* of a unified ideograph is the
default, and this one's are Simplified Chinese. **A Japanese, Korean or Traditional Chinese label
gets the Simplified Chinese form where the regions differ — INFERRED acceptable for map labels,
listed for confirmation.**

### 5.4 What is claimed, per script

The glyph ranges are indexed by codepoint, so what a tile client draws depends on what the client
does before it asks for glyphs. MapLibre GL JS needs the `@mapbox/mapbox-gl-rtl-text` plugin
(`setRTLTextPlugin`) for Arabic and Hebrew: it reorders right-to-left text and turns Arabic into
presentation forms, which is why those blocks are generated. It does no Indic shaping. The raster
faces shape with HarfBuzz (§5.5).

| Script | Tile face — MapLibre | Tile face — Esri JS SDK, ArcGIS Pro | Raster faces (export, WMS) |
|---|---|---|---|
| Latin, Greek, Cyrillic | draws | draws | draws |
| Armenian, Georgian | draws — no shaping needed | draws | draws |
| Hebrew | draws **with the RTL plugin**; without it, left to right | JS SDK: right to left per its documentation; Pro: **INFERRED**, not seen | draws, right to left |
| Arabic | draws joined and right to left **with the RTL plugin**; without it, isolated letters left to right | JS SDK 4.2+: joined and right to left per its documentation, **not seen**; Pro: **INFERRED**, not seen | draws, shaped, right to left |
| Thai | draws; stacked vowel and tone marks are positioned by the font's own offsets and may collide | same caveat, **INFERRED** | draws, shaped |
| Devanagari, Bengali, Tamil | **drawn unshaped**: conjuncts come apart and a pre-base vowel sign lands after its consonant | **INFERRED unshaped**: a conjunct has no codepoint, so no client drawing from glyph ranges can draw one | draws, shaped |
| Chinese, Japanese, Korean | default image: refused; MapLibre draws ideographs, kana and Hangul **locally from the browser's own font** by default (`localIdeographFontFamily`), so it often works anyway. `GLYPHS_CJK=1`: draws | default image: refused, no labels in those scripts. `GLYPHS_CJK=1`: draws | default image: the machine's font, else named in the log and boxes. `GLYPHS_CJK=1`: draws |

**So the product says**: Arabic, Hebrew, Armenian, Georgian and Thai labels are served on every
face; Devanagari, Bengali and Tamil are served and are **drawn correctly by the raster faces and
unshaped by MapLibre**; CJK is an image built with `GLYPHS_CJK=1`. The console's map page uses
the JavaScript SDK 4.29, which by its documentation draws Arabic right to left — **not yet seen
there**; the web map viewer draws vector tiles without labels at all.

### 5.5 The raster faces: the same stack, shaped

`Graticula.Render.Skia` embeds the nine Noto files and `stack.json`, and splits each label into
runs of one face by the same rule; Noto Sans CJK and any other face a deployment puts in
`/app/fonts` are read from there (D-161's *mount*). Each run is shaped by HarfBuzz
(**SkiaSharp.HarfBuzz 3.119.2**, MIT) — a new Tier 2 dependency, confined to the adapter beside
Skia and behind `IMapCanvas`, so no port changes. Spaces and punctuation stay in the run they are
in, digits form their own left-to-right run, and a label whose first strong letter is Arabic or
Hebrew lays its runs out right to left. **That is not the Unicode bidirectional algorithm**: a
label that nests several changes of direction can come out in the wrong order.

### 5.6 Native libraries the image never loads are removed

Adding HarfBuzz brought 103 MB of native builds for sixteen runtime identifiers into the publish,
and measuring it showed Skia had brought 415 MB the same way since ADR-041. The serving image now
deletes every SkiaSharp and HarfBuzz native library outside `linux-x64` and `linux-arm64`, as it
already did for DuckDB, and asserts the four it keeps. **Measured on a local publish, not yet on a
built image.**

## 6. Consequences

**Positive.**

- Every stored style labels in eight more scripts without an edit, and the substitution rule
  extends to them.
- The raster faces draw Arabic, Hebrew, Indic and Thai correctly on an air-gapped image, where
  before they drew boxes; they now shape better than MapLibre does from the same data.
- Three generator defects are gone: served boxes, missing spaces, an unservable range.
- The default image should be about 400 MB smaller (§5.6), a larger effect than everything else
  here together — measured on a publish, to be confirmed on the image.

**Negative.**

- **The stack is named after one font and is ten** (§3).
- **Indic labels are wrong in MapLibre** and, as far as is known, in every client drawing from
  glyph ranges. The product says so rather than hiding it.
- **CJK needs a second build**, and the release publishes only the default image. An air-gapped
  CJK deployment builds on a connected machine and carries the image across.
- **A package exported from a CJK image carries 40 MB of glyphs**, and
  `TileExportPackage.DocumentAllowance` (8 MB) underestimates it by about 32 MB.
- **1.5 MB more fonts in the repository**, which git keeps forever, and 5.5 MB of HarfBuzz in
  the image.
- **The ranges were regenerated on Windows.** The CI container gate will say whether they
  reproduce there; if not, they are regenerated in the container and committed from there.

**Ports created.** None. HarfBuzz sits behind `IMapCanvas` with Skia;
`NativeDependencyTests` confines `HarfBuzzSharp` to `Graticula.Render.Skia`.

**State.** None. The ranges are files in the image; `stack.json` is a build input.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| A-069 | ~~One Latin/Greek/Cyrillic typeface serves the deployments this product is for~~ | **Retired by this ADR** — replaced by the per-script statement of §5.4 |
| — | Simplified Chinese glyph forms are acceptable for Japanese, Korean and Traditional Chinese map labels | **INFERRED**, §5.3 |
| — | The Esri JS SDK and ArcGIS Pro draw Arabic from these ranges joined and right to left | JS SDK from its documentation; Pro **INFERRED**; neither seen |

## 8. Dependencies

**Depends on**: [ADR-027](ADR-027-glyphs-and-sprites.md), whose serving and generator this
extends; [ADR-041](ADR-041-the-map-renderer.md) for the renderer; [ADR-016](ADR-016-packaging-deployment-upgrade.md)
for the image; Q-15 for the air gap.

**Depended on by**: [ADR-098](ADR-098-vector-tiles-can-be-exported-as-a-package.md), whose packages
carry every range.

## 9. Revisit triggers

- **A live client draws a label this ADR claims wrongly** — the first Arabic label in the console
  map, in MapLibre with the plugin, or in Pro.
- **A deployment needs CJK from the published image**, or the owner reads the size gate as
  compressed (§5.3): the default flips, and the release carries the ranges.
- **A client that shapes Indic from glyph ranges** appears, or MapLibre gains Indic shaping: §5.4
  changes.
- **A script outside the stack is asked for** (Gurmukhi, Telugu, Sinhala, Ethiopic…): one more
  Noto family and one row in `stack.json`.

## 10. Dissent

**The honest name would be a new stack.** Keeping `DejaVu Sans Regular` for a ten-font composite
buys compatibility with every stored style at the price of a name that describes a tenth of what
it serves. Recorded because the price is paid by whoever reads a style and trusts the name.

**The raster face now draws Indic better than the tile face can**, so D-161's *the same labels on
both faces* is true of the fonts and not of the result. The alternative — not shaping the raster
face — would make the two agree by making both wrong.
