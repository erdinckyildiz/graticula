# ADR-054 — The symbology document is not bounded

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `HIGH` — `MEDIUM` on what replaces the bound until 2026-09-05, when condition 1 was measured |
| **Decided** | 2026-09-05, by owner decision |
| **Supersedes** | [ADR-033](ADR-033-symbology.md) §7 condition 5 |
| **Superseded by** | — |

---

## 1. Context

A layer's stored symbology could be at most **262,144 characters**, enforced by a check
constraint added in migration 23 and mirrored by a constant so that a refusal could name
the number. [ADR-033](ADR-033-symbology.md) §7's fifth condition asked
for exactly that, and the reasoning it gave was sound about *where* a bound belongs: one
that lives only in the application is one the next writer bypasses.

It was wrong about the number, and it said so in its own words. Migration 23's comment
reads: *256 KB is high enough never to be met by a real style.* Turkey has 81 provinces
and the owner's own layer has **1,394 place names**. A colour per province is an ordinary
map. A colour per place name is a map somebody actually wants. Both meet the bound.

The number had already been wrong once in a way that cost a day. The class ceiling of 256
was derived from it, on a measurement of **478 characters a class for a polygon and 690
for a point** — both taken against Esri's `drawingInfo`, when what is stored is CIM. The
same 256 classes measure **165,470** characters as CIM: **646 a class**, not 274. The
derivation was rebuilt in ADR-052 §3.12 and the ceiling stayed.

The owner's decision, 2026-09-05: *şu 200 limiti mantıklı değil. istemiyorum. limitleri
kaldıralım.* Asked which of the several limits they meant, they chose this one — the
262,144-character bound on the stored document.

## 2. Alternatives considered

### Alternative A — Raise the number

**Argument for.** The cheapest change there is: one constant, one migration, no new
reasoning. A megabyte would hold about 1,600 CIM classes, which covers the owner's 1,394
place names and every map anybody has asked for. The bound keeps doing the job ADR-033
gave it — the column cannot become a place to store something else.

**Argument against.** It moves the wall rather than removing it, and the next person to
meet it meets it with less warning, because a number that has been raised once reads as
considered. There is no principled figure: 262,144 was chosen because 256 classes fit
under it, and any replacement would be chosen because the largest layer anybody has
mentioned fits under it. That is not a bound, it is a memory of the last complaint.

### Alternative B — Bound the class count instead, and leave the column alone

**Argument for.** What actually goes wrong at scale is a legend nobody can read and a
renderer with more classes than colours, and both are counted in classes rather than in
characters. A limit expressed in the unit of the thing being limited is one an author can
reason about — *this map may have 2,000 classes* is advice; *this document may have
262,144 characters* is arithmetic somebody has to do.

**Argument against.** It is the same ceiling wearing better clothes, and ADR-052 §3.12
already settled that the readability of a large classification is the author's business
and not the server's. The number would still be invented.

### Alternative C — Remove the bound and add nothing

**Argument for.** It is what was asked for and it is honest: the server stops having an
opinion about how big somebody's map is.

**Argument against.** Nothing then stops a request putting a hundred megabytes in the
column, and the document is read on the drawing path — `MapServerEndpoints` and
`VectorTileEndpoints` both derive from it per request. A single pathological write becomes
a permanent cost on every tile.

## 3. Counterarguments to the preferred option

**The constraint was load-bearing and this is how columns rot.** ADR-033's argument was
that an unbounded `text` column becomes a place to put something else, and it is a real
failure mode. The answer here is that the column is not unbounded in the way that argument
imagines: nothing can reach it except this endpoint, and the endpoint bounds what it will
read. What is removed is the second, smaller bound behind the first.

**A bound in the application is one the next writer bypasses — ADR-033 said so and it is
still true.** It is. The bound that remains is in the application. If a second writer to
`layer.symbology` is ever added, this decision has to be revisited rather than inherited,
and that is written into §6.

~~**Nobody has measured what a five-megabyte document costs on the tile path.** Nobody has.
That is the honest state and it is why the confidence on *what replaces it* is `MEDIUM`
rather than `HIGH`.~~ **Measured 2026-09-05 — §7 condition 1.** The tile path does not pay for
classes at all: it composes a *style* from the symbology and a tile carries geometry, so 1,177
classes are 74 KB fetched once. The drawn path did pay, three times over, and the reason was an
evaluator that walked every class for every feature rather than the document's size. That is
repaired and this paragraph is struck rather than deleted, because the counterargument was
right to be made and the answer is not the one it expected. The read bound keeps the worst case
to two megabytes, which is eight times what was possible before rather than unbounded.

## 4. Evidence

Measured 2026-09-05 unless stated.

