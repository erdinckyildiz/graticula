# ADR-033 — Symbology: one canonical document per layer, and both protocol faces derive from it

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM-HIGH` for the canonical model · `MEDIUM` for the derivation's fidelity |
| **Decided** | 2026-08-17 |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-028](ADR-028-style-documents.md) — a style stops being *per service* and becomes *per layer, composed into a service* |

---

## 1. Context — we publish a service and it has no appearance

The owner's observation, 2026-08-17: *"bir kere servis olarak publish edeceğiz ama
sembolojisi yok."* Measured before answering, because the gap is narrower and stranger
than *no symbology*:

| Face | What it says about appearance today |
|---|---|
| **FeatureServer layer document** | **Nothing.** No `drawingInfo`, no renderer, no labels. Every client invents a default |
| **VectorTileServer style** | A MapLibre style — a stored one if somebody wrote it ([ADR-028](ADR-028-style-documents.md)), otherwise generated. **The generated one is one hard-coded colour per geometry type**: `#1f6f8b` for points and lines, `#8fb8cc` for fills |
| **Our own console's map** | Picks a colour **client-side**, from a six-entry palette, per layer shown |

So one layer has three appearances and the server has an opinion about none of them.
ADR-028's own §2A already recorded the complaint this produces — *everything is the same
blue* — and answered half of it: an author can now write a MapLibre style, for the tile
face, per service. What that half cannot do is tell an ArcGIS client anything at all.

**And there is nothing to inherit.** ArcGIS Enterprise gets symbology from the map
document a publisher authored in Pro. We publish from a database table, where appearance
is not among the facts. Whatever the server shows first, it decided.

## 2. Alternatives considered

### Alternative A — two independent documents, one per face

Esri `drawingInfo` for the feature face, MapLibre style for the tile face, each authored
and stored on its own.

**Argument for.** It is honest that these are two vocabularies with genuinely different
shapes: one is class-based and static, the other expression-based and zoom-dependent.
Neither is a subset of the other, so neither projection is free.

**Argument against.** The operator styles one layer twice, and the two drift. Not *may*
drift — will, because nothing can compare them. A layer that draws one way in ArcGIS Pro
and another in a MapLibre client is a support conversation with no correct answer.

### Alternative B — Esri `drawingInfo` is canonical, MapLibre derived

**Argument for.** The compatibility target is ArcGIS. Storing what that face needs makes
that face exact and free, and a customer migrating has `drawingInfo` in hand already —
paste it in and their maps look right on day one.

**Argument against.** It puts another product's schema at the centre of Tier 1, which
[the build-vs-adopt policy](../build-vs-adopt-policy.md) forbids for libraries and which
is worse for a wire format we do not control. And the derivation is lossy in the
direction that matters most for our own tile face: `drawingInfo` has no vocabulary for
zoom-dependent width, so every tile style derived from it would be flat.

### Alternative C — a MapLibre style is canonical, per layer; every face derives from it (chosen)

**Argument for.** See §4 — this is what the reference does, and looking at it changed
this decision. Beyond that: MapLibre Style Spec v8 is a published specification every
client already speaks and every styling tool already writes, we already store one, and
the three renderer families an ArcGIS client understands — simple, unique value, class
breaks — all express cleanly *into* MapLibre. The lossy direction is the derivation, and
a derivation can report what it dropped where a second stored document cannot report
that it disagrees.

**Argument against.** Deriving a class-based renderer from an arbitrary MapLibre document
is guessing, and a cartographer's careful zoom curve will reach ArcGIS Pro as an
approximation. This is real and is not solved, only disclosed — see §3 and condition 2.

### Alternative D — our own small model, projected to both faces

A `Symbology` record with a deliberately small vocabulary — single symbol, unique value
on one field, class breaks on one numeric field, labels — and two writers.

**Argument for.** Neither wire format at the centre. Both projections are ours, so
neither is privileged, and the model can be exactly as large as v1 needs.

