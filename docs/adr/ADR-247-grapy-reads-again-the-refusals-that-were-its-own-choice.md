# ADR-247 — grapy builds the storage properties a GeoPackage has, the random number generators, and toolboxes in a session, and refuses only what needs Esri's format, licensing or catalogue

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-10-31, under the owner's round-two instruction (*"go on until everything is done, then go back to the start and look at the rest once more"*): every refusal that carries a reason is read again. These three groups were refused for a reason that was grapy's own choice rather than the name's. The choices the pages leave open are INFERRED and listed for confirmation in §5.5 and [Q-217](../open-questions.md). |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

[ADR-224](ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md)'s classification refused three groups in batches:

- thirteen `arcpy.management` names as *"a storage property of file or mobile geodatabases with no counterpart in
  grapy's GeoPackages"*;
- `arcpy.CreateRandomValueGenerator` and `arcpy.RandomNumberGenerator` because *"its generators' streams are Esri's, and
  a seeded stream must match to be the same answer"*;
- nine `arcpy` names (AddToolbox, ImportToolbox, RemoveToolbox, ListToolboxes, ListTools, Usage, GetParameterInfo,
  GetParameterValue, Command) because *"it reads Esri's toolbox catalogue"*.

Each reason was grapy's own choice, not the name's. Four of the storage names have a GeoPackage counterpart. grapy's
statistics tools already draw from a seeded PCG64 that differs from Esri's stream, through `management._seed()`, so
*"a seeded stream must match"* is a rule grapy does not apply anywhere else. And a Python toolbox, which grapy's runner
already loads (ADR-223), is a toolbox of the user's, not a catalogue of Esri's.

## 2. Alternatives considered

### Alternative A — read each page, build what it defines on a GeoPackage, a seeded stream and a `.pyt`, refuse the rest by name (chosen)

**Argument for.** The pages define the answers. AddFullTextIndex lists SQLite among its inputs, and SQLite has FTS5.
CalculateDefaultClusterTolerance's rule is on its page. The generators' page names Mersenne Twister mt19937, which is
published. ImportToolbox's page defines what an imported toolbox is: an alias, its tools as `arcpy.<tool>_<alias>` and
`arcpy.<alias>.<tool>`, and a module whose `__all__` lists them.

**Argument against.** Two storage tools, MigrateObjectIDTo64Bit and MigrateDateFieldToHighPrecision, do no work on a
GeoPackage because a GeoPackage already has the property they give. A reader may take a check that answers the input
for a tool that does nothing.

### Alternative B — keep the batch refusals

**Argument against.** ADR-224 §5.6 and the owner's ruling forbid it: the reason must belong to the name.

## 3. Counterarguments to the preferred option

- The two migration tools are checks: a GeoPackage's keys are 64-bit, and its `DATETIME` text keeps fractions of a
  second. Building them as checks means a script that calls them before relying on the property keeps running. The
  checks refuse what the pages refuse, such as a joined layer or a field that is not a date.
- CalculateDefaultClusterTolerance and CalculateDefaultGridIndex have no Pro page. Pro's tool reference table of
  contents (`tool-reference/toc.json`) does not list them, but Pro's arcpy still answers the names. ArcMap's pages are
  the only public statement, as in ADR-245 §3.
- The brief suggested refusing Usage for grapy's system tools as catalogue text. The page says Usage answers *"the
  tool's syntax"*. A grapy tool's syntax is its own function's signature, so Usage is built. Usage's answer can list
  fewer parameters than arcpy's, where grapy accepts a trailing parameter without naming it (§5.5 (h)).

## 4. Evidence

Every page below was read on 2026-10-31 at `https://doc.esri.com/en/arcgis-pro/latest/`, unless another host is named.

