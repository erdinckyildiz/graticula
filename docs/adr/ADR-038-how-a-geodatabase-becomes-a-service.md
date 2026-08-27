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

**Amended 2026-08-19, the same day it was written: the geometry on that line is WKB, not GeoJSON.**
The line is still newline-delimited JSON and the pipe is still the one that already existed — what
changed is the one field inside it. This is a reversal and it is recorded as one rather than smoothed
into the original text, because the reason it was wrong is worth keeping.

The argument for GeoJSON was that GDAL writes it in one call (`ExportToJson`) and this server has read
that shape since its first import (`GeoJsonGeometry.TryRead`). Both halves are true. What neither half
mentions is that **GeoJSON's coordinate system is part of the format**: RFC 7946 §4 says coordinates are
WGS 84 longitude and latitude, and `GeoJsonGeometry.ReadPosition` enforces it — a position outside
±180/±90 is refused, deliberately, because the alternative is a latitude-first file landing in the wrong
hemisphere with no error at all.

The owner's archives are **EPSG:2952** — MTM zone 10, in metres. The first real publish therefore refused
all eight layers of the first archive, one feature at a time, with *position (271963.2, 4790579.1) is
outside WGS 84*. The check was right. The wire was wrong: a format whose definition pins the coordinate
system cannot carry a projected layer, and a private wire that had to disable that check would be
turning off a guard the public format needs.

**WKB has no opinion about the coordinate system** — the header line declares the EPSG code, and the
importer decides what to store. `WkbReader` is already in this repository, reads straight into the
server's own geometry model rather than through an adopted one, and does two things the GeoJSON path
does not: it **reports** the Z it had to drop (`docs/geometry-crs-policy.md`'s *lossy on read means not
writable*), and it **refuses** curves rather than approximating them (ADR-005 §3.3c). Base64 in the JSON
line costs a third over raw bytes and is far below what decimal text costs.

Two things this reversal did not change: the pipe, and the decision in §5 that one archive becomes one
service. It cost about forty minutes, all of it after the first end-to-end run, which is the argument for
running one.

### Alternative C — the reader writes into PostGIS itself

Fastest by a distance, and refused: ADR-037 §5 says the worker never touches the datastore, and that
sentence is what keeps an untrusted-file parser away from the database this server serves from.

## 5. Decision

**One archive becomes one service. Each chosen feature class becomes one layer in it, at the next free
index.** A `geodatabase.import` job carries the archive's id and the list of layers chosen on the
inspection screen; the runner asks the reader for each in turn and imports it into the named service.

**The features cross as newline-delimited JSON on the pipe that already exists, with each geometry as
base64 WKB** — Alternative B as amended (see §4B: it was GeoJSON for one afternoon and the first real
archive refused every layer). The `convert` operation and its GeoParquet output stay in the reader
**unused by this path**, because Q-74's boundary for the *Python SDK* is a different boundary with the
reason still intact, and deleting it would be spending a decision that was not made here.

**A publish is a second request, naming the inspection.** `POST /admin/hosted/geodatabase` carries the
inspection job's id, the service name and the layers chosen; the archive is still on disk under that
job's own id, so a two-gigabyte upload does not cross the wire twice. What may be named is checked
against what the inspection reported — ordinally, because those names go to `GetLayerByName`, which is
case-sensitive — and the job belongs to its caller, so this is not a way to publish out of somebody
else's upload.

**A service name that is already taken is refused rather than added to.** The catalogue publishes into
an existing service when the name matches, which is how three layers come to share one — and it does not
ask whose service that is. Adding twenty layers to a stranger's service is not what anybody typing a
name meant. D-104 records that the older `POST /admin/publish` has that hole; this endpoint does not open
a second one.

**Nothing about the coordinate system changes.** The reader resolves each layer's own EPSG code through
PROJ; a layer it cannot resolve is refused with the layer named, not imported into a guess.

**An empty feature class is published from the archive's own declaration, not from its rows.** Added
2026-08-19 (D-106) after the first full run refused one: every other import path in this server builds a
hosted table's columns by observing the features it read, which is the only thing a GeoJSON file offers.
A geodatabase declares its schema — the reader's header carries the field list with types and the
geometry type — so an empty layer becomes an empty hosted layer with its fields, which is what
`POST /admin/hosted/define` already does for a designed one. ArcGIS publishes these, and a survey layer
exported before anybody filled it in is the ordinary case rather than an oddity.