**Argument against, and this is why it was abandoned after being recommended.**
ADR-028 §2A already rejected a smaller version of this idea in this repository, in
writing: *"it re-implements a fraction of the style specification, badly, in a format
nobody else speaks."* A hand-rolled renderer model is a **fourth dialect** — after Esri's,
MapLibre's and SLD's — that every tool would have to learn and no tool would write. The
argument that killed it is the one this project already made against itself.

### Alternative E — SLD as an authoring format

**Rejected by owner decision, 2026-08-17: *"ben sld sevmiyorum."*** Recorded as a
preference rather than dressed up as a technical finding, because that is what it is —
and the technical case is thin either way. SLD is an OGC-published XML vocabulary that
GeoServer and QGIS both write, so accepting it would help somebody migrating from
GeoServer specifically. Against: it is a third serialisation to validate and translate,
its expression model matches neither of our faces, and the reference's own treatment of
it — an edge format offered through content negotiation, never the model — shows it can
be added later at the edge without any of this decision changing. **So it is not
implemented, and if it ever is, it is a serialisation at the boundary and never the
canonical form.**

## 3. Counterarguments to the preferred option

**The derivation is the whole risk, and it points at our strongest claim.** Q-07's
promise is that an unmodified ArcGIS client keeps working. A `drawingInfo` derived from a
MapLibre document is, for anything past the three simple renderer families, an
approximation of somebody's cartography — and ADR-008 §2 forbids degrading silently. The
mitigation is not cleverness, it is reporting: what could not be expressed is named in
the write's response and stored beside the style, so the operator learns it while they
are still holding the document. If that reporting is not built, this decision is worse
than Alternative A.

**A MapLibre document is per style, not per layer, and we are storing one per layer.**
The specification's top level carries `sources`, `sprite`, `glyphs` and an ordered
`layers` array — it describes a *map*, not a symbol. Storing one per layer means most of
that structure is either ignored or repeated. It is still the right trade, because the
`layers` array is exactly the part that carries appearance and because composing N
per-layer documents into one service style is mechanical, but the mismatch is real and
will produce awkward edges — the first being what to do with a stored `sources` block
(§5c).

**Nobody asked for authoring.** The owner asked for it, so this counterargument is
closed on this project, but it is worth writing down that the generated default plus a
derived `drawingInfo` would have answered the original complaint at a fraction of the
cost. Authoring is the larger half of the work and the smaller half of the improvement.

## 4. Evidence — how the reference holds this, and what was taken

Read 2026-08-17 at the owner's request and logged in
[reference-reading-log.md](../research/reference-reading-log.md): one guide in full and
three types under its styling feature. **This is the first read of that product's source
contents in this project**, and it changed the decision from Alternative D to C.

What it does, as facts about it:

- **One canonical document per layer, MapLibre Style Spec v8**, and every renderer reads
  it — vector tiles, its server-side raster paths, and an ArcGIS `drawingInfo` derived
  from it. Its stored record keeps the MapLibre JSON as canonical and a `drawingInfo`
  beside it described as a *cached conversion or import*.
- **`drawingInfo` and SLD are accepted as inputs and offered as outputs**, never as the
  model.
- **An unstyled layer answers with a deterministic default carrying version 0**, so
  *nobody has styled this* is distinguishable from *somebody chose exactly this*.
- **The lossy direction reports itself.** Its converter's contract requires callers that
  receive unsupported-symbolizer entries to surface them, in its own words, "so
  unsupported renderers are flagged rather than silently dropped."
- **A suggestion is advisory.** A separate call proposes a classification — equal
  interval, quantile, natural breaks, unique value — with a palette and a class count,
  and the operator applies it with an ordinary write. The server proposes; it does not
  decide.
- Revision metadata travels with the style: a version counter, a timestamp, who changed
  it, and a free-text summary.