| Claim | Evidence | Source |
|---|---|---|
| AddFullTextIndex takes SQLite | *"a SQLite geodatabase, or a mobile geodatabase. File geodatabases are not supported"*; for SQLite *"ArcGIS generates and manages the index name"*; views are not supported | `tool-reference/data-management/add-full-text-index.html` |
| grapy-env's SQLite has FTS5 | `pragma compile_options` lists `ENABLE_FTS5` and `ENABLE_RTREE` (SQLite 3.49.1) | measured |
| External content FTS5 kept in step by triggers | the three triggers of §4.4.3 | sqlite.org/fts5.html |
| AlterGeometryIndexPreference is SAP HANA's | *"in an SAP HANA database"*; preferences DEFAULT, RDICT, RTREE | `alter-geometry-index-preference.html` |
| The default XY tolerance | *"equivalent to 0.001 meter"*; a geodatabase feature class answers its XY tolerance or its topology's cluster tolerance | desktop.arcgis.com `tools/data-management-toolbox/calculate-default-xy-tolerance.htm` |
| The grid index rule is not given | *"based on the spatial reference, average feature size, and number of features"*; no formula | desktop.arcgis.com `calculate-default-spatial-grid-index.htm` |
| Neither has a Pro page | 404 at doc.esri.com and pro.arcgis.com (latest, 3.3); absent from `tool-reference/toc.json` | measured |
| 64-bit object IDs | a rowid table's key *"is always a 64-bit signed integer"*; a GeoPackage feature table's key is `INTEGER PRIMARY KEY AUTOINCREMENT` | sqlite.org/rowidtable.html; OGC GeoPackage 1.3 Req. 29 |
| High precision dates | *"High precision date fields support millisecond time"*; GeoPackage `DATETIME` is ISO 8601 with fractional seconds | `migratedatefieldtohighprecision.html`; GeoPackage 1.3 §1.1.1.1.1 |
| MigrateTextField is Oracle's | *"must be from an enterprise geodatabase in Oracle"* | `migrate-text-field.html` |
| Compression is unpublished and read-only | *"Once compressed, a feature class or table is read-only"*; no layout given | `compress-file-geodatabase-data.html` |
| RecoverFileGDB reads file geodatabase headers | *"opens each of the tables in the damaged file geodatabase directory, reads the file's header"* | `how-recover-file-geodatabase-works.html` |
| Licensed file geodatabases | `.licdef` and `.sdlic` files, their structure undocumented | `generate-licensed-file-geodatabase.html`, `generate-file-geodatabase-license.html` |
| UpgradeDataset's inputs | mosaic, network, annotation, dimension datasets, parcel fabrics, trace and utility networks, 3D object classes, upgraded to *"the current ArcGIS release"* | `upgrade-dataset.html` |
| The generators | ACM599 (default), MERSENNE_TWISTER (*"Mersenne Twister mt19937"*), STANDARD_C; seed an integer, default 0; `"99 ACM599"`; a RandomNumberGenerator with `exportToString` and `loadFromString` | `arcpy/functions/createrandomvaluegenerator.html`; `arcpy/classes/randomnumbergenerator.html`; `tool-reference/environment-settings/random-number-generator.html` |
| mt19937's reference outputs | seeded 5489, first output 3499211612; 10,000th 4123659995 | Matsumoto and Nishimura 1998; ISO/IEC 14882 [rand.predef] |
| C's `rand` is implementation-defined | the standard gives one implementation only as an example | ISO/IEC 9899:2018 §7.22.2.1 |
| ImportToolbox and AddToolbox | equivalent; `module_name` is required when the toolbox has no alias; the tools are `arcpy.<toolname>_<alias>` or `arcpy.<alias>.<toolname>`; returns a module whose `__all__` names them | `arcpy/functions/importtoolbox.html`, `addtoolbox.html` |
| `.atbx` is not published | Esri describes it as *"a zip archive containing json and other text files"* and says it is working to make the specification public | community.esri.com (Esri staff, the `.atbx` blog and the *Change .tbx format* idea) |
| RemoveToolbox | removes the tools *"only from the underlying geoprocessor object. It does not directly remove them from ArcPy"* | `arcpy/functions/removetoolbox.html` |
| ListToolboxes' form | toolboxes are printed as `toolbox_name(toolbox_alias)` | `arcpy/geoprocessing_and_python/listing-tools-toolboxes-and-environment-settings.html` |
| GetParameterValue's example | `CreateFeatureClass_management`'s `geometry_type` (index 2) defaults to `POLYGON` | `arcpy/functions/getparametervalue.html` |
| GetParameterInfo | a list of Parameter objects; no tool name inside a script tool's validation | `arcpy/functions/getparameterinfo.html` |
| Command | the legacy Command Line syntax, parameters separated by spaces; returns the messages, or only the errors when the tool fails | `arcpy/functions/command.html` |
| Tests | `src/grapy/tests/test_adr247.py`: the mt19937 reference outputs, the environment page's `"99 ACM599"`, the GetParameterValue page's example, FTS5 following inserts, updates and deletes, the tolerance on projected, geographic and topology data, a 2⁴⁰ object ID, a millisecond date, a `.pyt` imported, run, listed, removed and run through Command | 17 tests |

