# ADR-228 — grapy holds subtypes, field defaults, global IDs and attachments in the GeoPackage

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-10-30, by owner decision on scope — grapy gets *subtypes and attachments*, as their own ADR and batch, after the three group-2 models of [ADR-224](ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md) §5.7 ([ADR-225](ADR-225-grapy-holds-topology-and-relationship-classes.md), [ADR-226](ADR-226-grapy-holds-network-datasets.md), [ADR-227](ADR-227-grapy-holds-versions-and-archives.md)). The storage design below is INFERRED and listed for confirmation in §5.6. |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

`arcpy.management` refused seventeen tools because a GeoPackage has no geodatabase subtypes and no attachments:
AddSubtype, AlterSubtype, RemoveSubtype, SetDefaultSubtype, SetSubtypeField, AssignDefaultToField, EnableAttachments,
DisableAttachments, AddAttachments, RemoveAttachments, GenerateAttachmentMatchTable, ExportAttachments,
UpgradeAttachments, DowngradeAttachments, GeoTaggedPhotosToPoints, MatchPhotosToRowsByTime and AddGlobalIDs.
`arcpy.da.ListSubtypes` was refused, and so was the `subtype_code` parameter of AssignDomainToField,
RemoveDomainFromField, AddRuleToRelationshipClass and RemoveRuleFromRelationshipClass. A toolbox written against
arcpy that sets a default by subtype, or attaches a photo to a feature, stopped at the first such call.

A subtype is an integer field, a list of codes with names, a default code, and per-subtype default values and domains.
An attachment is a file kept in a table beside its feature, related to it through a composite relationship class keyed
on the feature's global ID. All of these are described on Esri's public tool pages, and none needs anything a
GeoPackage cannot hold.

## 2. Alternatives considered

### Alternative A — grapy's own tables for subtypes and defaults; ArcGIS's names for attachments (chosen)

Subtypes, defaults and per-subtype domains are rows of four `grapy_*` tables registered in `gpkg_extensions`. Global
IDs are a `GlobalID` text column, named in `grapy_global_id`. Attachments are a table `<class>__ATTACH` with the fields
ArcGIS's attachment tables carry, related through a relationship class `<class>__ATTACHREL` that ADR-225's relationship
module already holds. **For:** the file stays a valid GeoPackage. The attachment table and its fields have the names
ArcGIS uses, and GDAL's OpenFileGDB driver reads those names, so a file another tool produced from a geodatabase looks
the same. **Against:** only grapy understands subtypes and defaults; another reader sees an integer field.

### Alternative B — the column's own SQL DEFAULT for field defaults

**For:** every SQLite reader would honour it. **Against:** SQLite cannot alter a column's default after the table is
made, so AssignDefaultToField would have to rebuild the table, and a default *per subtype* has no SQL form at all.
Rejected.

### Alternative C — attachments only in the OGC Related Tables Extension's own shape

OGC 18-000 defines a *media* table (`id`, `data`, `content_type`) and a mapping table. **For:** it is the published
GeoPackage way to attach files. **Against:** it has no attachment name or size, and its column names are not ArcGIS's,
so a toolbox reading `ATT_NAME` or `DATA_SIZE` through a cursor would fail. **Taken in part:** the attachment table is
also registered as a `media` relation in `gpkgext_relations`, with a `<class>__ATTACHREL_rte` mapping. `DATA` and
`CONTENT_TYPE` match the extension's `data` and `content_type`, because SQLite column names ignore case. The primary
column is `ATTACHMENTID`, which `related_primary_column` names, and not `id`.

### Alternative D — keep REL_OBJECTID keys instead of global IDs

Older geodatabases related attachments by object ID. **For:** no new column on the class. **Against:** the owner's
brief names `REL_GLOBALID`, and arcpy's cursors read `GLOBALID@`. A class without global IDs could not answer that
token. Rejected. The cost is that AddGlobalIDs, refused until now as an *editing behaviour*, is built here too.

## 3. Counterarguments to the preferred option

