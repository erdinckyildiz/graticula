"""grapy — Graticula's Python library for user tools, keeping arcpy's documented names one to one (ADR-223 §5.2).

A tool begins ``import grapy as arcpy``. What is built answers as arcpy documents it; what is not raises
NotImplementedError naming it. Graticula's own extra tools are under ``grapy.graticula``, never under an arcpy name.
"""
from __future__ import annotations

import os

from . import _gpkg
from ._geometry import Array, Geometry, Multipoint, Point, PointGeometry, Polygon, Polyline
from ._parameter import Field, Filter, Parameter
from ._reference import Extent, SpatialReference
from ._result import Result
from ._raster import NumPyArrayToRaster, Raster, RasterToNumPyArray
from ._runtime import (AddError, AddIDMessage, AddMessage, AddWarning, ExecuteError, ExecuteWarning, NotBuiltError, GetMessageCount,
                       GetMessages, GetParameter, GetParameterAsText, GetParameterCount, ResetProgressor, SetParameter,
                       SetParameterAsText, SetProgressor, SetProgressorLabel, SetProgressorPosition, env)

__version__ = "0.1.0"


class _Described:
    """What Describe answers for a feature class: its fields, shape type, reference, extent and names."""

    def __init__(self, path):
        connection, table = _gpkg.table_of(path)
        try:
            columns, shape, srid, geometry = _gpkg.describe(connection, table)
            oid = _gpkg.oid_column(connection, table)
            bounds = None
            from shapely import union_all
            shapes = [_gpkg.decode(r[0]) for r in connection.execute(f'select "{geometry}" from "{table}"')]
            shapes = [s for s in shapes if s is not None and not s.is_empty]
            if shapes:
                bounds = union_all(shapes).bounds
        finally:
            connection.close()
        self.catalogPath = os.fspath(path)
        self.name = table
        self.baseName = table
        self.dataType = "FeatureClass"
        self.datasetType = "FeatureClass"
        self.shapeType = shape
        self.shapeFieldName = geometry
        self.OIDFieldName = oid
        self.hasOID = True
        self.spatialReference = SpatialReference(srid) if srid > 0 else None
        self.fields = [Field(oid, "OID", False), *[Field(c.name, c.type, c.nullable) for c in columns], Field(geometry, "Geometry")]
        self.extent = Extent(*bounds, self.spatialReference) if bounds else None
        self.featureType = "Simple"
        self.hasZ = False
        self.hasM = False


def Describe(value, datatype=None):
    return _Described(value)


def ListFields(dataset, wild_card=None, field_type=None):
    import fnmatch
    fields = _Described(dataset).fields
    return [f for f in fields
            if (wild_card is None or fnmatch.fnmatch(f.name.lower(), wild_card.lower()))
            and (field_type in (None, "All") or f.type == field_type)]


def ListFeatureClasses(wild_card=None, feature_type=None, feature_dataset=None):
    import fnmatch
    import glob
    found = []
    for file in sorted(glob.glob(os.path.join(env.workspace, "*.gpkg"))):
        connection = _gpkg.connect(file)
        try:
            for (table,) in connection.execute("select table_name from gpkg_contents where data_type = 'features'"):
                name = os.path.basename(file) if table == os.path.splitext(os.path.basename(file))[0] else f"{os.path.basename(file)}/{table}"
                if wild_card is None or fnmatch.fnmatch(name.lower(), wild_card.lower()):
                    found.append(name)
        finally:
            connection.close()
    return found


def Exists(dataset):
    try:
        file, table = _gpkg.split(dataset)
    except ValueError:
        return os.path.exists(os.fspath(dataset))
    if not os.path.exists(file):
        return False
    if table is None:
        return True
    connection = _gpkg.connect(file)
    try:
        return connection.execute("select 1 from gpkg_contents where table_name = ?", (table,)).fetchone() is not None
    finally:
        connection.close()


def CheckProduct(product):
    return "Available"


def CheckExtension(extension):
    return "Available"


def CheckOutExtension(extension):
    return "CheckedOut"


def CheckInExtension(extension):
    return "CheckedIn"


def CreateUniqueName(base_name, workspace=None):
    workspace = workspace or env.workspace
    stem, extension = os.path.splitext(base_name)
    name, n = base_name, 0
    while os.path.exists(os.path.join(workspace, name)):
        name = f"{stem}{n}{extension}"
        n += 1
    return os.path.join(workspace, name)


def ValidateFieldName(name, workspace=None):
    import re
    cleaned = re.sub(r"[^A-Za-z0-9_]", "_", name)
    return cleaned if cleaned and not cleaned[0].isdigit() else f"_{cleaned}"


from . import analysis, conversion, da, graticula, ia, management, sa  # noqa: E402 — they import the names above


def __getattr__(name):
    """A name arcpy documents and grapy has not built: said by name, not as an AttributeError."""
    if name.startswith("_"):
        raise AttributeError(name)
    raise NotBuiltError(f"arcpy.{name} is not built in grapy yet.")
