"""arcpy.topographic's schema and data-movement tools (ADR-251).

- **Workbooks** are Office Open XML spreadsheets (ECMA-376, Part 1, SpreadsheetML), read and written here with the
  standard library's ``zipfile`` and ``xml``: one worksheet per sheet, the first row the column names. Cells are written
  as inline strings, numbers and Booleans. A legacy ``.xls`` (BIFF8) workbook is refused by name.
- **The cross-reference database** is a GeoPackage holding the six tables the *Cross-reference geodatabase* help page
  documents — DatasetMapping, FieldMapping, ValueMapping, AnnoSymbolIds, SDEOwner and Version — as attributes tables
  under those names, each ``Id`` an integer column beside the object ID.
- **A topology definition file** is grapy's own XML (ADR-251 §5.8 (h)): ``<GrapyTopology name clusterTolerance>`` holding
  a ``<FeatureClass name xyRank zRank/>`` for each class and a ``<Rule type origin originSubtype destination
  destinationSubtype/>`` for each rule, subtypes by name. Esri's own file layout is not published.
"""
from __future__ import annotations

import os
import re
import zipfile
from xml.etree import ElementTree
from xml.sax.saxutils import escape

from . import _gpkg
from . import _topo_prod as tp
from ._result import Result
from ._runtime import AddMessage, AddWarning, ExecuteError, NotBuiltError

_MAIN = "http://schemas.openxmlformats.org/spreadsheetml/2006/main"
_REL = "http://schemas.openxmlformats.org/officeDocument/2006/relationships"
_PACKAGE_REL = "http://schemas.openxmlformats.org/package/2006/relationships"


# ---- xlsx ------------------------------------------------------------------------------------------------------------

def _column_letters(index):
    letters = ""
    index += 1
    while index:
        index, rest = divmod(index - 1, 26)
        letters = chr(65 + rest) + letters
    return letters


def _cell(reference, value):
    if value is None or value == "":
        return ""
    if isinstance(value, bool):
        return f'<c r="{reference}" t="b"><v>{1 if value else 0}</v></c>'
    if isinstance(value, (int, float)):
        return f'<c r="{reference}"><v>{value!r}</v></c>'
    text = escape(str(value))
    return f'<c r="{reference}" t="inlineStr"><is><t xml:space="preserve">{text}</t></is></c>'


def write_xlsx(path, sheets):
    """A workbook of ``sheets``: [(name, header, rows)]."""
    if str(path).lower().endswith(".xls"):
        raise NotBuiltError("arcpy.topographic: an .xls workbook (BIFF8) is not written by grapy; write .xlsx, the "
                            "Office Open XML workbook (ECMA-376).")
    os.makedirs(os.path.dirname(os.path.abspath(path)) or ".", exist_ok=True)
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
        overrides = "".join(f'<Override PartName="/xl/worksheets/sheet{i}.xml" ContentType="application/'
                            'vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>'
                            for i in range(1, len(sheets) + 1))
        z.writestr("[Content_Types].xml",
                   '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
                   '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
                   '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>'
                   '<Default Extension="xml" ContentType="application/xml"/>'
                   '<Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.'
                   'spreadsheetml.sheet.main+xml"/>' + overrides + '</Types>')
        z.writestr("_rels/.rels",
                   f'<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="{_PACKAGE_REL}">'
                   '<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/'
                   'officeDocument" Target="xl/workbook.xml"/></Relationships>')
        listed = "".join(f'<sheet name="{escape(name)}" sheetId="{i}" r:id="rId{i}"/>'
                         for i, (name, _, _) in enumerate(sheets, start=1))
        z.writestr("xl/workbook.xml",
                   f'<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="{_MAIN}" '
                   f'xmlns:r="{_REL}"><sheets>{listed}</sheets></workbook>')
        relations = "".join(f'<Relationship Id="rId{i}" Type="http://schemas.openxmlformats.org/officeDocument/2006/'
                            f'relationships/worksheet" Target="worksheets/sheet{i}.xml"/>'
                            for i in range(1, len(sheets) + 1))
        z.writestr("xl/_rels/workbook.xml.rels",
                   f'<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="{_PACKAGE_REL}">'
                   f'{relations}</Relationships>')
        for i, (_, header, rows) in enumerate(sheets, start=1):
            lines = []
            for r, values in enumerate([header, *rows], start=1):
                cells = "".join(_cell(f"{_column_letters(c)}{r}", v) for c, v in enumerate(values))
                lines.append(f'<row r="{r}">{cells}</row>')
            z.writestr(f"xl/worksheets/sheet{i}.xml",
                       f'<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="{_MAIN}">'
                       f'<sheetData>{"".join(lines)}</sheetData></worksheet>')


def _column_index(reference):
    letters = re.match(r"[A-Z]+", reference).group(0)
    index = 0
    for ch in letters:
        index = index * 26 + ord(ch) - 64
    return index - 1


def read_xlsx(path):
    """{sheet name: [row dicts by the first row's names]} of a workbook."""
    text = tp.text(path)
    if text.lower().endswith(".xls"):
        raise NotBuiltError("arcpy.topographic: an .xls workbook (BIFF8) is not read by grapy; save it as .xlsx, the "
                            "Office Open XML workbook (ECMA-376).")
    ns = {"m": _MAIN}
    with zipfile.ZipFile(text) as z:
        shared = []
        if "xl/sharedStrings.xml" in z.namelist():
            root = ElementTree.fromstring(z.read("xl/sharedStrings.xml"))
            for si in root.findall("m:si", ns):
                shared.append("".join(t.text or "" for t in si.iter(f"{{{_MAIN}}}t")))
        workbook = ElementTree.fromstring(z.read("xl/workbook.xml"))
        relations = ElementTree.fromstring(z.read("xl/_rels/workbook.xml.rels"))
        targets = {r.get("Id"): r.get("Target") for r in relations}
        out = {}
        for sheet in workbook.find("m:sheets", ns):
            target = targets[sheet.get(f"{{{_REL}}}id")].lstrip("/")
            target = target if target.startswith("xl/") else "xl/" + target
            root = ElementTree.fromstring(z.read(target))
            grid = []
            for row in root.iter(f"{{{_MAIN}}}row"):
                values = {}
                for c in row.findall("m:c", ns):
                    kind, v = c.get("t"), c.find("m:v", ns)
                    if kind == "inlineStr":
                        value = "".join(t.text or "" for t in c.iter(f"{{{_MAIN}}}t"))
                    elif v is None:
                        value = None
                    elif kind == "s":
                        value = shared[int(v.text)]
                    elif kind == "b":
                        value = v.text == "1"
                    elif kind in ("str", "e"):
                        value = v.text
                    else:
                        number = float(v.text)
                        value = int(number) if number.is_integer() else number
                    values[_column_index(c.get("r"))] = value
                grid.append(values)
            if not grid:
                out[sheet.get("name")] = []
                continue
            header = {i: str(n).strip() for i, n in grid[0].items() if n not in (None, "")}
            out[sheet.get("name")] = [{name: r.get(i) for i, name in header.items()} for r in grid[1:]
                                      if any(v not in (None, "") for v in r.values())]
    return out


