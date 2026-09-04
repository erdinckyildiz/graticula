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
| `CIMUniqueValueRenderer` | `fields`, `valueExpressionInfo`, `groups[].classes[]`, `defaultSymbol`, `useDefaultSymbol`, `defaultLabel` |
| `CIMUniqueValueClass` | `values[].fieldValues`, `label`, `symbol`, `visible` |
| `CIMClassBreaksRenderer` | `field`, `valueExpressionInfo`, `breaks[]`, `minimumBreak`, `classBreakType`, `defaultSymbol` |
| `CIMClassBreak` | `upperBound`, `label`, `symbol` |
| `CIMProportionalRenderer` | `field`, `valueExpressionInfo`, `minSymbol`, `minDataValue`, `maxDataValue`, `flanneryCompensation`, `heading` — §3.10 |
| `CIMSymbolReference` | `symbol` |
| `CIMPolygonSymbol`, `CIMLineSymbol`, `CIMPointSymbol` | `symbolLayers`, `effects` |
| `CIMSolidFill` | `color`, `enable` |
| `CIMSolidStroke` | `color`, `width`, `capStyle`, `joinStyle`, `enable` |
| `CIMVectorMarker` | `size`, `rotation`, `markerGraphics[].symbol` |
| `CIMGeometricEffectDashes` | `dashTemplate` |
| `CIMRGBColor` | `values` |

**Three of those five were added on 2026-09-04** and the table said two of them
were read before they were: `minimumBreak` was listed from the day this ADR was
written and was not read until [D-205](../architecture-debt.md); `classBreakType`
and `valueExpressionInfo` were neither listed nor read, and are now both
([D-206](../architecture-debt.md), [D-207](../architecture-debt.md)). A read-subset
table nothing compares against the reader is a claim, and this one was wrong in
both directions at once.

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

### 3.6 Visual variables are the second axis, and they are stored

**Owner decision 2026-09-03, after the research note.** Esri's model has three axes, not one:
a *renderer* decides which feature gets which symbol; a *visual variable* slides one property
of that symbol continuously with a number; a *symbol* is a stack of layers. Map Viewer's
twenty-three named styles are cells of that product — `Counts and Amounts (color)` is a simple
renderer plus a colour variable, `Predominant category` is a unique-value renderer plus an
opacity one. Storing only the first axis means those styles have nowhere to live.

**Three of the four are read**, by the names the specification gives them:

| CIM | Reads | Becomes |
|---|---|---|
| `CIMColorVisualVariable` | `expression`, `minValue`, `maxValue`, `colorRamp` | `interpolate` on the colour property |
| `CIMSizeVisualVariable` | `expression`, `minSize`/`maxSize` or `dataValues`/`sizeValues` | `interpolate` on `line-width` or `circle-radius` |
| `CIMTransparencyVisualVariable` | `field`, `dataValues`/`transparencyValues` | `interpolate` on the opacity property |
| `CIMRotationVisualVariable` | — | refused in a sentence: this renderer does not rotate a symbol |

**Colour ramps**: `CIMLinearContinuousColorRamp`, `CIMPolarContinuousColorRamp` and
`CIMFixedColorRamp` are read. A `CIMMultipartColorRamp` is flattened end to end with its parts
spaced evenly, and the weights it loses are reported — a ramp built to emphasise one end
changes colour at slightly different values than it was authored to.

**Nothing new was built to draw them.** `SymbologyPlan` has compiled `interpolate` since
[ADR-041](ADR-041-the-map-renderer.md) and `Interpolate.Evaluate` takes its input from the
*feature's* context, not from the zoom — so a continuous colour over a column was already a
style this renderer executed. It had simply never been reachable, because no vocabulary the
server accepted could express it. That was measured by reading the evaluator, not assumed.

**Where a variable and the classes want the same property, the variable wins**, which is Esri's
own precedence and the only reading that makes sense: somebody who asked for colour to follow a
number asked for it to stop following the class. It is reported rather than done quietly,
because a legend whose colours are not the map's is confusing exactly because both look
deliberate.

**A size variable on a fill changes nothing and says so.** A fill has no size, and the nearest
plausible guess — widening its outline instead — would be a renderer inventing an intent.

