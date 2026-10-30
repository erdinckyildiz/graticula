# ADR-235 — grapy holds attribute rules, contingent values and database sequences, runs Arcade, and refuses replicas

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-10-30, by owner decision on scope — grapy gets *attribute rules, contingent values and replicas*, and for attribute rules an Arcade interpreter written clean-room from Esri's public documentation, core first (*"Arcade'i yaz, çekirdeğiyle başla"*). The storage design and the gaps the pages leave open are INFERRED and listed for confirmation in §5.8 and [Q-203](../open-questions.md). |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

[ADR-224](ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md) refused the group-2 models one by one, each with its
reason. Three were still refused as *a data model this server does not hold yet*: attribute rules (fourteen
`arcpy.management` tools), contingent values and field groups (eight), and geodatabase replicas (fourteen), with
CreateDatabaseSequence and DeleteDatabaseSequence under enterprise administration and `arcpy.da`'s
ListContingentValues, ContingentValue, ContingentFieldValue, ListDatabaseSequences, DatabaseSequence, ListReplicas,
Replica and SyncReplica beside them. A toolbox that adds a rule to the class it builds, or reads a field group, stopped
at the first such call.

An attribute rule is an Arcade expression the geodatabase runs on an edit, so the model cannot be built without the
language. Arcade is documented in public — a language guide (structure, variables, operators, types, type casting,
loops, functions, returns, template literals) and a function reference of seventeen categories — and nowhere in that
documentation is there an implementation to read, which suits clean room (CLAUDE.md §5).

## 2. Alternatives considered

### Alternative A — an Arcade interpreter of our own, rules in grapy's tables, run by grapy's cursors (chosen)

A lexer, a recursive-descent parser and a tree-walking evaluator in `grapy/_arcade/`, with the function library built
category by category from the reference. Rules, field groups and contingent values are rows of grapy's tables in the
GeoPackage; the immediate ones run inside `arcpy.da`'s cursors. **For:** a rule written for ArcGIS runs unchanged
where the documentation defines it, and every function that is not built says so by name. **Against:** a second
implementation of a language whose own implementation we cannot compare against; every place the guide is silent is a
place we may differ (§3).

### Alternative B — translate Arcade to Python and `exec` it

**For:** fast, and Python's semantics for free. **Against:** Python's semantics are the problem — Arcade's casting
rules (`"5" + 1` is `"51"`, `10 >= "10"` is true, `10 == "10"` is false), case-insensitive names, implicit returns and
block-less scope all differ from Python's, and a user tool runs fenced (ADR-223): handing a rule's text to `exec`
widens the attack surface for nothing. Rejected.

### Alternative C — store rules, but never run them

**For:** ExportAttributeRules and ImportAttributeRules round-trip without a language. **Against:** a constraint that
does not constrain is silently wrong, which ADR-224 §5.4 forbids. Rejected.

### Alternative D — replicas between GeoPackages on grapy's branch versions

A check-out or one-way replica could be modelled as a copy whose changes ADR-227's history tables already record.
**For:** the owner asked for replicas. **Against:** it would not be what CreateReplica documents. The page requires the
parent's data to be *registered as traditional versioned* in an enterprise geodatabase, and a two-way or
child-to-parent replica's child to be an enterprise geodatabase; synchronisation runs through version states and
generations, and the messages it exchanges are Esri's replica XML, whose schema is not published. Building it on
branch versions would answer arcpy's names with a different behaviour and no way to exchange data with ArcGIS.
Not taken now; recorded as a question for the owner in [Q-203](../open-questions.md) (h).

## 3. Counterarguments to the preferred option