def _sheet(book, *names):
    for name in names:
        for held in book:
            if held.replace(" ", "").lower() == name.replace(" ", "").lower():
                return book[held]
    return None


def _true(value):
    if isinstance(value, bool):
        return value
    return str(value).strip().upper() in ("TRUE", "1", "YES", "T")


# ---- workspaces ------------------------------------------------------------------------------------------------------

def workspace_file(value):
    file, _ = _gpkg.split(tp.text(value))
    if not os.path.exists(file):
        raise ExecuteError(f"ERROR 000732: '{file}' does not exist.")
    return file


def classes(file):
    """{table name: (data type, short name)} of a GeoPackage's feature classes and tables (grapy's and the
    GeoPackage's own tables left out)."""
    connection = _gpkg.connect(file)
    try:
        rows = connection.execute("select table_name, data_type from gpkg_contents where data_type in "
                                  "('features', 'attributes') order by table_name").fetchall()
    finally:
        connection.close()
    return {name: (kind, name.rsplit("/", 1)[-1]) for name, kind in rows
            if not name.lower().startswith(("grapy_", "gpkg_", "rtree_"))}


def by_short_name(file):
    return {short: table for table, (_, short) in classes(file).items()}


# ---- the cross-reference database ------------------------------------------------------------------------------------

_XREF = {
    "DatasetMapping": [("Id", "Integer"), ("SourceDataset", "String"), ("TargetDataset", "String"),
                       ("WhereClause", "String"), ("Subtype", "String")],
    "FieldMapping": [("Id", "Integer"), ("DatasetMapId", "Integer"), ("SourceField", "String"),
                     ("TargetField", "String"), ("WhereClause", "String")],
    "ValueMapping": [("Id", "Integer"), ("FieldMappingId", "Integer"), ("FromValue", "String"), ("ToValue", "String")],
    "AnnoSymbolIds": [("Id", "Integer"), ("SourceDataset", "String"), ("TargetDataset", "String"),
                      ("SourceSymbolId", "Integer"), ("TargetSymbolId", "Integer")],
    "SDEOwner": [("Id", "Integer"), ("SDESourceOwner", "String"), ("SDETargetOwner", "String")],
    "Version": [("Id", "Integer"), ("Version", "String")],
}
XREF_VERSION = "1"


def _columns(file, table):
    connection = _gpkg.connect(file)
    try:
        columns = _gpkg.describe(connection, table)[0]
    finally:
        connection.close()
    return {c.name: c.type for c in columns}


def create_cross_reference_geodatabase(source_workspace, target_database, out_database, mapping_file):
    tool = "CreateCrossReferenceGeodatabase"
    source, target = workspace_file(source_workspace), workspace_file(target_database)
    out = tp.text(out_database)
    out = out if out.lower().endswith(".gpkg") else out + ".gpkg"
    from .management import _empty_geopackage
    _empty_geopackage(out)
    for name, columns in _XREF.items():
        tp.create(f"{out}/{name}", "Geometry", 0, columns)
    src, tgt = by_short_name(source), by_short_name(target)
    datasets, fields, values = [], [], []
    if tp.given(mapping_file):
        book = read_xlsx(mapping_file)
        for row in _sheet(book, "Dataset Mapping", "DatasetMapping", "Data Mapping") or []:
            if row.get("SourceDataset") not in src:
                AddWarning(f"{tool}: the mapping file's source dataset '{row.get('SourceDataset')}' was not found.")
            if row.get("TargetDataset") not in tgt:
                AddWarning(f"{tool}: the mapping file's target dataset '{row.get('TargetDataset')}' was not found.")
            datasets.append({k: row.get(k) for k, _ in _XREF["DatasetMapping"]})
        names = {d["Id"]: d for d in datasets}
        for row in _sheet(book, "Field Mapping", "FieldMapping") or []:
            mapping = names.get(row.get("DatasetMapId"))
            if mapping is None:
                AddWarning(f"{tool}: the field mapping {row.get('Id')} names no dataset mapping "
                           f"{row.get('DatasetMapId')}.")
            else:
                for side, held in (("SourceField", src.get(mapping["SourceDataset"])),
                                   ("TargetField", tgt.get(mapping["TargetDataset"]))):
                    wanted = row.get(side)
                    owner = source if side == "SourceField" else target
                    if held and wanted not in _columns(owner, held):
                        AddWarning(f"{tool}: the mapping file's field '{wanted}' was not found in '{held}'.")
            fields.append({k: row.get(k) for k, _ in _XREF["FieldMapping"]})
        for row in _sheet(book, "Value Mapping", "ValueMapping") or []:
            values.append({k: (None if row.get(k) is None else str(row.get(k)) if k in ("FromValue", "ToValue")
                               else row.get(k)) for k, _ in _XREF["ValueMapping"]})
    else:
        for name in tgt:
            if name not in src:
                AddWarning(f"{tool}: the target dataset '{name}' has no source dataset.")
        for name, table in src.items():
            if name not in tgt:
                AddWarning(f"{tool}: the source dataset '{name}' has no target dataset.")
                continue
            number = len(datasets) + 1
            datasets.append({"Id": number, "SourceDataset": name, "TargetDataset": name})
            mine, theirs = _columns(source, table), _columns(target, tgt[name])
            for field, kind in mine.items():
                if field not in theirs:
                    AddWarning(f"{tool}: the source field '{name}.{field}' has no target field.")
                    continue
                if theirs[field] != kind:
                    AddWarning(f"{tool}: '{name}.{field}' is {kind} in the source and {theirs[field]} in the target, "
                               "and no value mapping is given.")
                fields.append({"Id": len(fields) + 1, "DatasetMapId": number, "SourceField": field,
                               "TargetField": field})
            for field in theirs:
                if field not in mine:
                    AddWarning(f"{tool}: the target field '{name}.{field}' has no source field.")
    tp.insert(f"{out}/DatasetMapping", datasets)
    tp.insert(f"{out}/FieldMapping", fields)
    tp.insert(f"{out}/ValueMapping", values)
    tp.insert(f"{out}/Version", [{"Id": 1, "Version": XREF_VERSION}])
    AddMessage(f"{tool}: {len(datasets)} dataset and {len(fields)} field mappings written.")
    return Result(out)