**What is taken, and it is taken as reasoning rather than as shape:** that one canonical
document beats two synchronised ones; that a default must be distinguishable from a
choice; that a lossy conversion is acceptable exactly when it reports its losses; and
that classification is advice rather than a decision the server takes on somebody's data.

**What is deliberately not taken.** Their stored document appears to carry its own
`sources` block with absolute tile URLs — their guide's example bakes a host into the
stored style — and a stored URL is a fact that goes stale behind a rename, a port change
or a proxy; this repository's debt register is largely made of stored facts that went
stale. So §5c normalises instead. Their suggestion is also gated behind a paid tier,
which is a product decision with no bearing here. And their server-side raster paths are
out of v1 entirely ([ADR-004](ADR-004-rendering-engine.md) is `DEFERRED`).

**Citations for the formats themselves go to their specifications, not to the reference**
(ADR-030 condition 3): the MapLibre Style Specification for the canonical document, the
ArcGIS REST API's published `drawingInfo` and renderer objects for the derived one, and
OGC Symbology Encoding for the SLD that §2E declines.

## 5. Decision

### 5a. The canonical document is a MapLibre style, stored per layer

One column on the layer, holding a validated MapLibre Style Spec v8 document. It is the
only authored artefact; everything else about appearance is derived from it on read.

Authoring is `PUT /admin/layers/{name}/symbology`, and the body may be **either** a
MapLibre document **or** an Esri `drawingInfo` with a `simple`, `uniqueValue` or
`classBreaks` renderer. A `drawingInfo` is converted on the way in and the conversion's
losses are reported in the response.

### 5b. An unstyled layer has a *generated* appearance, and it is not a colour

The generated default is computed in **one place** and used by every face — the tile
style, the feature document's `drawingInfo`, and offered to our own console so it stops
choosing its own. It is **deterministic from the layer's identity**, so the same layer is
the same colour tomorrow and on another deployment, and it is reported as *generated*
rather than presented as somebody's choice — a version of `0`, exactly the distinction
§4 took from the reference.

This alone answers the owner's complaint for every layer nobody will ever style, which is
most of them.

### 5c. `sources`, `sprite` and `glyphs` are generated on read, never stored

A stored absolute URL is a fact with an expiry date. The canonical document is normalised
on write: the `layers` array and its paint and layout are kept, and the map-level blocks
are regenerated per request from the request's own host — which is also how the tile
service already serves its style and its glyph range URLs.

### 5d. A service's style is composed from its layers'

ADR-028 stored one style per service, because a style names that service's source layers
and orders them. That stays true and stops being the *authoring* unit: the service style
is the composition of its layers' canonical documents, in layer-index order. **The stored
per-service style survives as an override**, because ordering and filtering across layers
is a real cartographic need that a per-layer document cannot express — and when one is
stored, it wins for the tile face and is stated as winning.

### 5e. The feature face gets `drawingInfo`, and it is honest about its subset

The FeatureServer layer document gains `drawingInfo` derived from the canonical document,
using the three renderer families a client understands. What could not be expressed is
recorded on the layer and reported when the style is written — never dropped in silence.

We emit the simple symbol subset — `esriSFS`, `esriSLS`, `esriSMS` — and do not claim
CIM. That is written into the response rather than left for a client to discover.

### 5f. Classification is advice

`POST /admin/layers/{name}/symbology/suggest` proposes a classification over a chosen
field — equal interval, quantile, natural breaks, or unique values — and returns a
ready-to-apply document plus legend metadata. It changes nothing. The operator applies it
with 5a, or does not.

### 5g. What this is not

**Not server-side rendering.** Both faces here are *instructions a client draws with*.
ADR-004 is `DEFERRED` and rendering is out of v1; symbology conversations drift towards
WMS and that door stays closed. If server-side rendering is ever built, this document is
its input.

**Not SLD** — §2E.

### 5h. It lives in the platform store, and the boundary is a rule rather than a habit

