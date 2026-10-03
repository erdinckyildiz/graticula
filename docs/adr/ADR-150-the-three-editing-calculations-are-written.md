# ADR-150 — autoComplete, reshape and trimExtend are computed

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `MEDIUM` — each is checked on the cases the specification describes; ArcGIS's handling of every degenerate case is not reproduced |
| **Decided** | 2026-10-03 by owner decision, asked directly (*"Evet, yap"*) — [Q-99](../open-questions.md) |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-022](ADR-022-geometry-server.md) (§2b's refusals) |

---

## 1. Context

Q-99 found on 2026-09-09 that ArcGIS's `autoComplete`, `reshape` and `trimExtend` are calculations on the geometries in
the request, not edits to stored features, and that GeometryServer refused them only because they were unwritten. The
owner asked for them to be written.

## 2. Alternatives considered

### Alternative A — In the overlay worker, from NTS (chosen)

All three run in the overlay worker, under the deadline and heap limit every other overlay has, and answer in
ArcGIS's shapes:

- **`autoComplete`** (`polygons`, `polylines` → `geometries`): the polygons' boundaries and the lines are noded together
  and polygonized; the faces not already covered by a polygon are returned.
- **`reshape`** (`target`, `reshaper` → `geometry`): the reshaper must cross the target at least twice. For a polyline,
  the part between the first and last crossing is replaced by the reshaper's part between them, in the target's
  direction. For a polygon, the shell's two arcs between the crossings are each closed by the reshaper's part, and the
  valid one with the larger area is kept, with the holes it still contains.
- **`trimExtend`** (`polylines`, `trimExtendTo` → `geometries`, one per input): a line the guide crosses keeps its
  parts to the left of the guide's direction; one it does not cross has each end extended along its last segment to the
  guide, where that reaches it; one it can do neither to is answered as an empty polyline, as the specification says.
  `extendHow`'s flags are not read.

### Alternative B — In PostGIS

**Against:** PostGIS has no reshape or extend; the worker already holds the noding and polygonizing these need.

## 3. Counterarguments to the preferred option

- *ArcGIS's choice between a reshaped polygon's two sides* is documented as keeping the larger; a reshaper that would
  make both invalid is refused with that sentence rather than guessed.
- *`extendHow` is ignored*, so a caller asking not to extend one end gets both extended.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Two squares and two lines closing the gap between them give one 100-unit polygon; a square reshaped outward gains its 6 × 5 bump; a line reshaped keeps its ends; a crossing line is trimmed to x = 5, a short one extended to it, and a parallel one answered empty | `GeometryServerConformanceTests` | this repository |

## 5. Decision

As §2. `findTransformations` is the one operation GeometryServer refuses, waiting on PROJ ([Q-100](../open-questions.md)).

## 6. Consequences

**Positive.** Every geometry calculation ArcGIS's GeometryServer offers is answered, but one.

**Negative.** `extendHow` is not honoured.

**State.** None.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | The three are wanted | Stated by the owner, 2026-10-03 |

## 8. Dependencies

**Depends on:** ADR-022.

**Depended on by:** —

## 9. Revisit triggers

- A client relying on `extendHow`.
