# ADR-052 — The canonical symbology document is CIM

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-09-03 |
| **Supersedes** | [ADR-033](ADR-033-symbology.md) §1 (the canonical vocabulary only) |
| **Superseded by** | — |

---

## 1. Context

[ADR-033](ADR-033-symbology.md) decided that a layer's appearance is **one
canonical MapLibre Style Spec v8 document per layer**, that an Esri
`drawingInfo` is derived from it for the ArcGIS face, and that a `PUT` accepts
either vocabulary and converts on the way in, reporting what it could not carry.

On 2026-09-03 the project owner asked whether the product could use a structure
like Esri's CIMSymbol, and — presented with the choice between accepting CIM as a
third *inbound* vocabulary and making it the *canonical* one — **decided the
canonical model becomes CIM**. That decision is an input, not a conclusion this
document reaches (§7 of [CLAUDE.md](../../CLAUDE.md)).

The reason the question arises at all is that `drawingInfo`'s simple symbols
(`esriSFS`, `esriSLS`, `esriSMS`) are **one layer deep**. A symbol is a fill, or a
stroke, or a marker, with at most one outline. Real cartography is not: a road is
a wide casing under a narrower fill, a boundary is a dashed line over a solid one,
a hatched polygon is a fill plus a repeated marker. CIM's model is a **stack of
symbol layers** with geometric effects, which is the structure those examples
need.

## 2. Recorded disagreement

**This document's author recommended the third-vocabulary option and was
overruled**, and §2 of [CLAUDE.md](../../CLAUDE.md) says that is recorded rather
than smoothed over. The argument against making CIM canonical:

1. **Cartographic logic is Tier 1** under
   [build-vs-adopt-policy.md](../build-vs-adopt-policy.md) — written by us. A
   canonical model is the thing every other part reasons in, and adopting
   another vendor's model for it puts a vendor's data structure at the centre of
   the domain. The counter-argument, which is real: CIM is a *published
   specification*, not a library, and adopting a specification is not adopting an
   implementation. §4's tiers govern code, not formats.

2. **The real ceiling is what `MapRenderer` draws**, not what the canonical
   document can express. Today it draws solid fills, strokes and simple markers.
   A canonical model far richer than the renderer means most of what can be
   stored cannot be drawn, and the gap is invisible until somebody looks at a
   map. This one is not answered by the decision and becomes §5's condition 4.

3. **Every derived face now loses something.** Under ADR-033 the losses were on
   the way in, once, at the moment somebody chose. Under this decision the ArcGIS
   `drawingInfo` face and the MapLibre style face are both *derivations of a
   richer model*, so both can lose — and a loss on the way out is one nobody
   asked for and nobody sees.

The argument for, which the decision rests on: **a canonical model poorer than
the thing it is asked to carry loses information at the point of storage, and
that loss is permanent.** A multi-layer symbol pasted from ArcGIS Pro into a
MapLibre-canonical store is flattened on the way in and cannot be recovered. Made
canonical, CIM keeps what it was given and each face loses only in its own
answer, which is recoverable by changing the face.

## 3. Decision

### 3.1 What is stored

**One CIM renderer per layer**, in `layer.symbology`, as JSON. One of:

- `CIMSimpleRenderer`
- `CIMUniqueValueRenderer`
- `CIMClassBreaksRenderer`

**A renderer, not a `CIMFeatureLayer`.** A CIM feature layer also carries the
data source, the definition query, labelling and popups — all of which this
catalogue already owns. Storing one would give those two homes and no rule about
which wins, which is exactly the shape [D-130](../architecture-debt.md) records.

### 3.2 The subset that is read and written

Verified against the published specification at
`https://github.com/Esri/cim-spec` (`docs/v3/CIMRenderers.md`,
`docs/v3/CIMSymbols.md`, `docs/v3/CIMColor.md`, `docs/v3/Example-Symbols.md`)
on 2026-09-03. **The specification is the citation, not any product.**