Asked by the owner directly — *the PostgreSQL database is mandatory, so this or the file
system?* The repository already applies a three-way rule, written in
[HostSettings](../../src/Graticula.Host/HostSettings.cs)'s own comments, and symbology is
only ambiguous until the rule is stated:

| Home | The test it applies | What is there |
|---|---|---|
| **Platform store** | authored state — somebody's decision, unrecoverable if lost | the catalogue, sharing, the style column from migration 14, sessions, the audit log |
| **`StatePath`** | a secret that must survive a container replacement | the serving certificate |
| **`TileCachePath`** | derived — **must not be backed up**, rebuilt in seconds | tiles |

Symbology fails the second and third tests and passes the first, so it goes in the store.
Four arguments beyond that, of which the first is decisive:

- **Instances are interchangeable.** [ADR-029](ADR-029-affinity-routing-is-not-the-default.md)
  rejected affinity routing, so any request may land on any instance. A tile cache missing
  on the second node is *slower* — a miss, a rebuild, the same answer. A **style** missing
  on the second node is a **different answer**: the layer draws in different colours
  depending on which node replied, and nothing in the response says so. The file system
  loses this outright without shared storage, which [deployment.md](../deployment.md)
  records as unvalidated and which §6's anti-overengineering rule would ask to justify.
- **One transaction, one foreign key.** A style names its service's source layers and is
  validated against them (ADR-028). Deleting a layer must not leave an orphan style. In
  the same database that is a constraint; on the file system it is a second store with its
  own consistency story — the *one fact, two homes* class this project has already paid
  for three times ([D-24](../architecture-debt.md), [D-47](../architecture-debt.md), and
  the stale registers found on 2026-08-17).
- **One backup ([Q-48](../open-questions.md)).** A single dump restores the catalogue and
  its cartography at the same instant. Files mean a second backup nobody schedules and a
  restore that yields last week's colours — and **nothing looks broken**, which is the
  worst property a restore can have.
- **The audit trail is already there.** Every administrative write is audited in this
  store, and §5a's revisions belong beside it. A file's modification time is not *who*.

**The size objection is where the file system usually wins, and it does not apply.** Real
styles are tens of kilobytes; the bound is a megabyte, enforced as a **check constraint**
in the database rather than in our code, so a second writer cannot bypass it. A thousand
services is thirty megabytes in practice and a gigabyte at the absurd bound. The column is
`text` rather than `jsonb` so an author gets back the bytes they sent.

**The one real objection, measured rather than argued:** if the store is unreachable, does
a map lose its appearance? [ADR-026](ADR-026-serving-through-a-platform-store-outage.md)
serves remembered public layers for a bounded window, and the style endpoint resolves
through that same fallback — `PublishedService.Style` travels on the remembered record, so
the last known style is served for as long as the window lasts. That was verified in the
code rather than assumed, and it is written here because it is the property that makes the
storage choice safe. Had it not held, the answer would still have been a cache and not a
different home.

**Where the file system keeps winning, so this boundary is principled:** glyph ranges are
build artefacts, identical in every deployment and served immutable, and they stay in the
image ([ADR-027](ADR-027-glyphs-and-sprites.md)); tiles are derived and per-node;
certificates are secrets. The interesting edge is an **operator-uploaded sprite sheet**,
which is authored content rather than a build artefact — by the rule above it belongs in
the store as bytes, and attachments (ADR-013) are the precedent. If attachments later move
to disk or object storage, sprites follow them; the style does not.

### 5i. Built 2026-08-18, and what is not

**Built:** the canonical column (migration 23), the reader that accepts either format and
reports its losses, the deriver that projects onto the three Esri families and reports what
it cannot carry, `GET`/`PUT`/`DELETE /admin/layers/{name}/symbology`, both protocol faces
reading the stored document, and a Symbology page in Studio that shows the canonical
document, the derived `drawingInfo` and the losses together.

