# ADR-027 — Glyphs and sprites: the tile service could not draw a label

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `HIGH` for the decision · `MEDIUM` for the coverage |
| **Decided** | 2026-08-15 |
| **Depends on** | [ADR-021](ADR-021-tile-encoding.md) |
| **Supersedes** | — |
| **Superseded by** | — |

---

## 1. Context

**The VectorTileServer shipped a style with no `glyphs` key**, which means a
MapLibre or Mapbox GL client rendered no text at all. Not badly — none. A layer
with `text-field` produced a map with geometry on it and no names, and the
client logged a fetch error against a URL that did not exist.

Labels are most of what anybody wants vector tiles for. A parcel map without
parcel numbers is a picture of some polygons.

**It was invisible from the server's side**, which is the part worth recording.
Every tile test passed, the tiles were correct, the style validated, and the
conformance suite walked service document → tiling scheme → style → tile and
found nothing wrong — because nothing in that sequence asks whether the style can
be *drawn*. [architecture-completeness.md](../architecture-completeness.md) had
carried *"Glyph & sprite serving — not started"* since the vector-first decision,
and the register was right while every test was green.

**The constraint that shapes the decision is [Q-15](../open-questions.md):
air-gapped.** Nothing is fetched at runtime. Pointing the style at a public
glyph server is the normal answer and it is not available to us.

## 2. Alternatives considered

### Alternative A — point at a public glyph service

**Argument for.** One line of configuration, no bytes to ship, no font licence
to reason about, and it is what most self-hosted tile servers do.

**Argument against.** It fails Q-15 outright: an air-gapped deployment renders
no labels, which is the failure this ADR exists to remove. It also puts a third
party on the critical path of every map, and it leaks which layers are being
looked at to whoever runs it.

### Alternative B — generate the distance fields at runtime

**Argument for.** No generated binaries in the repository. Any font an
administrator drops in works immediately, including a corporate one.

**Argument against.** It puts a font rasteriser and a distance transform on the
request path to compute something that never changes, and it puts a font parser
— an attacker-facing binary parser, historically a rich source of CVEs — inside
the server process. The .NET options are also awkward: the obvious rasteriser
sits under a licence ~~Apache-2.0~~ **ELv2 — corrected 2026-08-25,
[ADR-047](ADR-047-the-outbound-licence-is-elastic-2.md)** — outbound cannot carry.
**The constraint is unchanged in substance**: an inbound licence we cannot
redistribute under is a problem whatever we license our own code as, because the
obligation travels with the artefact rather than with our choice.

### Alternative C — pre-generate the ranges and serve them as files (chosen)

**Argument for.** Air-gapped by construction: the bytes are in the image. The
request path becomes a bounded file read. The generator is a tool, the output is
data, and both are inspectable. No font parsing at runtime.

**Argument against.** 4.3 MB of generated binaries in the repository, a font
licence to track, and a fixed set of typefaces: an administrator who wants their
own font must run the tool and rebuild rather than dropping a `.ttf` in.

### Alternative D — refuse to serve glyphs and document that labels need a proxy

**Argument for.** Honest, and zero code.

**Argument against.** It makes the flagship v1 capability half a capability, and
the workaround is exactly the thing Q-15 forbids.

## 3. Counterarguments to the preferred option

**The strongest one: one font is not internationalisation.** DejaVu Sans covers
Latin, Greek, Cyrillic and enough punctuation, which serves the audience this
product is aimed at and does not serve a deployment labelling in Arabic, Hebrew,
Chinese, Japanese, Korean or Devanagari. The ranges for those exist in the block
list and DejaVu has partial coverage at best. **A CJK deployment gets boxes**,
and that is a real limit rather than a rounding error — condition 2.

**The second: substituting a font silently is a compatibility decision that can
surprise.** A style naming `Arial Unicode MS Regular` gets DejaVu, with different
metrics, so a label laid out against Arial's advances will not be positioned as
the style's author intended. The alternative — 404 — makes the client drop every
label and report a fetch failure, which reads as a broken server. The
substitution is announced in an `X-Font-Stack` header, which is the most that can
be done from here.

**The third: checked-in binaries drift from their generator.** Nobody will
notice if the tool changes and the `.pbf` files do not. Condition 3.

## 4. Evidence