| | |
|---|---|
| CIM cost of one unique-value class | **646 characters** (owner's place names, point symbols) |
| 256 classes as `drawingInfo` | **72,986 characters** |
| 256 classes as CIM | **165,470 characters** |
| Old stored bound | 262,144 characters ≈ **406 CIM classes** |
| Read bound that remains | 2,097,152 characters ≈ **3,246 CIM classes** |
| Owner's largest field | **1,394** distinct place names |

Two faults were found by removing the ceiling, both of which it had been hiding:

- **The palette ran out at 727 and said nothing.** `Distinct(n)` fills from a seven-colour
  palette and then a 720-candidate grid, and stopped when the grid was empty — returning
  fewer colours than classes. The caller indexes that list by class, so the **728th class
  threw**. Unreachable while the ceiling was 256; the owner's own layer is 1,394.
- **Choosing 256 colours took 2.0 seconds, inside the request.** The distance between two
  colours was computed with three `Math.Pow` calls and a `Math.Sqrt`, and the one caller
  only compares the results against each other. Squared distance is order-preserving:
  **372 ms** for a thousand colours now, against 7.1 s to fill the whole grid before.

## 5. Decision

**The stored symbology document has no length bound.** Migration 38 drops
`layer_symbology_is_bounded`. `SymbologyConversion.Serialise` no longer measures what it
produces. `GenerateRendererEndpoints.MostValues` is deleted: a classification takes every
value it read.

**One bound remains and it is about this server rather than about a map.**
`SymbologyConversion.MaximumReadCharacters` — 2,097,152 — is what will be read and parsed
in one request. It is the same number the request already carried as *eight times the
stored cap*; what changes is that it now stands on its own reason instead of on
arithmetic against a bound that no longer exists.

**The classifier fits its answer to that bound rather than to a class count.** It builds,
converts, weighs, and builds again with fewer classes if the result would not survive
being sent back. This is the loop that used to fit the stored cap, pointed at the only
number left — because a renderer this endpoint returns is stored by the console in the
very next request, and generating one that cannot be stored is an answer that fails on
arrival.

## 6. Consequences

- **A field with more distinct values than a request can carry is still truncated**, into
  an `Other` class with its own count, exactly as before. What changed is the number: about
  3,200 rather than 256.
- **Colours past the first 256 are cheaper.** The greedy chooser that picks each colour
  furthest from the ones already taken is capped there — it is quadratic, and *visibly
  distinct* stops being perceptible long before a thousand classes. Past it, a golden-angle
  walk with cycling lightness and saturation, which cannot exhaust and costs nothing.
- **The document is read on the drawing path**, and what that costs is now measured rather
  than feared — §7 condition 1. `VectorTileServer` turns it into a style and pays nothing per
  class; `MapServer` draws it, and paid **1.3 seconds of a 2-second country-wide picture** to
  an evaluator that walked every class for every feature. A keyed table took the same picture
  to 0.80 s. Nobody could produce a document this large before this decision, which is why the
  fault was reachable only after it.
- **State.** Nothing new. This decision removes a constraint from `layer.symbology`, a
  column ADR-033 already put in the catalogue; it adds no column, no cache and no runtime
  state of its own. What it changes is how long one existing shared value may be.
- **If a second writer to `layer.symbology` is ever added, this decision is revisited.**
  ADR-033's argument for a constraint rather than a guard was that a guard protects only
  the paths that call it. That is true, and it is survivable only while there is one path.

## 7. Conditions

1. **A layer with more than a thousand classes is drawn, and the tile path is timed
   against one that has ten.** The consequence above is stated and unmeasured; this is the
   measurement. Until it exists, the `MEDIUM` on this decision's confidence is doing real
   work. **DISCHARGED 2026-09-05**, on the owner's own `tr_ilce` — 25,280 polylines, 1,177
   classes — and it found a fault rather than confirming a guess.

   | | |
   |---|---|
   | Tile face, style document | **74,048 characters, 90 ms** — three `match` expressions of 1,177 stops each |
   | Tile face, z4 (whole country) | 902 KB, **403 ms** |
   | Tile face, z8 / z10 / z12 | 65 ms / 26 ms / 28 ms |
   | Drawn face, 1600×800 of Turkey | **1.90–2.04 s**, and **0.80–0.83 s** after the repair below |
   | The same picture with a simple renderer, 46,041 features | **0.56–0.88 s** |

   **The tile path does not pay for classes at all**, and that is the answer to the half of
   this condition that named it: `VectorTileEndpoints` uses the symbology to compose the
   *style*, and a tile carries geometry and attributes. A client applies the classification.
   So the cost of a thousand classes there is 74 KB fetched once.

   **The drawn face did, and the reason was the evaluator rather than the data.** A layer with
   **twice the features and no classification** drew in a third of the time. `StyleExpression`
   answered a `match` by walking every case and asking `Same` about each label — three paint
   properties × 25,280 features × up to 1,177 labels, about 45 million comparisons for one
   picture. At the 256 classes this decision removed the cap on, that walk costs a fifth as
   much and nobody could have met it. **A table keyed the way `Same` compares** takes the same
   layer to 0.80 s for a byte-identical picture; measured in isolation, 200,000 evaluations of a
   2,000-class match went from **20,361 ms to about what 8 classes cost**.

   **What the table refuses is as much of the repair as what it holds.** `Same` compares two
   doubles as numbers and everything else as invariant text, and those rules disagree about
   `-0.0`; a match carrying a zero, or a null label, keeps the walk, because a table cannot
   reproduce a comparison that is not transitive and *nearly the same answer* is worse than
   none.
2. **A second writer to `layer.symbology` reopens this.** Not a task — a trigger. It is
   written here so that whoever adds one finds it.
