# ADR-053 — The symbology editor is three columns, and the item page has a head

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `HIGH` |
| **Decided** | 2026-09-04 |
| **Supersedes** | — |
| **Superseded by** | — |

---

## 1. Context

[ADR-051](ADR-051-an-appearance-is-chosen-by-looking-at-it.md) put a picture beside
the symbology controls, and [ADR-052](ADR-052-the-canonical-symbology-document-is-cim.md)
gave those controls the renderer families, the classify step, the symbol stack, the
visual variables and the shipped symbol sets. Between them the editor became able to
author what the server can draw.

It also became long. On 2026-09-04 the owner classified a layer into 256 classes and
said: *ya ben bu ekranı cidden anlayamıyorum. çok karmaşık.* Two hours later, on
the same screen: *save diyorum, refleshleyince hepsi gidiyor. save ne, store ne?*

Both sentences are about the same property. The page was **one column of unrelated
sections**: a state line, a picture, a form, a class list, a symbol stack, a vary
form, a symbol gallery, a disclosure holding the canonical document, a losses block
and a derived `drawingInfo`. Nothing was wrong with any of them individually —
[D-217](../architecture-debt.md) and [D-219](../architecture-debt.md) had just
repaired the worst of what was — but a reader looking for one of the ten read past
four others to reach it, and **Store was below all of them**.

The owner then supplied a design handoff (direction 1c) covering the console:
the symbology editor as the main change, and the item page, the operations screen
and the landing page beside it. This ADR records what was taken from it, what was
not, and why the two departures are departures.

Note that this is a decision about **arrangement**, not about capability. No control
was removed and no element id was renamed; the register of what the editor can
author is still ADR-052 §3.

## 2. Alternatives considered

### Alternative A — shorten the page

**Argument for.** The complaint is length, and the page has ten sections. Four of
them — the losses, the derived `drawingInfo`, the document and the symbol gallery —
are read rarely. Putting each behind a disclosure would take the page from about
2,600 pixels to about 1,100 with no rearrangement at all, and `<details>` is already
the idiom this file uses for the document.

**Argument against.** It answers *long* and the complaint was *karmaşık*. A shorter
scroll through the same ten unrelated things is the same screen with less of it
visible, and the four sections chosen for hiding are exactly the four that answer
*what is actually stored* — which is the question D-219 was about. It would also
make the losses harder to reach, and ADR-033 accepted a lossy conversion **on the
condition that the loss is stated**; a mitigation behind a disclosure is a
mitigation that depends on curiosity.

### Alternative B — a wizard

**Argument for.** The task has a natural order: choose a family, choose a field,
classify, adjust, store. A four-step flow would put one decision on the screen at a
time and could not be misread.

**Against.** Symbology is not a task with an end; it is a thing you return to and
tune. A wizard is right for *publish this table*, which happens once, and wrong for
*make this a bit darker*, which happens fifty times. It would also make the picture —
the whole of ADR-051 — visible in only one step, and the picture is what the reader
is deciding against at every step.

### Alternative C — three columns, one question each (**chosen**)

Renderer on the left, the picture in the middle, an inspector on the right, under a
44-pixel strip carrying where you are and the two things you can do.

**Argument for.** Each column answers a different question — *what kind of drawing
is this*, *what does it look like*, *what is in it* — so a reader looking for one
of them is not reading past the others. Nothing is hidden: the document, the ArcGIS
projection and the losses become the inspector's three tabs rather than three more
sections under the fold. And the picture, which ADR-051 argues is the thing being
chosen, stops being a 336-pixel thumbnail and becomes most of the screen.

**Against.** It needs width. The three columns are 264 + flexible + 336, which sets
a floor of about 1,180 pixels, and the title strip wants about 1,370 before it
begins abbreviating. This is the departure recorded in §6.

## 3. Counterarguments to the preferred option

