"""arcpy.management's tools grapy builds, by their documented names and parameters — ADR-223 §5.2.

A tool not here raises NotImplementedError naming it (``__getattr__``), so a toolbox using one fails with the name
rather than with an AttributeError a reader has to decode.
"""
from __future__ import annotations

import os

from . import _gpkg, _units
from ._result import Result
from ._runtime import NotBuiltError

_FIELD_TYPES = {"SHORT": "SmallInteger", "LONG": "Integer", "BIGINTEGER": "BigInteger", "FLOAT": "Single",
                "DOUBLE": "Double", "TEXT": "String", "DATE": "Date", "GUID": "Guid", "BLOB": "Blob"}
_STORED_TYPES = {"SmallInteger": "SHORT", "Integer": "LONG", "BigInteger": "BIGINTEGER", "Single": "FLOAT",
                 "Double": "DOUBLE", "String": "TEXT", "Date": "DATE", "Guid": "GUID", "Blob": "BLOB"}


def _path(out_path, out_name=None):
    path = os.path.join(out_path, out_name) if out_name else out_path
    if path.lower().endswith(".gpkg") or ".gpkg/" in path.replace("\\", "/").lower():
        return path
    return path + ".gpkg"


def CreateFeatureclass(out_path, out_name, geometry_type="POLYGON", template=None, has_m="DISABLED", has_z="DISABLED",
                       spatial_reference=None, *_, **__):
    """A new, empty feature class. Inside a feature dataset (``<file>.gpkg/<dataset>``, see CreateFeatureDataset) it
    takes the dataset's spatial reference when none is given, and one that differs is an error, as a feature dataset's
    classes share one coordinate system. ``has_z`` and ``has_m`` — ENABLED, DISABLED or SAME_AS_TEMPLATE — set
    whether its shapes carry z and m (the GeoPackage geometry column's flags)."""
    from ._reference import reference_of
    from ._runtime import ExecuteError
    shape = {"POINT": "Point", "MULTIPOINT": "Multipoint", "POLYLINE": "Polyline", "POLYGON": "Polygon"}[geometry_type.upper()]
    reference = reference_of(spatial_reference)
    columns = []
    template_z = template_m = False
    if template:
        connection, table = _gpkg.table_of(template)
        columns = _gpkg.describe(connection, table)[0]
        template_z, template_m = _gpkg.dimensions(connection, table)
        connection.close()

    def flag(value, own, name):
        word = str(value or "DISABLED").upper()
        if word == "SAME_AS_TEMPLATE":
            return own
        if word not in ("ENABLED", "DISABLED"):
            from ._runtime import NotBuiltError
            raise NotBuiltError(f"arcpy.management.CreateFeatureclass: {name} '{value}' is not built in grapy.")
        return word == "ENABLED"

    with_z, with_m = flag(has_z, template_z, "has_z"), flag(has_m, template_m, "has_m")
    path = _path(out_path, out_name)
    code = reference.factoryCode if reference else 0
    dataset = _feature_dataset_of(path)
    if dataset is not None:
        if reference is None:
            code = dataset
        elif dataset and code != dataset:
            raise ExecuteError(f"ERROR 000622: the feature dataset's spatial reference is EPSG:{dataset}, not EPSG:{code}.")
    _gpkg.create(path, shape, code, columns, with_z, with_m)
    return Result(path)


def _feature_dataset_of(path):
    """The spatial reference code of the feature dataset a feature class path lies in, or None outside one."""
    file, table = _gpkg.split(path)
    if not table or "/" not in table or not os.path.exists(file):
        return None
    connection = _gpkg.connect(file)
    try:
        if not connection.execute("select 1 from sqlite_master where name = 'grapy_feature_datasets'").fetchone():
            return None
        row = connection.execute("select srs_id from grapy_feature_datasets where name = ?",
                                 (table.rsplit("/", 1)[0],)).fetchone()
        return int(row[0]) if row else None
    finally:
        connection.close()


def AddField(in_table, field_name, field_type, field_precision=None, field_scale=None, field_length=None,
             field_alias=None, field_is_nullable="NULLABLE", *_, **__):
    from ._raster import _is_raster_path
    if _is_raster_path(in_table):
        return _add_rat_field(in_table, field_name, field_type)
    _gpkg.add_column(in_table, _gpkg.Column(field_name, _FIELD_TYPES.get(field_type.upper(), "String")))
    return Result(in_table)


def _add_rat_field(in_table, field_name, field_type):
    """A field added to a raster's attribute table, empty in every row. INFERRED: SHORT, LONG and BIGINTEGER are GDAL
    integers, FLOAT and DOUBLE reals, everything else strings; the raster must already have a table."""
    from ._raster import _read_rat, _write_rat
    from ._runtime import ExecuteError
    rat = _read_rat(in_table)
    if rat is None:
        raise ExecuteError(f"ERROR 000732: {in_table} has no attribute table; build one first (BuildRasterAttributeTable).")
    if str(field_name).lower() in [n.lower() for n in rat.names()]:
        raise ExecuteError(f"ERROR 000012: {field_name} already exists.")
    word = str(field_type).upper()
    kind = "Integer" if word in ("SHORT", "LONG", "BIGINTEGER") else "Double" if word in ("FLOAT", "DOUBLE") else "String"
    rat.fields.append((field_name, kind, 0))
    for row in rat.rows:
        row.append(None if kind != "String" else "")
    _write_rat(in_table, rat)
    return Result(in_table)


def GetCount(in_rows):
    """The number of rows — of a layer or table view, its selected rows, or all it shows when nothing is selected; of a
    layer joined with ``KEEP_COMMON``, only the rows that found a match."""
    if _joined_layer(in_rows) is not None:
        from . import da
        with da.SearchCursor(_text(in_rows), ["OID@"]) as rows:
            return Result(str(sum(1 for _ in rows)))
    path, where = _source(in_rows)
    connection, table = _gpkg.table_of(path)
    try:
        count = connection.execute(f'select count(*) from "{table}"' + (f" where {where}" if where else "")).fetchone()[0]
    finally:
        connection.close()
    return Result(str(count))


def CopyFeatures(in_features, out_feature_class, *_, **__):
    """The features copied to a new feature class — of a layer, its selected features; of a joined layer, its own
    fields and the join tables' (see ``_joined_columns``)."""
    from . import da
    if _joined_layer(in_features) is not None:
        return _copy_joined(in_features, _path(out_feature_class), True)
    in_features, where = _source(in_features)
    connection, table = _gpkg.table_of(in_features)
    columns, shape, srid, _ = _gpkg.describe(connection, table)
    connection.close()
    _gpkg.create(_path(out_feature_class), shape, srid, columns)
    names = [c.name for c in columns]
    with da.SearchCursor(in_features, [*names, "SHAPE@"], where) as rows, da.InsertCursor(_path(out_feature_class), [*names, "SHAPE@"]) as out:
        for row in rows:
            out.insertRow(row)
    return Result(_path(out_feature_class))


def Delete(in_data, data_type=None):
    """A feature class, a GeoPackage, a raster or other file, a folder, or a layer or table view (which removes only the
    layer, as arcpy's does). INFERRED: a raster's GDAL side files (``.aux.xml``, ``.ovr``) and world file go with it."""
    if str(in_data) in _LAYERS:
        del _LAYERS[str(in_data)]
        return Result(str(in_data))
    if ".gpkg" not in _text(in_data).lower():
        import shutil
        path = _text(in_data)
        if os.path.isdir(path):
            shutil.rmtree(path)
        elif os.path.exists(path):
            os.remove(path)
            stem, extension = os.path.splitext(path)
            side = [path + ".aux.xml", path + ".ovr"] + ([f"{stem}.{extension[1]}{extension[-1]}w"] if len(extension) > 2 else [])
            for extra in side:
                if os.path.exists(extra):
                    os.remove(extra)
        return Result(path)
    file, table = _gpkg.split(in_data)
    if table is None:
        if os.path.exists(file):
            os.remove(file)
    else:
        connection = _gpkg.connect(file)
        _drop_models(connection, table)
        connection.execute(f'drop table if exists "{table}"')
        connection.execute("delete from gpkg_contents where table_name = ?", (table,))
        connection.execute("delete from gpkg_geometry_columns where table_name = ?", (table,))
        connection.commit()
        connection.close()
    return Result(in_data)


_PYTHON = ("PYTHON3", "PYTHON", "PYTHON_9.3")


def CalculateField(in_table, field, expression, expression_type="PYTHON3", code_block="", field_type="TEXT",
                   enforce_domains="NO_ENFORCE_DOMAINS"):
    """Each row's field set to a Python expression of its other fields, written ``!name!`` as arcpy writes them. A
    field that is not there is added as ``field_type``; on a layer only the selected rows are calculated."""
    _python_only("CalculateField", expression_type)
    _calculate(in_table, field, expression, code_block, None, field_type)
    return Result(str(in_table))


def _python_only(tool, expression_type):
    if str(expression_type or "PYTHON3").upper() not in _PYTHON:
        raise NotBuiltError(f"arcpy.management.{tool}: expression_type '{expression_type}' is not built in grapy; "
                            "only PYTHON3 expressions are.")


def _calculate(in_table, field, expression, code_block, where_clause, field_type="TEXT", scope=None):
    from . import da
    if _joined_layer(in_table) is not None:
        return _calculate_joined(in_table, field, expression, code_block, where_clause, field_type, scope)
    from ._raster import _is_raster_path, _read_rat
    if _is_raster_path(in_table):
        # A raster's attribute table, read and written through da's cursor on it, as AddField adds to it.
        path, where = _text(in_table), where_clause
        rat = _read_rat(path)
        if rat is None:
            from ._runtime import ExecuteError
            raise ExecuteError(f"ERROR 000732: {path} has no attribute table; build one first (BuildRasterAttributeTable).")
        names = rat.names()
    else:
        path, where = _source(in_table)
        if where_clause:
            where = f"({where}) and ({where_clause})" if where else where_clause
        names = [c.name for c in _schema(path)[0]]
    if field.lower() not in (n.lower() for n in names):
        AddField(path, field, field_type or "TEXT")
        names.append(field)
    field = next(n for n in names if n.lower() == field.lower())
    if scope is None:
        scope = {}
        if code_block:
            exec(code_block, scope)  # noqa: S102 — the tool's own code, in the tool's own fenced process (ADR-223 §5.3)
    compiled = expression
    for name in sorted(names, key=len, reverse=True):
        compiled = compiled.replace(f"!{name}!", f"__row[{names.index(name)}]")
    with da.UpdateCursor(path, names, where) as rows:
        for row in rows:
            scope["__row"] = row
            row[names.index(field)] = eval(compiled, scope)  # noqa: S307 — as above
            rows.updateRow(row)


def Project(in_dataset, out_dataset, out_coor_system, *_, **__):
    from . import da
    from ._reference import reference_of
    target = reference_of(out_coor_system)
    connection, table = _gpkg.table_of(in_dataset)
    columns, shape, _, _ = _gpkg.describe(connection, table)
    connection.close()
    _gpkg.create(_path(out_dataset), shape, target.factoryCode, columns)
    names = [c.name for c in columns]
    with da.SearchCursor(in_dataset, [*names, "SHAPE@"]) as rows, da.InsertCursor(_path(out_dataset), [*names, "SHAPE@"]) as out:
        for row in rows:
            out.insertRow([*row[:-1], row[-1].projectAs(target) if row[-1] is not None else None])
    return Result(_path(out_dataset))


def _schema(path):
    connection, table = _gpkg.table_of(path)
    try:
        return _gpkg.describe(connection, table)
    finally:
        connection.close()


def _list(value):
    """A multivalue parameter as arcpy accepts it: a list, or a string with ``;`` between the values."""
    if value is None:
        return []
    if isinstance(value, (list, tuple)):
        return [str(v) if not isinstance(v, (list, tuple)) else v for v in value]
    return [v.strip().strip("'\"") for v in str(value).split(";") if v.strip()]


def _pairs(value):
    """A value table such as ``"pop SUM;area MEAN"`` or ``[["pop", "SUM"]]`` as a list of lists."""
    return [v if isinstance(v, (list, tuple)) else v.split() for v in _list(value)]


def _shape_kind(shape):
    """The feature class shape word for a shapely geometry's type."""
    return {"Point": "Point", "MultiPoint": "Multipoint", "LineString": "Polyline", "MultiLineString": "Polyline",
            "Polygon": "Polygon", "MultiPolygon": "Polygon"}.get(shape.geom_type, "Geometry")


def _parts(shape):
    return list(shape.geoms) if hasattr(shape, "geoms") else [shape]


_STATISTICS = {
    "SUM": sum, "MEAN": lambda v: sum(v) / len(v), "MIN": min, "MAX": max, "RANGE": lambda v: max(v) - min(v),
    "COUNT": len, "FIRST": lambda v: v[0], "LAST": lambda v: v[-1],
    "STD": lambda v: (sum((x - sum(v) / len(v)) ** 2 for x in v) / len(v)) ** 0.5,
    "MEDIAN": lambda v: sorted(v)[len(v) // 2] if len(v) % 2 else sum(sorted(v)[len(v) // 2 - 1: len(v) // 2 + 1]) / 2,
    "VARIANCE": lambda v: sum((x - sum(v) / len(v)) ** 2 for x in v) / len(v),
    "UNIQUE": lambda v: len(set(v)),
}


def _statistic(kind, values):
    """One summary statistic of a group's values, nulls left out as arcpy leaves them; a count counts what is there."""
    present = [v for v in values if v is not None]
    if kind.upper() == "COUNT":
        return len(present)
    if not present:
        return None
    return _STATISTICS[kind.upper()](present)


def _statistic_type(kind, column):
    if kind.upper() in ("COUNT", "UNIQUE"):
        return "Integer"
    if kind.upper() in ("FIRST", "LAST", "MIN", "MAX") and column is not None:
        return column.type
    return "Double"


class FieldMap:
    """arcpy.FieldMap: one output field and the input fields its values come from, merged by ``mergeRule`` when a row's
    table gives it several. INFERRED: ``First`` and ``Last`` take the first or last input field's value as it is, null
    included; ``Join`` leaves nulls out; ``StDev`` is the population form, as Dissolve's ``STD``. Text positions other
    than -1 are not built: the page does not say whether the end position is inclusive."""

    _RULES = ("FIRST", "LAST", "JOIN", "MIN", "MAX", "MEAN", "MEDIAN", "SUM", "STDEV", "COUNT")

    def __init__(self):
        self._inputs: list = []
        self.mergeRule = "First"
        self.joinDelimiter = ""
        self._output = None

    @property
    def inputFieldCount(self):
        return len(self._inputs)

    @property
    def outputField(self):
        import copy
        return copy.copy(self._output)

    @outputField.setter
    def outputField(self, field):
        import copy
        self._output = copy.copy(field)

    def addInputField(self, table_dataset, field_name, start_position=-1, end_position=-1):
        from ._parameter import Field
        table = _text(table_dataset)
        column = {c.name.lower(): c for c in _schema(_source(table)[0])[0]}.get(str(field_name).lower())
        if column is None:
            raise ValueError(f"FieldMap.addInputField: '{table}' has no field '{field_name}'.")
        self._inputs.append([table, column.name, int(start_position), int(end_position)])
        if self._output is None:
            self._output = Field(column.name, column.type, column.nullable)

    def findInputFieldIndex(self, table_dataset, field_name):
        for i, (table, field, _, _) in enumerate(self._inputs):
            if _same_table(table, table_dataset) and field.lower() == str(field_name).lower():
                return i
        return -1

    def getInputFieldName(self, index):
        return self._inputs[index][1]

    def getInputTableName(self, index):
        return self._inputs[index][0]

    def getStartTextPosition(self, index):
        return self._inputs[index][2]

    def getEndTextPosition(self, index):
        return self._inputs[index][3]

    def setStartTextPosition(self, index, start_position):
        self._inputs[index][2] = int(start_position)

    def setEndTextPosition(self, index, end_position):
        self._inputs[index][3] = int(end_position)

    def removeInputField(self, index):
        del self._inputs[index]

    def removeAll(self):
        self._inputs, self._output = [], None

    def _value(self, table, row):
        """The output value for a row of ``table``, given as {lower-case field name: value}."""
        rule = str(self.mergeRule or "First").upper()
        if rule not in self._RULES:
            raise NotBuiltError(f"FieldMap: mergeRule '{self.mergeRule}' is not built in grapy.")
        values = []
        for source, field, start, end in self._inputs:
            if not _same_table(source, table):
                continue
            if start != -1 or end != -1:
                raise NotBuiltError("FieldMap: text start and end positions are not built in grapy.")
            values.append(row.get(field.lower()))
        if not values:
            return None
        if rule == "FIRST":
            return values[0]
        if rule == "LAST":
            return values[-1]
        if rule == "JOIN":
            present = [str(v) for v in values if v is not None]
            return str(self.joinDelimiter or "").join(present) if present else None
        return _statistic({"STDEV": "STD"}.get(rule, rule), values)


class FieldMappings:
    """arcpy.FieldMappings: the output fields of Merge, Append and the like, one FieldMap each. ``addTable`` adds a
    FieldMap for each field of the table, or an input to the FieldMap already named so, as the page's discussion says
    (one FieldMap per unique field name). INFERRED: names match without regard to case, and the object ID and shape
    are not fields here. The string form (``exportToString``, ``loadFromString``) is not built: the page does not define
    it."""

    def __init__(self):
        self.fieldMappings: list = []
        self.fieldValidationWorkspace = None

    @property
    def fieldCount(self):
        return len(self.fieldMappings)

    @property
    def fields(self):
        return [m.outputField for m in self.fieldMappings]

    def addTable(self, table_dataset):
        table = _text(table_dataset)
        for column in _schema(_source(table)[0])[0]:
            index = self.findFieldMapIndex(column.name)
            if index == -1:
                field_map = FieldMap()
                field_map.addInputField(table, column.name)
                self.fieldMappings.append(field_map)
            else:
                self.fieldMappings[index].addInputField(table, column.name)

    def addFieldMap(self, field_map):
        self.fieldMappings.append(field_map)

    def findFieldMapIndex(self, field_map_name):
        for i, field_map in enumerate(self.fieldMappings):
            if field_map._output is not None and field_map._output.name.lower() == str(field_map_name).lower():
                return i
        return -1

    def getFieldMap(self, index):
        return self.fieldMappings[index]

    def removeFieldMap(self, index):
        del self.fieldMappings[index]

    def replaceFieldMap(self, index, value):
        self.fieldMappings[index] = value

    def removeAll(self):
        self.fieldMappings = []

    def exportToString(self):
        raise NotBuiltError("FieldMappings.exportToString is not built in grapy; its string form is not documented.")

    def loadFromString(self, string):
        raise NotBuiltError("FieldMappings.loadFromString is not built in grapy; its string form is not documented.")


def _same_table(a, b):
    def norm(value):
        text = _text(value)
        return text if text in _LAYERS else os.path.normcase(os.path.abspath(text))
    return norm(a) == norm(b)


def _mappings(tool, value):
    if isinstance(value, FieldMappings):
        return value
    if value is None or (isinstance(value, str) and value.strip() in ("", "#")):
        return None
    raise NotBuiltError(f"arcpy.management.{tool}: field mappings as a string are not built in grapy; pass a "
                        "FieldMappings object.")


def Merge(inputs, output, field_mappings=None, add_source="NO_SOURCE_INFO", *_, **__):
    """The features of every input in one feature class, in the first input's reference; a field missing from an input
    is null in its rows. With a FieldMappings object the output has its fields, each filled from its inputs by its
    merge rule."""
    from . import da
    from ._reference import SpatialReference
    mappings = _mappings("Merge", field_mappings)
    paths = _list(inputs)
    schemas = [_schema(_source(p)[0]) for p in paths]
    kinds = {s[1] for s in schemas}
    if len(kinds) > 1:
        from ._runtime import ExecuteError
        raise ExecuteError(f"ERROR 000366: Merge needs inputs of one shape type; these are {', '.join(sorted(kinds))}.")
    srid = schemas[0][2]
    if mappings is not None:
        columns = [_gpkg.Column(m._output.name, m._output.type) for m in mappings.fieldMappings]
    else:
        columns, seen = [], set()
        for described in schemas:
            for column in described[0]:
                if column.name.lower() not in seen:
                    seen.add(column.name.lower())
                    columns.append(column)
    source = str(add_source).upper() in ("ADD_SOURCE_INFO", "TRUE")
    if source:
        columns.append(_gpkg.Column("MERGE_SRC", "String"))
    out = _path(output)
    _gpkg.create(out, kinds.pop(), srid, columns)
    target = SpatialReference(srid) if srid > 0 else None
    names = [c.name for c in columns]
    with da.InsertCursor(out, [*names, "SHAPE@"]) as written:
        for path, (own, _, own_srid, _) in zip(paths, schemas):
            fields = [c.name for c in own]
            source_path, where = _source(path)
            with da.SearchCursor(source_path, [*fields, "SHAPE@"], where) as rows:
                for row in rows:
                    values = dict(zip((f.lower() for f in fields), row[:-1]))
                    shape = row[-1]
                    if shape is not None and target is not None and own_srid > 0 and own_srid != srid:
                        shape = shape.projectAs(target)
                    if mappings is not None:
                        record = [m._value(path, values) for m in mappings.fieldMappings] + ([None] if source else [])
                    else:
                        record = [values.get(n.lower()) for n in names]
                    if source:
                        record[-1] = str(path)
                    written.insertRow((*record, shape))
    return Result(out)


def Append(inputs, target, schema_type="TEST", field_mapping=None, subtype="", expression=None, *_, **__):
    """Each input's rows added to an existing feature class. ``TEST`` asks that every target field be in each input, as
    arcpy's does; ``NO_TEST`` fills the fields they share and leaves the rest null — or, with a FieldMappings object,
    fills each target field its FieldMap names from that FieldMap's inputs. INFERRED: a target field no FieldMap names
    is null; mappings with ``TEST`` are not built."""
    from . import da
    from ._reference import SpatialReference
    from ._runtime import ExecuteError
    mappings = _mappings("Append", field_mapping)
    if mappings is not None and str(schema_type).upper() != "NO_TEST":
        raise NotBuiltError("arcpy.management.Append: a field mapping with schema_type TEST is not built in grapy.")
    columns, _, srid, _ = _schema(target)
    names = [c.name for c in columns]
    reference = SpatialReference(srid) if srid > 0 else None
    for path in _list(inputs):
        source_path, where = _source(path)
        own, _, own_srid, _ = _schema(source_path)
        have = {c.name.lower(): c.name for c in own}
        if mappings is None and str(schema_type).upper() == "TEST" and any(n.lower() not in have for n in names):
            missing = [n for n in names if n.lower() not in have]
            raise ExecuteError(f"ERROR 000466: {path} does not match the schema of the target; it has no {', '.join(missing)}.")
        fields = [c.name for c in own] if mappings is not None else [have[n.lower()] for n in names if n.lower() in have]
        if where and expression:
            where = f"({where}) and ({expression})"
        with da.SearchCursor(source_path, [*fields, "SHAPE@"], where or expression) as rows, \
                da.InsertCursor(target, [*names, "SHAPE@"]) as written:
            for row in rows:
                values = dict(zip((f.lower() for f in fields), row[:-1]))
                shape = row[-1]
                if shape is not None and reference is not None and own_srid > 0 and own_srid != srid:
                    shape = shape.projectAs(reference)
                if mappings is not None:
                    record = []
                    for n in names:
                        index = mappings.findFieldMapIndex(n)
                        record.append(mappings.fieldMappings[index]._value(path, values) if index != -1 else None)
                else:
                    record = [values.get(n.lower()) for n in names]
                written.insertRow((*record, shape))
    return Result(target)


def DeleteField(in_table, drop_field, method="DELETE_FIELDS"):
    """Fields dropped — or, with ``KEEP_FIELDS``, every field but those named. On a raster, from its attribute table;
    INFERRED: its ``Value`` field stays, as the table's key, and ``KEEP_FIELDS`` keeps it too."""
    from ._raster import _is_raster_path
    if _is_raster_path(in_table):
        return _delete_rat_fields(in_table, drop_field, method)
    from . import _attachments, _subtypes
    from ._runtime import ExecuteError
    connection, table = _gpkg.table_of(in_table)
    try:
        columns = [c.name for c in _gpkg.describe(connection, table)[0]]
        named = {n.lower() for n in _list(drop_field)}
        drop = [c for c in columns if (c.lower() in named) != (str(method).upper() == "KEEP_FIELDS")]
        # INFERRED (ADR-228): the subtype field, and the GlobalID field of a class with attachments, cannot be deleted —
        # clear the subtype field or disable attachments first; a field that is deleted takes its defaults with it.
        subtype = _subtypes.field(connection, table)
        keyed = _attachments.global_id_field(connection, table) if _attachments.enabled(connection, table) else None
        for name in drop:
            if (subtype and name.lower() == subtype[0].lower()) or (keyed and name.lower() == keyed.lower()):
                raise ExecuteError(f"ERROR 000800: '{name}' is the subtype field or the attachments' GlobalID field and "
                                   "cannot be deleted.")
        for name in drop:
            connection.execute(f'alter table "{table}" drop column "{name}"')
            _subtypes.drop_field(connection, table, name)
            if _attachments.global_id_field(connection, table) == name:
                connection.execute("delete from grapy_global_id where table_name = ?", (table,))
        connection.commit()
    finally:
        connection.close()
    return Result(in_table)


def _delete_rat_fields(in_table, drop_field, method):
    from ._raster import _read_rat, _write_rat
    from ._runtime import ExecuteError
    rat = _read_rat(_text(in_table))
    if rat is None:
        raise ExecuteError(f"ERROR 000732: {in_table} has no attribute table.")
    named = {n.lower() for n in _list(drop_field)}
    keep = str(method).upper() == "KEEP_FIELDS"
    if not keep and "value" in named:
        raise ExecuteError("ERROR 000800: the Value field is the attribute table's key and cannot be deleted.")
    wanted = [i for i, (name, _, _) in enumerate(rat.fields)
              if name.lower() == "value" or ((name.lower() in named) == keep)]
    rat.fields = [rat.fields[i] for i in wanted]
    rat.rows = [[row[i] for i in wanted] for row in rat.rows]
    _write_rat(_text(in_table), rat)
    return Result(in_table)


def AddFields(in_table, field_description, *_, **__):
    """Several fields at once, each ``[name, type, alias, length, default, domain]`` as the tool's value table has them."""
    for described in _pairs(field_description):
        AddField(in_table, described[0], described[1] if len(described) > 1 else "TEXT")
    return Result(in_table)


def DeleteFeatures(in_features):
    """Every row deleted — of a layer, only its selected rows; through a composite relationship class, their
    destination rows too (ADR-225)."""
    from . import _relationships
    path, where = _source(in_features)
    connection, table = _gpkg.table_of(path)
    try:
        oid = _gpkg.oid_column(connection, table)
        doomed = [r[0] for r in connection.execute(f'select "{oid}" from "{table}"' + (f" where {where}" if where else ""))]
        _relationships.cascade(connection, table, doomed)
        connection.execute(f'delete from "{table}"' + (f" where {where}" if where else ""))
        connection.commit()
    finally:
        connection.close()
    return Result(in_features)


DeleteRows = DeleteFeatures


def Rename(in_data, out_data, data_type=None):
    """A feature class renamed: a GeoPackage file whose single table takes the new name with it, or a table within one."""
    file, table = _gpkg.split(in_data)
    out_file, out_table = _gpkg.split(_path(out_data))
    connection, table = _gpkg.table_of(in_data)
    new = out_table or os.path.splitext(os.path.basename(out_file))[0]
    try:
        from . import _attachments, _subtypes
        connection.execute(f'alter table "{table}" rename to "{new}"')
        for meta in ("gpkg_contents", "gpkg_geometry_columns"):
            connection.execute(f"update {meta} set table_name = ? where table_name = ?", (new, table))
        connection.execute("update gpkg_contents set identifier = ? where table_name = ?", (new, new))
        # A class's attachments, global IDs and subtypes keep with it under its new name (ADR-228).
        _attachments.rename(connection, table, new)
        _subtypes.rename(connection, table, new)
        connection.commit()
    finally:
        connection.close()
    if os.path.abspath(out_file) != os.path.abspath(file):
        os.replace(file, out_file)
    return Result(_path(out_data))


def RepairGeometry(in_features, delete_null="DELETE_NULL", validation_method="ESRI"):
    """Each invalid shape made valid; empty ones deleted, as ``DELETE_NULL`` asks."""
    import shapely
    from . import da
    with da.UpdateCursor(in_features, ["SHAPE@"]) as rows:
        for row in rows:
            shape = row[0]._shape if row[0] is not None else None
            if shape is None or shape.is_empty:
                if str(delete_null).upper() == "DELETE_NULL":
                    rows.deleteRow()
                continue
            if not shape.is_valid:
                rows.updateRow([shapely.make_valid(shape)])
    return Result(in_features)


def Dissolve(in_features, out_feature_class, dissolve_field=None, statistics_fields=None, multi_part="MULTI_PART",
             unsplit_lines="DISSOLVE_LINES", *_, **__):
    """One feature per distinct value of the dissolve fields, its shape their union and its statistics fields named
    ``SUM_field`` as arcpy names them."""
    from shapely import union_all
    from . import da
    columns, shape_kind, srid, _ = _schema(in_features)
    by_name = {c.name.lower(): c for c in columns}
    fields = [by_name[f.lower()].name for f in _list(dissolve_field)]
    statistics = [(f, s.upper()) for f, s in _pairs(statistics_fields)]
    groups: dict = {}
    with da.SearchCursor(in_features, [*fields, *[f for f, _ in statistics], "SHAPE@"]) as rows:
        for row in rows:
            key = tuple(row[: len(fields)])
            group = groups.setdefault(key, ([], [[] for _ in statistics]))
            if row[-1] is not None:
                group[0].append(row[-1]._shape)
            for i, value in enumerate(row[len(fields):-1]):
                group[1][i].append(value)
    out_columns = [by_name[f.lower()] for f in fields] + [
        _gpkg.Column(f"{s}_{f}", _statistic_type(s, by_name.get(f.lower()))) for f, s in statistics]
    out = _path(out_feature_class)
    _gpkg.create(out, shape_kind, srid, out_columns)
    with da.InsertCursor(out, [*[c.name for c in out_columns], "SHAPE@"]) as written:
        for key, (shapes, values) in groups.items():
            merged = union_all(shapes) if shapes else None
            if shape_kind == "Polyline" and merged is not None and str(unsplit_lines).upper() == "UNSPLIT_LINES":
                from shapely.ops import linemerge
                merged = linemerge(merged) if merged.geom_type == "MultiLineString" else merged
            summary = [_statistic(s, v) for (_, s), v in zip(statistics, values)]
            pieces = _parts(merged) if merged is not None and str(multi_part).upper() == "SINGLE_PART" else [merged]
            for piece in pieces:
                written.insertRow((*key, *summary, piece))
    return Result(out)


def MultipartToSinglepart(in_features, out_feature_class):
    """One feature per part, each keeping its attributes and the ``ORIG_FID`` of the feature it came from."""
    from . import da
    columns, shape_kind, srid, _ = _schema(in_features)
    names = [c.name for c in columns]
    out = _path(out_feature_class)
    kind = {"Multipoint": "Point"}.get(shape_kind, shape_kind)
    _gpkg.create(out, kind, srid, [*columns, _gpkg.Column("ORIG_FID", "Integer")])
    with da.SearchCursor(in_features, ["OID@", *names, "SHAPE@"]) as rows, \
            da.InsertCursor(out, [*names, "ORIG_FID", "SHAPE@"]) as written:
        for row in rows:
            for part in (_parts(row[-1]._shape) if row[-1] is not None else [None]):
                written.insertRow((*row[1:-1], row[0], part))
    return Result(out)


def FeatureToPoint(in_features, out_feature_class, point_location="CENTROID"):
    """A point per feature: its centroid, or with ``INSIDE`` a point guaranteed within it."""
    from . import da
    columns, _, srid, _ = _schema(in_features)
    names = [c.name for c in columns]
    out = _path(out_feature_class)
    _gpkg.create(out, "Point", srid, [*columns, _gpkg.Column("ORIG_FID", "Integer")])
    inside = str(point_location).upper() == "INSIDE"
    with da.SearchCursor(in_features, ["OID@", *names, "SHAPE@"]) as rows, \
            da.InsertCursor(out, [*names, "ORIG_FID", "SHAPE@"]) as written:
        for row in rows:
            shape = row[-1]._shape if row[-1] is not None else None
            point = None if shape is None else (shape.representative_point() if inside else shape.centroid)
            written.insertRow((*row[1:-1], row[0], point))
    return Result(out)


def FeatureVerticesToPoints(in_features, out_feature_class, point_location="ALL"):
    """A point for each vertex — or for each part's ``START``, ``END``, ``BOTH_ENDS`` or ``MID`` point."""
    from shapely.geometry import Point as ShapelyPoint
    from . import da
    columns, _, srid, _ = _schema(in_features)
    names = [c.name for c in columns]
    out = _path(out_feature_class)
    _gpkg.create(out, "Point", srid, [*columns, _gpkg.Column("ORIG_FID", "Integer")])
    where = str(point_location).upper()
    with da.SearchCursor(in_features, ["OID@", *names, "SHAPE@"]) as rows, \
            da.InsertCursor(out, [*names, "ORIG_FID", "SHAPE@"]) as written:
        for row in rows:
            if row[-1] is None:
                continue
            for part in _parts(row[-1]._shape):
                line = part.exterior if part.geom_type == "Polygon" else part
                coordinates = list(line.coords)
                if where == "MID":
                    chosen = [line.interpolate(0.5, normalized=True).coords[0]]
                elif where == "START":
                    chosen = coordinates[:1]
                elif where == "END":
                    chosen = coordinates[-1:]
                elif where == "BOTH_ENDS":
                    chosen = [coordinates[0], coordinates[-1]]
                else:
                    chosen = coordinates[:-1] if part.geom_type == "Polygon" else coordinates
                for xy in chosen:
                    written.insertRow((*row[1:-1], row[0], ShapelyPoint(xy[:2])))
    return Result(out)


def AddXY(in_features):
    """``POINT_X`` and ``POINT_Y`` added to point features and filled from each point."""
    from . import da
    existing = {c.name.lower() for c in _schema(in_features)[0]}
    for name in ("POINT_X", "POINT_Y"):
        if name.lower() not in existing:
            _gpkg.add_column(in_features, _gpkg.Column(name, "Double"))
    with da.UpdateCursor(in_features, ["POINT_X", "POINT_Y", "SHAPE@"]) as rows:
        for row in rows:
            point = row[2]._shape.centroid if row[2] is not None else None
            rows.updateRow([point.x if point else None, point.y if point else None, row[2]])
    return Result(in_features)


_PROPERTIES = {
    "AREA": lambda s: s.area, "PERIMETER_LENGTH": lambda s: s.length, "LENGTH": lambda s: s.length,
    "CENTROID_X": lambda s: s.centroid.x, "CENTROID_Y": lambda s: s.centroid.y,
    "INSIDE_X": lambda s: s.representative_point().x, "INSIDE_Y": lambda s: s.representative_point().y,
    "POINT_X": lambda s: s.centroid.x, "POINT_Y": lambda s: s.centroid.y,
    "PART_COUNT": lambda s: len(_parts(s)), "POINT_COUNT": lambda s: len(_coordinates(s)),
    "EXTENT_MIN_X": lambda s: s.bounds[0], "EXTENT_MIN_Y": lambda s: s.bounds[1],
    "EXTENT_MAX_X": lambda s: s.bounds[2], "EXTENT_MAX_Y": lambda s: s.bounds[3],
    "LINE_START_X": lambda s: _coordinates(s)[0][0], "LINE_START_Y": lambda s: _coordinates(s)[0][1],
    "LINE_END_X": lambda s: _coordinates(s)[-1][0], "LINE_END_Y": lambda s: _coordinates(s)[-1][1],
}


def _coordinates(shape):
    import shapely
    return shapely.get_coordinates(shape).tolist()


def CalculateGeometryAttributes(in_features, geometry_property, length_unit="", area_unit="", coordinate_system=None,
                                coordinate_format="SAME_AS_INPUT"):
    """Fields filled with each shape's properties, ``[[field, property], …]``. Lengths and areas are planar, in the data's
    units or the unit named; a geographic reference must be projected first, as arcpy's planar measures need. The units
    are sized as Pro measures them (D-323): ``YARDS`` and ``NAUTICAL_MILES`` are US survey, ``ACRES``, ``SQUARE_YARDS``
    and ``SQUARE_NAUTICAL_MILES`` international."""
    from . import da
    from ._reference import SpatialReference
    pairs = [(f, p.upper()) for f, p in _pairs(geometry_property)]
    _, _, srid, _ = _schema(in_features)
    existing = {c.name.lower() for c in _schema(in_features)[0]}
    for field, prop in pairs:
        if field.lower() not in existing:
            _gpkg.add_column(in_features, _gpkg.Column(field, "Integer" if prop.endswith("_COUNT") else "Double"))
    metres = SpatialReference(srid).metersPerUnit if srid > 0 else 1.0
    linear = _unit_scale(length_unit, metres, _units.CALCULATE_LENGTH, "length_unit")
    square = _unit_scale(area_unit, metres ** 2, _units.CALCULATE_AREA, "area_unit")
    with da.UpdateCursor(in_features, [*[f for f, _ in pairs], "SHAPE@"]) as rows:
        for row in rows:
            shape = row[-1]._shape if row[-1] is not None else None
            values = []
            for _, prop in pairs:
                value = None if shape is None or shape.is_empty else _PROPERTIES[prop](shape)
                if value is not None and prop == "AREA":
                    value *= square
                elif value is not None and prop in ("PERIMETER_LENGTH", "LENGTH"):
                    value *= linear
                values.append(value)
            rows.updateRow([*values, row[-1]])
    return Result(in_features)


def _unit_scale(unit, metres_per_unit, table, parameter):
    """The factor taking a measure in the data's units (``metres_per_unit`` of them, or square metres) to the unit named
    from the tool's ``table``; 1 when none is."""
    if not _given(unit):
        return 1.0
    key = str(unit).upper()
    if key not in table:
        from ._runtime import ExecuteError
        raise ExecuteError(f"ERROR 000800: The value '{unit}' of {parameter} is not a member of {', '.join(table)}.")
    return metres_per_unit / table[key]


def _rectangle_by(shape, by_width):
    """The enclosing rectangle of smallest area, or of smallest width (its shorter side), as a polygon and its two side
    vectors. One side of either lies along an edge of the convex hull, so each hull edge's direction is tried. INFERRED:
    of rectangles equally narrow, the smaller in area."""
    import math
    import shapely
    hull = shape.convex_hull
    if hull.geom_type != "Polygon":
        ends = list(hull.coords) if hull.geom_type == "LineString" else []
        side = (ends[-1][0] - ends[0][0], ends[-1][1] - ends[0][1]) if ends else (0.0, 0.0)
        return shapely.minimum_rotated_rectangle(shape), side, (0.0, 0.0)
    points = list(hull.exterior.coords)[:-1]
    best = None
    for (ax, ay), (bx, by) in zip(points, points[1:] + points[:1]):
        length = math.hypot(bx - ax, by - ay)
        if length == 0:
            continue
        ux, uy = (bx - ax) / length, (by - ay) / length
        along = [x * ux + y * uy for x, y in points]
        across = [-x * uy + y * ux for x, y in points]
        a0, a1, c0, c1 = min(along), max(along), min(across), max(across)
        size_u, size_v = a1 - a0, c1 - c0
        key = (min(size_u, size_v), size_u * size_v) if by_width else (size_u * size_v, min(size_u, size_v))
        if best is None or key < best[0]:
            corners = [(a * ux - c * uy, a * uy + c * ux) for a, c in ((a0, c0), (a1, c0), (a1, c1), (a0, c1))]
            best = (key, corners, (size_u * ux, size_u * uy), (-size_v * uy, size_v * ux))
    _, corners, side_u, side_v = best
    return shapely.Polygon(corners), side_u, side_v


def _orientation(vector):
    """Degrees clockwise from north of a side. INFERRED: between 0 and 180, a side having two ends."""
    import math
    angle = math.degrees(math.atan2(vector[0], vector[1])) % 180.0
    return 0.0 if math.isclose(angle, 180.0) else angle


def MinimumBoundingGeometry(in_features, out_feature_class, geometry_type="RECTANGLE_BY_AREA", group_option="NONE",
                            group_field=None, mbg_fields_option="NO_MBG_FIELDS"):
    """The envelope, convex hull, rectangle of smallest area or of smallest width, or smallest circle around each
    feature, around all of them (``ALL``) or around each group of a ``LIST`` field's values. ``MBG_FIELDS`` adds, as
    documented, ``MBG_Width`` and ``MBG_Length`` (the shorter and longer side) and for rectangles ``MBG_Orientation`` (the
    longer side, degrees clockwise from north), or ``MBG_Diameter`` for a circle. The convex hull's fields are not
    built: the page does not say which of an antipodal pair is the first."""
    import math
    import shapely
    from . import da
    columns, _, srid, _ = _schema(_source(in_features)[0])
    group = str(group_option or "NONE").upper()
    fields = [f for f in _list(group_field)] if group == "LIST" else []
    by_name = {c.name.lower(): c for c in columns}
    kind = str(geometry_type or "RECTANGLE_BY_AREA").upper()
    if kind not in ("ENVELOPE", "CONVEX_HULL", "RECTANGLE_BY_AREA", "RECTANGLE_BY_WIDTH", "CIRCLE"):
        raise NotBuiltError(f"arcpy.management.MinimumBoundingGeometry: geometry_type '{kind}' is not built in grapy.")
    with_fields = str(mbg_fields_option or "NO_MBG_FIELDS").upper() in ("MBG_FIELDS", "ADD_ATTRIBUTES")
    if with_fields and kind == "CONVEX_HULL":
        raise NotBuiltError("arcpy.management.MinimumBoundingGeometry: MBG_FIELDS of a convex hull is not built in grapy.")
    extra = []
    if with_fields:
        extra = ["MBG_Diameter"] if kind == "CIRCLE" else (
            ["MBG_Width", "MBG_Length"] + (["MBG_Orientation"] if kind.startswith("RECTANGLE") else []))

    def bound(shape):
        if kind == "ENVELOPE":
            x0, y0, x1, y1 = shape.bounds
            return shapely.envelope(shape), [min(x1 - x0, y1 - y0), max(x1 - x0, y1 - y0)]
        if kind == "CONVEX_HULL":
            return shape.convex_hull, []
        if kind == "CIRCLE":
            return shapely.minimum_bounding_circle(shape), [2 * shapely.minimum_bounding_radius(shape)]
        rectangle, u, v = _rectangle_by(shape, kind == "RECTANGLE_BY_WIDTH")
        lu, lv = math.hypot(*u), math.hypot(*v)
        longer = u if lu >= lv else v
        return rectangle, [min(lu, lv), max(lu, lv), _orientation(longer)]

    out = _path(out_feature_class)
    lead = [by_name[f.lower()] for f in fields] if group != "NONE" else [*columns, _gpkg.Column("ORIG_FID", "Integer")]
    out_columns = lead + [_gpkg.Column(n, "Double") for n in extra]
    _gpkg.create(out, "Polygon", srid, out_columns)
    names = [c.name for c in columns]
    read = [r for r in _features(in_features) if r[2] is not None]
    with da.InsertCursor(out, [*[c.name for c in out_columns], "SHAPE@"]) as written:
        if group == "NONE":
            for oid, values, shape in read:
                made, measures = bound(shape)
                written.insertRow((*values, oid, *(measures if with_fields else []), made))
        else:
            groups: dict = {}
            for oid, values, shape in read:
                key = tuple(values[names.index(by_name[f.lower()].name)] for f in fields)
                groups.setdefault(key, []).append(shape)
            for key, shapes in groups.items():
                made, measures = bound(shapely.GeometryCollection(shapes))
                written.insertRow((*key, *(measures if with_fields else []), made))
    return Result(out)


def CalculateEndTime(in_table, start_field, end_field, fields=None):
    """Each row's end time set to the start time of the next row, the rows taken in order of the ID ``fields`` and then
    the start time; the last row's end time is its own start, as documented. INFERRED: with ID fields, rows of one
    entity look only to that entity's next row, and each entity's last row ends at its own start; rows starting at the
    same time keep their object ID order; a null start time is left out."""
    from . import da
    path = _plain("CalculateEndTime", in_table)
    columns = {c.name.lower(): c.name for c in _schema(path)[0]}
    start, end = columns[str(start_field).lower()], columns[str(end_field).lower()]
    ids = [columns[f.lower()] for f in _list(fields)]
    with da.SearchCursor(path, ["OID@", *ids, start]) as rows:
        read = [r for r in rows if r[-1] is not None]
    read.sort(key=lambda r: (tuple((v is not None, v) for v in r[1:-1]), r[-1], r[0]))
    ends = {}
    for here, after in zip(read, read[1:] + [None]):
        same = after is not None and tuple(after[1:-1]) == tuple(here[1:-1])
        ends[here[0]] = after[-1] if same else here[-1]
    with da.UpdateCursor(path, ["OID@", end]) as rows:
        for row in rows:
            if row[0] in ends:
                rows.updateRow([row[0], ends[row[0]]])
    return Result(_text(in_table))


def TransposeFields(in_table, in_field, out_table, in_transposed_field_name, in_value_field_name, attribute_fields=None):
    """Fields turned into rows: for each input row and each field of ``in_field`` (``"field name;…"`` — the name
    written in the transposed field, the field's own when none is given), a row holding that name and the field's value
    as text, with the ``attribute_fields`` kept; ``Shape`` among them makes a feature class, as documented. INFERRED:
    rows go input row by input row, each in the order of ``in_field``; a null value stays null; a number is written as
    Python writes it."""
    from . import da
    from ._runtime import ExecuteError
    path, where = _source(in_table)
    columns, kind, srid, geometry = _schema(path)
    by_name = {c.name.lower(): c for c in columns}
    pairs = [(by_name[p[0].lower()].name, p[1] if len(p) > 1 else by_name[p[0].lower()].name) for p in _pairs(in_field)]
    kept, shape = [], False
    for name in _list(attribute_fields):
        if name.lower() in ("shape", geometry.lower()):
            shape = True
        else:
            kept.append(by_name[name.lower()])
    if str(in_value_field_name).lower() in by_name:
        raise ExecuteError(f"ERROR 000728: the value field '{in_value_field_name}' is already a field of the input.")
    out_columns = [*kept, _gpkg.Column(str(in_transposed_field_name), "String"), _gpkg.Column(str(in_value_field_name), "String")]
    out = _table_out("TransposeFields", out_table)
    _gpkg.create(out, kind if shape else "Geometry", srid if shape else 0, out_columns)
    with da.SearchCursor(path, [*[c.name for c in kept], *[f for f, _ in pairs], "SHAPE@"], where) as rows, \
            da.InsertCursor(out, [*[c.name for c in out_columns], "SHAPE@"]) as written:
        for row in rows:
            values = row[len(kept):-1]
            for (_, label), value in zip(pairs, values):
                written.insertRow((*row[:len(kept)], label, None if value is None else str(value), row[-1] if shape else None))
    return Result(out)


# ---- layers and table views ------------------------------------------------------------------------------------------
#
# arcpy's layer is a name over a dataset, with a definition query and a selection, that lives as long as the session.
# The registry is ``_layers``, which ``_gpkg.table_of`` and the ``da`` cursors read, so every module's tools honour a
# layer's query and selection — not only this module's.

from ._layers import LAYERS as _LAYERS  # noqa: E402
from ._layers import Layer as _Layer  # noqa: E402
from ._layers import text as _text  # noqa: E402


def _given(value):
    return value is not None and str(value).strip() not in ("", "#")


def _oid_of(path):
    connection, table = _gpkg.table_of(path)
    try:
        return _gpkg.oid_column(connection, table)
    finally:
        connection.close()


def _source(value):
    """The dataset and the where clause a value names: a path as it is, or a layer's source with its definition query
    and selection. A joined layer is refused: a tool reading its dataset this way would leave the join's fields and
    its KEEP_COMMON out without a word."""
    layer = _LAYERS.get(_text(value))
    if layer is None:
        return _text(value), None
    if layer.joins:
        raise NotBuiltError(f"This tool on the joined layer '{layer.name}' is not built in grapy; copy the layer first "
                            "(CopyFeatures or CopyRows keep the joined fields), or remove the join.")
    clauses = [f"({layer.where})"] if layer.where else []
    if layer.selection:
        clauses.append(f'"{_oid_of(layer.source)}" in ({", ".join(str(int(i)) for i in sorted(layer.selection))})')
    return layer.source, " and ".join(clauses) or None


def _plain(tool, value):
    """A dataset path; a layer only when it neither filters nor selects, since the tool works on the whole dataset."""
    layer = _LAYERS.get(_text(value))
    if layer is None:
        return _text(value)
    if layer.where or layer.selection or layer.joins:
        raise NotBuiltError(f"arcpy.management.{tool} on a layer with a selection or definition query is not built in "
                            "grapy; pass the dataset or copy the selection first.")
    return layer.source


def _make_layer(tool, in_features, out_layer, where_clause, field_info, kind):
    from ._runtime import ExecuteError
    if _given(field_info):
        raise NotBuiltError(f"arcpy.management.{tool}: field_info is not built in grapy; a layer shows every field.")
    name = _text(out_layer)
    if ".gpkg" in name.lower():
        raise ExecuteError(f"ERROR 000725: a layer's name may not contain '.gpkg' in grapy: {name}.")
    parent = _LAYERS.get(_text(in_features))
    joins = []
    if parent is not None:
        source, where, selection, joins = parent.source, parent.where, set(parent.selection), list(parent.joins)
    else:
        source, where, selection = _text(in_features), None, set()
        _schema(source)
    if _given(where_clause):
        where = f"({where}) and ({where_clause})" if where else str(where_clause)
    layer = _Layer(name, source, where, kind)
    layer.selection = selection
    layer.joins = joins
    _LAYERS[name] = layer
    return Result(name)


def MakeFeatureLayer(in_features, out_layer, where_clause=None, workspace=None, field_info=None):
    """A layer over a feature class or layer, showing the rows of ``where_clause`` — combined with ``AND`` with the
    input layer's own query, whose selection it carries. ``workspace`` is unused in arcpy too."""
    return _make_layer("MakeFeatureLayer", in_features, out_layer, where_clause, field_info, "FeatureLayer")


def MakeTableView(in_table, out_view, where_clause=None, workspace=None, field_info=None):
    """A table view: a layer over a table's rows, as MakeFeatureLayer is over features."""
    return _make_layer("MakeTableView", in_table, out_view, where_clause, field_info, "TableView")


def _as_layer(value):
    """The layer a value names; for a dataset path, a new layer, as arcpy's selection tools make one. INFERRED: it is
    named after the dataset's table, with ``_1``, ``_2`` … when that name is taken by a layer over something else."""
    layer = _LAYERS.get(_text(value))
    if layer is not None:
        return layer
    path = _text(value)
    connection, table = _gpkg.table_of(path)
    connection.close()
    name, i = table, 0
    while name in _LAYERS and _LAYERS[name].source != path:
        i += 1
        name = f"{table}_{i}"
    _make_layer("SelectLayerByAttribute", path, name, None, None, "FeatureLayer")
    return _LAYERS[name]


def _eligible(layer, where_clause=None):
    """The OIDs a layer shows (its definition query), within ``where_clause`` when one is given."""
    from . import _layers
    clauses = [f"({c})" for c in (layer.where, where_clause) if _given(c)]
    connection, table = _gpkg.table_of(layer.source)
    try:
        oid = _gpkg.oid_column(connection, table)
        if layer.joins:
            joined = _layers.joined(layer, connection, table, oid, _gpkg.describe(connection, table)[3])
            sql = f"select {joined.oid_sql} from {joined.from_sql}"
        else:
            sql = f'select "{oid}" from "{table}"'
        sql += " where " + " and ".join(clauses) if clauses else ""
        return {r[0] for r in connection.execute(sql)}
    finally:
        connection.close()


def _combine(kind, current, matched, tool):
    if kind == "NEW_SELECTION":
        return set(matched)
    if kind == "ADD_TO_SELECTION":
        return current | matched
    if kind == "REMOVE_FROM_SELECTION":
        return current - matched
    if kind == "SUBSET_SELECTION":
        return current & matched
    raise NotBuiltError(f"arcpy.management.{tool}: selection_type '{kind}' is not built in grapy.")


def SelectLayerByAttribute(in_layer_or_view, selection_type="NEW_SELECTION", where_clause=None, invert_where_clause=None):
    """The rows of ``where_clause`` selected, combined with the current selection as ``selection_type`` says. Outputs
    are the layer and the number of rows now selected. A dataset path is given a new layer, as arcpy does."""
    layer = _as_layer(in_layer_or_view)
    kind = str(selection_type or "NEW_SELECTION").upper()
    if kind == "CLEAR_SELECTION":
        layer.selection = set()
    elif kind == "SWITCH_SELECTION":
        layer.selection = _eligible(layer) - layer.selection
    else:
        matched = _eligible(layer, where_clause)
        if str(invert_where_clause or "NON_INVERT").upper() == "INVERT":
            matched = _eligible(layer) - matched
        layer.selection = _combine(kind, layer.selection, matched, "SelectLayerByAttribute")
    return Result(layer.name, str(len(layer.selection)))


def _metres(unit, tool, table=None):
    """Metres in ``unit``: a word of a linear unit (``"Feet"`` of ``"20 Feet"``, a US survey foot), or one of a tool's own
    keywords from its ``table`` in _units — sized as Pro measures them (D-323)."""
    if table is not None:
        key = str(unit).upper().replace(" ", "")
        if key not in table:
            raise NotBuiltError(f"arcpy.management.{tool}: the linear unit '{unit}' is not built in grapy.")
        return table[key]
    metres = _units.linear(unit)
    if metres is None:
        raise NotBuiltError(f"arcpy.management.{tool}: the linear unit '{unit}' is not built in grapy.")
    return metres


def _per_unit(srid):
    from ._reference import SpatialReference
    if srid <= 0:
        return None
    reference = SpatialReference(srid)
    return None if reference.type == "Geographic" else reference.metersPerUnit


def _linear(value, srid, tool):
    """A linear unit such as ``"20 Meters"`` in the data's units; a bare number is in the data's units already."""
    if isinstance(value, (int, float)):
        return float(value)
    parts = str(value).split()
    if len(parts) == 1 or parts[1].upper() == "UNKNOWN":
        return float(parts[0])
    per = _per_unit(srid)
    if per is None:
        raise NotBuiltError(f"arcpy.management.{tool}: a distance in {parts[1]} on data with a geographic or unknown "
                            "reference is not built in grapy; project the data first.")
    return float(parts[0]) * _metres(parts[1], tool) / per


def _shapes(value, srid):
    """(oid, shapely shape) of each feature a value shows — a layer's selection — in ``srid``."""
    from . import da
    from ._reference import SpatialReference
    path, where = _source(value)
    own = _schema(path)[2]
    target = SpatialReference(srid) if srid > 0 and own > 0 and own != srid else None
    with da.SearchCursor(path, ["OID@", "SHAPE@"], where) as rows:
        return [(r[0], (r[1].projectAs(target) if target else r[1])._shape) for r in rows if r[1] is not None]


def _linework(shape):
    return shape.boundary if shape.geom_type in ("Polygon", "MultiPolygon") else shape


def _center(shape):
    """HAVE_THEIR_CENTER_IN's centre, as documented: a line's midpoint, any other shape's centroid."""
    if shape.geom_type in ("LineString", "MultiLineString"):
        return shape.interpolate(0.5, normalized=True)
    return shape.centroid


def SelectLayerByLocation(in_layer, overlap_type="INTERSECT", select_features=None, search_distance=None,
                          selection_type="NEW_SELECTION", invert_spatial_relationship="NOT_INVERT"):
    """The features of a layer that stand in ``overlap_type`` to any of ``select_features`` (a layer's selection, or
    every feature of a dataset) selected, and combined with the current selection.

    INFERRED, from the documented boundary cases: ``CONTAINS`` and ``WITHIN`` count a shape lying on the other's
    boundary (shapely's *covers* and *covered by*); the ``_CLEMENTINI`` forms do not (*contains*, *within*);
    ``COMPLETELY_`` forms keep the boundaries apart (*contains properly*). ``INTERSECT_DBMS`` answers as ``INTERSECT``.
    A search distance is inclusive. ``BOUNDARY_TOUCHES``, ``CROSSED_BY_THE_OUTLINE_OF``, the 3D and the geodesic forms
    are not built."""
    from shapely.strtree import STRtree
    from ._runtime import ExecuteError
    if isinstance(in_layer, (list, tuple)) or ";" in _text(in_layer):
        raise NotBuiltError("arcpy.management.SelectLayerByLocation over several layers is not built in grapy.")
    layer = _as_layer(in_layer)
    kind = str(selection_type or "NEW_SELECTION").upper()
    if kind == "SWITCH_SELECTION":
        layer.selection = _eligible(layer) - layer.selection
        return Result(layer.name, str(len(layer.selection)))
    if kind == "CLEAR_SELECTION":
        layer.selection = set()
        return Result(layer.name, "0")
    relation = str(overlap_type or "INTERSECT").upper()
    if select_features is None:
        raise ExecuteError("ERROR 000735: SelectLayerByLocation needs select_features.")
    srid = _schema(layer.source)[2]
    distance = _linear(search_distance, srid, "SelectLayerByLocation") if _given(search_distance) else None
    if distance is not None and relation not in ("INTERSECT", "INTERSECT_DBMS", "WITHIN_A_DISTANCE"):
        raise NotBuiltError(f"arcpy.management.SelectLayerByLocation: a search distance with {relation} is not built in grapy.")
    selecting = [s for _, s in _shapes(select_features, srid)]
    tree = STRtree(selecting) if selecting else None
    eligible = _eligible(layer)
    candidates = [(oid, shape) for oid, shape in _shapes(layer.source, srid) if oid in eligible]

    def hits(shape, predicate, **extra):
        return [selecting[int(i)] for i in tree.query(shape, predicate=predicate, **extra)] if tree is not None else []

    def related(shape):
        if relation in ("INTERSECT", "INTERSECT_DBMS"):
            return bool(hits(shape, "dwithin", distance=distance) if distance else hits(shape, "intersects"))
        if relation == "WITHIN_A_DISTANCE":
            if distance is None:
                raise ExecuteError("ERROR 000735: WITHIN_A_DISTANCE needs a search distance.")
            return bool(hits(shape, "dwithin", distance=distance))
        if relation in _PREDICATES:
            return bool(hits(shape, _PREDICATES[relation]))
        if relation == "COMPLETELY_WITHIN":
            return any(other.contains_properly(shape) for other in hits(shape, "intersects"))
        if relation == "ARE_IDENTICAL_TO":
            return any(other.equals(shape) for other in hits(shape, "intersects"))
        if relation == "SHARE_A_LINE_SEGMENT_WITH":
            if shape.geom_type in ("Point", "MultiPoint"):
                return False
            return any(_linework(other).intersection(_linework(shape)).length > 0 for other in hits(shape, "intersects")
                       if other.geom_type not in ("Point", "MultiPoint"))
        if relation == "HAVE_THEIR_CENTER_IN":
            return bool(hits(_center(shape), "covered_by"))
        raise NotBuiltError(f"arcpy.management.SelectLayerByLocation: overlap_type '{relation}' is not built in grapy.")

    matched = {oid for oid, shape in candidates if related(shape)}
    if str(invert_spatial_relationship or "NOT_INVERT").upper() == "INVERT":
        matched = eligible - matched
    layer.selection = _combine(kind, layer.selection, matched, "SelectLayerByLocation")
    return Result(layer.name, str(len(layer.selection)))


_PREDICATES = {"CONTAINS": "covers", "CONTAINS_CLEMENTINI": "contains", "COMPLETELY_CONTAINS": "contains_properly",
               "WITHIN": "covered_by", "WITHIN_CLEMENTINI": "within"}


# ---- tables -------------------------------------------------------------------------------------------------------------
#
# A table is a GeoPackage feature table with no shapes, as analysis.Statistics writes one: it is how a table crosses
# back to the server (ADR-223 §5.5).

def _table_out(tool, out_table):
    if str(out_table).lower().endswith((".csv", ".txt", ".dbf")):
        raise NotBuiltError(f"arcpy.management.{tool}: writing {os.path.splitext(str(out_table))[1]} is not built in "
                            "grapy; write a GeoPackage table.")
    return _path(out_table)


# ---- joins --------------------------------------------------------------------------------------------------------------

def _joined_layer(value):
    layer = _LAYERS.get(_text(value))
    return layer if layer is not None and layer.joins else None


def _joined_fields(layer):
    """The qualified fields a joined layer shows, (name, type, is the layer's own), read as a cursor reads them."""
    from . import _layers
    connection, table = _gpkg.table_of(layer.source)
    try:
        _, _, _, geometry = _gpkg.describe(connection, table)
        joined = _layers.joined(layer, connection, table, _gpkg.oid_column(connection, table), geometry)
        for schema in joined.attached:
            connection.execute(f"detach database {schema}")
        return [(name, kind, column is not None) for name, _, kind, column in joined.fields]
    finally:
        connection.close()


def _joined_columns(layer):
    """The output fields of a joined layer copied to a new table, as the Maintain Fully Qualified Field Names environment
    says: ``env.qualifiedFieldNames`` True (the default) keeps the table's name with each field, False drops it.
    INFERRED: a qualified name is written ``table_field``, a dot being no character of a geodatabase field name; an
    unqualified name already taken — by another field or by the output's own object ID and shape — gets ``_1``, ``_2`` …; the join tables' object IDs are copied as fields."""
    from ._runtime import env
    qualified = str(getattr(env, "qualifiedFieldNames", True)).upper() not in ("FALSE", "UNQUALIFIED", "0")
    out, taken = [], {_gpkg.OID.lower(), _gpkg.GEOMETRY.lower()}
    for name, kind, _ in _joined_fields(layer):
        new = name.replace(".", "_") if qualified else name.split(".", 1)[1]
        base, i = new, 0
        while new.lower() in taken:
            i += 1
            new = f"{base}_{i}"
        taken.add(new.lower())
        out.append((name, _gpkg.Column(new, kind)))
    return out


def _copy_joined(value, out, with_shapes):
    from . import da
    layer = _joined_layer(value)
    pairs = _joined_columns(layer)
    _, kind, srid, _ = _schema(layer.source)
    columns = [c for _, c in pairs]
    _gpkg.create(out, kind if with_shapes else "Geometry", srid if with_shapes else 0, columns)
    with da.SearchCursor(layer.name, [n for n, _ in pairs] + ["SHAPE@"]) as rows, \
            da.InsertCursor(out, [*[c.name for c in columns], "SHAPE@"]) as written:
        for row in rows:
            written.insertRow((*row[:-1], row[-1] if with_shapes else None))
    return Result(out)


def _calculate_joined(in_table, field, expression, code_block, where_clause, field_type, scope):
    """CalculateField on a joined layer: fields and ``!table.field!`` references are qualified; only the layer's own
    table's fields can be calculated, as only they are written through the cursor."""
    from . import da
    layer = _joined_layer(in_table)
    fields = _joined_fields(layer)
    names = [n for n, _, _ in fields]
    own = {n.lower(): n for n, _, mine in fields if mine}
    if field.lower() not in own:
        if "." not in field or field.split(".", 1)[0].lower() != _stem_of(layer.source).lower():
            raise NotBuiltError(f"arcpy.management.CalculateField: '{field}' is not a field of the layer's own table; a "
                                "joined table's fields are not written through the join in grapy.")
        AddField(layer.source, field.split(".", 1)[1], field_type or "TEXT")
        names = [n for n, _, _ in _joined_fields(layer)]
        own = {n.lower(): n for n in names if n.lower() == field.lower()}
    field = own[field.lower()]
    if scope is None:
        scope = {}
        if code_block:
            exec(code_block, scope)  # noqa: S102 — the tool's own code, in the tool's own fenced process (ADR-223 §5.3)
    compiled = expression
    for name in sorted(names, key=len, reverse=True):
        compiled = compiled.replace(f"!{name}!", f"__row[{names.index(name)}]")
    with da.UpdateCursor(layer.name, names, where_clause) as rows:
        for row in rows:
            scope["__row"] = row
            row[names.index(field)] = eval(compiled, scope)  # noqa: S307 — as above
            rows.updateRow(row)


def AddJoin(in_layer_or_view, in_field, join_table, join_field, join_type="KEEP_ALL", index_join_fields="NO_INDEX_JOIN_FIELDS",
            rebuild_index="NO_REBUILD_INDEX", join_operation=None):
    """A join of a table to a layer on matching field values: the layer then shows its own fields as ``<table>.<field>``
    and the join table's as ``<join table>.<field>``, as documented; ``KEEP_ALL`` keeps every row, ``KEEP_COMMON`` only
    those with a match. A dataset path is given a new layer, as documented. The join is one to first — each row meets
    the first match, text matched without regard to case, as the page says of one-to-first joins. ``INDEX_JOIN_FIELDS``
    indexes both fields. INFERRED: with no ``join_operation`` the join is one to first, the page listing GeoPackage
    among neither the workspaces that support one to many; the first match is the one of lowest object ID; the layer's
    own table is named by its dataset's table name. ``JOIN_ONE_TO_MANY``, a join table that is itself a filtered or
    joined layer, and a join table of the same name as the layer's are not built."""
    from ._runtime import ExecuteError
    if str(join_operation or "JOIN_ONE_TO_FIRST").upper() not in ("JOIN_ONE_TO_FIRST", "#"):
        raise NotBuiltError(f"arcpy.management.AddJoin: join_operation '{join_operation}' is not built in grapy; "
                            "JOIN_ONE_TO_FIRST is.")
    layer = _as_layer(in_layer_or_view)
    join_path = _plain("AddJoin", join_table)
    connection, their_table = _gpkg.table_of(join_path)
    try:
        theirs = {c.name.lower(): c.name for c in _gpkg.describe(connection, their_table)[0]}
    finally:
        connection.close()
    mine = {c.name.lower(): c.name for c in _schema(layer.source)[0]}
    own_table = _stem_of(layer.source)
    if str(in_field).lower() not in mine:
        raise ExecuteError(f"ERROR 000728: the field '{in_field}' does not exist in {own_table}.")
    if str(join_field).lower() not in theirs:
        raise ExecuteError(f"ERROR 000728: the field '{join_field}' does not exist in {their_table}.")
    if their_table.lower() == own_table.lower() or any(j.name.lower() == their_table.lower() for j in layer.joins):
        raise NotBuiltError(f"arcpy.management.AddJoin: a second table named '{their_table}' in one join is not built in grapy.")
    from ._layers import Join
    if str(index_join_fields or "").upper() == "INDEX_JOIN_FIELDS":
        import sqlite3
        for table_path, table_name, column in ((join_path, their_table, theirs[str(join_field).lower()]),
                                               (layer.source, own_table, mine[str(in_field).lower()])):
            try:
                AddIndex(table_path, column, f"{table_name}_{column}_join")
            except sqlite3.OperationalError:
                pass                                  # the index is there already
    layer.joins.append(Join(join_path, their_table, mine[str(in_field).lower()], theirs[str(join_field).lower()],
                            str(join_type or "KEEP_ALL").upper() == "KEEP_COMMON"))
    return Result(layer.name)


def RemoveJoin(in_layer_or_view, join_name=None):
    """The join named — by its join table's name, as documented — removed from a layer; with no name, every join."""
    from ._runtime import ExecuteError
    layer = _LAYERS.get(_text(in_layer_or_view))
    if layer is None:
        raise ExecuteError(f"ERROR 000732: '{in_layer_or_view}' is not a layer or table view.")
    if not _given(join_name):
        layer.joins = []
    else:
        kept = [j for j in layer.joins if j.name.lower() != str(join_name).lower()]
        if len(kept) == len(layer.joins):
            raise ExecuteError(f"ERROR 000800: the layer has no join named '{join_name}'.")
        layer.joins = kept
    return Result(layer.name)


def CopyRows(in_rows, out_table, config_keyword=None):
    """The rows of a table, feature class or view — a view's selected rows — copied to a new table, shapes left behind
    as documented."""
    from . import da
    if _joined_layer(in_rows) is not None:
        return _copy_joined(in_rows, _table_out("CopyRows", out_table), False)
    path, where = _source(in_rows)
    columns = _schema(path)[0]
    out = _table_out("CopyRows", out_table)
    _gpkg.create(out, "Geometry", 0, columns)
    names = [c.name for c in columns]
    with da.SearchCursor(path, names or ["OID@"], where) as rows, da.InsertCursor(out, [*names, "SHAPE@"]) as written:
        for row in rows:
            written.insertRow((*(row if names else ()), None))
    return Result(out)


def CreateTable(out_path, out_name, template=None, config_keyword=None, out_alias=None, oid_type="SAME_AS_TEMPLATE"):
    """A new, empty table with the fields of each template, the first template's field winning a repeated name."""
    columns, seen = [], set()
    for each in _list(template):
        for column in _schema(_source(each)[0])[0]:
            if column.name.lower() not in seen:
                seen.add(column.name.lower())
                columns.append(column)
    out = _table_out("CreateTable", os.path.join(_text(out_path), _text(out_name)))
    _gpkg.create(out, "Geometry", 0, columns)
    return Result(out)


def TruncateTable(in_table):
    """Every row of a table or feature class deleted; its schema stays. Composite relationship classes delete their
    destination rows too (ADR-225)."""
    from . import _relationships
    path = _plain("TruncateTable", in_table)
    connection, table = _gpkg.table_of(path)
    try:
        oid = _gpkg.oid_column(connection, table)
        _relationships.cascade(connection, table, [r[0] for r in connection.execute(f'select "{oid}" from "{table}"')])
        connection.execute(f'delete from "{table}"')
        connection.commit()
    finally:
        connection.close()
    return Result(str(in_table))


def JoinField(in_data, in_field, join_table, join_field, fields=None, fm_option="NOT_USE_FM", field_mapping=None,
              index_join_fields="NO_INDEXES"):
    """Fields of ``join_table`` added to ``in_data`` for good, each row taking the values of the first join row (in
    object ID order) whose ``join_field`` equals its ``in_field``. A name already taken gets ``_1``, ``_2`` …, as
    documented. INFERRED: a row with no match is left null, and with ``fields`` empty the join field itself is carried
    too, being one of the join table's fields."""
    from . import da
    if str(fm_option or "NOT_USE_FM").upper() == "USE_FM":
        raise NotBuiltError("arcpy.management.JoinField: fm_option 'USE_FM' is not built in grapy.")
    target = _plain("JoinField", in_data)
    join_path, join_where = _source(join_table)
    join_columns = {c.name.lower(): c for c in _schema(join_path)[0]}
    key = join_columns[join_field.lower()].name
    wanted = [join_columns[f.lower()] for f in _list(fields)] if _list(fields) else list(join_columns.values())
    first: dict = {}
    with da.SearchCursor(join_path, [key, *[c.name for c in wanted]], join_where, sql_clause=(None, f'order by "{_oid_of(join_path)}"')) as rows:
        for row in rows:
            first.setdefault(row[0], row[1:])
    own = {c.name.lower(): c.name for c in _schema(target)[0]}
    field = own[in_field.lower()]
    added = []
    for column in wanted:
        name, i = column.name, 0
        while name.lower() in own:
            i += 1
            name = f"{column.name}_{i}"
        own[name.lower()] = name
        _gpkg.add_column(target, _gpkg.Column(name, column.type))
        added.append(name)
    with da.UpdateCursor(target, [field, *added]) as rows:
        for row in rows:
            values = first.get(row[0])
            rows.updateRow([row[0], *(values if values is not None else [None] * len(added))])
    return Result(str(in_data))


def CalculateFields(in_table, expression_type, fields, code_block=None, enforce_domains="NO_ENFORCE_DOMAINS"):
    """Several fields calculated, ``[[field, expression, {where clause}, {field type}], …]``, each added as its type
    (text when none is named) when it is not there. INFERRED: the fields are calculated one after another in the order
    given, so a later expression reads an earlier one's new values."""
    _python_only("CalculateFields", expression_type)
    scope: dict = {}
    if code_block:
        exec(code_block, scope)  # noqa: S102 — the tool's own code, in the tool's own fenced process (ADR-223 §5.3)
    import shlex
    entries = fields if isinstance(fields, (list, tuple)) else [shlex.split(e) for e in str(fields).split(";") if e.strip()]
    for entry in entries:
        entry = list(entry)
        name, expression = entry[0], entry[1]
        where = entry[2] if len(entry) > 2 and _given(entry[2]) else None
        kind = entry[3] if len(entry) > 3 and _given(entry[3]) else "TEXT"
        _calculate(in_table, name, expression, None, where, kind, scope)
    return Result(str(in_table))


def Sort(in_dataset, out_dataset, sort_field, spatial_sort_method="UR"):
    """The rows written to a new dataset in the order of ``[[field, ASCENDING|DESCENDING], …]`` — a layer's selected
    rows. INFERRED: rows that tie keep their object ID order, nulls come first ascending and last descending, and text
    sorts by its characters' code points, as the GeoPackage orders them. Sorting on the shape is not built."""
    from . import da
    path, where = _source(in_dataset)
    columns, kind, srid, _ = _schema(path)
    by_name = {c.name.lower(): c.name for c in columns}
    order = []
    for pair in _pairs(sort_field):
        name = pair[0]
        if name.lower() not in by_name:
            raise NotBuiltError(f"arcpy.management.Sort on '{name}' is not built in grapy; only attribute fields sort.")
        direction = str(pair[1]).upper() if len(pair) > 1 else "ASCENDING"
        order.append(f'"{by_name[name.lower()]}" {"desc" if direction == "DESCENDING" else "asc"}')
    order.append(f'"{_oid_of(path)}" asc')
    out = _path(out_dataset)
    _gpkg.create(out, kind, srid, columns)
    names = [c.name for c in columns]
    with da.SearchCursor(path, [*names, "SHAPE@"], where, sql_clause=(None, "order by " + ", ".join(order))) as rows, \
            da.InsertCursor(out, [*names, "SHAPE@"]) as written:
        for row in rows:
            written.insertRow(row)
    return Result(out)


# ---- shapes to shapes ---------------------------------------------------------------------------------------------------

def _features(value, srid=None):
    """(oid, values, shapely shape) of each feature a value shows, its shape in ``srid`` when given."""
    from . import da
    from ._reference import SpatialReference
    path, where = _source(value)
    columns, _, own, _ = _schema(path)
    names = [c.name for c in columns]
    target = SpatialReference(srid) if srid and own > 0 and own != srid else None
    with da.SearchCursor(path, ["OID@", *names, "SHAPE@"], where) as rows:
        return [(r[0], list(r[1:-1]), (r[-1].projectAs(target) if target else r[-1])._shape if r[-1] is not None else None)
                for r in rows]


def _stem_of(path):
    file, table = _gpkg.split(_path(path))
    return table or os.path.splitext(os.path.basename(file))[0]


def _no_tolerance(tool, cluster_tolerance):
    if _given(cluster_tolerance):
        raise NotBuiltError(f"arcpy.management.{tool}: a cluster_tolerance is not built in grapy; the inputs' own "
                            "coordinates are used.")


def _on(edge, line):
    """Whether a noded edge lies along a feature's linework: its midpoint does, to a tolerance of rounding."""
    import shapely
    middle = edge.interpolate(0.5, normalized=True)
    return shapely.dwithin(line, middle, 1e-9 * max(1.0, *(abs(v) for v in middle.coords[0])))


def _noded(lines):
    import shapely
    return _parts(shapely.union_all(lines)) if lines else []


def FeatureToLine(in_features, out_feature_class, cluster_tolerance=None, attributes="ATTRIBUTES"):
    """The lines and polygon outlines of the inputs, split where they meet other than at their ends, each piece a
    feature. With ``ATTRIBUTES`` each piece carries ``FID_<input>`` and the fields of the features it lies along: a
    piece two features of one input share is written once for each, a piece from features of different inputs once
    with both — as documented. INFERRED: where several inputs each have several features along one piece, a row is
    written for each combination. -1 and nulls stand for an input with no feature along it."""
    from . import da
    _no_tolerance("FeatureToLine", cluster_tolerance)
    paths = _list(in_features)
    schemas = [_schema(_source(p)[0]) for p in paths]
    srid = schemas[0][2]
    inputs = [[(oid, values, _linework(shape)) for oid, values, shape in _features(p, srid) if shape is not None]
              for p in paths]
    edges = _noded([line for features in inputs for _, _, line in features])
    out = _path(out_feature_class)
    keep = str(attributes or "ATTRIBUTES").upper() != "NO_ATTRIBUTES"
    from .analysis import _joined_columns
    columns = _joined_columns(*[(_stem_of(_source(p)[0]), s[0]) for p, s in zip(paths, schemas)]) if keep else []
    _gpkg.create(out, "Polyline", srid, columns)
    import itertools
    with da.InsertCursor(out, [*[c.name for c in columns], "SHAPE@"]) as written:
        for edge in edges:
            if not keep:
                written.insertRow((edge,))
                continue
            owners = []
            for features, schema in zip(inputs, schemas):
                found = [[oid, *values] for oid, values, line in features if _on(edge, line)]
                owners.append(found or [[-1, *[None] * len(schema[0])]])
            for combination in itertools.product(*owners):
                written.insertRow((*[v for part in combination for v in part], edge))
    return Result(out)


def FeatureToPolygon(in_features, out_feature_class, cluster_tolerance=None, attributes="ATTRIBUTES", label_features=None,
                     polygon_construction_source="PER_ENCLOSED_AREA"):
    """A polygon for every area the inputs' lines and outlines enclose. With ``label_features`` each polygon takes the
    fields of a label point inside it; without, it carries each input's fields — and with ``ATTRIBUTES`` a
    ``FID_<input>`` of -1 — left empty, as documented. INFERRED: of several label points inside one polygon the lowest
    object ID is used; empty values are null, not zero; ``FID_<input>`` precedes each input's fields, as the overlay
    tools write them. ``PER_ENCLOSED_LINE`` is not built."""
    import shapely
    from . import da
    _no_tolerance("FeatureToPolygon", cluster_tolerance)
    if str(polygon_construction_source or "PER_ENCLOSED_AREA").upper() != "PER_ENCLOSED_AREA":
        raise NotBuiltError("arcpy.management.FeatureToPolygon: polygon_construction_source "
                            f"'{polygon_construction_source}' is not built in grapy.")
    paths = _list(in_features)
    schemas = [_schema(_source(p)[0]) for p in paths]
    srid = schemas[0][2]
    lines = [_linework(shape) for p in paths for _, _, shape in _features(p, srid) if shape is not None]
    faces = list(shapely.polygonize(_noded(lines)).geoms) if lines else []
    out = _path(out_feature_class)
    if _given(label_features):
        label_columns = _schema(_source(label_features)[0])[0]
        labels = sorted((oid, values, shape) for oid, values, shape in _features(label_features, srid) if shape is not None)
        _gpkg.create(out, "Polygon", srid, label_columns)
        with da.InsertCursor(out, [*[c.name for c in label_columns], "SHAPE@"]) as written:
            for face in faces:
                inside = [values for _, values, point in labels if face.covers(point)]
                written.insertRow((*(inside[0] if inside else [None] * len(label_columns)), face))
        return Result(out)
    keep = str(attributes if attributes is not None else "ATTRIBUTES").upper() not in ("NO_ATTRIBUTES", "FALSE")
    if keep:
        from .analysis import _joined_columns
        columns = _joined_columns(*[(_stem_of(_source(p)[0]), s[0]) for p, s in zip(paths, schemas)])
    else:
        columns, seen = [], set()
        for schema in schemas:
            for column in schema[0]:
                if column.name.lower() not in seen:
                    seen.add(column.name.lower())
                    columns.append(column)
    _gpkg.create(out, "Polygon", srid, columns)
    fids, at = [], 0
    for schema in (schemas if keep else []):
        fids.append(columns[at].name)
        at += 1 + len(schema[0])
    with da.InsertCursor(out, [*fids, "SHAPE@"]) as written:
        for face in faces:
            written.insertRow((*[-1] * len(fids), face))
    return Result(out)


def _left_right(edge, polygons):
    """The polygons to the left and to the right of an edge's first segment, by a point just off each side."""
    from shapely.geometry import Point as ShapelyPoint
    (x0, y0), (x1, y1) = edge.coords[0][:2], edge.coords[1][:2]
    length = ((x1 - x0) ** 2 + (y1 - y0) ** 2) ** 0.5
    mx, my = (x0 + x1) / 2, (y0 + y1) / 2
    step = length * 1e-6
    nx, ny = -(y1 - y0) / length * step, (x1 - x0) / length * step
    left, right = ShapelyPoint(mx + nx, my + ny), ShapelyPoint(mx - nx, my - ny)
    on_left = [oid for oid, shape in polygons if shape.contains(left)]
    on_right = [oid for oid, shape in polygons if shape.contains(right)]
    return (on_left[0] if on_left else -1), (on_right[0] if on_right else -1)


def PolygonToLine(in_features, out_feature_class, neighbor_option="IDENTIFY_NEIGHBORS"):
    """Polygon outlines as lines. ``IDENTIFY_NEIGHBORS``: each stretch of outline shared by the same polygons is one
    line, with ``LEFT_FID`` and ``RIGHT_FID`` the polygons on its sides and -1 outside; an outline with no neighbour runs
    clockwise, its polygon on the right, as documented. ``IGNORE_NEIGHBORS``: a line for each ring, with the polygon's
    fields and ``ORIG_FID``. INFERRED: a shared line runs as the noding left it. Overlapping input polygons are not
    built."""
    import shapely
    from shapely.ops import linemerge
    from . import da
    columns, _, srid, _ = _schema(_source(in_features)[0])
    features = [f for f in _features(in_features) if f[2] is not None and not f[2].is_empty]
    out = _path(out_feature_class)
    if str(neighbor_option or "IDENTIFY_NEIGHBORS").upper() == "IGNORE_NEIGHBORS":
        _gpkg.create(out, "Polyline", srid, [*columns, _gpkg.Column("ORIG_FID", "Integer")])
        with da.InsertCursor(out, [*[c.name for c in columns], "ORIG_FID", "SHAPE@"]) as written:
            for oid, values, shape in features:
                for part in _parts(shape):
                    for ring in [part.exterior, *part.interiors]:
                        written.insertRow((*values, oid, shapely.LineString(ring.coords)))
        return Result(out)
    polygons = [(oid, shape) for oid, _, shape in features]
    for i, (_, a) in enumerate(polygons):
        for _, b in polygons[i + 1:]:
            if a.intersection(b).area > 0:
                raise NotBuiltError("arcpy.management.PolygonToLine over overlapping polygons is not built in grapy.")
    groups: dict = {}
    for edge in _noded([shape.boundary for _, shape in polygons]):
        owners = tuple(sorted(oid for oid, shape in polygons if _on(edge, shape.boundary)))
        groups.setdefault(owners, []).append(edge)
    _gpkg.create(out, "Polyline", srid, [_gpkg.Column("LEFT_FID", "Integer"), _gpkg.Column("RIGHT_FID", "Integer")])
    with da.InsertCursor(out, ["LEFT_FID", "RIGHT_FID", "SHAPE@"]) as written:
        for owners, edges in groups.items():
            merged = linemerge(edges) if len(edges) > 1 else edges[0]
            for line in _parts(merged):
                left, right = _left_right(line, polygons)
                if left != -1 and right == -1:
                    line, (left, right) = line.reverse(), (-1, left)
                written.insertRow((left, right, line))
    return Result(out)


def SplitLine(in_features, out_feature_class):
    """Each line, or polygon outline, cut at every vertex: a feature per segment, with the input's fields, its
    ``ORIG_FID`` and ``ORIG_SEQ`` counting the segments from the first vertex. INFERRED: ``ORIG_SEQ`` starts at 1 and
    runs on through a feature's later parts and rings."""
    import shapely
    from . import da
    columns, _, srid, _ = _schema(_source(in_features)[0])
    out = _path(out_feature_class)
    _gpkg.create(out, "Polyline", srid, [*columns, _gpkg.Column("ORIG_FID", "Integer"), _gpkg.Column("ORIG_SEQ", "Integer")])
    with da.InsertCursor(out, [*[c.name for c in columns], "ORIG_FID", "ORIG_SEQ", "SHAPE@"]) as written:
        for oid, values, shape in _features(in_features):
            if shape is None:
                continue
            sequence = 0
            for line in _parts(_linework(shape)):
                coordinates = list(line.coords)
                for a, b in zip(coordinates, coordinates[1:]):
                    sequence += 1
                    written.insertRow((*values, oid, sequence, shapely.LineString([a, b])))
    return Result(out)


def SplitLineAtPoint(in_features, point_features, out_feature_class, search_radius=None):
    """Lines cut where each point within ``search_radius`` lies nearest along them, each piece keeping the line's
    fields, its ``ORIG_FID`` and ``ORIG_SEQ`` from the line's start. INFERRED: ``ORIG_SEQ`` starts at 1, and a point
    nearest a line's very end cuts nothing. Without a search radius arcpy uses one nearest point per line, which is
    not built; multipart lines are not built."""
    from shapely.ops import substring
    from . import da
    if not _given(search_radius):
        raise NotBuiltError("arcpy.management.SplitLineAtPoint without a search_radius is not built in grapy.")
    columns, _, srid, _ = _schema(_source(in_features)[0])
    radius = _linear(search_radius, srid, "SplitLineAtPoint")
    points = [s for _, s in _shapes(point_features, srid)]
    out = _path(out_feature_class)
    _gpkg.create(out, "Polyline", srid, [*columns, _gpkg.Column("ORIG_FID", "Integer"), _gpkg.Column("ORIG_SEQ", "Integer")])
    with da.InsertCursor(out, [*[c.name for c in columns], "ORIG_FID", "ORIG_SEQ", "SHAPE@"]) as written:
        for oid, values, shape in _features(in_features):
            if shape is None:
                continue
            parts = _parts(shape)
            if len(parts) > 1:
                raise NotBuiltError("arcpy.management.SplitLineAtPoint on a multipart line is not built in grapy.")
            line = parts[0]
            cuts = sorted({line.project(p) for p in points if line.distance(p) <= radius} - {0.0, line.length})
            stops = [0.0, *[c for c in cuts if 0 < c < line.length], line.length]
            for sequence, (a, b) in enumerate(zip(stops, stops[1:]), start=1):
                written.insertRow((*values, oid, sequence, substring(line, a, b)))
    return Result(out)


def FeatureEnvelopeToPolygon(in_features, out_feature_class, single_envelope="SINGLEPART"):
    """Each feature's envelope as a polygon, with its fields and ``ORIG_FID``; ``MULTIPART`` puts one envelope round
    each part in one multipart polygon. Envelopes of no area are left out, as documented."""
    import shapely
    from shapely.geometry import box
    from . import da
    columns, _, srid, _ = _schema(_source(in_features)[0])
    out = _path(out_feature_class)
    _gpkg.create(out, "Polygon", srid, [*columns, _gpkg.Column("ORIG_FID", "Integer")])
    each_part = str(single_envelope or "SINGLEPART").upper() == "MULTIPART"
    with da.InsertCursor(out, [*[c.name for c in columns], "ORIG_FID", "SHAPE@"]) as written:
        for oid, values, shape in _features(in_features):
            if shape is None or shape.is_empty:
                continue
            boxes = [box(*p.bounds) for p in (_parts(shape) if each_part else [shape])]
            boxes = [b for b in boxes if b.area > 0]
            if boxes:
                written.insertRow((*values, oid, shapely.MultiPolygon(boxes) if len(boxes) > 1 else boxes[0]))
    return Result(out)


def GeneratePointsAlongLines(Input_Features, Output_Feature_Class, Point_Placement="DISTANCE", Distance=None,
                             Percentage=None, Include_End_Points="NO_END_POINTS", Add_Chainage_Fields="NO_CHAINAGE",
                             Distance_Field=None, Distance_Method="PLANAR"):
    """Points along each line or outline: every ``Distance``, every ``Percentage`` of its length, or at a field's
    distance (a number for an interval, text for ``;``-separated distances), with the input's fields and ``ORIG_FID``;
    ``ADD_CHAINAGE`` adds ``ORIG_LEN`` and ``ORIG_SEQ``. INFERRED: interval points stop short of the end, which
    ``END_POINTS`` adds; ``ORIG_SEQ`` starts at 1. Multipart features and geodesic distances are not built."""
    from . import da
    if str(Distance_Method or "PLANAR").upper() != "PLANAR":
        raise NotBuiltError(f"arcpy.management.GeneratePointsAlongLines: Distance_Method '{Distance_Method}' is not built in grapy.")
    columns, _, srid, _ = _schema(_source(Input_Features)[0])
    placement = str(Point_Placement or "DISTANCE").upper()
    chainage = str(Add_Chainage_Fields or "NO_CHAINAGE").upper() == "ADD_CHAINAGE"
    ends = str(Include_End_Points or "NO_END_POINTS").upper() == "END_POINTS"
    extra = [_gpkg.Column("ORIG_FID", "Integer")] + ([_gpkg.Column("ORIG_LEN", "Double"), _gpkg.Column("ORIG_SEQ", "Integer")]
                                                     if chainage else [])
    names = [c.name for c in columns]
    field_at = None
    if placement == "DISTANCE_FIELD":
        field_at = {n.lower(): i for i, n in enumerate(names)}[str(Distance_Field).lower()]
    interval = _linear(Distance, srid, "GeneratePointsAlongLines") if placement == "DISTANCE" else None
    out = _path(Output_Feature_Class)
    _gpkg.create(out, "Point", srid, [*columns, *extra])
    with da.InsertCursor(out, [*names, *[c.name for c in extra], "SHAPE@"]) as written:
        for oid, values, shape in _features(Input_Features):
            if shape is None or shape.is_empty:
                continue
            lines = _parts(_linework(shape))
            if len(lines) > 1:
                raise NotBuiltError("arcpy.management.GeneratePointsAlongLines on a multipart feature is not built in grapy.")
            line = lines[0]
            length = line.length
            if placement == "PERCENTAGE":
                step = float(Percentage) / 100 * length
                stops = _steps(step, length)
            elif placement == "DISTANCE":
                stops = _steps(interval, length)
            elif placement == "DISTANCE_FIELD":
                value = values[field_at]
                if isinstance(value, str):
                    stops = sorted(d for d in (float(v) for v in value.split(";") if v.strip()) if 0 < d <= length)
                else:
                    stops = _steps(float(value), length) if value is not None and float(value) > 0 else []
            else:
                raise NotBuiltError(f"arcpy.management.GeneratePointsAlongLines: Point_Placement '{Point_Placement}' is not built in grapy.")
            if ends:
                stops = sorted({0.0, *stops, length})
            for sequence, at in enumerate(stops, start=1):
                written.insertRow((*values, oid, *((at, sequence) if chainage else ()), line.interpolate(at)))
    return Result(out)


def _steps(step, length):
    if step is None or step <= 0:
        return []
    count = int(length // step)
    stops = [step * k for k in range(1, count + 1)]
    return [s for s in stops if s < length - 1e-12 * max(1.0, length)]


def UnsplitLine(in_features, out_feature_class, dissolve_field=None, statistics_fields=None, concatenation_separator=""):
    """Lines of the same dissolve values joined where their ends meet, with statistics as ``SUM_field``. INFERRED: lines
    are joined only through ends where exactly two of them meet. ``CONCATENATE`` and ``MODE`` are not built."""
    from shapely.ops import linemerge
    from . import da
    columns, _, srid, _ = _schema(_source(in_features)[0])
    by_name = {c.name.lower(): c for c in columns}
    names = [c.name for c in columns]
    fields = [by_name[f.lower()].name for f in _list(dissolve_field)]
    statistics = [(by_name[f.lower()].name, s.upper()) for f, s in _pairs(statistics_fields)]
    for _, kind in statistics:
        if kind not in _STATISTICS:
            raise NotBuiltError(f"arcpy.management.UnsplitLine: the statistic '{kind}' is not built in grapy.")
    groups: dict = {}
    for oid, values, shape in _features(in_features):
        if shape is None:
            continue
        row = dict(zip(names, values))
        groups.setdefault(tuple(row[f] for f in fields), []).append((row, shape))
    out_columns = [by_name[f.lower()] for f in fields] + [
        _gpkg.Column(f"{s}_{f}", _statistic_type(s, by_name.get(f.lower()))) for f, s in statistics]
    out = _path(out_feature_class)
    _gpkg.create(out, "Polyline", srid, out_columns)
    with da.InsertCursor(out, [*[c.name for c in out_columns], "SHAPE@"]) as written:
        for key, members in groups.items():
            merged = linemerge([part for _, shape in members for part in _parts(shape)])
            for line in _parts(merged):
                inside = [row for row, shape in members if _on(_parts(shape)[0], line)]
                written.insertRow((*key, *[_statistic(s, [r[f] for r in inside]) for f, s in statistics], line))
    return Result(out)


def PointsToLine(Input_Features, Output_Feature_Class, Line_Field=None, Sort_Field=None, Close_Line="NO_CLOSE",
                 Line_Construction_Method="CONTINUOUS", Attribute_Source="NONE", Transfer_Fields=None):
    """Lines through points, one per value of ``Line_Field`` (or one in all), the points taken in ``Sort_Field`` order
    or the order they are read; ``TWO_POINT`` makes a line of each consecutive pair, ``CLOSE`` returns to the first
    point. A line needs two points. ``Transfer_Fields`` come from the ``START`` or ``END`` point, or both as
    ``START_field`` and ``END_field``. INFERRED: lines are written in the order their first point is read, and a
    ``CLOSE`` line's end is its last point read."""
    from shapely.geometry import LineString
    from . import da
    columns, _, srid, _ = _schema(_source(Input_Features)[0])
    by_name = {c.name.lower(): c for c in columns}
    names = [c.name for c in columns]
    line_field = by_name[str(Line_Field).lower()] if _given(Line_Field) else None
    sort_field = by_name[str(Sort_Field).lower()].name if _given(Sort_Field) else None
    source = str(Attribute_Source or "NONE").upper()
    transfer = [by_name[f.lower()] for f in _list(Transfer_Fields)] if source != "NONE" else []
    groups: dict = {}
    for oid, values, shape in _features(Input_Features):
        if shape is None or shape.is_empty:
            continue
        if shape.geom_type != "Point":
            raise NotBuiltError("arcpy.management.PointsToLine from multipoints is not built in grapy.")
        row = dict(zip(names, values))
        groups.setdefault(row[line_field.name] if line_field else None, []).append((row, (shape.x, shape.y)))
    if source == "BOTH_ENDS":
        carried = [_gpkg.Column(f"{p}_{c.name}", c.type) for p in ("START", "END") for c in transfer]
    else:
        carried = transfer
    out_columns = ([line_field] if line_field else []) + carried
    out = _path(Output_Feature_Class)
    _gpkg.create(out, "Polyline", srid, out_columns)
    two_point = str(Line_Construction_Method or "CONTINUOUS").upper() == "TWO_POINT"
    close = str(Close_Line or "NO_CLOSE").upper() == "CLOSE"
    with da.InsertCursor(out, [*[c.name for c in out_columns], "SHAPE@"]) as written:
        for key, points in groups.items():
            if sort_field:
                points = sorted(points, key=lambda p: (p[0][sort_field] is not None, p[0][sort_field]))
            if len(points) < 2:
                continue
            if two_point:
                pairs = list(zip(points, points[1:])) + ([(points[-1], points[0])] if close else [])
            else:
                pairs = [(points[0], points[-1])]
            for start, end in pairs:
                if two_point:
                    shape = LineString([start[1], end[1]])
                else:
                    shape = LineString([p[1] for p in points] + ([points[0][1]] if close else []))
                values = [] if source == "NONE" else (
                    [start[0][c.name] for c in transfer] + [end[0][c.name] for c in transfer] if source == "BOTH_ENDS"
                    else [(start if source == "START" else end)[0][c.name] for c in transfer])
                written.insertRow((*([key] if line_field else []), *values, shape))
    return Result(out)


# ---- tables to shapes ---------------------------------------------------------------------------------------------------

def _table_rows(in_table):
    if str(in_table).lower().endswith((".csv", ".txt", ".xlsx", ".xls", ".dbf")):
        raise NotBuiltError(f"Reading {os.path.splitext(str(in_table))[1]} tables is not built in grapy's management "
                            "tools; give a GeoPackage table.")
    path, _ = _source(in_table)
    columns, _, srid, _ = _schema(path)
    return columns, srid, _features(in_table)


def _reference_code(value, default):
    from ._reference import reference_of
    reference = reference_of(value) if _given(value) else None
    return reference.factoryCode if reference is not None else default


def XYTableToPoint(in_table, out_feature_class, x_field, y_field, z_field=None, coordinate_system=None):
    """A point for each row at its x and y fields, carrying all its fields, in WGS 1984 unless a coordinate system is
    given, as documented. A row whose x or y is null keeps an empty point; in a geographic system a coordinate beyond
    ±400 drops the row with a warning, as documented. A ``z_field`` makes a Z feature class, each point at its row's z
    (0 where the z is null — INFERRED, as grapy writes a missing z)."""
    from shapely.geometry import Point as ShapelyPoint
    from . import da
    from ._reference import SpatialReference
    from ._runtime import AddWarning
    columns, _, rows = _table_rows(in_table)
    srid = _reference_code(coordinate_system, 4326)
    geographic = srid > 0 and SpatialReference(srid).type == "Geographic"
    names = [c.name for c in columns]
    at = {n.lower(): i for i, n in enumerate(names)}
    xi, yi = at[str(x_field).lower()], at[str(y_field).lower()]
    zi = at[str(z_field).lower()] if _given(z_field) else None
    out = _path(out_feature_class)
    _gpkg.create(out, "Point", srid, columns, zi is not None)
    with da.InsertCursor(out, [*names, "SHAPE@"]) as written:
        for oid, values, _ in rows:
            x, y = values[xi], values[yi]
            if x is None or y is None:
                written.insertRow((*values, ShapelyPoint()))
                continue
            x, y = float(x), float(y)
            if geographic and not (-400 <= x <= 400 and -400 <= y <= 400):
                AddWarning(f"WARNING 001003: row {oid} has a coordinate out of range and was not written.")
                continue
            if zi is not None:
                z = values[zi]
                written.insertRow((*values, ShapelyPoint(x, y, float(z) if z is not None else 0.0)))
            else:
                written.insertRow((*values, ShapelyPoint(x, y)))
    return Result(out)


_Z_UNITS = _units.ADJUST_Z


def Adjust3DZ(in_features, reverse_sign="NO_REVERSE", adjust_value=None, from_units=None, to_units=None):
    """Every vertex's z changed in place, as documented: ``REVERSE`` inverts its sign, ``adjust_value`` — a number or a
    field — shifts it, and ``from_units``/``to_units`` convert it.

    The units are the US survey ones — a foot of 1200/3937 m, a fathom of six of them — as measured in Pro (D-323).

    INFERRED: the order is sign, then shift, then units, the order the parameters come in; a null shift field leaves
    the feature unshifted.
    Refused: a 2D feature class; units given one without the other."""
    import shapely
    from . import da
    from ._runtime import ExecuteError
    connection, table = _gpkg.table_of(in_features)
    try:
        columns = _gpkg.describe(connection, table)[0]
        has_z, has_m = _gpkg.dimensions(connection, table)
    finally:
        connection.close()
    if not has_z:
        raise NotBuiltError(f"arcpy.management.Adjust3DZ: {in_features} is a 2D feature class; it has no z to adjust.")
    if _given(from_units) != _given(to_units):
        raise ExecuteError("ERROR 000800: from_units and to_units are given together.")
    factor = 1.0
    if _given(from_units):
        try:
            factor = _Z_UNITS[str(from_units).upper()] / _Z_UNITS[str(to_units).upper()]
        except KeyError as missing:
            raise NotBuiltError(f"arcpy.management.Adjust3DZ: the unit {missing} is not built in grapy.") from None
    sign = -1.0 if str(reverse_sign or "NO_REVERSE").upper() == "REVERSE" else 1.0
    names = {c.name.lower(): c.name for c in columns}
    shift_field = names.get(str(adjust_value).lower()) if isinstance(adjust_value, str) else None
    fields = ["SHAPE@"] + ([shift_field] if shift_field else [])
    with da.UpdateCursor(in_features, fields) as rows:
        for row in rows:
            if row[0] is None:
                continue
            shift = row[1] if shift_field else (float(adjust_value) if _given(adjust_value) else 0.0)
            shift = 0.0 if shift is None else float(shift)
            changed = _gpkg.rebuild(row[0]._shape, True, has_m, None, lambda z, xy: (sign * z + shift) * factor)
            rows.updateRow([changed, *row[1:]])
    return Result(in_features)


def _planar_only(tool, line_type, srid):
    from ._reference import SpatialReference
    if str(line_type or "GEODESIC").upper() != "PLANAR":
        raise NotBuiltError(f"arcpy.management.{tool}: line_type '{line_type or 'GEODESIC'}' is not built in grapy; "
                            "PLANAR is.")
    if srid <= 0 or SpatialReference(srid).type == "Geographic":
        from ._runtime import ExecuteError
        raise ExecuteError(f"ERROR 000732: {tool}'s PLANAR lines need a projected coordinate system.")


def _line_table(tool, in_table, out_featureclass, used, id_field, spatial_reference, attributes, line_type, make):
    """The line tools' common shape: the fields used and ``id_field`` carried, with ``ATTRIBUTES`` the rest and
    ``ORIG_FID``. INFERRED: the fields are written in that order."""
    from . import da
    columns, srid, rows = _table_rows(in_table)
    srid = _reference_code(spatial_reference, srid if srid > 0 else 4326)
    _planar_only(tool, line_type, srid)
    by_name = {c.name.lower(): c for c in columns}
    names = [c.name for c in columns]
    lead = [by_name[str(f).lower()] for f in used]
    if _given(id_field) and str(id_field).lower() not in (c.name.lower() for c in lead):
        lead.append(by_name[str(id_field).lower()])
    rest = [c for c in columns if c not in lead] if str(attributes or "NO_ATTRIBUTES").upper() == "ATTRIBUTES" else None
    out_columns = lead + (rest + [_gpkg.Column("ORIG_FID", "Integer")] if rest is not None else [])
    out = _path(out_featureclass)
    _gpkg.create(out, "Polyline", srid, out_columns)
    with da.InsertCursor(out, [*[c.name for c in out_columns], "SHAPE@"]) as written:
        for oid, values, _ in rows:
            row = dict(zip(names, values))
            shape = make(row, srid)
            if shape is None:
                continue
            written.insertRow((*[row[c.name] for c in lead], *([row[c.name] for c in rest] + [oid] if rest is not None else []), shape))
    return Result(out)


def XYToLine(in_table, out_featureclass, startx_field, starty_field, endx_field, endy_field, line_type="GEODESIC",
             id_field=None, spatial_reference=None, attributes="NO_ATTRIBUTES"):
    """A line from each row's start to its end coordinates, carrying those fields and ``id_field``; ``ATTRIBUTES`` adds
    the rest and ``ORIG_FID``. Only ``PLANAR`` lines on a projected system are built. INFERRED: a row with a null
    coordinate makes no line."""
    from shapely.geometry import LineString
    fields = [startx_field, starty_field, endx_field, endy_field]

    def make(row, _srid):
        values = [row[next(n for n in row if n.lower() == str(f).lower())] for f in fields]
        if any(v is None for v in values):
            return None
        return LineString([(float(values[0]), float(values[1])), (float(values[2]), float(values[3]))])

    return _line_table("XYToLine", in_table, out_featureclass, fields, id_field, spatial_reference, attributes, line_type, make)


_BEARINGS = {"DEGREES": 3.141592653589793 / 180, "RADS": 1.0, "RADIANS": 1.0, "GRADS": 3.141592653589793 / 200}


def BearingDistanceToLine(in_table, out_featureclass, x_field, y_field, distance_field, distance_units=None,
                          bearing_field=None, bearing_units="DEGREES", line_type="GEODESIC", id_field=None,
                          spatial_reference=None, attributes="NO_ATTRIBUTES"):
    """A line from each row's x and y, ``distance`` long, at ``bearing`` clockwise from grid north as documented for
    planar lines; it carries those four fields and ``id_field``, and with ``ATTRIBUTES`` the rest and ``ORIG_FID``.
    Only ``PLANAR`` lines on a projected system are built. INFERRED: with no distance unit the distance is in the
    coordinate system's units. ``MILS`` is not built: the page does not say how many make a circle."""
    import math
    from shapely.geometry import LineString
    unit = str(bearing_units or "DEGREES").upper()
    if unit not in _BEARINGS:
        raise NotBuiltError(f"arcpy.management.BearingDistanceToLine: bearing_units '{bearing_units}' is not built in grapy.")
    fields = [x_field, y_field, distance_field, bearing_field]

    def make(row, srid):
        x, y, d, b = [row[next(n for n in row if n.lower() == str(f).lower())] for f in fields]
        if None in (x, y, d, b):
            return None
        d = float(d) * (_metres(distance_units, "BearingDistanceToLine", _units.BEARING_DISTANCE) / _per_unit(srid) if _given(distance_units) else 1.0)
        angle = float(b) * _BEARINGS[unit]
        return LineString([(float(x), float(y)), (float(x) + d * math.sin(angle), float(y) + d * math.cos(angle))])

    return _line_table("BearingDistanceToLine", in_table, out_featureclass, fields, id_field, spatial_reference,
                       attributes, line_type, make)


# ---- grids --------------------------------------------------------------------------------------------------------------

def _xy(value):
    if value is None:
        return None
    if hasattr(value, "X"):
        return float(value.X), float(value.Y)
    if isinstance(value, (list, tuple)):
        return float(value[0]), float(value[1])
    parts = str(value).replace(",", " ").split()
    return (float(parts[0]), float(parts[1])) if len(parts) >= 2 else None


def _extent_of(value):
    """xmin, ymin, xmax, ymax and the reference code of an Extent, an ``"xmin ymin xmax ymax"`` text or a dataset."""
    import shapely
    if hasattr(value, "XMin"):
        reference = value.spatialReference
        return (value.XMin, value.YMin, value.XMax, value.YMax), (reference.factoryCode if reference else 0)
    parts = str(value).split()
    if len(parts) >= 4:
        try:
            return tuple(float(p) for p in parts[:4]), 0
        except ValueError:
            pass
    features = _features(value)
    shapes = [s for _, _, s in features if s is not None and not s.is_empty]
    return shapely.union_all(shapes).bounds, _schema(_source(value)[0])[2]


def _count(extent, size, tool, what):
    if size <= 0:
        raise NotBuiltError(f"arcpy.management.{tool}: computing {what} needs a cell size; give one.")
    n = extent / size
    if abs(n - round(n)) > 1e-9 * max(1.0, n):
        raise NotBuiltError(f"arcpy.management.{tool}: an extent that is not a whole number of cells ({n:g}) is not "
                            "built in grapy, since arcpy does not document how it rounds.")
    return int(round(n))


def CreateFishnet(out_feature_class, origin_coord, y_axis_coord, cell_width, cell_height, number_rows, number_columns,
                  corner_coord=None, labels="LABELS", template=None, geometry_type="POLYLINE"):
    """A grid of rectangular cells from ``origin_coord``, turned so its y-axis points at ``y_axis_coord``; a cell size of
    0 is worked out from the opposite corner and the count, and a count of 0 from the corner and the size. ``LABELS``
    writes a point at each cell's centre to ``<output>_label``. Only ``POLYGON`` cells are built: the page's
    ``POLYLINE`` cells — four lines each — leave open whether neighbours share their lines. INFERRED: cells are written
    row by row from the origin; the reference is the template's, else ``env.outputCoordinateSystem``; a template with
    no origin or corner gives its extent's lower-left and upper-right; a count that does not divide the extent is not
    built."""
    import math
    from shapely.geometry import Point as ShapelyPoint, Polygon as ShapelyPolygon
    from . import da
    from ._runtime import env
    from ._reference import reference_of
    if str(geometry_type or "POLYLINE").upper() != "POLYGON":
        raise NotBuiltError(f"arcpy.management.CreateFishnet: geometry_type '{geometry_type or 'POLYLINE'}' is not built "
                            "in grapy; POLYGON is.")
    srid = 0
    origin, y_axis, corner = _xy(origin_coord) if _given(origin_coord) else None, \
        _xy(y_axis_coord) if _given(y_axis_coord) else None, _xy(corner_coord) if _given(corner_coord) else None
    if _given(template):
        (x0, y0, x1, y1), srid = _extent_of(template)
        origin = origin or (x0, y0)
        corner = corner or (x1, y1)
        y_axis = y_axis or (x0, y0 + 10)
    if not srid and env.outputCoordinateSystem is not None:
        srid = reference_of(env.outputCoordinateSystem).factoryCode
    vx, vy = y_axis[0] - origin[0], y_axis[1] - origin[1]
    norm = math.hypot(vx, vy)
    vx, vy = vx / norm, vy / norm
    ux, uy = vy, -vx
    width, height = float(cell_width or 0), float(cell_height or 0)
    rows, cols = int(number_rows or 0), int(number_columns or 0)
    if corner is not None:
        dx, dy = corner[0] - origin[0], corner[1] - origin[1]
        span_x, span_y = dx * ux + dy * uy, dx * vx + dy * vy
        if width == 0 and cols:
            width = span_x / cols
        if height == 0 and rows:
            height = span_y / rows
        if cols == 0:
            cols = _count(span_x, width, "CreateFishnet", "number_columns")
        if rows == 0:
            rows = _count(span_y, height, "CreateFishnet", "number_rows")
    if not (width > 0 and height > 0 and rows > 0 and cols > 0):
        from ._runtime import ExecuteError
        raise ExecuteError("ERROR 000989: CreateFishnet needs a cell size and a count, or an opposite corner to work one out.")

    def at(c, r):
        return (origin[0] + c * width * ux + r * height * vx, origin[1] + c * width * uy + r * height * vy)

    out = _path(out_feature_class)
    _gpkg.create(out, "Polygon", srid, [])
    with da.InsertCursor(out, ["SHAPE@"]) as written:
        for r in range(rows):
            for c in range(cols):
                written.insertRow((ShapelyPolygon([at(c, r), at(c + 1, r), at(c + 1, r + 1), at(c, r + 1)]),))
    outputs = [out]
    if str(labels or "LABELS").upper() == "LABELS":
        file, table = _gpkg.split(out)
        label = f"{file}/{table}_label" if table else f"{file[:-5]}_label.gpkg"
        _gpkg.create(label, "Point", srid, [])
        with da.InsertCursor(label, ["SHAPE@"]) as written:
            for r in range(rows):
                for c in range(cols):
                    written.insertRow((ShapelyPoint(at(c + 0.5, r + 0.5)),))
        outputs.append(label)
    return Result(*outputs)


_SQUARE_UNITS = _units.AREAL  # an areal unit's words, keyed without underscores: SquareFeet international (D-323)


def _letters(n):
    """1 → A, 26 → Z, 27 → AA, as spreadsheet columns are named."""
    text = ""
    while n > 0:
        n, rest = divmod(n - 1, 26)
        text = chr(65 + rest) + text
    return text


def GenerateTessellation(Output_Feature_Class, Extent, Shape_Type="HEXAGON", Size=None, Spatial_Reference=None,
                         H3_Resolution=7):
    """Cells of ``Size`` area — squares, flat-topped ``HEXAGON``s or pointed ``TRANSVERSE_HEXAGON``s — covering the
    extent, each with a ``GRID_ID`` of a letter and a number. INFERRED, since the page does not say: the grid is
    anchored with a cell centred on the extent's lower-left corner for hexagons and with a cell's corner there for
    squares; a cell is kept when it overlaps the extent with some area; letters count rows up from the bottom and
    numbers columns from the left, both from the first cell kept. ``DIAMOND``, ``TRIANGLE`` and ``H3_HEXAGON`` are not
    built."""
    import math
    from shapely.geometry import Polygon as ShapelyPolygon, box
    from . import da
    kind = str(Shape_Type or "HEXAGON").upper()
    if kind not in ("SQUARE", "HEXAGON", "TRANSVERSE_HEXAGON"):
        raise NotBuiltError(f"arcpy.management.GenerateTessellation: Shape_Type '{kind}' is not built in grapy.")
    (x0, y0, x1, y1), srid = _extent_of(Extent)
    srid = _reference_code(Spatial_Reference, srid)
    if not _given(Size):
        raise NotBuiltError("arcpy.management.GenerateTessellation without a Size is not built in grapy.")
    parts = str(Size).split()
    area = float(parts[0])
    if len(parts) > 1 and parts[1].upper() != "UNKNOWN":
        per = _per_unit(srid)
        unit = parts[1].upper().replace("_", "")
        if per is None or unit not in _SQUARE_UNITS:
            raise NotBuiltError(f"arcpy.management.GenerateTessellation: a Size in {parts[1]} here is not built in grapy.")
        area = area * _SQUARE_UNITS[unit] / per ** 2
    extent = box(x0, y0, x1, y1)
    cells = []
    if kind == "SQUARE":
        s = math.sqrt(area)
        for r in range(-1, int((y1 - y0) / s) + 2):
            for c in range(-1, int((x1 - x0) / s) + 2):
                cells.append((r, c, box(x0 + c * s, y0 + r * s, x0 + (c + 1) * s, y0 + (r + 1) * s)))
    else:
        s = math.sqrt(2 * area / (3 * math.sqrt(3)))
        h = math.sqrt(3) * s
        flat = kind == "HEXAGON"
        across, along = (x1 - x0, y1 - y0) if flat else (y1 - y0, x1 - x0)
        for c in range(-1, int(across / (1.5 * s)) + 2):
            for r in range(-1, int(along / h) + 2):
                a, b = 1.5 * s * c, h * r + (h / 2 if c % 2 else 0)
                corners = [(a + s * math.cos(math.pi / 3 * k), b + s * math.sin(math.pi / 3 * k)) for k in range(6)]
                ring = [(x0 + p, y0 + q) for p, q in corners] if flat else [(x0 + q, y0 + p) for p, q in corners]
                cells.append((r, c, ShapelyPolygon(ring)) if flat else (c, r, ShapelyPolygon(ring)))
    kept = [(r, c, cell) for r, c, cell in cells if cell.intersection(extent).area > 0]
    low_r, low_c = min(r for r, _, _ in kept), min(c for _, c, _ in kept)
    out = _path(Output_Feature_Class)
    _gpkg.create(out, "Polygon", srid, [_gpkg.Column("GRID_ID", "String")])
    with da.InsertCursor(out, ["GRID_ID", "SHAPE@"]) as written:
        for r, c, cell in sorted(kept, key=lambda k: (k[0], k[1])):
            written.insertRow((f"{_letters(r - low_r + 1)}-{c - low_c + 1}", cell))
    return Result(out)


# ---- cleaning -----------------------------------------------------------------------------------------------------------

def Eliminate(in_features, out_feature_class, selection="LENGTH", ex_where_clause=None, ex_features=None):
    """Each selected polygon of a layer merged into the neighbour it shares the longest border with (``LENGTH``) or the
    largest neighbour (``AREA``); the layer must have a selection, as documented. INFERRED: a neighbour shares a border
    of some length; selected polygons and those of ``ex_where_clause`` are not merged into, and the latter are left
    as they are; a polygon with no neighbour to take it stays; ties go to the lowest object ID. ``ex_features`` is not
    built."""
    import shapely
    from . import da
    from ._runtime import ExecuteError
    if _given(ex_features):
        raise NotBuiltError("arcpy.management.Eliminate: ex_features is not built in grapy.")
    layer = _LAYERS.get(_text(in_features))
    if layer is None or not layer.selection:
        raise ExecuteError("ERROR 000187: Eliminate needs a layer with a selection.")
    columns, _, srid, _ = _schema(layer.source)
    eligible = _eligible(layer)
    kept_as_is = _eligible(layer, ex_where_clause) if _given(ex_where_clause) else set()
    features = {oid: [values, shape] for oid, values, shape in _features(layer.source) if oid in eligible}
    chosen = sorted((layer.selection & eligible) - kept_as_is)
    targets = sorted(oid for oid in features if oid not in layer.selection and oid not in kept_as_is)
    by_area = str(selection or "LENGTH").upper() == "AREA"
    gone = set()
    for oid in chosen:
        shape = features[oid][1]
        if shape is None:
            continue
        best, score = None, 0.0
        for other in targets:
            border = features[other][1].boundary.intersection(shape.boundary).length if features[other][1] is not None else 0
            if border <= 0:
                continue
            value = features[other][1].area if by_area else border
            if value > score:
                best, score = other, value
        if best is not None:
            features[best][1] = shapely.union_all([features[best][1], shape])
            gone.add(oid)
    out = _path(out_feature_class)
    _gpkg.create(out, "Polygon", srid, columns)
    with da.InsertCursor(out, [*[c.name for c in columns], "SHAPE@"]) as written:
        for oid in sorted(features):
            if oid not in gone:
                written.insertRow((*features[oid][0], features[oid][1]))
    return Result(out)


def _identical_groups(in_dataset, fields, xy_tolerance, z_tolerance, tool):
    """The sequence number of each object ID's group of identical records, the groups numbered from 1 in the order
    their first record is read. INFERRED: shapes are identical when their normalised vertices are equal exactly."""
    import shapely
    from . import da
    if _given(xy_tolerance) or _given(z_tolerance):
        raise NotBuiltError(f"arcpy.management.{tool}: an xy or z tolerance is not built in grapy; shapes are compared exactly.")
    path, where = _source(in_dataset)
    columns, _, _, geometry = _schema(path)
    by_name = {c.name.lower(): c.name for c in columns}
    wanted = []
    for field in _list(fields):
        if field.lower() in ("shape", geometry.lower()):
            wanted.append("SHAPE@")
        else:
            wanted.append(by_name[field.lower()])
    sequence: dict = {}
    seen: dict = {}
    with da.SearchCursor(path, ["OID@", *wanted], where, sql_clause=(None, f'order by "{_oid_of(path)}"')) as rows:
        for row in rows:
            key = tuple(_shape_key(v._shape) if f == "SHAPE@" and v is not None else v for f, v in zip(wanted, row[1:]))
            sequence[row[0]] = seen.setdefault(key, len(seen) + 1)
    return sequence


def _shape_key(shape):
    """A shape's vertices in a canonical order; a multipart shape of one part is that part, as arcpy has no such
    distinction."""
    import shapely
    if hasattr(shape, "geoms") and len(shape.geoms) == 1:
        shape = shape.geoms[0]
    return shapely.to_wkb(shapely.normalize(shape))


def FindIdentical(in_dataset, out_dataset, fields, xy_tolerance=None, z_tolerance=None, output_record_option="ALL"):
    """A table of ``IN_FID`` and ``FEAT_SEQ``: records equal in every field named (``Shape`` for the shape) share a
    sequence number; ``ONLY_DUPLICATES`` leaves out the records with none like them."""
    from . import da
    sequence = _identical_groups(in_dataset, fields, xy_tolerance, z_tolerance, "FindIdentical")
    sizes: dict = {}
    for group in sequence.values():
        sizes[group] = sizes.get(group, 0) + 1
    only = str(output_record_option or "ALL").upper() == "ONLY_DUPLICATES"
    out = _path(out_dataset)
    _gpkg.create(out, "Geometry", 0, [_gpkg.Column("IN_FID", "Integer"), _gpkg.Column("FEAT_SEQ", "Integer")])
    with da.InsertCursor(out, ["IN_FID", "FEAT_SEQ", "SHAPE@"]) as written:
        for oid in sorted(sequence):
            if not only or sizes[sequence[oid]] > 1:
                written.insertRow((oid, sequence[oid], None))
    return Result(out)


def DeleteIdentical(in_dataset, fields, xy_tolerance=None, z_tolerance=None, out_mapping_table=None):
    """Every record but the first of each set equal in the fields named deleted, as documented. ``out_mapping_table``
    is not built."""
    if _given(out_mapping_table):
        raise NotBuiltError("arcpy.management.DeleteIdentical: out_mapping_table is not built in grapy.")
    sequence = _identical_groups(in_dataset, fields, xy_tolerance, z_tolerance, "DeleteIdentical")
    first: dict = {}
    for oid in sorted(sequence):
        first.setdefault(sequence[oid], oid)
    doomed = sorted(oid for oid in sequence if first[sequence[oid]] != oid)
    path, _ = _source(in_dataset)
    connection, table = _gpkg.table_of(path)
    try:
        from . import _relationships
        oid_name = _gpkg.oid_column(connection, table)
        _relationships.cascade(connection, table, doomed)
        connection.executemany(f'delete from "{table}" where "{oid_name}" = ?', [(o,) for o in doomed])
        connection.commit()
    finally:
        connection.close()
    return Result(str(in_dataset))


def _problems(shape, resolution):
    import shapely
    from shapely.validation import explain_validity
    if shape is None:
        return ["Null geometry"]
    found = []
    parts = _parts(shape)
    if shape.is_empty or any(p.is_empty for p in parts):
        found.append("Empty parts")
    rings = []
    for part in parts:
        if part.is_empty:
            continue
        rings.extend([part.exterior, *part.interiors] if part.geom_type == "Polygon" else
                     [part] if part.geom_type == "LineString" else [])
    coordinates = [list(r.coords) for r in rings]
    if any(a == b for c in coordinates for a, b in zip(c, c[1:])):
        found.append("Duplicate vertex")
    if any(0 < shapely.LineString([a, b]).length < resolution for c in coordinates for a, b in zip(c, c[1:])):
        found.append("Short segment")
    if not shape.is_empty and not shape.is_valid:
        reason = explain_validity(shape)
        if reason.lower().startswith(("self-intersection", "ring self-intersection")):
            found.append("Self intersections")
        else:
            raise NotBuiltError(f"arcpy.management.CheckGeometry: naming the problem '{reason}' is not built in grapy.")
    return found


def CheckGeometry(in_features, out_table=None, validation_method="ESRI"):
    """A table of each problem found: ``CLASS`` (the input's path), ``FEATURE_ID``, ``TEXT_ID`` (a GeoPackage's, as
    documented) and ``PROBLEM``; valid features write nothing. Problems named: ``Null geometry``, ``Empty parts``,
    ``Duplicate vertex``, ``Short segment`` and ``Self intersections``; a shape invalid any other way raises rather
    than going unreported. INFERRED: a segment is short when under the default resolution, 0.0001 m or 1e-9 degrees;
    ``TEXT_ID`` holds the object ID as text. ``OGC`` validation is not built."""
    from . import da
    from ._reference import SpatialReference
    if str(validation_method or "ESRI").upper() != "ESRI":
        raise NotBuiltError(f"arcpy.management.CheckGeometry: validation_method '{validation_method}' is not built in grapy.")
    if not _given(out_table):
        raise NotBuiltError("arcpy.management.CheckGeometry without an out_table is not built in grapy.")
    path, where = _source(in_features)
    srid = _schema(path)[2]
    if srid > 0 and SpatialReference(srid).type == "Geographic":
        resolution = 1e-9
    else:
        resolution = 0.0001 / (_per_unit(srid) or 1.0)
    out = _path(out_table)
    _gpkg.create(out, "Geometry", 0, [_gpkg.Column("CLASS", "String"), _gpkg.Column("FEATURE_ID", "Integer"),
                                      _gpkg.Column("TEXT_ID", "String"), _gpkg.Column("PROBLEM", "String")])
    with da.SearchCursor(path, ["OID@", "SHAPE@"], where) as rows, \
            da.InsertCursor(out, ["CLASS", "FEATURE_ID", "TEXT_ID", "PROBLEM", "SHAPE@"]) as written:
        for oid, shape in rows:
            for problem in _problems(shape._shape if shape is not None else None, resolution):
                written.insertRow((path, oid, str(oid), problem, None))
    return Result(out)


# ---- geometry attributes and references ---------------------------------------------------------------------------------

_AREA_UNITS = _units.ADD_GEOMETRY_AREA

_GEOMETRY_FIELDS = {
    "AREA": ["POLY_AREA"], "AREA_GEODESIC": ["AREA_GEO"], "CENTROID": ["CENTROID_X", "CENTROID_Y"],
    "EXTENT": ["EXT_MIN_X", "EXT_MIN_Y", "EXT_MAX_X", "EXT_MAX_Y"], "LENGTH": ["LENGTH"], "LENGTH_GEODESIC": ["LENGTH_GEO"],
    "LINE_START_MID_END": ["START_X", "START_Y", "MID_X", "MID_Y", "END_X", "END_Y"], "PART_COUNT": ["PART_COUNT"],
    "PERIMETER_LENGTH": ["PERIMETER"], "PERIMETER_LENGTH_GEODESIC": ["PERIM_GEO"], "POINT_COUNT": ["PNT_COUNT"],
    "POINT_X_Y_Z_M": ["POINT_X", "POINT_Y"],
}


def AddGeometryAttributes(Input_Features, Geometry_Properties, Length_Unit=None, Area_Unit=None, Coordinate_System=None):
    """Fields of each shape's measures added and filled, named as documented (``AREA`` → ``POLY_AREA``, ``EXTENT`` →
    ``EXT_MIN_X`` …), in ``Coordinate_System`` when given. Planar measures are in the reference's units unless a unit
    is named; geodesic ones are on the reference's ellipsoid and need a unit. INFERRED: ``POINT_COUNT`` counts a ring's
    closing vertex, as a shape's ``pointCount`` does; a geodesic length runs along the geodesic between each pair of
    vertices. The units are sized as Pro measures them (D-323): ``YARDS`` and ``NAUTICAL_MILES`` are US survey,
    ``ACRES``, ``SQUARE_YARDS`` and ``SQUARE_NAUTICAL_MILES`` international. ``CENTROID_INSIDE``, ``LINE_BEARING`` and
    ``LENGTH_3D`` are not built."""
    import shapely
    from . import da
    from ._reference import SpatialReference, reference_of
    properties = [p.upper() for p in _list(Geometry_Properties)]
    for prop in properties:
        if prop not in _GEOMETRY_FIELDS:
            raise NotBuiltError(f"arcpy.management.AddGeometryAttributes: '{prop}' is not built in grapy.")
    path = _plain("AddGeometryAttributes", Input_Features)
    columns, _, srid, _ = _schema(path)
    target = reference_of(Coordinate_System) if _given(Coordinate_System) else (SpatialReference(srid) if srid > 0 else None)
    code = target.factoryCode if target is not None else 0
    per = _per_unit(code)
    planar_length = planar_area = 1.0
    if _given(Length_Unit) and any(p in ("LENGTH", "PERIMETER_LENGTH") for p in properties):
        if per is None:
            raise NotBuiltError("arcpy.management.AddGeometryAttributes: a planar length in a unit on a geographic system is not built in grapy.")
        planar_length = per / _metres(Length_Unit, "AddGeometryAttributes", _units.ADD_GEOMETRY_LENGTH)
    if _given(Area_Unit):
        if str(Area_Unit).upper() not in _AREA_UNITS:
            raise NotBuiltError(f"arcpy.management.AddGeometryAttributes: Area_Unit '{Area_Unit}' is not built in grapy.")
        if "AREA" in properties:
            if per is None:
                raise NotBuiltError("arcpy.management.AddGeometryAttributes: a planar area in a unit on a geographic system is not built in grapy.")
            planar_area = per ** 2 / _AREA_UNITS[str(Area_Unit).upper()]
    geodesic = [p for p in properties if p.endswith("_GEODESIC")]
    geod = to_geographic = None
    if geodesic:
        if code <= 0:
            raise NotBuiltError("arcpy.management.AddGeometryAttributes: geodesic measures of data with no reference are not built in grapy.")
        if ("AREA_GEODESIC" in properties and not _given(Area_Unit)) or (
                set(geodesic) - {"AREA_GEODESIC"} and not _given(Length_Unit)):
            raise NotBuiltError("arcpy.management.AddGeometryAttributes: a geodesic measure needs its unit named in grapy.")
        from pyproj import CRS, Transformer
        crs = CRS.from_epsg(code)
        geod = crs.get_geod()
        to_geographic = Transformer.from_crs(crs, crs.geodetic_crs, always_xy=True)
    existing = {c.name.lower() for c in columns}
    fields = [f for p in properties for f in _GEOMETRY_FIELDS[p]]
    for name in fields:
        if name.lower() not in existing:
            _gpkg.add_column(path, _gpkg.Column(name, "Integer" if name in ("PART_COUNT", "PNT_COUNT") else "Double"))
            existing.add(name.lower())

    def geographic(shape):
        import numpy
        return shapely.transform(shape, lambda xy: numpy.column_stack(to_geographic.transform(xy[:, 0], xy[:, 1])))

    def measures(shape, prop):
        if prop == "AREA":
            return [shape.area * planar_area]
        if prop == "LENGTH":
            return [shape.length * planar_length]
        if prop == "PERIMETER_LENGTH":
            return [_linework(shape).length * planar_length]
        if prop == "CENTROID":
            return [shape.centroid.x, shape.centroid.y]
        if prop == "EXTENT":
            return list(shape.bounds)
        if prop == "LINE_START_MID_END":
            line = _parts(shape)[0]
            middle = shape.interpolate(0.5, normalized=True) if shape.geom_type == "LineString" else None
            if middle is None:
                raise NotBuiltError("arcpy.management.AddGeometryAttributes: LINE_START_MID_END of a multipart or non-line shape is not built in grapy.")
            first, last = line.coords[0], line.coords[-1]
            return [first[0], first[1], middle.x, middle.y, last[0], last[1]]
        if prop == "PART_COUNT":
            return [len(_parts(shape))]
        if prop == "POINT_COUNT":
            return [len(_coordinates(shape))]
        if prop == "POINT_X_Y_Z_M":
            point = shape if shape.geom_type == "Point" else shape.centroid
            return [point.x, point.y]
        on_earth = geographic(shape)
        if prop == "AREA_GEODESIC":
            return [abs(geod.geometry_area_perimeter(on_earth)[0]) / _AREA_UNITS[str(Area_Unit).upper()]]
        if prop == "LENGTH_GEODESIC":
            return [geod.geometry_length(on_earth) / _metres(Length_Unit, "AddGeometryAttributes", _units.ADD_GEOMETRY_LENGTH)]
        return [geod.geometry_length(_linework(on_earth)) / _metres(Length_Unit, "AddGeometryAttributes", _units.ADD_GEOMETRY_LENGTH)]

    with da.UpdateCursor(path, [*fields, "SHAPE@"]) as rows:
        for row in rows:
            geometry = row[-1]
            if geometry is not None and target is not None and geometry.spatialReference is not None:
                geometry = geometry.projectAs(target)
            shape = geometry._shape if geometry is not None else None
            values = []
            for prop in properties:
                count = len(_GEOMETRY_FIELDS[prop])
                values.extend([None] * count if shape is None or shape.is_empty else measures(shape, prop))
            rows.updateRow([*values, row[-1]])
    return Result(str(Input_Features))


def DefineProjection(in_dataset, coor_system):
    """The dataset's coordinate system recorded as ``coor_system``, its coordinates left as they are, as documented."""
    from . import da
    from ._reference import reference_of
    from ._runtime import AddWarning
    path = _plain("DefineProjection", in_dataset)
    reference = reference_of(coor_system)
    code = reference.factoryCode if reference is not None else 0
    from ._raster import _is_raster_path
    if _is_raster_path(path):
        # A raster's reference is its GeoTIFF's, rewritten in place; its cells and grid are left as they are.
        import rasterio
        from ._runtime import ExecuteError
        if not code:
            raise ExecuteError("ERROR 000622: a raster's coordinate system is set from one with an EPSG code.")
        from . import env
        located = path if os.path.isabs(path) or os.path.exists(path) else os.path.join(env.workspace or "", path)
        with rasterio.open(located, "r+") as raster:
            if raster.crs is not None:
                AddWarning("WARNING 001319: the dataset already had a coordinate system; it is replaced.")
            raster.crs = f"EPSG:{code}"
        return Result(str(in_dataset))
    connection, table = _gpkg.table_of(path)
    try:
        if _gpkg.describe(connection, table)[2] > 0:
            AddWarning("WARNING 001319: the dataset already had a coordinate system; it is replaced.")
        _gpkg._ensure_metadata(connection, code)
        connection.execute("update gpkg_geometry_columns set srs_id = ? where table_name = ?", (code, table))
        connection.execute("update gpkg_contents set srs_id = ? where table_name = ?", (code, table))
        connection.commit()
    finally:
        connection.close()
    with da.UpdateCursor(path, ["SHAPE@WKB"]) as rows:
        for row in rows:
            rows.updateRow(row)
    return Result(str(in_dataset))


def RecalculateFeatureClassExtent(in_features, store_extent="DO_NOT_STORE_EXTENT"):
    """The extent recorded for a feature class worked out again from its shapes."""
    import shapely
    path = _plain("RecalculateFeatureClassExtent", in_features)
    shapes = [s for _, s in _shapes(path, 0) if not s.is_empty]
    bounds = shapely.union_all(shapes).bounds if shapes else (None, None, None, None)
    connection, table = _gpkg.table_of(path)
    try:
        connection.execute("update gpkg_contents set min_x = ?, min_y = ?, max_x = ?, max_y = ? where table_name = ?",
                           (*bounds, table))
        connection.commit()
    finally:
        connection.close()
    return Result(str(in_features))


# ---- workspaces ---------------------------------------------------------------------------------------------------------

def _empty_geopackage(file):
    from ._runtime import ExecuteError, env
    if os.path.exists(file):
        if not env.overwriteOutput:
            raise ExecuteError(f"ERROR 000258: {file} already exists.")
        os.remove(file)
    connection = _gpkg.connect(file)
    try:
        _gpkg._ensure_metadata(connection, 0)
        connection.commit()
    finally:
        connection.close()
    return Result(file)


def CreateFileGDB(out_folder_path, out_name, out_version="CURRENT"):
    """A new, empty workspace. INFERRED: grapy's workspace is a GeoPackage, so ``name.gdb`` is made as ``name.gpkg`` in
    the folder, and that path is the output to write feature classes into (``<path>/<name>``)."""
    from ._runtime import ExecuteError
    folder = _text(out_folder_path)
    if not os.path.isdir(folder):
        raise ExecuteError(f"ERROR 000732: the folder {folder} does not exist.")
    name = _text(out_name)
    stem = name[:-4] if name.lower().endswith(".gdb") else name
    return _empty_geopackage(os.path.join(folder, stem + ".gpkg"))


def CreateSQLiteDatabase(out_database_name, spatial_type="ST_GEOMETRY"):
    """A new, empty SQLite database; only the ``GEOPACKAGE`` kinds are built, each written as a GeoPackage. INFERRED:
    ``.gpkg`` is added when the name has no extension."""
    kind = str(spatial_type or "ST_GEOMETRY").upper()
    if not kind.startswith("GEOPACKAGE"):
        raise NotBuiltError(f"arcpy.management.CreateSQLiteDatabase: spatial_type '{kind}' is not built in grapy; "
                            "GEOPACKAGE is.")
    name = _text(out_database_name)
    return _empty_geopackage(name if os.path.splitext(name)[1] else name + ".gpkg")


def CreateFolder(out_folder_path, out_name):
    """A new folder. Its name must be new, as documented."""
    from ._runtime import ExecuteError
    path = os.path.join(_text(out_folder_path), _text(out_name))
    if os.path.exists(path):
        raise ExecuteError(f"ERROR 000258: {path} already exists.")
    os.makedirs(path)
    return Result(path)


def Copy(in_data, out_data, data_type=None, associated_data=None):
    """Data copied whole: a GeoPackage file or folder to a new path, or a feature class or table to a new table with its
    rows and object IDs as they are. Layers are not copied, as documented."""
    import shutil
    import sqlite3
    from ._runtime import ExecuteError
    if _text(in_data) in _LAYERS:
        raise ExecuteError("ERROR 000732: Copy does not copy layers.")
    source, target = _text(in_data), _text(out_data)
    if os.path.isdir(source):
        shutil.copytree(source, target)
        return Result(target)
    file, table = _gpkg.split(source)
    out_file, out_table = _gpkg.split(target)
    if table is None and out_table is None and not _named_after_its_file(file):
        shutil.copyfile(file, out_file)
        return Result(target)
    connection, table = _gpkg.table_of(source)
    try:
        columns, shape, srid, geometry = _gpkg.describe(connection, table)
        oid = _gpkg.oid_column(connection, table)
    finally:
        connection.close()
    _gpkg.create(target, shape if shape != "Geometry" else "Geometry", srid, columns)
    out_connection, new_table = _gpkg.table_of(target)
    try:
        same = os.path.normcase(os.path.abspath(file)) == os.path.normcase(os.path.abspath(out_file))
        if not same:
            out_connection.execute("attach database ? as source", (file,))
        names = ", ".join(f'"{c.name}"' for c in columns)
        origin = f'"{table}"' if same else f'source."{table}"'
        out_connection.execute(f'insert into "{new_table}" ({_gpkg.OID}, {_gpkg.GEOMETRY}{", " + names if names else ""}) '
                               f'select "{oid}", "{geometry}"{", " + names if names else ""} from {origin}')
        out_connection.commit()
        if not same:
            out_connection.execute("detach database source")
    except sqlite3.Error as error:
        raise ExecuteError(f"ERROR 000732: {error}") from error
    finally:
        out_connection.close()
    return Result(target)


def _named_after_its_file(file):
    """Whether a GeoPackage is one feature class in grapy's sense — a single table named after the file — so that a copy
    names its table after the new file, as CreateFeatureclass and Rename do."""
    connection = _gpkg.connect(file)
    try:
        names = [r[0] for r in connection.execute("select table_name from gpkg_contents")]
    finally:
        connection.close()
    from ._attachments import is_companion
    return [n for n in names if not is_companion(n, names)] == [os.path.splitext(os.path.basename(file))[0]]


def Compact(in_workspace):
    """A GeoPackage's free pages given back (SQLite's VACUUM)."""
    file, _ = _gpkg.split(_text(in_workspace))
    connection = _gpkg.connect(file)
    try:
        connection.execute("vacuum")
    finally:
        connection.close()
    return Result(str(in_workspace))


def AddIndex(in_table, fields, index_name=None, unique="NON_UNIQUE", ascending="NON_ASCENDING"):
    """An attribute index on the fields named. INFERRED: unnamed, it is called ``<table>_<fields>_idx``."""
    path = _plain("AddIndex", in_table)
    connection, table = _gpkg.table_of(path)
    try:
        own = {c.name.lower(): c.name for c in _gpkg.describe(connection, table)[0]}
        names = [own[f.lower()] for f in _list(fields)]
        name = str(index_name) if _given(index_name) else f"{table}_{'_'.join(names)}_idx"
        order = " asc" if str(ascending or "").upper() == "ASCENDING" else ""
        connection.execute(f'create {"unique " if str(unique).upper() == "UNIQUE" else ""}index "{name}" on "{table}" '
                           f'({", ".join(chr(34) + n + chr(34) + order for n in names)})')
        connection.commit()
    finally:
        connection.close()
    return Result(str(in_table))


def RemoveIndex(in_table, index_name):
    """The attribute indexes named dropped."""
    path = _plain("RemoveIndex", in_table)
    connection, _ = _gpkg.table_of(path)
    try:
        for name in _list(index_name):
            connection.execute(f'drop index if exists "{name}"')
        connection.commit()
    finally:
        connection.close()
    return Result(str(in_table))


# ---- attribute domains --------------------------------------------------------------------------------------------------
#
# A domain is kept in the GeoPackage's own schema extension (http://www.geopackage.org/spec/#extension_schema): a coded
# value domain's codes are ``enum`` rows of ``gpkg_data_column_constraints``, each code in ``value`` and its description
# in ``description``; a range domain is one ``range`` row with inclusive ``min`` and ``max``; a field takes a domain by
# naming it in ``gpkg_data_columns.constraint_name``. INFERRED: what the extension has no column for — the domain's
# own description, its field type, its kind before it has a value, and its split and merge policies — is kept in a
# table of grapy's, ``grapy_domains``, so that any GeoPackage reader still sees the codes and ranges.

_SCHEMA_EXTENSION = "http://www.geopackage.org/spec/#extension_schema"


def _workspace_file(in_workspace):
    file, _ = _gpkg.split(_text(in_workspace))
    if not os.path.exists(file):
        raise FileNotFoundError(f"'{file}' does not exist.")
    return file


def _domain_tables(connection):
    connection.execute("""create table if not exists gpkg_data_column_constraints (constraint_name text not null,
        constraint_type text not null, value text, min numeric, min_is_inclusive boolean, max numeric,
        max_is_inclusive boolean, description text, constraint gdcc_ntv unique (constraint_name, constraint_type, value))""")
    connection.execute("""create table if not exists gpkg_data_columns (table_name text not null, column_name text not null,
        name text, title text, description text, mime_type text, constraint_name text,
        constraint pk_gdc primary key (table_name, column_name))""")
    connection.execute("""create table if not exists gpkg_extensions (table_name text, column_name text,
        extension_name text not null, definition text not null, scope text not null,
        constraint ge_tce unique (table_name, column_name, extension_name))""")
    for table in ("gpkg_data_columns", "gpkg_data_column_constraints"):
        connection.execute("insert into gpkg_extensions select ?, null, 'gpkg_schema', ?, 'read-write' where not exists "
                           "(select 1 from gpkg_extensions where table_name = ? and extension_name = 'gpkg_schema')",
                           (table, _SCHEMA_EXTENSION, table))
    connection.execute("""create table if not exists grapy_domains (name text primary key, description text,
        field_type text not null, domain_type text not null, split_policy text, merge_policy text)""")


def _domain(connection, name):
    """(field_type, domain_type) of a domain, or an ExecuteError naming it."""
    from ._runtime import ExecuteError
    _domain_tables(connection)
    row = connection.execute("select field_type, domain_type from grapy_domains where name = ?", (name,)).fetchone()
    if row is None:
        raise ExecuteError(f"ERROR 000800: the domain '{name}' does not exist in this workspace.")
    return row


def _code_text(code):
    """A code as the extension's ``value`` text holds it: a whole number without a decimal point."""
    if isinstance(code, float) and code.is_integer():
        return str(int(code))
    return str(code)


def _code_value(text, field_type):
    if text is None:
        return None
    if field_type in ("SHORT", "LONG", "BIGINTEGER"):
        return int(float(text))
    if field_type in ("FLOAT", "DOUBLE"):
        return float(text)
    return text


def CreateDomain(in_workspace, domain_name, domain_description=None, field_type="SHORT", domain_type="CODED",
                 split_policy="DEFAULT", merge_policy="DEFAULT"):
    """A new attribute domain, coded or range, of a field type. INFERRED: with no field type the domain is ``SHORT``,
    as the tool's dialog starts. ``DATEONLY`` and ``TIMEONLY`` are not built; nor are the range-only policies on a
    coded domain, which the page forbids."""
    from ._runtime import ExecuteError
    kind = str(field_type or "SHORT").upper()
    if kind not in ("SHORT", "LONG", "BIGINTEGER", "FLOAT", "DOUBLE", "TEXT", "DATE"):
        raise NotBuiltError(f"arcpy.management.CreateDomain: field_type '{kind}' is not built in grapy.")
    domain = str(domain_type or "CODED").upper()
    split, merge = str(split_policy or "DEFAULT").upper(), str(merge_policy or "DEFAULT").upper()
    if domain == "CODED" and (split not in ("DEFAULT", "DUPLICATE") or merge != "DEFAULT"):
        raise ExecuteError("ERROR 000800: a coded value domain takes only the DEFAULT or DUPLICATE split policy and the "
                           "DEFAULT merge policy.")
    connection = _gpkg.connect(_workspace_file(in_workspace))
    try:
        _domain_tables(connection)
        if connection.execute("select 1 from grapy_domains where name = ?", (str(domain_name),)).fetchone():
            raise ExecuteError(f"ERROR 000800: the domain '{domain_name}' already exists.")
        connection.execute("insert into grapy_domains values (?, ?, ?, ?, ?, ?)",
                           (str(domain_name), domain_description or "", kind, domain, split, merge))
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_workspace))


def AddCodedValueToDomain(in_workspace, domain_name, code, code_description):
    """A code and its description added to a coded value domain; a code already there is an error."""
    from ._runtime import ExecuteError
    connection = _gpkg.connect(_workspace_file(in_workspace))
    try:
        field_type, kind = _domain(connection, str(domain_name))
        if kind != "CODED":
            raise ExecuteError(f"ERROR 000800: '{domain_name}' is a range domain.")
        text = _code_text(_code_value(_code_text(code), field_type))
        if connection.execute("select 1 from gpkg_data_column_constraints where constraint_name = ? and "
                              "constraint_type = 'enum' and value = ?", (str(domain_name), text)).fetchone():
            raise ExecuteError(f"ERROR 000800: the code '{code}' is already in '{domain_name}'.")
        connection.execute("insert into gpkg_data_column_constraints (constraint_name, constraint_type, value, description) "
                           "values (?, 'enum', ?, ?)", (str(domain_name), text, code_description))
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_workspace))


def DeleteCodedValueFromDomain(in_workspace, domain_name, code):
    """The codes named removed from a coded value domain."""
    connection = _gpkg.connect(_workspace_file(in_workspace))
    try:
        field_type, _ = _domain(connection, str(domain_name))
        for each in _list(code) if not isinstance(code, (int, float)) else [code]:
            connection.execute("delete from gpkg_data_column_constraints where constraint_name = ? and "
                               "constraint_type = 'enum' and value = ?",
                               (str(domain_name), _code_text(_code_value(_code_text(each), field_type))))
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_workspace))


def SetValueForRangeDomain(in_workspace, domain_name, min_value, max_value):
    """A range domain's minimum and maximum set, both inclusive. INFERRED: inclusive, as an Esri range domain admits
    its bounds."""
    from ._runtime import ExecuteError
    connection = _gpkg.connect(_workspace_file(in_workspace))
    try:
        field_type, kind = _domain(connection, str(domain_name))
        if kind != "RANGE":
            raise ExecuteError(f"ERROR 000800: '{domain_name}' is a coded value domain.")
        if field_type == "TEXT":
            raise ExecuteError("ERROR 000800: a text domain cannot be a range.")
        low, high = _code_value(str(min_value), field_type), _code_value(str(max_value), field_type)
        if low > high:
            raise ExecuteError("ERROR 000800: the minimum is greater than the maximum.")
        connection.execute("delete from gpkg_data_column_constraints where constraint_name = ? and constraint_type = 'range'",
                           (str(domain_name),))
        description = connection.execute("select description from grapy_domains where name = ?", (str(domain_name),)).fetchone()[0]
        connection.execute("insert into gpkg_data_column_constraints (constraint_name, constraint_type, value, min, "
                           "min_is_inclusive, max, max_is_inclusive, description) values (?, 'range', null, ?, 1, ?, 1, ?)",
                           (str(domain_name), low, high, description))
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_workspace))


_DOMAIN_FIELD = {"SHORT": "SmallInteger", "LONG": "Integer", "BIGINTEGER": "Integer", "FLOAT": "Single",
                 "DOUBLE": "Double", "TEXT": "String", "DATE": "Date"}


def AssignDomainToField(in_table, field_name, domain_name, subtype_code=None):
    """A domain set on a field — or, with subtype codes, on the field in those subtypes only (ADR-228), kept in
    ``grapy_subtype_domain`` while the field's own domain stays in the schema extension. INFERRED: the field's type
    must be the domain's, a long or big integer domain taking an integer field."""
    from ._runtime import ExecuteError
    path = _plain("AssignDomainToField", in_table)
    connection, table = _gpkg.table_of(path)
    try:
        field_type, _ = _domain(connection, str(domain_name))
        columns = {c.name.lower(): c for c in _gpkg.describe(connection, table)[0]}
        column = columns.get(str(field_name).lower())
        if column is None:
            raise ExecuteError(f"ERROR 000728: the field '{field_name}' does not exist.")
        if _DOMAIN_FIELD[field_type] != column.type:
            raise ExecuteError(f"ERROR 000800: the domain '{domain_name}' is {field_type}; the field '{column.name}' is "
                               f"{column.type}.")
        if _given(subtype_code):
            codes = _subtype_codes(subtype_code)
            _subtyped(connection, table, codes)
            for code in codes:
                connection.execute("insert or replace into grapy_subtype_domain values (?, ?, ?, ?)",
                                   (table, code, column.name, str(domain_name)))
            connection.commit()
            return Result(_text(in_table))
        connection.execute("insert into gpkg_data_columns (table_name, column_name, name, constraint_name) values (?, ?, ?, ?) "
                           "on conflict (table_name, column_name) do update set constraint_name = excluded.constraint_name",
                           (table, column.name, column.name, str(domain_name)))
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_table))


def RemoveDomainFromField(in_table, field_name, subtype_code=None):
    """The domain taken off the fields named — or, with subtype codes, off the fields in those subtypes only (ADR-228),
    which then take the field's own domain again (INFERRED)."""
    path = _plain("RemoveDomainFromField", in_table)
    connection, table = _gpkg.table_of(path)
    try:
        if _given(subtype_code):
            codes = _subtype_codes(subtype_code)
            _subtyped(connection, table, codes)
            for field in _list(field_name):
                for code in codes:
                    connection.execute("delete from grapy_subtype_domain where table_name = ? and subtype_code = ? and "
                                       "lower(field_name) = lower(?)", (table, code, field))
            connection.commit()
            return Result(_text(in_table))
        _domain_tables(connection)
        for field in _list(field_name):
            connection.execute("update gpkg_data_columns set constraint_name = null where table_name = ? and "
                               "lower(column_name) = lower(?)", (table, field))
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_table))


def DeleteDomain(in_workspace, domain_name):
    """A domain deleted. INFERRED: a domain a field still uses cannot be, as a geodatabase refuses it."""
    from ._runtime import ExecuteError
    connection = _gpkg.connect(_workspace_file(in_workspace))
    try:
        _domain(connection, str(domain_name))
        used = connection.execute("select table_name, column_name from gpkg_data_columns where constraint_name = ?",
                                  (str(domain_name),)).fetchone()
        if used:
            raise ExecuteError(f"ERROR 000800: the domain '{domain_name}' is used by {used[0]}.{used[1]}.")
        connection.execute("delete from gpkg_data_column_constraints where constraint_name = ?", (str(domain_name),))
        connection.execute("delete from grapy_domains where name = ?", (str(domain_name),))
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_workspace))


def _coded_values(connection, name):
    return [(r[0], r[1]) for r in connection.execute(
        "select value, description from gpkg_data_column_constraints where constraint_name = ? and constraint_type = 'enum' "
        "order by rowid", (name,))]


def DomainToTable(in_workspace, domain_name, out_table, code_field, description_field, configuration_keyword=None):
    """A coded value domain as a table of codes and descriptions, in the domain's order, the code field of the domain's
    type. INFERRED: the description field is text; a range domain is not built."""
    from . import da
    connection = _gpkg.connect(_workspace_file(in_workspace))
    try:
        field_type, kind = _domain(connection, str(domain_name))
        if kind != "CODED":
            raise NotBuiltError("arcpy.management.DomainToTable of a range domain is not built in grapy.")
        values = _coded_values(connection, str(domain_name))
    finally:
        connection.close()
    out = _table_out("DomainToTable", out_table)
    _gpkg.create(out, "Geometry", 0, [_gpkg.Column(str(code_field), _DOMAIN_FIELD[field_type]),
                                      _gpkg.Column(str(description_field), "String")])
    with da.InsertCursor(out, [str(code_field), str(description_field), "SHAPE@"]) as written:
        for code, description in values:
            written.insertRow((_code_value(code, field_type), description, None))
    return Result(out)


def TableToDomain(in_table, code_field, description_field, in_workspace, domain_name, domain_description=None,
                  update_option="APPEND"):
    """A coded value domain made or updated from a table's codes and descriptions; ``REPLACE`` drops the codes it had.
    INFERRED: a new domain takes the code field's type; an appended code already there takes the table's description."""
    from . import da
    path, where = _source(in_table)
    columns = {c.name.lower(): c for c in _schema(path)[0]}
    code_column = columns[str(code_field).lower()]
    with da.SearchCursor(path, [code_column.name, columns[str(description_field).lower()].name], where) as rows:
        pairs = [(r[0], r[1]) for r in rows if r[0] is not None]
    back = {v: k for k, v in _DOMAIN_FIELD.items() if k != "BIGINTEGER"}
    connection = _gpkg.connect(_workspace_file(in_workspace))
    try:
        _domain_tables(connection)
        if not connection.execute("select 1 from grapy_domains where name = ?", (str(domain_name),)).fetchone():
            connection.execute("insert into grapy_domains values (?, ?, ?, 'CODED', 'DEFAULT', 'DEFAULT')",
                               (str(domain_name), domain_description or "", back.get(code_column.type, "TEXT")))
        field_type, _ = _domain(connection, str(domain_name))
        if str(update_option or "APPEND").upper() == "REPLACE":
            connection.execute("delete from gpkg_data_column_constraints where constraint_name = ? and constraint_type = 'enum'",
                               (str(domain_name),))
        for code, description in pairs:
            text = _code_text(_code_value(_code_text(code), field_type))
            connection.execute("insert into gpkg_data_column_constraints (constraint_name, constraint_type, value, description) "
                               "values (?, 'enum', ?, ?) on conflict (constraint_name, constraint_type, value) do update "
                               "set description = excluded.description", (str(domain_name), text, description))
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_workspace))


def SortCodedValueDomain(in_workspace, domain_name, sort_by, sort_order):
    """A coded value domain's codes put in order of their code or description. INFERRED: codes compare as the domain's
    type, text by code point."""
    connection = _gpkg.connect(_workspace_file(in_workspace))
    try:
        field_type, _ = _domain(connection, str(domain_name))
        values = _coded_values(connection, str(domain_name))
        by_description = str(sort_by).upper() == "DESCRIPTION"
        values.sort(key=lambda v: (v[1] or "") if by_description else _code_value(v[0], field_type),
                    reverse=str(sort_order).upper() == "DESCENDING")
        connection.execute("delete from gpkg_data_column_constraints where constraint_name = ? and constraint_type = 'enum'",
                           (str(domain_name),))
        connection.executemany("insert into gpkg_data_column_constraints (constraint_name, constraint_type, value, description) "
                               "values (?, 'enum', ?, ?)", [(str(domain_name), v, d) for v, d in values])
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_workspace))


def AlterAliasName(table, alias):
    """A table's or feature class's alias set. INFERRED: kept as the GeoPackage's ``identifier``, its human-readable
    name for a table."""
    path = _plain("AlterAliasName", table)
    connection, name = _gpkg.table_of(path)
    try:
        connection.execute("update gpkg_contents set identifier = ? where table_name = ?", (str(alias), name))
        connection.commit()
    finally:
        connection.close()
    return Result(_text(table))


# ---- random points ------------------------------------------------------------------------------------------------------

def _seed():
    """The seed of ``env.randomGenerator`` — ``"<seed> <generator>"`` or a number — and 0 when it is not set.
    INFERRED: grapy draws with NumPy's PCG64 from that seed, so a run repeats itself exactly; it cannot repeat arcpy's
    own generator, whose sequence is not published."""
    from ._runtime import env
    value = getattr(env, "randomGenerator", None)
    if value is None or str(value).strip() == "":
        return 0
    first = str(value).split()[0]
    return int(float(first))


def CreateRandomPoints(out_path, out_name, constraining_feature_class=None, constraining_extent=None,
                       number_of_points_or_field=100, minimum_allowed_distance=None, create_multipoint_output="POINT",
                       multipoint_size=10):
    """Random points: the number asked (or each feature's field value) inside each constraining polygon, along each line,
    as a random subset of each point feature's points, or inside an extent. ``CID`` holds the constraining feature's
    object ID (0 for an extent). INFERRED: the ``CID`` field; the default of 100 points; points are uniform by area or
    by length; draws are from ``env.randomGenerator``'s seed (see ``_seed``). A minimum allowed distance is not built:
    the page reduces the count to *the maximum that can fit* without saying how that is found."""
    import numpy
    import shapely
    from shapely.geometry import MultiPoint, Point as ShapelyPoint, box
    from . import da
    if _given(minimum_allowed_distance) and str(minimum_allowed_distance).split()[0] not in ("0", "0.0"):
        raise NotBuiltError("arcpy.management.CreateRandomPoints: minimum_allowed_distance is not built in grapy.")
    random = numpy.random.default_rng(_seed())
    groups = []
    if _given(constraining_feature_class):
        columns = {c.name.lower(): c.name for c in _schema(_source(constraining_feature_class)[0])[0]}
        names = list(columns.values())
        srid = _schema(_source(constraining_feature_class)[0])[2]
        for oid, values, shape in _features(constraining_feature_class):
            if shape is None or shape.is_empty:
                continue
            count = number_of_points_or_field
            if isinstance(count, str) and count.lower() in columns:
                count = dict(zip((n.lower() for n in names), values))[count.lower()]
            groups.append((oid, shape, max(0, int(round(float(count or 0))))))
    else:
        (x0, y0, x1, y1), srid = _extent_of(constraining_extent)
        groups.append((0, box(x0, y0, x1, y1), max(0, int(round(float(number_of_points_or_field))))))
    points = []
    for oid, shape, count in groups:
        kind = shape.geom_type
        if kind in ("Point", "MultiPoint"):
            parts = _parts(shape)
            chosen = random.choice(len(parts), size=min(count, len(parts)), replace=False) if parts else []
            points.extend((oid, parts[int(i)]) for i in chosen)
        elif kind in ("LineString", "MultiLineString"):
            lines = _parts(shape)
            lengths = numpy.cumsum([l.length for l in lines])
            for at in random.uniform(0, lengths[-1], count):
                i = int(numpy.searchsorted(lengths, at, side="right"))
                i = min(i, len(lines) - 1)
                points.append((oid, lines[i].interpolate(at - (lengths[i - 1] if i else 0.0))))
        else:
            xmin, ymin, xmax, ymax = shape.bounds
            from shapely.prepared import prep
            prepared = prep(shape)
            found = 0
            while found < count:
                xs = random.uniform(xmin, xmax, 2 * (count - found) + 8)
                ys = random.uniform(ymin, ymax, len(xs))
                for x, y in zip(xs, ys):
                    point = ShapelyPoint(x, y)
                    if prepared.contains(point):
                        points.append((oid, point))
                        found += 1
                        if found == count:
                            break
    multi = str(create_multipoint_output or "POINT").upper() == "MULTIPOINT"
    out = _path(_text(out_path), _text(out_name))
    _gpkg.create(out, "Multipoint" if multi else "Point", srid, [_gpkg.Column("CID", "Integer")])
    with da.InsertCursor(out, ["CID", "SHAPE@"]) as written:
        if not multi:
            for oid, point in points:
                written.insertRow((oid, point))
        else:
            size = int(multipoint_size or 10)
            by_group: dict = {}
            for oid, point in points:
                by_group.setdefault(oid, []).append(point)
            for oid, members in by_group.items():
                for i in range(0, len(members), size):
                    written.insertRow((oid, MultiPoint(members[i:i + size])))
    return Result(out)


# ---- rasters ------------------------------------------------------------------------------------------------------------

_PIXEL_TYPES = {"8_BIT_UNSIGNED": "uint8", "8_BIT_SIGNED": "int8", "16_BIT_UNSIGNED": "uint16", "16_BIT_SIGNED": "int16",
                "32_BIT_UNSIGNED": "uint32", "32_BIT_SIGNED": "int32", "32_BIT_FLOAT": "float32", "64_BIT": "float64"}


def _raster_in(value, tool, bands=False):
    from ._raster import as_raster
    return as_raster(value if not isinstance(value, Result) else str(value), f"arcpy.management.{tool}", bands=bands)


def _raster_path(out_raster, tool):
    from ._runtime import env
    path = _text(out_raster)
    if not os.path.isabs(path):
        path = os.path.join(env.workspace, path)
    extension = os.path.splitext(path)[1].lower()
    if not extension:
        path += ".tif"
    elif extension not in (".tif", ".tiff"):
        raise NotBuiltError(f"arcpy.management.{tool}: writing a '{extension}' raster is not built in grapy; GeoTIFF is.")
    return path


def _write_raster(array, mask, grid, path, nodata=None, names=None):
    """A GeoTIFF; NoData written as ``nodata`` or the type's own NoData value. A valid cell holding that value would read
    back as NoData, so it is refused. ``array`` and ``mask`` may be lists, a band each: one band is written as before,
    several as one GeoTIFF of that many bands in their common cell type."""
    import numpy
    import rasterio
    from ._raster import _nodata_for, _saved_dtype
    from ._runtime import ExecuteError
    if isinstance(array, list) and len(array) == 1:
        array, mask = array[0], mask[0]
    if isinstance(array, list):
        dtype = _saved_dtype(numpy.result_type(*[a.dtype for a in array]))
        if nodata is None:
            nodata = _nodata_for(dtype)
        for a, m in zip(array, mask):
            if numpy.any(a[~m] == nodata):
                raise ExecuteError(f"ERROR 999999: a valid cell holds {nodata}, the NoData value the output is written with.")
        profile = {"driver": "GTiff", "height": grid.rows, "width": grid.columns, "count": len(array),
                   "dtype": dtype.name, "transform": grid.transform(), "nodata": nodata}
        if grid.srid:
            profile["crs"] = f"EPSG:{grid.srid}"
        os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
        with rasterio.open(path, "w", **profile) as out:
            for i, (a, m) in enumerate(zip(array, mask), 1):
                out.write(numpy.where(m, nodata, a).astype(dtype), i)
                if names is not None:
                    out.set_band_description(i, names[i - 1])
        return Result(path)
    if nodata is None:
        nodata = _nodata_for(array.dtype)
    if numpy.any(array[~mask] == nodata):
        raise ExecuteError(f"ERROR 999999: a valid cell holds {nodata}, the NoData value the output is written with.")
    profile = {"driver": "GTiff", "height": grid.rows, "width": grid.columns, "count": 1, "dtype": array.dtype.name,
               "transform": grid.transform(), "nodata": nodata}
    if grid.srid:
        profile["crs"] = f"EPSG:{grid.srid}"
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    with rasterio.open(path, "w", **profile) as out:
        out.write(numpy.where(mask, nodata, array).astype(array.dtype), 1)
    return Result(path)


def _cast(array, mask, pixel_type, tool):
    """The cells as a pixel type, refused when a valid cell does not fit it: the page says such values are discarded
    without saying as what."""
    import numpy
    name = _PIXEL_TYPES.get(str(pixel_type).upper())
    if name is None:
        raise NotBuiltError(f"arcpy.management.{tool}: pixel_type '{pixel_type}' is not built in grapy.")
    kind = numpy.dtype(name)
    values = array[~mask]
    if numpy.issubdtype(kind, numpy.integer) and values.size:
        info = numpy.iinfo(kind)
        if numpy.issubdtype(values.dtype, numpy.floating) and not numpy.all(numpy.equal(numpy.mod(values, 1), 0)):
            raise NotBuiltError(f"arcpy.management.{tool}: fractional values in an integer pixel type are not built in grapy.")
        if values.min() < info.min or values.max() > info.max:
            raise NotBuiltError(f"arcpy.management.{tool}: values outside {pixel_type}'s range are not built in grapy.")
    return array.astype(kind)


def _rectangle(rectangle, template):
    if _given(rectangle):
        parts = str(rectangle).replace(",", " ").split()
        return tuple(float(p) for p in parts[:4])
    if hasattr(template, "XMin"):
        return template.XMin, template.YMin, template.XMax, template.YMax
    try:
        raster = _raster_in(template, "Clip")
        extent = raster.extent
        return extent.XMin, extent.YMin, extent.XMax, extent.YMax
    except Exception:
        return _extent_of(template)[0]


def Clip(in_raster, rectangle, out_raster, in_template_dataset=None, nodata_value=None, clipping_geometry="NONE",
         maintain_clipping_extent="NO_MAINTAIN_EXTENT"):
    """A raster cut to a rectangle (``"xmin ymin xmax ymax"``) or a template's extent, keeping the input's cells as they
    lie, as ``NO_MAINTAIN_EXTENT`` documents; ``ClippingGeometry`` also makes NoData every cell outside the template's
    polygons. INFERRED: the cut takes every cell the rectangle reaches into; a cell is inside a polygon when its centre
    is; ``nodata_value`` makes the cells holding it NoData and is the NoData value written. ``MAINTAIN_EXTENT``
    resamples and is not built. A raster of several bands is cut band by band, every band alike, and keeps its bands
    (INFERRED: the page does not single out multiband input)."""
    import numpy
    import shapely
    from ._raster import _Grid
    from ._runtime import ExecuteError
    if str(maintain_clipping_extent or "NO_MAINTAIN_EXTENT").upper() == "MAINTAIN_EXTENT":
        raise NotBuiltError("arcpy.management.Clip: MAINTAIN_EXTENT resamples, which is not built in grapy.")
    raster = _raster_in(in_raster, "Clip", bands=True)
    g = raster._grid
    x0, y0, x1, y1 = _rectangle(rectangle, in_template_dataset)
    c0 = max(0, int(numpy.floor((x0 - g.left) / g.width + 1e-9)))
    c1 = min(g.columns, int(numpy.ceil((x1 - g.left) / g.width - 1e-9)))
    r0 = max(0, int(numpy.floor((g.top - y1) / g.height + 1e-9)))
    r1 = min(g.rows, int(numpy.ceil((g.top - y0) / g.height - 1e-9)))
    if c1 <= c0 or r1 <= r0:
        raise ExecuteError("ERROR 001566: the clipping extent does not overlap the raster.")
    arrays = [a[r0:r1, c0:c1].copy() for a in raster._arrays]
    masks = [m[r0:r1, c0:c1].copy() for m in raster._masks]
    grid = _Grid(g.left + c0 * g.width, g.top - r0 * g.height, g.width, g.height, r1 - r0, c1 - c0, g.srid)
    nodata = None
    if _given(nodata_value):
        nodata = numpy.array(float(nodata_value)).astype(numpy.result_type(*[a.dtype for a in arrays])).item()
        for a, m in zip(arrays, masks):
            m |= a == nodata
    if str(clipping_geometry or "NONE").upper() in ("CLIPPINGGEOMETRY", "CLIPPING_GEOMETRY"):
        if not _given(in_template_dataset):
            raise ExecuteError("ERROR 000735: ClippingGeometry needs a template feature class.")
        shapes = [s for _, s in _shapes(in_template_dataset, g.srid)]
        area = shapely.union_all(shapes)
        xs, ys = grid.centres()
        xx, yy = numpy.meshgrid(xs, ys)
        inside = shapely.covers(area, shapely.points(xx, yy))
        for m in masks:
            m |= ~inside
    return _write_raster(arrays, masks, grid, _raster_path(out_raster, "Clip"), nodata, raster._names)


def GetRasterProperties(in_raster, property_type=None, band_index=None):
    """One property of a raster. INFERRED: ``STD`` is the population form, as arcpy's Raster.standardDeviation;
    ``ANYNODATA`` and ``ALLNODATA`` answer 1 or 0; values are written as text, whole numbers without a decimal point;
    ``band_index`` is one-based or a band's name, and the statistics are the first band's when none is named.
    The sensor and acquisition properties are not built."""
    import numpy
    from ._raster import _band_number
    whole = _raster_in(in_raster, "GetRasterProperties", bands=True)
    raster = whole
    if band_index not in (None, "", "#") or whole.bandCount > 1:
        # INFERRED: band_index is one-based or a band's name; the first band when none is given.
        number = 1 if band_index in (None, "", "#") else _band_number(str(band_index), whole.bandNames,
                                                                       "GetRasterProperties' raster")
        raster = whole._band(number)
    kind = str(property_type or "").upper()
    g = raster._grid
    values = raster._array[~raster._mask]
    codes = {"uint8": 3, "int8": 4, "uint16": 5, "int16": 6, "uint32": 7, "int32": 8, "float32": 9, "float64": 10}
    answers = {
        "MINIMUM": lambda: values.min(), "MAXIMUM": lambda: values.max(), "MEAN": lambda: values.mean(),
        "STD": lambda: values.std(), "UNIQUEVALUECOUNT": lambda: numpy.unique(values).size,
        "TOP": lambda: g.top, "LEFT": lambda: g.left, "RIGHT": lambda: g.right, "BOTTOM": lambda: g.bottom,
        "CELLSIZEX": lambda: g.width, "CELLSIZEY": lambda: g.height, "COLUMNCOUNT": lambda: g.columns,
        "ROWCOUNT": lambda: g.rows, "BANDCOUNT": lambda: whole.bandCount, "ANYNODATA": lambda: int(raster._mask.any()),
        "ALLNODATA": lambda: int(raster._mask.all()),
        "VALUETYPE": lambda: codes.get(raster._array.dtype.name, 10),
    }
    if kind not in answers:
        raise NotBuiltError(f"arcpy.management.GetRasterProperties: property_type '{property_type}' is not built in grapy.")
    if kind in ("MINIMUM", "MAXIMUM", "MEAN", "STD") and values.size == 0:
        raise NotBuiltError(f"arcpy.management.GetRasterProperties: {kind} of a raster with no data is not built in grapy.")
    value = answers[kind]()
    value = value.item() if hasattr(value, "item") else value
    if isinstance(value, float) and value.is_integer():
        value = int(value)
    return Result(str(value))


def CopyRaster(in_raster, out_rasterdataset, config_keyword=None, background_value=None, nodata_value=None,
               onebit_to_eightbit="NONE", colormap_to_RGB="NONE", pixel_type=None, scale_pixel_value="NONE",
               RGB_to_Colormap="NONE", format=None, transform=None, process_as_multidimensional="CURRENT_SLICE",
               build_multidimensional_transpose="NO_TRANSPOSE"):
    """A raster copied, its cells holding ``background_value`` or ``nodata_value`` made NoData, in ``pixel_type`` when
    one is named. INFERRED: ``nodata_value`` is also the NoData value written. A value that does not fit the pixel type,
    scaling, colormaps and formats other than TIFF are not built. A raster of several bands is copied band by band."""
    import numpy
    for name, value in (("onebit_to_eightbit", onebit_to_eightbit), ("colormap_to_RGB", colormap_to_RGB),
                        ("scale_pixel_value", scale_pixel_value), ("RGB_to_Colormap", RGB_to_Colormap)):
        if _given(value) and str(value).upper() != "NONE":
            raise NotBuiltError(f"arcpy.management.CopyRaster: {name} '{value}' is not built in grapy.")
    if _given(format) and str(format).upper() not in ("TIFF", "GTIFF"):
        raise NotBuiltError(f"arcpy.management.CopyRaster: format '{format}' is not built in grapy; TIFF is.")
    if _given(transform) and str(transform).upper() not in ("NONE", "FALSE"):
        raise NotBuiltError("arcpy.management.CopyRaster: transform is not built in grapy.")
    raster = _raster_in(in_raster, "CopyRaster", bands=True)
    arrays, masks = [a.copy() for a in raster._arrays], [m.copy() for m in raster._masks]
    nodata = None
    for value in (background_value, nodata_value):
        if _given(value):
            for a, m in zip(arrays, masks):
                m |= a == numpy.array(float(value)).astype(a.dtype)
    if _given(pixel_type):
        arrays = [_cast(a, m, pixel_type, "CopyRaster") for a, m in zip(arrays, masks)]
    if _given(nodata_value):
        nodata = numpy.array(float(nodata_value)).astype(numpy.result_type(*[a.dtype for a in arrays])).item()
    return _write_raster(arrays, masks, raster._grid, _raster_path(out_rasterdataset, "CopyRaster"), nodata,
                         raster._names)


def MosaicToNewRaster(input_rasters, output_location, raster_dataset_name_with_extension,
                      coordinate_system_for_the_raster=None, pixel_type="8_BIT_UNSIGNED", cellsize=None,
                      number_of_bands=1, mosaic_method="LAST", mosaic_colormap_mode="FIRST"):
    """Rasters on one grid mosaicked into a new one over their combined extent; where they overlap ``mosaic_method``
    decides. INFERRED: the extent is the union of the inputs'; NoData cells take no part, and a cell no input covers is
    NoData; with no pixel type named the output is 8-bit unsigned, as the page warns. Inputs that do not share a cell
    size, alignment and reference need resampling and are refused, as are ``BLEND``, a cell size or a coordinate
    system other than the inputs', and a ``MEAN`` that is not whole in an integer pixel type. With ``number_of_bands``
    above one, each band is mosaicked from the inputs' same band; inputs with another band count are refused."""
    import numpy
    from ._raster import _Grid
    from ._runtime import ExecuteError
    method = str(mosaic_method or "LAST").upper()
    if method not in ("FIRST", "LAST", "MEAN", "MINIMUM", "MAXIMUM", "SUM"):
        raise NotBuiltError(f"arcpy.management.MosaicToNewRaster: mosaic_method '{method}' is not built in grapy.")
    count = int(number_of_bands or 1)
    rasters = [_raster_in(r, "MosaicToNewRaster", bands=True) for r in _list(input_rasters)]
    if any(r.bandCount != count for r in rasters):
        raise NotBuiltError(f"arcpy.management.MosaicToNewRaster: inputs whose band count is not number_of_bands ({count}) "
                            "are not built in grapy; the page does not say which bands fill the output.")
    first = rasters[0]._grid
    for r in rasters[1:]:
        g = r._grid
        if g.srid != first.srid or not numpy.isclose(g.width, first.width) or not numpy.isclose(g.height, first.height):
            raise NotBuiltError("arcpy.management.MosaicToNewRaster: inputs of different cell sizes or references need "
                                "resampling, which is not built in grapy.")
        for offset, size in ((g.left - first.left, first.width), (g.top - first.top, first.height)):
            if not numpy.isclose(offset / size, round(offset / size)):
                raise NotBuiltError("arcpy.management.MosaicToNewRaster: inputs whose cells do not line up need "
                                    "resampling, which is not built in grapy.")
    if _given(cellsize) and not numpy.isclose(float(cellsize), first.width):
        raise NotBuiltError("arcpy.management.MosaicToNewRaster: a cell size other than the inputs' is not built in grapy.")
    if _given(coordinate_system_for_the_raster):
        code = _reference_code(coordinate_system_for_the_raster, first.srid)
        if code != first.srid:
            raise NotBuiltError("arcpy.management.MosaicToNewRaster: a coordinate system other than the inputs' is not "
                                "built in grapy.")
    left = min(r._grid.left for r in rasters)
    top = max(r._grid.top for r in rasters)
    right = max(r._grid.right for r in rasters)
    bottom = min(r._grid.bottom for r in rasters)
    columns = int(round((right - left) / first.width))
    rows = int(round((top - bottom) / first.height))
    grid = _Grid(left, top, first.width, first.height, rows, columns, first.srid)
    folder = _text(output_location)
    if not os.path.isdir(folder):
        raise ExecuteError(f"ERROR 000732: the folder {folder} does not exist.")
    arrays, masks = [], []
    for band in range(count):
        array, mask = _mosaic_band(rasters, band, method, top, left, rows, columns, first)
        arrays.append(_cast(array, mask, pixel_type or "8_BIT_UNSIGNED", "MosaicToNewRaster"))
        masks.append(mask)
    return _write_raster(arrays, masks, grid, _raster_path(os.path.join(folder, _text(raster_dataset_name_with_extension)),
                                                           "MosaicToNewRaster"))


def _mosaic_band(rasters, band, method, top, left, rows, columns, first):
    """One band of the inputs mosaicked over the output grid: its cells and NoData mask."""
    import numpy
    stack = numpy.full((len(rasters), rows, columns), numpy.nan)
    for i, r in enumerate(rasters):
        row0 = int(round((top - r._grid.top) / first.height))
        column0 = int(round((r._grid.left - left) / first.width))
        window = stack[i, row0:row0 + r._grid.rows, column0:column0 + r._grid.columns]
        window[:] = numpy.where(r._masks[band], numpy.nan, r._arrays[band].astype(float))
    present = ~numpy.isnan(stack)
    mask = ~present.any(axis=0)
    with numpy.errstate(all="ignore"):
        if method == "FIRST":
            index = numpy.argmax(present, axis=0)
            result = numpy.take_along_axis(stack, index[None], 0)[0]
        elif method == "LAST":
            index = len(rasters) - 1 - numpy.argmax(present[::-1], axis=0)
            result = numpy.take_along_axis(stack, index[None], 0)[0]
        else:
            reduce = {"MEAN": numpy.nanmean, "MINIMUM": numpy.nanmin, "MAXIMUM": numpy.nanmax, "SUM": numpy.nansum}[method]
            import warnings
            with warnings.catch_warnings():
                warnings.simplefilter("ignore", RuntimeWarning)
                result = reduce(stack, axis=0)
    return numpy.where(mask, 0, result), mask


def Resample(in_raster, out_raster, cell_size=None, resampling_type="NEAREST"):
    """A raster on cells of a new size over the same extent, its lower-left corner where the input's is, as documented;
    ``NEAREST`` gives each new cell the value of the input cell its centre falls in. An extent that is not a whole
    number of new cells is refused (the page rounds without saying which way), as is a centre falling on the edge
    between input cells. ``BILINEAR``, ``CUBIC`` and ``MAJORITY`` are not built."""
    import numpy
    from ._raster import _Grid
    if str(resampling_type or "NEAREST").upper() != "NEAREST":
        raise NotBuiltError(f"arcpy.management.Resample: resampling_type '{resampling_type}' is not built in grapy.")
    raster = _raster_in(in_raster, "Resample")
    g = raster._grid
    if not _given(cell_size):
        raise NotBuiltError("arcpy.management.Resample without a cell size is not built in grapy.")
    if isinstance(cell_size, (int, float)):
        cw = ch = float(cell_size)
    else:
        parts = str(cell_size).split()
        try:
            cw, ch = float(parts[0]), float(parts[-1])
        except ValueError:
            other = _raster_in(cell_size, "Resample")
            cw = ch = other._grid.width
    columns = (g.right - g.left) / cw
    rows = (g.top - g.bottom) / ch
    for n in (columns, rows):
        if abs(n - round(n)) > 1e-9 * max(1.0, n):
            raise NotBuiltError("arcpy.management.Resample: an extent that is not a whole number of new cells is not "
                                "built in grapy.")
    columns, rows = int(round(columns)), int(round(rows))
    grid = _Grid(g.left, g.bottom + rows * ch, cw, ch, rows, columns, g.srid)
    xs, ys = grid.centres()
    fx = (xs - g.left) / g.width
    fy = (g.top - ys) / g.height
    for f in (fx, fy):
        if numpy.any(numpy.isclose(f, numpy.round(f)) & (numpy.round(f) > 0) & (numpy.round(f) < max(g.columns, g.rows))):
            raise NotBuiltError("arcpy.management.Resample: a new cell centre on the edge between input cells is not "
                                "built in grapy; NEAREST does not say which cell it takes.")
    cols = numpy.clip(numpy.floor(fx).astype(int), 0, g.columns - 1)
    rws = numpy.clip(numpy.floor(fy).astype(int), 0, g.rows - 1)
    array = raster._array[numpy.ix_(rws, cols)]
    mask = raster._mask[numpy.ix_(rws, cols)]
    return _write_raster(array, mask, grid, _raster_path(out_raster, "Resample"))


def Flip(in_raster, out_raster):
    """A raster turned upside down about its horizontal centre line; its extent stays."""
    raster = _raster_in(in_raster, "Flip", bands=True)
    return _write_raster([a[::-1].copy() for a in raster._arrays], [m[::-1].copy() for m in raster._masks], raster._grid,
                         _raster_path(out_raster, "Flip"), None, raster._names)


def Mirror(in_raster, out_raster):
    """A raster turned left to right about its vertical centre line; its extent stays."""
    raster = _raster_in(in_raster, "Mirror", bands=True)
    return _write_raster([a[:, ::-1].copy() for a in raster._arrays], [m[:, ::-1].copy() for m in raster._masks],
                         raster._grid, _raster_path(out_raster, "Mirror"), None, raster._names)


def Shift(in_raster, out_raster, x_value, y_value, in_snap_raster=None):
    """A raster moved by ``x_value`` and ``y_value`` in its own units, its cells as they are, as documented. A snap
    raster is not built."""
    from ._raster import _Grid
    if _given(in_snap_raster):
        raise NotBuiltError("arcpy.management.Shift: in_snap_raster is not built in grapy.")
    raster = _raster_in(in_raster, "Shift", bands=True)
    g = raster._grid
    grid = _Grid(g.left + float(x_value), g.top + float(y_value), g.width, g.height, g.rows, g.columns, g.srid)
    return _write_raster([a.copy() for a in raster._arrays], [m.copy() for m in raster._masks], grid,
                         _raster_path(out_raster, "Shift"), None, raster._names)


# ---- workspaces, values and caches ---------------------------------------------------------------------------------------

def CreateFeatureDataset(out_dataset_path, out_name, spatial_reference=None):
    """A feature dataset: a group of feature classes sharing one coordinate system. INFERRED: a GeoPackage has no such
    container, so the dataset is a name prefix — its classes are ``<file>.gpkg/<dataset>/<class>`` tables — and its
    spatial reference is kept in a table of grapy's, ``grapy_feature_datasets``, which CreateFeatureclass reads to give
    a class created there the dataset's reference. The answer is the dataset's path, to create classes in."""
    from ._reference import reference_of
    from ._runtime import ExecuteError
    file, table = _gpkg.split(_text(out_dataset_path))
    if table:
        raise ExecuteError("ERROR 000732: a feature dataset is made in a GeoPackage, not inside a table or dataset.")
    if not os.path.exists(file):
        raise ExecuteError(f"ERROR 000732: '{file}' does not exist.")
    reference = None
    if _given(spatial_reference):
        try:
            reference = reference_of(spatial_reference)
        except Exception:
            code = _schema(_source(spatial_reference)[0])[2]
            reference = reference_of(code) if code > 0 else None
    name = _text(out_name)
    connection = _gpkg.connect(file)
    try:
        connection.execute("create table if not exists grapy_feature_datasets (name text primary key, srs_id integer not null)")
        if connection.execute("select 1 from grapy_feature_datasets where name = ?", (name,)).fetchone():
            raise ExecuteError(f"ERROR 000258: the feature dataset '{name}' already exists.")
        connection.execute("insert into grapy_feature_datasets values (?, ?)", (name, reference.factoryCode if reference else 0))
        connection.commit()
    finally:
        connection.close()
    return Result(_text(out_dataset_path).rstrip("/\\") + "/" + name)


def CalculateValue(expression, code_block=None, data_type="Variant"):
    """A Python expression's value, with the functions of ``code_block`` in scope, converted to ``data_type`` when it
    names a plain type. INFERRED: the output is written as text, as GetCount's is; ``Long``, ``Double``, ``String`` and
    ``Boolean`` convert, other types are kept as computed."""
    scope: dict = {}
    if code_block:
        exec(code_block, scope)  # noqa: S102 — the tool's own code, in the tool's own fenced process (ADR-223 §5.3)
    value = eval(str(expression), scope)  # noqa: S307 — as above
    kind = str(data_type or "Variant").upper()
    convert = {"LONG": int, "DOUBLE": float, "STRING": str, "BOOLEAN": bool}.get(kind)
    if convert is not None:
        value = convert(value)
    return Result(str(value))


def ClearWorkspaceCache(in_data=None):
    """Clears the cache of enterprise geodatabase connections. grapy keeps no connection open between tools, so there
    is nothing to clear and nothing any later answer depends on."""
    return Result(_text(in_data) if in_data is not None else "")


# ---- tables -------------------------------------------------------------------------------------------------------------

def PivotTable(in_table, fields, pivot_field, value_field, out_table):
    """A row for each combination of ``fields``, and a field for each value of ``pivot_field`` holding that row's
    ``value_field``. A numeric pivot value is appended to the pivot field's name, as documented; a combination of
    fields and pivot value that is not unique, or a null pivot value, fails, as documented. INFERRED: a text pivot value
    is the field name itself (a value starting with a digit is not built: the page says its values become 0); rows come
    in the order their combination is first read and pivot fields in ascending order of value; a missing combination
    is null."""
    from . import da
    from ._runtime import ExecuteError
    path, where = _source(in_table)
    columns = {c.name.lower(): c for c in _schema(path)[0]}
    keys = [columns[f.lower()] for f in _list(fields)]
    pivot, value = columns[str(pivot_field).lower()], columns[str(value_field).lower()]
    table: dict = {}
    seen = set()
    with da.SearchCursor(path, [*[k.name for k in keys], pivot.name, value.name], where) as rows:
        for row in rows:
            key, label, cell = tuple(row[:len(keys)]), row[-2], row[-1]
            if label is None:
                raise ExecuteError(f"ERROR 001156: the pivot field '{pivot.name}' holds a null value.")
            if (key, label) in seen:
                raise ExecuteError("ERROR 001156: the combination of input fields and pivot field is not unique.")
            seen.add((key, label))
            table.setdefault(key, {})[label] = cell
    labels = sorted({label for _, label in seen})
    names = []
    for label in labels:
        if isinstance(label, (int, float)):
            names.append(f"{pivot.name}{int(label) if float(label).is_integer() else label}")
        else:
            if not str(label) or str(label)[0].isdigit():
                raise NotBuiltError("arcpy.management.PivotTable: a text pivot value starting with a digit is not built in grapy.")
            names.append(str(label))
    out = _table_out("PivotTable", out_table)
    _gpkg.create(out, "Geometry", 0, [*keys, *[_gpkg.Column(n, value.type) for n in names]])
    with da.InsertCursor(out, [*[k.name for k in keys], *names, "SHAPE@"]) as written:
        for key, cells in table.items():
            written.insertRow((*key, *[cells.get(label) for label in labels], None))
    return Result(out)


_FORMAT_TOKENS = ("unix_hex", "unix_us", "unix_ms", "unix_s", "yyyy", "MMM", "MM", "ddd", "dd", "HH", "hh", "mm", "ss",
                  "tt", "t")
_MONTHS = ("Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec")
_DAYS = ("Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun")


def _tokens(fmt, tool):
    """A custom time format as a list of tokens and literal characters; any other letter is refused."""
    parts, i = [], 0
    while i < len(fmt):
        for token in _FORMAT_TOKENS:
            if fmt.startswith(token, i):
                parts.append(("token", token))
                i += len(token)
                break
        else:
            ch = fmt[i]
            if ch.isalpha() and ch not in "TZ":
                raise NotBuiltError(f"arcpy.management.{tool}: the format letter '{ch}' in '{fmt}' is not built in grapy.")
            parts.append(("literal", ch))
            i += 1
    return parts


def _parse_time(value, fmt, tool):
    """A value read by a custom format, or None when it does not fit it."""
    import datetime
    import re
    if fmt is None:
        try:
            parsed = datetime.datetime.fromisoformat(str(value))
        except ValueError:
            return None
        if parsed.tzinfo is not None:
            raise NotBuiltError(f"arcpy.management.{tool}: times with a UTC offset are not built in grapy.")
        return parsed
    parts = _tokens(fmt, tool)
    if len(parts) == 1 and parts[0][1].startswith("unix_"):
        token = parts[0][1]
        try:
            number = int(str(value), 16) if token == "unix_hex" else float(value)
        except ValueError:
            return None
        scale = {"unix_us": 1e6, "unix_ms": 1e3}.get(token, 1)
        return datetime.datetime(1970, 1, 1) + datetime.timedelta(seconds=number / scale)
    patterns = {"yyyy": r"(?P<yyyy>\d{4})", "MMM": r"(?P<MMM>[A-Za-z]{3})", "MM": r"(?P<MM>\d{1,2})",
                "ddd": r"(?P<ddd>[A-Za-z]{3})", "dd": r"(?P<dd>\d{1,2})", "HH": r"(?P<HH>\d{1,2})",
                "hh": r"(?P<hh>\d{1,2})", "mm": r"(?P<mm>\d{1,2})", "ss": r"(?P<ss>\d{1,2}(?:\.\d+)?)",
                "tt": r"(?P<tt>[AaPp][Mm])", "t": r"(?P<t>[AaPp])"}
    regex = "".join(patterns[p] if kind == "token" else re.escape(p) for kind, p in parts)
    match = re.fullmatch(regex, str(value).strip())
    if not match:
        return None
    got = match.groupdict()
    try:
        month = int(got["MM"]) if got.get("MM") else (_MONTHS.index(got["MMM"].title()) + 1 if got.get("MMM") else 1)
        hour = int(got["HH"]) if got.get("HH") else int(got.get("hh") or 0)
        marker = (got.get("tt") or got.get("t") or "").upper()
        if got.get("hh") and marker:
            hour = hour % 12 + (12 if marker.startswith("P") else 0)
        seconds = float(got.get("ss") or 0)
        return datetime.datetime(int(got.get("yyyy") or 1899), month, int(got.get("dd") or 1), hour, int(got.get("mm") or 0),
                                 int(seconds), int(round((seconds - int(seconds)) * 1e6)))
    except (ValueError, KeyError):
        return None


def _format_time(moment, fmt, tool):
    out = []
    for kind, part in _tokens(fmt, tool):
        if kind == "literal":
            out.append(part)
            continue
        out.append({"yyyy": f"{moment.year:04d}", "MMM": _MONTHS[moment.month - 1], "MM": f"{moment.month:02d}",
                    "ddd": _DAYS[moment.weekday()], "dd": f"{moment.day:02d}", "HH": f"{moment.hour:02d}",
                    "hh": f"{(moment.hour % 12) or 12:02d}", "mm": f"{moment.minute:02d}", "ss": f"{moment.second:02d}",
                    "tt": "AM" if moment.hour < 12 else "PM", "t": "A" if moment.hour < 12 else "P"}.get(part)
                   or _unix(moment, part))
    return "".join(out)


def _unix(moment, token):
    import datetime
    seconds = (moment - datetime.datetime(1970, 1, 1)).total_seconds()
    if token == "unix_hex":
        return format(int(seconds), "x")
    return str(int(round(seconds * {"unix_us": 1e6, "unix_ms": 1e3}.get(token, 1))))


def ConvertTimeField(in_table, input_time_field, input_time_format=None, output_time_field=None, output_time_type="DATE",
                     output_time_format=None, timezone_or_field=None):
    """A new field holding each row's time read from ``input_time_field`` by ``input_time_format`` — the documented
    tokens ``yyyy MM MMM dd ddd HH hh mm ss t tt`` and ``unix_s``, ``unix_ms``, ``unix_us``, ``unix_hex`` — and
    written as ``output_time_type``, by ``output_time_format`` for text and numbers. INFERRED: a value that does not fit
    the format is null; a date is written ``YYYY-MM-DDTHH:MM:SS``, a date only ``YYYY-MM-DD`` and a time only
    ``HH:MM:SS``; unix times are UTC; a part the format lacks is taken from 1899-01-01 00:00:00. Locale prefixes, ``TIMESTAMPOFFSET``, times with a UTC offset and time zones are not
    built."""
    from . import da
    from ._runtime import ExecuteError
    if _given(timezone_or_field):
        raise NotBuiltError("arcpy.management.ConvertTimeField: time zones are not built in grapy.")
    kind = str(output_time_type or "DATE").upper()
    types = {"DATE": "Date", "DATEONLY": "Date", "TIMEONLY": "String", "TEXT": "String", "LONG": "Integer",
             "SHORT": "SmallInteger", "BIGINTEGER": "Integer", "DOUBLE": "Double", "FLOAT": "Single"}
    if kind not in types:
        raise NotBuiltError(f"arcpy.management.ConvertTimeField: output_time_type '{kind}' is not built in grapy.")
    for fmt in (input_time_format, output_time_format):
        if _given(fmt) and ";" in str(fmt):
            raise NotBuiltError("arcpy.management.ConvertTimeField: locale-prefixed formats are not built in grapy.")
    if kind not in ("DATE", "DATEONLY", "TIMEONLY") and not _given(output_time_format):
        raise ExecuteError("ERROR 000735: an output time format is needed for a text or numeric output.")
    path = _plain("ConvertTimeField", in_table)
    columns = {c.name.lower(): c for c in _schema(path)[0]}
    source = columns[str(input_time_field).lower()]
    if str(output_time_field).lower() in columns:
        raise ExecuteError(f"ERROR 000012: the field '{output_time_field}' already exists.")
    fmt = None if source.type == "Date" or not _given(input_time_format) else str(input_time_format)
    if fmt is None and source.type != "Date":
        raise ExecuteError("ERROR 000735: an input time format is needed for a text or numeric field.")
    _gpkg.add_column(path, _gpkg.Column(str(output_time_field), types[kind]))
    with da.UpdateCursor(path, [source.name, str(output_time_field)]) as rows:
        for row in rows:
            raw = row[0]
            if isinstance(raw, float) and raw.is_integer() and fmt and not fmt.startswith("unix_"):
                raw = int(raw)
            moment = _parse_time(raw, fmt, "ConvertTimeField") if raw is not None else None
            if moment is None:
                out = None
            elif kind == "DATE":
                out = moment.isoformat(sep="T", timespec="seconds")
            elif kind == "DATEONLY":
                out = moment.date().isoformat()
            elif kind == "TIMEONLY":
                out = moment.time().isoformat(timespec="seconds")
            else:
                text = _format_time(moment, str(output_time_format), "ConvertTimeField")
                try:
                    out = text if kind == "TEXT" else (float(text) if kind in ("DOUBLE", "FLOAT") else int(text))
                except ValueError:
                    raise NotBuiltError(f"arcpy.management.ConvertTimeField: '{text}' is not a number for {kind}.")
            rows.updateRow([row[0], out])
    return Result(_text(in_table))


# ---- comparing ----------------------------------------------------------------------------------------------------------

def _compare_rows(base, test, sort_field, tolerances, omit, with_shapes, xy_tolerance):
    """Whether two datasets' rows, each sorted ascending by ``sort_field``, hold the same values pair by pair."""
    import shapely
    from . import da
    names = [c.name for c in _schema(_source(base)[0])[0] if c.name.lower() not in omit]
    order = ", ".join(f'"{f}"' for f in _list(sort_field))

    def read(value):
        path, where = _source(value)
        test_names = {c.name.lower(): c.name for c in _schema(path)[0]}
        fields = [test_names.get(n.lower(), n) for n in names] + (["SHAPE@"] if with_shapes else [])
        with da.SearchCursor(path, fields, where, sql_clause=(None, f"order by {order}" if order else None)) as rows:
            return [list(r) for r in rows]

    left, right = read(base), read(test)
    if len(left) != len(right):
        return False
    for a, b in zip(left, right):
        for i, name in enumerate(names):
            x, y = a[i], b[i]
            tolerance = tolerances.get(name.lower())
            if tolerance is not None and x is not None and y is not None:
                if abs(float(x) - float(y)) > tolerance:
                    return False
            elif x != y:
                return False
        if with_shapes:
            x, y = a[-1], b[-1]
            if (x is None) != (y is None):
                return False
            if x is not None and not shapely.equals_exact(shapely.normalize(x._shape), shapely.normalize(y._shape),
                                                          xy_tolerance):
                return False
    return True


def _same_schema(base, test, omit):
    def described(value):
        columns, shape, _, _ = _schema(_source(value)[0])
        return [(c.name.lower(), c.type) for c in columns if c.name.lower() not in omit], shape
    return described(base) == described(test)


def _compare_common(tool, out_compare_file, attribute_tolerances, omit_field):
    if _given(out_compare_file):
        raise NotBuiltError(f"arcpy.management.{tool}: out_compare_file is not built in grapy; its columns and messages "
                            "are not documented.")
    tolerances = {f.lower(): float(t) for f, *rest in _pairs(attribute_tolerances) for t in (rest or ["0"])}
    omit = {f.lower() for f in _list(omit_field)}
    return tolerances, omit


def TableCompare(in_base_table, in_test_table, sort_field, compare_type="ALL", ignore_options=None,
                 attribute_tolerances=None, omit_field=None, continue_compare="NO_CONTINUE_COMPARE", out_compare_file=None):
    """Whether two tables are the same: their schemas (field names and types, omitted fields left out) and their rows,
    both sorted ascending by ``sort_field`` and compared pair by pair, numeric fields within their tolerance. The
    outputs are the base table and ``'true'`` or ``'false'``, as documented. INFERRED: field names compare without
    regard to case and in order; ``ignore_options`` change nothing here, the schema comparison reading field names and
    types only — not aliases, subtypes (ADR-228), extension properties or relationship classes. The compare file is not
    built."""
    tolerances, omit = _compare_common("TableCompare", out_compare_file, attribute_tolerances, omit_field)
    kind = str(compare_type or "ALL").upper()
    same = True
    if kind in ("ALL", "SCHEMA_ONLY"):
        same = _same_schema(in_base_table, in_test_table, omit)
    if same and kind in ("ALL", "ATTRIBUTES_ONLY"):
        same = _compare_rows(in_base_table, in_test_table, sort_field, tolerances, omit, False, 0.0)
    return Result(_text(in_base_table), "true" if same else "false")


def FeatureCompare(in_base_features, in_test_features, sort_field, compare_type="ALL", ignore_options=None,
                   xy_tolerance=None, m_tolerance=None, z_tolerance=None, attribute_tolerances=None, omit_field=None,
                   continue_compare="NO_CONTINUE_COMPARE", out_compare_file=None):
    """Whether two feature classes are the same — schema, spatial reference, geometry and attributes, or one of them as
    ``compare_type`` says — the features paired after sorting both ascending by ``sort_field``, as documented. The
    outputs are the base features and ``'true'`` or ``'false'``. Geometry is compared only between the same spatial
    reference, with no projecting on the fly, as documented. INFERRED: two shapes are equal when, their vertices in a
    canonical order, each pair of vertices lies within ``xy_tolerance``; with none given the tolerance is 0.001 m (or
    its equivalent in the data's units), the default xy tolerance of a geodatabase. The compare file is not built."""
    from ._reference import SpatialReference
    tolerances, omit = _compare_common("FeatureCompare", out_compare_file, attribute_tolerances, omit_field)
    kind = str(compare_type or "ALL").upper()
    base_srid = _schema(_source(in_base_features)[0])[2]
    test_srid = _schema(_source(in_test_features)[0])[2]
    if _given(xy_tolerance):
        tolerance = _linear(xy_tolerance, base_srid, "FeatureCompare")
    elif base_srid > 0 and SpatialReference(base_srid).type == "Geographic":
        tolerance = 8.983152841195214e-09
    else:
        tolerance = 0.001 / (_per_unit(base_srid) or 1.0)
    same = True
    if kind in ("ALL", "SPATIAL_REFERENCE_ONLY", "GEOMETRY_ONLY"):
        same = base_srid == test_srid
    if same and kind in ("ALL", "SCHEMA_ONLY"):
        same = _same_schema(in_base_features, in_test_features, omit)
    if same and kind in ("ALL", "GEOMETRY_ONLY", "ATTRIBUTES_ONLY"):
        with_shapes = kind in ("ALL", "GEOMETRY_ONLY")
        with_values = kind in ("ALL", "ATTRIBUTES_ONLY")
        drop = omit if with_values else omit | {c.name.lower() for c in _schema(_source(in_base_features)[0])[0]}
        same = _compare_rows(in_base_features, in_test_features, sort_field, tolerances, drop, with_shapes, tolerance)
    return Result(_text(in_base_features), "true" if same else "false")


# ---- polygons and lines -------------------------------------------------------------------------------------------------

def _areal(value, srid, tool):
    """An areal unit such as ``"100 SquareMeters"`` in the data's square units; a bare number is in them already."""
    if isinstance(value, (int, float)):
        return float(value)
    parts = str(value).split()
    if len(parts) == 1 or parts[1].upper() == "UNKNOWN":
        return float(parts[0])
    unit = parts[1].upper().replace("_", "")
    per = _per_unit(srid)
    if per is None or unit not in _SQUARE_UNITS:
        raise NotBuiltError(f"arcpy.management.{tool}: an area in {parts[1]} here is not built in grapy.")
    return float(parts[0]) * _SQUARE_UNITS[unit] / per ** 2


def EliminatePolygonPart(in_features, out_feature_class, condition="AREA", part_area=0, part_area_percent=0,
                         part_option="CONTAINED_ONLY"):
    """Small parts and holes removed from polygons: a part or hole is small when its area is under ``part_area``, or
    its share of the feature's total outer area (holes included) under ``part_area_percent``, or both or either, as
    ``condition`` says. A small hole is filled, with any part inside it; with ``CONTAINED_ONLY`` only parts inside
    another part are removed, with ``ANY`` any small part — but when every part is small the largest is kept, as
    documented. INFERRED: a part's area is its outer ring's; fields are carried as they are."""
    import shapely
    from shapely.geometry import Polygon as ShapelyPolygon
    from . import da
    columns, _, srid, _ = _schema(_source(in_features)[0])
    how = str(condition or "AREA").upper()
    limit = _areal(part_area, srid, "EliminatePolygonPart") if _given(part_area) else 0.0
    percent = float(part_area_percent or 0)
    contained_only = str(part_option or "CONTAINED_ONLY").upper() == "CONTAINED_ONLY"

    def small(area, total):
        by_area, by_percent = area < limit, (area / total * 100 if total else 0) < percent
        return {"AREA": by_area, "PERCENT": by_percent, "AREA_AND_PERCENT": by_area and by_percent,
                "AREA_OR_PERCENT": by_area or by_percent}[how]

    out = _path(out_feature_class)
    _gpkg.create(out, "Polygon", srid, columns)
    with da.InsertCursor(out, [*[c.name for c in columns], "SHAPE@"]) as written:
        for _, values, shape in _features(in_features):
            if shape is None or shape.is_empty:
                written.insertRow((*values, shape))
                continue
            parts = _parts(shape)
            total = sum(ShapelyPolygon(p.exterior).area for p in parts)
            kept = []
            for part in parts:
                holes = [h for h in part.interiors if not small(ShapelyPolygon(h).area, total)]
                kept.append(ShapelyPolygon(part.exterior, holes))
            outers = [ShapelyPolygon(p.exterior) for p in kept]
            survivors = []
            for i, part in enumerate(kept):
                inside = any(j != i and outers[j].contains(outers[i]) for j in range(len(kept)))
                covered = inside and any(j != i and kept[j].contains(outers[i]) for j in range(len(kept)))
                if covered:
                    continue                      # it lay in a hole that has been filled
                if small(outers[i].area, total) and (inside or not contained_only):
                    continue
                survivors.append(part)
            if not survivors:
                survivors = [max(kept, key=lambda p: ShapelyPolygon(p.exterior).area)]
            written.insertRow((*values, shapely.MultiPolygon(survivors) if len(survivors) > 1 else survivors[0]))
    return Result(out)


def GenerateTransectsAlongLines(in_features, out_feature_class, interval, transect_length, include_ends="NO_END_POINTS"):
    """Lines across each input line every ``interval`` from its start, ``transect_length`` long in all and centred on
    it, perpendicular to it, with ``ORIG_FID``, as documented. INFERRED: the transects stop short of the end, which
    ``END_POINTS`` adds with the start; at a vertex the transect is square to the segment leaving it (at the end, the
    one arriving). Multipart lines are not built."""
    import math
    from shapely.geometry import LineString
    from . import da
    srid = _schema(_source(in_features)[0])[2]
    step = _linear(interval, srid, "GenerateTransectsAlongLines")
    half = _linear(transect_length, srid, "GenerateTransectsAlongLines") / 2
    ends = str(include_ends or "NO_END_POINTS").upper() == "END_POINTS"
    out = _path(out_feature_class)
    _gpkg.create(out, "Polyline", srid, [_gpkg.Column("ORIG_FID", "Integer")])
    with da.InsertCursor(out, ["ORIG_FID", "SHAPE@"]) as written:
        for oid, _, shape in _features(in_features):
            if shape is None or shape.is_empty:
                continue
            lines = _parts(shape)
            if len(lines) > 1:
                raise NotBuiltError("arcpy.management.GenerateTransectsAlongLines on a multipart line is not built in grapy.")
            line = lines[0]
            coordinates = [c[:2] for c in line.coords]
            stops = _steps(step, line.length)
            if ends:
                stops = sorted({0.0, *stops, line.length})
            for at in stops:
                travelled, segment = 0.0, None
                for a, b in zip(coordinates, coordinates[1:]):
                    piece = math.hypot(b[0] - a[0], b[1] - a[1])
                    if at < travelled + piece or (a, b) == (coordinates[-2], coordinates[-1]):
                        segment = (a, b)
                        break
                    travelled += piece
                (x0, y0), (x1, y1) = segment
                length = math.hypot(x1 - x0, y1 - y0)
                nx, ny = -(y1 - y0) / length, (x1 - x0) / length
                centre = line.interpolate(at)
                written.insertRow((oid, LineString([(centre.x + nx * half, centre.y + ny * half),
                                                    (centre.x - nx * half, centre.y - ny * half)])))
    return Result(out)


# ---- raster datasets ----------------------------------------------------------------------------------------------------

def Mosaic(inputs, target, mosaic_type="LAST", colormap="FIRST", background_value=None, nodata_value=None,
           onebit_to_eightbit="NONE", mosaicking_tolerance=0, MatchingMethod="NONE"):
    """Rasters mosaicked into an existing raster, the target's own cells taking part first; where cells overlap
    ``mosaic_type`` decides, and cells holding ``background_value`` or ``nodata_value`` become NoData, as documented.
    INFERRED: the target keeps its extent (the page grows it only for CRF), so inputs are cut to it; NoData cells take
    no part; a ``MEAN`` that is not whole in an integer target is not built. Inputs that do not share the target's
    cells need resampling and are refused, as are ``BLEND`` and colour matching."""
    import numpy
    method = str(mosaic_type or "LAST").upper()
    if method not in ("FIRST", "LAST", "MEAN", "MINIMUM", "MAXIMUM", "SUM"):
        raise NotBuiltError(f"arcpy.management.Mosaic: mosaic_type '{method}' is not built in grapy.")
    if _given(MatchingMethod) and str(MatchingMethod).upper() != "NONE":
        raise NotBuiltError("arcpy.management.Mosaic: MatchingMethod is not built in grapy.")
    if _given(onebit_to_eightbit) and str(onebit_to_eightbit).upper() != "NONE":
        raise NotBuiltError("arcpy.management.Mosaic: onebit_to_eightbit is not built in grapy.")
    base = _raster_in(target, "Mosaic")
    g = base._grid
    layers = [numpy.where(base._mask, numpy.nan, base._array.astype(float))]
    for each in _list(inputs):
        r = _raster_in(each, "Mosaic")
        h = r._grid
        if h.srid != g.srid or not numpy.isclose(h.width, g.width) or not numpy.isclose(h.height, g.height):
            raise NotBuiltError("arcpy.management.Mosaic: inputs of another cell size or reference need resampling, "
                                "which is not built in grapy.")
        dx, dy = (h.left - g.left) / g.width, (g.top - h.top) / g.height
        if not (numpy.isclose(dx, round(dx)) and numpy.isclose(dy, round(dy))):
            raise NotBuiltError("arcpy.management.Mosaic: inputs whose cells do not line up need resampling, which is "
                                "not built in grapy.")
        dx, dy = int(round(dx)), int(round(dy))
        layer = numpy.full((g.rows, g.columns), numpy.nan)
        r0, c0 = max(0, dy), max(0, dx)
        r1, c1 = min(g.rows, dy + h.rows), min(g.columns, dx + h.columns)
        if r1 > r0 and c1 > c0:
            piece = numpy.where(r._mask, numpy.nan, r._array.astype(float))
            layer[r0:r1, c0:c1] = piece[r0 - dy:r1 - dy, c0 - dx:c1 - dx]
        layers.append(layer)
    stack = numpy.stack(layers)
    present = ~numpy.isnan(stack)
    mask = ~present.any(axis=0)
    import warnings
    with warnings.catch_warnings():
        warnings.simplefilter("ignore", RuntimeWarning)
        if method == "FIRST":
            result = numpy.take_along_axis(stack, numpy.argmax(present, axis=0)[None], 0)[0]
        elif method == "LAST":
            result = numpy.take_along_axis(stack, (len(layers) - 1 - numpy.argmax(present[::-1], axis=0))[None], 0)[0]
        else:
            result = {"MEAN": numpy.nanmean, "MINIMUM": numpy.nanmin, "MAXIMUM": numpy.nanmax, "SUM": numpy.nansum}[method](stack, axis=0)
    result = numpy.where(mask, 0, result)
    for value in (background_value, nodata_value):
        if _given(value):
            mask |= result == float(value)
    kind = base._array.dtype
    if numpy.issubdtype(kind, numpy.integer) and not numpy.all(numpy.mod(result[~mask], 1) == 0):
        raise NotBuiltError("arcpy.management.Mosaic: fractional values in an integer target are not built in grapy.")
    return _write_raster(result.astype(kind), mask, g, base.catalogPath)


def _band_tags(path):
    import rasterio
    with rasterio.open(path) as read:
        return read.tags(1)


def CalculateStatistics(in_raster_dataset, x_skip_factor=1, y_skip_factor=1, ignore_values=None,
                        skip_existing="OVERWRITE", area_of_interest=None):
    """A raster's statistics stored with it — minimum, maximum, mean and standard deviation of every ``x_skip_factor``
    -th column and ``y_skip_factor``-th row, NoData and ``ignore_values`` left out. INFERRED: they are stored as the
    GeoTIFF band's ``STATISTICS_*`` metadata, which GDAL-based readers use, and the standard deviation is the
    population form. No cell changes. An area of interest is not built."""
    import numpy
    import rasterio
    if _given(area_of_interest):
        raise NotBuiltError("arcpy.management.CalculateStatistics: area_of_interest is not built in grapy.")
    raster = _raster_in(in_raster_dataset, "CalculateStatistics")
    path = raster.catalogPath
    if str(skip_existing or "OVERWRITE").upper() == "SKIP_EXISTING" and "STATISTICS_MINIMUM" in _band_tags(path):
        return Result(_text(in_raster_dataset))
    xs, ys = int(x_skip_factor or 1), int(y_skip_factor or 1)
    array, mask = raster._array[::ys, ::xs], raster._mask[::ys, ::xs].copy()
    for value in _list(ignore_values) if not isinstance(ignore_values, (int, float)) else [ignore_values]:
        mask |= array == float(value)
    values = array[~mask].astype(float)
    tags = {} if values.size == 0 else {
        "STATISTICS_MINIMUM": repr(float(values.min())), "STATISTICS_MAXIMUM": repr(float(values.max())),
        "STATISTICS_MEAN": repr(float(values.mean())), "STATISTICS_STDDEV": repr(float(values.std()))}
    with rasterio.open(path, "r+") as dataset:
        dataset.update_tags(1, **tags)
    return Result(_text(in_raster_dataset))


def BuildPyramids(in_raster_dataset, pyramid_level=-1, SKIP_FIRST="NONE", resample_technique="NEAREST",
                  compression_type="DEFAULT", compression_quality=75, skip_existing="OVERWRITE"):
    """Reduced-resolution overviews for display, as the page says pyramids are, built into the GeoTIFF; the cells
    themselves do not change. INFERRED: each level halves the last; ``-1`` builds levels until the smaller is no more
    than 256 cells on its longer side; ``SKIP_FIRST`` leaves out the half-size level; the compression options are the
    file's own. Deleting pyramids (level 0) is not built."""
    import rasterio
    from rasterio.enums import Resampling
    level = int(pyramid_level if pyramid_level not in (None, "", "#") else -1)
    if level == 0:
        raise NotBuiltError("arcpy.management.BuildPyramids: deleting pyramids (pyramid_level 0) is not built in grapy.")
    technique = {"NEAREST": Resampling.nearest, "BILINEAR": Resampling.bilinear, "CUBIC": Resampling.cubic}.get(
        str(resample_technique or "NEAREST").upper())
    if technique is None:
        raise NotBuiltError(f"arcpy.management.BuildPyramids: resample_technique '{resample_technique}' is not built in grapy.")
    raster = _raster_in(in_raster_dataset, "BuildPyramids")
    with rasterio.open(raster.catalogPath, "r+") as dataset:
        if str(skip_existing or "OVERWRITE").upper() == "SKIP_EXISTING" and dataset.overviews(1):
            return Result(_text(in_raster_dataset))
        longest = max(dataset.width, dataset.height)
        factors, factor = [], 2
        while (level == -1 and longest / factor >= 1 and (not factors or longest / factors[-1] > 256)) or \
                (level > 0 and len(factors) < level):
            factors.append(factor)
            factor *= 2
        if str(SKIP_FIRST or "NONE").upper() == "SKIP_FIRST":
            factors = factors[1:]
        if factors:
            dataset.build_overviews(factors, technique)
    return Result(_text(in_raster_dataset))


# ---- fields, domains and files ------------------------------------------------------------------------------------------

def _set_alias(path, field, alias):
    """A field's alias, kept as the GeoPackage schema extension's ``title`` of the column (None clears it). INFERRED:
    ``title`` is the extension's human-readable name of a column, which is what an alias is."""
    connection, table = _gpkg.table_of(path)
    try:
        _domain_tables(connection)
        connection.execute("insert into gpkg_data_columns (table_name, column_name, name, title) values (?, ?, ?, ?) "
                           "on conflict (table_name, column_name) do update set title = excluded.title",
                           (table, field, field, alias))
        connection.commit()
    finally:
        connection.close()


def _rename_field(path, field, new_name):
    connection, table = _gpkg.table_of(path)
    try:
        connection.execute(f'alter table "{table}" rename column "{field}" to "{new_name}"')
        if connection.execute("select 1 from sqlite_master where name = 'gpkg_data_columns'").fetchone():
            connection.execute("update gpkg_data_columns set column_name = ?, name = ? where table_name = ? and column_name = ?",
                               (new_name, new_name, table, field))
        connection.commit()
    finally:
        connection.close()


def AlterField(in_table, field, new_field_name=None, new_field_alias=None, field_type=None, field_length=None,
               field_is_nullable=None, clear_field_alias=None):
    """A field renamed, and its alias set or cleared (kept as the schema extension's column title, see ``_set_alias``).
    Changing a field's type, length or nullability is not built."""
    for name, value in (("field_type", field_type), ("field_length", field_length), ("field_is_nullable", field_is_nullable)):
        if _given(value):
            raise NotBuiltError(f"arcpy.management.AlterField: {name} is not built in grapy; a GeoPackage column's type "
                                "cannot change without rebuilding its table.")
    path = _plain("AlterField", in_table)
    own = {c.name.lower(): c.name for c in _schema(path)[0]}
    current = own.get(str(field).lower())
    if current is None:
        from ._runtime import ExecuteError
        raise ExecuteError(f"ERROR 000728: the field '{field}' does not exist.")
    if _given(new_field_name) and new_field_name != current:
        _rename_field(path, current, str(new_field_name))
        current = str(new_field_name)
    if str(clear_field_alias or "").upper() in ("TRUE", "CLEAR_ALIAS"):
        _set_alias(path, current, None)
    elif _given(new_field_alias):
        _set_alias(path, current, str(new_field_alias))
    return Result(_text(in_table))


def AlterFields(in_table, field_description=None):
    """Several fields altered at once, each ``[field, new name, new alias, new type, new length, nullable, clear
    alias]`` with ``#`` for what stays, as AlterField alters one."""
    for entry in _pairs(field_description) if field_description else []:
        entry = list(entry) + ["#"] * (7 - len(entry))
        AlterField(in_table, entry[0], entry[1] if _given(entry[1]) else None, entry[2] if _given(entry[2]) else None,
                   entry[3] if _given(entry[3]) else None, entry[4] if _given(entry[4]) else None,
                   entry[5] if _given(entry[5]) else None, entry[6] if _given(entry[6]) else None)
    return Result(_text(in_table))


def AlterDomain(in_workspace, domain_name, new_domain_name=None, new_domain_description=None, split_policy=None,
                merge_policy=None, new_domain_owner=None):
    """A domain renamed, described anew, or given new split and merge policies — the range-only policies refused on a
    coded value domain, as documented. A domain owner is not built: a GeoPackage has no database users."""
    from ._runtime import ExecuteError
    if _given(new_domain_owner):
        raise NotBuiltError("arcpy.management.AlterDomain: new_domain_owner is not built in grapy; a GeoPackage has no users.")
    connection = _gpkg.connect(_workspace_file(in_workspace))
    try:
        _, kind = _domain(connection, str(domain_name))
        split, merge = (str(split_policy).upper() if _given(split_policy) else None,
                        str(merge_policy).upper() if _given(merge_policy) else None)
        if kind == "CODED" and (split not in (None, "DEFAULT", "DUPLICATE") or merge not in (None, "DEFAULT")):
            raise ExecuteError("ERROR 000800: a coded value domain takes only the DEFAULT or DUPLICATE split policy and "
                               "the DEFAULT merge policy.")
        name = str(domain_name)
        if _given(new_domain_description):
            connection.execute("update grapy_domains set description = ? where name = ?", (str(new_domain_description), name))
            connection.execute("update gpkg_data_column_constraints set description = ? where constraint_name = ? and "
                               "constraint_type = 'range'", (str(new_domain_description), name))
        if split:
            connection.execute("update grapy_domains set split_policy = ? where name = ?", (split, name))
        if merge:
            connection.execute("update grapy_domains set merge_policy = ? where name = ?", (merge, name))
        if _given(new_domain_name) and str(new_domain_name) != name:
            if connection.execute("select 1 from grapy_domains where name = ?", (str(new_domain_name),)).fetchone():
                raise ExecuteError(f"ERROR 000800: the domain '{new_domain_name}' already exists.")
            for statement in ("update grapy_domains set name = ? where name = ?",
                              "update gpkg_data_column_constraints set constraint_name = ? where constraint_name = ?",
                              "update gpkg_data_columns set constraint_name = ? where constraint_name = ?"):
                connection.execute(statement, (str(new_domain_name), name))
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_workspace))


def FileCompare(in_base_file, in_test_file, file_type="ASCII", continue_compare="NO_CONTINUE_COMPARE", out_compare_file=None):
    """Whether two files are the same, byte for byte (``BINARY``) or character for character (``ASCII``); the outputs
    are the base file and ``'true'`` or ``'false'``, as documented. The ASCII comparison's masking symbols (``#``,
    ``??``, ``?!``, ``???``) are not built — the page does not define what each masks — so a base file holding one is
    refused, as is the compare file, whose columns are not documented."""
    if _given(out_compare_file):
        raise NotBuiltError("arcpy.management.FileCompare: out_compare_file is not built in grapy; its columns are not documented.")
    with open(_text(in_base_file), "rb") as read:
        base = read.read()
    with open(_text(in_test_file), "rb") as read:
        test = read.read()
    if str(file_type or "ASCII").upper() == "ASCII" and (b"#" in base or b"??" in base or b"?!" in base):
        raise NotBuiltError("arcpy.management.FileCompare: the base file holds a masking symbol, whose meaning the page "
                            "does not define; not built in grapy.")
    return Result(_text(in_base_file), "true" if base == test else "false")


# ---- rasters ------------------------------------------------------------------------------------------------------------

def GetCellValue(in_raster, location_point, band_index=None):
    """The value of the cell at ``"x y"``, as text. INFERRED: a NoData cell answers ``NoData``; a point outside the
    raster is an error; a whole value is written without a decimal point. Bands other than the first are not built."""
    from ._runtime import ExecuteError
    if _given(band_index) and str(band_index).strip() not in ("1", "Band_1"):
        raise NotBuiltError("arcpy.management.GetCellValue: bands other than the first are not built in grapy.")
    raster = _raster_in(in_raster, "GetCellValue")
    x, y = _xy(location_point)
    cell = raster._grid.cell_of(x, y)
    if cell is None:
        raise ExecuteError(f"ERROR 001143: the point {x} {y} is outside the raster.")
    if raster._mask[cell]:
        return Result("NoData")
    value = raster._array[cell].item()
    return Result(str(int(value)) if float(value).is_integer() else repr(float(value)))


def ExportRasterWorldFile(in_raster_dataset):
    """A world file beside the raster: its six lines are the cell width, the two rotation terms, the negative cell
    height and the x and y of the centre of the upper-left cell, as Esri's "World files for raster datasets" page
    describes them; the file's extension is the raster's first and last letters followed by ``w`` (``.tif`` →
    ``.tfw``), as that page says."""
    raster = _raster_in(in_raster_dataset, "ExportRasterWorldFile")
    g = raster._grid
    stem, extension = os.path.splitext(raster.catalogPath)
    world = stem + "." + extension[1] + extension[-1] + "w"
    lines = [g.width, 0.0, 0.0, -g.height, g.left + g.width / 2, g.top - g.height / 2]
    with open(world, "w", encoding="ascii", newline="\n") as written:
        written.write("\n".join(repr(float(v)) for v in lines) + "\n")
    return Result(world)


def BatchProject(Input_Feature_Class_or_Dataset, Output_Workspace, Output_Coordinate_System=None, Template_dataset=None,
                 Transformation=None):
    """Each feature class projected into a workspace under its own name, ``_1``, ``_2`` … added when the name is taken,
    as documented; the coordinate system is the one given, a template's, or ``env.outputCoordinateSystem``. Both a
    system and a template (the page does not say which wins) and a transformation are not built."""
    from ._runtime import ExecuteError, env
    if _given(Transformation):
        raise NotBuiltError("arcpy.management.BatchProject: Transformation is not built in grapy.")
    if _given(Output_Coordinate_System) and _given(Template_dataset):
        raise NotBuiltError("arcpy.management.BatchProject with both a coordinate system and a template is not built in "
                            "grapy; the page does not say which wins.")
    if _given(Output_Coordinate_System):
        target = Output_Coordinate_System
    elif _given(Template_dataset):
        target = _schema(_source(Template_dataset)[0])[2]
    elif env.outputCoordinateSystem is not None:
        target = env.outputCoordinateSystem
    else:
        raise ExecuteError("ERROR 000735: BatchProject needs a coordinate system or a template.")
    workspace = _text(Output_Workspace)
    from . import Exists
    for each in _list(Input_Feature_Class_or_Dataset):
        stem = _stem_of(_source(each)[0])
        name, i = stem, 0
        while True:
            out = f"{workspace}/{name}" if workspace.lower().endswith(".gpkg") else os.path.join(workspace, name + ".gpkg")
            if not Exists(out):
                break
            i += 1
            name = f"{stem}_{i}"
        Project(each, out, target)
    return Result(workspace)


def BatchCalculateStatistics(Input_Raster_Datasets, Number_of_columns_to_skip=1, Number_of_rows_to_skip=1,
                             Ignore_values=None, Skip_Existing="OVERWRITE"):
    """CalculateStatistics on each raster."""
    for each in _list(Input_Raster_Datasets):
        CalculateStatistics(each, Number_of_columns_to_skip, Number_of_rows_to_skip, Ignore_values, Skip_Existing)
    return Result("true")


def BatchBuildPyramids(Input_Raster_Datasets, Pyramid_levels=-1, Skip_first_level="NONE", Pyramid_resampling_technique="NEAREST",
                       Pyramid_compression_type="DEFAULT", Compression_quality=75, Skip_Existing="OVERWRITE"):
    """BuildPyramids on each raster; the output is ``true``, as the page's Boolean."""
    for each in _list(Input_Raster_Datasets):
        BuildPyramids(each, Pyramid_levels, Skip_first_level, Pyramid_resampling_technique, Pyramid_compression_type,
                      Compression_quality, Skip_Existing)
    return Result("true")


def BuildPyramidsandStatistics(in_workspace, include_subdirectories="INCLUDE_SUBDIRECTORIES", build_pyramids="BUILD_PYRAMIDS",
                               calculate_statistics="CALCULATE_STATISTICS", BUILD_ON_SOURCE="NONE", block_field=None,
                               estimate_statistics="NONE", x_skip_factor=1, y_skip_factor=1, ignore_values=None,
                               pyramid_level=-1, SKIP_FIRST="NONE", resample_technique="NEAREST", compression_type="DEFAULT",
                               compression_quality=75, skip_existing="OVERWRITE", where_clause=None, sips_mode=None):
    """Pyramids and statistics for every GeoTIFF in a folder, and its subfolders unless ``NONE``. The parameters for
    a mosaic dataset as the workspace (``BUILD_ON_SOURCE``, ``block_field``, ``estimate_statistics``, ``where_clause``)
    are refused when set: grapy reads a mosaic dataset's items at their own cell size and builds no pyramids or
    estimated statistics for them (ADR-230 §5.5)."""
    for name, value in (("BUILD_ON_SOURCE", BUILD_ON_SOURCE), ("estimate_statistics", estimate_statistics),
                        ("block_field", block_field), ("where_clause", where_clause), ("sips_mode", sips_mode)):
        if _given(value) and str(value).upper() != "NONE":
            raise NotBuiltError(f"arcpy.management.BuildPyramidsandStatistics: {name} is for a mosaic dataset as the "
                                "workspace, which is not built in grapy (ADR-230 §5.5).")
    folder = _text(in_workspace)
    deep = str(include_subdirectories or "INCLUDE_SUBDIRECTORIES").upper() != "NONE"
    found = []
    for root, folders, files in os.walk(folder):
        found.extend(os.path.join(root, f) for f in sorted(files) if f.lower().endswith((".tif", ".tiff")))
        if not deep:
            break
    for each in found:
        if str(build_pyramids or "BUILD_PYRAMIDS").upper() == "BUILD_PYRAMIDS":
            BuildPyramids(each, pyramid_level, SKIP_FIRST, resample_technique, compression_type, compression_quality,
                          skip_existing)
        if str(calculate_statistics or "CALCULATE_STATISTICS").upper() == "CALCULATE_STATISTICS":
            CalculateStatistics(each, x_skip_factor, y_skip_factor, ignore_values, skip_existing)
    return Result(folder)


def AlterCodedValueDescription(in_workspace, domain_name, code, new_description):
    """A coded value's description changed; a range domain is refused, as documented."""
    from ._runtime import ExecuteError
    connection = _gpkg.connect(_workspace_file(in_workspace))
    try:
        field_type, kind = _domain(connection, str(domain_name))
        if kind != "CODED":
            raise ExecuteError(f"ERROR 000800: '{domain_name}' is a range domain.")
        text = _code_text(_code_value(_code_text(code), field_type))
        changed = connection.execute("update gpkg_data_column_constraints set description = ? where constraint_name = ? and "
                                     "constraint_type = 'enum' and value = ?", (new_description, str(domain_name), text)).rowcount
        if not changed:
            raise ExecuteError(f"ERROR 000800: the code '{code}' is not in '{domain_name}'.")
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_workspace))


def _drop_models(connection, table):
    """INFERRED: a table that is deleted takes with it the relationship classes it takes part in (their relationship
    tables and Related Tables mappings) and its place, rules and errors in any topology (ADR-225); its attachment
    table, global IDs, subtypes and defaults go too (ADR-228); a mosaic dataset's rows in grapy's tables go with its
    catalog (ADR-230)."""
    from . import _attachments, _mosaic, _relationships, _subtypes
    _attachments.drop(connection, table)
    _subtypes.drop(connection, table)
    _mosaic.drop(connection, table)
    for rc in _relationships.classes(connection, origin=table) + _relationships.classes(connection, destination=table):
        for extra in (rc["relationship_table"], f'{rc["name"]}_rte'):
            if extra:
                connection.execute(f'drop table if exists "{extra}"')
                connection.execute("delete from gpkg_contents where table_name = ?", (extra,))
                connection.execute("delete from gpkg_geometry_columns where table_name = ?", (extra,))
        connection.execute("delete from gpkgext_relations where mapping_table_name = ?", (f'{rc["name"]}_rte',))
        connection.execute("delete from grapy_relationship_rule where relationship_class = ?", (rc["name"],))
        connection.execute("delete from grapy_relationship_class where name = ?", (rc["name"],))
    if connection.execute("select 1 from sqlite_master where name = 'grapy_topology_class'").fetchone():
        rules = [r[0] for r in connection.execute("select rule_id from grapy_topology_rule where origin = ? or destination = ?",
                                                  (table, table))]
        for rule_id in rules:
            connection.execute("delete from grapy_topology_error where rule_id = ?", (rule_id,))
            connection.execute("delete from grapy_topology_rule where rule_id = ?", (rule_id,))
        connection.execute("delete from grapy_topology_class where class_name = ?", (table,))


def DeleteMultiple(in_data):
    """Each item deleted, as Delete deletes one: ``[[data, {type}], …]`` or ``"a;b"``; the output is ``true``."""
    kinds = ("FEATURECLASS", "FEATUREDATASET", "MOSAICDATASET", "PARCELFABRIC", "RELATIONSHIPCLASS", "TOPOLOGY")
    for entry in _list(in_data):
        if isinstance(entry, (list, tuple)):
            Delete(entry[0], entry[1] if len(entry) > 1 else None)
            continue
        head, _, tail = str(entry).rpartition(" ")
        Delete(*(head, tail) if head and tail.upper() in kinds else (str(entry), None))
    return Result("true")


_BATCH_TYPES = {"BIGINTEGER": "BIGINTEGER", "DATE": "DATE", "DOUBLE": "DOUBLE", "GUID": "GUID", "INTEGER": "LONG",
                "LONG": "LONG", "FLOAT": "FLOAT", "SINGLE": "FLOAT", "SHORT": "SHORT", "SMALLINTEGER": "SHORT",
                "TEXT": "TEXT", "STRING": "TEXT"}


def _definition_rows(table):
    """The rows of a field definition table — a CSV file or a table grapy reads — as dictionaries by column name."""
    import csv
    path = _text(table)
    if path.lower().endswith((".csv", ".txt")):
        with open(path, encoding="utf-8-sig", newline="") as read:
            return [dict(row) for row in csv.DictReader(read)]
    from . import da
    names = [c.name for c in _schema(_source(path)[0])[0]]
    with da.SearchCursor(path, names) as rows:
        return [dict(zip(names, r)) for r in rows]


def BatchUpdateFields(in_table, out_table, field_definition_table, script_file=None, output_field_name=None,
                      source_field_name=None, output_field_type=None, output_field_decimals_or_length=None,
                      output_field_alias=None, output_field_script=None):
    """A new table whose fields are the definition table's rows, in their order: each copied from its source field or
    calculated by its Python script (``!field!`` for a field, a function of ``script_file`` callable), of its type and
    alias, as documented; a later field may use an earlier calculated one, as documented. The columns default to
    ``Target field``, ``Source field``, ``Type``, ``Decimals/Length``, ``Alias`` and ``Script``. INFERRED: a feature
    class keeps its shapes; a copied value is converted to the field's type; a calculated field's value overrides an
    input field of the same name for the fields after it; decimals and lengths change nothing in a GeoPackage, whose
    numbers and text have neither. ``Blob``, ``DateOnly``, ``TimeOnly``, ``TimestampOffset``, ``GlobalID`` and
    ``Raster`` types and a joined input are not built."""
    from . import da
    from ._runtime import ExecuteError
    path, where = _source(in_table)
    columns, kind, srid, _ = _schema(path)
    keys = {"name": output_field_name or "Target field", "source": source_field_name or "Source field",
            "type": output_field_type or "Type", "alias": output_field_alias or "Alias", "script": output_field_script or "Script"}
    definitions = []
    for row in _definition_rows(field_definition_table):
        lower = {str(k).lower(): v for k, v in row.items()}

        def cell(key):
            value = lower.get(keys[key].lower())
            return None if value is None or str(value).strip() == "" else str(value).strip()

        name, source, script = cell("name"), cell("source"), cell("script")
        if name is None:
            continue
        if source is None and script is None:
            raise ExecuteError(f"ERROR 000735: the field '{name}' needs a source field or a script.")
        word = (cell("type") or "TEXT").upper()
        if word not in _BATCH_TYPES:
            raise NotBuiltError(f"arcpy.management.BatchUpdateFields: the field type '{word}' is not built in grapy.")
        definitions.append((name, source, script, _BATCH_TYPES[word], cell("alias")))
    scope: dict = {}
    if _given(script_file):
        with open(_text(script_file), encoding="utf-8") as read:
            exec(read.read(), scope)  # noqa: S102 — the tool's own code, in the tool's own fenced process (ADR-223 §5.3)
    out_columns = [_gpkg.Column(name, _FIELD_TYPES[kind_word]) for name, _, _, kind_word, _ in definitions]
    with_shapes = kind != "Geometry"
    out = _table_out("BatchUpdateFields", out_table) if not with_shapes else _path(out_table)
    _gpkg.create(out, kind if with_shapes else "Geometry", srid if with_shapes else 0, out_columns)
    names = [c.name for c in columns]
    convert = {"SHORT": int, "LONG": int, "BIGINTEGER": int, "FLOAT": float, "DOUBLE": float, "TEXT": str}
    compiled = []
    for name, source, script, kind_word, _ in definitions:
        if script is None:
            compiled.append(None)
            continue
        import re
        compiled.append(compile(re.sub(r"!([^!]+)!", lambda m: f"__row[{m.group(1).lower()!r}]", script), name, "eval"))
    with da.SearchCursor(path, [*names, "SHAPE@"], where) as rows, \
            da.InsertCursor(out, [*[c.name for c in out_columns], "SHAPE@"]) as written:
        for row in rows:
            values = {n.lower(): v for n, v in zip(names, row[:-1])}
            record = []
            for (name, source, _, kind_word, _), code in zip(definitions, compiled):
                if code is None:
                    if source.lower() not in values:
                        raise ExecuteError(f"ERROR 000728: the source field '{source}' does not exist.")
                    value = values[source.lower()]
                else:
                    scope["__row"] = values
                    value = eval(code, scope)  # noqa: S307 — as above
                if value is not None and kind_word in convert:
                    value = convert[kind_word](value)
                values[name.lower()] = value
                record.append(value)
            written.insertRow((*record, row[-1] if with_shapes else None))
    for name, _, _, _, alias in definitions:
        if alias:
            _set_alias(out, name, alias)
    return Result(out)


# ---- topology (ADR-225) -------------------------------------------------------------------------------------------------

from ._runtime import equivalent as _equivalent  # noqa: E402

def _default_tolerance(srid):
    """INFERRED: a geodatabase's default xy tolerance, 0.001 m in the data's units, or 8.98e-9° on geographic data."""
    from ._reference import SpatialReference
    if srid > 0 and SpatialReference(srid).type == "Geographic":
        return 8.983152841195214e-09
    return 0.001 / (_per_unit(srid) or 1.0)


def _topology(in_topology):
    """(connection, file, dataset, name, row) of an existing topology; the caller closes the connection."""
    from . import _topology as topo
    from ._runtime import ExecuteError
    file, dataset, name = topo.locate(_text(in_topology))
    connection = _gpkg.connect(file)
    topo.ensure(connection)
    row = connection.execute("select dataset, cluster_tolerance from grapy_topology where name = ?", (name,)).fetchone()
    if row is None or row[0] != dataset:
        connection.close()
        raise ExecuteError(f"ERROR 000732: there is no topology '{name}' in the feature dataset '{dataset}'.")
    return connection, file, dataset, name, row


def _class_in(file, dataset, in_featureclass, tool):
    from ._runtime import ExecuteError
    path = _plain(tool, in_featureclass)
    their_file, table = _gpkg.split(path)
    if os.path.normcase(os.path.abspath(their_file)) != os.path.normcase(os.path.abspath(file)) or not table \
            or not table.startswith(dataset + "/"):
        raise ExecuteError(f"ERROR 000732: '{in_featureclass}' is not in the topology's feature dataset '{dataset}'.")
    return table


def CreateTopology(in_dataset, out_name, in_cluster_tolerance=None):
    """A new, empty topology in a feature dataset, its name unique in the workspace, as documented. A cluster tolerance
    of 0 or none is the default xy tolerance (see ``_default_tolerance``). The topology is kept in grapy's tables in the
    GeoPackage (``grapy._topology``, ADR-225)."""
    from . import _topology as topo
    from ._runtime import ExecuteError
    file, dataset = _gpkg.split(_text(in_dataset))
    srid = _feature_dataset_of(f"{file}/{dataset}/x") if dataset else None
    if srid is None:
        raise ExecuteError("ERROR 000732: a topology is created within a feature dataset (CreateFeatureDataset).")
    tolerance = float(in_cluster_tolerance) if _given(in_cluster_tolerance) else 0.0
    connection = _gpkg.connect(file)
    try:
        topo.ensure(connection)
        if connection.execute("select 1 from grapy_topology where name = ?", (str(out_name),)).fetchone():
            raise ExecuteError(f"ERROR 000258: a topology named '{out_name}' already exists in the workspace.")
        connection.execute("insert into grapy_topology values (?, ?, ?)",
                           (str(out_name), dataset, tolerance if tolerance > 0 else _default_tolerance(srid)))
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_dataset).rstrip("/\\") + "/" + str(out_name))


def SetClusterTolerance(in_topology, cluster_tolerance):
    """A topology's cluster tolerance changed; 0 gives the default, as documented."""
    connection, file, dataset, name, _ = _topology(in_topology)
    try:
        tolerance = float(cluster_tolerance or 0)
        if tolerance <= 0:
            tolerance = _default_tolerance(_feature_dataset_of(f"{file}/{dataset}/x") or 0)
        connection.execute("update grapy_topology set cluster_tolerance = ? where name = ?", (tolerance, name))
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_topology))


def AddFeatureClassToTopology(in_topology, in_featureclass, xy_rank=1, z_rank=1):
    """A feature class of the topology's feature dataset added, with its xy and z ranks — 1 the most accurate, up to 50,
    as documented. INFERRED: the z rank is kept though grapy's shapes are 2D."""
    from ._runtime import ExecuteError
    connection, file, dataset, name, _ = _topology(in_topology)
    try:
        table = _class_in(file, dataset, in_featureclass, "AddFeatureClassToTopology")
        for rank in (xy_rank, z_rank):
            if not 1 <= int(rank) <= 50:
                raise ExecuteError("ERROR 000800: a rank is from 1 to 50.")
        if connection.execute("select 1 from grapy_topology_class where topology = ? and class_name = ?", (name, table)).fetchone():
            raise ExecuteError(f"ERROR 001029: '{table}' is in the topology already.")
        connection.execute("insert into grapy_topology_class (topology, class_name, xy_rank, z_rank) values (?, ?, ?, ?)",
                           (name, table, int(xy_rank), int(z_rank)))
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_topology))


def RemoveFeatureClassFromTopology(in_topology, in_featureclass):
    """A feature class taken out of a topology, with every rule it takes part in, as documented, and their errors."""
    connection, file, dataset, name, _ = _topology(in_topology)
    try:
        table = _class_in(file, dataset, in_featureclass, "RemoveFeatureClassFromTopology")
        rules = [r[0] for r in connection.execute("select rule_id from grapy_topology_rule where topology = ? and "
                                                  "(origin = ? or destination = ?)", (name, table, table))]
        for rule_id in rules:
            connection.execute("delete from grapy_topology_error where rule_id = ?", (rule_id,))
            connection.execute("delete from grapy_topology_rule where rule_id = ?", (rule_id,))
        connection.execute("delete from grapy_topology_class where topology = ? and class_name = ?", (name, table))
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_topology))


def _class_kind(file, table):
    return {"Polygon": "area", "Polyline": "line", "Point": "point", "Multipoint": "point"}.get(_schema(f"{file}/{table}")[1])


def AddRuleToTopology(in_topology, rule_type, in_featureclass, subtype=None, in_featureclass2=None, subtype2=None):
    """A rule of the documented list added between the topology's classes, of the shape types its name gives. The same
    rule twice is an error (INFERRED). A rule on one subtype is refused: grapy holds subtypes (ADR-228) but its
    ValidateTopology evaluates a rule on every feature of a class."""
    from . import _topology as topo
    from ._runtime import ExecuteError
    if _given(subtype) or _given(subtype2):
        raise NotBuiltError("arcpy.management.AddRuleToTopology: a rule on one subtype is not built in grapy; its "
                            "ValidateTopology evaluates a rule on every feature of a class (ADR-228).")
    name_found, entry = topo.rule(rule_type)
    if entry is None:
        raise ExecuteError(f"ERROR 000800: '{rule_type}' is not a topology rule.")
    kinds = entry[0]
    connection, file, dataset, name, _ = _topology(in_topology)
    try:
        origin = _class_in(file, dataset, in_featureclass, "AddRuleToTopology")
        destination = _class_in(file, dataset, in_featureclass2, "AddRuleToTopology") if len(kinds) == 2 else None
        if len(kinds) == 2 and not _given(in_featureclass2):
            raise ExecuteError(f"ERROR 000735: '{name_found}' needs a second feature class.")
        for table in [t for t in (origin, destination) if t]:
            if not connection.execute("select 1 from grapy_topology_class where topology = ? and class_name = ?",
                                      (name, table)).fetchone():
                raise ExecuteError(f"ERROR 001029: '{table}' is not in the topology.")
        for table, kind in zip([origin, destination], kinds):
            if _class_kind(file, table) != kind:
                raise ExecuteError(f"ERROR 000800: '{name_found}' takes {kind} features; '{table}' holds others.")
        if connection.execute("select 1 from grapy_topology_rule where topology = ? and rule_type = ? and origin = ? and "
                              "destination is ?", (name, name_found, origin, destination)).fetchone():
            raise ExecuteError(f"ERROR 001029: the rule '{name_found}' is in the topology already.")
        connection.execute("insert into grapy_topology_rule (topology, rule_type, origin, destination) values (?, ?, ?, ?)",
                           (name, name_found, origin, destination))
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_topology))


def RemoveRuleFromTopology(in_topology, in_rule):
    """A rule removed, named as documented: its name and the object-class id of its class, or of both classes joined
    by a hyphen — ``"Must Not Have Dangles (21)"``, ``"Must Be Properly Inside (78-79)"`` — the ids being the
    ``class_id`` of ``grapy_topology_class``. Its errors go with it."""
    import re
    from ._runtime import ExecuteError
    match = re.fullmatch(r"\s*(.+?)\s*\((\d+)(?:-(\d+))?\)\s*", str(in_rule))
    if not match:
        raise ExecuteError(f"ERROR 000800: '{in_rule}' is not a rule name with its class ids in parentheses.")
    label, first, second = match.group(1).lower(), int(match.group(2)), match.group(3)
    connection, _, _, name, _ = _topology(in_topology)
    try:
        ids = {r[1]: r[0] for r in connection.execute("select class_id, class_name from grapy_topology_class where "
                                                      "topology = ?", (name,))}
        found = None
        for rule_id, rule_type, origin, destination in connection.execute(
                "select rule_id, rule_type, origin, destination from grapy_topology_rule where topology = ?", (name,)):
            from ._topology import description
            if description(rule_type).lower() == label and ids.get(origin) == first and \
                    (second is None and destination is None or second is not None and ids.get(destination) == int(second)):
                found = rule_id
        if found is None:
            raise ExecuteError(f"ERROR 000800: the topology has no rule '{in_rule}'.")
        connection.execute("delete from grapy_topology_error where rule_id = ?", (found,))
        connection.execute("delete from grapy_topology_rule where rule_id = ?", (found,))
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_topology))


def _validate_topology(in_topology, visible_extent="Full_Extent"):
    """The topology's vertices clustered within its cluster tolerance — the equivalent of ADR-225 §5.3, moving a vertex
    onto the nearest-ranked class's vertex — and then every rule evaluated over the whole topology, the errors replacing
    the last validation's. ``Visible_Extent`` and the extent environment are refused: a server tool has no map view.
    Cracking (inserting a shared vertex where features cross) is not done; the rules' predicates do not need it."""
    from . import _topology as topo, da
    from ._runtime import env
    if str(visible_extent or "Full_Extent").upper() in ("VISIBLE_EXTENT", "TRUE") or env.extent not in (None, "", "DEFAULT", "MAXOF"):
        raise NotBuiltError("arcpy.management.ValidateTopology over the visible or an environment extent is not built in "
                            "grapy; a server tool has no map view, and the whole topology is validated.")
    connection, file, _, name, (_, tolerance) = _topology(in_topology)
    try:
        members = connection.execute("select class_name, xy_rank from grapy_topology_class where topology = ? "
                                     "order by class_id", (name,)).fetchall()
        rules = connection.execute("select rule_id, rule_type, origin, destination from grapy_topology_rule where "
                                   "topology = ?", (name,)).fetchall()
        srid = _schema(f"{file}/{members[0][0]}")[2] if members else 0
    finally:
        connection.close()
    features = {}
    for table, _ in members:
        with da.SearchCursor(f"{file}/{table}", ["OID@", "SHAPE@"]) as rows:
            features[table] = [(oid, shape._shape) for oid, shape in rows if shape is not None and not shape._shape.is_empty]
    moved = topo.cluster([(rank, features[table]) for table, rank in members], tolerance)
    for index, (table, _) in enumerate(members):
        changed = {oid: shape for (i, oid), shape in moved.items() if i == index}
        if not changed:
            continue
        with da.UpdateCursor(f"{file}/{table}", ["OID@", "SHAPE@"]) as rows:
            for oid, _ in rows:
                if oid in changed:
                    rows.updateRow([oid, changed[oid]])
        features[table] = [(oid, changed.get(oid, shape)) for oid, shape in features[table]]
    connection = _gpkg.connect(file)
    try:
        connection.execute("delete from grapy_topology_error where topology = ?", (name,))
        for rule_id, rule_type, origin, destination in rules:
            for shape, origin_oid, destination_oid in topo.evaluate(rule_type, features[origin],
                                                                    features[destination] if destination else []):
                connection.execute(
                    "insert into grapy_topology_error (topology, rule_id, kind, origin_class, origin_oid, destination_class, "
                    "destination_oid, rule_type, description, is_exception, shape) values (?, ?, ?, ?, ?, ?, ?, ?, ?, 0, ?)",
                    (name, rule_id, topo.kind_of(shape), origin.rsplit("/", 1)[1],
                     int(origin_oid), (destination or origin).rsplit("/", 1)[1] if destination_oid else "",
                     int(destination_oid), topo.rule(rule_type)[1][1], topo.description(rule_type),
                     _gpkg.encode(shape, srid)))
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_topology))


@_equivalent("vertices within the cluster tolerance snap to the best-ranked class's vertex (lowest xy rank, then class "
             "order, then object id)", "ADR-225 §5.3")
def ValidateTopology(in_topology, visible_extent="Full_Extent"):
    """EQUIVALENT: clustering within the cluster tolerance snaps a vertex to the vertex of the best-ranked class within
    the tolerance (lowest xy rank, then class order, then object id), as ADR-225 §5.3 decides — Esri describes its
    cracking and clustering only in words. Every rule is then evaluated as the AddRuleToTopology page defines it; see
    ``_validate_topology``."""
    return _validate_topology(in_topology, visible_extent)


def ExportTopologyErrors(in_topology, out_path=None, out_basename=None):
    """The last validation's errors as three feature classes, always made even when empty — ``<basename>PointsErrors``,
    ``<basename>LineErrors`` and ``<basename>PolygonErrors`` — with ``OriginObjectClassName``, ``OriginObjectID``,
    ``DestinationObjectClassName``, ``DestinationObjectID``, ``RuleType``, ``RuleDescription`` and ``IsException``, as
    documented; ``out_path`` defaults to the topology's workspace and ``out_basename`` to its name. INFERRED: RuleType
    holds the esriTopologyRuleType name, RuleDescription the rule's name without its shape types, and a rule within one
    class has no destination class name and destination id 0."""
    from . import da
    connection, file, dataset, name, _ = _topology(in_topology)
    try:
        members = [r[0] for r in connection.execute("select class_name from grapy_topology_class where topology = ?", (name,))]
        errors = connection.execute("select kind, origin_class, origin_oid, destination_class, destination_oid, rule_type, "
                                    "description, is_exception, shape from grapy_topology_error where topology = ? "
                                    "order by error_id", (name,)).fetchall()
    finally:
        connection.close()
    srid = _schema(f"{file}/{members[0]}")[2] if members else (_feature_dataset_of(f"{file}/{dataset}/x") or 0)
    base = str(out_basename) if _given(out_basename) else name
    where = _text(out_path) if _given(out_path) else file
    fields = [_gpkg.Column("OriginObjectClassName", "String"), _gpkg.Column("OriginObjectID", "Integer"),
              _gpkg.Column("DestinationObjectClassName", "String"), _gpkg.Column("DestinationObjectID", "Integer"),
              _gpkg.Column("RuleType", "String"), _gpkg.Column("RuleDescription", "String"),
              _gpkg.Column("IsException", "Integer")]
    outputs = []
    for suffix, kind, shape in (("PointsErrors", "point", "Point"), ("LineErrors", "line", "Polyline"),
                                ("PolygonErrors", "area", "Polygon")):
        out = f"{where}/{base}{suffix}"
        _gpkg.create(out, shape, srid, fields)
        with da.InsertCursor(out, [*[f.name for f in fields], "SHAPE@"]) as written:
            for row in errors:
                if row[0] == kind:
                    written.insertRow((*row[1:8], _gpkg.decode(row[8])))
        outputs.append(out)
    return Result(*outputs)


# ---- relationship classes (ADR-225) -------------------------------------------------------------------------------------

def _relationship_path(value):
    from ._runtime import ExecuteError
    file, table = _gpkg.split(_text(value))
    if not table:
        raise ExecuteError(f"ERROR 000732: write a relationship class as <file>.gpkg/<name>, not '{value}'.")
    return file, table


def _same_file(*paths):
    from ._runtime import ExecuteError
    files = {os.path.normcase(os.path.abspath(_gpkg.split(p)[0])) for p in paths}
    if len(files) != 1:
        raise ExecuteError("ERROR 000732: a relationship class relates tables of one workspace.")


def _make_relationship(origin_table, destination_table, out_relationship_class, relationship_type, forward_label,
                       backward_label, message_direction, cardinality, attributed, origin_primary_key, origin_foreign_key,
                       destination_primary_key, destination_foreign_key, relationship_rows=None, attribute_fields=()):
    from . import _relationships as rel
    from ._runtime import ExecuteError
    origin_path, destination_path = _plain("CreateRelationshipClass", origin_table), _plain("CreateRelationshipClass", destination_table)
    _same_file(origin_path, destination_path, _text(out_relationship_class))
    file, name = _relationship_path(out_relationship_class)
    origin, destination = _gpkg.split(origin_path)[1] or _stem_of(origin_path), _gpkg.split(destination_path)[1] or _stem_of(destination_path)
    kind = str(relationship_type or "SIMPLE").upper()
    cardinality = str(cardinality or "ONE_TO_ONE").upper()
    with_table = cardinality == "MANY_TO_MANY" or str(attributed or "NONE").upper() == "ATTRIBUTED" or relationship_rows is not None
    if kind not in ("SIMPLE", "COMPOSITE") or cardinality not in ("ONE_TO_ONE", "ONE_TO_MANY", "MANY_TO_MANY"):
        raise ExecuteError("ERROR 000800: relationship_type is SIMPLE or COMPOSITE; cardinality ONE_TO_ONE, ONE_TO_MANY or "
                           "MANY_TO_MANY.")
    if kind == "COMPOSITE" and with_table:
        raise NotBuiltError("arcpy.management.CreateRelationshipClass: a composite class kept in a relationship table is "
                            "not built in grapy; the page says composite relationships are one to many.")
    origin_columns = {c.name.lower(): c for c in _schema(origin_path)[0]}
    destination_columns = {c.name.lower(): c for c in _schema(destination_path)[0]}
    origin_oid, destination_oid = _oid_of(origin_path), _oid_of(destination_path)

    def column(columns, oid, key, where):
        if str(key).lower() == oid.lower():
            return _gpkg.Column(oid, "Integer")
        if str(key).lower() not in columns:
            raise ExecuteError(f"ERROR 000728: the field '{key}' does not exist in {where}.")
        return columns[str(key).lower()]

    origin_pk = column(origin_columns, origin_oid, origin_primary_key, origin)
    destination_pk = None
    if with_table:
        if not (_given(destination_primary_key) and _given(destination_foreign_key)):
            raise ExecuteError("ERROR 000735: a many-to-many or attributed class needs destination keys.")
        destination_pk = column(destination_columns, destination_oid, destination_primary_key, destination)
    else:
        column(destination_columns, destination_oid, origin_foreign_key, destination)
    connection = _gpkg.connect(file)
    try:
        rel.ensure(connection)
        if connection.execute("select 1 from grapy_relationship_class where name = ?", (name,)).fetchone() or \
                connection.execute("select 1 from gpkg_contents where table_name = ?", (name,)).fetchone():
            raise ExecuteError(f"ERROR 000258: '{name}' already exists.")
    finally:
        connection.close()
    if with_table:
        extra = [_gpkg.Column(c.name, c.type) for c in attribute_fields]
        _gpkg.create(f"{file}/{name}", "Geometry", 0, [_gpkg.Column(str(origin_foreign_key), origin_pk.type),
                                                       _gpkg.Column(str(destination_foreign_key), destination_pk.type), *extra])
        if relationship_rows:
            from . import da
            with da.InsertCursor(f"{file}/{name}", [str(origin_foreign_key), str(destination_foreign_key),
                                                     *[c.name for c in extra], "SHAPE@"]) as written:
                for row in relationship_rows:
                    written.insertRow((*row, None))
    connection = _gpkg.connect(file)
    try:
        values = (name, origin, destination, kind, forward_label, backward_label,
                  str(message_direction or "NONE").upper(), cardinality, int(str(attributed or "NONE").upper() == "ATTRIBUTED"),
                  origin_pk.name, str(origin_foreign_key), destination_pk.name if destination_pk else None,
                  str(destination_foreign_key) if with_table else None, name if with_table else None)
        connection.execute("insert into grapy_relationship_class values (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)", values)
        rel.write_related_tables(connection, rel.classes(connection, name=name)[0])
        connection.commit()
    finally:
        connection.close()
    return Result(_text(out_relationship_class))


def CreateRelationshipClass(origin_table, destination_table, out_relationship_class, relationship_type="SIMPLE",
                            forward_label=None, backward_label=None, message_direction="NONE", cardinality="ONE_TO_ONE",
                            attributed="NONE", origin_primary_key=None, origin_foreign_key=None,
                            destination_primary_key=None, destination_foreign_key=None):
    """A relationship class between two tables of one workspace, as documented: SIMPLE or COMPOSITE (deleting an origin
    row deletes its destination rows, through grapy's delete paths), labels, message direction, cardinality, and keys —
    the destination's ``origin_foreign_key`` holding the origin's ``origin_primary_key`` for a one-to-one or
    one-to-many class, or a relationship table for a many-to-many or attributed one. Kept in grapy's tables and in the
    OGC Related Tables Extension (``grapy._relationships``). INFERRED: the relationship table is named after the class
    and holds the two foreign keys under the names given; messages are kept but nothing in grapy sends them; a
    composite class is one to one or one to many, the page saying composites are one to many."""
    return _make_relationship(origin_table, destination_table, out_relationship_class, relationship_type, forward_label,
                              backward_label, message_direction, cardinality, attributed, origin_primary_key,
                              origin_foreign_key, destination_primary_key, destination_foreign_key)


def TableToRelationshipClass(origin_table, destination_table, out_relationship_class, relationship_type="SIMPLE",
                             forward_label=None, backward_label=None, message_direction="NONE", cardinality="ONE_TO_ONE",
                             relationship_table=None, attribute_fields=None, origin_primary_key=None,
                             origin_foreign_key=None, destination_primary_key=None, destination_foreign_key=None):
    """A relationship class made from a table of pairs: each row of ``relationship_table`` relates the origin row whose
    ``origin_primary_key`` is its ``origin_foreign_key`` to the destination row whose ``destination_primary_key`` is its
    ``destination_foreign_key``, carrying its ``attribute_fields``; a null key is an error, as documented. The pairs are
    kept in the class's own relationship table, as CreateRelationshipClass keeps a many-to-many class's."""
    from . import da
    from ._runtime import ExecuteError
    path, where = _source(relationship_table)
    columns = {c.name.lower(): c for c in _schema(path)[0]}
    attributes = [columns[f.lower()] for f in _list(attribute_fields)]
    keys = [columns[str(origin_foreign_key).lower()].name, columns[str(destination_foreign_key).lower()].name]
    with da.SearchCursor(path, [*keys, *[c.name for c in attributes]], where) as rows:
        listed = [tuple(r) for r in rows]
    if any(r[0] is None or r[1] is None for r in listed):
        raise ExecuteError("ERROR 000800: a relationship table's foreign keys cannot be null.")
    return _make_relationship(origin_table, destination_table, out_relationship_class, relationship_type, forward_label,
                              backward_label, message_direction, cardinality, "ATTRIBUTED" if attributes else "NONE",
                              origin_primary_key, keys[0], destination_primary_key, keys[1], listed, attributes)


def AddRuleToRelationshipClass(in_rel_class, origin_subtype=None, origin_minimum=None, origin_maximum=None,
                               destination_subtype=None, destination_minimum=None, destination_maximum=None):
    """A rule narrowing the class's cardinality kept with it, as documented, for the origin and destination subtypes
    named (ADR-228): each must be a subtype of its table. INFERRED: the page does not say when a rule is checked, and
    no grapy tool checks it."""
    from . import _relationships as rel
    from ._runtime import ExecuteError
    file, name = _relationship_path(in_rel_class)
    connection = _gpkg.connect(file)
    try:
        rel.ensure(connection)
        found = rel.classes(connection, name=name)
        if not found:
            raise ExecuteError(f"ERROR 000732: there is no relationship class '{name}'.")
        origin_code = _rule_subtype(connection, found[0]["origin"], origin_subtype)
        destination_code = _rule_subtype(connection, found[0]["destination"], destination_subtype)
        number = lambda v: int(v) if _given(v) else None  # noqa: E731
        connection.execute("insert into grapy_relationship_rule (relationship_class, origin_subtype, origin_minimum, "
                           "origin_maximum, destination_subtype, destination_minimum, destination_maximum) values "
                           "(?, ?, ?, ?, ?, ?, ?)",
                           (name, origin_code, number(origin_minimum), number(origin_maximum), destination_code,
                            number(destination_minimum), number(destination_maximum)))
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_rel_class))


def RemoveRuleFromRelationshipClass(in_rel_class, origin_subtype=None, destination_subtype=None, remove_all="NOT_ALL"):
    """The class's rule for the subtypes named removed — or with ``REMOVE``, or no subtype named, every rule, as
    documented (ADR-228 for the subtypes)."""
    from . import _relationships as rel
    file, name = _relationship_path(in_rel_class)
    connection = _gpkg.connect(file)
    try:
        rel.ensure(connection)
        found = rel.classes(connection, name=name)
        if str(remove_all or "").upper() != "REMOVE" and found and (_given(origin_subtype) or _given(destination_subtype)):
            connection.execute("delete from grapy_relationship_rule where relationship_class = ? and origin_subtype is ? "
                               "and destination_subtype is ?",
                               (name, _rule_subtype(connection, found[0]["origin"], origin_subtype),
                                _rule_subtype(connection, found[0]["destination"], destination_subtype)))
        else:
            connection.execute("delete from grapy_relationship_rule where relationship_class = ?", (name,))
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_rel_class))


def _rule_subtype(connection, table, value):
    """A relationship rule's subtype code as text, checked against the table's subtypes; None when none is named.
    INFERRED: a subtype may be named by its code or by its name, as Pro's dialog lists subtypes by name."""
    if not _given(value):
        return None
    _, held = _subtyped(connection, table)
    by_name = {n.lower(): c for c, n in held.items()}
    codes = [by_name[str(value).strip().lower()]] if str(value).strip().lower() in by_name else _subtype_codes(value)
    _subtyped(connection, table, codes[:1])
    return str(codes[0])


def _cascade(path, oids):
    """Composite deletes and relationship-table rows for rows about to be deleted from ``path``."""
    from . import _relationships as rel
    connection, table = _gpkg.table_of(path)
    try:
        rel.cascade(connection, table, oids)
        connection.commit()
    finally:
        connection.close()


# ---- refused by name ----------------------------------------------------------------------------------------------------
#
# Every arcpy.management name grapy does not build is refused here with its own reason (ADR-224): a data model this
# server does not hold yet (group 2), an algorithm or output Esri does not document, Z values grapy's 2D shapes do not
# carry, an ArcGIS Pro application document, a network a fenced tool does not reach, or a storage property a
# GeoPackage does not have. Only a name in none of these answers the generic "not built yet".

_GROUP_2_MODELS = {
    "geodatabase versioning and archiving": (
        "AlterVersion", "ChangeVersion", "CreateVersion", "CreateVersionedView", "DeleteVersion", "DiagnoseVersionMetadata",
        "DiagnoseVersionTables", "PruneBranchHistory", "ReconcileVersion", "ReconcileVersions", "RegisterAsVersioned",
        "RepairVersionMetadata", "RepairVersionTables", "UnregisterAsVersioned", "UpdateGeodatabaseConnectionPropertiesToBranch",
        "EnableArchiving", "DisableArchiving", "TrimArchiveHistory", "Compress", "AddFieldConflictFilter",
        "RemoveFieldConflictFilter"),
    "geodatabase replicas": (
        "CreateReplica", "CreateReplicaFromServer", "SynchronizeChanges", "UnregisterReplica", "CompareReplicaSchema",
        "ExportReplicaSchema", "ImportReplicaSchema", "EnableReplicaTracking", "DisableReplicaTracking",
        "ExportAcknowledgementMessage", "ExportDataChangeMessage", "ImportMessage", "ReExportUnacknowledgedMessages",
        "ExtractDataFromGeodatabase"),
    "attribute rules": (
        "AddAttributeRule", "AlterAttributeRule", "DeleteAttributeRule", "DisableAttributeRules", "EnableAttributeRules",
        "EvaluateRules", "ExportAttributeRules", "ImportAttributeRules", "ReorderAttributeRule",
        "CalculateGeometryFieldsAttributeRule", "GenerateIDAttributeRule", "GenerateSpatialJoinAttributeRule",
        "GenerateSymbolRotationAttributeRule", "GenerateUpdateRelatedAttributeRule"),
    "enterprise geodatabase administration": (
        "CreateEnterpriseGeodatabase", "EnableEnterpriseGeodatabase", "UpdateEnterpriseGeodatabaseLicense",
        "CreateDatabaseUser", "CreateRole", "ChangePrivileges", "CreateSpatialType", "ConfigureGeodatabaseLogFileTables",
        "ExportGeodatabaseConfigurationKeywords", "ImportGeodatabaseConfigurationKeywords", "DeleteSchemaGeodatabase",
        "MigrateStorage", "RegisterWithGeodatabase", "CreateDatabaseSequence", "DeleteDatabaseSequence", "UpgradeGDB",
        "AnalyzeDatasets", "RebuildIndexes", "UpdatePortalDatasetOwner", "Analyze", "CreateDatabaseView",
        "CreateUnRegisteredFeatureclass", "CreateUnRegisteredTable", "AddIncrementingIDField"),
    "parcel fabrics": ("EnableCOGO", "DisableCOGO"),
    "contingent values and field groups": (
        "AddContingentValue", "RemoveContingentValue", "ExportContingentValues", "ImportContingentValues",
        "GenerateContingentValues", "CreateFieldGroup", "AlterFieldGroup", "DeleteFieldGroup"),
    "geodatabase editing behaviours (editor tracking, feature binning, split models)": (
        "EnableEditorTracking", "DisableEditorTracking", "EnableLastEditTime", "DisableLastEditTime",
        "EnableFeatureBinning", "DisableFeatureBinning", "ManageFeatureBinCache", "SetFeatureClassSplitModel"),
    "trajectory datasets": ("CreateTrajectoryDataset", "AddDataToTrajectoryDataset", "RepairTrajectoryDatasetPaths",
                            "MakeTrajectoryLayer"),
    "annotation feature classes": ("AppendAnnotation",),
    "ortho mapping and stereo workspaces": (
        "AnalyzeControlPoints", "AppendControlPoints", "ApplyBlockAdjustment", "ComputeBlockAdjustment",
        "ComputeCameraModel", "ComputeControlPoints", "ComputeDepthMap", "ComputeFiducials", "ComputeTiePoints",
        "ExportFrameAndCameraParameters", "GenerateBlockAdjustmentReport", "GeneratePointCloud", "MatchControlPoints",
        "RemoveDepthMap", "UpdateInteriorOrientation", "BuildStereoModel", "GenerateExcludeArea",
        "CreateOrthoCorrectedRasterDataset"),
}


def BuildRasterAttributeTable(in_raster, overwrite="NONE", convert_colormap="NONE"):
    """A raster attribute table for an integer raster: ``Value`` and ``Count``, each value present with the number of
    its cells, written to the raster's .aux.xml as GDAL keeps one. ``Overwrite`` replaces a table already there;
    ``NONE`` keeps it, its added fields and their values, and updates its values, as documented ("No fields will be
    deleted, but the values in the table will be updated").

    Refused: floating-point rasters, as documented; ``ConvertColormap`` (grapy's rasters carry no colormap).

    INFERRED: the fields are named Value and Count, as ArcGIS shows them (the page does not name them); NoData cells are
    not counted; under NONE a value no longer present loses its row and a new value gains one with its added fields
    empty."""
    from ._raster import Raster, _value_count, _write_rat
    from ._runtime import ExecuteError
    if str(convert_colormap or "NONE").upper() != "NONE":
        raise NotBuiltError("arcpy.management.BuildRasterAttributeTable: convert_colormap is not built in grapy; its "
                            "rasters carry no colormap.")
    raster = Raster(in_raster)
    if not raster.isInteger:
        raise ExecuteError("ERROR 000049: an attribute table cannot be built for a floating-point raster.")
    fresh = _value_count(raster._array, raster._mask)
    old = raster._rat
    if old is not None and str(overwrite or "NONE").upper() != "OVERWRITE":
        names = old.names()
        value_at = old.index("Value") if "value" in [n.lower() for n in names] else None
        kept = {row[value_at]: row for row in old.rows} if value_at is not None else {}
        rows = []
        for value, count in fresh.rows:
            row = list(kept.get(value, [None if f[1] != "String" else "" for f in old.fields]))
            if value_at is not None:
                row[value_at] = value
            if "count" in [n.lower() for n in names]:
                row[old.index("Count")] = count
            rows.append(row)
        fresh = type(old)(old.fields, rows)
    _write_rat(raster.catalogPath, fresh)
    return Result(raster.catalogPath)


def DeleteRasterAttributeTable(in_raster):
    """The raster's attribute table removed from its .aux.xml."""
    from ._raster import Raster, _write_rat
    raster = Raster(in_raster)
    _write_rat(raster.catalogPath, None)
    return Result(raster.catalogPath)


def CompositeBands(in_rasters, out_raster):
    """The input rasters' bands, in order, as one raster of several bands, saved (arcpy.sa.CompositeBand's rule: the
    inputs must lie on one grid, since resampling is not built). INFERRED: the inputs are a list or a
    semicolon-separated string."""
    from . import sa
    inputs = in_rasters if isinstance(in_rasters, (list, tuple)) else [p for p in _text(in_rasters).split(";") if p]
    path = _raster_path(out_raster, "CompositeBands")
    sa.CompositeBand([str(r) if isinstance(r, Result) else r for r in inputs]).save(path)
    return Result(path)


_REASONS = {
    "needs 3D data models — LAS datasets, TINs, multipatches, voxels, scenes — which this server does not hold yet "
    "(ADR-224 group 2)": (
        "Add3DFormats", "Remove3DFormats", "AddFilesToLasDataset", "RemoveFilesFromLasDataset", "BuildLasDatasetPyramid",
        "CreateLasDataset", "LasDatasetStatistics", "LasPointStatsAsRaster", "MakeLasDatasetLayer", "ProjectLAS",
        "Export3DObjects", "Import3DObjects", "TINCompare", "MakeTinLayer", "CreateVoxelSceneLayerContent",
        "GenerateLevelOfDetail", "MakeBuildingLayer", "MakeSceneLayer", "InterpolateFromPointCloud"),
    "needs an ArcGIS Pro application document — a map, layout, layer file, style, report, toolbox version, package or "
    "project — which a tool running on the server does not hold": (
        "ApplySymbologyFromLayer", "MatchLayerSymbologyToAStyle", "SaveToLayerFile", "ExportLayouts", "ExportReportToPDF",
        "ExportReportToExcel", "ConsolidateLayer", "ConsolidateLocator", "ConsolidateMap", "ConsolidateProject",
        "ConsolidateToolbox", "PackageLayer", "PackageLocator", "PackageMap", "PackageProject", "PackageResult",
        "ExtractPackage", "SharePackage", "CreateMapTilePackage", "CreateMobileMapPackage", "CreateMobileScenePackage",
        "CreateVectorTilePackage", "CreateVectorTileIndex", "Create3DObjectSceneLayerPackage",
        "CreateBuildingSceneLayerPackage", "CreateIntegratedMeshSceneLayerPackage", "CreatePointCloudSceneLayerPackage",
        "CreatePointSceneLayerPackage", "CreateSceneLayerPackage", "UpgradeSceneLayer", "ValidateSceneLayerPackage",
        "Package3DTiles", "GenerateTileCacheTilingScheme", "ManageTileCache", "ExportTileCache", "ImportTileCache",
        "AnalyzeToolsForPro", "AnalyzeToolboxForVersion", "SaveToolboxToVersion", "SelectData", "EnableEditingTemplates",
        "GenerateDefinitionQueryFromSelection", "RefreshExcel", "AddRelate", "RemoveRelate"),
    "reaches a database server, portal or cloud store, and a fenced tool has no network (ADR-223 §5.3)": (
        "CreateAIServiceConnectionFile", "CreateCloudStorageConnectionFile", "CreateDatabaseConnection",
        "CreateDatabaseConnectionString", "CreateConnectionString", "DownloadRasters", "MakeImageServerLayer",
        "MakeWCSLayer", "UploadFileToPortal", "TransferFiles", "MakeQueryLayer", "MakeQueryTable",
        "MakeAggregationQueryLayer"),
    "reads or writes an Esri format that is not publicly documented (XML workspace documents, schema reports, data "
    "loading workspaces, raster function templates, transformation files, parquet caches, spectral libraries)": (
        "ExportXMLWorkspaceDocument", "ImportXMLWorkspaceDocument", "GenerateSchemaReport", "ConvertSchemaReport",
        "CompareSchema", "CreateDataLoadingWorkspace", "LoadDataUsingWorkspace", "UpdateDataLoadingWorkspace",
        "LoadDataToPreview", "GenerateMappingTable", "EditRasterFunction", "ConvertRasterFunctionTemplate",
        "GenerateRasterFromRasterFunction", "GenerateTableFromRasterFunction", "CreateCustomGeoTransformation",
        "CreateCustomVerticalTransformation", "CreateParquetCache", "ResampleLibrarySpectra", "CreateSpatialReference"),
    "is a storage property of file or mobile geodatabases with no counterpart in grapy's GeoPackages": (
        "CompressFileGeodatabaseData", "UncompressFileGeodatabaseData", "RecoverFileGDB", "GenerateFgdbLicense",
        "GenerateLicensedFgdb", "UpgradeDataset", "MigrateObjectIDTo64Bit", "MigrateDateFieldToHighPrecision",
        "MigrateTextField", "AlterGeometryIndexPreference", "CalculateDefaultGridIndex",
        "CalculateDefaultClusterTolerance", "AddFullTextIndex"),
    "needs multidimensional or colormapped rasters, raster properties grapy does not keep, or a colour-composite or "
    "pansharpening method grapy does not build (ADR-224 §5.5)": (
        "CreateColorComposite", "ExtractSubDataset", "BuildMultidimensionalInfo",
        "BuildMultidimensionalTranspose", "AddColormap", "DeleteColormap", "SetRasterProperties", "ComputePansharpenWeights", "CreatePansharpenedRasterDataset"),
    "resamples or transforms cells by a method or extent rule the page does not define exactly": (
        "ProjectRaster", "Rescale", "Rotate", "Warp", "WarpFromFile", "RegisterRaster", "SplitRaster",
        "WorkspaceToRasterDataset", "CreateRasterDataset"),
    "follows an algorithm or writes an output Esri does not document, so any answer would be a guess under arcpy's "
    "name": (
        "Dice", "Integrate", "SubdividePolygon", "GenerateRectanglesAlongLines", "ConvertCoordinateNotation",
        "DetectFeatureChanges", "CreateSpatialSamplingLocations", "CreateSpatiallyBalancedPoints", "SubsetFeatures",
        "TableToEllipse", "GeodeticDensify", "ValidateJoin", "RasterCompare", "CreateRandomRaster", "AddSpatialJoin",
        "FieldStatisticsToTable", "AddGPSMetadataFields", "EncodeField", "ReclassifyField", "TransformField"),
    "keeps a GeoPackage spatial index (the RTree extension), which grapy does not maintain and no grapy tool reads": (
        "AddSpatialIndex", "RemoveSpatialIndex"),
    "makes a layer over a table read live (an event layer) or over a raster; grapy's layers are over feature classes "
    "and tables — XYTableToPoint makes the points": ("MakeXYEventLayer", "MakeRasterLayer"),
    "sets how ArcGIS Pro's split edit treats a relationship class's related rows; grapy has no split edit": (
        "SetRelationshipClassSplitPolicy",),
    "converts between Windows time zone names, whose mapping to the IANA zones Python holds Esri does not publish": (
        "ConvertTimeZone",),
    "makes a mobile geodatabase (.geodatabase), Esri's own SQLite schema; CreateFileGDB and CreateSQLiteDatabase make "
    "grapy's GeoPackage workspace": ("CreateMobileGDB",),
}

_REFUSED = {name: f"needs a data model this server does not hold yet (ADR-224 group 2): {model}"
            for model, names in _GROUP_2_MODELS.items() for name in names}
_REFUSED.update({name: reason for reason, names in _REASONS.items() for name in names})

_REFUSED["StandardizeField"] = (
    "has no page grapy could read: doc.esri.com/en/arcgis-pro/latest/tool-reference/data-management/standardize-field.html "
    "answered 404, and pro.arcgis.com's .../data-management/standardize-field.htm redirects to it (checked 2026-10-29), "
    "so its formulas and field names are unknown")


def __getattr__(name):
    if name.startswith("_"):
        raise AttributeError(name)
    reason = _REFUSED.get(name)
    message = f"arcpy.management.{name} {reason}." if reason else f"arcpy.management.{name} is not built in grapy yet."

    def missing(*_, **__):
        raise NotBuiltError(message)

    return missing


# ---- branch versions and archives (ADR-227) ------------------------------------------------------------------------------
#
# Held in the GeoPackage the tool works on: see grapy._versions. A registered class keeps DEFAULT's current state in its
# own table and its history beside it; named versions are branches off DEFAULT. Traditional versioning (delta tables,
# compress, edits to base), replicas and enterprise administration stay refused above.

def _versioned_tables(tool, in_dataset):
    """(file, [tables]) of a feature class, table or feature dataset (every class in it)."""
    path = _plain(tool, in_dataset) if _layers_find(in_dataset) is not None else _text(in_dataset)
    file, table = _gpkg.split(path)
    if not os.path.exists(file):
        raise FileNotFoundError(f"'{file}' does not exist.")
    connection = _gpkg.connect(file)
    try:
        names = [r[0] for r in connection.execute("select table_name from gpkg_contents")]
        datasets = []
        if connection.execute("select 1 from sqlite_master where name = 'grapy_feature_datasets'").fetchone():
            datasets = [r[0] for r in connection.execute("select name from grapy_feature_datasets")]
    finally:
        connection.close()
    if table is None:
        if len(names) != 1:
            raise ValueError(f"arcpy.management.{tool}: '{file}' holds {len(names)} tables; name one.")
        return file, names
    if table in datasets:
        return file, [n for n in names if n.startswith(table + "/")]
    if table not in names:
        raise ValueError(f"arcpy.management.{tool}: '{path}' does not exist.")
    return file, [table]


def _layers_find(value):
    from . import _layers
    return _layers.find(value)


def _version_workspace(value):
    file, _ = _gpkg.split(_text(value))
    if not os.path.exists(file):
        raise FileNotFoundError(f"'{file}' does not exist.")
    return file


def RegisterAsVersioned(in_dataset, edit_to_base=None):
    """The dataset registered as branch versioned (ADR-227): its history begins, and named versions can edit it. A
    feature dataset registers every class in it, as documented; an archive-enabled class is refused, as documented —
    disable archiving first. ``EDITS_TO_BASE`` is traditional versioning's and is refused."""
    from . import _versions
    from ._runtime import ExecuteError
    if str(edit_to_base or "NO_EDITS_TO_BASE").upper() in ("EDITS_TO_BASE", "TRUE"):
        raise NotBuiltError("arcpy.management.RegisterAsVersioned: EDITS_TO_BASE is traditional versioning's, which is "
                            "not built in grapy (ADR-227 §5.4).")
    file, tables = _versioned_tables("RegisterAsVersioned", in_dataset)
    connection = _gpkg.connect(file)
    try:
        _versions.ensure(connection)
        for table in tables:
            kind = _versions.kind(connection, table)
            if kind == "archive":
                raise ExecuteError(f"ERROR 000800: '{table}' is archive enabled; archive-enabled datasets cannot be "
                                   "registered as versioned.")
            if kind is None:
                _versions.register(connection, table, "branch")
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_dataset))


def UnregisterAsVersioned(in_dataset, keep_edit="KEEP_EDIT", compress_default=None):
    """The dataset no longer versioned: its history dropped; DEFAULT's state stays in the table. With KEEP_EDIT (the
    default) the tool fails while a named version holds edits of it, as documented; NO_KEEP_EDIT discards them.
    ``compress_default`` is traditional versioning's and changes nothing here."""
    from . import _versions
    from ._runtime import ExecuteError
    file, tables = _versioned_tables("UnregisterAsVersioned", in_dataset)
    connection = _gpkg.connect(file)
    try:
        for table in tables:
            if _versions.kind(connection, table) != "branch":
                raise ExecuteError(f"ERROR 000800: '{table}' is not registered as versioned.")
            pending = connection.execute(f'select count(*) from "{_versions.history(table)}" where gdb_branch_id <> 0'
                                         ).fetchone()[0]
            if pending and str(keep_edit or "KEEP_EDIT").upper() in ("KEEP_EDIT", "TRUE"):
                raise ExecuteError(f"ERROR 000800: named versions hold {pending} edits of '{table}'; post them, or pass "
                                   "NO_KEEP_EDIT to discard them.")
            _versions.unregister(connection, table)
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_dataset))


def EnableArchiving(in_dataset):
    """Archiving on a non-versioned class (ADR-227): every later insert, update and delete is kept with the moment
    it was made, and a layer set to a historical moment (ChangeVersion) reads the class as it was then. A branch
    versioned class is archived already, as documented."""
    from . import _versions
    file, tables = _versioned_tables("EnableArchiving", in_dataset)
    connection = _gpkg.connect(file)
    try:
        _versions.ensure(connection)
        for table in tables:
            if _versions.kind(connection, table) is None:
                _versions.register(connection, table, "archive")
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_dataset))


def DisableArchiving(in_dataset, preserve_history="PRESERVE"):
    """Archiving off. With PRESERVE (the default) the history is kept as a table named after the class with ``_h``
    appended, as documented for non-versioned data; DELETE drops it. A branch versioned class is refused: its
    archive is its versioning (unregister it instead)."""
    from . import _versions
    from ._runtime import ExecuteError
    file, tables = _versioned_tables("DisableArchiving", in_dataset)
    connection = _gpkg.connect(file)
    try:
        for table in tables:
            kind = _versions.kind(connection, table)
            if kind != "archive":
                raise ExecuteError(f"ERROR 000800: '{table}' is not archive enabled" +
                                   (" — it is branch versioned." if kind == "branch" else "."))
            keep = str(preserve_history or "PRESERVE").upper() in ("PRESERVE", "TRUE")
            _versions.unregister(connection, table, f"{table}_h" if keep else None)
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_dataset))


_ACCESS = {"PRIVATE": "Private", "PUBLIC": "Public", "PROTECTED": "Protected"}


def CreateVersion(in_workspace, parent_version, version_name, access_permission="PRIVATE", version_description=None):
    """A branch version off DEFAULT, seeing DEFAULT as it is now. A branch version's parent is always DEFAULT, so any
    other parent is refused. INFERRED: the name is kept as given — a fenced user tool has no database user to prefix
    it with — and the access is recorded but not enforced, since a tool runs as one user."""
    from . import _versions
    from ._runtime import ExecuteError
    file = _version_workspace(in_workspace)
    access = _ACCESS.get(str(access_permission or "PRIVATE").upper())
    if access is None:
        raise ValueError("arcpy.management.CreateVersion: access_permission is PRIVATE, PUBLIC or PROTECTED.")
    if version_description and len(str(version_description)) > 64:
        raise ExecuteError("ERROR 000800: the description cannot exceed 64 characters.")
    connection = _gpkg.connect(file)
    try:
        parent = _versions.version(connection, parent_version)
        if parent[1] != 0:
            raise NotBuiltError("arcpy.management.CreateVersion: a branch version's parent is DEFAULT; a version of a "
                                "version is traditional versioning's, not built in grapy.")
        name = str(version_name)
        if connection.execute(f"select 1 from {_versions.VERSIONS} where name = ?", (name,)).fetchone():
            raise ExecuteError(f"ERROR 000800: the version '{name}' already exists.")
        branch = (connection.execute(f"select max(branch_id) from {_versions.VERSIONS}").fetchone()[0] or 0) + 1
        now = _versions.now_text()
        connection.execute(f"insert into {_versions.VERSIONS} values (?, ?, ?, ?, ?, '', ?, ?, ?)",
                           (name, branch, _versions.DEFAULT, version_description or "", access, now, now,
                            _versions.current_moment(connection)))
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_workspace))


def AlterVersion(in_workspace, in_version, name=None, description=None, access=None, target_owner=None):
    """A version renamed, described, its access or owner changed. DEFAULT cannot be renamed."""
    from . import _versions
    from ._runtime import ExecuteError
    file = _version_workspace(in_workspace)
    connection = _gpkg.connect(file)
    try:
        current, branch, _ = _versions.version(connection, in_version)
        if _given(name):
            if branch == 0:
                raise ExecuteError("ERROR 000800: DEFAULT cannot be renamed.")
            connection.execute(f"update {_versions.VERSIONS} set name = ? where branch_id = ?", (str(name), branch))
        if _given(description):
            connection.execute(f"update {_versions.VERSIONS} set description = ? where branch_id = ?",
                               (str(description), branch))
        if _given(access):
            value = _ACCESS.get(str(access).upper())
            if value is None:
                raise ValueError("arcpy.management.AlterVersion: access is PRIVATE, PUBLIC or PROTECTED.")
            connection.execute(f"update {_versions.VERSIONS} set access = ? where branch_id = ?", (value, branch))
        if _given(target_owner):
            connection.execute(f"update {_versions.VERSIONS} set owner = ? where branch_id = ?", (str(target_owner), branch))
        connection.execute(f"update {_versions.VERSIONS} set modified = ? where branch_id = ?", (_versions.now_text(), branch))
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_workspace))


def DeleteVersion(in_workspace, version_name):
    """A named version deleted, with its unposted edits. DEFAULT cannot be deleted."""
    from . import _versions
    from ._runtime import ExecuteError
    file = _version_workspace(in_workspace)
    connection = _gpkg.connect(file)
    try:
        _, branch, _ = _versions.version(connection, version_name)
        if branch == 0:
            raise ExecuteError("ERROR 000800: DEFAULT cannot be deleted.")
        for table in _versions.registered(connection, "branch"):
            connection.execute(f'delete from "{_versions.history(table)}" where gdb_branch_id = ?', (branch,))
        connection.execute(f"delete from {_versions.VERSIONS} where branch_id = ?", (branch,))
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_workspace))


def _rows_of_state(connection, table, branch, base, until=None):
    """{object id: {column: value}} of a class as a version sees it (or DEFAULT at a moment)."""
    from . import _versions
    _, oid, _ = _versions.columns(connection, table)
    cursor = connection.execute(f"select * from {_versions.state_sql(connection, table, branch, base, until)}")
    names = [d[0] for d in cursor.description]
    return {row[names.index(oid)]: {n: v for n, v in zip(names, row) if n != oid} for row in cursor.fetchall()}


def _branch_latest(connection, table, branch):
    """{object id: ({column: value}, deleted)} — each object's latest row on the branch."""
    from . import _versions
    names, _, geometry = _versions.columns(connection, table)
    listed = [geometry, *names]
    cursor = connection.execute(
        f'select gdb_objectid, gdb_is_delete, {", ".join(chr(34) + n + chr(34) for n in listed)} from '
        f'(select *, row_number() over (partition by gdb_objectid order by gdb_from_date desc, gdb_archive_oid desc) '
        f'as rank from "{_versions.history(table)}" where gdb_branch_id = ?) where rank = 1', (branch,))
    return {r[0]: (dict(zip(listed, r[2:])), bool(r[1])) for r in cursor.fetchall()}


def _reconcile_one(connection, version_name, definition, resolution, abort, post, delete, said):
    """Reconcile one branch version with DEFAULT, then post it when asked. Conflicts, as the ReconcileVersions page
    defines them: BY_OBJECT, a row changed (updated or deleted) in both DEFAULT — since the version's base moment — and
    the version; BY_ATTRIBUTE, the same column of the same row changed in both, a delete conflicting with any change.
    FAVOR_EDIT_VERSION keeps the version's value, FAVOR_TARGET_VERSION takes DEFAULT's. A field with a conflict filter
    (AddFieldConflictFilter) never conflicts by attribute, as documented. INFERRED: a column both sides set to the
    same value is not a conflict; with BY_ATTRIBUTE, changes to different columns of one row are merged; a filtered
    field changed on both sides keeps the version's value."""
    from . import _versions
    from ._runtime import ExecuteError
    name, branch, base = _versions.version(connection, version_name)
    if branch == 0:
        raise ExecuteError("ERROR 000800: DEFAULT is the target; it is not reconciled with itself.")
    plans, conflicts = [], 0
    for table in _versions.registered(connection, "branch"):
        _, oid, _ = _versions.columns(connection, table)
        filtered = set(_versions.filters(connection, table))
        edits = _branch_latest(connection, table, branch)
        if not edits:
            continue
        original = _rows_of_state(connection, table, 0, 0, base)
        target = _rows_of_state(connection, table, 0, 0, None)
        changed_in_target = {r[0] for r in connection.execute(
            f'select distinct gdb_objectid from "{_versions.history(table)}" where gdb_branch_id = 0 and gdb_from_date > ?',
            (base,))}
        for object_id, (row, deleted) in edits.items():
            if object_id not in changed_in_target or object_id not in original:
                continue
            was, now = original[object_id], target.get(object_id)
            mine = None if deleted else row
            if definition == "BY_OBJECT" or mine is None or now is None:
                if mine == now:
                    continue
                conflicts += 1
                said(f"Conflict on {table} {object_id} (by object).")
                if resolution == "FAVOR_TARGET_VERSION":
                    plans.append((table, object_id, now))
                continue
            merged, clashed = dict(mine), False
            for column, value in now.items():
                if value == was.get(column):
                    continue
                if mine.get(column) == was.get(column):
                    merged[column] = value
                elif mine.get(column) != value and column in filtered:
                    continue   # a field conflict filter: not a conflict, and the version's value stands (INFERRED)
                elif mine.get(column) != value:
                    clashed = True
                    conflicts += 1
                    said(f"Conflict on {table} {object_id}, {column} (by attribute).")
                    if resolution == "FAVOR_TARGET_VERSION":
                        merged[column] = value
            if merged != mine:
                plans.append((table, object_id, merged))
            elif clashed:
                continue
    if conflicts and abort:
        raise ExecuteError(f"ERROR 001156: {conflicts} conflicts were found reconciling '{name}', and the reconcile "
                           "aborts on conflicts.")
    for table, object_id, values in plans:
        _versions.write(connection, table, branch, object_id, values or {}, deleted=values is None)
    connection.execute(f"update {_versions.VERSIONS} set base_moment = ? where branch_id = ?",
                       (_versions.current_moment(connection), branch))
    said(f"Reconciled '{name}' with DEFAULT: {conflicts} conflicts.")
    if post:
        for table in _versions.registered(connection, "branch"):
            names, oid, geometry = _versions.columns(connection, table)
            listed = [geometry, *names]
            for object_id, (row, deleted) in _branch_latest(connection, table, branch).items():
                exists = connection.execute(f'select 1 from "{table}" where "{oid}" = ?', (object_id,)).fetchone()
                if deleted:
                    if exists:
                        connection.execute(f'delete from "{table}" where "{oid}" = ?', (object_id,))
                elif exists:
                    connection.execute(f'update "{table}" set {", ".join(chr(34) + n + chr(34) + " = ?" for n in listed)} '
                                       f'where "{oid}" = ?', [row.get(n) for n in listed] + [object_id])
                else:
                    connection.execute(f'insert into "{table}" ("{oid}", {", ".join(chr(34) + n + chr(34) for n in listed)})'
                                       f' values (?, {", ".join("?" for _ in listed)})', [object_id] + [row.get(n) for n in listed])
            connection.execute(f'delete from "{_versions.history(table)}" where gdb_branch_id = ?', (branch,))
        connection.execute(f"update {_versions.VERSIONS} set base_moment = ?, modified = ? where branch_id = ?",
                           (_versions.current_moment(connection), _versions.now_text(), branch))
        said(f"Posted '{name}' to DEFAULT.")
        if delete:
            connection.execute(f"delete from {_versions.VERSIONS} where branch_id = ?", (branch,))
            said(f"Deleted '{name}'.")
    return conflicts


def ReconcileVersions(input_database, reconcile_mode="ALL_VERSIONS", target_version=None, edit_versions=None,
                      acquire_locks=None, abort_if_conflicts="NO_ABORT", conflict_definition="BY_OBJECT",
                      conflict_resolution="FAVOR_EDIT_VERSION", with_post="NO_POST", with_delete="KEEP_VERSION",
                      out_log=None, proceed_if_conflicts_not_reviewed=None, reconcile_checkout_versions=None):
    """Branch versions reconciled with DEFAULT — "if the input is a branch workspace, the only valid option for this
    parameter is to reconcile with the default version" — and posted to it with POST, then deleted with
    DELETE_VERSION. With no edit versions named, every named version is reconciled. The branch defaults hold: locks are
    not acquired, and conflicts favour the edit version. See ``_reconcile_one`` for the conflict definitions.

    Refused: BLOCKING_VERSIONS (traditional versioning's compress order). INFERRED: the log file holds the messages,
    one per line; conflicts are reported in messages, not kept for review.
    """
    from . import _versions
    from ._runtime import AddMessage
    if str(reconcile_mode or "ALL_VERSIONS").upper() != "ALL_VERSIONS":
        raise NotBuiltError("arcpy.management.ReconcileVersions: BLOCKING_VERSIONS is traditional versioning's, not built "
                            "in grapy.")
    if _given(target_version) and str(target_version).split(".")[-1].upper() != _versions.DEFAULT:
        raise NotBuiltError("arcpy.management.ReconcileVersions: a branch version is reconciled with DEFAULT only.")
    definition = str(conflict_definition or "BY_OBJECT").upper()
    resolution = str(conflict_resolution or "FAVOR_EDIT_VERSION").upper()
    if definition not in ("BY_OBJECT", "BY_ATTRIBUTE") or resolution not in ("FAVOR_EDIT_VERSION", "FAVOR_TARGET_VERSION"):
        raise ValueError("arcpy.management.ReconcileVersions: conflict_definition is BY_OBJECT or BY_ATTRIBUTE, "
                         "conflict_resolution FAVOR_EDIT_VERSION or FAVOR_TARGET_VERSION.")
    file = _version_workspace(input_database)
    lines = []

    def said(text):
        lines.append(text)
        AddMessage(text)

    connection = _gpkg.connect(file)
    try:
        _versions.ensure(connection)
        names = [v for v in _list(edit_versions) if _given(v)] or [
            r[0] for r in connection.execute(f"select name from {_versions.VERSIONS} where branch_id <> 0 order by branch_id")]
        for name in names:
            _reconcile_one(connection, name, definition, resolution,
                           str(abort_if_conflicts or "NO_ABORT").upper() in ("ABORT_CONFLICTS", "TRUE"),
                           str(with_post or "NO_POST").upper() in ("POST", "TRUE"),
                           str(with_delete or "KEEP_VERSION").upper() in ("DELETE_VERSION", "TRUE"), said)
        connection.commit()
    except Exception:
        connection.rollback()
        raise
    finally:
        connection.close()
    if _given(out_log):
        with open(_text(out_log), "w", encoding="utf-8") as written:
            written.write("\n".join(lines) + "\n")
    return Result(_text(input_database))


def ReconcileVersion(in_workspace, version_name, target_name, conflict_definition="BY_OBJECT",
                     conflict_resolution="FAVOR_TARGET_VERSION", aquired_locks=None, abort_if_conflicts="NO_ABORT",
                     post="NO_POST"):
    """One version reconciled with its target, DEFAULT, and posted with POST — ReconcileVersions for one version, with
    this tool's own default resolution, FAVOR_TARGET_VERSION."""
    return ReconcileVersions(in_workspace, "ALL_VERSIONS", target_name, [version_name], aquired_locks,
                             abort_if_conflicts, conflict_definition, conflict_resolution, post)


def ChangeVersion(in_features, version_type, version_name=None, date=None, include_participating=None):
    """A layer switched to a version (TRANSACTIONAL) or to a historical moment (HISTORICAL, at ``date`` in UTC): the da
    cursors on the layer, and every tool given it, then read that version or moment. INFERRED: this tool's page could
    not be read (it answered 404 on 2026-10-30), so its parameters are taken from its arcpy signature; a named
    historical marker is refused, as grapy keeps no markers; ``include_participating`` changes nothing, as grapy's
    classes have no controller datasets."""
    from . import _versions
    layer = _layers_find(in_features)
    if layer is None:
        raise ValueError("arcpy.management.ChangeVersion takes a layer or table view (MakeFeatureLayer, MakeTableView).")
    file, table = _gpkg.split(layer.source)
    connection = _gpkg.connect(file)
    try:
        kind = str(version_type).upper()
        if kind == "TRANSACTIONAL":
            name = _versions.version(connection, version_name or _versions.DEFAULT)[0]
            layer.version, layer.moment = (None if name == _versions.DEFAULT else name), None
        elif kind == "HISTORICAL":
            if not _given(date):
                raise NotBuiltError("arcpy.management.ChangeVersion: a historical marker is not built in grapy; give a "
                                    "date.")
            import datetime
            when = date if isinstance(date, datetime.datetime) else datetime.datetime.fromisoformat(str(date))
            layer.version, layer.moment = None, _versions.moment_at(connection, when)
        else:
            raise ValueError("arcpy.management.ChangeVersion: version_type is TRANSACTIONAL or HISTORICAL.")
    finally:
        connection.close()
    return Result(layer.name)


def TrimArchiveHistory(in_table, trim_mode="DELETE", trim_before_date=None):
    """Archive records older than ``trim_before_date`` (UTC) deleted — all of them with no date: the rows that are no
    longer current, as an archive record is. The class must be archive enabled and not versioned, as documented.
    INFERRED: a record is older than the date when it stopped being current (a later row of its object was made)
    before that date."""
    from . import _versions
    from ._runtime import ExecuteError
    if str(trim_mode or "DELETE").upper() != "DELETE":
        raise ValueError("arcpy.management.TrimArchiveHistory: DELETE is the only trim mode.")
    file, tables = _versioned_tables("TrimArchiveHistory", in_table)
    connection = _gpkg.connect(file)
    try:
        for table in tables:
            if _versions.kind(connection, table) != "archive":
                raise ExecuteError(f"ERROR 000800: '{table}' is not an archive-enabled, non-versioned class.")
            if _given(trim_before_date):
                import datetime
                when = trim_before_date if isinstance(trim_before_date, datetime.datetime) else \
                    datetime.datetime.fromisoformat(str(trim_before_date))
                cutoff = _versions.moment_at(connection, when)
            else:
                cutoff = 2 ** 62
            history = _versions.history(table)
            connection.execute(f'delete from "{history}" where gdb_archive_oid in (select h.gdb_archive_oid from '
                               f'"{history}" h where exists (select 1 from "{history}" later where later.gdb_objectid = '
                               f'h.gdb_objectid and (later.gdb_from_date > h.gdb_from_date or (later.gdb_from_date = '
                               f'h.gdb_from_date and later.gdb_archive_oid > h.gdb_archive_oid)) and later.gdb_from_date <= ?))',
                               (cutoff,))
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_table))


def PruneBranchHistory(in_dataset, out_log, report_only="REPORT_ONLY", system_tables_only="ALL", prune_before_date=None):
    """DEFAULT's history of a branch versioned class pruned: the rows no longer current at the oldest moment a version
    still refers to (its base moment), or at ``prune_before_date`` (UTC) when that is earlier — as documented, "as
    much of the archive history as possible based on referenced moments". REPORT_ONLY (the default) writes the count to
    the log and prunes nothing. INFERRED: a version's base moment is the moment it refers to; SYSTEM_ONLY finds nothing,
    as grapy keeps no extension datasets' internal tables."""
    from . import _versions
    from ._runtime import ExecuteError
    file, tables = _versioned_tables("PruneBranchHistory", in_dataset)
    report = str(report_only or "REPORT_ONLY").upper() in ("REPORT_ONLY", "TRUE")
    system = str(system_tables_only or "ALL").upper() == "SYSTEM_ONLY"
    lines = []
    connection = _gpkg.connect(file)
    try:
        referenced = connection.execute(f"select min(base_moment) from {_versions.VERSIONS} where branch_id <> 0"
                                        ).fetchone()[0]
        cutoff = int(referenced) if referenced is not None else _versions.current_moment(connection)
        if _given(prune_before_date):
            import datetime
            when = prune_before_date if isinstance(prune_before_date, datetime.datetime) else \
                datetime.datetime.fromisoformat(str(prune_before_date))
            cutoff = min(cutoff, _versions.moment_at(connection, when))
        for table in tables:
            if _versions.kind(connection, table) != "branch":
                raise ExecuteError(f"ERROR 000800: '{table}' is not registered as branch versioned.")
            if system:
                lines.append(f"{table}: no internal tables")
                continue
            history = _versions.history(table)
            superseded = (f'select h.gdb_archive_oid from "{history}" h where h.gdb_branch_id = 0 and exists (select 1 '
                          f'from "{history}" later where later.gdb_branch_id = 0 and later.gdb_objectid = h.gdb_objectid '
                          f'and (later.gdb_from_date > h.gdb_from_date or (later.gdb_from_date = h.gdb_from_date and '
                          f'later.gdb_archive_oid > h.gdb_archive_oid)) and later.gdb_from_date <= {cutoff})')
            count = connection.execute(f"select count(*) from ({superseded})").fetchone()[0]
            lines.append(f"{table}: {count} archive rows {'eligible' if report else 'pruned'} before moment {cutoff}")
            if not report:
                connection.execute(f'delete from "{history}" where gdb_archive_oid in ({superseded})')
        connection.commit()
    finally:
        connection.close()
    with open(_text(out_log), "w", encoding="utf-8") as written:
        written.write("\n".join(lines) + "\n")
    return Result(_text(in_dataset))


_REFUSED.update({name: "is traditional versioning's or enterprise geodatabase administration, which grapy's branch "
                       "versions do not have (ADR-227 §5.4)" for name in (
    "Compress", "CreateVersionedView", "DiagnoseVersionMetadata", "DiagnoseVersionTables", "RepairVersionMetadata",
    "RepairVersionTables", "UpdateGeodatabaseConnectionPropertiesToBranch")})
_REFUSED.update({name: "keeps field conflict filters, which grapy's branch versions do not yet honour in "
                       "ReconcileVersions (ADR-227)" for name in ("AddFieldConflictFilter", "RemoveFieldConflictFilter")})
for _built in ("AlterVersion", "ChangeVersion", "CreateVersion", "DeleteVersion", "PruneBranchHistory", "ReconcileVersion",
               "ReconcileVersions", "RegisterAsVersioned", "UnregisterAsVersioned", "EnableArchiving", "DisableArchiving",
               "TrimArchiveHistory"):
    _REFUSED.pop(_built, None)


def _filter_fields(tool, table, fields, on):
    """Field conflict filters set or removed (ADR-227): on a branch versioned class, not on its id or geometry."""
    from . import _versions
    from ._runtime import ExecuteError
    path = _plain(tool, table)
    connection, name = _gpkg.table_of(path)
    try:
        if _versions.kind(connection, name) != "branch":
            raise ExecuteError(f"ERROR 000800: '{name}' is not registered as versioned.")
        names, _, _ = _versions.columns(connection, name)
        by_lower = {n.lower(): n for n in names}
        chosen = []
        for field in _list(fields):
            if str(field).lower() not in by_lower:
                raise ExecuteError(f"ERROR 000800: '{field}' is not a field conflict filters apply to — not the object "
                                   "id, the shape, nor a field the table lacks.")
            chosen.append(by_lower[str(field).lower()])
        _versions.set_filters(connection, name, chosen, on)
        connection.commit()
    finally:
        connection.close()
    return Result(_text(table))


def AddFieldConflictFilter(table, fields):
    """Field conflict filters on fields of a branch versioned class: a reconcile by attribute does not flag a change
    to them on both sides as a conflict, as documented. INFERRED: the class must be registered as versioned."""
    return _filter_fields("AddFieldConflictFilter", table, fields, True)


def RemoveFieldConflictFilter(table, fields):
    """The field conflict filters on those fields removed."""
    return _filter_fields("RemoveFieldConflictFilter", table, fields, False)


for _built in ("AddFieldConflictFilter", "RemoveFieldConflictFilter"):
    _REFUSED.pop(_built, None)


# ---- equivalents of the final sweep (ADR-224 §5.6) --------------------------------------------------------------------

def _geographic(srid):
    """Transformers from a reference to its own geographic system and back."""
    from pyproj import CRS, Transformer
    crs = CRS.from_epsg(srid)
    geographic = crs.geodetic_crs
    return (crs.get_geod(), Transformer.from_crs(crs, geographic, always_xy=True),
            Transformer.from_crs(geographic, crs, always_xy=True))


@_equivalent("geodesic interpolation at equal spacing no longer than the distance", "Karney 2013, as pyproj.Geod computes it")
def GeodeticDensify(in_features, out_feature_class, geodetic_type="GEODESIC", distance="50 Kilometers"):
    """EQUIVALENT: each segment replaced by the geodesic between its end points on the reference's ellipsoid, as
    documented, with vertices at equal spacing no longer than ``distance`` — the page not saying how a shorter last
    piece is handled. A single segment whose ends coincide is not written, as documented. ``LOXODROME``,
    ``GREAT_ELLIPTIC`` and ``NORMAL_SECTION`` are not built."""
    import math
    import shapely
    from . import da
    from ._runtime import ExecuteError
    if str(geodetic_type or "GEODESIC").upper() != "GEODESIC":
        raise NotBuiltError(f"arcpy.management.GeodeticDensify: geodetic_type '{geodetic_type}' is not built in grapy; "
                            "GEODESIC is.")
    columns, kind, srid, _ = _schema(_source(in_features)[0])
    if srid <= 0:
        raise ExecuteError("ERROR 000732: GeodeticDensify needs a coordinate system.")
    parts = str(distance).split()
    metres = float(parts[0]) * (_metres(parts[1], "GeodeticDensify") if len(parts) > 1 else 1.0)
    geod, to_geographic, back = _geographic(srid)

    def densify(coordinates):
        out = []
        for (x0, y0), (x1, y1) in zip(coordinates, coordinates[1:]):
            (lon0, lon1), (lat0, lat1) = to_geographic.transform([x0, x1], [y0, y1])
            length = geod.inv(lon0, lat0, lon1, lat1)[2]
            count = max(0, math.ceil(length / metres) - 1)
            between = geod.npts(lon0, lat0, lon1, lat1, count) if count else []
            xs, ys = back.transform([p[0] for p in between], [p[1] for p in between]) if between else ([], [])
            out.extend([(x0, y0), *zip(xs, ys)])
        out.append(coordinates[-1])
        return out

    def rebuild(shape):
        if shape.geom_type == "LineString":
            return shapely.LineString(densify([c[:2] for c in shape.coords]))
        if shape.geom_type == "Polygon":
            return shapely.Polygon(densify([c[:2] for c in shape.exterior.coords]),
                                   [densify([c[:2] for c in r.coords]) for r in shape.interiors])
        return type(shape)([rebuild(p) for p in shape.geoms])

    out = _path(out_feature_class)
    _gpkg.create(out, kind, srid, columns)
    with da.InsertCursor(out, [*[c.name for c in columns], "SHAPE@"]) as written:
        for _, values, shape in _features(in_features):
            if shape is None or shape.is_empty:
                continue
            coordinates = shapely.get_coordinates(shape).tolist()
            if len(coordinates) == 2 and coordinates[0] == coordinates[1]:
                continue
            written.insertRow((*values, rebuild(shape)))
    return Result(out)


def _bisect(shape, limit, depth=0):
    """A shape cut in halves across its envelope's longer side, and again, until no piece has more than ``limit``
    vertices — or 32 cuts deep, a limit a polygon's own four corners could otherwise never meet."""
    import shapely
    if len(shapely.get_coordinates(shape)) <= limit or depth >= 32:
        return [shape]
    x0, y0, x1, y1 = shape.bounds
    if x1 - x0 >= y1 - y0:
        middle = (x0 + x1) / 2
        halves = [shapely.box(x0 - 1, y0 - 1, middle, y1 + 1), shapely.box(middle, y0 - 1, x1 + 1, y1 + 1)]
    else:
        middle = (y0 + y1) / 2
        halves = [shapely.box(x0 - 1, y0 - 1, x1 + 1, middle), shapely.box(x0 - 1, middle, x1 + 1, y1 + 1)]
    pieces = []
    dimension = shapely.get_dimensions(shape)
    for half in halves:
        part = shape.intersection(half)
        kept = [p for p in _parts(part) if not p.is_empty and shapely.get_dimensions(p) == dimension]
        if kept:
            whole = shapely.union_all(kept) if len(kept) > 1 else kept[0]
            pieces.extend(_bisect(whole, limit, depth + 1))
    return pieces


@_equivalent("recursive bisection across the envelope's longer side until each piece is within the vertex limit",
             "a quadtree-style subdivision")
def Dice(in_features, out_feature_class, vertex_limit):
    """EQUIVALENT: a feature with more vertices than ``vertex_limit`` is cut in two across the longer side of its
    envelope, and each half again, until every piece is within the limit; a feature within it is copied unchanged and
    each piece keeps its feature's attributes, as documented. Esri's own cutting is not published. A layer's ratio
    policy is not built."""
    from . import da
    limit = int(vertex_limit)
    columns, kind, srid, _ = _schema(_source(in_features)[0])
    out = _path(out_feature_class)
    _gpkg.create(out, kind, srid, columns)
    with da.InsertCursor(out, [*[c.name for c in columns], "SHAPE@"]) as written:
        for _, values, shape in _features(in_features):
            if shape is None:
                continue
            for piece in _bisect(shape, limit) if kind in ("Polygon", "Polyline") else [shape]:
                written.insertRow((*values, piece))
    return Result(out)


def _crack(coordinates, nodes, tolerance):
    """A ring's or line's vertices with every node lying on a segment within the tolerance inserted, in order."""
    from shapely.geometry import LineString, Point as ShapelyPoint
    out = []
    for a, b in zip(coordinates, coordinates[1:]):
        out.append(a)
        segment = LineString([a, b])
        inner = []
        for node in nodes:
            point = ShapelyPoint(node)
            if node in (a, b) or segment.distance(point) > tolerance:
                continue
            along = segment.project(point)
            if 0 < along < segment.length:
                inner.append((along, node))
        out.extend(node for _, node in sorted(set(inner)))
    out.append(coordinates[-1])
    return out


@_equivalent("vertex clustering onto the best-ranked feature class's vertex, then cracking at intersections",
             "ADR-225 §5.3's clustering rule")
def Integrate(in_features, cluster_tolerance=None):
    """EQUIVALENT: the features of every class given (``[[features, rank], …]``) have their vertices within the
    tolerance made coincident — onto the vertex of the class with the lower rank number (1 the highest), as documented,
    by ADR-225 §5.3's rule — and a vertex inserted on an edge wherever another feature's vertex or a line's
    intersection lies on it, as documented. INFERRED: the default tolerance is a geodatabase's default xy tolerance
    (see CreateTopology). The inputs are changed in place, as documented."""
    import shapely
    from . import _topology, da
    listed = []
    for entry in _pairs(in_features) if not isinstance(in_features, (list, tuple)) else in_features:
        entry = [entry] if isinstance(entry, str) else list(entry)
        listed.append((_plain("Integrate", entry[0]), int(entry[1]) if len(entry) > 1 and _given(entry[1]) else 1))
    srid = _schema(listed[0][0])[2]
    tolerance = _linear(cluster_tolerance, srid, "Integrate") if _given(cluster_tolerance) else _default_tolerance(srid)
    classes = []
    for path, rank in listed:
        with da.SearchCursor(path, ["OID@", "SHAPE@"]) as rows:
            classes.append((rank, [(oid, shape._shape) for oid, shape in rows if shape is not None]))
    moved = _topology.cluster(classes, tolerance)
    shapes = [{oid: moved.get((i, oid), shape) for oid, shape in features} for i, (_, features) in enumerate(classes)]
    linework = [s.boundary if s.geom_type in ("Polygon", "MultiPolygon") else s
                for by_oid in shapes for s in by_oid.values() if shapely.get_dimensions(s) > 0]
    nodes = {tuple(c) for c in shapely.get_coordinates(shapely.union_all(linework)).tolist()} if linework else set()
    nodes |= {tuple(c) for by_oid in shapes for s in by_oid.values() for c in shapely.get_coordinates(s).tolist()}

    def cracked(shape):
        if shape.geom_type == "LineString":
            return shapely.LineString(_crack([tuple(c[:2]) for c in shape.coords], nodes, tolerance))
        if shape.geom_type == "Polygon":
            return shapely.Polygon(_crack([tuple(c[:2]) for c in shape.exterior.coords], nodes, tolerance),
                                   [_crack([tuple(c[:2]) for c in r.coords], nodes, tolerance) for r in shape.interiors])
        if hasattr(shape, "geoms") and shape.geom_type != "MultiPoint":
            return type(shape)([cracked(p) for p in shape.geoms])
        return shape

    for (path, _), by_oid in zip(listed, shapes):
        with da.UpdateCursor(path, ["OID@", "SHAPE@"]) as rows:
            for oid, shape in rows:
                if oid in by_oid:
                    new = cracked(by_oid[oid])
                    if not new.equals_exact(shape._shape, 0):
                        rows.updateRow([oid, new])
    return Result(";".join(p for p, _ in listed))


def _ellipse(center, major, minor, azimuth, vertices, geod):
    """An ellipse's ring: ``major`` and ``minor`` full axis lengths, the major axis at ``azimuth`` degrees clockwise
    from north; on the ellipsoid (geodesic distances from the centre) when ``geod`` is given, else in the plane."""
    import math
    a, b = major / 2.0, minor / 2.0
    ring = []
    for k in range(vertices):
        phi = 2 * math.pi * k / vertices
        radius = a * b / math.hypot(b * math.cos(phi), a * math.sin(phi)) if a and b else 0.0
        bearing = azimuth + math.degrees(phi)
        if geod is not None:
            lon, lat, _ = geod.fwd(center[0], center[1], bearing, radius)
            ring.append((lon, lat))
        else:
            ring.append((center[0] + radius * math.sin(math.radians(bearing)),
                         center[1] + radius * math.cos(math.radians(bearing))))
    ring.append(ring[0])
    return ring


@_equivalent("a 360-vertex ellipse traced by geodesic distance from its centre", "pyproj.Geod (Karney 2013)")
def TableToEllipse(in_table, out_featureclass, x_field, y_field, major_field, minor_field, distance_units,
                   azimuth_field=None, azimuth_units="DEGREES", id_field=None, spatial_reference=None,
                   attributes="NO_ATTRIBUTES", geometry_type="LINE", method="GEODESIC"):
    """EQUIVALENT: an ellipse around each row's x and y, its major axis rotated by the azimuth clockwise from north, as
    a closed line (``LINE``) or a polygon; ``GEODESIC`` traces it by geodesic distance from the centre, ``PLANAR`` in
    the plane of a projected system, as documented. INFERRED, the page not saying: the major and minor fields hold
    full axis lengths; the ellipse has 360 vertices; fields as BearingDistanceToLine writes them. ``MILS`` is not
    built."""
    import shapely
    from . import da
    from ._reference import SpatialReference
    from ._runtime import ExecuteError
    units = {"DEGREES": 1.0, "RADS": 57.29577951308232, "RADIANS": 57.29577951308232, "GRADS": 0.9}
    unit = str(azimuth_units or "DEGREES").upper()
    if unit not in units:
        raise NotBuiltError(f"arcpy.management.TableToEllipse: azimuth_units '{azimuth_units}' is not built in grapy.")
    columns, srid, rows = _table_rows(in_table)
    srid = _reference_code(spatial_reference, srid if srid > 0 else 4326)
    geodesic = str(method or "GEODESIC").upper() == "GEODESIC"
    reference = SpatialReference(srid)
    if not geodesic and reference.type == "Geographic":
        raise ExecuteError("ERROR 000732: PLANAR ellipses need a projected coordinate system.")
    by_name = {c.name.lower(): c for c in columns}
    lead = [by_name[str(f).lower()] for f in (x_field, y_field, major_field, minor_field, azimuth_field) if _given(f)]
    if _given(id_field) and by_name[str(id_field).lower()] not in lead:
        lead.append(by_name[str(id_field).lower()])
    rest = [c for c in columns if c not in lead] if str(attributes).upper() == "ATTRIBUTES" else None
    out_columns = lead + (rest + [_gpkg.Column("ORIG_FID", "Integer")] if rest is not None else [])
    polygon = str(geometry_type or "LINE").upper() == "POLYGON"
    metres = _metres(distance_units, "TableToEllipse", _units.BEARING_DISTANCE)
    out = _path(out_featureclass)
    _gpkg.create(out, "Polygon" if polygon else "Polyline", srid, out_columns)
    geod = to_geographic = back = None
    if geodesic:
        geod, to_geographic, back = _geographic(srid)
    names = [c.name for c in columns]
    with da.InsertCursor(out, [*[c.name for c in out_columns], "SHAPE@"]) as written:
        for oid, values, _ in rows:
            row = dict(zip(names, values))
            x, y = float(row[by_name[str(x_field).lower()].name]), float(row[by_name[str(y_field).lower()].name])
            major = float(row[by_name[str(major_field).lower()].name]) * metres
            minor = float(row[by_name[str(minor_field).lower()].name]) * metres
            azimuth = float(row[by_name[str(azimuth_field).lower()].name]) * units[unit] if _given(azimuth_field) else 0.0
            if geodesic:
                lon, lat = to_geographic.transform(x, y)
                ring = _ellipse((lon, lat), major, minor, azimuth, 360, geod)
                xs, ys = back.transform([p[0] for p in ring], [p[1] for p in ring])
                ring = list(zip(xs, ys))
            else:
                per = reference.metersPerUnit
                ring = _ellipse((x, y), major / per, minor / per, azimuth, 360, None)
            shape = shapely.Polygon(ring) if polygon else shapely.LineString(ring)
            written.insertRow((*[row[c.name] for c in lead], *([row[c.name] for c in rest] + [oid] if rest is not None else []),
                               shape))
    return Result(out)


_DISTRIBUTIONS = {"UNIFORM": (0.0, 1.0), "INTEGER": (1, 10), "NORMAL": (0.0, 1.0), "EXPONENTIAL": (1.0,),
                  "POISSON": (1.0,), "GAMMA": (1.0, 1.0), "BINOMIAL": (10, 0.5), "GEOMETRIC": (0.5,),
                  "NEGATIVE_BINOMIAL": (10.0, 0.5)}


@_equivalent("NumPy's PCG64 generator seeded from env.randomGenerator", "NumPy random Generator")
def CreateRandomRaster(out_path, out_name, distribution="UNIFORM 0.0 1.0", raster_extent=None, cellsize=None,
                       build_rat="BUILD"):
    """EQUIVALENT: a raster of random values from the documented distributions and their defaults, drawn with NumPy's
    generator from ``env.randomGenerator``'s seed (see ``_seed``) — arcpy's own stream is not published. INFERRED: the
    integer distributions answer 32-bit integers with an attribute table under ``BUILD``, the others doubles;
    ``INTEGER`` draws its minimum and maximum inclusively; ``GAMMA``'s beta is the scale; with no cell size, the
    extent's shorter side over 250, as the conversion tools document their default. An extent is required."""
    import numpy
    from ._raster import _Grid, _value_count
    from ._runtime import env
    from ._reference import reference_of
    words = str(distribution or "UNIFORM").replace(",", " ").split()
    name = words[0].upper()
    if name == "NEGATIVE" and len(words) > 1 and words[1].upper() == "BINOMIAL":
        name, words = "NEGATIVE_BINOMIAL", ["NEGATIVE_BINOMIAL", *words[2:]]
    if name not in _DISTRIBUTIONS:
        raise NotBuiltError(f"arcpy.management.CreateRandomRaster: distribution '{words[0]}' is not built in grapy.")
    given = [float(w) for w in words[1:]]
    p = list(given) + list(_DISTRIBUTIONS[name][len(given):])
    if not _given(raster_extent) or str(raster_extent).upper() in ("MAXOF", "MINOF", "DISPLAY"):
        raise NotBuiltError("arcpy.management.CreateRandomRaster: give the extent as an Extent or 'xmin ymin xmax ymax'.")
    (x0, y0, x1, y1), srid = _extent_of(raster_extent)
    if not srid and env.outputCoordinateSystem is not None:
        srid = reference_of(env.outputCoordinateSystem).factoryCode
    size = float(cellsize) if _given(cellsize) else min(x1 - x0, y1 - y0) / 250
    columns, rows = max(1, int(round((x1 - x0) / size))), max(1, int(round((y1 - y0) / size)))
    random = numpy.random.default_rng(_seed())
    shape = (rows, columns)
    draw = {"UNIFORM": lambda: random.uniform(p[0], p[1], shape),
            "INTEGER": lambda: random.integers(int(p[0]), int(p[1]), shape, endpoint=True),
            "NORMAL": lambda: random.normal(p[0], p[1], shape), "EXPONENTIAL": lambda: random.exponential(p[0], shape),
            "POISSON": lambda: random.poisson(p[0], shape), "GAMMA": lambda: random.gamma(p[0], p[1], shape),
            "BINOMIAL": lambda: random.binomial(int(p[0]), p[1], shape), "GEOMETRIC": lambda: random.geometric(p[0], shape),
            "NEGATIVE_BINOMIAL": lambda: random.negative_binomial(p[0], p[1], shape)}[name]
    array = draw()
    integer = name in ("INTEGER", "POISSON", "BINOMIAL", "GEOMETRIC", "NEGATIVE_BINOMIAL")
    array = array.astype(numpy.int32 if integer else numpy.float64)
    mask = numpy.zeros(shape, bool)
    grid = _Grid(x0, y0 + rows * size, size, size, rows, columns, srid)
    path = _raster_path(os.path.join(_text(out_path), _text(out_name)), "CreateRandomRaster")
    result = _write_raster(array, mask, grid, path)
    if integer and str(build_rat or "BUILD").upper() == "BUILD":
        from ._raster import _write_rat
        _write_rat(path, _value_count(array, mask))
    return result


@_equivalent("a uniform random sample without replacement, NumPy's PCG64 seeded from env.randomGenerator",
             "NumPy random Generator")
def SubsetFeatures(in_features, out_training_feature_class, out_test_feature_class=None, size_of_training_dataset=50,
                   subset_size_units="PERCENTAGE_OF_INPUT"):
    """EQUIVALENT: a random subset of the features as the training set, the rest as the test set when one is asked,
    as documented; drawn with NumPy's generator from ``env.randomGenerator``'s seed — arcpy's Mersenne Twister stream
    is not reproduced. INFERRED: a percentage rounds to the nearest whole feature; features keep their input order."""
    import numpy
    from . import da
    columns, kind, srid, _ = _schema(_source(in_features)[0])
    features = _features(in_features)
    count = len(features)
    size = float(size_of_training_dataset if _given(size_of_training_dataset) else 50)
    wanted = int(round(count * size / 100)) if str(subset_size_units or "PERCENTAGE_OF_INPUT").upper() == \
        "PERCENTAGE_OF_INPUT" else int(size)
    chosen = set(numpy.random.default_rng(_seed()).choice(count, size=min(wanted, count), replace=False).tolist())
    names = [c.name for c in columns]
    for out, keep in ((out_training_feature_class, True), (out_test_feature_class, False)):
        if not _given(out):
            continue
        path = _path(out)
        _gpkg.create(path, kind, srid, columns)
        with da.InsertCursor(path, [*names, "SHAPE@"]) as written:
            for i, (_, values, shape) in enumerate(features):
                if (i in chosen) == keep:
                    written.insertRow((*values, shape))
    return Result(_path(out_training_feature_class), _path(out_test_feature_class) if _given(out_test_feature_class) else "")


@_equivalent("one indicator field per category, named <field>_<category>", "one-hot encoding")
def EncodeField(in_table, field, method="ONEHOT", time_step_interval=None, time_step_alignment="END_TIME",
                reference_time=None):
    """EQUIVALENT: one new field per category of ``field``, 1 where the row has it and 0 elsewhere (``ONECOLD`` the
    reverse), added to the input, as documented. INFERRED, the page not naming them: the fields are named
    ``<field>_<category>``, made valid with ValidateFieldName, short integers, in ascending order of category; a
    null is no category. ``TEMPORAL`` is not built."""
    from . import ValidateFieldName, da
    kind = str(method or "ONEHOT").upper()
    if kind not in ("ONEHOT", "ONECOLD"):
        raise NotBuiltError(f"arcpy.management.EncodeField: method '{method}' is not built in grapy.")
    path = _plain("EncodeField", in_table)
    source = {c.name.lower(): c.name for c in _schema(path)[0]}[str(field).lower()]
    with da.SearchCursor(path, [source]) as rows:
        categories = sorted({r[0] for r in rows if r[0] is not None}, key=lambda v: (str(type(v)), v))
    names = [ValidateFieldName(f"{source}_{c}") for c in categories]
    for name in names:
        AddField(path, name, "SHORT")
    hit, miss = (1, 0) if kind == "ONEHOT" else (0, 1)
    with da.UpdateCursor(path, [source, *names]) as rows:
        for row in rows:
            rows.updateRow([row[0], *[hit if row[0] == c else miss for c in categories]])
    return Result(_text(in_table))


@_equivalent("the documented transformations, Box-Cox's power fitted by maximum likelihood",
             "Box and Cox 1964, scipy.stats.boxcox")
def TransformField(in_table, fields, method="BOX-COX", power=None, shift=None):
    """EQUIVALENT: each field transformed into a new field — ``LOG`` ln(x + shift), ``SQRT`` √(x + shift), ``INVX``
    1/x (0 null), ``BOX-COX`` ((x + λ2)^λ1 − 1)/λ1 (ln(x + λ2) when λ1 is 0) with λ1 fitted by maximum likelihood when
    no power is given, ``INV_BOX-COX`` its inverse, ``INV_SQRT`` x² − shift and ``INV_LOG`` eˣ − shift — as documented.
    Default shifts as documented: LOG, SQRT and BOX-COX add the largest absolute negative value (and, for LOG and
    BOX-COX, about 10⁻⁶ more, so a zero is lifted too). INFERRED: the new field is ``<field>_<METHOD>`` (BOX-COX as
    BOXCOX), a double; an existing one is overwritten, as documented."""
    import math
    import numpy
    from . import da
    kind = str(method or "BOX-COX").upper()
    if kind not in ("LOG", "SQRT", "INVX", "BOX-COX", "INV_BOX-COX", "INV_SQRT", "INV_LOG"):
        raise NotBuiltError(f"arcpy.management.TransformField: method '{method}' is not built in grapy.")
    if kind == "INV_BOX-COX" and not _given(power):
        from ._runtime import ExecuteError
        raise ExecuteError("ERROR 000735: INV_BOX-COX needs a power.")
    path = _plain("TransformField", in_table)
    own = {c.name.lower(): c.name for c in _schema(path)[0]}
    for field in _list(fields):
        source = own[str(field).lower()]
        with da.SearchCursor(path, [source]) as rows:
            values = [r[0] for r in rows]
        present = numpy.array([float(v) for v in values if v is not None])
        lowest = present.min() if present.size else 0.0
        if _given(shift):
            lift = float(shift)
        elif kind in ("LOG", "BOX-COX"):
            lift = abs(lowest) + 1e-6 if lowest <= 0 else 0.0
        elif kind == "SQRT":
            lift = abs(lowest) if lowest < 0 else 0.0
        else:
            lift = 0.0
        lam = float(power) if _given(power) else None
        if kind == "BOX-COX" and lam is None:
            from scipy import stats
            _, lam = stats.boxcox(present + lift)

        def transform(x):
            if x is None:
                return None
            x = float(x)
            if kind == "LOG":
                return math.log(x + lift)
            if kind == "SQRT":
                return math.sqrt(x + lift)
            if kind == "INVX":
                return None if x == 0 else 1.0 / x
            if kind == "BOX-COX":
                return math.log(x + lift) if lam == 0 else ((x + lift) ** lam - 1) / lam
            if kind == "INV_BOX-COX":
                return (math.exp(x) if lam == 0 else (lam * x + 1) ** (1 / lam)) - lift
            if kind == "INV_SQRT":
                return x * x - lift
            return math.exp(x) - lift

        name = f"{source}_{kind.replace('-', '')}"
        if name.lower() not in own:
            AddField(path, name, "DOUBLE")
            own[name.lower()] = name
        with da.UpdateCursor(path, [source, own[name.lower()]]) as rows:
            for row in rows:
                rows.updateRow([row[0], transform(row[0])])
    return Result(_text(in_table))


def _jenks(values, classes):
    """Natural breaks: the class boundaries minimising the within-class sum of squared deviations, by dynamic
    programming (Fisher 1958; Jenks 1967). Answers each class's upper bound."""
    import numpy
    data = numpy.sort(numpy.asarray(values, float))
    n = len(data)
    classes = min(classes, len(numpy.unique(data)))
    prefix, squares = numpy.concatenate([[0], numpy.cumsum(data)]), numpy.concatenate([[0], numpy.cumsum(data ** 2)])

    def cost(i, j):  # data[i:j]
        s, q, m = prefix[j] - prefix[i], squares[j] - squares[i], j - i
        return q - s * s / m
    best = numpy.full((classes + 1, n + 1), numpy.inf)
    split = numpy.zeros((classes + 1, n + 1), int)
    best[0, 0] = 0.0
    for k in range(1, classes + 1):
        for j in range(k, n + 1):
            for i in range(k - 1, j):
                value = best[k - 1, i] + cost(i, j)
                if value < best[k, j]:
                    best[k, j], split[k, j] = value, i
    bounds, j = [], n
    for k in range(classes, 0, -1):
        bounds.append(float(data[j - 1]))
        j = split[k, j]
    return sorted(bounds)


@_equivalent("the documented classification methods, natural breaks by Fisher's exact dynamic programme",
             "Fisher 1958; Jenks 1967")
def ReclassifyField(in_table, field, method="EQUAL_INTERVAL", classes=None, interval=None, standard_deviations="ONE",
                    reclass_table=None, reverse_values="ASC", output_field_name=None):
    """EQUIVALENT: a numeric field classed by ``EQUAL_INTERVAL``, ``DEFINED_INTERVAL``, ``QUANTILE``, ``NATURAL_BREAKS``
    or ``UNIQUE_VALUES`` into two new fields, the class (1 the lowest, or the highest with ``DESC``) and its range, as
    documented; a text field by ``UNIQUE_VALUES`` into one, named ``output_field_name``. INFERRED, the page not naming
    them: the numeric fields are ``<name>_CLASS`` and ``<name>_RANGE``, ``<name>`` being ``output_field_name`` or
    ``<field>_<METHOD>``; a range is written "low - high"; a value on a boundary belongs to the lower class; 5
    classes when none is given; quantile classes split at the nearest ranks. ``MANUAL``, ``STANDARD_DEVIATION`` and
    ``GEOMETRIC_INTERVAL`` are not built."""
    import math
    from . import da
    kind = str(method or "EQUAL_INTERVAL").upper()
    if kind not in ("EQUAL_INTERVAL", "DEFINED_INTERVAL", "QUANTILE", "NATURAL_BREAKS", "UNIQUE_VALUES"):
        raise NotBuiltError(f"arcpy.management.ReclassifyField: method '{method}' is not built in grapy.")
    path = _plain("ReclassifyField", in_table)
    columns = {c.name.lower(): c for c in _schema(path)[0]}
    column = columns[str(field).lower()]
    with da.SearchCursor(path, [column.name]) as rows:
        values = [r[0] for r in rows if r[0] is not None]
    descending = str(reverse_values or "ASC").upper() == "DESC"
    prefix = str(output_field_name) if _given(output_field_name) else f"{column.name}_{kind}"
    if column.type == "String":
        if kind != "UNIQUE_VALUES":
            raise NotBuiltError("arcpy.management.ReclassifyField: a text field is classed by UNIQUE_VALUES only in grapy.")
        order = sorted(set(values), reverse=descending)
        AddField(path, prefix, "LONG")
        with da.UpdateCursor(path, [column.name, prefix]) as rows:
            for row in rows:
                rows.updateRow([row[0], order.index(row[0]) + 1 if row[0] is not None else None])
        return Result(_text(in_table))
    count = int(classes) if _given(classes) else 5
    low, high = min(values), max(values)
    if kind == "UNIQUE_VALUES":
        uppers = sorted(set(values))
    elif kind == "EQUAL_INTERVAL":
        uppers = [low + (high - low) * k / count for k in range(1, count + 1)]
    elif kind == "DEFINED_INTERVAL":
        step = float(interval)
        uppers = [low + step * k for k in range(1, int(math.ceil((high - low) / step)) + 1)] or [high]
    elif kind == "QUANTILE":
        ordered = sorted(values)
        uppers = sorted({ordered[max(0, int(round(len(ordered) * k / count)) - 1)] for k in range(1, count + 1)})
    else:
        uppers = _jenks(values, count)
    uppers[-1] = max(uppers[-1], high)
    lowers = [low] + uppers[:-1]
    class_field, range_field = f"{prefix}_CLASS", f"{prefix}_RANGE"
    AddField(path, class_field, "LONG")
    AddField(path, range_field, "TEXT")

    def of(value):
        if value is None:
            return None, None
        k = next(i for i, upper in enumerate(uppers) if value <= upper)
        number = len(uppers) - k if descending else k + 1
        if kind == "UNIQUE_VALUES":
            return number, f"{uppers[k]:g}"
        return number, f"{lowers[k]:g} - {uppers[k]:g}"

    with da.UpdateCursor(path, [column.name, class_field, range_field]) as rows:
        for row in rows:
            rows.updateRow([row[0], *of(row[0])])
    return Result(_text(in_table))


for _built in ("GeodeticDensify", "Dice", "Integrate", "TableToEllipse", "CreateRandomRaster", "SubsetFeatures",
               "EncodeField", "TransformField", "ReclassifyField"):
    _REFUSED.pop(_built, None)


# ---- coordinate notations -----------------------------------------------------------------------------------------
#
# MGRS and USNG follow NGA's "The Universal Grids and the Transverse Mercator and Polar Stereographic Map Projections"
# (NGA.SIG.0012) and FGDC-STD-011-2001; GARS and GEOREF NGA's published definitions. Their letters skip I and O.

_BANDS = "CDEFGHJKLMNPQRSTUVWX"
_ROWS = "ABCDEFGHJKLMNPQRSTUV"
_COLUMN_SETS = ("ABCDEFGH", "JKLMNPQR", "STUVWXYZ")
_LETTERS_24 = "ABCDEFGHJKLMNPQRSTUVWXYZ"


def _utm(lon, lat):
    """(zone, band letter, easting, northing) of a WGS 1984 point, the Norway and Svalbard zone exceptions kept."""
    import math
    from pyproj import Transformer
    if not -80 <= lat < 84:
        raise ValueError("outside the UTM latitudes (UPS is not built)")
    zone = int(math.floor((lon + 180) / 6)) % 60 + 1
    if 56 <= lat < 64 and 3 <= lon < 12:
        zone = 32
    if 72 <= lat < 84:
        for low, high, z in ((0, 9, 31), (9, 21, 33), (21, 33, 35), (33, 42, 37)):
            if low <= lon < high:
                zone = z
    band = _BANDS[min(19, int(math.floor((lat + 80) / 8)))]
    epsg = (32600 if lat >= 0 else 32700) + zone
    easting, northing = Transformer.from_crs(4326, epsg, always_xy=True).transform(lon, lat)
    return zone, band, easting, northing


def _mgrs(lon, lat, digits, spaced):
    zone, band, easting, northing = _utm(lon, lat)
    column = _COLUMN_SETS[(zone - 1) % 3][int(easting // 100000) - 1]
    row = _ROWS[(int(northing // 100000) + (5 if zone % 2 == 0 else 0)) % 20]
    scale = 10 ** (digits - 5)
    e = int((easting % 100000) * scale) if scale >= 1 else int((easting % 100000) // (10 ** (5 - digits)))
    n = int((northing % 100000) * scale) if scale >= 1 else int((northing % 100000) // (10 ** (5 - digits)))
    parts = [f"{zone}{band}", f"{column}{row}", f"{e:0{digits}d}", f"{n:0{digits}d}"]
    return " ".join(parts) if spaced else "".join(parts)


def _gars(lon, lat):
    import math
    x, y = (lon + 180) * 2, (lat + 90) * 2
    band = int(math.floor(y))
    cell = f"{int(math.floor(x)) + 1:03d}{_LETTERS_24[band // 24]}{_LETTERS_24[band % 24]}"
    fx, fy = x - math.floor(x), y - math.floor(y)
    quadrant = 1 + int(fx * 2) + 2 * (1 - int(fy * 2))
    kx, ky = (fx * 2 - int(fx * 2)), (fy * 2 - int(fy * 2))
    key = (2 - int(ky * 3)) * 3 + int(kx * 3) + 1
    return f"{cell}{quadrant}{key}"


def _georef(lon, lat):
    import math
    x, y = lon + 180, lat + 90
    minutes_x, minutes_y = int((x - math.floor(x)) * 60), int((y - math.floor(y)) * 60)
    return (f"{_LETTERS_24[int(x // 15)]}{'ABCDEFGHJKLM'[int(y // 15)]}{'ABCDEFGHJKLMNPQ'[int(x % 15)]}"
            f"{'ABCDEFGHJKLMNPQ'[int(y % 15)]}{minutes_x:02d}{minutes_y:02d}")


def _hemisphere(value, positive, negative):
    return positive if value >= 0 else negative


def _dms(value, positive, negative, kind):
    """A latitude or longitude written as decimal degrees, degrees and decimal minutes, or degrees, minutes and
    seconds, with its hemisphere letter."""
    letter = _hemisphere(value, positive, negative)
    value = abs(value)
    if kind == "DD":
        return f"{value:.5f}{letter}"
    degrees = int(value)
    minutes = (value - degrees) * 60
    if kind == "DDM":
        return f"{degrees} {minutes:.4f}{letter}"
    whole = int(minutes)
    return f"{degrees} {whole} {(minutes - whole) * 60:.2f}{letter}"


def _parse_dd1(text):
    """A DD_1 value — latitude then longitude, each signed or with a hemisphere letter, separated by a space, comma or
    slash — as (lon, lat)."""
    import re
    parts = [p for p in re.split(r"[\s,/]+", str(text).strip()) if p]
    if len(parts) != 2:
        raise ValueError(text)
    values = []
    for part in parts:
        letter = part[-1].upper() if part[-1].isalpha() else ""
        number = float(part[:-1] if letter else part)
        values.append(-number if letter in ("S", "W") else number)
    return values[1], values[0]


_NOTATIONS_ONE = {"DD_1", "DDM_1", "DMS_1", "UTM_ZONES", "UTM_BANDS", "MGRS", "MGRS16", "USNG", "USNG16", "GARS", "GEOREF"}
_NOTATIONS_TWO = {"DD_2": ("DDLat", "DDLon"), "DDM_2": ("DDMLat", "DDMLon"), "DMS_2": ("DMSLat", "DMSLon"),
                  "DD_NUMERIC": ("DDLat", "DDLon")}


def _notation(kind, lon, lat):
    """The value(s) of one output notation for a WGS 1984 point."""
    if kind in _NOTATIONS_TWO:
        if kind == "DD_NUMERIC":
            return [lat, lon]
        style = kind.split("_")[0]
        return [_dms(lat, "N", "S", style), _dms(lon, "E", "W", style)]
    if kind in ("DD_1", "DDM_1", "DMS_1"):
        style = kind.split("_")[0]
        return [f"{_dms(lat, 'N', 'S', style)} {_dms(lon, 'E', 'W', style)}"]
    if kind in ("UTM_ZONES", "UTM_BANDS"):
        zone, band, easting, northing = _utm(lon, lat)
        letter = band if kind == "UTM_BANDS" else ("N" if lat >= 0 else "S")
        return [f"{zone}{letter}{int(round(easting)):06d}{int(round(northing)):07d}"]
    if kind in ("MGRS", "USNG"):
        return [_mgrs(lon, lat, 4, kind == "USNG")]
    if kind in ("MGRS16", "USNG16"):
        return [_mgrs(lon, lat, 8, kind == "USNG16")]
    if kind == "GARS":
        return [_gars(lon, lat)]
    return [_georef(lon, lat)]


@_equivalent("the published grid definitions (NGA.SIG.0012 for MGRS and UTM, FGDC-STD-011-2001 for USNG, NGA's GARS and "
             "GEOREF) with fixed-precision strings", "NGA and FGDC public specifications")
def ConvertCoordinateNotation(in_table, out_featureclass, x_field, y_field, input_coordinate_format="DD_2",
                              output_coordinate_format="DD_2", id_field=None, spatial_reference=None, in_coor_system=None,
                              exclude_invalid_records="EXCLUDE_INVALID"):
    """EQUIVALENT: a point for each row, carrying all its fields and the new notation, as documented. The grids are the
    public specifications; the strings, which the page does not define, are INFERRED: DD 5 decimals, DDM 4 decimal
    minutes and DMS 2 decimal seconds, each with a hemisphere letter, latitude first in the one-field forms (as the
    page's DD_1 example); UTM as zone, letter, 6-digit easting and 7-digit northing (as the page's examples); MGRS and
    USNG to 4 digits (10 m) and the 16 forms to 8, truncated, USNG spaced; GARS to its 5-minute key; GEOREF to whole
    minutes. Two-field forms are named DDLat/DDLon, DDMLat/DDMLon, DMSLat/DMSLon; a name already taken gets a number,
    as documented. Inputs built: DD_2, DD_NUMERIC, DD_1 and SHAPE; other inputs, GEOREF16 and polar (UPS) grids are not
    built."""
    import shapely
    from . import da
    from ._reference import SpatialReference
    from ._runtime import AddWarning
    source_kind, target_kind = str(input_coordinate_format or "DD_2").upper(), str(output_coordinate_format).upper()
    if source_kind not in ("DD_2", "DD_NUMERIC", "DD_1", "SHAPE"):
        raise NotBuiltError(f"arcpy.management.ConvertCoordinateNotation: input format '{source_kind}' is not built in grapy.")
    if target_kind not in _NOTATIONS_ONE and target_kind not in _NOTATIONS_TWO:
        raise NotBuiltError(f"arcpy.management.ConvertCoordinateNotation: output format '{target_kind}' is not built in grapy.")
    path, where = _source(in_table)
    columns, kind, own_srid, _ = _schema(path)
    names = [c.name for c in columns]
    taken = {n.lower() for n in names} | {_gpkg.OID.lower(), _gpkg.GEOMETRY.lower()}
    new_names = []
    for base in (_NOTATIONS_TWO[target_kind] if target_kind in _NOTATIONS_TWO else (target_kind,)):
        name, i = base, 0
        while name.lower() in taken:
            i += 1
            name = f"{base}_{i}"
        taken.add(name.lower())
        new_names.append(name)
    new_type = "Double" if target_kind == "DD_NUMERIC" else "String"
    srid = _reference_code(spatial_reference, 4326)
    out = _path(out_featureclass)
    _gpkg.create(out, "Point", srid, [*columns, *[_gpkg.Column(n, new_type) for n in new_names]])
    if source_kind == "SHAPE":
        from_srid = _reference_code(in_coor_system, own_srid if own_srid > 0 else 4326)
    else:
        from_srid = _reference_code(in_coor_system, 4326)
    from pyproj import Transformer
    to_wgs = Transformer.from_crs(from_srid, 4326, always_xy=True) if from_srid != 4326 else None
    to_out = Transformer.from_crs(4326, srid, always_xy=True) if srid != 4326 else None
    at = {n.lower(): i for i, n in enumerate(names)}
    keep_invalid = str(exclude_invalid_records or "EXCLUDE_INVALID").upper() == "INCLUDE_INVALID"
    with da.SearchCursor(path, ["OID@", *names, "SHAPE@"], where) as rows, \
            da.InsertCursor(out, [*names, *new_names, "SHAPE@"]) as written:
        for row in rows:
            values = list(row[1:-1])
            try:
                if source_kind == "SHAPE":
                    lon, lat = row[-1]._shape.x, row[-1]._shape.y
                elif source_kind == "DD_1":
                    lon, lat = _parse_dd1(values[at[str(x_field).lower()]])
                else:
                    lon, lat = float(values[at[str(x_field).lower()]]), float(values[at[str(y_field).lower()]])
                if to_wgs is not None:
                    lon, lat = to_wgs.transform(lon, lat)
                if not (-180 <= lon <= 180 and -90 <= lat <= 90):
                    raise ValueError("out of range")
                notation = _notation(target_kind, lon, lat)
                x, y = to_out.transform(lon, lat) if to_out is not None else (lon, lat)
                shape = shapely.Point(x, y)
            except (ValueError, TypeError, KeyError):
                if not keep_invalid:
                    AddWarning(f"WARNING 001003: row {row[0]} has no valid {source_kind} notation and was not written.")
                    continue
                notation, shape = [None] * len(new_names), None
            written.insertRow((*values, *notation, shape))
    return Result(out)


_REFUSED.pop("ConvertCoordinateNotation", None)


# ---- DTED (MIL-PRF-89020B) -------------------------------------------------------------------------------------------

_DTED_SECONDS = {"DTED_0": 30, "DTED_1": 3, "DTED_2": 1}


def _dted_longitude_factor(south):
    """How many times the latitude spacing the longitude spacing is, by the tile's latitude zone (MIL-PRF-89020B)."""
    nearest = min(abs(south), abs(south + 1))
    for limit, factor in ((50, 1), (70, 2), (75, 3), (80, 4)):
        if nearest < limit:
            return factor
    return 6


def RasterToDTED(in_raster, out_folder, dted_level, resampling_type=None):
    """A single-band elevation raster written as one-degree DTED tiles in WGS 1984, as documented: each tile spans its
    degree plus half a post on each edge, so neighbours share a row or column; values are signed 16-bit integers; the
    post spacing is the level's — 30, 3 or 1 arc-second in latitude, and in longitude that times the latitude zone's
    factor (1 below 50°, 2 to 70°, 3 to 75°, 4 to 80°, 6 beyond), as MIL-PRF-89020B defines. The files are written by
    GDAL's DTED driver. INFERRED, the page not saying: the folder and file names are the specification's convention —
    a folder per longitude (``e032``, ``w005``) holding a file per latitude (``n39.dt0``) — named by the tile's
    south-west corner; values are rounded and clipped to the 16-bit range; a cell with no data is written as DTED's
    null, -32767; a tile with no data at all is not written; with no resampling type, the environment's
    ``resamplingMethod``, else ``NEAREST``."""
    import math
    import numpy as np
    import rasterio
    from rasterio.transform import from_origin
    from rasterio.warp import Resampling, reproject, transform_bounds
    from ._raster import Raster
    from ._runtime import env
    level = str(dted_level or "").upper()
    if level not in _DTED_SECONDS:
        raise ValueError(f"RasterToDTED: dted_level '{dted_level}' is not DTED_0, DTED_1 or DTED_2.")
    method = str(resampling_type or getattr(env, "resamplingMethod", None) or "NEAREST").upper()
    resampling = {"NEAREST": Resampling.nearest, "BILINEAR": Resampling.bilinear, "CUBIC": Resampling.cubic}.get(method)
    if resampling is None:
        raise ValueError(f"RasterToDTED: resampling_type '{resampling_type}' is not NEAREST, BILINEAR or CUBIC.")
    raster = in_raster if isinstance(in_raster, Raster) else Raster(in_raster)
    grid = raster._grid
    if grid.srid <= 0:
        raise NotBuiltError("RasterToDTED: a raster with no spatial reference cannot be placed on the WGS 1984 grid.")
    source = np.where(raster._mask, np.nan, raster._array.astype(np.float64))
    source_transform = from_origin(grid.left, grid.top, grid.width, grid.height)
    west, south, east, north = transform_bounds(f"EPSG:{grid.srid}", "EPSG:4326", grid.left, grid.top - grid.rows * grid.height,
                                                grid.left + grid.columns * grid.width, grid.top)
    folder = os.fspath(out_folder)
    os.makedirs(folder, exist_ok=True)
    seconds = _DTED_SECONDS[level]
    for tile_south in range(math.floor(south), math.ceil(north)):
        if not -90 <= tile_south < 90:
            continue
        factor = _dted_longitude_factor(tile_south)
        dy = seconds / 3600.0
        dx = dy * factor
        rows, columns = round(1 / dy) + 1, round(1 / dx) + 1
        for tile_west in range(math.floor(west), math.ceil(east)):
            if not -180 <= tile_west < 180:
                continue
            out = np.full((rows, columns), np.nan)
            reproject(source, out, src_transform=source_transform, src_crs=f"EPSG:{grid.srid}", src_nodata=np.nan,
                      dst_transform=from_origin(tile_west - dx / 2, tile_south + 1 + dy / 2, dx, dy), dst_crs="EPSG:4326",
                      dst_nodata=np.nan, resampling=resampling)
            if np.isnan(out).all():
                continue
            posts = np.where(np.isnan(out), -32767, np.clip(np.round(np.nan_to_num(out)), -32766, 32767)).astype(np.int16)
            column_folder = os.path.join(folder, f"{'e' if tile_west >= 0 else 'w'}{abs(tile_west):03d}")
            os.makedirs(column_folder, exist_ok=True)
            name = f"{'n' if tile_south >= 0 else 's'}{abs(tile_south):02d}.dt{level[-1]}"
            with rasterio.open(os.path.join(column_folder, name), "w", driver="DTED", width=columns, height=rows, count=1,
                               dtype="int16", crs="EPSG:4326", nodata=-32767,
                               transform=from_origin(tile_west - dx / 2, tile_south + 1 + dy / 2, dx, dy)) as written:
                written.write(posts, 1)
    return Result(folder)




# ---- subtypes, field defaults, global IDs and attachments (ADR-228) ----------------------------------------------------
#
# Held in the GeoPackage the tool works on: see grapy._subtypes (subtypes and defaults) and grapy._attachments (global IDs
# and the <name>__ATTACH table with its <name>__ATTACHREL relationship class). Built from the tools' public pages; what a
# page leaves open is INFERRED in the tool's docstring and listed in ADR-228 §5.

def _model_table(tool, in_table):
    """(connection, table) of the dataset a subtype or attachment tool works on: a layer's source, whole — these
    tools change the dataset, not a layer's selection."""
    from ._raster import _is_raster_path
    if _is_raster_path(in_table):
        raise NotBuiltError(f"arcpy.management.{tool} on a raster or mosaic is not built in grapy; its rasters keep no "
                            "fields with defaults.")
    layer = _LAYERS.get(_text(in_table))
    return _gpkg.table_of(layer.source if layer is not None else _text(in_table))


def _column_of(connection, table, name):
    from ._runtime import ExecuteError
    columns = {c.name.lower(): c for c in _gpkg.describe(connection, table)[0]}
    column = columns.get(str(name).lower())
    if column is None:
        raise ExecuteError(f"ERROR 000728: the field '{name}' does not exist in '{table}'.")
    return column


def _subtype_codes(value):
    """Subtype codes as integers, from a code, a list, ``"1;2"``, or ``"1: Name"`` as a dialog shows a subtype
    (INFERRED: the number before the colon is the code)."""
    from ._runtime import ExecuteError
    out = []
    for each in (value if isinstance(value, (list, tuple)) else _list(value) if isinstance(value, str) else [value]):
        text = str(each).split(":", 1)[0].strip()
        try:
            number = float(text)
        except ValueError:
            raise ExecuteError(f"ERROR 000800: '{each}' is not a subtype code; a code is an integer.") from None
        if not number.is_integer():
            raise ExecuteError(f"ERROR 000800: '{each}' is not a subtype code; a code is an integer.")
        out.append(int(number))
    return out


def _subtyped(connection, table, codes=None):
    """The table's subtype field and its codes; an error when it has none, or lacks a code asked for."""
    from . import _subtypes
    from ._runtime import ExecuteError
    found = _subtypes.field(connection, table)
    if found is None:
        raise ExecuteError(f"ERROR 000800: '{table}' has no subtype field; set one first (SetSubtypeField).")
    held = _subtypes.codes(connection, table)
    for code in codes or []:
        if code not in held:
            raise ExecuteError(f"ERROR 000800: '{table}' has no subtype {code}.")
    return found, held


def SetSubtypeField(in_table, field=None, clear_value=False):
    """The integer field that holds a table's subtype codes, as documented; a table that had one loses its subtypes, as
    the page says. ``CLEAR_SUBTYPE_FIELD`` (or True) clears it. INFERRED: clearing leaves the field and its values in
    place and removes the designation, the subtypes and their own defaults and domains; the default subtype code is 0
    until SetDefaultSubtype sets another."""
    from . import _subtypes
    from ._runtime import ExecuteError
    clear = clear_value is True or str(clear_value or "").upper() in ("CLEAR_SUBTYPE_FIELD", "TRUE")
    connection, table = _model_table("SetSubtypeField", in_table)
    try:
        _subtypes.ensure(connection)
        if not clear:
            if not _given(field):
                raise ExecuteError("ERROR 000735: Field Name: value is required unless the subtype field is cleared.")
            column = _column_of(connection, table, field)
            if column.type not in _subtypes.INTEGER_TYPES:
                raise ExecuteError(f"ERROR 000800: the subtype field must be an integer field; '{column.name}' is "
                                   f"{column.type}.")
        _subtypes.drop_subtypes(connection, table)
        if not clear:
            connection.execute("insert into grapy_subtype_field values (?, ?, 0)", (table, column.name))
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_table))


def AddSubtype(in_table, subtype_code, subtype_description):
    """A subtype added: its integer code and its name. A code already there is an error, and the subtype field must be
    set first, as documented."""
    from ._runtime import ExecuteError
    code = _subtype_codes(subtype_code)
    if len(code) != 1:
        raise ExecuteError("ERROR 000800: AddSubtype adds one subtype code.")
    connection, table = _model_table("AddSubtype", in_table)
    try:
        _, held = _subtyped(connection, table)
        if code[0] in held:
            raise ExecuteError(f"ERROR 000800: the subtype code {code[0]} already exists in '{table}'.")
        connection.execute("insert into grapy_subtype values (?, ?, ?)", (table, code[0], str(subtype_description)))
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_table))


def RemoveSubtype(in_table, subtype_code):
    """The subtypes named by code removed, one or several, with their own defaults and domains. INFERRED: a code the
    table does not have is an error; removing the default subtype leaves the default code as it was (the page is
    silent on both). Rows keep the code they hold."""
    codes = _subtype_codes(subtype_code)
    connection, table = _model_table("RemoveSubtype", in_table)
    try:
        _subtyped(connection, table, codes)
        for code in codes:
            connection.execute("delete from grapy_subtype where table_name = ? and code = ?", (table, code))
            connection.execute("delete from grapy_subtype_domain where table_name = ? and subtype_code = ?", (table, code))
            connection.execute("delete from grapy_field_default where table_name = ? and subtype_code = ?", (table, code))
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_table))


def AlterSubtype(in_table, subtype_code, new_subtype_name):
    """A subtype's name changed; its code cannot be, as documented."""
    codes = _subtype_codes(subtype_code)
    connection, table = _model_table("AlterSubtype", in_table)
    try:
        _subtyped(connection, table, codes)
        connection.execute("update grapy_subtype set name = ? where table_name = ? and code = ?",
                           (str(new_subtype_name), table, codes[0]))
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_table))


def SetDefaultSubtype(in_table, subtype_code):
    """The subtype code a new row takes when it names none. INFERRED: the code must be one of the table's subtypes, the
    page saying the table must already hold subtype codes."""
    codes = _subtype_codes(subtype_code)
    connection, table = _model_table("SetDefaultSubtype", in_table)
    try:
        _subtyped(connection, table, codes[:1])
        connection.execute("update grapy_subtype_field set default_code = ? where table_name = ?", (codes[0], table))
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_table))


def _default_typed(value, column):
    """A default value as the field's type holds it; a value of another type is an error, as documented."""
    from ._runtime import ExecuteError
    text = str(value).strip()
    try:
        if column.type in ("SmallInteger", "Integer", "BigInteger"):
            number = float(text)
            if not number.is_integer():
                raise ValueError
            return int(number)
        if column.type in ("Single", "Double"):
            return float(text)
        if column.type == "Date":
            import datetime
            return datetime.datetime.fromisoformat(text).isoformat(" ")
        if column.type == "String":
            return str(value)
    except ValueError:
        raise ExecuteError(f"ERROR 000800: '{value}' is not a {column.type} value for the field '{column.name}'.") from None
    raise NotBuiltError(f"arcpy.management.AssignDefaultToField: a default on a {column.type} field is not built in grapy.")


def AssignDefaultToField(in_table, field_name, default_value=None, subtype_code=None, clear_value=False):
    """A field's default value: what a new row takes when it names no value for the field — for the field itself, or
    for the subtypes named. ``CLEAR_VALUE`` (or an empty value) clears it, as documented. Existing rows are not
    changed. INFERRED: the value is converted to the field's type, and a date is written as ISO 8601."""
    from . import _subtypes
    clear = clear_value is True or str(clear_value or "").upper() in ("CLEAR_VALUE", "TRUE") or \
        default_value is None or str(default_value) == ""
    connection, table = _model_table("AssignDefaultToField", in_table)
    try:
        column = _column_of(connection, table, field_name)
        codes = _subtype_codes(subtype_code) if _given(subtype_code) else [None]
        if codes != [None]:
            _subtyped(connection, table, codes)
        value = None if clear else _default_typed(default_value, column)
        for code in codes:
            _subtypes.set_default(connection, table, column.name, code, value)
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_table))


def AddGlobalIDs(in_datasets):
    """Global IDs added to each class or table, or to every class of a feature dataset: a ``GlobalID`` field holding
    each row's GUID, filled for the rows there and for every row grapy inserts after (grapy._attachments). A dataset
    that has them is left as it is (INFERRED)."""
    from . import _attachments
    listed = list(in_datasets) if isinstance(in_datasets, (list, tuple)) else _list(in_datasets)
    for each in listed:
        path = _text(each)
        layer = _LAYERS.get(path)
        path = layer.source if layer is not None else path
        file, table = _gpkg.split(path)
        connection = _gpkg.connect(file)
        try:
            tables = []
            if table and connection.execute("select 1 from sqlite_master where name = 'grapy_feature_datasets'").fetchone() \
                    and connection.execute("select 1 from grapy_feature_datasets where name = ?", (table,)).fetchone():
                tables = [r[0] for r in connection.execute("select table_name from gpkg_contents where table_name like ?",
                                                           (table + "/%",))]
        finally:
            connection.close()
        if not tables:
            connection, found = _gpkg.table_of(path)
            connection.close()
            tables = [found]
        connection = _gpkg.connect(file)
        try:
            for name in tables:
                _attachments.add_global_ids(connection, name)
            connection.commit()
        finally:
            connection.close()
    return Result(";".join(_text(each) for each in listed))


def EnableAttachments(in_dataset):
    """Attachments enabled: the ``<name>__ATTACH`` table and the ``<name>__ATTACHREL`` relationship class, with global
    IDs added first when the class has none (INFERRED: the page is silent; ArcGIS's attachment tables key on them). A
    class that has attachments gets a warning and nothing more, as documented."""
    from . import _attachments
    from ._runtime import AddWarning
    connection, table = _model_table("EnableAttachments", in_dataset)
    try:
        if _attachments.enabled(connection, table):
            AddWarning(f"WARNING 000800: attachments are already enabled on '{table}'.")
        else:
            _attachments.enable(connection, table)
            connection.commit()
    finally:
        connection.close()
    return Result(_text(in_dataset))


def DisableAttachments(in_dataset):
    """Attachments disabled: every attachment of the class deleted, with its attachment table and relationship class,
    as documented. A class without attachments gets a warning and nothing more."""
    from . import _attachments
    from ._runtime import AddWarning
    connection, table = _model_table("DisableAttachments", in_dataset)
    try:
        if not _attachments.enabled(connection, table):
            AddWarning(f"WARNING 000800: attachments are not enabled on '{table}'.")
        else:
            _attachments.disable(connection, table)
            connection.commit()
    finally:
        connection.close()
    return Result(_text(in_dataset))


def _attached(tool, in_dataset):
    """(file, table, GlobalID field) of a class with attachments; an error without them."""
    from . import _attachments
    from ._runtime import ExecuteError
    connection, table = _model_table(tool, in_dataset)
    try:
        if not _attachments.enabled(connection, table):
            raise ExecuteError(f"ERROR 000800: attachments are not enabled on '{table}'; run EnableAttachments first.")
        key = _attachments.global_id_field(connection, table)
        file = connection.execute("pragma database_list").fetchone()[2]
    finally:
        connection.close()
    return file, table, key


def _join_key(value):
    """A join value compared as text, a whole float written as an integer (INFERRED: a match table's 3.0 finds 3)."""
    return None if value is None else _code_text(value)


def _rows_by_key(file, table, join_field, key):
    from . import da
    by_value = {}
    with da.SearchCursor(f"{file}/{table}", [str(join_field), key]) as rows:
        for value, guid in rows:
            by_value.setdefault(_join_key(value), []).append(guid)
    return by_value


def AddAttachments(in_dataset, in_join_field, in_match_table, in_match_join_field, in_match_path_field,
                   in_working_folder=None):
    """Files attached to the rows whose join field matches a match table's join field, each copied into the attachment
    table, as documented; several rows of the match table attach several files to one row. Selections on the input are
    not honoured, as documented. INFERRED: a relative path is read from the working folder, or the current folder
    without one; a file that is not there is skipped with a warning; the attachment's name is the file's name and its
    content type is its MIME type by extension."""
    from . import _attachments, da
    from ._runtime import AddWarning
    file, table, key = _attached("AddAttachments", in_dataset)
    by_value = _rows_by_key(file, table, in_join_field, key)
    match, where = _source(in_match_table)
    with da.SearchCursor(match, [str(in_match_join_field), str(in_match_path_field)], where) as rows:
        wanted = [(value, path) for value, path in rows]
    connection = _gpkg.connect(file)
    try:
        for value, path in wanted:
            if not path or _join_key(value) not in by_value:
                continue
            full = str(path)
            if not os.path.isabs(full) and _given(in_working_folder):
                full = os.path.join(_text(in_working_folder), full)
            if not os.path.isfile(full):
                AddWarning(f"WARNING 000800: '{full}' does not exist and was not attached.")
                continue
            with open(full, "rb") as opened:
                data = opened.read()
            for guid in by_value[_join_key(value)]:
                _attachments.add(connection, table, guid, os.path.basename(full), data)
        _attachments.refresh(connection, table)
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_dataset))


def RemoveAttachments(in_dataset, in_join_field, in_match_table, in_match_join_field, in_match_name_field=None):
    """Attachments removed from the rows a match table names: those whose name is in the name field, or every one of a
    row whose name is null or empty or when no name field is given, as documented. Selections on the input are not
    honoured. INFERRED: a name is compared with the attachment's name as a file name (a path's directory is
    dropped), ignoring case."""
    from . import _attachments, da
    file, table, key = _attached("RemoveAttachments", in_dataset)
    by_value = _rows_by_key(file, table, in_join_field, key)
    match, where = _source(in_match_table)
    fields = [str(in_match_join_field)] + ([str(in_match_name_field)] if _given(in_match_name_field) else [])
    with da.SearchCursor(match, fields, where) as rows:
        wanted = [(row[0], row[1] if len(row) > 1 else None) for row in rows]
    connection = _gpkg.connect(file)
    try:
        attached = _attachments.attach_table(table)
        for value, name in wanted:
            for guid in by_value.get(_join_key(value), []):
                if name is None or str(name).strip() == "":
                    connection.execute(f'delete from "{attached}" where REL_GLOBALID = ?', (guid,))
                else:
                    connection.execute(f'delete from "{attached}" where REL_GLOBALID = ? and lower(ATT_NAME) = lower(?)',
                                       (guid, os.path.basename(str(name).replace("\\", "/"))))
        _attachments.refresh(connection, table)
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_dataset))


def _file_filter(patterns):
    """A filter's patterns as fnmatch patterns. INFERRED: a pattern with no wildcard, such as ``.jpg``, matches the end
    of a name."""
    out = []
    for each in str(patterns or "").split(";"):
        each = each.strip()
        if each:
            out.append(each.lower() if any(c in each for c in "*?[") else "*" + each.lower())
    return out


def _new_table(tool, out, fields):
    out = _table_out(tool, _text(out))
    CreateTable(os.path.dirname(out), os.path.basename(out))
    for name, kind in fields:
        AddField(out, name, kind)
    return out


def GenerateAttachmentMatchTable(in_dataset, in_folder, out_match_table, in_key_field, in_file_filter=None,
                                 in_use_relative_paths="RELATIVE", match_pattern="EXACT"):
    """A match table for AddAttachments: a row for each file of the folder whose name, extension disregarded, matches a
    row's key field — exactly, as a prefix, as a suffix or anywhere — holding ``MATCHID``, the row's object id, and
    ``FILENAME``, the file's name or with ``ABSOLUTE`` its full path, as documented. INFERRED: the folder's own files,
    not its subfolders'; every file counts (the page names no media types); matching ignores case; rows come in object
    id order and then by file name; a row with a null key matches nothing; a layer's selection is honoured."""
    import fnmatch
    from . import da
    from ._runtime import ExecuteError
    folder = _text(in_folder)
    if not os.path.isdir(folder):
        raise ExecuteError(f"ERROR 000732: the folder {folder} does not exist.")
    pattern = str(match_pattern or "EXACT").upper()
    tests = {"EXACT": lambda k, s: s == k, "PREFIX": lambda k, s: s.startswith(k), "SUFFIX": lambda k, s: s.endswith(k),
             "ANY": lambda k, s: k in s}
    if pattern not in tests:
        raise ExecuteError(f"ERROR 000800: '{match_pattern}' is not EXACT, PREFIX, SUFFIX or ANY.")
    relative = str(in_use_relative_paths or "RELATIVE").upper() not in ("ABSOLUTE", "FALSE")
    filters = _file_filter(in_file_filter)
    files = sorted(n for n in os.listdir(folder) if os.path.isfile(os.path.join(folder, n))
                   and (not filters or any(fnmatch.fnmatch(n.lower(), f) for f in filters)))
    path, where = _source(in_dataset)
    with da.SearchCursor(path, ["OID@", str(in_key_field)], where) as rows:
        keyed = sorted((oid, value) for oid, value in rows)
    out = _new_table("GenerateAttachmentMatchTable", out_match_table, (("MATCHID", "LONG"), ("FILENAME", "TEXT")))
    with da.InsertCursor(out, ["MATCHID", "FILENAME"]) as written:
        for oid, value in keyed:
            if value is None or str(value) == "":
                continue
            key = _code_text(value).lower()
            for name in files:
                if tests[pattern](key, os.path.splitext(name)[0].lower()):
                    written.insertRow((oid, name if relative else os.path.abspath(os.path.join(folder, name))))
    return Result(out)


_UNSAFE = {"/": "-", "\\": "-", "|": "-", "*": "_", "<": "_", ">": "_", "?": "_", '"': "_", ":": "_"}


def _safe(text):
    """A field value made fit for a file or folder name, as documented: slashes, backslashes and pipes become hyphens,
    asterisks, angle brackets and question marks underscores (INFERRED: quotes and colons too); a null is ``None``."""
    return "".join(_UNSAFE.get(c, c) for c in ("None" if text is None else _code_text(text)))


def ExportAttachments(in_dataset, out_location, subdirectory_field=None, name_format="ORIGINAL", name_fields=None):
    """Each attachment written to a file in the output folder, as documented: under its original name, or named by the
    name fields' values joined with underscores (``REPLACE``, its extension kept), or with them before (``PREFIX``) or
    after (``SUFFIX``) the original name; with a subdirectory field, in a folder named by each row's value. A
    layer's selection is honoured. INFERRED: the joined values and the original name are separated by an underscore;
    a name already taken gets ``_1``, ``_2`` … before its extension."""
    from . import _attachments, da
    from ._runtime import ExecuteError
    file, table, key = _attached("ExportAttachments", in_dataset)
    style = str(name_format or "ORIGINAL").upper()
    if style not in ("ORIGINAL", "REPLACE", "PREFIX", "SUFFIX"):
        raise ExecuteError(f"ERROR 000800: '{name_format}' is not ORIGINAL, REPLACE, PREFIX or SUFFIX.")
    named = [str(f) for f in (list(name_fields) if isinstance(name_fields, (list, tuple)) else _list(name_fields))]
    if style != "ORIGINAL" and not named:
        raise ExecuteError(f"ERROR 000735: Name Fields: value is required for {style}.")
    folder = _text(out_location)
    os.makedirs(folder, exist_ok=True)
    path, where = _source(in_dataset)
    subfield = [str(subdirectory_field)] if _given(subdirectory_field) else []
    with da.SearchCursor(path, [key, *subfield, *(named if style != "ORIGINAL" else [])], where) as rows:
        listed = [tuple(r) for r in rows]
    connection = _gpkg.connect(file)
    taken = set()
    try:
        for row in listed:
            guid, rest = row[0], row[1:]
            target = os.path.join(folder, _safe(rest[0])) if subfield else folder
            values = "_".join(_safe(v) for v in rest[len(subfield):])
            for name, data in connection.execute(f'select ATT_NAME, DATA from "{_attachments.attach_table(table)}" '
                                                 'where REL_GLOBALID = ? order by ATTACHMENTID', (guid,)).fetchall():
                stem, extension = os.path.splitext(name)
                stem = {"ORIGINAL": stem, "REPLACE": values, "PREFIX": f"{values}_{stem}",
                        "SUFFIX": f"{stem}_{values}"}[style]
                os.makedirs(target, exist_ok=True)
                out, n = os.path.join(target, stem + extension), 0
                while os.path.normcase(out) in taken or os.path.exists(out):
                    n += 1
                    out = os.path.join(target, f"{stem}_{n}{extension}")
                taken.add(os.path.normcase(out))
                with open(out, "wb") as written:
                    written.write(data)
    finally:
        connection.close()
    return Result(folder)


def _upgrade_fields(tool, in_dataset, fields, add):
    from . import _attachments
    file, table, _ = _attached(tool, in_dataset)
    connection = _gpkg.connect(file)
    try:
        attached = _attachments.attach_table(table)
        have = {r[1].upper() for r in connection.execute(f'pragma table_info("{attached}")')}
        for name in fields:
            if add and name not in have:
                connection.execute(f'alter table "{attached}" add column "{name}" TEXT')
            elif not add and name in have:
                connection.execute(f'alter table "{attached}" drop column "{name}"')
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_dataset))


def UpgradeAttachments(in_dataset, upgrade_level="EXIF"):
    """The attachment table given the documented nullable fields: ``KEYWORDS`` and ``EXIFINFO`` (``EXIF``), and with
    ``EXIF_AND_ALT`` also ``ATT_TITLE``, ``ATT_ALT_TEXT``, ``ATT_CAPTION`` and ``ATT_CREDIT``. INFERRED: all are text;
    a field already there stays as it is."""
    from . import _attachments
    from ._runtime import ExecuteError
    level = str(upgrade_level or "EXIF").upper()
    if level not in ("EXIF", "EXIF_AND_ALT"):
        raise ExecuteError(f"ERROR 000800: '{upgrade_level}' is not EXIF or EXIF_AND_ALT.")
    fields = _attachments.UPGRADE_EXIF + (_attachments.UPGRADE_ALT if level == "EXIF_AND_ALT" else ())
    return _upgrade_fields("UpgradeAttachments", in_dataset, fields, True)


def DowngradeAttachments(in_dataset):
    """An upgraded attachment table put back: the fields UpgradeAttachments adds, removed with their values."""
    from . import _attachments
    return _upgrade_fields("DowngradeAttachments", in_dataset, _attachments.UPGRADE_EXIF + _attachments.UPGRADE_ALT, False)


_PHOTOS = (".jpg", ".jpeg", ".tif", ".tiff")


def _photos(folder, kinds):
    """Every photo file under a folder and its subfolders, by path."""
    from ._runtime import ExecuteError
    if not os.path.isdir(_text(folder)):
        raise ExecuteError(f"ERROR 000732: the folder {_text(folder)} does not exist.")
    found = []
    for root, _, names in os.walk(_text(folder)):
        found.extend(os.path.join(root, n) for n in names if os.path.splitext(n)[1].lower() in kinds)
    return sorted(found)


def _attach_files(path, files_by_oid):
    """Files attached to rows by object id, attachments enabled first when the class has none."""
    from . import _attachments
    connection, table = _gpkg.table_of(path)
    try:
        if not _attachments.enabled(connection, table):
            _attachments.enable(connection, table)
        key = _attachments.global_id_field(connection, table)
        oid = _gpkg.oid_column(connection, table)
        for each, files in files_by_oid.items():
            guid = connection.execute(f'select "{key}" from "{table}" where "{oid}" = ?', (each,)).fetchone()[0]
            for photo in files:
                with open(photo, "rb") as opened:
                    _attachments.add(connection, table, guid, os.path.basename(photo), opened.read())
        _attachments.refresh(connection, table)
        connection.commit()
    finally:
        connection.close()


def _attaching(value):
    return str(value or "ADD_ATTACHMENTS").upper() not in ("NO_ATTACHMENTS", "FALSE")


def GeoTaggedPhotosToPoints(Input_Folder, Output_Feature_Class, Invalid_Photos_Table=None,
                            Include_Non_GeoTagged_Photos="ALL_PHOTOS", Add_Photos_As_Attachments="ADD_ATTACHMENTS",
                            **kwargs):
    """A point for each geotagged photo under a folder and its subfolders, as documented: in WGS 1984, with ``Path``,
    ``Name``, ``DateTime`` (when taken), ``Direction``, ``X``, ``Y`` and ``Z`` (altitude in metres) read from its Exif
    tags (grapy._exif); a photo at 0,0 has no point; ``ALL_PHOTOS`` keeps photos without coordinates with a null shape;
    ``ADD_ATTACHMENTS`` attaches each photo to its point. The documented name ``Include_Non-GeoTagged_Photos`` is
    taken as a keyword too. ``.heic`` photos are not read (their Exif sits in a HEIF box grapy does not parse): each is
    listed as invalid with a warning. INFERRED: the invalid photos table holds ``Path`` and ``Name``; points come in
    order of path; a value the photo does not record is null; the shape is 2D, the altitude kept in ``Z``."""
    from . import _exif, da
    from ._runtime import AddWarning
    include = kwargs.pop("Include_Non-GeoTagged_Photos", Include_Non_GeoTagged_Photos)
    if kwargs:
        raise TypeError(f"GeoTaggedPhotosToPoints got unexpected arguments {sorted(kwargs)}.")
    keep_all = str(include or "ALL_PHOTOS").upper() not in ("ONLY_GEOTAGGED", "FALSE")
    out = _path(_text(Output_Feature_Class))
    CreateFeatureclass(os.path.dirname(out), os.path.basename(out), "POINT", spatial_reference=4326)
    for name, kind in (("Path", "TEXT"), ("Name", "TEXT"), ("DateTime", "DATE"), ("Direction", "DOUBLE"),
                       ("X", "DOUBLE"), ("Y", "DOUBLE"), ("Z", "DOUBLE")):
        AddField(out, name, kind)
    invalid, files_by_oid = [], {}
    with da.InsertCursor(out, ["Path", "Name", "DateTime", "Direction", "X", "Y", "Z", "SHAPE@XY"]) as written:
        for photo in _photos(Input_Folder, _PHOTOS + (".heic",)):
            if photo.lower().endswith(".heic"):
                AddWarning(f"WARNING 000800: {photo} is a HEIF photo, whose Exif grapy does not read.")
                invalid.append(photo)
                continue
            try:
                taken, x, y, z, direction = _exif.read(photo)
            except _exif.Unreadable:
                invalid.append(photo)
                continue
            located = x is not None and y is not None and not (x == 0 and y == 0)
            if not located:
                invalid.append(photo)
                if not keep_all:
                    continue
            oid = written.insertRow((os.path.abspath(photo), os.path.basename(photo), taken, direction,
                                     x if located else None, y if located else None, z, (x, y) if located else None))
            files_by_oid[oid] = [photo]
    if _attaching(Add_Photos_As_Attachments):
        _attach_files(out, files_by_oid)
    if _given(Invalid_Photos_Table):
        table = _new_table("GeoTaggedPhotosToPoints", Invalid_Photos_Table, (("Path", "TEXT"), ("Name", "TEXT")))
        with da.InsertCursor(table, ["Path", "Name"]) as written:
            for photo in invalid:
                written.insertRow((os.path.abspath(photo), os.path.basename(photo)))
    return Result(out)


def MatchPhotosToRowsByTime(Input_Folder, Input_Table, Time_Field, Output_Table, Unmatched_Photos_Table=None,
                            Add_Photos_As_Attachments="ADD_ATTACHMENTS", Time_Tolerance=None, Clock_Offset=None):
    """Each photo under a folder matched to the row whose date field is closest to the time it was taken, shifted by the
    clock offset (positive when the camera's clock is behind), within the tolerance (0: any distance), as documented.
    The output holds ``IN_FID``, ``Photo_Path``, ``Photo_Name`` and ``Match_Diff``; one row may match several photos.
    ``ADD_ATTACHMENTS`` attaches each matched photo to its row. INFERRED: the photo's time is its Exif
    DateTimeOriginal; ``Match_Diff`` is the absolute difference in seconds; with no tolerance any distance matches; a
    tie goes to the lower object id; outputs come in order of photo path; the unmatched photos table holds
    ``Photo_Path`` and ``Photo_Name``; a layer's selection and query are honoured."""
    import datetime
    from . import _exif, da
    from ._runtime import ExecuteError
    path, where = _source(Input_Table)
    connection, table = _gpkg.table_of(path)
    try:
        column = _column_of(connection, table, Time_Field)
    finally:
        connection.close()
    if column.type != "Date":
        raise ExecuteError(f"ERROR 000800: the time field must be a date field; '{column.name}' is {column.type}.")
    times = []
    with da.SearchCursor(path, ["OID@", column.name], where) as rows:
        for oid, value in rows:
            if isinstance(value, str):
                try:
                    value = datetime.datetime.fromisoformat(value)
                except ValueError:
                    value = None
            if value is not None:
                times.append((oid, value))
    tolerance = abs(float(Time_Tolerance)) if _given(Time_Tolerance) else 0.0
    offset = datetime.timedelta(seconds=float(Clock_Offset) if _given(Clock_Offset) else 0.0)
    matched, unmatched, files_by_oid = [], [], {}
    for photo in _photos(Input_Folder, _PHOTOS):
        try:
            taken = _exif.read(photo)[0]
        except _exif.Unreadable:
            taken = None
        best = None
        if taken is not None and times:
            shifted = taken + offset
            best = min((abs((t - shifted).total_seconds()), oid) for oid, t in times)
            if tolerance and best[0] > tolerance:
                best = None
        if best is None:
            unmatched.append(photo)
            continue
        matched.append((best[1], os.path.abspath(photo), os.path.basename(photo), best[0]))
        files_by_oid.setdefault(best[1], []).append(photo)
    out = _new_table("MatchPhotosToRowsByTime", Output_Table, (("IN_FID", "LONG"), ("Photo_Path", "TEXT"),
                                                               ("Photo_Name", "TEXT"), ("Match_Diff", "DOUBLE")))
    with da.InsertCursor(out, ["IN_FID", "Photo_Path", "Photo_Name", "Match_Diff"]) as written:
        for row in matched:
            written.insertRow(row)
    if _given(Unmatched_Photos_Table):
        table_out = _new_table("MatchPhotosToRowsByTime", Unmatched_Photos_Table,
                               (("Photo_Path", "TEXT"), ("Photo_Name", "TEXT")))
        with da.InsertCursor(table_out, ["Photo_Path", "Photo_Name"]) as written:
            for photo in unmatched:
                written.insertRow((os.path.abspath(photo), os.path.basename(photo)))
    if _attaching(Add_Photos_As_Attachments):
        _attach_files(path, files_by_oid)
    return Result(out)


for _built in ("AddSubtype", "AlterSubtype", "RemoveSubtype", "SetDefaultSubtype", "SetSubtypeField",
               "AssignDefaultToField", "AddGlobalIDs", "AddAttachments", "RemoveAttachments", "EnableAttachments",
               "DisableAttachments", "ExportAttachments", "GenerateAttachmentMatchTable", "DowngradeAttachments",
               "UpgradeAttachments", "GeoTaggedPhotosToPoints", "MatchPhotosToRowsByTime"):
    _REFUSED.pop(_built, None)
_REFUSED["MigrateRelationshipClass"] = (
    "has no page grapy could read: doc.esri.com/en/arcgis-pro/latest/tool-reference/data-management/"
    "migrate-relationship-class.html answered 404 (checked 2026-10-30), so which fields it adds and what becomes of "
    "the old keys is unknown; grapy's classes have carried GlobalID fields since ADR-228")


# ---- LAS datasets and TINs (ADR-229): built in _las and _tintools, answered here under arcpy.management's names


from . import _las as _las_tools, _tintools as _tin_tools  # noqa: E402

CreateLasDataset = _las_tools.CreateLasDataset
AddFilesToLasDataset = _las_tools.AddFilesToLasDataset
RemoveFilesFromLasDataset = _las_tools.RemoveFilesFromLasDataset
LasDatasetStatistics = _las_tools.LasDatasetStatistics
MakeLasDatasetLayer = _las_tools.MakeLasDatasetLayer
LasPointStatsAsRaster = _las_tools.LasPointStatsAsRaster
MakeTinLayer = _tin_tools.MakeTinLayer

for _built in ("CreateLasDataset", "AddFilesToLasDataset", "RemoveFilesFromLasDataset", "LasDatasetStatistics",
               "MakeLasDatasetLayer", "LasPointStatsAsRaster", "MakeTinLayer"):
    _REFUSED.pop(_built, None)
_REFUSED.update({
    "BuildLasDatasetPyramid": "is not built in grapy: the pyramid's format and the default point selection are not "
                              "documented, and grapy's LAS datasets read their files directly (ADR-229 §5.2)",
    "TINCompare": "is not built in grapy: the columns of its comparison file are not documented",
    "ProjectLAS": "is not built in grapy: not in ADR-229's first batch of LAS and TIN tools (its condition 4)",
    "InterpolateFromPointCloud": "is not built in grapy: not in ADR-229's first batch of LAS and TIN tools (its "
                                 "condition 4)",
})


# ---- mosaic datasets (ADR-230): built in _mosaic and _mosaictools, answered here under arcpy.management's names


from . import _mosaictools as _mosaic_tools  # noqa: E402

CreateMosaicDataset = _mosaic_tools.CreateMosaicDataset
CreateReferencedMosaicDataset = _mosaic_tools.CreateReferencedMosaicDataset
DeleteMosaicDataset = _mosaic_tools.DeleteMosaicDataset
AddRastersToMosaicDataset = _mosaic_tools.AddRastersToMosaicDataset
RemoveRastersFromMosaicDataset = _mosaic_tools.RemoveRastersFromMosaicDataset
SynchronizeMosaicDataset = _mosaic_tools.SynchronizeMosaicDataset
RepairMosaicDatasetPaths = _mosaic_tools.RepairMosaicDatasetPaths
CalculateCellSizeRanges = _mosaic_tools.CalculateCellSizeRanges
BuildFootprints = _mosaic_tools.BuildFootprints
BuildBoundary = _mosaic_tools.BuildBoundary
SetMosaicDatasetProperties = _mosaic_tools.SetMosaicDatasetProperties
MakeMosaicLayer = _mosaic_tools.MakeMosaicLayer
DefineMosaicDatasetNoData = _mosaic_tools.DefineMosaicDatasetNoData
ExportMosaicDatasetPaths = _mosaic_tools.ExportMosaicDatasetPaths
ExportMosaicDatasetItems = _mosaic_tools.ExportMosaicDatasetItems
ExportMosaicDatasetGeometry = _mosaic_tools.ExportMosaicDatasetGeometry
ImportMosaicDatasetGeometry = _mosaic_tools.ImportMosaicDatasetGeometry

for _built in ("CreateMosaicDataset", "CreateReferencedMosaicDataset", "DeleteMosaicDataset", "AddRastersToMosaicDataset",
               "RemoveRastersFromMosaicDataset", "SynchronizeMosaicDataset", "RepairMosaicDatasetPaths",
               "CalculateCellSizeRanges", "BuildFootprints", "BuildBoundary", "SetMosaicDatasetProperties",
               "MakeMosaicLayer", "DefineMosaicDatasetNoData", "ExportMosaicDatasetPaths", "ExportMosaicDatasetItems",
               "ExportMosaicDatasetGeometry", "ImportMosaicDatasetGeometry"):
    _REFUSED.pop(_built, None)
_REFUSED.update({
    "AnalyzeMosaicDataset": "is not built in grapy: its page describes its checks in a sentence or two each, without "
                            "their rules or thresholds, and says where its results appear only for ArcGIS Pro's Analyze "
                            "Mosaic Dataset pane (ADR-230 §5.5)",
    "BuildOverviews": "is not built in grapy: overviews are resampled images whose file format the pages do not name, "
                      "and grapy reads a mosaic dataset at its items' own cell size (ADR-230 §5.5)",
    "DefineOverviews": "is not built in grapy: it lays out overviews whose file format is not named and whose number of "
                       "levels is 'an optimal value' the page does not define (ADR-230 §5.5)",
    "BuildSeamlines": "is not built in grapy: how ArcGIS generates seamlines is not documented, and grapy stores none — "
                      "the Seamline method draws the halfway lines between item centres as a mosaic dataset is read, "
                      "as this server's image services do (ADR-161, ADR-230 §5.4)",
    "ColorBalanceMosaicDataset": "is not built in grapy: its dodging, histogram and standard deviation balancing are "
                                 "named but not specified, nor is the colour correction it stores (ADR-230 §5.5)",
    "ComputeDirtyArea": "is not built in grapy: its page does not say how a changed item is identified or what fields its "
                        "output has (ADR-230 §5.5)",
    "ComputeMosaicCandidates": "is not built in grapy: how candidates are chosen is not documented (ADR-230 §5.5)",
    "MergeMosaicDatasetItems": "is not built in grapy: an item made of several rasters is Esri's item raster, whose "
                               "pixels and footprint the page does not say how to form; grapy's items are one raster "
                               "file each (ADR-230 §5.2)",
    "SplitMosaicDatasetItems": "is not built in grapy: it splits items made of several rasters, which grapy's mosaic "
                               "datasets do not hold (ADR-230 §5.2)",
    "BuildMosaicDatasetItemCache": "is not built in grapy: an item cache is Esri's Cloud Raster Format (CRF), which grapy "
                                   "does not write (ADR-230 §5.5)",
    "ClearPixelCache": "is not built in grapy: the pixel cache is Esri's own cache, which grapy does not keep "
                       "(ADR-230 §5.5)",
    "MosaicDatasetToMobileMosaicDataset": "is not built in grapy: a mobile mosaic dataset is Esri's mobile geodatabase "
                                          "format (ADR-230 §5.5)",
    "AlterMosaicDatasetSchema": "is not built in grapy: it edits which raster types and processing templates a mosaic "
                                "dataset accepts, and grapy's accept the Raster Dataset type and no templates "
                                "(ADR-230 §5.3)",
    "CreateRasterType": "is not built in grapy: it writes a raster type definition, Esri's own format (ADR-230 §5.3)",
    "GenerateRasterCollection": "is not built in grapy: it builds a collection by a raster function template, Esri's own "
                                "format (ADR-230 §5.5)",
    "CreateCatalogDataset": "is not built in grapy: a catalog dataset is a collection of layers, scene layers and portal "
                            "items, a model ADR-230 does not hold — no tool reads one as data, and a fenced tool cannot "
                            "reach a portal (ADR-223 §5.3)",
    "AddItemsToCatalogDataset": "is not built in grapy: catalog datasets are not held (ADR-230 §5.1)",
    "AddPortalItemsToCatalogDataset": "is not built in grapy: catalog datasets are not held (ADR-230 §5.1), and a fenced "
                                      "tool cannot reach a portal (ADR-223 §5.3)",
})
