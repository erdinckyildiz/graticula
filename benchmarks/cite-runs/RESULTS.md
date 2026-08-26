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
