"""arcpy.management's tools grapy builds, by their documented names and parameters — ADR-223 §5.2.

A tool not here raises NotImplementedError naming it (``__getattr__``), so a toolbox using one fails with the name
rather than with an AttributeError a reader has to decode.
"""
from __future__ import annotations

import os

from . import _gpkg
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
    from ._reference import reference_of
    shape = {"POINT": "Point", "MULTIPOINT": "Multipoint", "POLYLINE": "Polyline", "POLYGON": "Polygon"}[geometry_type.upper()]
    reference = reference_of(spatial_reference)
    columns = []
    if template:
        connection, table = _gpkg.table_of(template)
        columns = _gpkg.describe(connection, table)[0]
        connection.close()
    path = _path(out_path, out_name)
    _gpkg.create(path, shape, reference.factoryCode if reference else 0, columns)
    return Result(path)


def AddField(in_table, field_name, field_type, field_precision=None, field_scale=None, field_length=None,
             field_alias=None, field_is_nullable="NULLABLE", *_, **__):
    _gpkg.add_column(in_table, _gpkg.Column(field_name, _FIELD_TYPES.get(field_type.upper(), "String")))
    return Result(in_table)


def GetCount(in_rows):
    connection, table = _gpkg.table_of(in_rows)
    try:
        count = connection.execute(f'select count(*) from "{table}"').fetchone()[0]
    finally:
        connection.close()
    return Result(str(count))


def CopyFeatures(in_features, out_feature_class, *_, **__):
    from . import da
    connection, table = _gpkg.table_of(in_features)
    columns, shape, srid, _ = _gpkg.describe(connection, table)
    connection.close()
    _gpkg.create(_path(out_feature_class), shape, srid, columns)
    names = [c.name for c in columns]
    with da.SearchCursor(in_features, [*names, "SHAPE@"]) as rows, da.InsertCursor(_path(out_feature_class), [*names, "SHAPE@"]) as out:
        for row in rows:
            out.insertRow(row)
    return Result(_path(out_feature_class))


def Delete(in_data, data_type=None):
    file, table = _gpkg.split(in_data)
    if table is None:
        if os.path.exists(file):
            os.remove(file)
    else:
        connection = _gpkg.connect(file)
        connection.execute(f'drop table if exists "{table}"')
        connection.execute("delete from gpkg_contents where table_name = ?", (table,))
        connection.execute("delete from gpkg_geometry_columns where table_name = ?", (table,))
        connection.commit()
        connection.close()
    return Result(in_data)


def CalculateField(in_table, field, expression, expression_type="PYTHON3", code_block="", *_, **__):
    """Each row's field set to a Python expression of its other fields, written ``!name!`` as arcpy writes them."""
    from . import da
    connection, table = _gpkg.table_of(in_table)
    names = [c.name for c in _gpkg.describe(connection, table)[0]]
    connection.close()
    scope: dict = {}
    if code_block:
        exec(code_block, scope)  # noqa: S102 — the tool's own code, in the tool's own fenced process (ADR-223 §5.3)
    compiled = expression
    for name in names:
        compiled = compiled.replace(f"!{name}!", f"__row[{names.index(name)}]")
    with da.UpdateCursor(in_table, names) as rows:
        for row in rows:
            scope["__row"] = row
            row[names.index(field)] = eval(compiled, scope)  # noqa: S307 — as above
            rows.updateRow(row)
    return Result(in_table)


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