**Three columns is the shape of a tool, and this console is a set of documents.**
Every other screen in Studio is a page in a 44-pixel margin. Making one of them an
instrument panel means the product has two layouts, and the brief that produced the
current stylesheet was explicit that the console should read as one product. The
counter is that this screen is genuinely a different kind of thing — it is the only
screen whose subject is a picture — and that the alternative was a page nobody could
read. But the cost is real and it is one screen's worth of inconsistency.

**It reverses [D-217](../architecture-debt.md), which is one day old.** D-217 made
the class list and the symbol stack alternate, because a permanently rendered stack
could be titled after a row scrolled out of sight — *Symbol layers — Ankara* over a
list showing ten classes of 256, none of them Ankara. That fault was reproduced by a
design review and the repair was correct. This ADR puts them back on the screen
together. The argument is in §5; the honest statement of the risk is that a
rearrangement which brings back a fault the project fixed yesterday is exactly the
kind of change that should be hard to make, and this one was made in an afternoon.

**A generated appearance now costs a click.** The editor opens on a screen that says
the layer is unstyled rather than on a form filled with a document nobody wrote. On
this fixture set almost every layer is unstyled, so almost every visit begins by
dismissing something. If the reader's intent is *always* to start styling, the screen
is a toll.

## 4. Evidence

Measured in Chromium at 1440×900 against the fixture server, 2026-09-04. The
screenshots and the probe scripts are disposable and were not kept; the numbers were
read off `getBoundingClientRect` and `document.scrollHeight` rather than estimated.

| Claim | Evidence | Source |
|---|---|---|
| The old page was a scroll rather than a screen | `#page-symbology` measured about 2,600 px tall on `ci_many` (256 classes) against a 900 px window | measured 2026-09-04 |
| Store was below the fold | the button sat under the class list, the stack, the vary form and the gallery | the markup it replaced |
| The title strip does not fit at 1440 | its children want 1,370 px of content and the column is 1,208 | measured, three viewport widths |
| Ten class rows do not fit beside a symbol stack | a row is 58 px in a 336 px column, so ten is 580 of about 700 | measured, and the reason the list gives up the room |
| The preview can say *nothing drew* exactly | `ThumbnailEndpoints.RenderAsync` clears to `Rgba.Transparent`, so a blank picture is a picture whose every alpha is zero | `ThumbnailEndpoints.cs` |
| The ground chips are a real control | the same transparency makes what is behind the picture this page's own decision, with no request | as above |
| `dl.metrics` had no rules at all | the operations cards rendered as a default `dl`, ten lines with a 40 px staircase | measured 2026-09-04 |
| Every *Map* shortcut opened layer 0 | `visHref` read `at.index`; `placeOf` returns `at.id` | `console.js`, fixed here |

## 5. Decision

**The layer symbology page becomes a three-column editor that fills the window**,
under a sticky 44-pixel strip carrying the breadcrumb, the service's own item
tabs, the state line and the two acts — *Back to generated* and **Store**. (The strip carried a
layer switcher too until §9a moved it into the rail the same day.) The columns are Renderer (264 px), the picture (flexible), and an
inspector (336 px) whose three tabs are Classes, Document and ArcGIS. Nothing was
removed and no id was renamed.

**The class list and the symbol stack are both on the screen again**, adjacent
inside the inspector's column, with the selected row marked and scrolled into view
whenever the selection moves. D-217's fault — *the panel is titled after a row
nobody can see* — is prevented by adjacency rather than by alternation, and the
console suite now asserts that property directly rather than asserting either
mechanism: `The_symbol_panel_names_a_class_that_is_on_the_screen` measures that the
row the panel names is inside the visible box of the list.

**A layer with no stored document opens on a screen that says so**, with *Start from
the generated look* and *Paste a document*. §5b of ADR-033 makes a generated
appearance a real state with a version of 0; this is the first screen that treats it
as one.

**The three renderer families become three radio cards** rather than a `<select>`,
and `#symKind` stops being an element with a value: `symKindValue()` and
`symKindSet()` are the single reader and writer.

