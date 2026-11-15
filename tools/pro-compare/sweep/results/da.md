# arcpy.da against ArcGIS Pro 3.7

Measured 2026-11-08, case module `cases/da.py`, 25 names. Describe and the "*" field list are compared on a GeoPackage
on both sides; the rest on the shared fixtures or a workspace each case builds with `ap`.

First run: MATCH 14, DIFF 6, BOTH-ERROR 3, PRO-ERROR 2 (the BOTH-ERRORs were the case's AddContingentValue syntax:
a RANGE value is "min;max").
Now: MATCH 22, DIFF 1, PRO-ERROR 2.
Run again 2026-11-12 after the column names (ADR-289 §5.1): MATCH 23, PRO-ERROR 2.

| Name | First run | Now | Action | Note |
|---|---|---|---|---|
| ContingentFieldValue | BOTH-ERROR | MATCH | case | |
| ContingentValue | BOTH-ERROR | MATCH | fixed | subtype is 0 for a table without subtypes (was None) |
| CreateTable | MATCH | MATCH | — | |
| DatabaseSequence | DIFF | MATCH | case | the case compared the Python class name ("Database Sequence" in Pro); dropped |
| Describe | DIFF | MATCH | fixed | hasSpatialIndex, extension, canVersion, isVersioned, aliasName ("main.<table>"), areaFieldName and lengthFieldName ("" in a GeoPackage), shared with arcpy.Describe |
| Domain | DIFF | MATCH | case | class name dropped, as DatabaseSequence |
| Editor | MATCH | MATCH | — | |
| ExtendTable | MATCH | MATCH | — | |
| FeatureClassToNumPyArray | MATCH | MATCH | — | |
| InsertCursor | MATCH | MATCH | — | |
| ListContingentValues | BOTH-ERROR | MATCH | fixed | as ContingentValue |
| ListDatabaseSequences | MATCH | MATCH | — | |
| ListDomains | DIFF | MATCH | fixed | a range domain's codedValues is None (was {}) |
| ListFieldConflictFilters | MATCH | MATCH | — | both [] on a class that is not versioned |
| ListSubtypes | MATCH | MATCH | — | |
| ListVersions | PRO-ERROR | PRO-ERROR | not comparable | Pro refuses a file geodatabase (TypeError naming it); versions need an enterprise geodatabase. grapy answers its own branch versions (ADR-227) |
| NumPyArrayToFeatureClass | MATCH | MATCH | — | |
| NumPyArrayToTable | MATCH | MATCH | — | |
| SearchCursor | DIFF | MATCH | fixed | spatial_reference was applied to SHAPE@ only — SHAPE@XY and the other tokens stayed in the data's reference; SHAPE@WKT now in Pro's form (MULTIPOLYGON); "*" names the table's own fields in its order and reads the shape as SHAPE@XY (documented); the names are OBJECTID and Shape, as Pro writes them (ADR-289 §5.1) |
| SearchRelatedRecords | MATCH | MATCH | — | |
| TableToArrowTable | MATCH | MATCH | — | |
| TableToNumPyArray | MATCH | MATCH | — | |
| UpdateCursor | MATCH | MATCH | — | |
| Version | PRO-ERROR | PRO-ERROR | not comparable | as ListVersions |
| Walk | DIFF | MATCH | fixed | a feature dataset is walked as a workspace inside its GeoPackage (dirnames, then its own classes), not as "<dataset>/<class>" file names; the case strips the workspace's extension (.gdb written as .gpkg) |

## Open differences worth a debt row

1. ~~GeoPackage column names fid/geom against Pro's OBJECTID/Shape~~ — built 2026-11-12 (ADR-289 §5.1).
2. UpdateCursor with "*": left reading the shape as a geometry (SHAPE@); whether Pro's UpdateCursor also reads it as
   SHAPE@XY was not measured.
3. Versions and replicas need an enterprise geodatabase in Pro, so grapy's ADR-227 versions have no Pro comparison here.