The subtype model's documented behaviour has gaps. The pages do not say what RemoveSubtype does to the default code,
whether a subtype added later inherits a field's default, which subtype a new row gets before SetDefaultSubtype runs,
or which properties ListSubtypes answers beyond its sample. Each gap is filled by inference and listed in §5.6, and any
one could differ from Pro. ExportAttachments also leaves its name separators and conflict suffix open, and the Exif
tools leave open their invalid-photo tables and which photo timestamp they read. A toolbox that depends on one of
those details would see a difference.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Subtype tool parameters, the duplicate-code error, the integer subtype field, and that SetSubtypeField removes existing subtypes | Tool pages read 2026-10-30 | doc.esri.com …/data-management/add-subtype, remove-subtype, alter-subtype, set-subtype-field, set-default-subtype, assign-default-to-field |
| ListSubtypes answers `{code: properties}`, with `FieldValues` mapping each field to `(default, Domain)`; a table without subtypes answers code 0, `Default` True and `SubtypeField` empty | ListSubtypes page and its sample | doc.esri.com …/arcpy/data-access/listsubtypes |
| `SUBTYPE@` (subtype code) and `GLOBALID@` (read-only GUID) are cursor tokens | SearchCursor page | doc.esri.com …/arcpy/data-access/searchcursor-class |
| DisableAttachments deletes every attachment; enabling again brings none back | DisableAttachments page | …/data-management/disable-attachments |
| The attachment table is `<InputDataset>__ATTACH` | RemoveAttachments usage notes | …/data-management/remove-attachments |
| The match table has `MATCHID` (the input's object ID) and `FILENAME`; matching ignores the file extension; EXACT, PREFIX, SUFFIX and ANY | GenerateAttachmentMatchTable page | …/data-management/generate-attachment-match-table |
| ExportAttachments' name formats, subdirectories, character replacement and `None` for nulls | ExportAttachments page | …/data-management/export-attachments |
| UpgradeAttachments adds `KEYWORDS` and `EXIFINFO`, plus `ATT_TITLE`, `ATT_ALT_TEXT`, `ATT_CAPTION` and `ATT_CREDIT` with `EXIF_AND_ALT` | UpgradeAttachments page | …/data-management/upgrade-attachments |
| Photo points: fields, WGS 1984, 0,0 has no point, `.jpg` `.tif` `.heic` | GeoTaggedPhotosToPoints page | …/data-management/geotagged-photos-to-points |
| Matching photos by time: closest row, tolerance (0 = any), clock offset, output fields | MatchPhotosToRowsByTime page | …/data-management/match-photos-to-rows-by-time |
| Exif tags DateTimeOriginal (0x9003), the GPS IFD (0x8825) and its latitude, longitude, altitude and direction tags | CIPA DC-008 (Exif), the public standard | `grapy/_exif.py` |
| A media relation and its table's `data` and `content_type` columns | OGC 18-000, GeoPackage Related Tables Extension | `grapy/_attachments.py` |
| MigrateRelationshipClass is undocumented here | Its page answered 404 on 2026-10-30 | refused with that reason |

No benchmark is at stake: a user tool works on the data it was given.

## 5. Decision

1. **Subtypes and field defaults** are rows of `grapy_subtype_field` (table, subtype field, default code),
   `grapy_subtype` (table, code, name), `grapy_field_default` (table, field, subtype code or null, value) and
   `grapy_subtype_domain` (table, code, field, domain). All four are registered as the `grapy_subtypes` extension. A
   field's own domain stays in the schema extension's `gpkg_data_columns`, where AssignDomainToField has always put it.
2. **Global IDs** are a text column `GlobalID`, named in `grapy_global_id`. Each row holds a version-4 UUID written
   `{XXXXXXXX-…}` in upper case. AddGlobalIDs adds the column, and EnableAttachments adds it when the class has none.
3. **Attachments** are the `<class>__ATTACH` table (`ATTACHMENTID`, `REL_GLOBALID`, `CONTENT_TYPE`, `ATT_NAME`,
   `DATA_SIZE`, `DATA`) and the COMPOSITE one-to-many relationship class `<class>__ATTACHREL` in
   `grapy_relationship_class` (ADR-225). Deleting a row therefore deletes its attachments through every grapy delete
   path. The class is also written to the Related Tables Extension as a `media` relation. Delete removes a class's
   attachments, global IDs and subtypes with it, and Rename carries them under the new name.
4. **Insert behaviour.** For each field a new row does not name, `da.InsertCursor` gives it the default subtype code,
   then its subtype's default or else the field's own, and then a new GlobalID. Every grapy tool that writes through
   the cursor does the same. A field that is named keeps its value, `None` included.
5. **The photo tools** read Exif with the standard library, from the public standard. HEIF (`.heic`) photos are not
   read: each one is listed as invalid, with a warning.
6. **INFERRED, for the owner's confirmation:**
   - (a) the four `grapy_subtype*`/`grapy_field_default` tables and their columns, and `grapy_global_id`. No GeoPackage
     or GDAL convention stores subtypes or defaults;
   - (b) a subtype that sets no default or domain for a field answers the field's own (read-time fallback);
   - (c) the default subtype code is 0 until SetDefaultSubtype sets another. SetDefaultSubtype requires an existing
     code. RemoveSubtype of the default leaves the default code as it was;
   - (d) clearing the subtype field (`CLEAR_SUBTYPE_FIELD`) keeps the field and its values and removes the designation
     and the subtypes;
   - (e) ListSubtypes also answers `Code`, and `FieldValues` covers every field except the object ID and the shape;
     Describe answers `subtypeFieldName` `""` and `defaultSubtypeCode` -1 without subtypes;
   - (f) the attachment table and relationship class names, `ATTACHMENTID` as the media table's primary column, and
     `application/octet-stream` for a file type Python's `mimetypes` does not know;
   - (g) EnableAttachments adds global IDs when the class has none;
   - (h) the subtype field, and the GlobalID field of a class with attachments, cannot be deleted;
   - (i) AddAttachments skips a missing file with a warning, and reads a relative path from the working folder, or
     from the current folder when no working folder is given;
   - (j) GenerateAttachmentMatchTable reads only the folder's own files, counts every file, and ignores case;
   - (k) ExportAttachments joins values to names with an underscore, gives a taken name `_1`, `_2` and so on, and
     also replaces quotes and colons;
   - (l) the photo tools' invalid and unmatched tables hold `Path` and `Name` (`Photo_Path` and `Photo_Name`).
     `Match_Diff` is in absolute seconds. The photo's time is Exif DateTimeOriginal. With no tolerance, any distance
     matches;
   - (m) a relationship rule may name its subtype by code or by name.
7. **Still refused, each with its reason:** MigrateRelationshipClass, because its page answered 404, so its fields and
   key handling are unknown; and AddRuleToTopology on one subtype, because grapy's ValidateTopology evaluates a rule
   on every feature of a class.

## 6. Consequences

- **State.** The GeoPackage the tool works on holds five grapy tables, the `GlobalID` column of a class with global
  IDs, and `<class>__ATTACH` with its Related Tables mapping. Nothing goes in the catalogue or on the server. A hosted
  layer imported from a tool's output does not carry the subtypes or the attachments: ADR-223 §5.5 copies features,
  not models.
- Seventeen `management` names and one `da` name are built. Four tools gain their `subtype` parameters. The counter
  moves from 149 to 166 built in `management` and from 18 to 19 in `da`.
- **Negative.** A GeoPackage reader that is not grapy sees an integer field where ArcGIS sees subtypes, and sees no
  defaults. Copy of a single table (not a whole file) does not carry subtypes or attachments. Neither did it carry
  relationship classes before this ADR.
- **Ports created.** None: the standard library (`sqlite3`, `uuid`, `mimetypes`, `struct`) is the only dependency.

## 7. Assumptions this decision rests on

- The public tool pages describe the subtype and attachment behaviour that toolboxes rely on. Where a page is silent,
  the gap is listed in §5.6 and is not presented as Pro's behaviour.

## 8. Dependencies

[ADR-223](ADR-223-user-tools-run-sandboxed-on-grapy.md), [ADR-224](ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md),
[ADR-225](ADR-225-grapy-holds-topology-and-relationship-classes.md) (the attachment relationship class is one of its
classes), the GeoPackage standard (OGC 12-128r18), its Related Tables Extension (OGC 18-000), and the Exif standard
(CIPA DC-008). Clean room (CLAUDE.md §5): built from the public pages; Esri's implementation was not read.

## 9. Revisit triggers

- A toolbox needs subtypes or attachments to survive publishing as a hosted layer. Feature services carry
  attachments, so the server would need the model too, and that is a different decision.
- A page this ADR found silent or missing (MigrateRelationshipClass) becomes readable.

## 10. Dissent

None recorded.

## 11. Conditions

1. **AddSubtype, AlterSubtype, RemoveSubtype, SetDefaultSubtype, SetSubtypeField, AssignDefaultToField and
   da.ListSubtypes** are built, together with the `subtype_code` of AssignDomainToField, RemoveDomainFromField and
   the relationship-rule tools, and default values apply on insert. **DISCHARGED 2026-10-30**: the `Subtypes` tests in
   `src/grapy/tests/test_subtypes_attachments.py` cover each documented error. A hand-worked table of four inserts
   checks that a row with no subtype gets the default subtype, a subtype's default beats the field's own, a named
   `None` stays `None`, and a cleared default falls back.
2. **AddGlobalIDs, EnableAttachments, DisableAttachments, AddAttachments, RemoveAttachments,
   GenerateAttachmentMatchTable, ExportAttachments, UpgradeAttachments and DowngradeAttachments** are built, with
   composite deletes and the Related Tables media relation. **DISCHARGED 2026-10-30**: the `GlobalIDs` and
   `Attachments` tests check each file's bytes, names and MIME types, the mapping pairs, cascade on delete, Disable
   leaving nothing to re-enable, Rename and Delete, each match pattern, and each export name format against
   hand-worked file listings.
3. **GeoTaggedPhotosToPoints and MatchPhotosToRowsByTime** are built on an Exif reader tested against hand-built
   files. **DISCHARGED 2026-10-30**: the `Photos` tests write JPEGs from Exif parts (DMS rationals, references,
   altitude below sea level). They check the decimal degrees, 0,0 and missing positions, HEIF refusal, ONLY_GEOTAGGED,
   attachments, and a clock offset and tolerance worked by hand.
4. **Every remaining subtype and attachment name** is built or refused with its reason. **DISCHARGED 2026-10-30**:
   `tools/grapy-refusals.json` shows none of them unexamined. MigrateRelationshipClass is refused because its page
   answered 404, and a topology rule on one subtype is refused by parameter.
5. **The owner confirms §5.6's inferences**, or names the ones to change, before data made with them is relied on.
6. **A topology rule on one subtype** is evaluated by ValidateTopology, so AddRuleToTopology takes its `subtype` and
   `subtype2` parameters.