Two things that fall back rather than refuse, because refusing a whole feature class over one column
would be the wrong trade: an OGR field type this server does not map becomes text, and a `25D`
declaration becomes its 2D kind — which is what the import does with the geometry itself either way
(D-107). What is still refused is a layer whose *geometry* type is unstorable, because there is nothing
to create the column as. That is an attachment table, and the inspection already says so.

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
- **Elevation in the source is not carried into the hosted table.** Six of the eight layers in the
  owner's smallest archive are `25D`; the hosted table is 2D and the job reports, per layer, how many
  features arrived with a Z. Nothing is written back to the archive, so
  `docs/geometry-crs-policy.md`'s *lossy on read means not writable* rule is not broken — but the loss
  is real, it is the common case rather than an edge, and **D-107** is the entry for it rather than a
  silence.
- **A declared geometry type is not a per-feature fact.** `HaLRT_Locate_Areas` is declared
  `wkbMultiPolygon25D` and 49 of its 51 features carry a Z. The type in the header says what the layer
  is for; only the features say what is in it, which is why the count is reported per layer and not
  derived from the declaration.

**State.** *Catalogue*: the layers an import creates, and the job row that records
it ([ADR-037](ADR-037-job-workers-come-in-two-kinds.md)'s). *Runtime*: the **scratch directory**
the archive is read from, node-local and bounded, holding nothing after the job either way.

## 7. Conditions

**Measured 2026-08-19 against all three of the owner's archives**, end to end through the endpoint, the
job, the reader, the importer and the query surface. The numbers are in
[file-geodatabase-readers.md](../research/file-geodatabase-readers.md) §8g; the services and every table
they created were deleted afterwards, and the archives are the owner's client data and are not in this
repository.

1. **The streaming claim is measured, not asserted.** An import of a layer larger than
   `ImportLimits.Default`'s 50 MB proves the ceiling has moved for this path; without that measurement
   §5 is a hope with a mechanism attached.

   **Open, and now known to be unmeetable as written.** The pipe streams and the importer does not:
   `GeodatabaseImporter` collects the features into an `ImportedDataset`, because that is what
   `PostGisImporter` takes. So the ceiling stopped being the *format's* and became the *importer's*,
   which is a smaller claim than §4B made. The largest layer measured is 3,659 features; nothing here is
   bounded by it today. **D-108** is the entry, and it names what would discharge this: an
   `IAsyncEnumerable` overload on the importer, which is a Tier 1 change and a decision of its own.

2. **The reader's operation set is closed by a test**, discharging ADR-037 condition 3's outstanding
   half. A fifth operation that took a path to code would otherwise pass every test in this repository.

   *(Discharged 2026-08-19 — `GeodatabaseReaderTests.The_reader_answers_exactly_the_operations_its_refusal_names`.
   Two halves: the refusal's own sentence is parsed for the names it lists, and every name it lists is
   then asked for. Falsified by staling the sentence, which failed the test as intended. A hard-coded
   list would have needed editing in the same commit as the change it exists to catch.)*

3. **A partly failed import is exercised** — one layer refused among several — and the job's detail
   names which. Falsified rather than trusted, the way the deadline and the claim protocol were.

   *(Discharged 2026-08-19, and not by construction: the 55-layer archive refused exactly two of its
   layers on its own. `AECOM_Archeological_Assessment_Results` died on PostgreSQL `42701` — two field
   names that agree for sixty characters, which is **D-105**, fixed the same day with a test that
   reproduces the error — and `AECOM_Monitoring_Well_Inventory` holds no features, which is **D-106**.
   Fifty-three layers landed, the job reported per layer with a reason for each refusal, and the service
   holds what landed.)*

4. **The asymmetry with the other two import paths is recorded as debt on the day this ships**, with the
   numbers, so that *shapefile is still capped at a million features* is a known state rather than a
   surprise.

   *(Discharged 2026-08-19 — D-108 carries the numbers and the asymmetry.)*

5. **The owner's three archives round-trip**, and the count of features in each published layer matches
   what the inspection reported. ADR-024 condition 2's rule: verified against files this project did
   not write.

   *(Discharged 2026-08-19. Project Information: 8 of 8 layers, 116 features, 5 s. Environmental: 53 of
   55, 16,806 features, 28 s. PointofInvestigation: 6 of 6 publishable beside 6 attachment tables,
   3,079 features, 5 s. Every published layer's row count equals what the inspection reported, every
   table stored EPSG:2952 without reprojection, and the ArcGIS query surface answered on each. The two
   refusals are condition 3's, not count mismatches.)*

6. **The console's two new screens go through the ux-designer before they ship**, which is the owner's
   standing instruction and the one this repository has broken twice. *"ekranlar tasarlanırken ui-ux
   designer tarafından yapılmasını şart koşmuştum diye hatırlıyorum."*

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