| Claim | Evidence |
|---|---|
| The glyphs are what the font says they are | All **7,720** glyphs decoded and reconstructed by thresholding the field at 192, as a client does, and compared to the font's own rasterisation: **IoU 1.000, every glyph**. Zero bitmaps with the wrong length |
| The metrics are right | `A` 17×18 top=18 (cap height above baseline); `g` 15×18 top=13 (descender below); `İ` 7×21 top=21 (dot above cap height); `ğ` 15×24 top=19 |
| The client contract holds end to end | A label composed **only** from served bytes — advance, left, top, field at 192 — with no access to the font: *"İstanbul Büyükşehir — parsel 1907"*, plus Greek and Cyrillic, legible and correctly spaced. Fetched as `Arial Unicode MS Regular`, so this also exercises the substitution |
| Nothing in the URL becomes a path | 30 unit tests and 14 conformance tests, including `../secrets`, `..\secrets`, `/etc/passwd`, `C:\Windows\win.ini`, `0-255/../../secrets` and a traversal in the font stack, which lands on the fallback rather than being sanitised |
| A range off the grid is refused | `100-200`, `0-100`, `256-255`, `+0-255`, `" 0-255"`, `65536-65791` |
| The wire format is right | The conformance decoder is written from the format, not from our encoder, so an encoder that agrees only with itself still fails |

**The sub-pixel limit, measured rather than asserted.** The field is computed
from the 24-pixel rasterisation, so the edge lands on a whole pixel: against an
eight-times reference it is **0.52 px out on average, 2.06 px at worst**. A
client magnifies that when it draws text far above the design size.

**Supersampling was tried and made it worse.** Rasterising at six times and
sampling the field back down should have placed the edge within a sixth of a
pixel. It did not: FreeType grid-fits each size separately, so a 144-pixel
outline is not a 24-pixel one scaled — stems land differently — and
reconstruction against the font's own 24-pixel rasterisation fell from
**1.000 to 0.820**. The finer measurement was a precise measurement of a
different shape. It also took two attempts to find out, because the first one
aligned the sample grid through the ascender, which is rounded per size and is
not shared; through the baseline, which is, it reached 0.820 and no further.
Recorded in the tool so nobody repeats it.

## 5. Decision

**Signed-distance-field glyph ranges are generated at build time by
[tools/make-glyphs.py](../../tools/make-glyphs.py), checked in, and served as
files.** The font is **DejaVu Sans** under the Bitstream Vera licence —
permissive, redistributable, and covering Turkish, which the audience needs.
31 ranges, 7,720 glyphs, 4.3 MB.

The server serves
`/rest/services/{service}/VectorTileServer/resources/fonts/{fontstack}/{range}.pbf`,
behind the same sharing check as every other resource under a service, with
`Cache-Control: immutable` because a range never changes within a build.

**Neither the fontstack nor the range reaches the filesystem as text.** The
stack is matched against the directories that exist; the range is parsed into two
integers and the filename rebuilt from them, and only on the fixed 256-glyph
grid. Nothing is sanitised — a check that rejects beats a filter that repairs.

**An unknown font stack is substituted, not refused**, and the response says so
in `X-Font-Stack`. **A range the font does not cover is not substituted**: Latin
glyphs answering a request for the Japanese range renders mojibake, and a client
can draw a box for a missing glyph but cannot un-draw a wrong one.

**The sprite sheet is served and is empty** — `{}` and a one-pixel transparent
PNG for `sprite.json`, `sprite.png` and their `@2x` forms. There is no icon
library to ship and no way for anybody to upload one yet; what this buys is that
a client probing the sheet gets an answer instead of a 404, which is the
difference between *this service has no icons* and *this service is broken*.

**The style gains `glyphs` and `sprite`**, with `{fontstack}` and `{range}` left
as the client's placeholders. A build shipped without the glyph directory omits
both keys and still serves tiles.

## 6. Consequences

**Positive.**

- The vector tile service can draw a label, which it could not before.
- Air-gapped by construction rather than by configuration.
- No font parsing in the server process.
- The generator is checked in, so what is in the ranges is a matter of record.

**Negative.**

- **4.3 MB of generated binaries in the repository**, which git will keep
  forever and which no reviewer will read.
- **One typeface, and no CJK.** Condition 2.
- **A font substitution changes label metrics** relative to what a style's
  author designed against.
- **Text far above the design size is half a pixel blocky.** §4.
- **An administrator cannot add a font without running a Python tool and
  rebuilding**, which is a worse story than a directory drop.
- The sprite sheet exists and is empty, which is a stub with a route.

**Ports created.** None. `System.IO` and files.