def load_data(in_cross_reference, in_sources, in_target, in_dataset_map_defs, row_level_errors):
    from . import da
    tool = "LoadData"
    xref = workspace_file(in_cross_reference)
    log_rows = tp.flag(tool, row_level_errors, "ROW_LEVEL_ERROR_LOGGING", "NO_ROW_LEVEL_ERROR_LOGGING",
                       "ROW_LEVEL_ERROR_LOGGING")
    mappings = tp.read(f"{xref}/DatasetMapping", shapes=False)
    field_rows = tp.read(f"{xref}/FieldMapping", shapes=False)
    value_rows = tp.read(f"{xref}/ValueMapping", shapes=False)
    if tp.given(in_dataset_map_defs):
        chosen = set()
        for definition in tp.listed(in_dataset_map_defs):
            text = definition if isinstance(definition, str) else " | ".join(str(v) for v in definition)
            first = text.split("|")[0].strip()
            if not first.lstrip("-").isdigit() or int(first) not in {m["Id"] for m in mappings}:
                raise ExecuteError(f"ERROR 000800: {tool}: '{text}' names no dataset mapping of the cross-reference.")
            chosen.add(int(first))
        mappings = [m for m in mappings if m["Id"] in chosen]
    target_file = workspace_file(in_target)
    targets = by_short_name(target_file)
    loaded = errors = 0
    for source_value in tp.listed(in_sources):
        source_file = workspace_file(source_value)
        sources = by_short_name(source_file)
        for mapping in mappings:
            source_table = sources.get(mapping["SourceDataset"])
            target_table = targets.get(mapping["TargetDataset"])
            if source_table is None:
                AddWarning(f"Source dataset [{mapping['SourceDataset']}] not found or is inaccessible.")
                continue
            if target_table is None:
                AddWarning(f"Target dataset [{mapping['TargetDataset']}] not found or is inaccessible.")
                continue
            source_path, target_path = f"{source_file}/{source_table}", f"{target_file}/{target_table}"
            own_fields = [f for f in field_rows if f["DatasetMapId"] == mapping["Id"]]
            applies = {}
            for f in own_fields:
                if tp.given(f["WhereClause"]):
                    applies[f["Id"]] = {r.oid for r in tp.read(source_path, fields=[], where=f["WhereClause"],
                                                               shapes=False)}
            values = {}
            for v in value_rows:
                values.setdefault(v["FieldMappingId"], {})[str(v["FromValue"])] = v["ToValue"]
            subtype = None
            if tp.given(mapping["Subtype"]):
                from . import _subtypes
                connection, table = _gpkg.table_of(target_path)
                try:
                    held = _subtypes.field(connection, table)
                finally:
                    connection.close()
                if held is None:
                    raise ExecuteError(f"ERROR 000800: {tool}: '{target_table}' has no subtype field for mapping "
                                       f"{mapping['Id']}.")
                subtype = (held[0], int(mapping["Subtype"]))
            source_kind = tp.schema(source_path)[1]
            target_kind = tp.schema(target_path)[1]
            names = sorted({f["SourceField"] for f in own_fields})
            rows = tp.read(source_path, fields=names, where=mapping["WhereClause"] if tp.given(mapping["WhereClause"])
                           else None, shapes=source_kind != "Geometry")
            target_fields = sorted({f["TargetField"] for f in own_fields} | ({subtype[0]} if subtype else set()))
            tokens = target_fields + (["SHAPE@"] if target_kind != "Geometry" else [])
            with da.InsertCursor(target_path, tokens) as cursor:
                for row in rows:
                    out = {}
                    for f in own_fields:
                        if f["Id"] in applies and row.oid not in applies[f["Id"]]:
                            continue
                        value = row[f["SourceField"]]
                        mapped = values.get(f["Id"], {})
                        if value is not None and str(value) in mapped:
                            value = mapped[str(value)]
                        elif isinstance(value, float) and value.is_integer() and str(int(value)) in mapped:
                            value = mapped[str(int(value))]
                        out[f["TargetField"]] = value
                    if subtype:
                        out[subtype[0]] = subtype[1]
                    record = [out.get(f) for f in target_fields]
                    if target_kind != "Geometry":
                        record.append(tp.as_multi(row.shape, target_kind) if row.shape is not None else None)
                    try:
                        cursor.insertRow(record)
                        loaded += 1
                    except Exception as said:  # noqa: BLE001 — a row the target refuses is logged, not fatal
                        errors += 1
                        if log_rows:
                            AddWarning(f"{tool}: row {row.oid} of '{mapping['SourceDataset']}' was not loaded into "
                                       f"'{mapping['TargetDataset']}': {said}")
    AddMessage(f"{tool}: {loaded} rows loaded, {errors} rejected.")
    return Result(tp.text(in_target))


# ---- the geodatabase workbook ----------------------------------------------------------------------------------------

_SHEETS = {
    "SpatialReferences": ["SpatialReference_ID", "Description", "FactoryCode", "SpatialReferenceString",
                          "VerticalDescription", "VerticalFactoryCode", "XMin", "YMin", "XMax", "YMax", "XYPrecision",
                          "ZMin", "ZMax", "ZPrecision", "MMin", "MMax", "MPrecision"],
    "DatasetContainers": ["Dataset_ID", "CreateDataset", "DatasetType", "DatasetName", "SpatialReference_ID"],
    "ObjectClasses": ["ObjectClass_ID", "Dataset_ID", "CreateObjectClass", "ObjectClassName", "ObjectClassAlias",
                      "ObjectClassDescription", "ObjectClassDataType", "ObjectClassFeatureType", "ObjectClassGeometry",
                      "SpatialReference_ID", "HasZ", "HasM"],
    "Subtypes": ["Subtype_ID", "ObjectClass_ID", "CreateSubtype", "SubtypeField", "SubtypeNumber", "SubtypeName",
                 "SubtypeDescription", "SubtypeDefault"],
    "Fields": ["Field_ID", "ObjectClass_ID", "Subtype_ID", "CreateField", "FieldName", "FieldAlias", "FieldDescription",
               "FieldDomainName", "FieldType", "FieldDefaultValue", "FieldEditable", "FieldIsNullable", "FieldLength",
               "FieldPrecision", "FieldScale"],
    "Domains": ["Domain_ID", "CreateDomain", "Name", "Description", "DomainCode", "DomainMin", "DomainMax",
                "DomainDescription", "DomainDataType", "DomainFieldType", "DomainSplitPolicy", "DomainMergePolicy"],
    "MetaInfo": ["MetaInfo_ID", "ObjectClass_ID", "Subtype_ID", "CreateMetaInfo", "MetaInfoName", "MetaInfoAlias",
                 "MetaInfoDescription", "MetaInfoDomainName", "MetaInfoFieldType", "MetaInfoDefaultValue",
                 "MetaInfoEditable", "MetaInfoIsNullable", "MetaInfoLength", "MetaInfoPrecision", "MetaInfoScale"],
}
_FIELD_TYPES = {"SmallInteger": "esriFieldTypeSmallInteger", "Integer": "esriFieldTypeInteger",
                "BigInteger": "esriFieldTypeBigInteger", "Single": "esriFieldTypeSingle", "Double": "esriFieldTypeDouble",
                "String": "esriFieldTypeString", "Date": "esriFieldTypeDate", "Blob": "esriFieldTypeBlob",
                "Guid": "esriFieldTypeGUID"}
_ADD_FIELD = {"esriFieldTypeSmallInteger": "SHORT", "esriFieldTypeInteger": "LONG", "esriFieldTypeBigInteger": "BIGINTEGER",
              "esriFieldTypeSingle": "FLOAT", "esriFieldTypeDouble": "DOUBLE", "esriFieldTypeString": "TEXT",
              "esriFieldTypeDate": "DATE", "esriFieldTypeBlob": "BLOB", "esriFieldTypeGUID": "GUID",
              "esriFieldTypeGlobalID": "GUID"}
