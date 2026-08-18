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

## 8b. Measured against three real geodatabases, and two of §8's conclusions were wrong

**The owner supplied three of their own** on 2026-08-18 — the corpus ADR-037 condition 5 asks for, and
the thing §5 said everything here was missing. **The data is a client's and stays out of this
repository:** what follows is structure and counts, never a layer name or a value. Read with
`ogrinfo` from `ghcr.io/osgeo/gdal:ubuntu-small-latest`, network disabled, the folder mounted
read-only.

| | archive A | archive B | archive C |
|---|---|---|---|
| members | 122 | **338** | 96 |
| uncompressed | 2.0 MB | 11.5 MB | 0.5 MB |
| whole-archive ratio | 6.2× | 2.9× | 8.8× |
| **worst single-member ratio** | | **430×** | |
| layers | 12 | **55** | 8 |
| relationships | **6** | 0 | 0 |

### What this corrects

**1. There is no need to extract anything to disk.** §8 and [ADR-037](../adr/ADR-037-job-workers-come-in-two-kinds.md)
both said `pyogrio` cannot read a stream, so a `.gdb` must be unpacked into a temporary directory — and
ADR-037 listed *"import gains a disk dependency"* as a negative consequence with a condition attached to
bounding it. **GDAL reads inside the archive**: `ogrinfo /vsizip//data/x.gdb.zip` opened all three with
the `OpenFileGDB` driver and listed their layers. The virtual filesystem does the unpacking, in memory,
member by member. So the temporary-directory capability, its bounds and its cleanup are **not needed**,
and the consequence and condition are withdrawn rather than deferred.

**2. The operator does not supply a coordinate system.** Our shapefile import requires `srid` and
refuses to infer it, for a stated reason: a `.prj` is bare WKT and *"matching WKT to a code by comparing
strings is how a layer comes to declare a system it is not in."* **That reasoning does not transfer.**
GDAL returned `ID["EPSG",2952]` — NAD83(CSRS) / MTM zone 10 — resolved through PROJ's authority
database rather than by string comparison, with the full parameter set and the area of use. A
geodatabase import therefore has an authoritative code and asking for one would be asking the operator
to confirm something the file already says better than they can.

### What this adds, and it is larger than the corrections

**3. A real geodatabase holds four kinds of thing our import has no place for.**

- **Attachments.** Archive A carries six `__ATTACH` tables, each with geometry type `None` — plain
  tables holding the files attached to features.
- **Relationship classes.** Six, all `Composite`, each binding a feature class to its attachment table.
  [ADR-013](../adr/ADR-013-feature-service-data-model.md) has both concepts, and
  `RelationshipEndpoints` says reading them out of a geodatabase's system tables would be Esri internals
  — **GDAL reports them as a first-class listing**, which is §7 of this note arriving with evidence.
- **Coded value domains.** A field came back carrying `domain name=…`, so the valid values are a
  declared set. We have no domains.
- **Field aliases.** `alternative name="…"` beside the column name.

**An import that reads the feature classes and stops loses all four, silently.** That is the finding
that matters most, and it is not a reader problem — `OpenFileGDB` surfaces every one of them. It is a
question about what *our* import is for, and it has to be answered before the worker writes anything,
because "we imported your geodatabase" is a sentence a client will read as including their photographs.

**4. Z is on most of the owner's real layers.** Archive B and C are full of `3D Multi Polygon`,
`3D Multi Line String` and `3D Point`. Our shapefile reader drops Z and M and
[ADR-024](../adr/ADR-024-shapefile-import.md) condition 5 made it say so in the import response; the
same report is owed here, and it will fire on nearly every layer rather than occasionally.

**5. Fifty-five layers in one archive.** The layer picker is not a nicety. And twelve of archive A's are
`__ATTACH` tables with no geometry, so a picker that lists everything the driver reports would offer
six things nobody wants to publish beside six they do.

**6. Our archive bounds would refuse all three, on two separate grounds** — 338 members against
`ArchiveLimits.ForShapefile`'s 32, and a single member compressing **430×** against the 100× cap. The
cap is not wrong; it is calibrated for shapefile content, which compresses 2–20×, while a `.gdbtablx`
is a sparse index and compresses far harder. **And the resolution is that neither number applies**: with
`/vsizip/` the archive is opened by GDAL inside the worker, so `BoundedArchive` is not in the path at
all. The bomb defence becomes the worker's memory and time bound — which is what
[ADR-037](../adr/ADR-037-job-workers-come-in-two-kinds.md)'s process boundary is for, and a better
answer than a ratio nobody could calibrate for two formats at once.