**State.** *Catalogue*: none. The glyph ranges are **checked-in files** served from
disk, and the sprite sheet is a stub; nothing about them is per deployment, which is why a
deployment that needs another script regenerates the files rather than configuring anything.
*Runtime*: none — a range is read and written out.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| A-069 | One Latin/Greek/Cyrillic typeface serves the deployments this product is for | `UNVALIDATED`, and known to be false for a CJK deployment. Taken because the audience is a Turkish-speaking organisation leaving ArcGIS Enterprise (ADR-018 §1) |
| A-070 | Substituting an unavailable font is better than refusing | `UNVALIDATED` by use. Reasoned from what a client does with a 404 — drops every label and reports a fetch failure — which is a worse failure than a different typeface |

## 8. Dependencies

**Depends on**: [ADR-021](ADR-021-tile-encoding.md), which decided the tile
pipeline this style describes; [ADR-016](ADR-016-packaging-deployment-upgrade.md)
for the image the ranges ship in; Q-15 for the air-gapped constraint.

**Depended on by**: style document management, which does not exist. When it
does, a user-supplied style will reference these same resources, and the sprite
sheet stops being allowed to be empty.

## 9. Revisit triggers

- **A deployment needs a script DejaVu does not cover.** The block list and the
  generator both change, and so does the one-font assumption.
- **Somebody asks to use their own typeface.** The rebuild story is the answer
  today and it is not a good one.
- **Style document management ships.** Users will supply styles naming fonts and
  icons, and both the substitution rule and the empty sprite sheet need
  revisiting together.
- **A client reports labels drawn in the wrong place.** The substitution changes
  metrics, and this is what that would look like.

## 10. Conditions

1. **The glyphs are verified against the font rather than against our own
   encoder.** *(Discharged — 7,720 glyphs reconstructed at IoU 1.000, and a
   conformance decoder written from the wire format.)*
2. **The absence of CJK and other scripts is stated where somebody choosing this
   product would see it**, not only here. A GIS server that cannot label a map in
   the local language is not a smaller product for that deployment; it is not a
   usable one.
   *(Discharged 2026-08-27 — `README.md`'s **Status** section now names the covered
   scripts and the missing ones, beside the v1 scope statement, which is the first
   place a stranger reads what this is. **It says what is missing rather than what is
   present**, because *Latin, Greek and Cyrillic* reads as a feature list and *not
   Chinese, Japanese, Korean or Devanagari* reads as the limit it is. Found live while
   sweeping this ADR's conditions; the repository had been public for days with the
   limitation stated only here.)*
3. **The checked-in ranges are provably the output of the checked-in tool.** A
   regeneration that changes the bytes should fail something. Today nothing
   notices, and generated artefacts drifting from their generator is a matter of
   time.
4. **The font licence is in [DEPENDENCY-LICENSES.md](../../DEPENDENCY-LICENSES.md)
   with the text shipped beside the font**, because the Bitstream Vera licence
   requires the notice to travel with the file. *(Discharged — [DEPENDENCY-LICENSES.md](../../DEPENDENCY-LICENSES.md) carries the row and a section explaining why a redistributed font is unlike every other entry, and the licence text sits beside the font at [tools/fonts/LICENSE-DejaVu.txt](../../tools/fonts/LICENSE-DejaVu.txt) — which is what Bitstream Vera requires, since the notice must travel with the file rather than be referenced from elsewhere.)*

5. **The sprite sheet stops being empty, or the `sprite` key stops being
   written**, when style management arrives. A permanent empty stub is a
   temporary compromise that stopped being temporary.
   *(Style management arrived the same day —
   [ADR-028](ADR-028-style-documents.md) — and this was answered a **third
   way**: neither option was right, because there is no icon library to ship and
   clients probe the sheet whether or not we advertise it. Instead the style
   validator **refuses any layer setting `icon-image`**, so a style that would
   silently draw nothing is turned away at the moment its author can fix it. The
   harm is removed; the gap is not. **Still open**, and the check is deleted
   rather than relaxed when sprites can be uploaded.)*

## 11. Dissent

**Recorded.** Alternative B is right that checked-in binaries are a smell, and
right that an administrator should be able to add a font by putting one in a
directory. The runtime path was rejected on licensing and attack surface, not
because it is the worse design — and if a permissively licensed .NET rasteriser
that can be told not to hint appears, Alternative B becomes the better answer and
this decision should not be defended out of habit.

There is also a narrower dissent worth keeping: **substituting a font silently is
the kind of helpfulness that hides a problem.** A style asking for Arial and
getting DejaVu will render, and the person looking at the map will not know their
style was not honoured. The header is a weak mitigation, because nobody reads
response headers on a map tile.