**Both faces, and that had to be one change rather than two.** Deriving the feature face
from a canonical document while the tile face went on generating its own would have made
the two disagree about the same layer — which is precisely the drift §7's first condition
was written to prevent, arriving by the back door of a half-finished change. So
`VectorTileServerMetadataWriter` reads the same document and puts back the `source` and
`source-layer` that §5c strips on write. **Measured on the running server:** a
zoom-interpolated line width survives on the tile face and arrives as 0.375 pt with a loss
report on the feature face; clearing the document returns both to the generated appearance
in the same request.

**Not built: §5f, the classification suggestion.**
`POST /admin/layers/{name}/symbology/suggest` — equal interval, quantile, natural breaks,
unique values — is designed here and has no code. It is the only part of this decision that
*changes nothing when called*, which is why it was the part to leave: everything above is
needed for a layer to have an authored appearance at all, and a suggestion is a
convenience on top of it. Recorded here rather than in a register, because it is a piece of
this decision rather than a compromise in it.

**Two defects found on the way in, both worth the sentence.** A hand-counted column ordinal
was wrong by one, so a stored symbology reached the admin endpoint and not the public
document — the two faces disagreed and *neither looked broken*. That read is by column name
now, and the comment says why it is the one read in that file that is. And a parameter
inside `case when @document is null` gave Postgres no type to infer, which failed only on
the clear path — so setting a document worked and clearing it answered 503, in a pair that
looks symmetrical.

## 6. Consequences

- **A migration**, adding the canonical column and the fidelity report to the layer. The
  existing per-service style column stays where it is and changes meaning to *override*,
  which is a documentation change rather than a data one.
- **ADR-028 is amended, not superseded.** Its decision — a style is a validated document
  checked on write, in MapLibre's vocabulary, because no server can guess cartography —
  is the foundation this stands on. What moves is the unit and the number of faces.
- **v1 scope grows by an authoring surface**, at the owner's decision. [v1-scope](../v1-scope.md)
  gains a line, because a scope document that does not list what was added is how scope
  moves without anybody deciding.
- **The console gains a symbology page** on the layer editor, beside Capabilities and
  Limits — and it is the first screen in this product where a text area holding JSON is
  not an acceptable answer for long.
- **Q-114 opens**: whether the generated default should consider what the layer *is* —
  a road network and a parcel fabric want different treatments and the geometry type does
  not distinguish them. Deliberately not answered here.

## 7. Conditions

1. **The generated default is computed once and consumed by all three faces**, asserted
   by a test that draws the same layer through the tile style, the feature document and
   the console's own choice and compares the colour. Three faces agreeing by accident is
   how they drift apart later.
   *(Discharged 2026-08-17. `GeneratedSymbologyTests.Both_faces_draw_the_layer_in_the_same_colour`
   compares the two protocol faces per geometry kind, converting `[r, g, b, a]` back to
   hex; the console has no third choice left to compare, because it now reads
   `drawingInfo` from the document instead of picking from a palette. Measured on the
   running server as well — seven layers, both faces, every pair equal — and **measured
   from inside a real client**: the ArcGIS Maps SDK parsed our document into a `simple`
   renderer with a `simple-fill` at `#ccbb44` and alpha `0.451`, which is the generator's
   0.45 carried through the alpha channel and applied once rather than twice.)*
2. **The derivation reports its losses, and the report is tested against a style that
   loses something** — a zoom-interpolated width is the obvious case. A conversion that
   silently approximates is the failure mode this whole decision accepts a risk on, and
   an untested report is not a report.
   *(Discharged 2026-08-18. `SymbologyConversionTests` carries the case this condition
   names: a `line-width` of `["interpolate", ["linear"], ["zoom"], 6, 0.5, 14, 6]` is
   reported as varying with zoom, and the width the feature face does emit is asserted to
   be the value at the lowest stop — 0.5 px, 0.375 pt — rather than an average or a guess.
   **And the pair, because a report that always fires is a report nobody reads:** a solid
   fill with an alpha reports nothing on either side of the conversion. Twelve more cases
   cover a hatched fill, a marker shape with no sprite, a marker angle and offset, a
   normalised class-breaks renderer, a compound `field2`, a filter, a `symbol` layer's
   labels, a second paint layer for one geometry, an absent `defaultSymbol`, and the top
   class of a `step` expression. **Measured through the endpoint too:** `PUT` of a
   zoom-varying line style on `tr_yol` returned the loss, the tile face kept the
   interpolation, and the feature face carried 0.375 pt.)*
