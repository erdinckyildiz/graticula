# Reading a File Geodatabase without GDAL

**Surveyed 2026-08-18**, on two owner instructions in sequence: *"peki gdb alırken
kullanabileceğimiz açık kaynaklı bir kütüphane yok mu?"*, then *"bulunan şey gdal'a bağımlı
olmasın ama."*

The second sentence is what makes this note worth writing. There **is** an open-source library —
GDAL's `OpenFileGDB` — and the answer to the first question alone would have been *yes, and it is
already the plan*. Ruling GDAL out changes the question from *which library* to *can we write one,
and from what*.

**Answers [Q-108](../open-questions.md).** Nothing here is a decision; the recommendation is still
that this is not v1 work, and it is now recommended against on evidence rather than on estimate.

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
the other direction.

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

## 8. Recommendation

**Unchanged, and now better grounded: not v1.**

- If *no GDAL in the serving artefact* is the rule, it is already satisfied ([A-016](../architecture-assumptions.md)), and `OpenFileGDB` in the worker image is the cheapest complete answer.
- If *no GDAL anywhere* is the rule, the only route is our own reader, and the cost is a multi-month
  binary-format project whose defects are silent. It buys a smaller air-gapped checklist and one
  fewer image — worth doing eventually, wrong to start before v1 ships.
- **The intermediate step worth taking now costs nothing:** say so in the product. A `.gdb.zip`
  uploaded today is refused with *"no shapefile in this archive"*, which is true and useless.

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
