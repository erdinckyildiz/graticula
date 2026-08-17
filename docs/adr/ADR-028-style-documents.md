# ADR-028 — Style documents: the one thing about a map a server cannot guess

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `HIGH` |
| **Decided** | 2026-08-15 |
| **Depends on** | [ADR-021](ADR-021-tile-encoding.md), [ADR-027](ADR-027-glyphs-and-sprites.md) |
| **Supersedes** | — |
| **Superseded by** | — |


> **Amended 2026-08-17 by [ADR-033](ADR-033-symbology.md).** This decision's substance
> stands: a style is a validated MapLibre document, checked on write, because no server
> can guess cartography. What moved is the **unit and the audience** — the canonical
> document is now stored **per layer** and both protocol faces derive from it, including
> an ArcGIS `drawingInfo` this ADR could not produce. The per-service style described
> below survives as an **override**, for the ordering and filtering across layers that a
> per-layer document cannot express. Read §2A here before proposing a bespoke symbology
> model: it is the argument that later killed one.

---

## 1. Context

Until now the VectorTileServer generated its style: every polygon layer the same
blue, every line the same blue, drawn in publication order, and — before
[ADR-027](ADR-027-glyphs-and-sprites.md) — no labels at all.

**That was always a placeholder, and it cannot be improved into a solution.**
What colour a layer should be depends on what it means, what it is drawn on top
of, which layers must be readable at a glance and which are context, and who is
looking at the map. None of that is knowable from a table's geometry type. A
server that picks colours is a server guessing at the one part of a map that is
entirely a human judgement.

The completeness register carried *"Style document management — not started"*
from the vector-first decision. ADR-027 shipped glyphs and closed half the gap;
this is the other half, and ADR-027 §10 condition 5 pointed at it directly: the
empty sprite sheet stops being acceptable when user styles arrive.

## 2. Alternatives considered

### Alternative A — keep generating, and add configuration

Colours per geometry type in `appsettings`, or a palette per layer in the
catalogue.

**Argument for.** No new resource, no new document to validate, and it fixes the
most common complaint — *everything is the same blue* — with a fraction of the
work.

**Argument against.** It re-implements a fraction of the style specification,
badly, in a format nobody else speaks. A cartographer cannot express a
data-driven colour ramp, a zoom-dependent width, or a label placement rule in a
palette setting, and those are the things a real map needs. It also produces a
second dialect of styling that any tooling would have to learn.

### Alternative B — store the style as a portal item, like ArcGIS does

**Argument for.** Matches the product being compatible with, and separates
content from service.

**Argument against.** There is no portal item model in this product and building
one to hold a JSON blob is a large detour. [ADR-019](ADR-019-portal-server-split.md)
fused the tiers precisely to avoid that shape until something needs it.

### Alternative C — store one style per service, checked on write (chosen)

**Argument for.** A style *is* a document about a service: it names that
service's source layers and orders them. One column, one resource, and the
existing route already serves a style — it just serves a generated one. The
format is the Mapbox/MapLibre style specification, which every client already
speaks and every styling tool already writes.

**Argument against.** It ties a service to exactly one style, when a real
deployment often wants several — a light one, a dark one, a print one. And a
stored document is a thing that can rot: a layer removed from the service later
leaves a style naming a source that no longer exists.

### Alternative D — accept any document and serve it back unexamined

**Argument for.** Simplest possible implementation, and maximally future-proof
against a specification that grows.

**Argument against.** The two failures it permits are exactly the ones worth
preventing. A mistyped `source-layer` renders a blank map with **no error
anywhere** — correct tiles, correct client, nothing to search for. And a style is
a document this server hands to every viewer's browser, so an absolute URL in it
makes those browsers fetch from wherever the author said.

## 3. Counterarguments to the preferred option

**The strongest one: one style per service is not enough and everyone will hit
it.** Light and dark are the obvious pair, and a print style is the obvious
third. The answer today is that a second style needs a second resource path and
a name, which is a small change to make later and a real limitation now.
Recorded as condition 2 rather than pretended away.

**The second: a validator is a compatibility risk.** The style specification
grows, and a check written against today's version will eventually refuse
something valid. This is mitigated by checking almost nothing — the version, the
presence of `layers`, source-layer names, and URLs — and by storing unknown
properties untouched, which is tested. But the risk is real and it is the reason
the check does not go further.

**The third: refusing `icon-image` is a validator enforcing a gap in the
product.** It refuses a style that is perfectly valid, because *our* sprite sheet
is empty. That is a strange thing for a validator to do, and the alternative —
storing it and drawing nothing — is worse only if you accept that a silent blank
is worse than a loud refusal. This ADR does accept that, and notes the check must
be deleted the day sprites can be uploaded.

## 4. Evidence

| Claim | Evidence |
|---|---|
| The document survives byte for byte | `A_stored_style_comes_back_exactly_as_it_was_written` — the served bytes equal the file, whitespace and key order intact. Verified against a running server, not a mock |
| Unknown properties are not dropped | A style carrying `metadata`, `terrain` and an invented key round-trips whole |
| A mistyped source layer is caught | Refused with *"draws source-layer 'parcel', which this service does not have. It has: parcels"* |
| Case matters, because it matters in the tile | `Parcels` is refused against a layer named `parcels` |
| A style cannot send viewers elsewhere | Six absolute-URL forms refused, including `//host`, `javascript:`, and `http://169.254.169.254/…`; and a root-relative path, which stays on the host and still leaves the service |
| A refused style does not disturb the stored one | `A_refused_style_does_not_disturb_the_stored_one` |
| Clearing works | `Clearing_a_style_goes_back_to_the_generated_one` |
| Styling is a privilege | An anonymous `PUT` is refused |

