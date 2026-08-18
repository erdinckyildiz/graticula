# Reading a File Geodatabase

**Surveyed 2026-08-18**, over four owner instructions that arrived in sequence and moved the answer
twice. Written in the order they came, because the reasoning is only legible that way:

1. *"peki gdb alırken kullanabileceğimiz açık kaynaklı bir kütüphane yok mu?"*
2. *"bulunan şey gdal'a bağımlı olmasın ama."*
3. *"gdal da ekleyebiliriz. çok büyük bir problem değil."*
4. *"illa .net olmak zorunda değil… geoprocessing araçları pythonda yazılacağı için."*

**The conclusion is in §8 and §9, and it contradicts §1–§7.** Sections 1 to 7 answer the question
under instruction 2 — *no GDAL* — and reach *there is nothing to adopt, so writing our own is the only
route, and it is expensive*. Instructions 3 and 4 remove the premise: with GDAL acceptable and .NET not
required, `pyogrio` in a Python worker reads a `.gdb` in one line.

**The superseded sections are kept rather than rewritten**, because the survey in them is the evidence
the new answer rests on — the cost of writing our own is exactly why adopting is right — and because a
note that shows its own reasoning being overturned is worth more than one that reads as though it
always knew.

**Answers [Q-108](../open-questions.md)**, and opens [Q-120](../open-questions.md).

---

## 1. The finding, in one line

**There is no ready-made, open-source, GDAL-free File Geodatabase reader for .NET.** Three
searches on 2026-08-18 found none, and the two .NET routes that do exist each fail a constraint we
have already set.

**Stated as an absence of evidence, not a proof.** A US-only web search on one day is not a survey
of the world's source code. What it does establish is that there is nothing obvious enough to find
in three attempts, which is the practical form of the question.

## 2. What exists, and why each one is or is not usable