The guide does not give operator precedence, does not say what a function given null answers, does not say how a
FeatureSet's Stdev differs from an array's beyond one word (*sample* against *population*), and gives its date examples
in Pacific time. The tool pages name the validation error tables but not their fields, and do not say in which order
calculation and constraint rules run, whether a field group with no contingent values restricts, or what ID a new
contingent value takes. Each gap is filled by inference and listed in §5.8; any of them could differ from Pro, and a
rule that depends on one would answer differently. The function reference prints at least one wrong value (Sin(1) as
0.841741, a transposition of 0.841471); the tests follow arithmetic, and say so.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Identifiers are not case-sensitive; `var` declares; no block scope; an undeclared assignment is an error | Variables page, read 2026-10-30 | developers.arcgis.com/arcade/guide/variables |
| Operators, safe access `?.`, bitwise on 32-bit numbers; `==` does no coercion; relational operators cast text compared with a number | Operators page | …/guide/operators |
| The casting tables to Number, Text, Boolean and Date, and which operators cast | Type casting page | …/guide/type-casting |
| An expression answers its `return` or the value of the last statement executed | Return page | …/guide/return |
| `for…in` gives indexes and keys, `for…of` (1.29) elements and key-value pairs, `while` (1.19), `break`, `continue` | Loops page | …/guide/loops |
| Template literals, their placeholders and newlines | Template literals page | …/guide/template-literals |
| Dictionary keys are case-insensitive; arrays cannot have missing elements; geometries are immutable | Types page | …/guide/types |
| Every function's signature and the results its examples print | The seventeen category pages of the function reference | …/arcade/function-reference |
| The dictionary a rule may return: `result` (a value or `attributes` and `geometry`), `errorMessage`, `edit` (`className`, `adds`, `updates`, `deletes`), `calculationRequired`, `validationRequired` | Attribute rule dictionary keywords page | doc.esri.com …/attribute-rule-dictionary-keywords |
| AddAttributeRule's parameters and rules (unique 64-character names, batch and immediate, error number and message, severity 1–5, triggering fields) | Tool page | …/data-management/add-attribute-rule |
| AlterAttributeRule's RESET and what can be altered; Reorder's shifting; Disable's type check | Tool pages | …/alter-attribute-rule, reorder-attribute-rule, disable-attribute-rules |
| ExportAttributeRules' twenty CSV columns in order | Tool page | …/export-attribute-rules |
| Describe's `attributeRules` properties (`type` esriARTCalculation …, `evaluationOrder`, `subtypeCode` -1 for several) | Attribute rule properties page | …/arcpy/functions/attribute-rule-properties |
| EvaluateRules' parameters; the four validation error tables GDB_ValidationPoint/Line/Polygon/ObjectErrors | Tool page; analyzer error 00298 | …/evaluate-rules; …/00298-validation-error-table-must-be-referenced-by-a-layer |
| Field groups (RESTRICT by default, no system or subtype fields), contingent values (CODED_VALUE, RANGE 'min;max', ANY, NULL, RETIRE, one subtype per value), their CSV files and CV_TYPE codes 1–4, Import's UNION and REPLACE, Generate's DATA and SCHEMA | Tool pages and the contingent values help topic | …/create-field-group, alter-field-group, add-contingent-value, remove-contingent-value, export-contingent-values, import-contingent-values, generate-contingent-values, …/contingent-values |
| ListContingentValues' objects (`id`, `subtype`, `isRetired`, `values` of `name`, `type`, `code`, `range`) | Function page | …/arcpy/data-access/listcontingentvalues |
| Database sequences: start greater than 0, default 1, increment default 1, names unique; `startValue`, `incrementValue`, `currentValue`; NextSequenceValue errors on a missing sequence | Tool and function pages | …/create-database-sequence; …/listdatabasesequences; …/arcade/function-reference/enterprise_functions |
| CreateReplica needs traditional versioning in an enterprise parent; two-way and child-to-parent children are enterprise geodatabases | Tool page | …/data-management/create-replica |
| Esri's Hash is not Java's string hash | `Hash('text value')` is documented as 1649420691; Java's hashCode of the same text is 393123006, computed 2026-10-30 | refused in §5.3 |

No benchmark is at stake: a rule runs on the rows a user tool edits.

## 5. Decision

1. **Storage** (INFERRED, §5.8 a). Rules are rows of `grapy_attribute_rule`; field groups of `grapy_field_group`;
   contingent values of `grapy_contingent_value` (one JSON entry per field of the group); all registered in
   `gpkg_extensions` as `grapy_attribute_rules` and `grapy_contingent_values`. Delete removes a table's rules and
   contingent values with it, Rename carries them, and Describe answers `attributeRules` with the documented
   properties.