## 5. Decision

### 5.1 Storage properties (`grapy._storage`)

**Built:**

- **AddFullTextIndex** is an FTS5 external content table `grapy_fts_<table>` over the named text fields. It is filled
  with `rebuild` and kept in step by SQLite's three documented triggers. A where clause reads it:
  `fid in (select rowid from "grapy_fts_<table>" where "grapy_fts_<table>" match 'term')`. A view is refused, as
  documented. `index_name`, `catalog_name` and `sync_on_commit` are ignored, as the page says for SQLite.
- **CalculateDefaultClusterTolerance** answers a grapy topology's cluster tolerance (ADR-225), else 0.001 m in the
  data's units (8.983152841195214e-9° on geographic data). This is the value CreateTopology already uses.
- **MigrateObjectIDTo64Bit** and **MigrateDateFieldToHighPrecision** are checks, as §3 says.

**Refused, each by its own reason:**

| Name | Reason |
|---|---|
| AlterGeometryIndexPreference | SAP HANA only, which a fenced tool cannot reach. A GeoPackage has one spatial index kind. |
| CalculateDefaultGridIndex | Grid sizes from a rule the page does not give. A GeoPackage R-tree has no grid sizes. |
| CompressFileGeodatabaseData, UncompressFileGeodatabaseData | Esri's compressed read-only storage is unpublished. A GeoPackage has no compressed form. |
| RecoverFileGDB | Reads damaged `.gdbtable` headers. grapy reads no file geodatabase. |
| GenerateFgdbLicense, GenerateLicensedFgdb | Esri's licensing, which is unpublished. |
| UpgradeDataset | grapy's dataset tables have one form and no ArcGIS release, so there is nothing to upgrade from. |
| MigrateTextField | Oracle only. SQLite's TEXT has no Unicode and non-Unicode kinds. |

### 5.2 Random number generators (`grapy._randomgen`)

**CreateRandomValueGenerator** and **RandomNumberGenerator** are built. `env.randomGenerator` takes the documented
forms: `"<seed> <generator>"`, a seed alone, or the object. `management._seed()` answers what
`numpy.random.default_rng` is seeded with, so every seeded tool follows the generator:

- MERSENNE_TWISTER is NumPy's MT19937 in the state `init_genrand(seed)` gives (Matsumoto and Nishimura 1998). It is
  checked against the reference outputs.
- ACM599 and STANDARD_C are NumPy's PCG64 from the seed (O'Neill 2014). This is what grapy's tools drew before.

The build is an **equivalent** (ADR-224 §5.6). ArcGIS's seeding of its mt19937 is not published, and a tool's draws
from the raw stream are NumPy's. `_binsizes` now seeds through `default_rng`, which is the same PCG64 stream for an
integer seed.

### 5.3 Toolboxes in a session (`grapy._toolboxes`)

**Built:**

- **ImportToolbox** and **AddToolbox** import a Python toolbox (`.pyt`). The alias is `module_name`, else the toolbox's
  own. The tools become `arcpy.<tool>_<alias>` and `arcpy.<alias>.<tool>`. A tool runs with its values in order or by
  name: it is validated (`isLicensed`, `updateParameters`, `updateMessages`), then executed. Its Result holds the
  output parameters. Any failure raises ExecuteError.
- **RemoveToolbox** removes a toolbox from the session but leaves the `arcpy` names, as documented.
- **ListToolboxes** and **ListTools** list grapy's thirteen tool modules and the imported toolboxes, as
  `label(alias)` and `<tool>_<alias>`.
- **Usage** answers `<tool>_<alias>(required, {optional})`.
- **GetParameterValue** answers a `.pyt` parameter's value, or a grapy function's default.
- **GetParameterInfo** answers the running tool's parameters when no name is given, or a `.pyt` tool's
  `getParameterInfo()`.