def Merge(inputs, output, field_mappings=None, add_source="NO_SOURCE_INFO", *_, **__):
    """The features of every input in one feature class, in the first input's reference; a field missing from an input
    is null in its rows."""
    from . import da
    from ._reference import SpatialReference
    paths = _list(inputs)
    schemas = [_schema(p) for p in paths]
    kinds = {s[1] for s in schemas}
    if len(kinds) > 1:
        from ._runtime import ExecuteError
        raise ExecuteError(f"ERROR 000366: Merge needs inputs of one shape type; these are {', '.join(sorted(kinds))}.")
    srid = schemas[0][2]
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
            have = {c.name.lower(): c.name for c in own}
            fields = [have[n.lower()] for n in names if n.lower() in have]
            with da.SearchCursor(path, [*fields, "SHAPE@"]) as rows:
                for row in rows:
                    values = dict(zip((f.lower() for f in fields), row[:-1]))
                    shape = row[-1]
                    if shape is not None and target is not None and own_srid > 0 and own_srid != srid:
                        shape = shape.projectAs(target)
                    record = [values.get(n.lower()) for n in names]
                    if source:
                        record[-1] = str(path)
                    written.insertRow((*record, shape))
    return Result(out)


def Append(inputs, target, schema_type="TEST", field_mapping=None, subtype="", expression=None, *_, **__):
    """Each input's rows added to an existing feature class. ``TEST`` asks that every target field be in each input, as
    arcpy's does; ``NO_TEST`` fills the fields they share and leaves the rest null."""
    from . import da
    from ._reference import SpatialReference
    from ._runtime import ExecuteError
    columns, _, srid, _ = _schema(target)
    names = [c.name for c in columns]
    reference = SpatialReference(srid) if srid > 0 else None
    for path in _list(inputs):
        own, _, own_srid, _ = _schema(path)
        have = {c.name.lower(): c.name for c in own}
        if str(schema_type).upper() == "TEST" and any(n.lower() not in have for n in names):
            missing = [n for n in names if n.lower() not in have]
            raise ExecuteError(f"ERROR 000466: {path} does not match the schema of the target; it has no {', '.join(missing)}.")
        fields = [have[n.lower()] for n in names if n.lower() in have]
        with da.SearchCursor(path, [*fields, "SHAPE@"], expression) as rows, da.InsertCursor(target, [*names, "SHAPE@"]) as written:
            for row in rows:
                values = dict(zip((f.lower() for f in fields), row[:-1]))
                shape = row[-1]
                if shape is not None and reference is not None and own_srid > 0 and own_srid != srid:
                    shape = shape.projectAs(reference)
                written.insertRow((*[values.get(n.lower()) for n in names], shape))
    return Result(target)


def DeleteField(in_table, drop_field, method="DELETE_FIELDS"):
    """Fields dropped — or, with ``KEEP_FIELDS``, every field but those named."""
    connection, table = _gpkg.table_of(in_table)
    try:
        columns = [c.name for c in _gpkg.describe(connection, table)[0]]
        named = {n.lower() for n in _list(drop_field)}
        drop = [c for c in columns if (c.lower() in named) != (str(method).upper() == "KEEP_FIELDS")]
        for name in drop:
            connection.execute(f'alter table "{table}" drop column "{name}"')
        connection.commit()
    finally:
        connection.close()
    return Result(in_table)


def AlterField(in_table, field, new_field_name=None, new_field_alias=None, *_, **__):
    if new_field_name and new_field_name != field:
        connection, table = _gpkg.table_of(in_table)
        try:
            connection.execute(f'alter table "{table}" rename column "{field}" to "{new_field_name}"')
            connection.commit()
        finally:
            connection.close()
    return Result(in_table)


def AddFields(in_table, field_description, *_, **__):
    """Several fields at once, each ``[name, type, alias, length, default, domain]`` as the tool's value table has them."""
    for described in _pairs(field_description):
        AddField(in_table, described[0], described[1] if len(described) > 1 else "TEXT")
    return Result(in_table)


