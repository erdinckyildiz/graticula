# ADR-074 — Z and M are read, said out loud, and not served

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `HIGH` |
| **Decided** | 2026-09-16. The owner asked how to proceed *if we plan to support 3D soon* (*"Yakın zamanda 3B'yi de desteklemeyi planlıyorsak nasıl ilerleyelim?"*) and chose the first step of the staged answer: **ADR + honesty**. **That elevation is not served in v1 is [v1-scope](../v1-scope.md) and is not this ADR's to decide. What is decided here: that the difference between what a table holds and what this server returns is stated wherever a person meets it, that a three-dimensional table is publishable rather than refused, and that `allowGeometryUpdates` is false on such a layer. §5 is the order the rest of 3D would arrive in, and it is a plan rather than a decision** |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

The geometry model holds x and y. `Point`, `LinearRing` and the rest are built on `XySequence`,
whose stride is two; `WkbReader` reads a `PointZ` and drops the third ordinate, reporting that it
did; `PostGisFeatureWriter` refuses a geometry update to a row whose stored geometry carries Z or
M, because writing a flat shape over it would discard what the client never saw
([ADR-008](ADR-008-query-engine.md) §4.5a); `ArcGisGeometryReader` refuses a geometry that declares
`hasZ`; and every layer document said `hasZ: false` as a literal.

That is a coherent two-dimensional server, and none of it was the problem. The problem was what a
person is told, and it was four different things:

- **The geodatabase import job** counted what it flattened, per layer, and the console shows the
  count ([D-107](../architecture-debt.md)) — the one place the loss was visible.
- **A shapefile upload** warned that z or m values *were not stored*.
- **A registered table** — somebody's own PostGIS database, which is the ordinary way data arrives
  here — said nothing at any point. Worse: a column declared `geometry(PointZ, 4326)` could not be
  published at all. The registration probe reports the declared type, the console hands it back to
  `POST /admin/layers`, and that parsed it with `Enum.TryParse` into `GeometryKind` — so the
  refusal read *geometryType 'POINTZ' is not one of: Point, MultiPoint, …*, which says *this is not
  a geometry type* about a type PostGIS declares.
- **An editor in ArcGIS Pro**, against a three-dimensional layer that did publish, was offered the
  geometry edit tool: the layer document derived `allowGeometryUpdates` from the capability string
  alone, and the capability string is right — attribute editing works. Every geometry save came
  back refused, one feature at a time.

The last two are the ones that cost somebody a day. The first is a refusal that misnames itself;
the second is the over-claim [ADR-008](ADR-008-query-engine.md) §2 exists to refuse, one layer
narrower than a capability string can express.

## 2. Alternatives considered

### Alternative A — read the declaration, keep serving 2D, and say so at every surface *(chosen)*

The describe reads the geometry column's declared type in the round trip it already sends, and the
layer's description carries it. Nothing about storage, reading, tiling or drawing changes. What
changes is that the difference is said: at publish, in the table list, in the layer document's
`allowGeometryUpdates`, and in the refusal an edit gets.

**Argument for.** Every honest thing this server could say about elevation, it can say today
without touching the geometry model. The order matters: a 3D geometry model that arrives before
anybody can see which of their layers are affected lands in a system where nobody knows what would
change.

**Argument against.** It is a documentation change with a flag attached, and it does not give
anybody their elevations. That is true, and §5 is where the rest lives.

### Alternative B — report `hasZ: true` from the column

**Argument for.** It is the truth about the table, and `hasZ` is where an ArcGIS client looks.

**Argument against, and it is decisive.** `hasZ` is a claim about the answer, not about the store.
A client reads it to decide whether to offer a z control and whether to expect a third ordinate in
`query`; this server returns two, so the control would be offered over features that have no
elevation in them, and a client writing back what it read would send `hasZ` geometry that the
reader then refuses. Over-claiming here produces a broken workflow rather than a missing feature.

### Alternative C — refuse to publish a three-dimensional table

**Argument for.** Nothing is lost, because nothing is served.

**Argument against.** It is refusing the data rather than describing it. A contour layer served
flat is useful to the person who wanted it on a map; the loss is real and is theirs to weigh, and
the server's job is to make sure they know before they publish rather than to decide for them. The
same reasoning already governs the geodatabase import, which publishes and counts.

### Alternative D — flatten silently, as before, and change nothing

Rejected by its own record: four surfaces each saying a different half of it, and two saying
nothing at all, is what this ADR is about.

## 3. Decision — the model is two-dimensional, and that is v1

Restated here rather than decided: `XySequence` has stride two, every surface answers x and y, and
[v1-scope](../v1-scope.md) is where that comes from. `hasZ` and `hasM` in a layer document are
false, and they are the true answer rather than a placeholder, because they describe what `query`
returns.

## 4. Decision — what the data holds is read and said