_GEOMETRIES = {"Point": "esriGeometryPoint", "Multipoint": "esriGeometryMultipoint", "Polyline": "esriGeometryPolyline",
               "Polygon": "esriGeometryPolygon"}
_DOMAIN_TYPES = {"SHORT": "Short", "LONG": "Integer", "BIGINTEGER": "BigInteger", "FLOAT": "Float", "DOUBLE": "Double",
                 "TEXT": "String", "DATE": "Date"}
_SPLIT = {"DEFAULT": "esriSPTDefaultValue", "DUPLICATE": "esriSPTDuplicate", "GEOMETRY_RATIO": "esriSPTGeometryRatio"}
_MERGE = {"DEFAULT": "esriMPTDefaultValue", "SUM_VALUES": "esriMPTSumValues", "AREA_WEIGHTED": "esriMPTAreaWeighted"}


def _held(connection, name):
    return connection.execute("select 1 from sqlite_master where name = ?", (name,)).fetchone() is not None


def generate_excel_from_geodatabase(in_geodatabase, out_excel_file):
    from . import _subtypes
    file = workspace_file(in_geodatabase)
    out = tp.text(out_excel_file)
    connection = _gpkg.connect(file)
    sheets = {name: [] for name in _SHEETS}
    try:
        references = {}

        def reference(srid):
            if srid is None:
                return None
            if srid not in references:
                row = connection.execute("select srs_name, definition from gpkg_spatial_ref_sys where srs_id = ?",
                                         (srid,)).fetchone()
                references[srid] = len(references) + 1
                sheets["SpatialReferences"].append([references[srid], row[0] if row else "", srid if srid > 0 else None,
                                                    row[1] if row else ""] + [None] * 13)
            return references[srid]

        datasets = {}
        if _held(connection, "grapy_feature_datasets"):
            for name, srid in connection.execute("select name, srs_id from grapy_feature_datasets order by name"):
                datasets[name] = len(datasets) + 1
                sheets["DatasetContainers"].append([datasets[name], True, "esriDTFeatureDataset", name,
                                                    reference(srid)])
        domains = {}
        if _held(connection, "grapy_domains"):
            for name, description, field_type, kind, split, merge in connection.execute(
                    "select name, description, field_type, domain_type, split_policy, merge_policy from grapy_domains "
                    "order by name"):
                common = [_DOMAIN_TYPES.get(field_type, field_type), _SPLIT.get(split, "esriSPTDefaultValue"),
                          _MERGE.get(merge, "esriMPTDefaultValue")]
                if kind == "CODED":
                    for code, text in connection.execute(
                            "select value, description from gpkg_data_column_constraints where constraint_name = ? and "
                            "constraint_type = 'enum' order by rowid", (name,)):
                        from .management import _code_value
                        sheets["Domains"].append([len(sheets["Domains"]) + 1, True, name, description,
                                                  _code_value(code, field_type), None, None,
                                                  text, "esriDTCodedValue", *common])
                else:
                    low, high = connection.execute("select min, max from gpkg_data_column_constraints where "
                                                   "constraint_name = ? and constraint_type = 'range'",
                                                   (name,)).fetchone() or (None, None)
                    sheets["Domains"].append([len(sheets["Domains"]) + 1, True, name, description, None, low, high, None,
                                              "esriDTRange", *common])
                domains[name] = True
        field_domains = {}
        if _held(connection, "gpkg_data_columns"):
            for table, column, constraint in connection.execute(
                    "select table_name, column_name, constraint_name from gpkg_data_columns"):
                field_domains[(table, column.lower())] = constraint
        for table, (kind, short) in classes(file).items():
            columns, shape, srid, _ = _gpkg.describe(connection, table)
            has_z, has_m = _gpkg.dimensions(connection, table)
            dataset = datasets.get(table.rsplit("/", 1)[0]) if "/" in table else None
            number = len(sheets["ObjectClasses"]) + 1
            description = connection.execute("select description from gpkg_contents where table_name = ?",
                                             (table,)).fetchone()[0]
            features = kind == "features"
            sheets["ObjectClasses"].append([number, dataset if dataset else -9999, True, short, short, description or None,
                                            "esriDTFeatureClass" if features else "esriDTTable",
                                            "esriFTSimple" if features else None,
                                            _GEOMETRIES.get(shape) if features else None,
                                            reference(srid) if features else None, has_z if features else False,
                                            has_m if features else False])
            subtype = _subtypes.field(connection, table)
            codes = _subtypes.codes(connection, table)
            defaults = _subtypes.defaults(connection, table)
            per_subtype = _subtypes.domains(connection, table)
            ids = {}
            for code, name in codes.items():
                ids[code] = len(sheets["Subtypes"]) + 1
                sheets["Subtypes"].append([ids[code], number, True, subtype[0], code, name, name,
                                           code == subtype[1]])
            notnull = {c.name: not c.nullable for c in columns}
            for column in columns:
                for code in (list(codes) if codes else [None]):
                    domain = per_subtype.get((column.name.lower(), code)) if code is not None else None
                    domain = domain or field_domains.get((table, column.name.lower()))
                    default = defaults.get((column.name.lower(), code), defaults.get((column.name.lower(), None)))
                    sheets["Fields"].append([len(sheets["Fields"]) + 1, number, ids.get(code, -9999), True, column.name,
                                             column.name, None, domain, _FIELD_TYPES.get(column.type, column.type),
                                             default, True, not notnull[column.name], None, None, None])
    finally:
        connection.close()
    write_xlsx(out, [(name, _SHEETS[name], sheets[name]) for name in _SHEETS])
    AddMessage(f"GenerateExcelFromGeodatabase: {len(sheets['ObjectClasses'])} object classes written.")
    return Result(out)