**Two controls in the handoff were not built**, and both for the same rule —
ADR-034's *a control is not drawn for a feature that does not exist*: the preview's
zoom buttons, because the preview is one PNG at the layer's own drawn extent and
they could not change it; and a status dot on the runtime and cache cards on
Operations, because neither has a failure state to report. The dots that were built —
the platform store's and route governance's — are read from `platformStore.reachable`
and from ADR-018 condition 5's ungoverned count.

**The item page gains a head**: a 33-pixel title, a subtitle, and its five tabs as a
segmented control beside them. The mono facts strip that carried
`4 entries · max 50,000 rows · operations: Query,Create,Update,Delete` becomes two
panels — *What a client may do* and *What one request may spend* — read from the
service document rather than from the settings form, because the document is what a
client gets. Sharing becomes three radio cards on the Settings page, and the
service-wide style override moves from the Visualization tab to the foot of the
Symbology panel, which is the rule it is the exception to.

## 6. Consequences

**Positive.**

- The question *what will this look like* is answered by most of the screen rather
  than by a thumbnail, which is what ADR-051 decided and could not yet show.
- Store is beside the state line that describes it, which is the answer to *save ne,
  store ne?* that a signpost was not.
- The conversion's losses get a count on the tab that holds them, so ADR-033's
  mitigation no longer depends on the reader scrolling to the bottom of a page.
- The ground chips answer *will anybody see this on a dark basemap*, which this
  console could not answer at all before, and they cost no request.
- Four defects were found by measuring rather than by review: `visHref` reading a
  field `placeOf` does not return, `dl.metrics` having no stylesheet rules, the
  service page having no title, and `EPSG:3,857`.

**Negative.**

- **The editor needs 1,180 pixels and the strip wants about 1,370.** Below 1,440 the
  breadcrumb abbreviates; below 1,180 the whole editor scrolls horizontally. This
  console is an operator's tool on a desktop and that is the trade taken, but it is a
  trade: the symbology page is now the one screen with a width requirement.
- **One screen has a different layout from every other.** See §3.
- **A click before styling an unstyled layer.** See §3. The console suite carries the
  cost visibly: `OpenSymbologyAsync` exists because twelve tests had to learn about
  it.
- **D-217's mechanism is gone and only its property is protected.** If a future
  rearrangement makes the inspector taller than a window, the panel and its row can
  separate again, and the only thing standing between that and the owner's original
  complaint is one assertion.
- **`#symState` moved into the Document tab**, which is behind a tab a reader may not
  be on. Two facts were promoted out of it so that nothing important is hidden: the
  generated case became its own screen, and the service style override became a
  banner. What stays behind the tab is *a stored document, n bytes*, which is a fact
  about the document.

**State.** None. This decision rearranges a screen: nothing is added to the catalogue and nothing is held at runtime beyond what the page has in its own variables while it is open — which of three inspector tabs is showing and which of three grounds is painted behind the preview. Both are deliberately *not* in the address and *not* remembered: a tab is a read-out of one document rather than a place, and a ground is a thing somebody tries and moves on from. Neither survives a navigation, and neither is shared between nodes because neither leaves the browser.

**Ports created.** None. No dependency was adopted.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| [A-081](../architecture-assumptions.md) | The operator console is read on a screen at least 1,180 CSS pixels wide | `UNVALIDATED` — **written for this ADR**, because nothing in the product depended on a width until now. Every other screen degrades; this one scrolls sideways and the picture is what leaves |

## 8. Dependencies

**Depends on:** [ADR-033](ADR-033-symbology.md) (the canonical document and its
losses), [ADR-034](ADR-034-server-and-studio.md) (the two surfaces, the item page's
tabs, and *a control is not drawn for a feature that does not exist*),
[ADR-051](ADR-051-an-appearance-is-chosen-by-looking-at-it.md) (the server draws the
preview), [ADR-052](ADR-052-the-canonical-symbology-document-is-cim.md) (what the
form authors).

**Depended on by:** none yet.

## 9. Revisit triggers

- A reader has to operate this console on a screen narrower than 1,180 CSS pixels.
- The inspector column grows tall enough that the class list and the symbol stack can
  be a screen apart — measured, not judged: the selected row's `getBoundingClientRect`
  falling outside the list's own box while the panel names it.
