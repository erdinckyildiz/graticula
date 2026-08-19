# ADR-037 — Job workers come in two kinds

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-08-18 |
| **Reversed** | **2026-08-18, the same day — see §5a. The Python worker is gone; GDAL is linked in .NET.** |
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

## 5a. Reversed the same day, and the reason is that §2's Alternative A was argued against a
constraint that had already gone

**The owner: *"iyi de öyle saçmalık mı olur. Ben gdalsız dedim. eğer ki illa gdal kullanacaksam .net de
olur. gidip dockerize gdal mı kullanılır"*.** They are right, and the error is visible in this
document's own structure.

§2's Alternative A is *"one worker, .NET, with a managed File Geodatabase reader"* — and its argument
against is entirely about the cost of **writing a parser**: the reverse-engineered format, the silent
failure mode, `spark-gdb`'s abandoned TODO list. That argument is sound and it answers a question nobody
was asking any more. **Once GDAL is acceptable, .NET does not need a parser — it needs a binding**, and
[`MaxRev.Gdal.Core`](https://www.nuget.org/packages/MaxRev.Gdal.Core/) is one. This ADR chose Python to
avoid writing a reader, while accepting GDAL, which is the only thing that made writing a reader
necessary.

**So the whole justification for a second runtime dissolved and I kept its conclusion.**

### Measured before reversing, on the owner's own archives

| | Python worker (`pyogrio`) | .NET (`MaxRev.Gdal.Core`) |
|---|---|---|
| same layer, 3,659 features, read + write GeoParquet | 0.29 s | **0.06 s** |
| output | 2,260 KB | 2,335 KB |
| GDAL | 3.10.3, from the base image | 3.13.1, from the package |
| `OpenFileGDB` | present | present |
| `Parquet` driver | via `pyarrow` | **present in the build** |
| second language runtime | yes | no |
| second container image | yes | no |
| a channel from server to worker | the job table | a child process's pipe |

The `.gdb` opened, all twelve layers listed with their feature counts, and `EPSG:2952` resolved through
PROJ in both. `VectorTranslate` is `ogr2ogr` as a library call rather than a process, so it is the same
code path without the process.

### What the reversal costs, and none of it is hidden

- **[A-016](../architecture-assumptions.md) and [ADR-009](ADR-009-raster-engine.md) §2.2 are broken as
  written.** *"The serving container ships no GDAL. It exists only in the job worker image."* GDAL now
  ships in the server image, loaded by a child process rather than by the serving process. The rule
  becomes about the **process** rather than the **image**, which is weaker: an image boundary is
  checkable and a process boundary is a convention.
- **[Q-28](../open-questions.md)'s stricter form is given up.** *"No GDAL or OSGeo package reference
  anywhere in the solution"* — mechanically checkable where an image boundary is not — was recorded as
  available to us and is now spent. There is a package reference.
- **[D-06](../architecture-debt.md) comes due.** `Directory.Packages.props` says so in its own header:
  the repayment trigger is *"before the first binary that bundles a database driver, a GDAL build or a
  Python wheel set."* This is that binary, and GDAL's licence is a bill of materials rather than one
  licence.
- **The job record loses its reason to be a channel** but keeps its reason to exist. It is still how a
  caller is told *later*, and it is still ADR-011's first increment; it is no longer the only way the
  server can reach a worker.

### What the reversal buys

One language, one image, one dependency graph, and five times faster on the measurement that matters.
The Python runtime returns to being what [v1-scope](../v1-scope.md) §3c cut it as: the cost of
user-supplied tools, arriving with them and not before.

## 5. Decision

**There are two kinds of job worker, chosen by what bounds the work rather than by what the work is
about.**

A **.NET worker** runs work our own runtime must bound and kill — where the ceiling is enforced by the
runtime we control, and where the input is adversarial because a caller supplied it.
`Graticula.Overlay.Worker` is this kind and does not move.

~~A **Python worker** runs work whose value is in somebody else's ecosystem — reading foreign formats
and, later, running foreign tools. It carries GDAL and a pinned wheel set; the serving container carries
neither, which leaves [A-016](../architecture-assumptions.md) intact. **File Geodatabase import is its
first job.**~~

**Reversed — §5a.** There is **one kind of worker: a .NET child process the server starts and kills.**
Reading a foreign format is one of its jobs, and GDAL is linked into it through
[`MaxRev.Gdal.Core`](https://www.nuget.org/packages/MaxRev.Gdal.Core/) rather than reached through a
second language. **A Python runtime arrives with user-supplied tools and not before**, which is where
[v1-scope](../v1-scope.md) §3c had it.

**The two-kinds rule survives in a narrower and more useful form:** what decides whether work leaves
the serving process is whether its input is chosen by somebody else. Adversarial geometry
([ADR-022](ADR-022-geometry-server.md) §9) and an uploaded archive are both that; a catalogue read is
not. Both now go to the same kind of process, and one of them already existed.

**The boundary between the Python worker and our runtime is a file, and the format is GeoParquet** —
executing [Q-74](../open-questions.md)'s choice rather than making a new one. **The worker never touches
the datastore**: it reads `.gdb`, writes GeoParquet, and our importer reads that and writes the features.
Q-74 rejected a datastore connection for user tools on authorization grounds — a tool that can read the
spatial store bypasses our sharing model and our capability checks — and that boundary is built now
rather than when user tools arrive.

> **Corrected 2026-08-18, hours after this was written, and the first version contradicted
> [ADR-011](ADR-011-job-system.md).** It said *"the worker never holds a database connection"*, full
> stop. ADR-011 §3.2 decided on 2026-08-12 that a worker **claims its own work from the platform
> store** — `SELECT … FOR UPDATE SKIP LOCKED` on PostgreSQL — and §3.3 that it wakes on
> `LISTEN`/`NOTIFY`. So the sentence as written forbade the mechanism the job system already has.
>
> **The distinction that makes both right is which store.** The **platform store** holds the job table;
> a worker claiming its own row bypasses nothing and is how work reaches it at all. The **datastore**
> holds the features, and that is what Q-74's argument is about. Conflating the two produced a
> constraint that sounded stronger and was simply wrong.
>
> **Found while wiring the worker to the server**, by asking how a job would reach a separate container
> and checking whether anything had already decided it. Something had, six days earlier. Recorded here
> rather than quietly narrowed, because a decision that contradicts an existing one and is then edited
> to agree leaves no trace of what was believed — and the contradiction sweep exists because that trace
> is what nobody has.

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
  of the owner's real geodatabases and listed their layers, unpacking member by member in memory —
  [file-geodatabase-readers.md](../research/file-geodatabase-readers.md) §8b.

  **Narrowed the same day, because the withdrawal over-claimed.** `/vsizip/` removes the *extraction*,
  not the *storage*: an upload is a stream and a worker is handed a path, so the archive itself is
  still written to one scratch file. What is gone is unpacking hundreds of members onto our disk and
  the zip-bomb expansion that came with it — one file at its transferred size, against an archive that
  can expand arbitrarily. **So this consequence is smaller than first written and larger than the
  withdrawal claimed**, and the line exists because correcting a correction costs less than leaving a
  document that denies a capability it needs.
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

1. **DISCHARGED 2026-08-19.** **The rule that separates the two kinds is written where a worker is
   added**, not only here. A job placed in the wrong worker is the failure this ADR exists to prevent,
   and an ADR is not where somebody looks while writing one. It is now in three places a person writing
   a worker cannot miss: `NativeDependencyTests.Confined` names each confined library, its one project
   and the reason in one table; `Graticula.Import.Reader.csproj` and `Graticula.Overlay.Worker.csproj`
   each open with why they are an executable rather than a library; and `GeodatabaseReader`'s class note
   states the difference between the two kinds — the overlay worker is pooled because a launch dominates
   a request-path operation, and the reader is a process per archive because nothing should survive
   between two files somebody else chose.
2. **Restated 2026-08-19, and still open. The GDAL build ships with its drivers and their licences
   enumerated**, per [DEPENDENCY-LICENSES.md](../../DEPENDENCY-LICENSES.md)'s own warning that a GDAL
   build is a bill of materials rather than one licence. It said *the Python worker's image*, and §5a
   removed both the Python worker and the image — the drivers now arrive as
   `MaxRev.Gdal.WindowsRuntime.Minimal` and `MaxRev.Gdal.LinuxRuntime.Minimal` package payloads, which
   is a **narrower** set than a distribution build and still not an enumerated one. **The condition
   survives the reversal because the obligation is about what we distribute, not about how it was
   packaged.** What is known so far is only what has been asked for: `OpenFileGDB` and `Parquet` are
   present, asserted by `GeodatabaseReaderTests`. Nothing has listed the rest.
3. **PARTLY DISCHARGED 2026-08-19, and restated because its subject is gone.** It read *a test fails
   the build if the **Python worker** acquires a path to user-supplied code* — and there is no Python
   worker. The reason it existed is untouched: the distinction between *our script* and *their tool* is
   one sentence wide and is the whole of why [Q-75](../open-questions.md) is not reopened by this
   decision, and a sentence is not a guard. **So it now applies to the reader, which is the process a
   path could reach.** What is met: the wire is three named operations with named string arguments,
   nothing evaluates, and `GeodatabaseReaderTests` asserts that an operation the reader does not have
   comes back as a refusal rather than being attempted. ***(Discharged later the same day, in full, and by
   the event it anticipated: ADR-038 added a fourth operation — `features` — and the closing test was
   written with it.
   `GeodatabaseReaderTests.The_reader_answers_exactly_the_operations_its_refusal_names` parses the
   reader's own refusal for the names it lists, asserts that set is exactly `ping`, `layers`, `convert`,
   `features`, and then asks for each one to check the sentence is not listing something the reader does
   not answer. Falsified by staling the sentence. `PARTLY DISCHARGED` above is left standing rather than
   rewritten, because the state it recorded was true for six hours.)***
4. ~~**No GDAL or OSGeo package reference appears in the solution.** Verified absent 2026-08-18;
   Q-28's stricter form is mechanically checkable where an image boundary is not, so it is checked.~~
   ***(Withdrawn 2026-08-19 — §5a spent it the same day it was written. There is a
   `MaxRev.Gdal.Core` reference now, and a condition that the decision above makes impossible is not
   outstanding work; it is a decision recorded twice. [D-88](../architecture-debt.md).)***
   **Replaced by what remains checkable, and it is checked: a confined dependency is referenced by its
   one project and no other, and the serving project cannot reach it through a project reference.**
   `NativeDependencyTests` asserts both directions, including the arm that fails when the rule stops
   asserting anything — and it honours `ReferenceOutputAssembly="false"`, which is the mechanism the
   host uses to build a worker without linking it. **The replacement is met.** What was genuinely lost
   is checkability of the *image*, not the isolation: `GeodatabaseInspector` spawns a child and the
   serving process never loads GDAL.
5. **PARTLY DISCHARGED 2026-08-18** — ADR-024 condition 2's rule, which found a winding-order defect
   in the shapefile reader. **The corpus exists:** the owner supplied three of their own geodatabases,
   and `OpenFileGDB` opened all three and listed 12, 55 and 8 layers with relationships, domains, field
   aliases and resolved EPSG codes. So the adopted reader is verified against data this project did not
   write, which is the half that could be done before a worker exists. ~~**What is still owed is an
   actual import** — reading a layer is not writing one, and the geometry, the encoding and the Z drop
   are only settled by a round trip.~~ ***(Discharged 2026-08-19: all three archives round-tripped into
   PostGIS through ADR-038's publish. 67 of 69 publishable layers landed — 20,001 features, EPSG:2952
   stored without reprojection, counts equal to what the inspection reported — and the two that did not
   are D-105 and D-106, both found by this round trip and neither predicted by reading a layer. The Z
   drop is settled and is not free: 25D is the common case in this data, the hosted table is 2D, and the
   job now reports per layer how many features carried an elevation (D-107). §8g has the numbers.)***
   Details, including a file type the published specification does
   not cover, in [file-geodatabase-readers.md](../research/file-geodatabase-readers.md) §8b.
6. ~~**The temporary extraction directory has stated bounds and is cleaned**, with the same shape of
   argument `ArchiveLimits.ForShapefile` carries: numbers derived from the format rather than round.~~
   ***(Withdrawn 2026-08-18 — there is no extraction directory. See §6's negative consequences.)***
   **Replaced by a narrower condition, since the withdrawal over-claimed:** the *uploaded archive* is
   still written to one scratch file, because a worker is handed a path rather than a stream — so that
   file has a stated maximum size and is removed whether the job succeeds or fails. What is gone is the
   expansion: with `/vsizip/` GDAL opens the archive inside the worker, nothing unpacks onto our disk,
   `BoundedArchive` is not in the path, and the bomb defence is the worker's memory and time bound —
   this ADR's own process boundary. Measured on the way: the owner's
   archives run to 338 members and one member compressing **430×**, against
   `ArchiveLimits.ForShapefile`'s 32 and 100×, so the shapefile numbers were never going to serve both
   formats. A ratio calibrated for two formats at once is a ratio calibrated for neither.

   **The replacement is DISCHARGED 2026-08-19.** `ImportScratch` is that scratch file and its bounds:
   `GisServer:ImportScratchBudgetMB` caps what may be resident in the directory at once — 2 GB by
   default, three orders of magnitude above the owner's largest archive and derived from the format
   rather than round — and the file is deleted in a `finally` whether the job succeeded or failed,
   because the failure path is where an archive stays for ever and then refuses the next upload for a
   reason nobody can see. The file is named by job id and nothing else, so a path is never composed out
   of a string somebody else chose, and a dirty directory can be reconciled against the job table.

   **A note on how this item counts, because it counts wrong in an instructive way.**
   [tools/conditions.py](../../tools/conditions.py) treats a leading `~~` as discharged, so this item
   has read as *done* since the withdrawal — while the narrower condition inside it was open for a day.
   The count was accidentally right and is now actually right. A withdrawal that carries a live
   replacement is a shape the tool cannot see, and there is exactly one of them.

## 8. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| [A-016](../architecture-assumptions.md) | GDAL-backed providers can be made optional, so a PostGIS-only deployment ships as one artefact | `VALIDATED` by design decision — and this ADR keeps it, since the serving container gains nothing |
| [A-038](../architecture-assumptions.md) | GDAL is needed for the import formats we care about | `INVALIDATED` — and the invalidation is what made this a cost decision rather than a forced one |
| [A-049](../architecture-assumptions.md) | A curated Python wheel set can cover realistic work without pip at runtime | `UNVALIDATED`, and load-bearing sooner than ADR-016 planned |
| [A-042](../architecture-assumptions.md) | Caps on vertex count, batch size and wall clock bound overlay work | `INVALIDATED` — the reason the .NET worker exists, and the reason it stays |

## 9. Dependencies

**Depends on** — [ADR-011](ADR-011-job-system.md) (the job system this is a worker for, and whose
§3.2 claim mechanism this ADR's first version contradicted — see §5),
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