def generate_geodatabase_from_excel(in_excel_file, out_geodatabase):
    from . import management
    tool = "GenerateGeodatabaseFromExcel"
    book = read_xlsx(in_excel_file)
    missing = [name for name in ("SpatialReferences", "ObjectClasses", "Fields") if _sheet(book, name) is None]
    if missing:
        raise ExecuteError(f"ERROR 000800: {tool}: the workbook has no worksheet {', '.join(missing)}.")
    out = tp.text(out_geodatabase)
    out = out if out.lower().endswith(".gpkg") else out + ".gpkg"
    if not os.path.exists(out):
        management._empty_geopackage(out)
    references = {}
    for row in _sheet(book, "SpatialReferences"):
        code = row.get("FactoryCode")
        if code in (None, "") and row.get("SpatialReferenceString"):
            from pyproj import CRS
            found = CRS.from_wkt(str(row["SpatialReferenceString"])).to_epsg()
            code = found
        references[row.get("SpatialReference_ID")] = int(code) if code not in (None, "") else 0
    datasets = {}
    for row in _sheet(book, "DatasetContainers") or []:
        if not _true(row.get("CreateDataset", True)):
            continue
        management.CreateFeatureDataset(out, row["DatasetName"], references.get(row.get("SpatialReference_ID")) or None)
        datasets[row["Dataset_ID"]] = row["DatasetName"]
    for row in _sheet(book, "Domains") or []:
        if not _true(row.get("CreateDomain", True)):
            continue
        name = row["Name"]
        kind = "CODED" if str(row.get("DomainDataType")) == "esriDTCodedValue" else "RANGE"
        field_type = {v.upper(): k for k, v in _DOMAIN_TYPES.items()}.get(str(row.get("DomainFieldType")).upper(), "LONG")
        split = {v: k for k, v in _SPLIT.items()}.get(row.get("DomainSplitPolicy"), "DEFAULT")
        merge = {v: k for k, v in _MERGE.items()}.get(row.get("DomainMergePolicy"), "DEFAULT")
        connection = _gpkg.connect(out)
        try:
            management._domain_tables(connection)
            exists = connection.execute("select 1 from grapy_domains where name = ?", (name,)).fetchone()
        finally:
            connection.close()
        if not exists:
            management.CreateDomain(out, name, row.get("Description") or "", field_type, kind, split, merge)
        if kind == "CODED":
            management.AddCodedValueToDomain(out, name, row.get("DomainCode"), row.get("DomainDescription") or "")
        else:
            management.SetValueForRangeDomain(out, name, row.get("DomainMin"), row.get("DomainMax"))
    meta = [r for r in _sheet(book, "MetaInfo") or [] if _true(r.get("CreateMetaInfo", True))]
    tables = {}
    for row in _sheet(book, "ObjectClasses"):
        if not _true(row.get("CreateObjectClass", True)):
            continue
        dataset = datasets.get(row.get("Dataset_ID"))
        container = f"{out}/{dataset}" if dataset else out
        name = row["ObjectClassName"]
        if row.get("ObjectClassDataType") == "esriDTTable":
            management.CreateTable(out, f"{dataset}/{name}" if dataset else name)
        else:
            geometry = {v: k for k, v in _GEOMETRIES.items()}.get(row.get("ObjectClassGeometry"))
            if geometry is None:
                raise ExecuteError(f"ERROR 000800: {tool}: '{name}' has no geometry type grapy holds.")
            management.CreateFeatureclass(container, name, geometry.upper().replace("MULTIPOINT", "MULTIPOINT"),
                                          has_z="ENABLED" if _true(row.get("HasZ")) else "DISABLED",
                                          has_m="ENABLED" if _true(row.get("HasM")) else "DISABLED",
                                          spatial_reference=references.get(row.get("SpatialReference_ID")) or None)
        tables[row["ObjectClass_ID"]] = f"{container}/{name}"
        if row.get("ObjectClassAlias") and row["ObjectClassAlias"] != name:
            management.AlterAliasName(tables[row["ObjectClass_ID"]], row["ObjectClassAlias"])
    subtypes = {}
    for row in _sheet(book, "Subtypes") or []:
        if not _true(row.get("CreateSubtype", True)) or row.get("ObjectClass_ID") not in tables:
            continue
        subtypes[row["Subtype_ID"]] = (row["ObjectClass_ID"], int(row["SubtypeNumber"]), row)
    fields = [r for r in _sheet(book, "Fields") if _true(r.get("CreateField", True)) and r.get("ObjectClass_ID") in tables]
    added = set()

    def add(table, name, kind, nullable=True):
        if (table, name.lower()) in added or tp.find_field(table, name):
            added.add((table, name.lower()))
            return
        management.AddField(table, name, _ADD_FIELD.get(str(kind), "TEXT"))
        added.add((table, name.lower()))

    for row in fields:
        table = tables[row["ObjectClass_ID"]]
        if str(row["FieldName"]).upper() == "METADATAINFO":
            for m in meta:
                add(table, m["MetaInfoName"], m.get("MetaInfoFieldType"))
            continue
        add(table, row["FieldName"], row.get("FieldType"))
    for subtype_id, (class_id, code, row) in sorted(subtypes.items(), key=lambda t: t[1][1]):
        table = tables[class_id]
        add(table, row["SubtypeField"], "esriFieldTypeInteger")
        connection, name = _gpkg.table_of(table)
        try:
            from . import _subtypes
            held = _subtypes.field(connection, name)
        finally:
            connection.close()
        if held is None:
            management.SetSubtypeField(table, row["SubtypeField"])
        management.AddSubtype(table, code, row.get("SubtypeName") or str(code))
        if _true(row.get("SubtypeDefault")):
            management.SetDefaultSubtype(table, code)
    for row in fields:
        table = tables[row["ObjectClass_ID"]]
        if str(row["FieldName"]).upper() == "METADATAINFO":
            continue
        code = subtypes.get(row.get("Subtype_ID"), (None, None))[1]
        if row.get("FieldDomainName"):
            management.AssignDomainToField(table, row["FieldName"], row["FieldDomainName"],
                                           code if code is not None else None)
        if row.get("FieldDefaultValue") not in (None, ""):
            management.AssignDefaultToField(table, row["FieldName"], row["FieldDefaultValue"],
                                            code if code is not None else None)
    AddMessage(f"{tool}: {len(tables)} object classes written to '{out}'.")
    return Result(out)


# ---- GeodatabaseToShape ----------------------------------------------------------------------------------------------

_SHAPE_CODES = {"Point": 1, "Multipoint": 8, "Polyline": 3, "Polygon": 5}
_MGCP_PREFIX = {"Polygon": "S", "Polyline": "L", "Point": "P", "Multipoint": "P"}
_MUVD_PREFIX = {"Polygon": "S", "Polyline": "C", "Point": "P", "Multipoint": "P"}