| Route | Language | Licence | GDAL? | Esri SDK? | Usable here |
|---|---|---|---|---|---|
| [GDAL `OpenFileGDB`](https://gdal.org/en/stable/drivers/vector/openfilegdb.html) | C++ | MIT-style, in-tree | **is GDAL** | no | **Only out-of-process.** The recorded plan (A-016, Q-28): worker image, invoked as `ogr2ogr`. Ruled out by the owner's constraint if the constraint means *no GDAL at all* rather than *none in the serving artefact*. |
| [`spark-gdb`](https://github.com/mraad/spark-gdb) | **pure Scala** | Apache-2.0 | **no** | **no** | Not as a dependency — JVM. **Valuable as evidence**, see §3. |
| [`mraad/FileGDB`](https://github.com/mraad/FileGDB) | Java/Scala, Spark | — | no | no | Same: evidence, not a dependency. |
| [`GeoDataToolkit`](https://github.com/g-a-freitas/GeoDataToolkit) | C# | open | no | **yes** | **No.** It exists to *"abstract the ESRI APIs details"*, so it depends on Esri's closed *"free as in beer"* FileGDB API. That is a proprietary runtime dependency with a EULA in a product we give away. |
| [Esri File Geodatabase API](https://github.com/Esri/file-geodatabase-api) | C++ with .NET bindings | closed, free-as-in-beer | no | **is the SDK** | **No**, for the same reason, and it is the thing `OpenFileGDB` was written to avoid. |
| [Aspose.GIS](https://docs.aspose.com/gis/net/gdb-file-esri/) | C# | **commercial** | no | no | **No.** §7's licensing stance is open source, copyleft acceptable; a paid closed library is a distribution constraint on a gift. |

**So the owner's constraint resolves to: write our own.** Which is exactly Q-108, arrived at from
the other direction — **and is superseded by §8.** Read on before acting on this line.

## 3. It is writable, and there are three independent proofs

This matters because [A-038](../architecture-assumptions.md) was `INVALIDATED` on **one** of them —
the peer's reader, seen only as file names — and one proof is thin ground for a cost decision.

1. **GDAL's `OpenFileGDB`**, written from reverse engineering, no proprietary dependency, and now
   far more than a reader: **writes since GDAL 3.6**, relationship classes since 3.6, attribute
   domains since 3.3, raster layers since 3.7, curves supported, reads ArcGIS 9.x as well as 10+.
2. **`spark-gdb`** — a *pure* implementation in a managed language, on the JVM, by somebody who is
   not a GIS vendor. Read-only, Apache-2.0.
3. **The peer's managed .NET reader** (A-038), not public.

## 4. The format is documented, and that is what to build from

**[FGDB Spec](https://github.com/rouault/dump_gdbtable/wiki/FGDB-Spec)** — the reverse-engineered
specification behind `OpenFileGDB`, maintained by its author. It covers `.gdbtable`, `.gdbtablx`,
`.gdbindexes`, `.atx`, `.spx` and `.freelist`, and states that it applies to v10 datasets and
earlier. It describes itself as **work in progress**.

**Build from the specification, not from an implementation, and the distinction is not pedantic.**
CLAUDE.md §5 forbids reproducing *proprietary* source, and neither GDAL's `filegdbtable.cpp` nor
`spark-gdb` is proprietary — so §5 is not the constraint here. The constraint is licence: Apache-2.0
and MIT both attach notice and attribution obligations to derivative work, and a reader whose
structure follows another project's file line by line is derivative whatever the header says. A
specification is documentation. Reading it produces an independent implementation; reading the code
beside it produces an argument later.

## 5. What it would cost, and the useful number is somebody else's TODO list

`spark-gdb` is the closest analogue to what we would write — a pure managed reader, built from this
same specification, by one person. **Where it stopped is the most honest cost estimate available:**

- **Reached:** feature classes with Point, Polyline and Polygon, including X/Y/Z/M.
- **Did not reach**, per its own TODO: **multi-part geometries**, **XML fields**, **Blob fields**,
  **rasters**.
- **Last activity: January 2016.** 34 commits.

So a competent single-author pure implementation gets the common case and stops before the long
tail — and multi-part geometries are not an exotic corner. Our shapefile reader is 860 lines and
that format is documented, stable since 1998, and has one geometry encoding.

**And the failure mode is the part that makes this expensive rather than merely long.** A
mis-parsed varint or a wrong field offset does not throw; it yields a plausible wrong value. Our
shapefile reader was verified against a corpus this project did not write (ADR-024 condition 2) and
a winding-order defect was found *because of it* — the same discipline for `.gdb` needs a corpus of
geodatabases written by ArcGIS versions we do not have.

## 6. Two limits to know before promising the format to anybody

Both are `OpenFileGDB`'s and both would be ours as well, since they are properties of the format's
reverse engineering rather than of any implementation:

- **SDC (Smart Data Compression) and CDF (Compressed Data Format) cannot be read at all.** A
  customer whose geodatabases are compressed is not served by any open-source route.
- **Sparse 64-bit `OBJECTID`s** are *"read-only and incomplete"* even in GDAL.

## 7. One obstacle this survey dissolves

`RelationshipEndpoints` and migration 1229 both record that reading relationship classes out of a
geodatabase's `GDB_ITEMS` system tables is Esri internals, which §5 forbids. **`OpenFileGDB`
exposes relationships as an API and the specification covers the tables**, so the route exists
without touching anybody's internals.

This does **not** change [ADR-013](../adr/ADR-013-feature-service-data-model.md)'s decision —
relationships stay ours and engine-independent, because most PostGIS estates have no geodatabase
anywhere. It removes one stated reason from that argument, which is worth knowing the next time the
argument is made.

## 8. And then the constraint moved twice, which changes the answer

**The owner, later the same day:** *"gdal da ekleyebiliriz. çok büyük bir problem değil. gdalsız gitmeyi
tercih ederdim ama dediğim gibi. çok da önemli değil. bu arada illa .net olmak zorunda değil. ileride
koyacağımız geoprocessing araçları pythonda yazılacağı için, python kütüphanesi kullanılabilirdi."*

Two inputs, and the second is the material one. GDAL becoming acceptable makes §2's first row usable
again; **dropping the .NET requirement makes most of this note's cost analysis irrelevant.**

### The convergence, and half of it was already written down

[Q-74](../open-questions.md) had already reasoned its way to Python on the *outbound* side. Asked how
data crosses into a geoprocessing tool, it rejected giving Python a database connection, rejected
calling our own API for bulk, and chose *materialise the input to a file the tool reads* — concluding
that the interchange format **wants to be one Python's geospatial stack reads natively, which means
GeoParquet or Arrow**, because *"`geopandas`, `pyarrow` and `shapely` all read them without a shim."*

The owner's observation completes the circle on the *inbound* side. The same Python process reads
`.gdb` through [`pyogrio`](https://github.com/geopandas/pyogrio), which is GDAL-backed, vectorised, and
**Arrow-native** — the same boundary format Q-74 arrived at independently. One worker, Arrow in both
directions.

### And it is a line of code, not a project

```python
gdf = geopandas.read_file("estate.gdb", layer="roads", driver="OpenFileGDB", use_arrow=True)
```

`pyogrio` reports **5–10× faster reads and 5–20× faster writes** than Fiona's non-vectorised path, and
`use_arrow=True` with GDAL 3.6+ is faster again. It also reads a geodatabase's **non-spatial tables**,
which a migration needs and which our shapefile path has no equivalent of.

Set against §5's estimate for our own reader — a multi-month binary-format project whose defects are
silent, benchmarked against a single-author pure implementation that stopped before multi-part
geometries — this is not a close call.

### The objection, and why it does not block

v1-scope §3c **cut the Python runtime from the job-worker image**, and Q-75 (sandbox) and Q-76
(dependencies) gate the Python SDK. So does a Python import worker pull forward a decision that was
deliberately deferred?

**No, and the distinction is the whole answer: those questions are about *user-supplied* code.**
Q-75 is *"how is user-supplied Python sandboxed"* — the largest security surface in the product,
arbitrary code execution by design. Q-76 is whether *user* tools may install dependencies; ADR-016
answered it with a curated wheel set we version. A Python process running **our** import script,
against **our** pinned wheels, has neither problem. It is our code in a second language, which is a
packaging cost and not a security surface.

**What it does cost is honest and should be stated:** a second runtime in the deployment, a second
dependency graph to patch, and a wheel set that arrives earlier than ADR-016 planned. Against that it
**removes** the thing Q-108's prize was about — writing and maintaining a reverse-engineered binary
parser — and it arrives in a process that has to exist anyway for Q-17b.

### What this makes into a real question

**The job worker's language is not decided anywhere.** `Graticula.Overlay.Worker` is .NET, and it is a
worker for one reason ADR-022 measured: an overlay that cannot be bounded from inside must be killable
from outside. Nothing in ADR-011 or ADR-016 says what language a *job* worker is written in, and until
today nothing needed to.

Now two things want to be in one: File Geodatabase import, and the geoprocessing runtime. Both point
at Python; the overlay worker points at .NET and has a measured reason. **Two worker kinds is a
defensible answer** — they are different jobs with different reasons — **and it should be a decision
rather than a residue.** Opened as [Q-120](../open-questions.md).

## 9. Recommendation

**Changed by §8, and the change is larger than *not v1*: do not write one at all.**

- **`pyogrio` in a Python job worker**, which is the process Q-17b brings anyway, and Arrow is the
  boundary Q-74 already chose. One line of code against a multi-month parser.
- **Writing our own managed reader is now the wrong project**, not merely a deferred one. Its whole
  prize was removing GDAL; the owner has said GDAL is acceptable, and the remaining prize — one fewer
  runtime — is *lost* rather than won by writing .NET, because the Python runtime arrives with
  geoprocessing regardless.
- **Not v1 still**, because the worker is not built and v1-scope §3c cut its runtime. What changes is
  what v2 does: adopt, not build.
- **The step worth taking now costs nothing and is done:** a `.gdb.zip` is refused by name, pointing at
  Q-108 and this note, instead of *"zip the shapefile's files directly rather than the folder holding
  them"* — advice that cannot be followed for a format whose whole shape is a folder.

### Sources added 2026-08-18

- [`pyogrio`](https://github.com/geopandas/pyogrio) and [on PyPI](https://pypi.org/project/pyogrio)
- [GeoPandas — reading and writing files](https://geopandas.org/en/stable/docs/user_guide/io.html)

## Sources

- [GDAL — OpenFileGDB driver](https://gdal.org/en/stable/drivers/vector/openfilegdb.html)
- [GDAL — FileGDB (SDK) driver](https://gdal.org/en/stable/drivers/vector/filegdb.html)
- [GDAL `LICENSE.TXT`](https://github.com/OSGeo/gdal/blob/master/LICENSE.TXT) — the bill-of-materials point, confirmed
- [FGDB Spec wiki](https://github.com/rouault/dump_gdbtable/wiki/FGDB-Spec)
- [Even Rouault — *FileGDB format reverse-engineered*](http://erouault.blogspot.com/2013/10/filegdb-format-reverse-engineered.html)
- [`mraad/spark-gdb`](https://github.com/mraad/spark-gdb)
- [`mraad/FileGDB`](https://github.com/mraad/FileGDB)
- [`g-a-freitas/GeoDataToolkit`](https://github.com/g-a-freitas/GeoDataToolkit)
- [Esri `file-geodatabase-api`](https://github.com/Esri/file-geodatabase-api)
- [Aspose.GIS — Esri GDB in C#](https://docs.aspose.com/gis/net/gdb-file-esri/)
