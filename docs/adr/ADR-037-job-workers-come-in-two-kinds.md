# ADR-037 — Job workers come in two kinds

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-08-18 |
| **Answers** | [Q-120](../open-questions.md) |
| **Supersedes** | — |
| **Superseded by** | — |

---

## 1. Context

**Nothing in this repository says what language a job worker is written in, and until
2026-08-18 nothing needed to.**

[ADR-011](ADR-011-job-system.md) decides that long work belongs in a job system and describes its
surfaces. [ADR-016](ADR-016-packaging-deployment-upgrade.md) §7 goes as far as *"the job-worker image
carries a curated wheel set that we version and patch"* — a Python detail — without saying whose
process runs it. And one worker exists in code: `Graticula.Overlay.Worker`, .NET, spawned by
[`GeometryWorkerPool`](../../src/Graticula.Host/GeometryWorkerPool.cs).

**Two things now want to be in one process, and they pull in opposite directions.**

The first is **File Geodatabase import**. [Q-108](../open-questions.md) and
[file-geodatabase-readers.md](../research/file-geodatabase-readers.md) concluded that writing our own
managed `.gdb` reader is the wrong project: there is no ready-made open-source GDAL-free reader for
.NET, and `pyogrio` in Python reads one in a line. The owner then removed both constraints the
conclusion had been held back by — *"gdal da ekleyebiliriz"* and *"illa .net olmak zorunda değil…
geoprocessing araçları pythonda yazılacağı için"*.

The second is the **geoprocessing runtime**, which Q-17b already settled as Python — *"the toolbox is
not ours to write. Tools are Python, the ArcPy and PyQGIS model."*

**And half the boundary between them is already chosen.** [Q-74](../open-questions.md) asked how data
crosses into a tool, rejected giving Python a database connection, rejected calling our own API for
bulk, and chose *materialise the input to a file the tool reads* — concluding the format wants to be
**Arrow or GeoParquet**, because *"`geopandas`, `pyarrow` and `shapely` all read them without a shim."*
`pyogrio` is Arrow-native, so the same boundary serves both directions.

**What breaks without this decision** is not the code; it is the record. A Python worker would arrive
because an import needed one, and the deployment would acquire a second runtime that no decision ever
chose. That is the shape [D-46](../architecture-debt.md) names for UI and [D-74](../architecture-debt.md)
names for enumerations: a thing that grew because nobody had to decide it.

## 2. Alternatives considered

### Alternative A — One worker, .NET, with a managed File Geodatabase reader

**Argument for.** One runtime in the deployment, one dependency graph to patch, one language for
whoever maintains this. `Graticula.Overlay.Worker` already exists and already has the killable-process
shape a long job needs. No GDAL anywhere, which restores [ADR-001](ADR-001-core-language.md) C7 fully
and shrinks the air-gapped bundle — the prize [Q-28](../open-questions.md) and
[A-016](../architecture-assumptions.md) are about. And it is *possible*: three independent
implementations prove the format is writable from a public specification.

