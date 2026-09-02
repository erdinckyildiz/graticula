# What a CITE run costs, and what it caught

**Run 2026-08-26**, against the development server on the machine described in
[benchmarks/README.md](../README.md), to answer the thing
[D-158](../../docs/architecture-debt.md) says is *not measured*: **nobody has
timed the CITE engines**, and that timing is the whole of what stands between
the row and a decision about where the job belongs.

---

## 1. The timings

Both images were already local. A run is one REST call to TEAM Engine, which
drives the whole suite and returns the EARL report as RDF.

| Suite | Image | Assertions | Wall time |
|---|---|---|---|
| WFS 2.0 | `ogccite/ets-wfs20` | 757 | **55 s** |
| WFS 2.0, second run | same | 757 | **44 s** |
| OGC API Features 1.0 | `ogccite/ets-ogcapi-features10` | 332 | **10 s** |
| WMS 1.3 | `ogccite/ets-wms13` | 188 | **10 s**, then 7 s |
| OGC API Features 1.0, every collection | same | 1,402 | **29 s** |

**Container start is not in those numbers and is not free**: TEAM Engine needs
about 30 s before it answers, once per container rather than once per run.

**So the three suites together are about a minute and a quarter of engine time**,
plus a half-minute of container start each, against a server that is already up. That is the
figure D-158 was missing. It does not decide where the job goes — a minute on
every push is a different proposition from a minute nightly — but the decision
now has a number rather than the word *minutes*.

**The OGC API Features figure is not comparable to the recorded baseline.** The
2026-08-23 evidence was a run *against every collection*, 1,268 assertions; this
was the default scope, 332. The suite scales with collections, so a
full-collection run is longer by roughly the same factor its assertion count
grows.

---

## 2. What the run caught, which is the point of the row

**WFS 2.0, first run: 395 passed, 2 failed, 360 untested.** The recorded
2026-08-23 evidence is 420 passed, 0 failed, 390 untested. The counts are not
comparable — the two runs tested different numbers of assertions — but the
failures are real, and they are the same assertion twice:

```
org/opengis/cite/iso19142/basic/filter/PropertyIsNilOperatorTests#propertyIsNil
java.lang.AssertionError: Unexpected HTTP status code. expected [200] but found [400]
```

**Reproduced by hand, independently of the suite**, so it is not an artefact of
how this run was configured:

| Request | Before | After |
|---|---|---|
| `fes:PropertyIsNull` on `name` | 200, a FeatureCollection | 200 |
| `fes:PropertyIsNil` on `name` | **400 `OperationNotSupported`** | **200** |
| `fes:PropertyIsNil nilReason="withheld"` | 400 | 400, and it says why |

Also confirmed over HTTPS on the ordinary development server, so it is not the
plain-HTTP configuration this run used.

**The cause.** `CapabilitiesDocument` advertised `PropertyIsNull` and not
`PropertyIsNil`, and `FilterReader` refused the second by name. Filter Encoding
2.0 separates them — *absent* against *present and carrying `xsi:nil`* — and a
relational column has one representation for both, so this server answers them
the same and now says so where somebody will read it.

**`nilReason` is refused rather than ignored**, because it asks *why* a value is
absent and a null column records no reason. Ignoring it would answer a narrower
question and call it the same answer.

**After: 397 passed, 0 failed, 360 untested.** Two failures gone, two more
assertions passing, same scope.

---

## 2b. And the third suite, which D-158 does not mention

**There is a third recorded run and the row lists two.** `cite-wms13-2026-08-20.rdf` and
`cite-wms13-2026-08-23.rdf` have been in `docs/reviews` all along; D-158 names only WFS 2.0
and OGC API Features. So the evidence it says is unmaintained is larger than it says.

**Re-run 2026-08-26: 181 passed, 7 failed.** The 2026-08-23 baseline is 194 passed, 8
failed — again not comparable, 188 assertions against 202. Five of the failures are in both
sets. Two are not in the baseline, and one of them is exact and checkable:

