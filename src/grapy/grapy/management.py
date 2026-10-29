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


def __getattr__(name):
    if name.startswith("_"):
        raise AttributeError(name)

    def missing(*_, **__):
        raise NotBuiltError(f"arcpy.management.{name} is not built in grapy yet.")

    return missing