def geodatabase_to_shape(in_features, output_folder, coded_value_domain_export_mode, conversion_method,
                         create_empties):
    from . import _subtypes
    from .conversion import _dbf_layout, _dbf_names, _write_dbf, _write_shapefile
    tool = "GeodatabaseToShape"
    mode = tp.word(coded_value_domain_export_mode, "VALUES")
    if mode not in ("VALUES", "DESCRIPTIONS", "VALUES_AND_DESCRIPTIONS"):
        raise ExecuteError(f"ERROR 000800: {tool}: '{mode}' is not a coded value domain export mode.")
    method = tp.word(conversion_method, "DEFENSE_BY_SUBTYPE")
    if method not in ("DEFENSE_BY_SUBTYPE", "DEFENSE_BY_FEATURECLASS", "GENERIC_BY_FEATURECLASS", "GENERIC_BY_SUBTYPE",
                      "MGCP", "MUVD"):
        raise ExecuteError(f"ERROR 000800: {tool}: '{method}' is not a conversion method.")
    empties = tp.flag(tool, create_empties, "CREATE_EMPTIES", "NO_CREATE_EMPTIES", "NO_CREATE_EMPTIES")
    folder = tp.text(output_folder)
    if not os.path.isdir(folder):
        raise ExecuteError(f"ERROR 000732: {tool}: the folder '{folder}' does not exist.")
    written = []
    for each in tp.listed(in_features):
        path = tp.dataset(each)
        columns, kind, srid, _ = tp.schema(path)
        code = _SHAPE_CODES.get(kind)
        if code is None:
            raise ExecuteError(f"ERROR 000800: {tool}: '{path}' holds {kind} shapes, which a shapefile cannot.")
        connection, table = _gpkg.table_of(path)
        try:
            subtype = _subtypes.field(connection, table)
            names_of = _subtypes.codes(connection, table)
            per_subtype = _subtypes.domains(connection, table)
            own = {}
            if _held(connection, "gpkg_data_columns"):
                own = {c.lower(): d for c, d in connection.execute(
                    "select column_name, constraint_name from gpkg_data_columns where table_name = ?", (table,))}
            coded = {}
            if _held(connection, "gpkg_data_column_constraints"):
                for name, value, text in connection.execute(
                        "select constraint_name, value, description from gpkg_data_column_constraints where "
                        "constraint_type = 'enum'"):
                    coded.setdefault(name, {})[str(value)] = text
        finally:
            connection.close()
        rows = tp.read(each)
        short = table.rsplit("/", 1)[-1]
        groups = {}
        if method in ("DEFENSE_BY_SUBTYPE", "GENERIC_BY_SUBTYPE") and subtype:
            for c, name in names_of.items():
                groups[name] = (c, [r for r in rows if r[subtype[0]] == c])
        elif method in ("MGCP", "MUVD"):
            code_field = tp.find_field(path, "FCODE") or tp.find_field(path, "F_CODE")
            if code_field is None:
                raise ExecuteError(f"ERROR 000728: {tool}: {method} names each shapefile by its feature code, and "
                                   f"'{short}' has no FCODE or F_CODE field.")
            prefix = (_MGCP_PREFIX if method == "MGCP" else _MUVD_PREFIX)[kind]
            for r in rows:
                groups.setdefault(prefix + str(r[code_field]), (None, []))[1].append(r)
        else:
            groups[short] = (None, rows)
        for name, (subtype_code, members) in groups.items():
            if not members and not empties:
                continue
            fields = [c for c in columns]
            if method == "DEFENSE_BY_SUBTYPE" and subtype:
                # The page: only the subtype's applicable fields. INFERRED: a field that holds a value in one of the
                # subtype's features, or takes a domain in the subtype; with no features, every field.
                fields = [c for c in columns if not members or c.name == subtype[0]
                          or (c.name.lower(), subtype_code) in per_subtype
                          or any(r[c.name] is not None for r in members)]
            out_names, out_types, values = [], [], [[] for _ in members]
            for c in fields:
                domain = per_subtype.get((c.name.lower(), subtype_code)) or own.get(c.name.lower())
                lookup = coded.get(domain) if domain else None
                label = c.name.rstrip("_") if method.startswith("DEFENSE") else c.name
                if lookup and mode == "DESCRIPTIONS":
                    out_names.append(label)
                    out_types.append(_gpkg.Column(label, "String"))
                    for i, r in enumerate(members):
                        values[i].append(lookup.get(_code_key(r[c.name]), r[c.name]))
                    continue
                out_names.append(label)
                out_types.append(c)
                for i, r in enumerate(members):
                    values[i].append(r[c.name])
                if lookup and mode == "VALUES_AND_DESCRIPTIONS":
                    out_names.append(label + "_D")
                    out_types.append(_gpkg.Column(label + "_D", "String"))
                    for i, r in enumerate(members):
                        values[i].append(lookup.get(_code_key(r[c.name])))
            stem = os.path.join(folder, name)
            layout = _dbf_layout(out_types, values)
            _write_dbf(stem + ".dbf", _dbf_names(out_names), layout, values)
            with open(stem + ".cpg", "w", encoding="ascii") as cpg:
                cpg.write("UTF-8")
            _write_shapefile(stem, code, [r.shape for r in members], srid)
            written.append(stem + ".shp")
    AddMessage(f"{tool}: {len(written)} shapefiles written.")
    return Result(folder)


def _code_key(value):
    if isinstance(value, float) and value.is_integer():
        return str(int(value))
    return None if value is None else str(value)


# ---- ExtractDataByFeature --------------------------------------------------------------------------------------------