2. **The engine** (`grapy/_rules.py`). Every insert, update and delete made through `arcpy.da.InsertCursor` and
   `UpdateCursor` — and so every grapy tool that writes through them — runs the table's enabled immediate rules:
   calculation rules in evaluation order, each seeing what the ones before it set, then constraint rules, then the
   restrictive field groups' contingent values. `$feature`, `$originalFeature`, `$editcontext.editType` and
   `$datastore` are the attribute-rule profile's. A calculation rule sets its field, or applies the `result`
   dictionary's attributes and geometry, or applies its `edit` dictionary's adds, updates and deletes to another
   class — whose own rules run, eight levels deep at most. An `errorMessage`, a constraint answering false, or a
   combination no contingent value allows raises RuntimeError and the edit is not made. A NONEDITABLE field cannot be
   edited directly. EvaluateRules runs batch calculation rules and validation rules and writes failures to the four
   validation error tables, with the feature's geometry.
3. **Arcade** (`grapy/_arcade/`): the whole language of the guide — literals in every documented notation, `var`,
   case-insensitive names, function scope, operators with JavaScript's precedence (§5.8 b), `if`/`else`, three forms
   of `for`, `while`, `break`, `continue`, `function`, `return` and implicit return, arrays, dictionaries, features,
   geometries, template literals, safe access, and the casting tables — and a function library catalogued against
   all **214** documented names: **188 built, 26 refused**, each refused one raising an error that names it and why
   (`grapy._arcade.coverage()` counts them; `test_arcade.Coverage` checks every one).
   - **Built:** all of Math except Hash; all of Text, Logical, Array, Dictionary and Debugging, with Number's text
     patterns; all of Date, with DateOnly's and Time's formats; Feature (Feature, Schema, Domain, DomainName,
     DomainCode, Subtypes, SubtypeCode, SubtypeName, FeatureInFilter, HasKey, HasValue, DefaultValue, Expects,
     GdbVersion — the edit's version on a versioned class (ADR-227) — and TimeReceived); FeatureSet on the GeoPackage
     (FeatureSetByName, FeatureSet from REST JSON, Filter with `@variables`, OrderBy, Top, First, Distinct, GroupBy,
     Count, Sum, Min, Max, Mean, Average, Stdev, Variance, FilterBySubtypeCode, the spatial filters, Area, Length,
     AreaGeodetic, LengthGeodetic, Length3D, GetUser, and through grapy's models FeatureSetByRelationshipClass
     (ADR-225), FeatureSetByAssociation (ADR-231) and Attachments (ADR-228)); Geometry with shapely and pyproj
     (constructors with z and m, properties, the measures, Distance, DistanceGeodetic, Buffer, BufferGeodetic, Centroid,
     Clip, ConvexHull, Cut, Densify, DensifyGeodetic, Generalize, Offset, Intersection, Union, Difference,
     SymmetricDifference, the relations, Relate, IsSimple, IsSelfIntersecting, Simplify, MultiPartToSinglePart, Rotate,
     Angle, Bearing, ConvertDirection, DistanceToCoordinate, MeasureToCoordinate, PointToCoordinate, NearestCoordinate,
     NearestVertex, RingIsClockwise, SetGeometry, Extent); NextSequenceValue.
   - **Built as equivalents**, each saying its method in its docstring: BufferGeodetic (a buffer in an azimuthal
     equidistant projection centred on the geometry, where distance from the centre is geodesic — Snyder 1987, §25)
     and Generalize (Douglas–Peucker 1973, the method ArcGIS's Generalize tool names). DensifyGeodetic places vertices
     on the geodesic with pyproj's Geod (Karney 2013), as the page describes.
   - **Refused by value**, each with its reason: the curve types other than geodesic (pyproj measures geodesics);
     Offset's `square` join (shapely joins round, bevel or mitre); curves in a geometry definition (grapy's geometries
     are linear).
   - **Refused, each with its reason:** TranslateText, FeatureSetByPortalItem, Portal, KnowledgeGraphByPortalItem and
     QueryGraph (a fenced tool has no network or portal; knowledge graphs are not held); FeatureSetById,
     FeatureSetByRelationshipName, GetFeatureSet and GetFeatureSetInfo (map- and service-profile answers a GeoPackage
     does not have); Hash (its algorithm is undocumented — §4); the sixteen Track functions (GeoAnalytics track
     tools only).
4. **Attribute rule tools built:** AddAttributeRule, AlterAttributeRule, DeleteAttributeRule, DisableAttributeRules,
   EnableAttributeRules, ReorderAttributeRule, ExportAttributeRules, ImportAttributeRules and EvaluateRules.
   **Built as equivalents** (`grapy/_rulegen.py`, ADR-224 §5.6): CalculateGeometryFieldsAttributeRule,
   GenerateIDAttributeRule, GenerateSpatialJoinAttributeRule, GenerateSymbolRotationAttributeRule and
   GenerateUpdateRelatedAttributeRule. Each writes a template rule whose Arcade script Esri's tool writes and does not
   publish, so grapy writes a script of its own, from the functions of §5.3, that does what the page says the rule
   does, in ExportAttributeRules' columns for ImportAttributeRules. Refused by parameter, each with its reason: the z
   and m of centroids, inside points, extents and line ends, POINT_COORD_NOTATION and `coordinate_format` (the page's
   format descriptions do not match its keywords); GenerateID's CODED_VALUES, *Sequence by lookup*, an intersect class
   and an id table's IntersectClass (the page does not say what sequence, prefix or ID each gets); field mappings as
   a string (their string form is not documented).
5. **Database sequences** are rows of `grapy_sequence` (`grapy_sequences` extension): CreateDatabaseSequence,
   DeleteDatabaseSequence, `da.ListDatabaseSequences`, `da.DatabaseSequence`, and Arcade's NextSequenceValue.
6. **Replicas are refused**, each name with its reason: CreateReplica (traditional versioning in an enterprise parent,
   enterprise children for two-way replicas, synchronisation through version states — §2 D), CreateReplicaFromServer
   (a service a fenced tool cannot reach), SynchronizeChanges, UnregisterReplica, CompareReplicaSchema,
   ExportReplicaSchema and ImportReplicaSchema (they work on a replica grapy cannot make), ExportAcknowledgementMessage,
   ExportDataChangeMessage, ImportMessage and ReExportUnacknowledgedMessages (also Esri's unpublished replica XML),
   EnableReplicaTracking and DisableReplicaTracking (a feature service's offline replicas of enterprise data), and
   `da.ListReplicas`, `da.Replica`, `da.SyncReplica`. ExtractDataFromGeodatabase is refused: it extracts into a
   geodatabase, a mobile geodatabase or Esri's XML workspace document, none of which grapy writes, and carries the
   expanded classes (topologies, networks, relationship classes) by rules the page leaves to
   `expand_feature_classes_and_tables` without defining them; a GeoPackage copy of a class filtered by extent or
   geometry is `arcpy.conversion.ExportFeatures` or `ExportTable`.
7. **Contingent values built:** CreateFieldGroup, AlterFieldGroup, DeleteFieldGroup, AddContingentValue,
   RemoveContingentValue, ExportContingentValues, ImportContingentValues, GenerateContingentValues,
   `da.ListContingentValues`, `da.ContingentValue` and `da.ContingentFieldValue`. A restrictive group is enforced on
   every edit through grapy's cursors — grapy is the application the help topic says enforces them. (No tool named
   AlterContingentValue or AddFieldConflictFilter belongs here: the first does not exist in arcpy 3.7's surface, and
   the second is ADR-227's.)
8. ~~**INFERRED, for confirmation**~~ **Confirmed 2026-10-31 by owner decision in Q-203** (*"Hepsini onayla"*) ([Q-203](../open-questions.md)):
   - (a) the five tables above and their columns, since no GeoPackage or GDAL convention holds rules, field groups,
     contingent values or sequences;
   - (b) operator precedence and associativity are JavaScript's; a half rounds toward +∞ (Round(2.5) = 3,
     Round(-2.5) = -2); a pattern without grouping keeps as many integer digits as it has placeholders, as
     `Text(123, '00')` = `'23'` shows;
   - (c) a rule runs in UTC: Now, Today and a Date without a zone are UTC, a GeoPackage's dates read as UTC, ToLocal
     answers UTC; named IANA zones need the system's time zone database, offsets always work;
   - (d) a geometry function given null answers null (a relation false); planar measures with units convert from the
     reference's linear unit and are refused on a geographic reference; an empty overlay answers null; a buffer is a
     polygon of 64 sides;
   - (e) a FeatureSet's Stdev and Variance are sample statistics; `@name` in a Filter becomes an SQL literal; a mixed
     array without a comparator sorts by text; Concatenate whose first argument is not an array joins all its
     arguments; UrlEncode writes a dictionary's keys in GetKeys' order; DomainName of a code no domain holds answers
     the code; GetEnvironment answers engine "grapy" and claims no Arcade version;
   - (f) calculation rules run before constraint rules; a feature's every field is present (null when the edit does
     not name it, the object id null on insert); `calculationRequired` and `validationRequired` are accepted and
     ignored, since grapy keeps no validation status and EvaluateRules evaluates every feature; an `edit` runs whether
     or not the rule is excluded from client evaluation; a sequence value drawn by a refused edit is not given back;
   - (g) the validation error tables' fields (RULENAME, RULEID, RULETYPE, ORIGINOBJECTCLASSNAME, ORIGINOBJECTID,
     ORIGINGLOBALID, ERRORNUMBER, ERRORMESSAGE, SEVERITY, ERRORSTATUS, ERRORPHASE, DESCRIPTION, CREATIONDATE), a point
     error stored as a multipoint, evaluating again replacing a rule's earlier errors; ExportAttributeRules' TYPE as
     the tool's keyword, SUBTYPE as names, TRIGGERINGFIELDS as a JSON list, CATEGORY and CHECKPARAMETERS empty; an
     immediate rule needs a triggering event; a validation rule is batch whatever `batch` says; an existing name on
     import is an error;
   - (h) a restrictive group with no contingent values for a row's subtype, and a row whose group fields are all
     null, are not restricted; a field the value table leaves out is ANY; a CODED_VALUE needs the field's coded
     domain; a new contingent value's id is one more than the highest held; RANGE is written 'min;max'; in Generate's
     DATA mode a field without a domain is ANY, or NULL where null; and ~~whether the owner wants Alternative D's
     replicas on branch versions despite §2~~ — **decided by the owner 2026-10-30: replicas stay refused (*"Reddedilmiş
     kalsın"*)**: grapy has none of what CreateReplica's page requires, and a tool under its name that behaved otherwise
     would be worse than a plain refusal;
   - (i) a FeatureSet that GroupBy, Distinct or FeatureSet makes is a temporary table of the connection, its object id
     ROW__ID unless the definition names one; FeatureSetByAssociation's className omits the feature dataset; an
     attachment is a Dictionary, and `metadata` adds exifInfo and keywords as UpgradeAttachments keeps them;
     GdbVersion answers DEFAULT outside a named version;
   - (j) ConvertDirection reads text input by its numbers and letters and writes text without a format as the number
     (DMS as `d° m' s"`, a quadrant's parts joined by spaces); Number's single pattern also reads a minus sign;
     YY is 2000–2068 for 00–68; a Time format reads M and MM as minutes, as the page's own example does; a DateOnly
     is its midnight, UTC, in milliseconds;
   - (k) Cut puts a piece to the side of the cutter its interior point falls on and does not separate *undefined*
     cuts; NearestCoordinate finds a point inside a polygon on it, and a point or multipoint input answers `left` off
     it; Offset's bevelRatio is 10 and its round joins sixteen segments a quarter unless flattenError says otherwise;
     the distance of a geodetic function without a unit is in metres on a geographic reference;
   - (l) the generators write `<out_folder>/<table>/<table>.csv` (then `_1`, `_2` …); their rules' names, their
     insert-and-update triggers (insert only for an ID), an ID kept when a feature arrives with one, the rotation of
     the segment from its start vertex, and CONCATENATE onto an empty target being the value alone.

## 6. Consequences

- **State.** The GeoPackage the tool works on holds up to five grapy tables and, after EvaluateRules, up to four
  validation error tables. Nothing goes in the catalogue or on the server. A hosted layer imported from a tool's
  output carries neither rules nor contingent values: ADR-223 §5.5 copies features, not models.
- Against `grapy-surface` when this ADR merged, `arcpy.management` moves from 192 to 216 built (255 to 231 refused;
  equivalents 11 to 16) and `arcpy.da` from 19 to 24 (10 to 5); all modules from 1,116 to 1,145 of 2,174.
- **Negative.** Every edit through a cursor on a table with rules runs Arcade, row by row; a bulk load into such a
  table is slower, as it is in Pro, where the page advises disabling rules first. A GeoPackage reader that is not
  grapy runs none of the rules.
- **Ports created.** None. The interpreter is Tier 1 (our own); shapely and pyproj stay behind grapy's geometry as
  before.

## 7. Assumptions this decision rests on

- The public guide and function reference define Arcade closely enough that a rule written for ArcGIS answers the same
  in grapy wherever §5.8 does not apply. Where it is silent, §5.8 says so and the answer is not presented as Pro's.

## 8. Dependencies

[ADR-223](ADR-223-user-tools-run-sandboxed-on-grapy.md), [ADR-224](ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md),
[ADR-227](ADR-227-grapy-holds-versions-and-archives.md) (why replicas are refused),
[ADR-228](ADR-228-grapy-holds-subtypes-and-attachments.md) (subtypes and domains, on which rules and contingent values
are keyed). Clean room (CLAUDE.md §5): built from the public pages listed in §4; Esri's Arcade implementation was not
read, and no Esri script was reproduced.

## 9. Revisit triggers

- A toolbox needs a refused parameter value of §5.3 or §5.4, or Pro's own generated script.
- ~~The owner answers Q-203 (h) in favour of replicas on branch versions.~~ Answered against, 2026-10-30.
- Esri publishes the replica message schema or the validation error tables' fields.

## 10. Dissent

None recorded.

## 11. Conditions

1. **The Arcade core** — the guide's language and the built functions of §5.3 — is tested against the documentation's
   own worked examples. **DISCHARGED 2026-10-30**: `src/grapy/tests/test_arcade.py` checks the examples the guide's
   pages and the Math, Text, Logical, Array, Dictionary and Date pages print (with the page's Pacific-time and Sin(1)
   exceptions named), the casting tables, and geometry on hand-worked shapes.
2. **Every documented Arcade function is built or refused with its reason**, and one refused names itself.
   **DISCHARGED 2026-10-30**: `test_arcade.Coverage` finds none of the 214 without a status and calls each of the 26
   refused, none of whose reasons is a schedule.
3. **Immediate rules run on grapy's edits; batch rules run in EvaluateRules.** **DISCHARGED 2026-10-30**:
   `src/grapy/tests/test_attribute_rules.py` works out by hand each rule's effect on inserts, updates and deletes —
   order, triggering fields, subtypes, NONEDITABLE, result dictionaries, edits to another class and their own rules,
   constraints, `$datastore` reads, sequences, validation error tables and extents.
4. **Contingent values are enforced and round-trip through their files.** **DISCHARGED 2026-10-30**: the
   `ContingentValues` tests cover restriction, retirement, subtypes, Alter and Delete, Export and Import against the
   documented columns, and Generate's DATA and SCHEMA modes against hand-counted combinations.
5. **The functions of §5.3 and the template-rule generators of §5.4 are built**, or refused with a reason that is
   not *not yet*. **DISCHARGED 2026-10-30**: `src/grapy/tests/test_arcade_more.py` checks ConvertDirection on the
   page's ten examples, Number's seven pattern examples, DateOnly's and Time's format examples, the coordinate,
   nearest, Cut, Generalize, Offset, Length3D and geodetic functions on hand-worked shapes, GroupBy, Distinct and
   FeatureSet from JSON, FeatureSetByRelationshipClass, FeatureSetByAssociation and Attachments through grapy's
   models, and each generator's rule imported and run on hand-worked edits.
6. **The owner confirms §5.8's inferences**, or names the ones to change, before data made with them is relied on.