**39 tests**: 32 on the validator, 7 conformance, plus 3 in the platform store.

**A defect only a database could find.** The clear path wrote
`style_updated_at = case when @style is null then null else now() end`, and a
null parameter inside a `CASE` gives Postgres nothing to infer a type from —
`42P08, could not determine data type of parameter $1`. **Storing a style worked
and removing one returned a 500**, which is a state somebody could get into and
not get out of. The unit tests could not have found it; the end-to-end run did,
on the first `DELETE`.

**A test-fixture lesson worth keeping.** The conformance class first read the
served style to discover a source layer, and hit a *stored* style whose first
layer was a background — no `source-layer`, and a failure with nothing to do
with the behaviour under test. It now clears to the generated style first. A
test whose fixture is whatever the server happens to be holding fails for the
wrong reasons.

## 5. Decision

**A service may carry one style document, stored as text, served in place of the
generated one.**

- `PUT`, `GET` and `DELETE` at `/admin/services/{name}/style`, behind
  `content:publishFeatures` — styling is a publisher's job, and the person who
  published a layer is the person who knows what colour it should be.
- The public resource `…/VectorTileServer/resources/styles` serves the stored
  document **unchanged** when there is one, and the generated one otherwise.
- **Text, not `jsonb`.** A style is a document somebody authored and will open
  again; `jsonb` reorders keys and normalises whitespace, so a cartographer
  diffing the server's copy against their file would find changes they did not
  make. Validity is checked before the write instead, which is where the person
  who can fix it is still holding it.
- **Read as text, never bound to a model.** The specification allows properties
  this server has never heard of, and a client that understands them should get
  them. Binding would silently drop them.

**What is checked, and nothing else:** it parses, at depth ≤ 32; it declares
`version: 8`; it has a `layers` array; every layer has an `id` and a
`source-layer` that the service actually has (background layers excepted); no
URL anywhere leaves this server; and no layer sets `icon-image`, because the
sprite sheet is empty and the icon would silently not draw.

**Bounded at 1 MB**, enforced while reading the body rather than after, and again
by a column constraint so a second writer cannot bypass it.

## 6. Consequences

**Positive.**

- The one part of a map a server cannot guess is now somebody else's to decide.
- The most common authoring mistake — a mistyped source layer — becomes a
  refusal that names the layers that exist, instead of a blank map.
- A style cannot be used to point every viewer's browser at another host.
- The generated style stays as the default, so nothing changes for a service
  nobody has styled.

**Negative.**

- **One style per service.** Condition 2.
- **A style can rot.** Removing a layer from a service leaves a style naming a
  source that no longer exists, and nothing revalidates it. Condition 3.
- **The validator will eventually refuse something valid**, because the
  specification grows and this does not.
- **`icon-image` is refused**, which is a valid style being turned away for a
  reason that is ours.
- One more thing in the platform store that is not a catalogue fact.

**Ports created.** None.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| A-071 | One style per service is enough to be useful, even though it is not enough to be finished | `UNVALIDATED`. Light/dark is the obvious counterexample and it is expected to arrive |
| A-072 | Refusing a style at write time costs an author less than a blank map costs them at read time | `UNVALIDATED` by use, and it is the premise of the whole validator |

## 8. Dependencies

**Depends on**: [ADR-027](ADR-027-glyphs-and-sprites.md) — a style with labels
needs glyphs, and the `icon-image` refusal exists because that ADR's sprite sheet
is empty. [ADR-021](ADR-021-tile-encoding.md) for the tiles a style draws.

**Depended on by**: ADR-027 condition 5, answered here a third way. Any future
sprite upload, which deletes the `icon-image` check.

## 9. Revisit triggers

- **Somebody asks for a second style on one service.** Light and dark is the
  case, and it is when condition 2 becomes work rather than a note.
- **The validator refuses a style that is valid.** Then the check is behind the
  specification and the trade in §3 has stopped paying.
- **Sprites become uploadable.** The `icon-image` refusal is deleted, not
  relaxed.
- **A style is found naming a layer that has since been removed.** Condition 3
  stops being theoretical.

## 10. Conditions

1. **A stored style is served byte for byte.** *(Discharged —
   `A_stored_style_comes_back_exactly_as_it_was_written`, and the column is text
   for this reason.)*
2. **More than one style per service**, before anybody is told this product
   supports theming. Light and dark is not an exotic request.
3. **A style is revalidated when the service's layers change.** Unpublishing a
   layer today leaves a style that draws a source which no longer exists, and
   nothing notices — which is the exact failure the write-time check was built
   to prevent, arriving by a different door.
4. **The `icon-image` refusal is deleted when sprites can be uploaded**, rather
   than left as a permanent restriction whose reason nobody remembers.
5. **The admin route naming is straightened out.** `/admin/services/{name}/sharing`
   addresses a *system* service while `/admin/services/{name}/groups` and now
   `/style` address a *published* one. A published service named `Geometry`
   would collide with the system one, which is a latent bug this ADR adds a third
   route to rather than causes.

## 11. Dissent

**Recorded.** Alternative D is right that a validator on an evolving
specification is a liability, and this one will eventually refuse a style that a
newer client would have rendered correctly. The bet is that the two failures it
prevents — a blank map with no error, and a document that redirects every
viewer's browser — cost more than the false refusal will. That bet is not
obviously correct, and the way it would be seen to be wrong is somebody working
around this server by hosting their style elsewhere.
