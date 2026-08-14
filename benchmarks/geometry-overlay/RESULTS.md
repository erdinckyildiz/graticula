# Geometry overlay — Results

**Settles:** [A-042](../../docs/architecture-assumptions.md), and it **invalidates** it.
**Harness:** [`../harness`](../harness) · `GisBench.exe a042`
**Date:** 2026-08-14

---

A-042 said:

> Caps on vertex count, batch size and wall clock are sufficient to make a public
> general-overlay endpoint safe.

and set out how to test it:

> Validate by measuring overlay cost against vertex count to find where the cap
> must sit — **and by checking whether a cap low enough to be safe is still high
> enough to be useful.**

Both halves were measured. The first half answered a different question than it
asked, and the answer kills the assumption.

## Environment

Same machine and corpus as [mvt-generation](../mvt-generation/RESULTS.md):
Windows, .NET 9 Release, server GC, PostGIS 3.4.3, `planet_osm_polygon` at
6,499,215 features. NetTopologySuite 2.5.0, OverlayNG, single-threaded, warm.
Each real case is a polygon intersected with a copy of itself shifted by a tenth
of its own width — maximal edge interaction, which is what a caller reaches by
overlaying two versions of the same boundary.

## Real geometry: cost does not track vertex count

Median of 3, `intersect`:

| Vertices | Median ms | Alloc MB | Candidate pairs |
|---|---|---|---|
| 500 | 6.8 | 1.0 | 424 |
| 2,000 | 50.3 | 5.2 | 1,634 |
| 5,013 | 22.1 | 8.7 | 3,116 |
| 9,893 | 16.3 | 7.0 | 81 |
| 20,105 | 59.2 | 17.8 | 7,029 |
| 29,936 | 24.5 | 8.9 | 4,924 |
| 44,735 | 62.4 | 23.5 | 48,066 |
| 60,344 | 265.4 | 55.3 | 17,374 |
| **72,919** | **312** | **17.2** | **15** |

**Not monotonic, and not close.** 60,344 vertices costs 265 ms; 72,919 costs 312
but with a third of the allocation. 9,893 costs less than 2,000. The largest real
polygon in the corpus — the national outline — is one of the *cheapest* per
vertex, because it barely overlaps its own shifted copy.

Every real case finished inside 350 ms. Read alone, that says a vertex cap could
sit at 100,000 and never fire.

## Finding 15: a vertex cap is not a control

The security question is not what a GIS analyst posts. It is what somebody posts
on purpose. Two combs at right angles, *n* teeth each: the input is linear in
*n*, the crossings are quadratic.

| Teeth | Vertices | Candidates | Intersect ms | Alloc MB | Output vertices |
|---|---|---|---|---|---|
| 50 | 408 | 8,469 | 169 | 65 | 10,486 |
| 100 | 808 | 33,129 | 884 | 256 | 41,221 |
| 200 | 1,608 | 131,049 | 3,415 | 1,037 | 163,441 |
| 400 | 3,208 | 521,289 | 17,086 | 4,198 | 650,881 |
| **800** | **6,408** | — | **153,416** | **16,756** | **2,597,761** |

Set against the real data:

| | Vertices | Time | Allocated |
|---|---|---|---|
| National outline, real | **72,919** | 312 ms | 17 MB |
| Comb pair, adversarial | **6,408** | **153,416 ms** | **16,756 MB** |

**A polygon eleven times smaller costs 490× the time and 970× the memory.** Any
vertex cap generous enough to serve the real corpus admits an input that runs for
two and a half minutes and allocates sixteen gigabytes.

**A-042 is invalidated.** Not weakened — the mechanism it names does not
discriminate between the safe case and the attack.

### The 800-teeth run took the host down

It pushed the machine into swap and killed the Docker daemon, and with it the
PostGIS container this benchmark reads from. Recorded because it is the clearest
statement of the finding: **a single unauthenticated HTTP request would have done
that.** The sweep now stops at 400 teeth; a benchmark that has to destroy the
machine to make its point only has to do it once.

## Finding 16: candidate pairs predict far better, and still not well enough

Overlay works on segment pairs whose bounding boxes overlap. Counting them is an
R-tree build and query — O((n+m) log n), no arithmetic on the segments:

| Case | Predict ms | Intersect ms | Ratio |
|---|---|---|---|
| real, 72,919 vertices | 103 | 312 | 3× |
| comb, 400 teeth | 83 | 17,086 | **206×** |

**83 milliseconds to foresee a seventeen-second operation** is a genuinely useful
pre-flight check, and it is cheap enough to run on every request.

**But it under-predicts the adversarial case by an order of magnitude**, so it
cannot be the only control:

| | Candidates | Intersect ms | ms per 1,000 candidates |
|---|---|---|---|
| real, 44,735 vertices | 48,066 | 62 | **1.3** |
| comb, 100 teeth | 33,129 | 884 | **26.7** |

Fewer candidates, fourteen times the cost. The difference is what fraction of
candidates are real crossings — in real data most bounding-box overlaps are false
positives, and in a comb every one is a crossing that produces output. A
threshold set from real data at, say, 50,000 candidates admits the 100-teeth comb
at 884 ms and 256 MB, and a hundred concurrent copies of that request is 25 GB.

## What follows

**No input-derived cap is sufficient on its own.** Vertex count fails outright;
candidate pairs narrow the gap by an order of magnitude and leave one. The only
quantity that reliably bounds the work is the work itself, which is not knowable
before doing it.

So a safe public overlay endpoint needs a **bound on execution**, not only on
input — and that is the part .NET makes hard. OverlayNG offers no cooperative
cancellation, so a wall-clock cap cannot interrupt it in-process. `Thread.Abort`
does not exist on .NET Core, and would corrupt shared state if it did. The
available mechanisms are:

1. **A separate process per overlay, killed on a deadline.** Actually bounds both
   time and memory. Costs a process launch per request and a serialisation
   boundary.
2. **A pre-flight candidate-pair estimate**, refusing above a threshold. Cheap,
   catches the extreme cases, and finding 16 shows it leaves a gap.
3. **Authentication and a privilege**, so the surface is not public. Reduces who
   can do it; does not stop them.
4. **Not shipping general overlay**, and offering only the linear-cost operations
   (project, lengths, areas, label points, simplify).

These are not exclusive and 1 is the only one that is a bound. The choice is
[Q-97](../../docs/open-questions.md) and it is the owner's, because it trades a
capability described as *crucial* against a defect that takes the host down.

## What this does not show

- **One overlay engine.** NTS OverlayNG. PostGIS/GEOS would have its own curve,
  and pushing overlay into the datastore is a fifth option nobody has costed —
  though it moves the denial of service onto the shared datastore rather than
  removing it.
- **One adversarial shape.** Combs are the textbook quadratic case; spirals,
  dense slivers and near-degenerate collinear edges are others, and some may be
  worse per vertex.
- **`union` and `difference` were measured only on real data.** They share
  OverlayNG's machinery so the adversarial curve should be the same shape, and
  "should be" is not a measurement.
- **No concurrency.** Every figure is a single request on an idle machine. The
  numbers that matter for a denial of service are the concurrent ones, and 16 GB
  for one request makes the concurrent case easy to predict and unnecessary to
  run.