| Type | Properties read |
|---|---|
| `CIMSimpleRenderer` | `symbol`, `label`, `description` |
| `CIMUniqueValueRenderer` | `fields`, `groups[].classes[]`, `defaultSymbol`, `useDefaultSymbol`, `defaultLabel` |
| `CIMUniqueValueClass` | `values[].fieldValues`, `label`, `symbol`, `visible` |
| `CIMClassBreaksRenderer` | `field`, `breaks[]`, `minimumBreak`, `defaultSymbol` |
| `CIMClassBreak` | `upperBound`, `label`, `symbol` |
| `CIMSymbolReference` | `symbol` |
| `CIMPolygonSymbol`, `CIMLineSymbol`, `CIMPointSymbol` | `symbolLayers`, `effects` |
| `CIMSolidFill` | `color`, `enable` |
| `CIMSolidStroke` | `color`, `width`, `capStyle`, `joinStyle`, `enable` |
| `CIMVectorMarker` | `size`, `rotation`, `markerGraphics[].symbol` |
| `CIMGeometricEffectDashes` | `dashTemplate` |
| `CIMRGBColor` | `values` |

**Everything else is kept and not understood.** An unrecognised symbol layer,
effect or renderer property is preserved verbatim in the stored document and
reported as *drawn as if it were not there* — never silently dropped. That is the
one rule that makes a canonical model worth having: it can hold what this
renderer cannot yet draw.

### 3.3 Colour