1. **The description carries what the column declares.** `LayerDescription.StoredOrdinates` is read
   from the geometry column's typmod — `postgis_typmod_type` in the query that already reads the
   fields — and is `Z`, `M`, both or none. **The declaration, not the rows**: a column typed as bare
   `geometry` reports none although it may hold a `PointZ`, because the alternative is reading every
   geometry in the table to describe a layer. Nothing over-claims on the strength of it — the write
   path asks each row it is about to overwrite what that row actually carries, per feature, and
   refuses there. **A GeoParquet layer reports none and is not asked**: it is read-only, so the
   flag §4.2 turns off is already off for it, and reading a file's schema to answer a question
   nothing acts on would be a claim with no consequence. It becomes worth asking the day a file
   source is writable, and not before.
2. **`allowGeometryUpdates` is false on a layer whose column declares Z or M.** The specification's
   derivation is *the capability set contains Update*, and that is what the service document
   computes; the layer document narrows it by the one case the writer refuses outright. The
   capability string keeps `Update`, because attribute editing works.
3. **A three-dimensional table publishes.** `POST /admin/layers` reads the ordinate suffix off the
   declared type, publishes the two-dimensional kind underneath it, and reports in the publish
   response what happens to the ordinate.
4. **One sentence, in one place.** `Ordinates.TwoDimensional` — *this server reads, stores and
   returns two dimensions* — is the shared clause of the edit refusal, the reader's refusal, the
   shapefile warning and the publish note. Four surfaces phrasing the same fact four ways is how
   this looked like four unrelated behaviours.
5. **The console shows it where the decision is made**: the registered-table list marks a column
   that carries more than x and y, and the publish report repeats it.

**The message that promised a way out is gone.** The reader's refusal used to end *or use a layer
whose provider carries Z*. There is no such provider and there was none when that was written.

## 5. The order the rest would arrive in — a plan, not a decision

If 3D is taken up, this is the sequence, and each step is its own ADR:

1. **Honesty** — this ADR.
2. **The geometry model** — ~~`XySequence` becomes stride-aware (2, 3 or 4) with behaviour unchanged
   at every existing call site, and the tile benchmark is re-run before anything depends on it.~~
   **Done 2026-09-16 as [ADR-077](ADR-077-z-and-m-ride-beside-x-and-y.md), and both halves of the sentence
   were wrong**: a stride would have made a dozen `AsSpan()` consumers misread a 3D shape silently, so Z
   and M ride beside x and y instead; and production tiles are encoded by PostGIS, so the benchmark that
   mattered was the query path's. A flat geometry costs nothing measurable. The three semantic questions
   below were answered by the owner and are ADR-077 §5.
   Three semantics have to be answered there rather than discovered: *what does a 2D operation do
   to Z* (clip, simplify, buffer — interpolate, carry, or drop and say so), *does the tiling and
   rendering pipeline drop to 2D at its edge* (it should: a tile is a picture), and *may a hosted
   layer be defined 3D*.
3. **Surfaces, one at a time** — `query` returning Z, then `applyEdits` accepting it, then WFS and
   OGC API Features. Each is a place `hasZ` becomes conditional rather than false.
4. **Storage and import** — hosted tables defined `PointZ`, imports keeping the ordinate they
   currently count, which is what closes [D-107](../architecture-debt.md).

## 6. Conditions

1. **A layer whose column declares Z is published on the showcase and opened in a real ArcGIS
   client**, and the client does not offer a geometry edit tool for it. *(Open — the flag is pinned
   by `ThreeDimensionalLayerDocumentTests` against the document, and what a client does with it is
   the claim that matters.)*
2. **The declaration is read from a real column, not a parsed string.**
   *(**DISCHARGED** 2026-09-16 — `TheDescribedShapeSaysWhatTheColumnCarriesTests` describes five
   declared columns and one bare column holding a `PointZ`, and asserts the bare one reports
   nothing while `ST_Zmflag` says the row carries Z.)*
3. **If step 2 of §5 is ever started, this ADR is reopened rather than amended in place**, because
   §3 is v1-scope's and changing it is a scope decision the owner takes.

## 7. Consequences

- A person publishing a registered 3D table learns, at the moment they publish it, that the
  elevation stays in their table.
- An ArcGIS editor is not offered a tool that refuses every save.
- A table that could not be published can be.
- Nothing about storage changes, so [D-107](../architecture-debt.md) stays open and keeps its own
  trigger. This ADR is the record of what is honest while it is open.

**State.** None. What a column declares is read from PostgreSQL's catalogue on every describe and
cached only where a description already is — per node, in `ServiceContexts`, for as long as any
other described fact. Nothing is stored at publish time, deliberately: a flag written into the
catalogue would be right on the day it was written and wrong the day somebody altered the column,
which is the drift [ADR-063](ADR-063-a-field-list-may-differ-from-the-table.md) answers the same way.