def DeleteFeatures(in_features):
    connection, table = _gpkg.table_of(in_features)
    try:
        connection.execute(f'delete from "{table}"')
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
        connection.execute(f'alter table "{table}" rename to "{new}"')
        for meta in ("gpkg_contents", "gpkg_geometry_columns"):
            connection.execute(f"update {meta} set table_name = ? where table_name = ?", (new, table))
        connection.execute("update gpkg_contents set identifier = ? where table_name = ?", (new, new))
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
    units or the unit named; a geographic reference must be projected first, as arcpy's planar measures need."""
    from . import da
    from ._reference import SpatialReference
    pairs = [(f, p.upper()) for f, p in _pairs(geometry_property)]
    _, _, srid, _ = _schema(in_features)
    existing = {c.name.lower() for c in _schema(in_features)[0]}
    for field, prop in pairs:
        if field.lower() not in existing:
            _gpkg.add_column(in_features, _gpkg.Column(field, "Integer" if prop.endswith("_COUNT") else "Double"))
    metres = SpatialReference(srid).metersPerUnit if srid > 0 else 1.0
    linear = _unit_scale(length_unit, metres)
    square = _unit_scale(str(area_unit).replace("SQUARE_", ""), metres) ** 2 if area_unit else 1.0
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


_UNITS = {"METERS": 1, "KILOMETERS": 1000, "FEET_US": 1200 / 3937, "FEET_INT": 0.3048, "FEET": 0.3048,
          "MILES_US": 1609.347, "MILES_INT": 1609.344, "MILES": 1609.344, "YARDS": 0.9144, "NAUTICAL_MILES": 1852,
          "CENTIMETERS": 0.01, "MILLIMETERS": 0.001, "INCHES": 0.0254, "DECIMETERS": 0.1, "HECTARES": 100, "ACRES": 63.6149}


def _unit_scale(unit, metres_per_unit):
    """The factor taking a length in the data's units to the unit named; 1 when none is."""
    if not unit:
        return 1.0
    return metres_per_unit / _UNITS[str(unit).upper()]


def MinimumBoundingGeometry(in_features, out_feature_class, geometry_type="RECTANGLE_BY_AREA", group_option="NONE",
                            group_field=None, mbg_fields_option="NO_MBG_FIELDS"):
    """The envelope, convex hull, smallest rotated rectangle or smallest circle around each feature, around all of them
    (``ALL``) or around each group of a ``LIST`` field's values."""
    import shapely
    from . import da
    columns, _, srid, _ = _schema(in_features)
    group = str(group_option).upper()
    fields = [f for f in _list(group_field)] if group == "LIST" else []
    by_name = {c.name.lower(): c for c in columns}
    kind = str(geometry_type).upper()
    bound = {"ENVELOPE": lambda s: shapely.envelope(s), "CONVEX_HULL": lambda s: s.convex_hull,
             "RECTANGLE_BY_AREA": lambda s: s.minimum_rotated_rectangle,
             "RECTANGLE_BY_WIDTH": lambda s: s.minimum_rotated_rectangle,
             "CIRCLE": lambda s: shapely.minimum_bounding_circle(s)}[kind]
    out = _path(out_feature_class)
    out_columns = [by_name[f.lower()] for f in fields] if group != "NONE" else [*columns, _gpkg.Column("ORIG_FID", "Integer")]
    _gpkg.create(out, "Polygon", srid, out_columns)
    names = [c.name for c in columns]
    with da.SearchCursor(in_features, ["OID@", *names, "SHAPE@"]) as rows:
        read = [r for r in rows if r[-1] is not None]
    with da.InsertCursor(out, [*[c.name for c in out_columns], "SHAPE@"]) as written:
        if group == "NONE":
            for row in read:
                written.insertRow((*row[1:-1], row[0], bound(row[-1]._shape)))
        else:
            groups: dict = {}
            for row in read:
                key = tuple(row[1 + names.index(by_name[f.lower()].name)] for f in fields)
                groups.setdefault(key, []).append(row[-1]._shape)
            for key, shapes in groups.items():
                written.insertRow((*key, bound(shapely.GeometryCollection(shapes))))
    return Result(out)


def __getattr__(name):
    if name.startswith("_"):
        raise AttributeError(name)

    def missing(*_, **__):
        raise NotBuiltError(f"arcpy.management.{name} is not built in grapy yet.")

    return missing