### And one thing the specification does not cover

Across the three archives: 102 `.gdbtable`, 102 `.gdbtablx`, 99 `.gdbindexes`, 95 `.atx`, 72 `.spx`,
5 `.freelist` — and **72 `.horizon` files**, which are in none of the six extensions the
[FGDB Spec](https://github.com/rouault/dump_gdbtable/wiki/FGDB-Spec) enumerates. Whatever they are,
they are in every one of the owner's geodatabases and not in the published specification.

**That is §5's argument arriving as evidence rather than as an estimate.** A reader written from the
specification would have met an undocumented file type in its first real archive — not as an exception,
but as seventy-two files it had no rule for. `OpenFileGDB` read all three without complaint.

## 8c. The Python half of the survey, and why the raster answer does not transfer

**The owner asked the sharpest form of the question**, 2026-08-18: *"raster'i gdal'siz hallettiysek gdb
için gdal zorunlu mu. onu gdalsız python ile kapatamaz mıyız?"* — and it found a real gap. §1–§2
surveyed **.NET** and said so; nothing here had looked at Python, where the ecosystem is larger and the
answer could have been different.

**It is not.** Every open-source route to reading a `.gdb` from Python goes through GDAL:

| Candidate | Claim | Actually |
|---|---|---|
| [`pyogrio`](https://github.com/geopandas/pyogrio) | GDAL bindings | GDAL, and honest about it |
| `fiona` | GDAL bindings | GDAL |
| [`fgdb-to-gpkg`](https://pypi.org/project/fgdb-to-gpkg/) | *"does not have a dependency on ArcPy"* | **True and not the question.** No ArcPy is not no GDAL; it reads through the GDAL stack |
| [`esri-converter`](https://github.com/mihiarc/esri-converter) | *"No CLI Dependencies: Pure Python library"* | **Depends on Fiona.** *Pure Python* here means *no subprocess*, not *no native library*. 2 stars, 3 commits |

The distinction those last two blur is worth stating once: **not shelling out to `ogr2ogr` is not the
same as not linking GDAL**, and a README that says *pure Python* usually means the first.

### Why the raster answer does not carry over, and it is not a technical limit

[ADR-009](../adr/ADR-009-raster-engine.md) §2.1's answer works because **COG is already the served
format**. A customer whose imagery is COG needs no conversion, so no reader is needed at all and GDAL
never loads. The trick is not that raster is easier — it is that the customer's file is *already in our
format*.

**There is no such case for `.gdb`.** We store features in PostGIS and serve them as FeatureServer; a
geodatabase is never our storage format and never our served format, so it always has to be read and
converted. The exemption raster gets does not exist here, and no amount of Python changes that.

### Which leaves four routes, and the fourth was missed

1. **GDAL in the Python worker** — [ADR-037](../adr/ADR-037-job-workers-come-in-two-kinds.md). Optional
   at the cost of exactly this one feature.
2. **Write the parser ourselves** — §5's cost, and §8b's seventy-two `.horizon` files say what the first
   real archive does to it.
3. **The customer converts first** — which is today's behaviour and what the refusal says: ArcGIS Pro
   exports a feature class to a shapefile or GeoJSON, both of which we already read. **Free, honest, and
   the friction ADR-024 §1 says this product exists to remove.**
4. **A one-shot migration converter, outside the deployment.** [v1-scope](../v1-scope.md) §3 already has
   *"migration tooling — inventory scan and definition import, free"*, and a `.gdb`-to-something
   converter is migration tooling rather than a server capability. GDAL would live in a tool the
   customer runs once, never in anything they operate. **This is the only route that gets the feature
   with no GDAL anywhere in the running system**, and §1–§8 missed it entirely by treating the question
   as *which library does the server load*.

**The honest weakness of the fourth** is that it is a thin wrapper over one `ogr2ogr` command, and the
customer could run that command themselves — so what it really ships is a documented recipe and a
support burden. For the organisation this product is aimed at, *"run this Docker command against your
geodatabase first"* is friction of exactly the kind the product exists to remove, and it lands on the
customer least equipped to absorb it. That is the trade, and it is the owner's to make rather than mine.

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