def extract_data_by_feature(in_datasets, target_gdb, reuse_schema, filter_feature, filter_type, checkout_replica,
                            replica_name, expand_feature_classes_and_tables, get_related_data, excluded_rel_classes,
                            where_clause):
    from . import _relationships
    tool = "ExtractDataByFeature"
    if tp.flag(tool, checkout_replica, "CHECKOUT_REPLICA", "DO_NOT_CHECKOUT_REPLICA", "DO_NOT_CHECKOUT_REPLICA"):
        raise NotBuiltError("arcpy.topographic.ExtractDataByFeature: checkout_replica 'CHECKOUT_REPLICA' is not built "
                            "in grapy — a checkout replica needs versioned data in an enterprise geodatabase, which a "
                            "GeoPackage is not.")
    if tp.given(replica_name):
        raise NotBuiltError("arcpy.topographic.ExtractDataByFeature: replica_name is not built in grapy — it names a "
                            "checkout replica, which needs versioned data in an enterprise geodatabase.")
    reuse = tp.flag(tool, reuse_schema, "REUSE", "DO_NOT_REUSE", "DO_NOT_REUSE")
    kind = tp.word(filter_type, "INTERSECTS")
    if kind not in ("INTERSECTS", "CONTAINS", "CLIP"):
        raise ExecuteError(f"ERROR 000800: {tool}: filter_type is INTERSECTS, CONTAINS or CLIP.")
    expand = tp.word(expand_feature_classes_and_tables, "DO_NOT_ADD")
    if expand not in ("USE_DEFAULTS", "ADD_WITH_SCHEMA_ONLY", "ALL_ROWS", "DO_NOT_ADD"):
        raise ExecuteError(f"ERROR 000800: {tool}: '{expand}' is not an expand option.")
    related = tp.flag(tool, get_related_data, "GET_RELATED", "DO_NOT_GET_RELATED", "DO_NOT_GET_RELATED")
    excluded = {str(v).rsplit("/", 1)[-1] for v in tp.listed(excluded_rel_classes)}
    target = workspace_file(target_gdb)
    area = None
    if tp.given(filter_feature):
        from ._topo_prod_relief import _one_area
        found = _one_area(tool, filter_feature).shape
        if found.geom_type not in ("Polygon", "MultiPolygon"):
            raise ExecuteError(f"ERROR 000800: {tool}: the filter feature must be a polygon.")
        area = found
    entries = []
    for row in tp.table_rows(in_datasets):
        option = str(row[1]).upper() if len(row) > 1 else "USE_FILTERS"
        if option not in ("ALL_ROWS", "SCHEMA_ONLY", "USE_FILTERS"):
            raise ExecuteError(f"ERROR 000800: {tool}: '{row[1]}' is not ALL_ROWS, SCHEMA_ONLY or USE_FILTERS.")
        entries.append((tp.dataset(row[0]), option))
    files = {os.path.normcase(_gpkg.split(p)[0]) for p, _ in entries}
    if len(files) > 1:
        raise ExecuteError(f"ERROR 000800: {tool}: every input must come from one workspace.")
    if tp.given(where_clause):
        lacking = [p for p, _ in entries if not _fields_exist(p, where_clause)]
        if lacking:
            raise ExecuteError(f"ERROR 000800: {tool}: the where clause names fields not in {', '.join(lacking)}.")
    source_file = _gpkg.split(entries[0][0])[0] if entries else None
    wanted = {p: option for p, option in entries}
    # Topology participants come with the classes taking part in a topology with any input.
    connection = _gpkg.connect(source_file)
    try:
        topology_classes = {}
        if _held(connection, "grapy_topology_class"):
            for topology, name in connection.execute("select topology, class_name from grapy_topology_class"):
                topology_classes.setdefault(topology, []).append(f"{source_file}/{name}")
        relationships = _relationships.classes(connection)
    finally:
        connection.close()
    for members in topology_classes.values():
        if any(os.path.normcase(m) in {os.path.normcase(p) for p in wanted} for m in members):
            for m in members:
                wanted.setdefault(m, "USE_FILTERS")
    if expand != "DO_NOT_ADD":
        extra = "SCHEMA_ONLY" if expand in ("USE_DEFAULTS", "ADD_WITH_SCHEMA_ONLY") else "ALL_ROWS"
        for rc in relationships:
            if rc["name"].rsplit("/", 1)[-1] in excluded:
                continue
            origin = f"{source_file}/{rc['origin']}"
            if any(os.path.normcase(origin) == os.path.normcase(p) for p in wanted):
                wanted.setdefault(f"{source_file}/{rc['destination']}", extra)
    extracted = {}
    for path, option in wanted.items():
        rows = []
        if option != "SCHEMA_ONLY":
            rows = tp.read(path, where=where_clause if tp.given(where_clause) else None,
                           shapes=tp.schema(path)[1] != "Geometry")
            if option == "USE_FILTERS" and area is not None:
                kept = []
                for r in rows:
                    if r.shape is None:
                        kept.append(r)   # a table is not filtered by location
                    elif kind == "CONTAINS" and area.contains(r.shape) or kind == "INTERSECTS" and area.intersects(r.shape):
                        kept.append(r)
                    elif kind == "CLIP" and area.intersects(r.shape):
                        r.shape = area.intersection(r.shape)
                        kept.append(r)
                rows = kept
        extracted[path] = rows
    if related:
        connection = _gpkg.connect(source_file)
        try:
            for rc in relationships:
                if rc["name"].rsplit("/", 1)[-1] in excluded:
                    continue
                origin = f"{source_file}/{rc['origin']}"
                destination = f"{source_file}/{rc['destination']}"
                key = next((p for p in extracted if os.path.normcase(p) == os.path.normcase(origin)), None)
                if key is None:
                    continue
                have = {r.oid for r in extracted[key]}
                wanted_ids = {d for o, d in _relationships.pairs(connection, rc) if o in have}
                into = next((p for p in extracted if os.path.normcase(p) == os.path.normcase(destination)), destination)
                held = {r.oid for r in extracted.get(into, [])}
                extra_rows = [r for r in tp.read(destination, shapes=tp.schema(destination)[1] != "Geometry")
                              if r.oid in wanted_ids - held]
                extracted.setdefault(into, []).extend(extra_rows)
        finally:
            connection.close()
    for path, rows in extracted.items():
        columns, shape, srid, _ = tp.schema(path)
        name = _gpkg.split(path)[1].rsplit("/", 1)[-1]
        out = f"{target}/{name}"
        has_z = _dims(path)[0]
        if not (reuse and tp.exists(out)):
            _gpkg.create(out, shape if shape != "Geometry" else "Geometry", srid if shape != "Geometry" else 0,
                         columns, has_z)
        names = [c.name for c in columns]
        tp.insert(out, [{**{n: r[n] for n in names if n in r},
                         **({"SHAPE@": tp.as_multi(r.shape, shape)} if shape != "Geometry" else {})} for r in rows])
    AddMessage(f"{tool}: {sum(len(r) for r in extracted.values())} rows extracted into {len(extracted)} datasets.")
    return Result(tp.text(target_gdb))


def _dims(path):
    connection, table = _gpkg.table_of(path)
    try:
        return _gpkg.dimensions(connection, table)
    finally:
        connection.close()


def _fields_exist(path, where):
    connection, table = _gpkg.table_of(path)
    try:
        connection.execute(f'select 1 from "{table}" where {where} limit 1')
        return True
    except Exception:  # noqa: BLE001 — a field the table does not have
        return False
    finally:
        connection.close()


# ---- topology definition files ---------------------------------------------------------------------------------------

def export_topology(topology, location, file_name):
    from . import _subtypes
    from .management import _topology
    connection, file, dataset, name, row = _topology(topology)
    try:
        root = ElementTree.Element("GrapyTopology", {"version": "1", "name": name, "clusterTolerance": repr(row[1])})
        for class_name, xy, z in connection.execute("select class_name, xy_rank, z_rank from grapy_topology_class "
                                                    "where topology = ? order by class_id", (name,)):
            ElementTree.SubElement(root, "FeatureClass", {"name": class_name.rsplit("/", 1)[-1], "xyRank": str(xy),
                                                          "zRank": str(z)})
        for rule_type, origin, destination, o_code, d_code in connection.execute(
                "select rule_type, origin, destination, origin_subtype, destination_subtype from grapy_topology_rule "
                "where topology = ? order by rule_id", (name,)):
            attributes = {"type": rule_type, "origin": origin.rsplit("/", 1)[-1]}
            if o_code is not None:
                attributes["originSubtype"] = _subtypes.codes(connection, origin).get(int(o_code), str(o_code))
            if destination:
                attributes["destination"] = destination.rsplit("/", 1)[-1]
                if d_code is not None:
                    attributes["destinationSubtype"] = _subtypes.codes(connection, destination).get(int(d_code),
                                                                                                    str(d_code))
            ElementTree.SubElement(root, "Rule", attributes)
    finally:
        connection.close()
    folder = tp.text(location)
    if not os.path.isdir(folder):
        raise ExecuteError(f"ERROR 000732: ExportTopology: the folder '{folder}' does not exist.")
    out = os.path.join(folder, tp.text(file_name))
    out = out if out.lower().endswith(".xml") else out + ".xml"
    ElementTree.ElementTree(root).write(out, encoding="utf-8", xml_declaration=True)
    return Result(out)


def import_topology(in_feature_dataset, topology_definition_file):
    from . import management
    tool = "ImportTopology"
    try:
        root = ElementTree.parse(tp.text(topology_definition_file)).getroot()
    except (OSError, ElementTree.ParseError) as said:
        raise ExecuteError(f"ERROR 000800: {tool}: '{topology_definition_file}' cannot be read: {said}") from None
    if root.tag != "GrapyTopology":
        raise ExecuteError(f"ERROR 000800: {tool}: '{topology_definition_file}' is not a topology definition grapy "
                           "wrote (ExportTopology); Esri's own layout is not published.")
    dataset = tp.text(in_feature_dataset).rstrip("/")
    made = management.CreateTopology(dataset, root.get("name"), float(root.get("clusterTolerance") or 0))[0]
    for c in root.findall("FeatureClass"):
        management.AddFeatureClassToTopology(made, f"{dataset}/{c.get('name')}", int(c.get("xyRank", 1)),
                                             int(c.get("zRank", 1)))
    for r in root.findall("Rule"):
        destination = r.get("destination")
        management.AddRuleToTopology(made, r.get("type"), f"{dataset}/{r.get('origin')}", r.get("originSubtype"),
                                     f"{dataset}/{destination}" if destination else None, r.get("destinationSubtype"))
    return Result(made)


