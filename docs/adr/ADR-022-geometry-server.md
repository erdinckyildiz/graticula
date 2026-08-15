# ADR-022 — GeometryServer ships in halves, and the projection engine is the datastore's

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `HIGH` for the split · `HIGH` for the engine · `MEDIUM` for the vertex cap |
| **Decided** | 2026-08-14 |
| **Rests on** | [A-042's invalidation](../architecture-assumptions.md) · [benchmarks/geometry-overlay](../../benchmarks/geometry-overlay/RESULTS.md) |
| **Defers** | the overlay half to [Q-97](../open-questions.md) |

---

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
| `toGeoCoordinateString`, `fromGeoCoordinateString`, `findTransformations` | **Were not even on the refusal list** until the comparison. No geometry engine needed — MGRS/USNG strings and enumerating PROJ's transformation paths |

**10 of 22 supported, 9 refused with reasons, 3 newly discovered.**

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
NetTopologySuite is referenced by `GisServer.Overlay.Worker` and by nothing
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
list is untouched: one engine, one adversarial shape, `union` and `difference`
measured only on real data, and **no concurrency**. The pool bounds concurrency
to two, which makes the unmeasured case much smaller — it does not measure it.
Recorded as **D-31**.

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
3. **The vertex cap is not validated, only argued.** 500,000 comes from the
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