**One loss stopped being a loss.** Before this, a MapLibre style that faded a colour with
population was flattened to the colour at its lowest stop and the variation was reported gone
(§3.7's conversion). The canonical model has somewhere to keep it now, so `CimStyle.FromMapLibre`
turns it into a variable instead. An `interpolate` over the *zoom* is still not stored: that is
a scale rule rather than a statement about the data, and filing it as a visual variable would
claim the map says something about a field it never mentions.

### 3.7 The editor authors CIM, and edits the stack

**Owner decision 2026-09-03**, taking the shape from the reference they named: ArcGIS Map
Viewer's Symbol Styler, where a symbol with several layers is edited *layer by layer* — each
with its own fill and outline — rather than as one colour and one width.

Three things follow, and the first is a defect the change removes:

1. **The form reads the stored CIM, not the derived `drawingInfo`.** It read the derived one
   before, which is flattened to a single symbol for the Esri face — so opening a two-layer
   symbol and pressing Store would have thrown the second layer away. Every edit. Silently.

2. **The form's model is the parsed document, whole.** The controls mutate the object they were
   handed and write it back; they do not rebuild a renderer from the inputs. That is what makes
   §3.2's *everything else is kept and not understood* true through an edit rather than only
   through a read: a hatch fill this console cannot draw is listed as *kept, not editable here*
   and survives being edited around.

3. **`GET .../symbology` answers a generated appearance as CIM**, where it answered `null`
   before. The editor always reads one vocabulary. It is built by running the generated
   `drawingInfo` through the same conversion a paste takes, so there is one implementation of
   *what the generated appearance is*.

**This amends [ADR-051](ADR-051-an-appearance-is-chosen-by-looking-at-it.md) §3.3.** That said
editing the document box by hand left the controls alone, because a MapLibre expression had no
checkbox. With CIM canonical and a model that keeps what it does not understand, the box is
parsed and adopted instead — so the controls and the text can no longer describe different
things. Store still sends the box.

**What is not taken from the reference**, and is named rather than approximated: `Join` /
`Join and merge` / `No join` drawing order, `Alignment` between display and map, and
`Lock color`. This renderer has no equivalent of any of them, and a control that looks like
Esri's and does something else is worse than no control.

### 3.8 A shipped library of symbols, and the line it stops at

**Owner decision 2026-09-03.** Esri's Symbol Styler opens on *Current symbol* and a set to pick
from, because a complex symbol is not something people build twice by hand. The console ships
sets of its own: sixteen symbols across *Lines*, *Areas* and *Points*, only the ones whose
geometry is being edited, each a **stack** — the road with a casing under it, the dashed
boundary over a solid halo, the polygon whose edge is heavier than its fill.

**Every one is drawn from `CIMSolidFill`, `CIMSolidStroke` and `CIMVectorMarker`**, which is
exactly what `MapRenderer` paints. Choosing one replaces the selected class's symbol rather than
merging with it: a preset chosen for its colours arriving in somebody else's is a gallery nobody
can predict, and recolouring is one click in the row below.

**The gallery swatch is drawn by the console and the preview is not.** Sixteen server previews
is sixteen requests before anybody has chosen anything. The swatch is an icon; the picture
beside the form remains the renderer's, and that is the one that decides.

**What this library cannot contain, and why it is a decision rather than a gap.** Esri's
*Classic Symbols* are shapes — square, triangle, cross, star — and a picture marker is an image.
Measured 2026-09-03 rather than assumed:

- `MapSymbol.Marker` is `(Colour, Radius, OutlineColour, OutlineWidth)` and the canvas draws it
  with `DrawCircle`. There is no shape.
- MapLibre, which §3.5 makes the renderer's only route to a stored document, has **no shape on
  `circle`** either. The one way to draw a square is `symbol` with `icon-image`, which needs a
  sprite sheet.
- [ADR-027](ADR-027-glyphs-and-sprites.md) condition 5 is still open and says the refusal of
  `icon-image` is *deleted rather than relaxed when sprites can be uploaded*.

So marker shapes are not a small addition to this library: they need either a sprite store —
upload, licensing, a size ceiling, a served sheet — or a second route from CIM to the renderer
that bypasses §3.5's single derivation. **Both are decisions, and neither is taken here.** The
library ships what can be drawn today and says what it is missing, which is the honest half of a
gallery.

### 3.9 What happens to documents stored under ADR-033

**Read tolerates both and write always produces CIM.** A stored document whose
root has `"version": 8` and `"layers"` is a MapLibre style and is converted on
read; anything with a `"type"` beginning `CIM` is CIM. A `PUT` rewrites it in CIM,
so a layer migrates the first time anybody edits it.

A one-shot `graticula symbology migrate` rewrites the whole store so that no
deployment carries two shapes forever. It is not run automatically: a migration
that runs itself at startup is a migration nobody can decline.

### 3.10 The proportional renderer, added 2026-09-04

**Owner decision 2026-09-04**, after reading the JavaScript SDK's
[`Renderer`](https://developers.arcgis.com/javascript/latest/references/core/renderers/Renderer/)
reference against the specification's own
[`CIMRenderers.md`](https://github.com/Esri/cim-spec/blob/main/docs/v3/CIMRenderers.md).
CIM defines **nine** renderers and the SDK exposes **seven** in 2D; seven of the
nine correspond one for one, and the two CIM has alone are
`CIMProportionalRenderer` and `CIMRepresentationRenderer`.

**The first of those two is missing from the SDK because it is not a separate
drawing.** The SDK draws the same map with a `SimpleRenderer` and a size visual
variable, and the specification says so itself, in the note on
`CIMSizeVisualVariable`: *VariableType = Proportional, unit NOT defined use
Expression, MinSize, MinValue, could use MaxSize.* So this server reads it by
projecting it onto what §3.6 already draws — `Cim.Project` returns a projection
whose `Kind` is `CIMSimpleRenderer`, one class holding `minSymbol`, and one
synthesised size variable. **Neither face changed.** The stored document is
untouched and still says `CIMProportionalRenderer`; §3.1 is unaffected.

**The high end is computed, and that is the part being decided.** The renderer
carries `minSymbol`, `minDataValue` and `maxDataValue` and **no maximum symbol** —
the size above the minimum comes from the proportional rule, and the rule is the
one thing the specification does not state. This server uses **area
proportionality**: a symbol's radius goes as the square root of its value, and
`flanneryCompensation: true` replaces the exponent with **0.5716**, after
J. J. Flannery, *The relative effectiveness of some common graduated point symbols
in the presentation of quantitative data*, Cartographica 8(2), 1971. That is
published cartography and no part of it is read out of an implementation (§5 of
`CLAUDE.md`). **A consequence worth stating plainly: a proportional symbol drawn
here will not match ArcGIS Pro to the pixel**, and cannot be made to without
knowing a rule nobody has published.

**Twelve stops, spaced geometrically, and both halves were measured.** The faces
carry straight segments between stops, so approximating a curve costs accuracy.
Measured against the true curve over three decades of data with a 4pt minimum
symbol:

| Stops | Spaced evenly by value | Spaced geometrically |
|---|---|---|
| 4 | 55.6% | 14.6% |
| 8 | 46.7% | 2.97% |
| 12 | 41.5% | **1.22%** |

Even spacing does not converge, because a power curve's relative error is worst
where the values are smallest and even spacing puts almost no stops there. Over a
narrower range — a ratio of twenty rather than a thousand — twelve geometric stops
are wrong by **0.23%**, which on a 4pt dot is a tenth of a point and smaller than
what antialiasing does to the same edge.

**Four things it refuses, each reported and none guessed at.**

- `unitSymbolization` — the symbol's size *is* the value in ground units, so its
  size on screen changes with the scale. This server sizes markers in points; a
  fixed size would be right at one scale and silently wrong at every other. The
  minimum symbol is drawn and does not grow.
- `backgroundSymbol` — could be prepended to the symbol's stack, which is
  bottom-first and would take it, but the size variable moves *every* width in the
  stack and the background would swell with the data. A missing background is
  visible; a background that pulses is not obviously wrong.
- A range reaching zero or below — a proportional size is a ratio to the smallest
  value, so a minimum of zero divides by it.
- `useDefaultSymbol` — there is one class here and nothing for a fallback to sit
  beside.

**`CIMRepresentationRenderer` stays refused**, and so do the four the SDK also
lists: `CIMChartRenderer`, `CIMDictionaryRenderer`, `CIMDotDensityRenderer` and
`CIMHeatMapRenderer`. None of them reduces to something this server already draws,
so each is work rather than a reading. **Corrected 2026-09-04, the same day this
section was written:** the first version of this paragraph said each *needs a
drawing primitive this server does not have*, and that is false for three of the
five. `IMapCanvas` carries `DrawImage`, implemented — so a heat map is a density
buffer composited, not a missing primitive; a dot density is `DrawMarker` over
sampled points; a pie is a tessellated arc through `FillArea`. **Two are genuinely
blocked, and for the same reason: they need data this server does not hold** — a
dictionary style, and a geodatabase's representation classes. Overstating the
obstacle in a shipped refusal message would have told an operator their document
was impossible when it was merely unwritten. The refusal messages on both write
paths name all five, so a paste is diagnosed rather than merely rejected.

### 3.11 A percentile statistic, so a classifier has one to stand on — 2026-09-04

**CIM names seven classification methods** in `ClassificationMethod`, and
`CIMClassBreaksProperties.classificationMethod` records which one produced a
document's bounds. Five of the seven need nothing this server did not already
serve:

| Method | Needs | Already served |
|---|---|---|
| `Manual` | nothing | — |
| `EqualInterval` | min, max, class count | `outStatistics` |
| `DefinedInterval` | min, max, the interval | `outStatistics` |
| `GeometricalInterval` | min, max, class count | `outStatistics` |
| `StandardDeviation` | mean, standard deviation | `outStatistics` |
| `Quantile` | **percentiles** | **no** |
| `NaturalBreaks` | **the values themselves** | **no** |

**`Quantile` is the cheap half of what was missing.** `statisticType` accepted
`count`, `sum`, `min`, `max`, `avg`, `stddev` and `var`, and refused percentiles
with a message saying they *need an ordered-set aggregate and are not
implemented* — a true sentence describing a PostgreSQL feature this server had
always been able to call. It now serves `PERCENTILE_CONT` and `PERCENTILE_DISC`,
with the fraction in `statisticParameters.value` and an optional `orderBy`,
exactly as ArcGIS spells them, through
`percentile_cont(f) within group (order by "col")`.

**The fraction is required rather than defaulted to the median**, and both ends
are checked: a value outside 0–1 is refused, and an `orderBy` that is neither
`ASC` nor `DESC` is refused rather than silently read as ascending. A percentile
with no fraction is a request nobody meant to make.

Measured on the fixture (`reading` = 10…15): `PERCENTILE_CONT` at 0.5 returned
**12.5**, `PERCENTILE_DISC` at 0.5 returned **12**, and 0.25 returned **11.25** —
the continuous form interpolating and the discrete form naming a value the column
holds, which is the whole difference between them.

**Two other places carried the old claim** and both were wrong for as long as it
took to find them: `advancedQueryCapabilities.supportsPercentileStatistics` was
published as `false`, and [ADR-008](ADR-008-query-engine.md) §4 listed percentiles
among what is refused. The capability check caught the first — it probes every
advertised flag against the running server — and reported an over-claim, because
its probe had been written without `statisticParameters` for a capability that
did not exist. The probe now carries the fraction.

**`NaturalBreaks` is still owed** and is deliberately last: Jenks needs the values
themselves, so the decision is not the algorithm but whether to read a whole
column or sample it, and at what size. That is an argument to have on its own.

### 3.12 The classifier — 2026-09-04

**Reading a classified renderer and authoring one are different problems, and only
the first was solved.** The editor offered a unique-value renderer with one class
whose value was `""`, and a class-breaks renderer with one bound of `0` and an
*Add a class* button that added the previous bound plus one. Numbers with no
relationship to the data at all, so styling a field meant already knowing its
values — which is the thing a graphical editor exists to spare somebody.

`Classification` is the arithmetic. **Pure and Tier 1**: it takes numbers and
returns numbers, so a column of one value, a range reaching zero, more classes
than distinct values and ties across a quantile boundary are all testable without
a server. `Classification.Fractions` says which quantiles a method needs and the
caller does the asking, so the five methods that need none never drag a database
connection.

**Every method is published cartography** (§5 of `CLAUDE.md`). Equal interval,
quantile, standard deviation and geometric progression are arithmetic. Natural
breaks is Fisher's exact dynamic programme — W. D. Fisher, *On grouping for
maximum homogeneity*, JASA 53 (1958), applied to cartography by Jenks in 1967 —
and not a hill climb, so the same data gives the same answer rather than one that
depends on where the search started.

**The sampling decision, taken rather than deferred.** Fisher is O(n²k), so
nobody runs it over a million rows and every implementation samples. The usual
sample is random rows, which makes the classification different on every run.
This samples **the distribution rather than the rows**: 254 evenly spaced
quantiles, one SQL statement, and Fisher over those. Each quantile stands for the
same number of rows, which is exactly what Fisher weighs, so the approximation is
principled; and it is deterministic, which random sampling is not.

**Measured, because otherwise it is a hope.** Over a 600-row column with a skewed
distribution, Fisher over all 600 values and Fisher over the 254-quantile sample
of the same values agree on all five bounds to within 2% of the range. The test
computes both and fails if they diverge.

**What is refused, and each refusal names an alternative.** A geometrical
interval from a minimum of zero is refused rather than shifted — ArcGIS shifts the
data, and a bound that is not a number in the column is a legend that lies about
the column, so this says *equal interval or quantile will classify this field as
it stands*. A standard-deviation classification of a column with no spread is
refused. A defined interval that would make hundreds of classes is refused with
the arithmetic in the message. `Manual` is refused because it means the bounds
came from whoever wrote them.

**Two methods derive their own class count** — defined interval and standard
deviation — so the requested count does not apply to them and is not checked
against them. Both check the count they arrive at against the same ceiling of 32.

### 3.13 `generateRenderer`, and why it is not in the console — 2026-09-04

**The classifier needs somewhere to live and ArcGIS already named it.**
`generateRenderer` is a POST on a layer taking a `classificationDef` — either a
`classBreaksDef` (`classificationField`, `classificationMethod`, `breakCount`,
`classificationIntervalSize`) or a `uniqueValueDef` (`uniqueValueFields`,
`baseSymbol`, `colorRamp`) — and returning a finished renderer. Every ArcGIS
client already calls it. This server answered 404.

**Behind the operation rather than inside the console**, so one implementation
serves the console and every client. The alternative — the arithmetic in
JavaScript — would have served the console alone and left the operation
unimplemented for everybody else, which is the same shape as a feature that
exists only in the vendor's own tool.

**One round trip.** A classification wants the minimum, the maximum, sometimes
the mean and standard deviation, and for two methods a list of quantiles; all of
them are aggregates over the same rows, so they go in one `outStatistics` query.
The connection lease is taken before the provider is unwrapped, because three
aggregates and a sort over a whole column is not a statement to issue outside
ADR-007 §4.8's cap.

**Both verbs and three places to put the definition.** ArcGIS documents a form
POST, scripts reach for the query string, and anything modern sends a JSON body.
The operation reads and changes nothing, so refusing the GET would be ceremony.

**What it refuses, and why each refusal is not laziness.**

- `normalizationType` — refused rather than ignored. A client asking to normalise
  by area and receiving unnormalised classes gets a plausible choropleth of the
  wrong quantity with nothing anywhere to say so.
- More than one `uniqueValueFields` — this server classifies by one (§3.2), so
  combining several would produce classes it could not read back from its own
  document.
- More than **64** distinct values — refused rather than truncated. Truncating
  produces a map that looks right and is missing most of its data, and a field
  with that many values is usually an identifier rather than a category.
- A field the layer does not have, by name.

**The default ramp is a single-hue sequential one, and that is a cartographic
choice.** A classification is ordered, so its colours must be orderable by eye; a
rainbow is not. Pale to deep in one hue reads as *less* to *more* without a
legend. The outline is deliberately not recoloured — that is what stops
neighbouring classes bleeding into each other.

Measured against the fixture: all six computed methods answer, the
standard-deviation one derives **four** classes where three were asked for
(because the count is the data's answer there), and a `uniqueValueDef` over a
text field returns one class per value in a monotone ramp. Falsified: with the
ramp interpolation removed and the field check removed, exactly the two tests
that assert them fail and the other eight pass.

### 3.14 The heat map — 2026-09-04

**The fifth renderer, and the first that is not a symbol.** Every other one here
answers *which symbol does this feature get*. A heat map answers *how crowded is
this place*, so a pixel's colour depends on every point near it and there is
nothing to resolve per feature. It accumulates while the features go past and is
composited once.

**It needed no new drawing primitive**, which is the claim §3.10 was corrected
for on the same day: `IMapCanvas.DrawImage` was declared and implemented the
whole time. What it needed was an accumulator and a ramp.

**The kernel is Epanechnikov's** — `1 - t²`, zero beyond the radius — after
V. A. Epanechnikov, *Non-parametric estimation of a multivariate probability
density*, Theory of Probability and its Applications 14 (1969). Published
statistics, and the reason the surface is smooth rather than a pile of discs.

**Three decisions worth reading:**

- **A point outside the image still lights the pixels inside it.** Dropping a
  feature because its centre is off the edge puts a visible seam down every tile
  boundary; the plan's margin is widened by the radius so the reader fetches
  beyond what it draws.
- **The alpha rises with the density over the bottom fifth of the range.** A ramp
  painted at full opacity from zero tints every empty pixel and turns a map of
  three hot spots into a coloured rectangle.
- **`maxPixelIntensity` is what makes two tiles comparable**, and its absence is
  reported as a loss rather than passed over: without it every image is scaled
  against its own densest pixel, so a quiet corner looks as hot as a busy one.

**The faces carry it natively, and neither needed inventing.** MapLibre has a
`heatmap` layer whose `heatmap-color` interpolates over `heatmap-density` — the
one paint expression in this whole style vocabulary that cannot be evaluated per
feature, because its input does not exist until every feature has been read, so
it is written and read as stops. ArcGIS has a `heatmap` renderer, and this server
publishes `radius` and `maxDensity` rather than `blurRadius` and
`maxPixelIntensity`: the web map specification marks the second pair deprecated
as belonging to the older Gaussian-blur heat map, and what is computed here is a
kernel density.

**A falsification that passed, and what it cost.** Breaking the kernel to a flat
disc left all eight tests green — the density tests asserted that a crowded corner
is hotter than a lonely one and that the middle is empty, and all of that stays
true for a pile of hard-edged circles, which is exactly what a heat map looks
like when it is wrong. A ninth test now asserts the one thing that tells them
apart: with one point, the middle of its circle is hotter than the rim. **The
first attempt at the second falsification was also worthless** — it clamped a
negative coordinate that was already being clamped — and redoing it properly
failed the seam test as it should.

### 3.15 Dot density, and the one place the plan leaves the faces — 2026-09-04

**The sixth renderer.** `value / dotValue` dots inside each polygon, one colour
per field, and the reader judges the mixture. The drawing is a marker per dot and
needed nothing new. Two other things did.

**The scatter is computed in world coordinates and clipped afterwards.** A
district straddling two tiles scatters over its whole area and each tile draws
what lands in it. Scattering into the visible part instead gives each half the
district's entire count, and the density doubles along every seam — on a map made
of marks somebody can count. Measured: one square drawn as two half-tiles gives
14 and 18 dots of 30, so two near the boundary are drawn in both, which is
correct and necessary — a dot whose centre is a pixel outside a tile still has
most of its circle inside it.

**And it is deterministic**, which is what `randomSeed` is on the CIM renderer
for: a scatter reseeded per request moves every dot when the reader pans. The
generator is **SplitMix64**, written out rather than taken from the framework —
`System.Random` promises no stable sequence across .NET versions, and a dot map
whose dots move when the runtime is upgraded cannot be compared with last year's.
The seed mixes the document's own with the feature's identity **and the field's
index**: without the feature the map looks tiled, and without the index every
field scatters into the same places and the last drawn hides the rest, which
reads as a map of one variable.

**Point-in-polygon is even-odd, because that is the rule the canvas fills by.**
`IMapCanvas.FillArea` says so in as many words. A dot placed by a different rule
lands in a hole the fill leaves empty — a mark on the map that the map says is
not there.

**THE ONE PLACE THE PLAN IS BUILT FROM CIM RATHER THAN FROM A FACE.**
`SymbologyPlan` compiles from the derived MapLibre style on purpose: a CIM front
end would be a second reading of the same document and the two would drift, and
it means what is drawn and what is advertised come from one function. **MapLibre
has no dot-density layer type at all**, so there is nothing for a second reading
to drift from, and the alternative is emitting an invented layer type into a
style real clients read and validate. So this renderer, and only this one, builds
its plan from the canonical document.

**Which makes the tile face genuinely lossy, and it says so.** It publishes a
flat fill in the first counted field's colour and a sentence: the raster faces
draw the dots and the tile face cannot, so the same layer looks different on the
two. That is the first time a face has had to admit it cannot carry a renderer at
all, and it is exactly the situation ADR-052's argument was for — the canonical
document holds what the faces cannot.

**Two more falsifications that passed, and both were about coverage rather than
code.** Reseeding the renderer's scatter from the clock failed nothing: the
determinism tests called `DotScatter` directly, so the seed the *renderer* builds
was covered by nothing — and the clock-based version was undetectable anyway
because both calls fall in the same millisecond. A renderer-level test now draws
the same feature twice and compares the pixels, and a seed that provably changes
fails it. **And the seam test's first form asserted the wrong thing** — that the
halves sum exactly to the whole — which is false and should be: the seam band is
drawn in both tiles on purpose.

### 3.16 The chart renderer, and the list closes at seven of nine — 2026-09-04

**The last of the three that were called blocked and were not.** A wedge
tessellated into a ring is a polygon and `FillArea` fills polygons, so this needed
no new primitive either. What it needed was the arithmetic that turns several
numbers into angles, and three decisions about how a pie is read.

**It starts at twelve o'clock and runs clockwise**, which is what every pie chart
anybody has read does. Screen y grows downward, so clockwise on screen is the
direction a mathematician calls negative — getting it backwards mirrors the chart,
and a mirrored pie looks like a perfectly plausible chart of a different
arrangement rather than like a bug.

**The segment count comes from the radius.** A pie twelve pixels across needs a
handful of segments to look round and one six hundred across needs dozens; a
fixed count wastes work on the small ones or shows corners on the large. The rule
keeps every chord within a twentieth of a pixel of its arc.

**And the test for that measures sagitta rather than area, which is the point.**
The first version asserted the tessellated area against πr² and failed a pie that
is visibly round: a twenty-five-sided polygon inscribed in a six-pixel circle is
1% short in *area* and 0.05 pixels short at the middle of each chord, and the
second number is the one an eye can see. An area tolerance would have been tuned
until it stopped complaining, which is how a test stops meaning anything.

**A bar chart is reported rather than drawn as a pie.** A bar chart compares
magnitudes and a pie compares shares of a whole; drawing one as the other is not a
smaller picture, it is a different claim. **`preventChartOverlap` is reported
too**, and that is a real limit: resolving overlapping charts is the same problem
as placing labels and this server solves that one, but for text with a measured
box. Moving a chart away from the feature it describes without saying so is worse
than letting two neighbours sit on each other.

**A chart sized by its sum uses the proportional renderer's curve, deliberately.**
Area proportionality and Flannery's correction, the same as §3.10 — a chart sized
by its total is a proportional symbol whose symbol happens to be a chart, and two
renderers answering *how big is this* differently would be two answers to one
question.

**Where the nine stand now.** Seven are read: simple, unique value, class breaks,
proportional, heat map, dot density and chart. Two are refused and **both are
blocked rather than unwritten** — `CIMDictionaryRenderer` needs a dictionary style
this server does not hold and an Arcade evaluator it deliberately does not have,
and `CIMRepresentationRenderer` needs a geodatabase's representation classes,
which PostGIS has no equivalent of. Neither is a matter of effort, and that is
the honest end of the list rather than a pause in it.

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
