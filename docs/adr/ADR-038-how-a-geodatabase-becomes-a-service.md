# ADR-038 — How a geodatabase becomes one service with many layers

**Status:** ACCEPTED WITH CONDITIONS
**Confidence:** MEDIUM
**Date:** 2026-08-19

---

## 1. Context

A File Geodatabase is read end to end today: an upload opens a `geodatabase.inspect` job, the job spawns
`Graticula.Import.Reader`, and the console lists what is inside. Measured against the owner's three real
archives — 12, 55 and 8 layers ([file-geodatabase-readers.md](../research/file-geodatabase-readers.md)
§8f). What does not exist is the next step: **publishing the layers.**

**The owner settled the shape it must take, 2026-08-19:**

> *"mesela o gdb içerisinde 20'den fazla katman var. hepsi hosted db'ye import edildi, ve o servis adı
> altında publish edildi. yani db'den direkt servis publish edilmeyecek. servis ve katman ayrı şeyler.
> bir serviste n katman olabilir."*

One archive becomes **one service** holding **N layers**, each its own table in the datastore. Which
sounds like what the product already does and is not: every route into hosted data today creates one
service holding one layer, so *service* and *layer* have been interchangeable in practice and are about
to stop being.

## 2. What is already decided elsewhere, and must not be re-decided here

- **[ADR-011](ADR-011-job-system.md) §3.2** — a worker claims its own work with `for update skip
  locked`. `GeodatabaseInspector` does this; an import job uses the same protocol.
- **[ADR-037](ADR-037-job-workers-come-in-two-kinds.md) §5** — the worker never touches the *datastore*.
  It reads a file and hands something back; this server writes the features. So *the reader writes
  straight into PostGIS* is not an option, and it is the fastest one there is.
- **[ADR-024](ADR-024-shapefile-import.md)** — an import declares its coordinate system rather than
  guessing. For a geodatabase the reader resolves it through PROJ's authority tables, which is asking
  rather than guessing, and §5j's shapefile amendment applies the same mechanism.
- **The mechanism for a second layer in one service already exists**: the registered-table form's *Into
  service* field adds a layer at the next free index. What is missing is the geodatabase import using it.

## 3. The question this ADR answers

**In what form do a layer's features cross from the reader process into this server?**

## 4. Alternatives

### Alternative A — GeoParquet in the scratch directory (the inherited answer)

The reader already has a `convert` operation that writes GeoParquet with WKB geometry, and
[Q-120](../open-questions.md) records the boundary as *"a GeoParquet file and a process, which executes
Q-74's choice rather than making a new one."*

**And the premise under that sentence has gone.** [Q-74](../open-questions.md) chose GeoParquet *because
Python's geospatial stack reads it natively* — `geopandas`, `pyarrow` and `shapely` without a shim. That
was the right reason when the worker was Python. **ADR-037 §5a reversed the worker to .NET on the day it
was decided**, so both ends of this boundary are now .NET, and the reason no longer reaches it.

What it costs here: a Parquet reader **in the serving process** — `Parquet.Net` or Apache Arrow — which
is a new Tier 2 dependency in the one project this repository is most careful about, plus a port
interface to keep the library type out of Tier 1 signatures, plus a second scratch file per layer.

### Alternative B — the features stream over the pipe the processes already have (chosen)

The reader writes one JSON object per line on stdout: the layer's schema first, then a feature per line.
The host reads them as they arrive.

- **No new dependency anywhere.** The host already parses JSON and already owns this pipe.
- **No second scratch file.** The archive is on disk because GDAL opens a path; the output does not need
  to be, because the host is right there.
- **It streams, which is the property the plan wanted and the code lacks.** `ImportedDataset` holds an
  `IReadOnlyList<ImportedFeature>` and `ImportLimits.Default` caps at **1,000,000 features / 50 MB / 250
  columns**. A line-per-feature pipe has no such shape: the ceiling becomes the importer's, not the
  format's.
- **The wire is ours and private.** It is not GeoJSON as a product feature — the owner declined that
  when it was proposed as the *interchange* format (*"geojson a gerek yok"*) — it is two of our own
  processes talking, versioned together and shipped together, the way `GeometryWorkerPool`'s pipe
  already is.

