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

## 7. Conditions

1. **Q-97 is answered before any overlay operation ships.** Not softened, not
   partially implemented behind a flag.
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