3. **A `drawingInfo` accepted as input round-trips to an equivalent `drawingInfo`** for
   the three simple renderer families. If a customer's own symbology comes back different
   from what they sent, the migration promise is worth less than the paste-in convenience
   that motivated accepting it.
   *(Discharged 2026-08-18, for all three families and with one honest exception stated.
   `simple` returns the same `esriSFS` with its colour, alpha and outline; `uniqueValue`
   returns the same field, the same three values in order, their colours and the
   `defaultSymbol`; `classBreaks` returns the same field and its interior breaks exactly.
   A 6-point marker comes back 6 points and a 2-point dashed line comes back 2 points and
   `esriSLSDash` — the arithmetic is ×4/3 and back through a repeating decimal, so the
   canonical document keeps four decimal places for exactly this reason.
   **The exception is the top class of a `classBreaks` renderer**, and it is reported
   rather than hidden: a MapLibre `step` expression has no upper bound on its last class,
   so the last `classMaxValue` cannot survive and is reconstructed from the last break.
   The test asserts the loss report, not a number that came back.)*
4. **No absolute URL is ever stored in a canonical document**, asserted by a test that
   writes one and reads the stored form back. §5c is the kind of rule that decays the
   first time somebody stores what they were sent.
   *(Discharged 2026-08-18, and enforced wider than the condition asks. Three tests write
   a style with an absolute `sprite`, `glyphs` and `sources.tiles` and assert that the
   stored form contains neither the host nor `https://` — and that the writer was told,
   because a silently dropped block renders differently from the style that was sent.
   **The rule is checked on every string in the document, not only in those three
   blocks**, because a version enforced where it was first written is the version this
   condition predicts will decay: a `fill-pattern` naming somebody's host is refused with
   the URL in the message. Refused rather than stripped there, deliberately — it is not a
   block this server regenerates, so dropping it would change the appearance and keeping
   it would store a fact with an expiry date.)*
5. **The size bound on the new column is a check constraint, not a C# guard.** Migration
   14 already did this for the per-service style and its comment says why: a bound that
   lives only in the application is a bound the next writer bypasses. A column that can
   hold a megabyte of anything becomes a place to store something else.
   *(Discharged 2026-08-18. Migration 23 adds `layer.symbology` with
   `check (symbology is null or length(symbology) <= 262144)`. There is a constant in the
   application as well — `SymbologyConversion.MaximumCharacters` — and it exists so that a
   refusal can name a number a caller can act on rather than surfacing as a constraint
   violation; the constraint is what makes it a bound. Applied to the development store
   and verified: dry run reported one expand migration, `--apply` took it from 22 to 23,
   `minimum_reader_version` unchanged at 1.)*

## 8. Assumptions

| ID | Assumption | Status |
|---|---|---|
| A-077 | The three simple ArcGIS renderer families cover the symbology of the overwhelming majority of published layers, so a derivation limited to them is a small loss in practice | `UNVALIDATED`. It is what both the reference and ArcGIS's own defaults bet on, and it is checkable against any real estate of services — including, eventually, the owner's 32 layers |
| A-078 | An operator would rather write MapLibre than a format we invented, because tools exist for the first | `UNVALIDATED`, and it is the load-bearing assumption under choosing C over D. The counter-case is an operator who writes neither and only ever uses the suggestion, in which case the canonical vocabulary matters much less than §2C claims |