`CIMRGBColor.values` is `[red, green, blue, alpha]`. **Red, green and blue are
0–255 and alpha is 0–100.** The specification documents neither range — it says
only that "Alpha is the last value in the array for all colors" — so this is
taken from the specification's own worked examples, which use `[110, 110, 110,
100]` and `[0, 122, 194, 100]` for opaque colours. Converting to and from Esri's
REST `drawingInfo`, whose alpha is 0–255, therefore **rescales rather than
copies**, and getting that backwards makes every colour either opaque or almost
invisible with nothing in the document to say which is meant. Condition 1.

### 3.4 The faces

- **`GET`/`PUT`/`DELETE /admin/layers/{name}/symbology`** keeps its shape. `PUT`
  accepts **CIM, MapLibre or `drawingInfo`** and converts the last two into CIM.
- **The ArcGIS FeatureServer face** publishes a `drawingInfo` derived from the
  stored CIM.
- **The VectorTileServer style face** publishes MapLibre derived from the stored
  CIM.
- **`MapRenderer`** draws from a `SymbologyPlan` compiled from the stored CIM.
- **Losses are reported per face**, not once on the way in. `GET` answers what
  each derivation could not carry.

### 3.5 How the renderer reads it

**Through the MapLibre derivation, not through a second compiler.**
`SymbologyPlan` compiles MapLibre paint values — constants, `match`, `step`,
`interpolate` — into the expressions `MapRenderer` evaluates per feature, and it
is the most heavily tested part of this area. Giving it a second front end that
read CIM directly would be a second implementation of the same reading, which is
the defect §2 of [CLAUDE.md](../../CLAUDE.md) exists to prevent.

So the render path is **CIM → MapLibre → `SymbologyPlan`**, using the same
derivation the tile style face publishes. Three consequences, and the third is
the reason this is written down rather than left as an implementation detail:

1. A `CIMUniqueValueRenderer` becomes a `match` and a `CIMClassBreaksRenderer` a
   `step`, which is what those two things are.
2. **A multi-layer symbol draws.** A casing under a fill is two MapLibre layers,
   and `SymbologyPlan` already compiles a list of them — so the structural gain
   that motivated this decision arrives without a new renderer.
3. **The renderer's ceiling is the derivation's ceiling**, and therefore the
   losses reported by `CIM → MapLibre` are exactly the list condition 4 asks for:
   *what is stored and not drawn*. There is no third answer to that question that
   could disagree with this one.

### 3.6 What happens to documents stored under ADR-033

**Read tolerates both and write always produces CIM.** A stored document whose
root has `"version": 8` and `"layers"` is a MapLibre style and is converted on
read; anything with a `"type"` beginning `CIM` is CIM. A `PUT` rewrites it in CIM,
so a layer migrates the first time anybody edits it.

A one-shot `graticula symbology migrate` rewrites the whole store so that no
deployment carries two shapes forever. It is not run automatically: a migration
that runs itself at startup is a migration nobody can decline.

## 4. Consequences

**State.** The `layer.symbology` column changes what it holds — CIM JSON rather
than a MapLibre style — and holds nothing else new. No new table, no runtime
cache, nothing node-local. The document is read on every render through
`SymbologyPlan.Compile`, which is unchanged in shape.

- `SymbologyConversion` grows a CIM reader and two derivations, and loses nothing
  it had: the MapLibre and `drawingInfo` readers stay, because both remain
  accepted vocabularies.
- `GeneratedSymbology` emits CIM for an unstyled layer.
- The GUI editor of [ADR-051](ADR-051-an-appearance-is-chosen-by-looking-at-it.md)
  is unaffected in shape: it builds a `drawingInfo` and the server converts, which
  is now a conversion into CIM rather than into MapLibre. Its preview endpoint,
  its refusals and its single-source-of-truth rule are unchanged.
- **The v1 scope document is not widened by this.** ADR-052 changes the
  vocabulary of a thing already in scope; it does not add cartographic features.
  What `MapRenderer` draws is unchanged on the day this lands.

**What the move costs, found while implementing rather than while deciding.**
Both are reported to whoever stores a document, and neither is recoverable by
changing a face — they are gone from the canonical model:

- **A style layer's zoom range.** MapLibre puts `minzoom` and `maxzoom` on a
  layer and `SymbologyPlan` honours them; CIM puts a scale range on a
  *`CIMFeatureLayer`*, and what is stored here is a renderer (§3.1). So a style
  that appeared only between two zooms is stored without that and drawn at every
  scale. This is a real capability the reversal costs and it is written down
  because it was not foreseen in §2.
- **A style layer's `layout`.** `line-cap`, `line-join` and the rest have CIM
  spellings on `CIMSolidStroke`; this server does not map them across, so they are
  not carried. That one is a gap in the implementation rather than in the model,
  and it can be closed without another decision.

**And one defect the move surfaced rather than caused.** `SymbologyPlan` folded a
static opacity into its colour by evaluating the colour expression with no
feature — which for a literal is the colour and for a `match` is the *fallback*.
A layer classified by a column and given `fill-opacity` therefore drew every
feature in the fallback colour, with a one-row legend that agreed. It was
reachable before this ADR and nothing reached it, because stored styles rarely
carried an opacity; the derivation here writes one every time, and a two-class
fixture turned grey within a minute of the first migration. Fixed by folding only
what does not vary.

## 5. Conditions

1. **The alpha range is verified against a CIM document that ArcGIS Pro wrote**,
   not only against the specification's examples, before the reader is relied on
   for round trips. A test asserts that an opaque colour survives
   `drawingInfo → CIM → drawingInfo` as `255`, and a half-transparent one as
   `128 ± 1`.
   **PARTLY DISCHARGED 2026-09-03** — the round trip is asserted for all 256
   values (`CimEsriTests`), and the range comes from the specification's own
   worked examples. **The half that is still open is the one the condition
   names**: no document written by ArcGIS Pro has been read. Until one is, the
   evidence is a published example rather than a product's behaviour, and the two
   have been different before.
2. **Every property in §3.2's table is checked against the published schema by a
   test that reads the schema**, so that a rename in a later CIM version fails
   here rather than in a map. Where that is not practical, the table is re-read
   by hand whenever a type is added to it, and the ADR records the date.
3. **A document stored under ADR-033 still serves after this lands**, asserted by
   a test that writes a MapLibre style directly into the column and then asks for
   a rendered tile, a `drawingInfo` and a style.
   *(Discharged 2026-09-03 —
   `CimTests.A_document_stored_before_the_reversal_still_answers_every_face` asks
   all three, and the whole conformance suite ran 428/428 against a store holding
   MapLibre documents before the migration and again after it.)*
4. **The gap between what CIM can express and what `MapRenderer` draws is
   reported, not hidden.** `GET` says which parts of a stored document are not
   drawn. Without this the model's whole advantage — that it keeps what it was
   given — becomes a way to store an appearance nobody gets.
   *(Discharged 2026-09-03 — `Cim.Project` collects a sentence per unread symbol
   layer, effect, colour model and invisible class, every face carries them, and
   `CimTests.What_the_renderer_cannot_draw_is_reported_rather_than_dropped` fails
   when one is dropped.)*
5. **A round trip through the richest supported shape is asserted**: a two-layer
   `CIMLineSymbol` with a dash effect survives store, read, and re-store
   byte-comparably.
   *(Discharged 2026-09-03 —
   `CimTests.A_style_derived_from_a_stack_reads_back_as_the_same_stack` asserts
   the widths, the colours and the order after a full trip out to MapLibre and
   back. Byte-comparison was not used: the two documents differ in properties
   neither side reads, and asserting on the parts that mean something is the
   stronger test rather than the weaker one. Measured end to end as well — a
   two-stroke road symbol PUT through the API drew 5,161 casing pixels under
   2,287 road pixels, which under ADR-033 could not have been stored at all.)*