**Argument against.** The cost is not the length, it is the failure mode. `.gdb` is a
reverse-engineered binary format whose specification calls itself work in progress, and a mis-parsed
varint or a wrong field offset does not throw — it yields a plausible wrong value.
[`spark-gdb`](https://github.com/mraad/spark-gdb) is the closest analogue, a pure managed reader built
from the same specification by one author: it reached Point, Polyline and Polygon with X/Y/Z/M and
stopped before **multi-part geometries, XML fields, Blob fields and rasters**, last commit January 2016.
Our shapefile reader is 860 lines against a format documented and stable since 1998 with one geometry
encoding.

**And the prize is not there any more.** The whole argument for writing it was removing GDAL. The owner
has said GDAL is acceptable, and the *remaining* prize — one fewer runtime — is **lost rather than won**
by choosing .NET, because Python arrives with geoprocessing regardless. So this alternative pays a
multi-month binary-format project for a benefit that expires.

### Alternative B — One worker, Python, for everything

**Argument for.** One runtime after all, and it is the one both new needs point at. `pyogrio` for
formats, the geoprocessing SDK later, Arrow as the boundary in both directions. The .NET overlay worker
could be reimplemented over `shapely`, which is GEOS — the same engine PostGIS uses, so the geometry
answers would agree by construction rather than by hope ([A-043](../architecture-assumptions.md) is
about six engines disagreeing).

**Argument against.** It throws away the one thing about `Graticula.Overlay.Worker` that was *measured*
rather than argued. [ADR-022](ADR-022-geometry-server.md) §9 exists because
[A-042](../architecture-assumptions.md) was invalidated by measurement: a 6,408-vertex adversarial comb
pair cost **153 seconds and 16.7 GB** where a real 72,919-vertex national outline cost 312 ms and 17 MB,
and the run that produced that figure took the host into swap and killed the Docker daemon. The answer
was a killable process with `DOTNET_GCHeapHardLimit` — a runtime-enforced memory ceiling that makes the
worker throw `OutOfMemoryException` instead of asking the operating system for more.

**Python has no equivalent.** There is no interpreter-level heap hard limit; the tools are `ulimit`,
cgroups and `RLIMIT_AS`, which are the operating system's and behave differently on the three platforms
this product supports. Reimplementing the overlay worker in Python means re-litigating a decision that
cost a benchmark and a dead Docker daemon to reach, in exchange for tidiness.

### Alternative C — One worker per job type, as many as there are jobs

**Argument for.** No shared image, so no job's dependencies can break another's. The tightest possible
blast radius.

**Argument against.** It answers a question nobody asked. There are two job families with two different
reasons; three, four and five images would be one per *task* rather than one per *reason*, and each is a
build, a patch cadence and an air-gapped bundle entry. §82: *what concrete problem does this solve?*

## 3. Counterarguments to the preferred option

**Two runtimes is the cost, and it is not small.** A second dependency graph to patch, a second base
image to track for CVEs, a second thing that can be the wrong version in a customer's deployment, and a
larger air-gapped bundle — the very thing [Q-28](../open-questions.md) and A-016 were narrowing. Whoever
inherits this maintains Python they may not know.

**It arrives earlier than ADR-016 planned.** §7's curated wheel set was scoped as *the cost of
user-supplied tools*, and [v1-scope](../v1-scope.md) §3c cut the Python runtime from the worker image
precisely to shrink the air-gapped bundle and Q-76's maintenance burden. This decision brings the
runtime back for our own code, so §3c's stated benefit is partly given up **before** the feature it was
given up for exists.

**And there is a real slippery slope.** A Python worker running our import script is a packaging cost.
A Python worker running *user* tools is [Q-75](../open-questions.md) — *"the largest security surface in
the product by a wide margin — arbitrary code execution by design, against a server holding the
organisation's spatial data."* The distinction is sound and it is one sentence wide. The pressure to
widen it will come from convenience, and the register is the only thing that will notice.

**The honest reply to that last one is a condition, not an argument** — see §7's condition 3.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| No open-source GDAL-free `.gdb` reader exists for .NET | Three searches, 2026-08-18. GeoDataToolkit wraps Esri's closed SDK; Aspose.GIS is commercial; OpenFileGDB is GDAL | [file-geodatabase-readers.md](../research/file-geodatabase-readers.md) §1–2 |
| A pure managed reader stops before the long tail | `spark-gdb`'s own TODO: multi-part geometries, XML fields, Blob fields, rasters. Last commit January 2016 | [`spark-gdb`](https://github.com/mraad/spark-gdb) |
| `pyogrio` reads `.gdb` and is Arrow-native | Reads via `OpenFileGDB`; 5–10× faster reads than Fiona; `use_arrow=True` with GDAL 3.6+ faster again; reads non-spatial tables too | [pyogrio](https://github.com/geopandas/pyogrio), [GeoPandas I/O](https://geopandas.org/en/stable/docs/user_guide/io.html) |
| `OpenFileGDB` needs no proprietary library | *"Does not depend on a third-party library."* Writes since GDAL 3.6; relationships 3.6, domains 3.3, raster 3.7, curves supported | [GDAL OpenFileGDB](https://gdal.org/en/stable/drivers/vector/openfilegdb.html) |
| A .NET worker's memory ceiling is enforced by the runtime | `DOTNET_GCHeapHardLimit` makes the worker throw at the ceiling instead of taking the host into swap | [`GeometryWorkerPool.cs`](../../src/Graticula.Host/GeometryWorkerPool.cs) |
| The overlay worker's existence was measured, not argued | 6,408-vertex adversarial pair: **153 s, 16.7 GB**. Real 72,919-vertex outline: 312 ms, 17 MB. The run killed the Docker daemon | [benchmarks/geometry-overlay](../../benchmarks/geometry-overlay/RESULTS.md), A-042 `INVALIDATED` |
| GDAL is a bill of materials, not one licence | `LICENSE.TXT` lists BSD, public-domain, **Apache-2.0 (Esri components)**, ISC, Info-ZIP and Qhull beside the MIT-style core | [GDAL LICENSE.TXT](https://github.com/OSGeo/gdal/blob/master/LICENSE.TXT), [DEPENDENCY-LICENSES.md](../../DEPENDENCY-LICENSES.md) |
| Nothing decided this before | No `job` table, no queue, no status endpoint. ADR-011 is a decision with no implementation | Verified against the schema and `src/`, 2026-08-18 |

## 5. Decision

**There are two kinds of job worker, chosen by what bounds the work rather than by what the work is
about.**

A **.NET worker** runs work our own runtime must bound and kill — where the ceiling is enforced by the
runtime we control, and where the input is adversarial because a caller supplied it.
`Graticula.Overlay.Worker` is this kind and does not move.

A **Python worker** runs work whose value is in somebody else's ecosystem — reading foreign formats and,
later, running foreign tools. It carries GDAL and a pinned wheel set; the serving container carries
neither, which leaves [A-016](../architecture-assumptions.md) intact. **File Geodatabase import is its
first job.**

**The boundary between the Python worker and our runtime is a file, and the format is GeoParquet** —
executing [Q-74](../open-questions.md)'s choice rather than making a new one. The worker never holds a
database connection: it reads `.gdb` and writes GeoParquet, and our importer reads that. Q-74 rejected a
database connection for user tools on authorization grounds; the same shape is kept here even though our
own script would not bypass anything, because the boundary that survives the arrival of user tools is
the one to build now.

**Two kinds is the answer, and it is a ceiling rather than a floor.** A third kind needs its own ADR.

## 6. Consequences

**Positive.**

- The `.gdb` reader is adopted rather than written — a line of code against a multi-month binary-format
  project whose defects are silent.
- The process the geoprocessing runtime needs arrives once, for a job that pays for it now.
- Arrow/GeoParquet stops being a homeless owner interest. Q-74 said this *"finally gives GeoParquet a
  concrete problem to solve"*; this is that problem.
- `OpenFileGDB` brings relationships, domains, curves and non-spatial tables — more than the format
  survey originally credited it with, and more than a first-cut reader of ours would have had.
- The overlay worker's measured memory bound is left alone.

**Negative.**

- **A second runtime in the deployment**, with its own patch cadence, its own CVE surface and its own
  air-gapped bundle weight. This partly gives up v1-scope §3c's stated benefit.
- **The wheel set becomes ours earlier than planned.** ADR-016 §7 scoped it as the cost of user tools;
  it is now the cost of import as well. [A-049](../architecture-assumptions.md) is `UNVALIDATED` and
  now load-bearing sooner.
- **Two worker shapes to understand**, and the rule that separates them is a sentence. A reader who
  does not know the rule will put a job in the wrong one.
- ~~**Import gains a disk dependency.** `pyogrio` cannot read a stream, so a `.gdb` must be extracted
  to a temporary directory — a capability the server does not have and
  [security.md](../security.md)'s upload rules did not contemplate.~~ **Withdrawn 2026-08-18, measured
  rather than reasoned.** GDAL reads inside the archive: `ogrinfo /vsizip//data/x.gdb.zip` opened three
  of the owner's real geodatabases and listed their layers, unpacking member by member in memory. There
  is no temporary directory, so there is nothing to bound or clean —
  [file-geodatabase-readers.md](../research/file-geodatabase-readers.md) §8b.
- **GDAL's licence is a bill of materials**, so the worker image's drivers must be enumerated before it
  ships. [D-06](../architecture-debt.md) narrows again rather than closing.

**Ports created.** GDAL and `pyogrio` are Tier 2 under
[build-vs-adopt-policy.md](../build-vs-adopt-policy.md) §4, and the seam is **the process boundary plus
the GeoParquet file** — stronger than an interface, because no library type can cross it in either
direction by construction. On our side the named interface is the reader that turns a GeoParquet file
into the importer's feature stream; **no Parquet or Arrow library type may appear in a Tier 1
signature**, which is the ordinary rule and is restated because a columnar reader is exactly the kind of
dependency that leaks.

## 7. Conditions

1. **The rule that separates the two kinds is written where a worker is added**, not only here. A job
   placed in the wrong worker is the failure this ADR exists to prevent, and an ADR is not where
   somebody looks while writing one.
2. **The Python worker's image enumerates its GDAL drivers and their licences** before it ships, per
   [DEPENDENCY-LICENSES.md](../../DEPENDENCY-LICENSES.md)'s own warning that a GDAL build is a bill of
   materials rather than one licence.
3. **A test fails the build if the Python worker acquires a path to user-supplied code.** The
   distinction between *our script* and *their tool* is one sentence wide and is the whole of why
   [Q-75](../open-questions.md) is not reopened by this decision. A sentence is not a guard.
4. **No GDAL or OSGeo package reference appears in the solution.** Verified absent 2026-08-18; Q-28's
   stricter form is mechanically checkable where an image boundary is not, so it is checked.
5. **PARTLY DISCHARGED 2026-08-18** — ADR-024 condition 2's rule, which found a winding-order defect
   in the shapefile reader. **The corpus exists:** the owner supplied three of their own geodatabases,
   and `OpenFileGDB` opened all three and listed 12, 55 and 8 layers with relationships, domains, field
   aliases and resolved EPSG codes. So the adopted reader is verified against data this project did not
   write, which is the half that could be done before a worker exists. **What is still owed is an
   actual import** — reading a layer is not writing one, and the geometry, the encoding and the Z drop
   are only settled by a round trip. Details, including a file type the published specification does
   not cover, in [file-geodatabase-readers.md](../research/file-geodatabase-readers.md) §8b.
6. ~~**The temporary extraction directory has stated bounds and is cleaned**, with the same shape of
   argument `ArchiveLimits.ForShapefile` carries: numbers derived from the format rather than round.~~
   ***(Withdrawn 2026-08-18 — there is no extraction directory. See §6's negative consequences.)***
   **What replaces it is not another condition but an existing one:** with `/vsizip/` the archive is
   opened by GDAL inside the worker, so `BoundedArchive` is not in the path and the bomb defence is the
   worker's memory and time bound — this ADR's own process boundary. Measured on the way: the owner's
   archives run to 338 members and one member compressing **430×**, against
   `ArchiveLimits.ForShapefile`'s 32 and 100×, so the shapefile numbers were never going to serve both
   formats. A ratio calibrated for two formats at once is a ratio calibrated for neither.

## 8. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| [A-016](../architecture-assumptions.md) | GDAL-backed providers can be made optional, so a PostGIS-only deployment ships as one artefact | `VALIDATED` by design decision — and this ADR keeps it, since the serving container gains nothing |
| [A-038](../architecture-assumptions.md) | GDAL is needed for the import formats we care about | `INVALIDATED` — and the invalidation is what made this a cost decision rather than a forced one |
| [A-049](../architecture-assumptions.md) | A curated Python wheel set can cover realistic work without pip at runtime | `UNVALIDATED`, and load-bearing sooner than ADR-016 planned |
| [A-042](../architecture-assumptions.md) | Caps on vertex count, batch size and wall clock bound overlay work | `INVALIDATED` — the reason the .NET worker exists, and the reason it stays |

## 9. Dependencies

**Depends on** — [ADR-011](ADR-011-job-system.md) (the job system this is a worker for),
[ADR-016](ADR-016-packaging-deployment-upgrade.md) §7 (the wheel set),
[ADR-022](ADR-022-geometry-server.md) §9 (why the .NET worker exists),
[ADR-024](ADR-024-shapefile-import.md) (the archive exception this extends, condition 3),
[ADR-001](ADR-001-core-language.md) C7 (the artefact rule GDAL is measured against).

**Depended on by** — [ADR-009](ADR-009-raster-engine.md) §2.2 (GDAL at registration, whose process this
now names), [ADR-006](ADR-006-plugin-model.md) (the plugin model Q-17b reopened),
[ADR-013](ADR-013-feature-service-data-model.md) (relationships, which `OpenFileGDB` can now supply
without touching Esri internals).

## 10. Revisit triggers

- **A third job family appears that fits neither rule.** Two kinds is a ceiling; a third needs its own
  argument.
- **A Python heap ceiling becomes enforceable in-interpreter** across all three supported platforms.
  Then Alternative B's tidiness argument gains the one thing it lacks, and the overlay worker's language
  is worth reopening — with `shapely` over GEOS as the prize, since it would make our geometry answers
  agree with PostGIS by construction ([A-043](../architecture-assumptions.md)).
- **A deployment forbids the Python image.** Then `.gdb` import is unavailable there and
  [Q-108](../open-questions.md) reopens as *write our own after all* — with the cost this ADR records
  as the estimate.
- **The wheel set stops being maintainable** (A-049 invalidated). ADR-016's escape hatch is a
  customer-built image, which shifts the burden rather than removing it, and that is a different
  decision.
- **`pyogrio` fails against a real geodatabase** in a way its documentation did not predict. Condition 5
  is where that would surface.

## 11. Dissent

**Recorded, and it is the author's own, against the option chosen.**

*A second runtime is the kind of cost this project was written to refuse.* §82 puts Kubernetes, Kafka
and Redis on a challenge list and asks *what concrete problem does this solve* — and A-003's own row
says our exposure there *"is the price of not taking Redis, which is a trade worth stating as one."*
This decision takes a dependency of comparable weight, and the honest form of the objection is that
*File Geodatabase import* is a smaller problem than *a whole second language runtime in every
deployment* is a cost.

**The reply, and it does not fully dissolve the objection:** the runtime is not bought for import. It is
bought for the geoprocessing surface the owner has already settled as Python (Q-17b), and import is the
first job that pays for a process which was arriving anyway. If geoprocessing were ever cut for good,
this ADR should be reopened rather than kept — because then the objection wins.

**A second, smaller dissent:** choosing GeoParquet as the boundary commits us to a columnar format
before anything has measured it against our own data. Q-74 reasoned to it from what Python reads
natively, which is sound about Python and silent about us. The alternative — newline-delimited GeoJSON,
which our importer nearly handles today — is slower and larger and needs no new dependency. That is a
real trade and it was decided on Q-74's reasoning rather than on a measurement, which is thinner ground
than §3 of this document usually stands on.