> Every named layer in the capabilities document has at least one BoundingBox element
> (direct or inherited).

**Two of this deployment's fourteen layers had neither `BoundingBox` nor
`EX_GeographicBoundingBox`** — `ci_editable` and `LiveSensors`, both with zero rows. An
empty layer has no extent, the writer returned early on an empty extent, and the root layer
states no `BoundingBox` to inherit. WMS 1.3.0 §7.2.4.6.6 and §7.2.4.6.8 require both on
every named layer.

**Fixed the same day.** A named layer with an empty extent now gets the whole world:
`EX_GeographicBoundingBox` of −180/−90/180/90, and a `BoundingBox` in **CRS:84** rather
than in the layer's own reference. That choice is the interesting half — the world in
EPSG:3857 would need that reference's own domain, which this server cannot look up reliably
([Q-123](../../docs/open-questions.md) measured why), while CRS:84 is longitude-first by
definition and is inherited by every layer from the root.

**The whole world is not a lie.** It does not claim the data spans the earth; there is no
data to span anything. It says *this is not constrained*, which is what is known. The
alternative that suggests itself — a zero-area box at the origin — is a false pinpoint in
the Gulf of Guinea.

**After: 184 passed, 6 failed**, same scope. `EmptyLayerStillHasABoundingBoxTests` holds it,
including the half a blanket repair would lose: a layer that *has* an extent still states
its own, in its own reference.

**The remaining six are older than today** — background colour, exponential BBOX notation,
pixel-edge interpretation, transparency, and one bbox-outside-CRS case — and five of them
are in the 2026-08-23 baseline as well.

---

## 2c. The six that remain, and what five of them are

**Read the requests, not the assertion names.** Five of the six failing WMS assertions were
built by the suite with **an empty `LAYERS` parameter** — one of them with `LAYERS=,,,,,,,`,
eight empty entries — so they are requests for no layers at all:

| Assertion | `LAYERS` sent |
|---|---|
| no-bgcolor | *(empty)* |
| blue-bgcolor | *(empty)* |
| exponential BBOX notation | `,,,,,,,` |
| pixel-edge interpretation | *(empty)* |
| TRANSPARENT=TRUE | *(empty)* |
| bbox-outside-crs | `ci_buildings` |

**So five of them say nothing about this server's GetMap.** The suite could not select a
layer for them and sent none. Whatever the selection criterion is, it is not one this run
established — and the same five are in the 2026-08-23 baseline, so *8 failures* there is
partly the same artefact rather than eight conformance gaps. A hypothesis was tested and
refused: giving every layer a second `BoundingBox` in `CRS:84` changed nothing — 184/6 both
ways, `LAYERS` still empty — so it was reverted rather than kept as a change with no
measured effect (§82).

**Exponential BBOX notation works, checked directly**: `bbox=3.218E6,5.012E6,3.219E6,5.013E6`
against a real layer returns 200 and a PNG. The assertion that carries that name is one of
the five.

**The sixth was real, is fixed, and is [D-163](../../docs/architecture-debt.md).** **187 passed / 5 failed** after it, from 184 / 6 — the assertion passes and three others pass with it, and the five that remain are exactly the empty-`LAYERS` ones above, which is what that analysis predicted. `bbox-outside-crs`
sends a genuine layer with `BBOX=-10,90,10,110` in CRS:84 — latitudes up to 110°. The suite
expects an image; this server answers a `ServiceException`.

**And answering it revealed a second defect, which is fixed.** PostGIS raised
`XX000: transform: latitude or longitude exceeded limits`, `PostgresException` derives from
`NpgsqlException`, and the server said *this service is temporarily unavailable, retry in a
few seconds* — for a request that will never succeed, sending whoever read it to check a
database that was working. It now answers 400 and names the bounding box.
**That did not change the suite's count**, because the suite wants a map rather than a
better refusal; it changed what an operator is told, which is worth its own line.