- The empty screen is measured to be a step people click through without reading —
  the sign would be layers stored straight from the generated document with no edit
  in between.
- A second screen needs a width floor. One is an exception; two is a layout policy
  and needs deciding as one.

## 9a. Amended the same day, by a revised handoff

The owner revised the bundle a few hours after the first application. Four changes, all of them
about the same complaint the ADR was written for — the rail was still a wall of controls, and the
route into it went through a page with nothing on it.

**The layer list moved into the rail, and the segmented switcher in the title strip is gone.**
Which layer you are editing is the editor's *first question*, not a place — so it belongs in the
column where the renderer, the symbol sets and everything else about appearance are chosen. Each
entry carries a geometry swatch, `id · name` and *Authored · n classes* / *Generated · version 0*,
which is what the removed tab used to say. The strip gave the room back to the breadcrumb.

**The service page's Symbology tab is a link, and its panel is deleted.** It drew one row per
layer whose only control was an *Edit* link — an indirection with nothing in it, and at four
layers still a page you pass through rather than work in. The tab opens the editor directly;
`?tab=symbology` in an address redirects, because that link already exists in the world.

**Two sections of the rail fold shut and summarise themselves.** *Vary with a number* says
*nothing* or *its colour, by population*; *Symbol sets* says *6 for lines*. They were the two
blocks a reader has usually not asked for, and a fold that says nothing about itself only moves
the cost.

**The service-wide style override moved to the foot of the rail**, as three lines of prose and a
*Write one…* that opens the document. It is a MapLibre document for the whole service — an expert
control most services do not have — and as a panel with a textarea standing open it outweighed the
one layer the page is about.

**One departure, and it is a defect the revision created.** The prototype's generated screen covers
all three columns; ours covers two. Moving the layer list into the rail made the third column the
only way to the service's other layers, so on a three-layer service whose first layer is unstyled,
covering it would put the other two behind a sentence that is not about them. What the generated
screen exists to withhold is *the claim about this layer's appearance* — the picture and the
inspector. Which layer you are editing is not that claim.

**Also corrected here**, because the revision named it: an Overview row's *Data* button opened
whichever layer the Data tab chose for itself, so on a four-layer service all four rows opened the
same table. Each of the three buttons carries its own layer now. A group layer gets a sentence —
*its children carry the symbology* — rather than three buttons that would fail on press.

## 9b. The layer the tab opens, decided 2026-09-05

**The Symbology tab opens the service's first drawable layer, and that is the decision rather
than a default nobody chose.** A design review escalated it above the visual inconsistency the
owner had asked about: on `ci_EarlyAlert`, which holds three, the tab always lands on `_sites`,
so somebody who came to restyle `_routes` is put in front of the wrong document without being
told. Two alternatives were put to the owner — remember the layer they last looked at on that
service, or open with nothing chosen and ask — and both were declined. *ilk katman seçili olsun.
tek katman varsa o zaten seçili olsun.*

**What makes it defensible is the rail.** The list of the service's layers is the first thing in
the editor's own left column, the chosen one carries a 2px accent edge, and each entry says
whether it is authored and into how many classes. The screen is not silent about which layer it
opened; it names it, marks it, and offers the others one click away. That is the difference
between a default and a guess.

**Measured on every entry path, 2026-09-05:** from the tab, the first layer; from an address or
an Overview row, the layer that was asked for; on a one-layer service, that layer. The third is
not an exception to the rule — a caller who names a layer has already chosen.

## 10. Dissent

**Recorded, and it is about §3's second paragraph.** Reversing a one-day-old repair
on the strength of a design handoff is a process this repository is supposed to make
difficult, and it did not: D-217 was written after a design review reproduced a
specific fault, and it was undone without reproducing the fault again under the new
arrangement. What was done instead — asserting the property rather than the mechanism
— is the better test, and it was written after the change rather than before it.
The counter-argument, which did not win, is that the handoff is the owner's own
design and the owner is also the person the complaint came from.