# ---- GeoNames --------------------------------------------------------------------------------------------------------

_LATITUDE = ("lat_dd", "lat")
_LONGITUDE = ("long_dd", "long", "lon")


def _gns_rows(path):
    """The rows of an NGA GNS country file: UTF-8, tab-delimited, the first line the column names."""
    import csv
    with open(path, encoding="utf-8-sig", newline="") as handle:
        reader = csv.DictReader(handle, delimiter="\t")
        rows = [{(k or "").strip(): (v.strip() if isinstance(v, str) else v) for k, v in row.items()} for row in reader]
    return rows


def _lookup(row, names):
    lower = {k.lower(): k for k in row}
    for n in names:
        if n in lower:
            return row[lower[n]]
    return None


def geonames_to_geodatabase(in_source, in_feature_class, in_allow_duplicates, in_table, match_fields):
    tool = "GeoNamesToGeodatabase"
    source = tp.text(in_source)
    if source.lower().endswith(".shp"):
        raise NotBuiltError("arcpy.topographic.GeoNamesToGeodatabase: a GeoNames shapefile source is not built in "
                            "grapy; give the NGA GNS tab-delimited text file.")
    allow = tp.flag(tool, in_allow_duplicates, "ALLOW_DUPLICATES", "DON'T_ALLOW_DUPLICATES", "DON'T_ALLOW_DUPLICATES")
    fc, table = tp.dataset(in_feature_class), tp.dataset(in_table)
    for path in (fc, table):
        for needed in ("UFI", "UNI"):
            tp.require_field(tool, path, needed)
    srid = tp.srid_of(fc)
    rows = _gns_rows(source)
    if not rows:
        return Result(tp.text(in_feature_class), tp.text(in_table))
    mapping = {}
    for row in tp.table_rows(match_fields):
        if len(row) >= 3:
            mapping[row[2].lower()] = row[1]
        elif len(row) == 2:
            mapping[row[1].lower()] = row[0]
    columns = {k.lower(): k for k in rows[0]}

    def values_for(path):
        out = {}
        for field in tp.field_names(path):
            wanted = mapping.get(field.lower(), field)
            if wanted.lower() in columns:
                out[field] = columns[wanted.lower()]
        return out

    fc_fields, table_fields = values_for(fc), values_for(table)
    kinds = {c.name: c.type for c in tp.schema(fc)[0]}
    table_kinds = {c.name: c.type for c in tp.schema(table)[0]}
    from pyproj import Transformer
    move = Transformer.from_crs(4326, srid, always_xy=True) if srid > 0 and srid != 4326 else None
    ufi_fc, uni_table = tp.find_field(fc, "UFI"), tp.find_field(table, "UNI")
    held_features = {(r[ufi_fc], r.shape.wkb if r.shape is not None else None) for r in tp.read(fc, fields=[ufi_fc])}
    held_names = {r[uni_table] for r in tp.read(table, fields=[uni_table], shapes=False)}
    features, names, seen = [], [], set()
    for row in rows:
        ufi = row[columns["ufi"]] if "ufi" in columns else None
        lat, lon = _lookup(row, _LATITUDE), _lookup(row, _LONGITUDE)
        if ufi not in seen and lat not in (None, "") and lon not in (None, ""):
            seen.add(ufi)
            x, y = float(lon), float(lat)
            if move is not None:
                x, y = move.transform(x, y)
            from shapely.geometry import Point
            point = Point(x, y)
            record = {f: _typed(row[c], kinds[f]) for f, c in fc_fields.items()}
            if allow or (_typed(ufi, kinds[ufi_fc]), point.wkb) not in held_features:
                features.append({**record, "SHAPE@": point})
        record = {f: _typed(row[c], table_kinds[f]) for f, c in table_fields.items()}
        if allow or record.get(uni_table) not in held_names:
            names.append(record)
            held_names.add(record.get(uni_table))
    tp.insert(fc, features)
    tp.insert(table, names)
    AddMessage(f"{tool}: {len(features)} named features and {len(names)} names loaded.")
    return Result(tp.text(in_feature_class), tp.text(in_table))


def _typed(value, kind):
    if value in (None, ""):
        return None
    try:
        if kind in ("SmallInteger", "Integer", "BigInteger"):
            return int(float(value))
        if kind in ("Single", "Double"):
            return float(value)
    except ValueError:
        return None
    return value


def update_geonames(in_features, in_geonames_features, in_geonames_table, named_feature_id_field, name_id_field,
                    name_field):
    tool = "UpdateGeoNames"
    path = tp.dataset(in_features)
    nfi = tp.require_field(tool, path, named_feature_id_field)
    nfn = tp.require_field(tool, path, name_id_field)
    nam = tp.require_field(tool, path, name_field)
    gfc, gtable = tp.dataset(in_geonames_features), tp.dataset(in_geonames_table)
    ufi_fc = tp.require_field(tool, gfc, "UFI")
    ufi = tp.require_field(tool, gtable, "UFI")
    uni = tp.require_field(tool, gtable, "UNI")
    full = tp.find_field(gtable, "full_name") or tp.find_field(gtable, "FULL_NAME_RO") or \
        tp.require_field(tool, gtable, "full_name")
    nt = tp.find_field(gtable, "nt")
    rank = tp.find_field(gtable, "name_rank")
    known = {str(r[ufi_fc]) for r in tp.read(gfc, fields=[ufi_fc], shapes=False)}
    names = {}
    for r in tp.read(gtable, fields=[f for f in (ufi, uni, full, nt, rank) if f], shapes=False):
        names.setdefault(str(r[ufi]), []).append(r)
    changes, missing = {}, 0
    for r in tp.read(in_features, fields=[nfi, nfn, nam], shapes=False):
        if r[nfi] in (None, ""):
            continue
        key = str(r[nfi])
        if key not in known or key not in names:
            missing += 1
            continue
        candidates = names[key]
        chosen = next((c for c in candidates if r[nfn] not in (None, "") and str(c[uni]) == str(r[nfn])), None)
        if chosen is None:
            def order(c):
                approved = 0 if nt and str(c[nt]).upper() == "N" else 1
                ranking = float(c[rank]) if rank and c[rank] not in (None, "") else 1e9
                return (approved, ranking, str(c[uni]))
            chosen = min(candidates, key=order)
        change = {}
        if str(r[nfn]) != str(chosen[uni]):
            change[nfn] = chosen[uni]
        if r[nam] != chosen[full]:
            change[nam] = chosen[full]
        if change:
            changes[r.oid] = change
    tp.update(path, changes)
    if missing:
        AddWarning(f"{tool}: {missing} features name a feature ID the GeoNames data does not hold.")
    AddMessage(f"{tool}: {len(changes)} features updated.")
    return Result(tp.text(in_features))
