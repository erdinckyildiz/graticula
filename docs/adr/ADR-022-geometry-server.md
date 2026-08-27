# ADR-022 — GeometryServer ships in halves, and the projection engine is the datastore's

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` · **§2 SUPERSEDED by §2b, 2026-08-15** |
| **Confidence** | `HIGH` for the split · `HIGH` for the engine · `MEDIUM` for the vertex cap |
| **Decided** | 2026-08-14 |
| **Rests on** | [A-042's invalidation](../architecture-assumptions.md) · [benchmarks/geometry-overlay](../../benchmarks/geometry-overlay/RESULTS.md) |
| **Defers** | the overlay half to [Q-97](../open-questions.md) |

---

> **Scope note, 2026-08-18 — v1 serves PostGIS only, and the other engines are
> deferred rather than cut.** This decision reasons about several database engines.
> Owner decision: *"Şimdilik postgis ile gideceğiz. Sonra diğer db'ler eklenecek. V1'de
> sadece Postgis olarak kalabiliriz."* — [v1-scope](../v1-scope.md) §3a, which is the one
> place that says what the deferral means.
>
> **The multi-engine reasoning here is kept on purpose**, because it is what the second
> engine will be built from and because deleting it would make it be re-derived later
> from nothing. What it is not is a description of what v1 does. Where a sentence below
> reads as *the server supports Oracle today*, it has been corrected; where it reads as
> *this is how several engines would be supported*, it stands and waits.
>
> [D-27](../architecture-debt.md).

## 1. Context

[v1-scope](../v1-scope.md) records GeometryServer as *crucial* on the owner's
say-so, described there as "a thin surface over PROJ and NetTopologySuite — with
the caps A11 demands".

A-042 was the assumption those caps rested on: that limits on vertex count, batch
size and wall clock make a public overlay endpoint safe. It is
[invalidated](../../benchmarks/geometry-overlay/RESULTS.md). A 6,408-vertex
adversarial input costs 153 seconds and 16.7 GB where a real 72,919-vertex
national outline costs 312 ms and 17 MB — and the benchmark run that produced it
pushed the host into swap and killed the Docker daemon.

So the ADR that was going to be *here is GeometryServer* is instead about what
can ship without a known way to take the server down.

---

## 2. Decision — the surface splits by cost shape, not by usefulness

> **Superseded on 2026-08-15 by [§2b](#2b-decision-reversed-by-the-owner--the-server-bounds-cost-it-does-not-decide-usefulness).**
> The reasoning below is left intact rather than rewritten: it is what was
> believed before the question was put to the owner, and the ADR is worth less
> if it only ever records the answer that survived.

**Shipped:** `project`, `areasAndLengths`, `lengths`, `labelPoints`.

**Refused with 501, pending Q-97:** `intersect`, `difference`, `union`, `cut`,
`buffer`, `offset`, `relation`, `autoComplete`, `reshape`, `trimExtend`,
`convexHull`, `simplify`, `densify`, `generalize`, `distance`.

The line is **whether the work is linear in the input**. Everything shipped is
one pass over the coordinates. Everything refused either runs general overlay or
can produce output unbounded by its input.

**Some of the refusals are more conservative than they need to be**, and that is
deliberate for now. `convexHull` is O(n log n) and safe; `generalize` is
Douglas–Peucker, whose worst case is quadratic but whose realistic case is not;
`densify` is linear in output and unbounded only because the caller sets the
segment length. Each could be argued back in individually. None has been
measured, and after A-042 the standard for saying *this one is fine* is a
measurement rather than an argument about asymptotics.

**Refused, not absent.** A missing route answers 404, which a client reads as
*this server has no GeometryServer*. The 501 names the operation, states the
measurement, and points at Q-97.

---

## 2b. Decision reversed by the owner — the server bounds cost, it does not decide usefulness

**Decided 2026-08-15 by the project owner, in their own words:**

> *tehlikeli ya da değil. Sen onu kullanıcıya bırak. öyle saçma bir şey
> yapacaksa yapsın. timeout koyarız her bir servise. bir request o süreyi
> geçerse timeout olur.*
>
> ("Dangerous or not — leave that to the user. If they want to do something
> absurd, let them. We put a timeout on each service; a request that runs past it
> times out.")

§2 drew its line at *whether the work is linear in the input*, and refused nine
operations on the grounds that they might be expensive. **That is a judgement
about what a caller should want, dressed as a safety property.** The owner's
ruling is that it is not the server's to make. What the server owes its operator
is a bound on cost. What it owes its caller is the operation.

### What actually changed, and what did not

**The bound did not change, because the bound was never about which operation it
was.** Q-97 built a worker process with a ten-second deadline and a 1 GB heap
ceiling, and both hold for a buffer exactly as they hold for an intersection. Six
operations were sitting outside a door that was already unlocked.

**Six moved from refused to supported**, all through that worker:

| | |
|---|---|
| `cut` | Polygonised from the target's boundary noded with the cutter; faces outside the target dropped |
| `buffer`, `offset` | NTS `Buffer` and `OffsetCurve` |
| `simplify` | NTS `GeometryFixer` — ArcGIS `simplify` is *make this valid*, which is what this is. Vertex reduction stays `generalize` |
| `relation` | DE-9IM, and Esri's named relations where NTS has the predicate |
| `distance` | `DistanceOp` |

**The pre-flight is off by default.** It counted candidate segment pairs and
refused above 100,000. It was measured leaky when it was introduced — finding 16
had it under-predicting an adversarial input by fourteen times — so it was never
the bound, and a filter that both leaks *and* turns real work away is the worst
of the two options. It survives as a constructor argument for an operator who
would rather refuse a heavy request in 80 ms than spend ten seconds on it. That
is a cost optimisation and is now labelled as one.

**Ten seconds of a worker is the price**, and it is worth stating plainly. The
adversarial comb pair used to be refused in 80 ms and is now attempted and
killed. The conformance test that asserted `400 TooLarge` now asserts
`503 Deadline` and that the server is still serving. Both properties that matter
survived; what changed is who pays for the caller's choice, and the owner chose.

### The one thing a timeout does not do

**A deadline answers 153 seconds. It does not answer 16.7 GB.** That figure is
not slowness — it is an allocation the host cannot survive, and the run that
produced it took the machine into swap and killed the Docker daemon. A request
can exhaust memory well inside any timeout worth having.

This is recorded because the instruction was *"we put a timeout on it"*, and a
timeout alone would not have been enough. It is implementable today only because
Q-97 already built the other half: the work runs in a process with a hard heap
limit, and that process dies instead of the machine. **Both bounds, or neither.**

### The bounds are settings, and until 2026-08-17 they were constants

The owner, asking what this service's controls are: *"geometry server'in, startı stop'u, timeout'u
vs si yok mu?"* The timeout existed — ten seconds — and was **compiled in**. `GeometryWorkerPool`
had taken a deadline and a pre-flight threshold as constructor arguments since it was written, with
a comment saying an operator may still want the pre-flight; `Program.cs` passed neither. So the
constants were the only values any deployment could have, and the comment described a choice
nobody could make.

**Their own instruction is what makes a fixed deadline wrong.** §2b records it: when they removed
the rule refusing six operations for being *potentially* expensive, the instruction was *let them,
and put a timeout on it.* A timeout the operator cannot move is half of that — it is still the
server deciding, one level up.

- `Graticula:OverlayDeadlineSeconds`, default **10**. The default is unchanged and the reason is
  §2b's measurement rather than taste: every real case finished inside 350 ms and the smallest
  adversarial input that matters took 17 seconds, so ten leaves real work thirty times its measured
  cost and still refuses the attack. A deployment that would rather wait, or rather not, can say so.
- `Graticula:OverlayPreflightPairs`, default **0** meaning off. Kept reachable rather than deleted
  because a deployment that would rather refuse a heavy request in 80 ms than spend the deadline on
  it can choose that; it is off by default because it was measured under-predicting fourteenfold.

**The service document reports what is enforced, not what is compiled.** `EnforcedDeadline` and
`EnforcedPreflightPairs` are read off the pool. Said as a decision because the alternative was one
line shorter and would have made the document state a number the server does not use — the same
fault as a control displaying a figure it did not read, which cost two separate defects on the same
day this was written.

**And both are writable through the admin API, after the owner rejected the deferral.** The first
version of this made them configuration-file settings only, and recorded the API version as an open
question on the grounds that changing a deadline live would mean rebuilding the worker pool under
in-flight requests. The owner: *"iyi de neden yok. yani ben neden max timeout süresi
tanımlayamıyorum?"* — fine, but why not; why can I not define a maximum timeout. **They were right
and the stated reason was false.** The pool applies both bounds *per operation*, so only the number
ever had to move; nothing is rebuilt and nothing is restarted. The deferral was caution about work
nobody needed to do, and it is left recorded here rather than quietly replaced, because a reason
that turns out not to exist is worth being able to see.

- `GET`/`PUT /admin/services/{name}/limits`, under `admin:manageServer`. Stored on the service
  (migration 20), **null meaning nobody has said** — the same three-way rule as a layer's cache TTL,
  so an administrator gets the server's default back by clearing the field rather than by typing a
  copy of it that stops tracking the setting.
- The `GET` reports the stored value, the default, **and the effective value**, because those are
  three different facts and a screen that cannot tell *set to ten* from *defaulting to ten* is the
  fault this endpoint exists to remove.
- Out-of-range is **refused rather than clamped**: a clamp would leave an administrator believing a
  number the server is not using.
- **Measured end to end.** The two-comb corpus at 200 teeth costs 130,324 candidate pairs and about
  4.8 s. With the deadline set to 2 it is cut off at 2,021 ms and the refusal says *"ran longer than
  2 seconds"*; with the pre-flight set to 100,000 it is refused in 31 ms naming the count it
  measured; with the pre-flight at 200,000 the same work completes. Raising the deadline to 30 for
  the 400-teeth case does not make it succeed — it dies on the **heap ceiling** instead, at
  10.6 s, which is *both bounds, or neither* demonstrated rather than argued.

**A defect this found in its own first version, recorded because it is the class this project keeps
producing.** That first version put the deadline on the request and left the pre-flight reading the
pool's field. So `PUT` stored a threshold, the service document advertised it, and the engine ignored
it: a request measuring 130,324 pairs was computed against a stored threshold of 100,000. **A setting
that is stored and reported and does nothing is worse than one that does not exist**, because a
deployment believes it is protected. Found by measuring against a running server, not by reading the
diff, and `RequestBoundsTests` is now the cheap test that would have caught it.

### The other three controls on the reference's pooling page — added 2026-08-17

The owner, with a screenshot of ArcGIS Server Manager's *Pooling* page for its own geometry service:
*"e bunlar güzel örnekler değil mi?"* — aren't these good examples? They are. Five controls, and
mapping them honestly is worth more than adopting them:

| Theirs | Ours, before | Ours, now |
|---|---|---|
| Max time a client can **use** a service (600 s) | the overlay deadline, compiled in | settable per service (above) |
| Max time a client will **wait** to get a service (60 s) | **the work's deadline**, one number doing two jobs | its own budget, settable |
| Max time an **idle instance** can be kept (1800 s) | **nothing** — a worker was kept for ever | reclaimed after 30 min, settable |
| **Minimum** instances per machine (1) | `OverlayWorkers`, one number | unchanged, and see below |
| **Maximum** instances per machine (2) | the same one number | unchanged |

**The wait was the subtler gap, because the code already argued for the fix.** The comment beside
`_slots.WaitAsync` said *"a caller queued behind two long overlays should be told the server is busy,
not left holding a connection for a minute"* — and then passed the work's deadline as the wait. So a
deployment that wanted long work had to accept long queueing, which is exactly the trade their two
boxes let an operator refuse. **Measured:** one worker, a 30-second work deadline and a 1-second wait
budget, three concurrent 200-tooth overlays — the third is refused after **1,053 ms** naming the
budget it exhausted, while the other two complete in 4.3 and 4.6 s. Under the old arrangement that
third request would have held its connection for up to thirty seconds.

**The idle reclaim was a plain absence.** A returned worker went into a `ConcurrentBag` and came out
again for ever, so a deployment that ran one overlay at nine in the morning held two worker
processes — each able to have grown to its 1 GB ceiling — until it was restarted. A timer now sweeps
every thirty seconds and disposes workers idle past the budget; a rented worker is not in the bag, so
the sweep needs no coordination with the compute path at all. **Measured:** with the budget at 35 s,
one trivial overlay leaves two processes, and both are gone within a sweep — `Get-Process` reports 2,
then 0, and the log records *"Reclaimed 2 geometry worker process(es) idle for more than 35 s"*.

**And the cost of reclaiming was measured after being guessed wrong.** The first version of this
paragraph said a cold start costs about 60 ms. It does not: the same trivial overlay took **674 ms
cold against 26 ms warm**, of which the overlay itself was 112 ms and 1 ms — the rest is process
launch, runtime start and first-call JIT. Recorded rather than corrected silently, because writing a
number before measuring it is the thing §3 exists to stop and it happened here. Half an hour remains
the right default *at* 650 ms: a deployment quiet that long is not one whose next request is measured
in milliseconds, and `RentAsync` warms outside the caller's **deadline** — though not outside their
wall clock, which the first version also implied and which is the same mistake twice.

**The two that did not transfer, and why not adopting them is the decision.** Their minimum and
maximum instances per machine describe an elastic pool; ours is one number, so minimum equals
maximum by construction. Making it elastic means a supervisor deciding when to grow and shrink, and
§82's question — *what concrete problem does this solve* — has no answer here yet: the pool exists to
**bound** memory, and its worst case is `OverlayWorkers` times the heap ceiling whether the processes
are running or not. *Per machine* does not transfer at all, since this is one process. Recorded so
the absence reads as a choice rather than an oversight.

**What is still not a setting, and why:**

- **The 500,000-vertex cap.** §3's argument is that a cap is the right mechanism *here* because
  every operation on this half is one pass over the coordinates, so input size bounds work exactly.
  Nothing about it is a deployment preference — it is generous enough that no ordinary request
  reaches it and its purpose is that a request cannot be unbounded.
- **The 1 GB heap ceiling.** It is the other half of *both bounds, or neither* above. Total exposure
  is `OverlayWorkers` times this ceiling, and `OverlayWorkers` is already a setting, so the
  operator-facing number already exists. The 400-teeth measurement above is what makes leaving it
  fixed safe: an administrator who raises the deadline has not removed the memory bound.
- **A seconds-timeout on the in-process half, and this one is a gap rather than a decision.** Their
  *maximum time a client can use a service* governs every request; ours governs overlay only. The
  seven in-process operations — `project`, `areasAndLengths`, `lengths`, `labelPoints`, `convexHull`,
  `densify`, `generalize` — are bounded by the vertex cap and by nothing in seconds. §3's argument
  for the cap holds and is why this has not mattered, **but the honest reason it is not simply added
  is that it could not be enforced**: none of `GeometryOperations` takes a `CancellationToken`, so a
  timeout there would abandon the response while the CPU kept burning. That is worse than no timeout,
  because it looks like protection. Fixing it means either threading cancellation through those
  algorithms or moving them behind the worker — a real choice, recorded as
  [Q-115](../open-questions.md) rather than papered over with a timer that stops nothing.

### Verified rather than asserted

NetTopologySuite being mature is evidence about NetTopologySuite, not about the
code in this repository that calls it — and that is where both defects found on
the way here lived. Five oracle tests check the new operations against PostGIS on
real geometry from the datastore: `ST_Distance` exactly, `ST_Buffer` by area
within one per cent, `ST_IsValid` on a repaired bow-tie, area conservation across
the pieces of a cut, and `ST_Intersects` on every pair of a cross product
including the ones that must not match.

**The two defects, both found by running the thing rather than by reading it:**

- **A cut returned one geometry instead of two.** `BuildGeometry` turns a list of
  polygons into a MultiPolygon, which the worker's flattening step deliberately
  keeps whole — so a square cut in half came back as one shape with two rings. A
  cut's entire output is the separateness of the pieces.
- **A named relation was sent to the topology engine as a DE-9IM pattern**, which
  answered `Should be length 9: esriGeometryRelationIntersection`. Esri's names
  are now resolved to NTS predicates, and three of them — intersects, touches,
  crosses — have no single DE-9IM pattern at all, so writing them out as one each
  would have been wrong in exactly the cases the predicate exists to catch.

**Four of Esri's relation names are refused rather than approximated.**
`InteriorIntersection`, `LineCoincidence`, `LineTouch` and `PointTouch` are
refinements whose exact semantics are Esri's rather than OGC's. A wrong spatial
predicate is not a degraded answer — it is a caller filtering the wrong features
and never finding out. They point at `esriGeometryRelationRelation` with an
explicit pattern.

### What is still refused, and it is no longer about cost

`autoComplete`, `reshape` and `trimExtend`. All three edit existing features
rather than calculating on the geometry the request carries, and whether they
belong on GeometryServer or on FeatureServer is an open design question —
[Q-99](../open-questions.md). Saying "too expensive" about them would be the same
lie in a new place.

**18 of 22 supported, 4 refused with reasons** — see §2c for the three that
were not on any list until the owner compared this service with a real one.

---

## 2c. The three nobody had listed — grid strings ship, transformation paths do not

The owner's comparison turned up three operations that were **neither supported
nor refused**: `toGeoCoordinateString`, `fromGeoCoordinateString` and
`findTransformations`. A caller asking for any of them got 404 — the answer for
an operation that does not exist, given for three that do. The refusal list had
been treated as the record of what is missing, and it was only a record of what
somebody had thought about.

### The two string operations ship, computed in process

> **Amended 2026-08-23 by [D-114](../architecture-debt.md), and the amendment is this
> section admitting §4 against itself.** *UTM is a closed-form series* is how a second
> coordinate engine came to sit in Tier 1 — 92 lines of transverse Mercator forward and
> inverse — under a reclassification that made §4 appear not to apply. The independent
> §66 simplicity gate named it as its disqualifying finding, in §4's own words: *two
> coordinate engines with two EPSG datasets and two sets of grids, differing by metres on
> exactly the cadastral and survey work where metres are legally significant — and
> differing silently, because both would answer.*
>
> **The series is deleted.** The UTM leg goes to the datastore's PROJ, into the EPSG:326nn
> and 327nn codes this section already compared against, and what stays in process is the
> notation: which zone a coordinate falls in, the hundred-kilometre lettering, the band
> letters, the packing and the angular formats. PostGIS has no function for any of those.
>
> **So the paragraph below is now half true and the half that changed is the important
> one.** A round trip to the datastore does not cost more than the entire conversion; it
> *is* the conversion, for the three grid notations. A caller already in 4326 no longer
> pays nothing — it pays one projection per UTM zone the batch touches, which is the price
> of there being one engine. Verified end to end in both directions to sub-metre.

They write and read **DD, DDM, DMS, UTM, MGRS and USNG**. This is §4b's rule
applied without exception: the input is a coordinate pair in the request, UTM is
a closed-form series and MGRS is a lettering scheme over it, so a round trip to
the datastore would cost more than the entire conversion.

**The one thing that does go to the datastore is the datum**, and only when it
has to. A caller already in 4326 pays no round trip; anything else is projected
by the datastore's PROJ first, and the response names the engine — the same
provenance rule `project` follows, for the same reason.

~~**Verified against PROJ to the millimetre**, on ten places chosen to be cases
rather than samples: both hemispheres, the equator on a central meridian, the
widened zone 32 over Bergen, the rearranged Svalbard zones, and a point on a zone
boundary. The transverse Mercator series agrees with PostGIS's own transform to
EPSG:326nn and 327nn within a millimetre everywhere, which is three orders below
the one metre MGRS's finest form can express.~~ **That test retired with the series it
watched, 2026-08-23 — PROJ is the answer now, so pinning our answer against it would be
pinning it against itself.** What replaced it tests the part that stayed ours: the zone
rule, whose Norway and Svalbard exceptions are what a converter written from the
definition alone gets wrong, and the round trips, which now walk the projection too. DMS
is still checked against `ST_AsLatLonText`.

**The polar regions are refused rather than approximated.** Above 84°N and
below 80°S, MGRS is Universal Polar Stereographic — a different projection
with its own lettering. A UTM-based string there would be silently wrong, and a
wrong grid reference is discovered by somebody standing in the wrong place.

**Four things the tests caught that review would not have:**

- The first test compared *formatted* UTM strings against PROJ and reported
  half-metre disagreements that were entirely its own rounding. The unrounded
  numbers are now public for exactly that reason — a test that can only see
  whole metres cannot tell a correct series from one forty centimetres out.
- `addSpaces=false` on DMS produced `39560.2400N`, which nothing can read back:
  the parts are variable width. The flag now applies only to the grid notations,
  where the packed form is standard and fixed width.
- Packed UTM was unsplittable near the equator — a northing of 42 metres wrote
  as `42`. It is padded to six and seven digits now.
- **The rounding carry.** 39.99999999° at one decimal is 60.0 seconds, and a
  formatter that rounds each part separately emits `39 59 60.0` — a minute
  that does not exist. The rounding happens once, in the smallest unit, and
  carries upward.

**GARS and GEOREF are not written**, and the refusal says so rather than
implying they are not ArcGIS types. Both are simple cell schemes; this is a gap.

### findTransformations does not ship, and the reason is a decision nobody has made

It lists the datum transformation paths between two references, **ranked by
accuracy** — which is precisely the information geometry-crs-policy §3
says a caller needs, because the paths differ by metres and that difference is
legally significant for cadastral work.

The paths live in PROJ's own operation database. **This server does not have
PROJ**: §4 sends projection to the datastore to avoid shipping it, and
PostGIS exposes no SQL function that enumerates candidate operations. PROJ's
`proj.db` is right there inside the datastore container and unreachable from a
SQL connection.

**§4's reasoning does not obviously carry here, and that is worth saying
plainly.** §4 declined to ship PROJ *and its datum grids* — hundreds of
megabytes and a distribution commitment. `proj.db` is about 9 MB of metadata and
no grids at all. Reading it in process to answer a question, while still letting
the datastore perform the transformation, is a different trade from the one §4
rejected. It has not been made: **[Q-100](../open-questions.md)**.

**What was not done, and why it would have been worse than refusing.** We could
transform a probe point and report that it worked. That answers *is there a
path*, which is not the question; and dressing the single path PROJ happened to
pick as a ranked list of one would give a surveyor a number with no accuracy and
no alternatives, which is the exact failure geometry-crs-policy §3 exists to
prevent.

---

## 3. Decision — the vertex cap is 500,000, and it is sound here for a reason that does not generalise

A-042's actual error was applying one mechanism to two kinds of work. For a
single pass over coordinates, input size bounds the work exactly: 500,000
vertices is 500,000 units of work. For general overlay it bounds nothing, because
cost is set by how the geometries interact rather than by how large they are.

So the same cap that was useless on overlay is exactly right here, and the
distinction is written into the code rather than left as a number somebody might
reuse.

Half a million is about seven copies of the largest polygon in the test corpus
and roughly 12 MB of JSON. Generous on purpose: the cap exists so a request
cannot be unbounded, not to ration ordinary work.

---

## 3b. Amended 2026-08-25 — the cap bounds five of six, and the other two are bounded on the way out

**§3's argument is that each linear operation is one pass over the coordinates, so the
vertex cap bounds the work exactly.** [Q-115](../open-questions.md) asked whether that
class of operation needs a time bound as well. The argument had never been costed, so it
was — and it is true of five cases and false of two, for two different reasons.

**Measured at the cap, on input built to be hostile to each operation:**

| operation | worst input | at 500,000 vertices |
|---|---|---|
| `ConvexHull` | spiral, no interior point | **351 ms**, 91 MB |
| `ConvexHull` | circle, every point on the hull | **591 ms**, 98 MB |
| `Densify` | every segment split | **85 ms**, 76 MB |
| `Densify` | nothing to split | **19 ms**, 8 MB |
| `Generalize` | everything removable | **32 ms**, 0 MB |
| `Generalize` | **nothing removable** | **≈3 hours**, extrapolated |

**Densify's cost is its output, and its output is a caller's number.** A two-vertex line
one kilometre long at `maxSegmentLength=0.001` produces 1,000,001 vertices and 47 MB; the
input cap sees two vertices and waves it through, and `0.000001` was equally accepted.
The bound is therefore on the output and is computable in advance:
`GeometryOperations.DensifiedVertexCount` walks the same coordinates the operation would
and the endpoint refuses before a byte is allocated. Saturating arithmetic, so a count
too large for a `long` cannot wrap into a small one and pass the check.

**Generalize is quadratic on input chosen to defeat it.** Douglas-Peucker splits at the
farthest vertex and rescans the range; on a run where nothing may be dropped that is
`O(n²)`. Measured: 237 ms at 2,000 vertices, 708 at 4,000, 2,751 at 8,000, 10,169 at
16,000, 46,241 at 32,000 — a clean quadratic. The same operation on a circle of 32,000
vertices is 47 ms. So the work is counted rather than timed: 64 comparisons per vertex,
against the ~19 per vertex a well-behaved run of half a million costs, and the quadratic
case needs five orders of magnitude more than the budget.

**Counted, not timed, and that is Q-115's own reasoning.** Nothing in
`GeometryOperations` takes a `CancellationToken`, so a clock would abandon the response
while the CPU kept burning — a bound that stops nothing and looks like protection. A
count refuses deterministically, on the same input, on any machine, before the work
starts for densify and 1.28 million comparisons into it for generalize.

**What did not change.** No operation gained a time bound, no algorithm gained a
cancellation token, and the overlay half keeps the worker process and its deadline. §3's
argument stands for the five cases it covers; what it did not cover is now covered by a
different kind of bound rather than by a clock.

## 4. Decision — projection uses the datastore's PROJ

**Not a .NET projection library.** The datastore is mandatory
([Q-69](../open-questions.md)) and fused into the product
([ADR-019](ADR-019-portal-server-split.md)); it already carries PROJ, its shift
grids and the EPSG database.

**The argument is not convenience, it is that two engines disagree.** Adding a
.NET library beside PostGIS means two coordinate engines with two EPSG datasets
and two sets of grids, differing by metres on exactly the cadastral and survey
work where metres are legally significant — and differing *silently*, because
both would answer. A feature service that reprojects one way and a GeometryServer
that reprojects another is a defect nobody would find until a surveyor did.

**It also dissolves [Q-23](../open-questions.md)** rather than answering it.
That question asks whether a PROJ transformation object is thread-affine, because
it decides whether prepared transformations are shared or duplicated per worker.
Through PostgreSQL the question does not arise: each connection has its own.

**The cost is one round trip per batch**, which is invisible next to the HTTP the
request arrived on. This is not the tile path, where ADR-021 measured the same
trade and the datastore won there too.

**Consequence: a second connection pool**, because the platform store's
connection sets a search path that excludes the schema PostGIS lives in. That
exact defect had already been found and fixed once for the datastore
registration and arrived again by a second route within the hour, which is why
there is now one keyed pool that both callers use.

---

### 4b. Where an operation runs, and the rule that decides — added 2026-08-15

The owner put a real ArcGIS GeometryServer beside this one: **22 operations
there, 7 here**, and asked why. Three answers came out of it, and the first is a
rule that was missing.

**Push down when the data is already there; compute in process when the caller
brought it.**

[ADR-021](ADR-021-tile-encoding.md) pushes tile encoding into PostGIS and is
right to — the rows are already in the database, and a z16 tile read 201,580
vertices to emit 2,080, so pushing down is what *avoids* the traffic. A
GeometryServer request is the opposite shape: the geometry arrives in the request
body. Sending it to the database **creates** the traffic instead of avoiding it —
two more copies of every coordinate, WKB out and WKB back, plus a round trip, on
a system four benchmark rounds found to be bound by memory traffic rather than
CPU.

I proposed exactly that wrong thing first, and the owner refused it: *"I don't
use the datastore for every job — what has the datastore got to do with it?"*
Then, more precisely: *"if you do it on PostgreSQL you'll also have to deal with
converting the incoming ArcGIS geometry."* That conversion is not a nuisance
around the edge of the design; by our own measurements it **is** the cost.

**`project` remains the exception and is now marked as one.** It goes to the
datastore because the alternative is shipping PROJ and its datum grids (Q-15),
and because the accuracy is then the datastore's. Nothing in that reasoning
transfers to `ST_Distance`.

**Three operations moved from refused to supported**, computed in process on
flat arrays (ADR-003 §6a tier 2): **`convexHull`** (Andrew's monotone chain),
**`densify`** (interpolate, never move an original coordinate) and
**`generalize`** (Douglas–Peucker). That is condition 2 arriving with evidence:
they were refused on an argument about asymptotics, which this ADR already
called the kind of reasoning measurement overturns.

**PostGIS is the oracle, not the runtime.** They are verified against
`ST_ConvexHull`, `ST_Segmentize` and `ST_SimplifyPreserveTopology` on real
polygons from the datastore — hull vertices match exactly, and generalize agrees
with PostGIS on **47 of 50** shapes. Using a database to check an implementation
is a different thing from depending on one to run it, and it is the method
`WkbReader` used against 6.5 million polygons.

**Two defects were found by that comparison rather than by review.** Douglas–
Peucker on a closed ring started from a degenerate segment — first and last
coordinate are the same point, so the first split was chosen by distance from
that point rather than deviation from a chord, and on a notched square it dropped
a genuine corner. And the floor that stops a ring collapsing took the extreme x
and y vertices, which resolve ties to the same corner and produced a triangle
with half the area. **The test did not catch either, because it asserted a
ceiling** — ours no coarser than twice PostGIS's — instead of comparing shapes.
It now asserts an agreement rate.

**And every refusal now carries its own reason.** All twelve used to say *"it
needs general polygon overlay"*, which is true of `cut` and nonsense for
`distance`, a minimum over segment pairs that does no overlay at all. Nine
operations were being refused with a sentence written for a different one.
Telling a caller something untrue about why they cannot have a thing is worse
than the missing thing.

**What is still absent, and why**, so the gap is a known one:

| | |
|---|---|
| `buffer`, `offset` | Curve construction, bounded by the input unlike overlay. Not written; not unsafe |
| `cut` | Genuinely overlay. Belongs in the worker beside intersect, and is not there yet |
| `simplify` | ArcGIS `simplify` repairs topology. Offering `generalize` under that name would be the worst kind of compatibility |
| `relation` | DE-9IM against a topology engine. One exists in the worker; nobody has wired it |
| `distance` | O(n×m) over segment pairs, and the containment case needs point-in-polygon. Only not written |
| `autoComplete`, `reshape`, `trimExtend` | Editing operations over existing features, not calculations on the geometry sent |
| `toGeoCoordinateString`, `fromGeoCoordinateString`, `findTransformations` | **Were not even on the refusal list** until the comparison. *(Resolved §2c: the two string operations ship; `findTransformations` is refused and its route is [Q-100](../open-questions.md).)* |

**10 of 22 supported, 9 refused with reasons, 3 newly discovered.** *(Superseded within the day: §2b took it to 16, §2c to 18.)*

## 4b. Decision — the datum caution is told to the operator, not to the client

*Added 2026-08-25 by owner decision, closing [Q-141](../open-questions.md) and the residual
of [D-32](../architecture-debt.md).* The owner's words were *"Operatöre söyle — günlük ve
/admin"*.

**The question.** §4 chose the datastore's PROJ, and PROJ falls back to a ballpark
transformation when the shift grids for the accurate path are absent — without failing.
`GeometryServer`'s `project` has reported that since 2026-08-16, because it has a response
to put a caution on. The FeatureServer's `outSR` and the tile path do the same
transformation and report nothing: both reproject in SQL, inside the query the datastore
runs, so neither passes through `IProjector` at all.

**Q-141 listed three shapes and the answer is a fourth.** The service document, a
non-standard member on `query`, or refusing the transform — each of them argues about *how
to tell the client*, and the client is the wrong audience. A caller asking for `outSR=4326`
cannot install shift grids, cannot choose a pipeline, and on the tile path cannot read a
caution at all, because a protobuf tile has nowhere to carry one. **The person who can act
is the one who administers the datastore.** This server already has two channels aimed at
exactly them, so the caution goes to the log and to `/admin/health`.

**What that buys, and it is the part worth stating.** It costs no compatibility risk. The
other three each spent some: a non-standard member on the surface whose whole promise is
that an unmodified ArcGIS client keeps working ([Q-17](../open-questions.md)), or a refusal
that breaks every client asking for a transform today. This spends none, and reaches the
tile path, which two of the three could not reach at all.

**Once per layer and target reference.** D-32's failure has no error, no log line and no
visual signature — the map looks right and is in the wrong place — so the first line is
worth a great deal and the ten-thousandth is worth less than nothing. A warning that
repeats on every request is one an operator filters out, and then the channel is gone. The
pair is the unit because *this layer, served as that reference* is a sentence somebody can
check grids against.

**A pair that could not be read is recorded, not assumed fine.** A reference whose WKT
names no datum is precisely the case to look at; treating *could not tell* as *no datum
change* is how the failure stays invisible. 4326 to 3857 is one datum and is not recorded,
which is what keeps the list worth reading.

**The register is bounded and says when it stops.** Half its key space is the caller's — a
client naming ten thousand SRIDs would otherwise grow it without limit — so it holds 256
pairs and reports `truncated`. It stops recording rather than evicting: eviction would let
the same notice be logged again later, which is the repetition this exists to avoid.

**It cannot fail a request.** The lookup is wrapped, and a projection database that cannot
be read leaves the pair unrecorded and the request answering normally. That is
[D-152](../architecture-debt.md)'s shape — a cosmetic check that stopped the thing it was
commenting on — and the notice is an aside on a request that is about to succeed.

**Measured 2026-08-25 rather than asserted.** Against a layer stored in EPSG:5254
(Turkish National Reference Frame) in a live server: `outSR=4326` logged one line naming
both datums and appeared under `datumShifts` on `/admin/health`, which was empty before it;
six identical queries produced one line; a layer stored in EPSG:3857 served as EPSG:4326
produced none; and a vector tile request produced the 5254→3857 line with no query
involved. `DatumShiftNoticesTests` is what keeps each of those true.

**What is unchanged.** The transform is still performed. Refusing it was the third shape
and it breaks every client that asks for one today; geometry-crs-policy §3's position is
that a silent default is the problem and a documented one is not. The accuracy is still
null, and still needs PROJ's operation database ([Q-100](../open-questions.md)).

---

## 5. Decision — measurement is planar, and says so in every response

Area and length treat coordinates as being on a plane. In Web Mercator that
overstates area by sec²(latitude) — about 1.75× at Istanbul, 4× at Helsinki.

**Geodesic measurement is not offered rather than being offered wrongly.** It is
a different calculation on the ellipsoid, and the failure mode of getting it
silently wrong is somebody quoting a land area 75% too large. The response
carries the caveat on every call, not in documentation nobody reads at the
moment they need it.

`ST_Area(geography)` would give it correctly and is one round trip away. It is
not built because ArcGIS expresses the choice through a `calculationType`
parameter and unit conversions this surface does not yet parse, and half-parsing
that is how a planar answer comes back labelled geodesic.

---

## 6. Consequences

- **Q-23 is answered by construction** for this path and stays open for any
  future in-process engine.
- **`labelPoints` is Tier 1 and is a scanline**, not a centroid. The centroid of
  a crescent falls outside it, which puts a label in the sea. Verified against
  `ST_PointOnSurface` on the same shapes.
- **The framework's form limit had to be raised.** The default 4 MB refused
  requests well inside the documented 500,000-vertex bound, as a 500 that told
  the caller the server had failed — an undocumented limit producing an error
  that blames the wrong party, ahead of the documented one.
- **GeometryServer is now the second surface where a capability is refused with
  its reasoning attached**, after the FeatureServer query parameters. That is
  becoming a pattern worth naming: a refusal that cites a measurement is a
  design record a client can read.

**State.** *Catalogue*: the geometry service is a **service row** like any other, so
that it can be shared and stopped like any other — the migration that made services separable
from layers exists for it. It holds no data of its own. *Runtime*: the overlay workers, their
queue and the deadline that bounds them, all per worker process and therefore **node-local**;
nothing survives the request that created it.

## 9. Q-97 answered — overlay ships, in a process that can be killed

**Owner's choice, taken 2026-08-15: worker process plus pre-flight.**
Implemented the same day. `intersect`, `union` and `difference` are now offered.

**What the answer is not.** It is not a cap on anything the caller sends.
[Measurement](../../benchmarks/geometry-overlay/RESULTS.md) established that no
property of the input predicts the cost: a 6,408-vertex adversarial comb pair
cost 153 seconds and 16.7 GB where a real 72,919-vertex national outline cost
312 ms and 17 MB. Vertex count fails outright; candidate segment pairs narrow
the gap by an order of magnitude and leave one.

**Three mechanisms, and only two of them are bounds.**

| | What it does | Is it a bound? |
|---|---|---|
| **Pre-flight** — candidate segment pairs, limit 100,000 | Refuses obviously explosive inputs before any arithmetic | **No.** Finding 16 measured it under-predicting the adversarial case fourteenfold. It is a filter and is documented as one |
| **Deadline** — 10 seconds, worker killed | Stops the work | **Yes**, and it is the only one that stops it |
| **Heap ceiling** — 1 GB via `DOTNET_GCHeapHardLimit` | The worker throws `OutOfMemoryException` instead of asking the OS for more | **Yes**, for memory |

**The isolation is the decision, not an implementation detail.**
NetTopologySuite is referenced by `Graticula.Overlay.Worker` and by nothing
else — the host never loads it, so an overlay cannot allocate a byte in the
server's heap. That is what makes "kill it" available at all: OverlayNG offers
no cooperative cancellation, `Thread.Abort` does not exist on .NET Core, and the
run that produced the 16.7 GB figure took the machine into swap and killed the
Docker daemon with it.

**Where the threshold sits, and why it is placed there.** The largest real case
measured 48,066 pairs at 62 ms; the 200-teeth comb measured 131,049 at 3.4
seconds. 100,000 admits every real case that was measured and turns the larger
combs away before any arithmetic. It **does not** catch the 100-teeth comb at
40,201 pairs and roughly 1.5 seconds — finding 16 says nothing at this layer
will — and that case is what the deadline and the pool size are for.

**The operator-facing number is workers × ceiling.** Two workers at 1 GB. Not
"one process per request", which would make the exposure unbounded in exactly
the way the pre-flight already fails to bound.

**What this costs.** A serialisation boundary (WKB over a pipe), a process
launch on first use — warmed outside the deadline, because the deadline bounds
the overlay and not the runtime's start-up — and a second executable to ship.

**What is still refused, and now for its own reasons rather than for Q-97's:**
`cut` (a different algorithm, not overlay), `buffer`, `offset`, `convexHull`,
`simplify`, `densify`, `generalize`, `distance`, and the topological predicates.
ADR-022 condition 2 stands: each of those is refused on an argument about
asymptotics, which is the kind of reasoning A-042 was, and each returns only
with a number.

**What this does not settle.** The benchmark's own *what this does not show*
list is untouched: one engine, one adversarial shape, and `union` and
`difference` measured only on real data.

**Concurrency was the fourth item and it is measured now, 2026-08-24** —
[CONCURRENCY.md](../../benchmarks/geometry-overlay/CONCURRENCY.md), which closes
**D-31**. Two workers hold under the ceiling with eight 300,000-vertex overlays
in flight (754 MB and 618 MB of a permitted 1 GB each), a killed worker's memory
is back within a second and a launch under contention costs about 240 ms, and the
queue wait refuses with its own sentence at ten seconds. What that run also found
is that **the wait bounds queueing for a worker rather than the request**: a
25 MB operand arrives slowly enough that sixteen callers can each see seventeen
seconds without any of them waiting ten for a worker.

---

## 7. Conditions

1. ~~**Q-97 is answered before any overlay operation ships.** Not softened, not
   partially implemented behind a flag.~~ **DISCHARGED 2026-08-15** — answered by
   the owner and implemented in full the same day (§9). Not behind a flag: the
   operations are in `supportedOperations` and the service document states the
   limits it enforces.
2. **The over-conservative refusals get measured, individually.** `convexHull`,
   `generalize`, `densify` and `distance` are refused on an argument about
   asymptotics, which is the kind of reasoning A-042 was and which measurement
   overturned. Each returns only with a number.
   **PARTLY DISCHARGED 2026-08-15** (§4b) — `convexHull`, `densify` and
   `generalize` now ship, computed in process and verified against
   `ST_ConvexHull`, `ST_Segmentize` and `ST_SimplifyPreserveTopology` on real
   polygons from the datastore: hull vertices match exactly, generalize agrees
   on 47 of 50 shapes. **The comparison is the number, and it found two defects
   review had not** — a degenerate first segment in Douglas–Peucker on a closed
   ring, and a collapse floor that resolved ties to one corner and returned a
   triangle with half the area. ~~`distance` is still refused, now with its own
   reason rather than the overlay sentence that had been pasted onto it.~~
   **Corrected 2026-08-27: `distance` has answered since §2b**, which moved it to NTS's
   `DistanceOp` on the same day this note was written, and it is in the engine's operation
   list. Measured on the same run as condition 3: two 2,900-vertex polygons, **30 ms**. The
   sentence outlived the decision that reversed it by twelve days, in the register entry that
   records the reversal —
   [D-130](../architecture-debt.md)'s shape at close range. **What is genuinely left of this
   condition is the individual numbers for `convexHull`, `generalize` and `densify`**, which
   were verified against PostGIS for correctness and never timed; the four §2b operations
   beside them now are.
3. **The six operations added in §2b have no measured cost profile.** They are
   verified for *correctness* against PostGIS, and bounded by the deadline and
   the heap limit — but nobody has measured what a realistic `buffer` or
   `relation` costs, so the ten-second deadline and the two-worker pool are
   sized from overlay's numbers alone. A `relation` over two sets of thirty is
   nine hundred comparisons in one worker slot, and that shape did not exist
   when the pool was sized.
   *(Discharged 2026-08-27 —
   [benchmarks/geometry-operations/RESULTS.md](../../benchmarks/geometry-operations/RESULTS.md),
   against real OpenStreetMap polygons out of the 6.5-million-polygon corpus rather than
   against fixtures. **Four findings, and two of them change how to think about the bound.**
   **(a) The deadline fires on ordinary input.** Thirty polygons of about 2,900 vertices
   buffered by 500 metres — a few city blocks — runs past ten seconds and is refused. At 10
   metres the same thirty cost 4.3 s. So the bound is real rather than a formality, which is
   what makes it worth having.
   **(b) The shape this condition singled out is cheap.** *A `relation` over two sets of
   thirty* is **660 ms** on the largest ordinary band — sixteen times inside the deadline,
   and less work than one 500-metre buffer over the same shapes, because a predicate can
   stop early and a buffer cannot.
   **(c) One enormous polygon is not the expensive case.** The largest polygon in the corpus,
   **215,488 vertices**, buffers in 3.5 s. Cost is dominated by how many shapes must be
   dissolved against each other, not by how many vertices one of them has — so a per-geometry
   vertex cap would not be the control it looks like.
   **(d) The pool is two, and the wait is not part of the deadline.** Two concurrent expensive
   requests run at full speed; at eight, two are refused after waiting ten seconds and two are
   *answered at fifteen*. A caller's worst case is **the wait plus the work**, and two
   requests arriving in the same millisecond can end up one refused and one served ten seconds
   later. The server's own refusal says *the wait and the work have separate budgets*; what
   was missing was anybody having read that as a latency promise and found out it is not one.
   **What is not settled**: one machine, one corpus, polygons only, and memory unmeasured — the
   1 GB heap ceiling is still sized from overlay's numbers.)*
4. **The vertex cap is not validated, only argued.** 500,000 comes from the
   corpus and a JSON size estimate, not from a measurement of what a request at
   that size actually costs this server under concurrency.

## 8. Assumptions

| ID | Assumption | Status |
|---|---|---|
| A-059 | A vertex cap bounds the work for single-pass operations | `UNVALIDATED` by measurement, but it is a statement about algorithmic shape rather than about behaviour, and the shape is visible in the code |
| A-060 | Using the datastore's PROJ rather than an in-process library costs nothing that matters | `PARTLY VALIDATED` — the projection is byte-identical to PostGIS's own answer, which is trivially true given it *is* PostGIS's answer. The round-trip cost under concurrency is unmeasured |

## 9. Dissent

**Against shipping half a service.** A GeometryServer that refuses `buffer` and
`intersect` is not what an ArcGIS client expects, and a client that probes for
capability and finds most of them missing may reasonably conclude the server is
not worth pointing at. The counter is that the alternative is not a complete
GeometryServer — it is a complete one with a measured way to stop the process,
and that is worse than an incomplete one that says why.

**Against the datastore as the projection engine.** It puts a synchronous
database round trip on an operation that is arithmetic, and it means the
geometry service cannot answer during a datastore outage — while
`areasAndLengths`, which needs no database, still can. That inconsistency is
real. It is accepted because two coordinate engines that disagree by metres is
worse than one that is sometimes unavailable.