- **Command** answers the messages, or only the errors when the tool fails.

**Refused (raised by the call, with its reason):**

- a `.tbx` or `.atbx` toolbox, whose formats are unpublished;
- a service or `.ags` connection, because a fenced tool has no network (ADR-223 §5.3);
- GetParameterInfo of a grapy system tool, because a Parameter's data type and direction for a system tool are Esri's
  catalogue.

### 5.4 Counts

Measured against the branch at 68652828:

| Scope | Built | Equivalents | Refused |
|---|---|---|---|
| All modules | 1,468 → 1,483 | 231 → 232 | 689 → 674 (of 2,174) |
| `arcpy` | 104 → 115 | 0 → 1 | 79 → 68 |
| `arcpy.management` | 245 → 249 | — | 202 → 198 |

### 5.5 INFERRED choices ([Q-217](../open-questions.md))

- (a) ACM599 and STANDARD_C draw from PCG64, not from Algorithm 599's generator or a C library's `rand`.
- (b) A seed outside 0 … 2³² − 1 is taken modulo 2³² for mt19937.
- (c) Each tool restarts from the seed. The environment page's global stream, which continues across a model's tools,
  is not kept.
- (d) RandomNumberGenerator's constructor takes `(seed=0, distribution="ACM599")`; the page documents none.
- (e) The full-text index is named `grapy_fts_<table>`. Several fields make one index, and a table takes one index. A
  stale index left by a deleted table is replaced.
- (f) CalculateDefaultClusterTolerance's answer is the Result's output, as a string. For a class in several topologies,
  the first by name answers.
- (g) A column declared `DATE` (date only) is not a date field to migrate.
- (h) Usage uses the tool reference's syntax notation, which the page does not show. A grapy tool lists the parameters
  its function names.
- (i) ListToolboxes lists grapy's thirteen tool modules with the tool reference's labels. ListTools lists each module's
  public functions whose names begin with a capital.
- (j) A bare tool name looks in imported toolboxes first. An alias that names a grapy module is an error. RemoveToolbox
  of an unknown toolbox is a ValueError.
- (k) GetParameterValue answers `""` for no default.
- (l) Command answers a failure as its error messages and does not raise. A quoted token is one parameter, and `#` is
  the default.
- (m) An imported tool's parameter message after validation fails the tool, as grapy's runner does.

## 6. Consequences

- **State.** AddFullTextIndex writes a virtual table, its shadow tables and three triggers into the GeoPackage.
  Everything else keeps only session state: imported toolboxes and their `arcpy` attributes.
- **Negative.**
  - DeleteField of a field with a full-text index fails in SQLite, because the triggers name it. No tool removes a
    full-text index.
  - grapy's tool listings are its functions, not arcpy's catalogue.
- **Ports created.** None. The build uses SQLite's FTS5 and NumPy, both already held.

## 7. Assumptions this decision rests on

- The SQLite CI's Python links has FTS5. If it does not, the tool refuses and its tests are skipped.
- ArcMap's pages still describe the two tools Pro's reference no longer has.

## 8. Dependencies

- [ADR-224](ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md) (§5.6 equivalents)
- [ADR-223](ADR-223-user-tools-run-sandboxed-on-grapy.md) (the runner, the fence and the network)
- [ADR-225](ADR-225-grapy-holds-topology-and-relationship-classes.md) (topology cluster tolerance)

Clean room (CLAUDE.md §5): the build uses the public pages in §4, SQLite's documentation, the GeoPackage standard and
the cited papers. No Esri implementation was read.

## 9. Revisit triggers

- Esri publishes the `.atbx` specification. ImportToolbox would then read `.atbx`.
- Esri publishes ACM599's or its seeding of mt19937.
- A Pro comparison answers differently on §5.5 (b) or (c).

## 10. Dissent

None recorded.

## 11. Conditions

1. **A seeded tool compared with ArcGIS Pro under `"<seed> MERSENNE_TWISTER"`** (§5.2): one CreateRandomPoints run in
   Pro 3.7 and in grapy, to record how far the streams differ. Needs a Pro session.
2. **The owner answers [Q-217](../open-questions.md).**