**A worry that was measured and is not real.** Five bad requests in a row did not open the
source's circuit breaker: normal requests kept returning PNG throughout. `SourceBreaker`
distinguishes a database that *answered* from one that is unreachable, so a client cannot
take a data source down with a malformed bounding box.

---

## 2d. Full scope on OGC API Features, which the default run was not

**`noofcollections=-1` is the parameter, and it is what the 2026-08-23 baseline used.** The
default is three collections; §1's ten-second figure was that. Every collection is **29 s**
over 1,402 assertions.

| Run | Passed | Failed | Untested |
|---|---|---|---|
| 2026-08-23 baseline, every collection | 1,268 | 6 | 78 |
| 2026-08-26, every collection | 1,256 | **20** | 126 |

**Six of the twenty are the baseline's six** — `numberReturned (300) does not match the
number of features in all responses (50)`, a paging count that has been wrong since before
today.

**Fourteen are new, and all fourteen are against the two empty layers.** They are one
defect, not four:

```
GET /ogc/features/v1/collections/LiveSensors/items?bbox=-180,-90,180,90&limit=10  ->  400
```

**Measured directly across five layers, all EPSG:3857:**

| Layer | Rows | `bbox=-180,-90,180,90` |
|---|---|---|
| LiveSensors | 0 | **400** |
| ci_editable | 0 | **400** |
| ci_buildings | 8 | 200 |
| ci_parcels | 12 | 200 |
| tiles-buildings | 20,000 | 200 |

Same request, same reference, and the answer depends on whether the table has rows.
±90° is valid in CRS84 and is not representable in Web Mercator, so transforming the filter
raises — and whether that raise is reached depends on the plan the table's contents produce.

**Fixed the same day: 1,256 / 20 before, 1,351 / 10 after — 95 more assertions pass.** The ten that remain are six `items/null` requests the suite builds when a collection has no feature to name, and the four paging counts from the baseline. **This was [D-163](../../docs/architecture-debt.md) on the surface D-163 did not touch**, and
it is [D-164](../../docs/architecture-debt.md). The map path's answer — draw nothing where
PROJ cannot reach — is *wrong* here: a world bbox on a Web Mercator layer should return
every feature, because everything is inside ±85°. The filter has to be clipped to what the
target can represent, and `postgis_srs('EPSG','3857')` gives exactly that: (-180, -85.06) to
(180, 85.06).

**A detail worth keeping straight.** The 400 the suite sees is the message written this
morning for the WMS path — *a coordinate in this request is outside the range its coordinate
reference system allows*. Before that it would have read *this service is temporarily
unavailable*. The defect is the same; only the honesty changed, and it is what made this
diagnosable in one reading.

---

## 2e. The ten that remain are accounted for, and none of them is a defect here

**A conformance claim is only worth what its residual is understood to be**, so the ten were
chased rather than left as a number.

**Six are `items/null`.** The suite asks
`/collections/{id}/items/null?crs=…` because it takes a feature id from the collection and
the two empty layers have none to take. A 404 for a feature that does not exist is correct;
the assertion simply cannot run on an empty collection.

**Four are the suite's own page cap.** The message reads *Value of numberReturned (300) does
not match the number of features in all responses (50)*, which invites the conclusion that
paging is broken. It is not, and the measurement is unambiguous:

| Walk of `parks`, default limit | Result |
|---|---|
| pages followed to exhaustion | **30** |
| features collected | **300** |
| distinct among them | **300** |
| `numberMatched` reported | **300** |

**50 is exactly the first five pages of ten.** The suite stops after five and compares what
it collected against `numberMatched`, so any collection with more than fifty features and
the recommended default limit fails this assertion by construction.

**Paging was checked across all fourteen collections and is self-consistent**: no page
repeats a feature, `numberReturned` equals the number of features actually carried in every
response, and `limit` above the collection's size returns the whole collection with
`numberReturned` equal to `numberMatched`.

**It could be made to pass and should not be.** A default `limit` of 100 would put 300
features inside the suite's five pages. OGC API Features *recommends* 10, and raising a
default to move an assertion is tuning the server to the test rather than to its callers —
which is the kind of green that [D-158](../../docs/architecture-debt.md) is warning about
rather than asking for.

---

## 3. What this says about D-158

The row's argument is that *a conformance claim ages into a conformance belief*,
and this run is that argument arriving as a defect rather than as a warning. The
repository's recorded evidence says 420 of 420 for WFS 2.0. The first thing a
re-run did was find a filter operator that was never implemented and that the
recorded run had not exercised.

**It is not established that this was a regression.** The baseline left 390
assertions untested and this run left 360, so the likeliest story is that the
test was newly *reached* rather than newly broken. What is established is that
the recorded number was not a statement about the server, only about one run of
it — which is exactly the gap D-158 records.

---

## 4. How to repeat it

```sh
# One server the container can reach by name, over plain HTTP.
docker run -d --name cite-wfs --add-host DESKTOP-M804G0L:host-gateway \
  -p 8112:8080 ogccite/ets-wfs20