Cost, stated: JSON is verbose. A 3,659-feature layer is nothing; a ten-million-feature layer would move
several times its own weight in bytes through a pipe. That is the trigger in §7.

### Alternative C — the reader writes into PostGIS itself

Fastest by a distance, and refused: ADR-037 §5 says the worker never touches the datastore, and that
sentence is what keeps an untrusted-file parser away from the database this server serves from.

## 5. Decision

**One archive becomes one service. Each chosen feature class becomes one layer in it, at the next free
index.** A `geodatabase.import` job carries the archive's id and the list of layers chosen on the
inspection screen; the runner asks the reader for each in turn and imports it into the named service.

**The features cross as newline-delimited JSON on the pipe that already exists** — Alternative B. The
`convert` operation and its GeoParquet output stay in the reader **unused by this path**, because
Q-74's boundary for the *Python SDK* is a different boundary with the reason still intact, and deleting
it would be spending a decision that was not made here.

**Nothing about the coordinate system changes.** The reader resolves each layer's own EPSG code through
PROJ; a layer it cannot resolve is refused with the layer named, not imported into a guess.

**A partly finished import is reported as partly finished.** Fifty-five layers is fifty-five chances to
fail, and a job that reports only *failed* after importing forty is a job nobody can act on — which is
what `IJobStore` refuses. The job's detail carries, per layer, whether it landed and why not.

## 6. Consequences

- **`service` and `layer` stop being interchangeable in the console**, which
  [ADR-034](ADR-034-server-and-studio.md) §5k has already begun: a service page lists its layers.
- **The 1M-feature ceiling stops applying to this path and still applies to the other two.** A
  shapefile and a GeoJSON upload are still read whole into memory. That asymmetry is a debt, not a
  design, and it is recorded as one.
- **The reader gains a fourth operation** — `features` — and ADR-037 condition 3 is about exactly that:
  the set of operations must stay closed and no test asserts it. This ADR makes that condition due.
- **A second archive format arrives at the same door for free, and must not.** ADR-024 condition 3 says
  a format does not reuse an exception without its own decision; GeoPackage and KML stay refused.

## 7. Conditions

1. **The streaming claim is measured, not asserted.** An import of a layer larger than
   `ImportLimits.Default`'s 50 MB proves the ceiling has moved for this path; without that measurement
   §5 is a hope with a mechanism attached.
2. **The reader's operation set is closed by a test**, discharging ADR-037 condition 3's outstanding
   half. A fifth operation that took a path to code would otherwise pass every test in this repository.
3. **A partly failed import is exercised** — one layer refused among several — and the job's detail
   names which. Falsified rather than trusted, the way the deadline and the claim protocol were.
4. **The asymmetry with the other two import paths is recorded as debt on the day this ships**, with the
   numbers, so that *shapefile is still capped at a million features* is a known state rather than a
   surprise.
5. **The owner's three archives round-trip**, and the count of features in each published layer matches
   what the inspection reported. ADR-024 condition 2's rule: verified against files this project did
   not write.

## 8. Assumptions this decision rests on

- **A-016** as amended 2026-08-19 — the boundary is the process, not the image. The reader loads GDAL
  and the serving process does not.
- That a JSON line per feature is affordable at the scale this product targets (100–1,000 services,
  CLAUDE.md §7). Condition 1 is what turns that into a measurement.

## 9. Dissent

**Against Alternative B, and it is the author's own.** GeoParquet was already written, already measured,
and already the recorded boundary; choosing a JSON pipe means the `convert` operation sits unused and a
reader of this repository finds two answers to *how do features cross* with only one in use. The reply
is that the recorded boundary was chosen for a Python endpoint that no longer exists, and inheriting a
decision whose reason has gone is how a codebase acquires a dependency nobody chose — which is §82's
whole subject. **It does not fully dissolve the objection**, and the revisit trigger is honest: if the
Python SDK arrives and needs GeoParquet anyway, the pipe becomes the odd one out and this section is
where to start reading.