curl -u ogctest:ogctest \
  "http://localhost:8112/teamengine/rest/suites/wfs20/run?wfs=<url-encoded GetCapabilities URL>"
```

The OGC API Features suite is the same shape with `ogccite/ets-ogcapi-features10`,
suite `ogcapi-features-1.0`, and `iut=` in place of `wfs=`; WMS 1.3 is
`ogccite/ets-wms13`, suite `wms13`, parameter `capabilities-url=`.

**Plain HTTP was used deliberately**: the suites run over HTTPS too — the
recorded evidence did — but a self-signed certificate has to be imported into the
container's Java truststore first, and that is a step between somebody and a
measurement. For a timing it changes nothing, and the one failure found was
reproduced over HTTPS as well.

---

## Run 2026-09-02 — after a day of changes to three of the faces

`tools/cite-run.sh` against the development server on plain HTTP, all three suites, one
after another. The reason for running them is that this day changed WFS `GetFeature`, WMS
`GetMap` and `GetFeatureInfo` ([ADR-049](../../docs/adr/ADR-049-a-face-refuses-in-its-own-vocabulary.md)),
the OGC API Features write path ([D-186](../../docs/architecture-debt.md)) and
`ServiceLookup`, which every face resolves through.

| Suite | Passed | Failed | Untested | Wall time | Against 2026-08-27 |
|---|---:|---:|---:|---:|---|
| WFS 2.0 | **281** | **0** | 244 | 39 s | 279 passed — two better |
| OGC API Features 1.0 | **308** | **0** | 24 | 9 s | unchanged |
| WMS 1.3 | **158** | **6** | 0 | 8 s | unchanged, same six |

**Nothing regressed.** WFS gained two assertions against the earlier run, which is not
explained here and is recorded rather than claimed: the suites are re-run, not diffed.

## The address the container needs, which cost a diagnosis

The first attempt used the machine's own name, which is what the 2026-08-26 section above
records — and it failed with a 452-byte HTML page. From inside the container,
`DESKTOP-M804G0L:8445` does not resolve and `host.docker.internal:8445` answers **200**.
`cite-run.sh`'s own comment says to pass `http://host.docker.internal:PORT` and it is right;
what was wrong was reading the older section for the recipe.

**And the failure said the wrong thing.** `cite-count.py` reported only *the report has no
earl:outcome at all*, while the file in its hand held TEAM Engine's own sentence — *Failed
to connect to resource located at …*. It prints that now. A tool that has the reason and
does not show it is [D-177](../../docs/architecture-debt.md)'s rule broken by one of the
tools that exists to enforce it.
