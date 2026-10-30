"""grapy — Graticula's Python library for user tools, keeping arcpy's documented names one to one (ADR-223 §5.2).

A tool begins ``import grapy as arcpy``. What is built answers as arcpy documents it; what is not raises
NotImplementedError naming it. Graticula's own extra tools are under ``grapy.graticula``, never under an arcpy name.
"""
from __future__ import annotations

import os

from . import _gpkg
from ._geometry import (Array, ArealUnitConversionFactor, AsShape, FromCoordString, FromGeohash, FromWKB, FromWKT,
                        Geometry, LinearUnitConversionFactor, Multipoint, Point, PointGeometry, Polygon, Polyline)
from ._parameter import Field, FieldInfo, Filter, Index, Parameter, ValueTable
from ._reference import Extent, SpatialReference
from ._result import Result
from ._raster import NumPyArrayToRaster, Raster, RasterToNumPyArray
from ._runtime import (AddError, AddIDMessage, AddMessage, AddReturnMessage, AddWarning, ClearEnvironment, EnvManager,
                       ExecuteError, ExecuteWarning, GetAllMessages, GetArgumentCount, GetMaxSeverity, GetMessage,
                       GetMessageCount, GetMessages, GetParameter, GetParameterAsText, GetParameterCount, GetReturnCode,
                       GetSeverity, GetSeverityLevel, GetSystemEnvironment, ListEnvironments, NotBuiltError, ResetEnvironments,
                       ResetProgressor, SetParameter, SetParameterAsText, SetProgressor, SetProgressorLabel,
                       SetProgressorPosition, SetSeverityLevel, env)

__version__ = "0.1.0"


def _field_domains(connection, table):
    """{field name (lower case): domain name} from the GeoPackage schema extension's gpkg_data_columns."""
    try:
        rows = connection.execute("select column_name, constraint_name from gpkg_data_columns where table_name = ? "
                                  "and constraint_name is not null", (table,)).fetchall()
    except Exception:  # No schema extension: no domains.
        return {}
    return {column.lower(): name for column, name in rows}


def _field_titles(connection, table):
    """{field name (lower case): alias} from the schema extension's ``gpkg_data_columns.title``, where AlterField keeps
    an alias."""
    try:
        rows = connection.execute("select column_name, title from gpkg_data_columns where table_name = ? "
                                  "and title is not null", (table,)).fetchall()
    except Exception:  # No schema extension: no aliases.
        return {}
    return {column.lower(): title for column, title in rows}


def _joined_fields(path, connection, table, oid, geometry):
    """The fields of a joined layer as its cursors name them, ``<table>.<field>`` for its own table and each join
    table, or None for anything else."""
    from . import _layers
    layer = _layers.find(path)
    if layer is None or not layer.joins:
        return None
    joined = _layers.joined(layer, connection, table, oid, geometry)
    for schema in joined.attached:
        connection.execute(f"detach database {schema}")
    fields = [Field(f"{table}.{oid}", "OID", False)]
    for name, _, kind, _ in joined.fields:
        fields.append(Field(name, kind))
    fields.append(Field(f"{table}.{geometry}", "Geometry"))
    return fields


class _Described:
    """What Describe answers for a feature class: its fields, shape type, reference, extent and names. A joined layer's
    fields are named ``<table>.<field>``, as its cursors read them; a field's alias is its schema extension title."""

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
            domains = _field_domains(connection, table)
            titles = _field_titles(connection, table)
            joined = _joined_fields(path, connection, table, oid, geometry)
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
        self.fields = [Field(oid, "OID", False), *[Field(c.name, c.type, c.nullable, aliasName=titles.get(c.name.lower()))
                                                    for c in columns], Field(geometry, "Geometry")]
        for field in self.fields:
            field.domain = domains.get(field.name.lower(), "")
        if joined is not None:
            own = f"{table}.".lower()
            for field in joined:
                bare = field.name.split(".", 1)[1].lower() if field.name.lower().startswith(own) else None
                field.domain = domains.get(bare, "") if bare else ""
                if bare and titles.get(bare):
                    field.aliasName = titles[bare]
            self.fields = joined
            self.OIDFieldName = f"{table}.{oid}"
            self.shapeFieldName = f"{table}.{geometry}"
        self.extent = Extent(*bounds, self.spatialReference) if bounds else None
        self.featureType = "Simple"
        self.hasZ = False
        self.hasM = False
        try:
            file = _gpkg.split(path)[0]
            self.path = os.path.dirname(file) if table == os.path.splitext(os.path.basename(file))[0] else file
        except ValueError:  # A layer's name: grapy does not resolve its source's folder here.
            self.path = ""
        if shape == "Geometry" and srid == 0:
            self.dataType = self.datasetType = "Table"


class _DescribedRaster:
    """What Describe answers for a raster dataset: its size, cells, extent, reference and type."""

    def __init__(self, raster):
        self.catalogPath = raster.catalogPath or ""
        self.name = self.baseName = os.path.basename(self.catalogPath)
        self.path = os.path.dirname(self.catalogPath)
        self.dataType = self.datasetType = "RasterDataset"
        self.format = "TIFF" if self.catalogPath.lower().endswith((".tif", ".tiff")) else \
            _RASTER_EXTENSIONS.get(os.path.splitext(self.catalogPath)[1].lower(), "")
        self.bandCount = raster.bandCount
        self.height, self.width = raster.height, raster.width
        self.meanCellHeight, self.meanCellWidth = raster.meanCellHeight, raster.meanCellWidth
        self.extent = raster.extent
        self.spatialReference = raster.spatialReference
        self.pixelType = raster.pixelType
        self.noDataValue = raster.noDataValue
        self.isInteger = raster.isInteger


class _DescribedWorkspace:
    """What Describe answers for a folder or a GeoPackage of several tables."""

    def __init__(self, path):
        path = os.fspath(path)
        self.catalogPath = path
        self.name = self.baseName = os.path.basename(path.rstrip("/\\"))
        self.path = os.path.dirname(path.rstrip("/\\"))
        self.dataType = "Workspace" if path.lower().endswith(".gpkg") else "Folder"
        self.workspaceType = "LocalDatabase" if path.lower().endswith(".gpkg") else "FileSystem"
        self.workspaceFactoryProgID = ""
        self.children = []


def Describe(value, datatype=None):
    """A description of a feature class or table, a raster dataset (a GeoTIFF or other raster file, or a Raster
    object), a folder, a GeoPackage of several tables (a workspace), or a topology, relationship class or network
    dataset held in a GeoPackage (ADR-225, ADR-226)."""
    if isinstance(value, Raster):
        return _DescribedRaster(value)
    try:
        path = os.fspath(value)
    except TypeError:  # A layer object: described by its source.
        return _Described(value)
    if os.path.isdir(path):
        return _DescribedWorkspace(path)
    if os.path.splitext(path)[1].lower() in _RASTER_EXTENSIONS:
        return _DescribedRaster(Raster(path))
    if path.lower().endswith(".gpkg") and os.path.exists(path) and len(_contents(path)) != 1:
        return _DescribedWorkspace(path)
    from . import _network, _relationships, _topology
    for model in (_network, _topology, _relationships):
        described = model.describe(path)
        if described is not None:
            return described
    return _Described(value)


def ListFields(dataset, wild_card=None, field_type=None):
    import fnmatch
    fields = _Described(dataset).fields
    return [f for f in fields
            if (wild_card is None or fnmatch.fnmatch(f.name.lower(), wild_card.lower()))
            and (field_type in (None, "All") or f.type == field_type)]


def ListVersions(sde_workspace=None):
    """The names of a workspace's versions — DEFAULT and the branch versions made off it (ADR-227) — as a list of
    strings, as documented; ``env.workspace`` when none is given. arcpy.da.ListVersions answers them as objects.
    INFERRED: names are written as they were made, DEFAULT unqualified: a GeoPackage has no database owners."""
    from .da import ListVersions as versions
    return [v.name for v in versions(sde_workspace)]


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
        if connection.execute("select 1 from gpkg_contents where table_name = ?", (table,)).fetchone() is not None:
            return True
        # A feature dataset made by management.CreateFeatureDataset is a row of grapy_feature_datasets.
        if connection.execute("select 1 from sqlite_master where name = 'grapy_feature_datasets'").fetchone():
            return connection.execute("select 1 from grapy_feature_datasets where name = ?",
                                      (table.strip("/"),)).fetchone() is not None
        return False
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


# ---- what a workspace holds ------------------------------------------------------------------------------------------
#
# A workspace is a folder or a GeoPackage. In a folder, a GeoPackage holding one table is named by its file
# (``parcels.gpkg``) and one holding several by file and table (``data.gpkg/roads``), as ListFeatureClasses has always
# named them; in a GeoPackage workspace a table is named by itself. A *table* is a GeoPackage attributes table, or a
# feature table whose geometry type is GEOMETRY in reference 0 — how grapy's own tools write a table. INFERRED: that
# second rule, which is grapy's convention rather than the GeoPackage's.

_RASTER_EXTENSIONS = {".tif": "TIF", ".tiff": "TIF", ".img": "IMG", ".jp2": "JP2", ".jpg": "JPG", ".jpeg": "JPG",
                      ".png": "PNG", ".bmp": "BMP", ".gif": "GIF"}


def _matches(name, wild_card):
    import fnmatch
    return wild_card in (None, "", "*") or fnmatch.fnmatch(name.lower(), str(wild_card).lower())


def _contents(file):
    """[(table, kind, shape)] of a GeoPackage: kind 'features', 'table' or 'raster'."""
    connection = _gpkg.connect(file)
    try:
        rows = connection.execute("select c.table_name, c.data_type, c.srs_id, g.geometry_type_name from gpkg_contents c "
                                  "left join gpkg_geometry_columns g on g.table_name = c.table_name").fetchall()
    except Exception:  # Not a GeoPackage, or one without contents.
        return []
    finally:
        connection.close()
    out = []
    for table, data_type, srid, geometry in rows:
        if data_type == "attributes" or (data_type == "features" and (geometry or "").upper() == "GEOMETRY" and not srid):
            out.append((table, "table", "Geometry"))
        elif data_type == "features":
            out.append((table, "features", _gpkg._SHAPES.get((geometry or "GEOMETRY").upper(), "Geometry")))
        elif data_type in ("tiles", "2d-gridded-coverage"):
            out.append((table, "raster", None))
    return out


def _entries(kind):
    """(name, shape) of every table of ``kind`` in the current workspace."""
    import glob
    workspace = os.fspath(env.workspace or os.getcwd())
    if workspace.lower().endswith(".gpkg"):
        return [(table, shape) for table, k, shape in _contents(workspace) if k == kind] if os.path.exists(workspace) else []
    found = []
    for file in sorted(glob.glob(os.path.join(workspace, "*.gpkg"))):
        contents = _contents(file)
        base = os.path.basename(file)
        stem = os.path.splitext(base)[0]
        for table, k, shape in contents:
            if k == kind:
                found.append((base if table == stem or len(contents) == 1 else f"{base}/{table}", shape))
    return found


def ListFeatureClasses(wild_card=None, feature_type=None, feature_dataset=None):
    """The feature classes of the current workspace, filtered by a case-insensitive wildcard and by shape type (Point,
    Multipoint, Polyline, Polygon; Line is Polyline). INFERRED: a GeoPackage has no feature datasets, so naming one
    answers an empty list, and the feature types grapy cannot hold (Annotation, Dimension, Multipatch …) find none."""
    if feature_dataset not in (None, "", "#"):
        return []
    wanted = str(feature_type or "All").upper()
    wanted = "POLYLINE" if wanted == "LINE" else wanted
    return [name for name, shape in _entries("features")
            if _matches(name, wild_card) and (wanted in ("ALL", "") or shape.upper() == wanted)]


def ListTables(wild_card=None, table_type=None):
    """The stand-alone tables of the current workspace. dBASE and INFO tables are not written by grapy, so asking for
    those answers an empty list (INFERRED)."""
    if str(table_type or "ALL").upper() in ("DBASE", "INFO"):
        return []
    return [name for name, _ in _entries("table") if _matches(name, wild_card)]


def ListRasters(wild_card=None, raster_type=None):
    """The raster datasets of the current workspace: in a folder, its raster files by extension (TIF, IMG, JP2, JPG,
    PNG, BMP, GIF) and its Esri grids (folders holding hdr.adf); in a GeoPackage, its tile and coverage tables."""
    workspace = os.fspath(env.workspace or os.getcwd())
    wanted = str(raster_type or "All").upper()
    if workspace.lower().endswith(".gpkg"):
        return [name for name, _ in _entries("raster") if _matches(name, wild_card)] if wanted == "ALL" else []
    found = []
    for name in sorted(os.listdir(workspace)):
        path = os.path.join(workspace, name)
        if os.path.isfile(path):
            kind = _RASTER_EXTENSIONS.get(os.path.splitext(name)[1].lower())
        else:
            kind = "GRID" if os.path.exists(os.path.join(path, "hdr.adf")) else None
        if kind and wanted in ("ALL", kind) and _matches(name, wild_card):
            found.append(name)
    return found


def ListDatasets(wild_card=None, feature_type=None):
    """The datasets of the current workspace. grapy holds no feature datasets, mosaics, networks, TINs or topologies,
    so the datasets are raster datasets: All and Raster list them, any other type finds none (INFERRED)."""
    if str(feature_type or "All").upper() not in ("ALL", "RASTER"):
        return []
    return ListRasters(wild_card)


def ListWorkspaces(wild_card=None, workspace_type=None):
    """The workspaces in the current workspace, as full paths: folders (Folder), file geodatabases (FileGDB), SQLite
    and GeoPackage files (SQLite), and .sde connection files (SDE)."""
    workspace = os.fspath(env.workspace or os.getcwd())
    wanted = str(workspace_type or "All").upper()
    found = []
    if not os.path.isdir(workspace):
        return found
    for name in sorted(os.listdir(workspace)):
        path = os.path.join(workspace, name)
        lower = name.lower()
        if os.path.isdir(path):
            kind = "FILEGDB" if lower.endswith(".gdb") else "FOLDER"
        elif lower.endswith((".gpkg", ".sqlite")):
            kind = "SQLITE"
        elif lower.endswith(".sde"):
            kind = "SDE"
        else:
            continue
        if wanted in ("ALL", kind) and _matches(name, wild_card):
            found.append(path)
    return found


def ListFiles(wild_card=None):
    """The files of the current folder workspace, by name. INFERRED: a GeoPackage workspace holds no files and answers
    an empty list."""
    workspace = os.fspath(env.workspace or os.getcwd())
    if not os.path.isdir(workspace):
        return []
    return [n for n in sorted(os.listdir(workspace)) if os.path.isfile(os.path.join(workspace, n)) and _matches(n, wild_card)]


def ListIndexes(dataset, wild_card=None):
    """The attribute indexes of a table, as Index objects: SQLite's indexes on it, their fields in order. INFERRED: an
    index is ascending unless every one of its columns is declared descending."""
    connection, table = _gpkg.table_of(dataset)
    try:
        found = []
        for _, name, unique, *_ in connection.execute(f'pragma index_list("{table}")').fetchall():
            columns = connection.execute(f'pragma index_xinfo("{name}")').fetchall()
            keyed = [c for c in columns if c[5] and c[2] is not None]
            descending = bool(keyed) and all(c[3] for c in keyed)
            if _matches(name, wild_card):
                found.append(Index(name, [c[2] for c in keyed], bool(unique), not descending))
        return found
    finally:
        connection.close()


def AddFieldDelimiters(datasource, field):
    """The field name in the delimiters of the data source's SQL. INFERRED: grapy's data are SQLite, whose SQL
    delimits a name with double quotes; a name already delimited is returned as it is."""
    text = str(field)
    return text if text.startswith('"') and text.endswith('"') else f'"{text}"'


def _qualified(name, parts):
    pieces = str(name).split(".")
    pieces = [""] * (parts - len(pieces)) + pieces[-parts:]
    return ", ".join(pieces)


def ParseFieldName(name, workspace=None):
    """"database, owner, table, field". INFERRED: a GeoPackage has neither database nor owner, so those are empty, and
    a name qualified with dots is read from the right."""
    return _qualified(name, 4)


def ParseTableName(name, workspace=None):
    """"database, owner, table", as ParseFieldName's."""
    return _qualified(name, 3)


def ValidateTableName(name, workspace=None):
    """The name with each character that is not a letter, digit or underscore made an underscore, and a leading digit
    preceded by one. INFERRED: SQLite takes almost any table name, so grapy keeps the rule ValidateFieldName uses."""
    return ValidateFieldName(name, workspace)


_SCRATCH_EXTENSIONS = {"FEATURECLASS": ".gpkg", "DATASET": ".gpkg", "GEODATASET": ".gpkg", "ARCINFOTABLE": ".gpkg",
                       "RASTERDATASET": ".tif", "FOLDER": "", "WORKSPACE": "", "": ".gpkg"}


def CreateScratchName(prefix="xx", suffix="", data_type="", workspace=None):
    """A path in the workspace that does not exist yet: the prefix (``xx`` by default), a number, the suffix.
    INFERRED: in a folder, a feature class, table or dataset is a GeoPackage (``.gpkg``) and a raster dataset a GeoTIFF
    (``.tif``); in a GeoPackage workspace the name is a table in it. Coverage, shapefile, network, terrain and the other
    types grapy does not write are refused."""
    workspace = os.fspath(workspace or env.workspace or os.getcwd())
    kind = str(data_type or "").replace(" ", "").upper()
    if kind not in _SCRATCH_EXTENSIONS:
        raise NotBuiltError(f"arcpy.CreateScratchName: data_type '{data_type}' is not built in grapy.")
    prefix = "xx" if prefix in (None, "") else str(prefix)
    suffix = str(suffix or "")
    for n in range(100000):
        name = f"{prefix}{n}{suffix}"
        if workspace.lower().endswith(".gpkg"):
            path = f"{workspace}/{name}"
            if not Exists(path):
                return path
        else:
            path = os.path.join(workspace, name + _SCRATCH_EXTENSIONS[kind])
            if not os.path.exists(path):
                return path
    raise RuntimeError("arcpy.CreateScratchName: no free name was found.")


def TestSchemaLock(dataset):
    """Whether an exclusive lock can be taken on the dataset's GeoPackage now — what a schema change needs."""
    import sqlite3
    file, _ = _gpkg.split(dataset)
    try:
        connection = sqlite3.connect(file, timeout=0)
    except sqlite3.Error:
        return False
    try:
        connection.execute("begin exclusive")
        connection.execute("rollback")
        return True
    except sqlite3.OperationalError:
        return False
    finally:
        connection.close()


class FeatureSet:
    """A set of features a tool takes or gives: a reference to a feature class, or — loaded from Esri JSON or GeoJSON,
    or with a where clause — a copy in the scratch workspace. A tool given a FeatureSet reads the feature class it
    refers to, as documented ("will modify the original feature class"). A hosted layer's URL is refused: a user
    tool has no network (ADR-223 §5.4)."""

    _table = False

    def __init__(self, table_path=None, where_clause=None, time_filter=None, renderer=None, is_renderer=None,
                 geojson_geometry_type=None):
        self._path = None
        if table_path not in (None, ""):
            FeatureSet.load(self, table_path, where_clause, time_filter, renderer, is_renderer, geojson_geometry_type)

    def load(self, table_path=None, where_clause=None, time_filter=None, renderer=None, is_renderer=None,
             geojson_geometry_type=None):
        import json
        if time_filter not in (None, ""):
            raise NotBuiltError(f"arcpy.{type(self).__name__}.load: time_filter is not built in grapy.")
        text = table_path if isinstance(table_path, str) else (json.dumps(table_path) if isinstance(table_path, dict) else os.fspath(table_path))
        stripped = text.strip()
        if stripped.lower().startswith(("http://", "https://")) or stripped.startswith("{") and '"url"' in stripped:
            raise NotBuiltError(f"arcpy.{type(self).__name__}: a URL is not built in grapy — a user tool has no network.")
        if stripped.startswith("{"):
            self._path = self._from_json(stripped)
        else:
            self._path = text
        if where_clause not in (None, ""):
            from .analysis import Select
            self._path = Select(self._path, CreateScratchName("fs", "", "FeatureClass", env.scratchWorkspace),
                                where_clause).getOutput(0)
        return self

    @staticmethod
    def _from_json(text):
        from .conversion import JSONToFeatures
        file = CreateScratchName("fs", "", "Folder", env.scratchWorkspace) + ".json"
        with open(file, "w", encoding="utf-8") as written:
            written.write(text)
        return JSONToFeatures(file, CreateScratchName("fs", "", "FeatureClass", env.scratchWorkspace)).getOutput(0)

    @classmethod
    def _from_esri_json(cls, document):
        import json
        made = cls()
        made._path = cls._from_json(json.dumps(document))
        return made

    def save(self, table_path):
        """The features written to a new feature class (a RecordSet: a table)."""
        from .management import CopyFeatures, CopyRows
        return (CopyRows if self._table else CopyFeatures)(self._path, table_path).getOutput(0)

    def _json(self, geojson):
        from .conversion import FeaturesToJSON
        file = CreateScratchName("fs", "", "Folder", env.scratchWorkspace) + (".geojson" if geojson else ".json")
        FeaturesToJSON(self._path, file, geoJSON="GEOJSON" if geojson else "NO_GEOJSON")
        with open(file, encoding="utf-8") as read:
            return read.read()

    @property
    def JSON(self):
        return self._json(False)

    @property
    def GeoJSON(self):
        return self._json(True)

    def __fspath__(self):
        if self._path is None:
            raise ValueError(f"{type(self).__name__}: nothing is loaded.")
        return os.fspath(self._path)

    def __str__(self):
        return "" if self._path is None else str(self._path)


class RecordSet(FeatureSet):
    """FeatureSet's twin for a table: ``RecordSet({table_path}, {where_clause})``."""

    _table = True

    def __init__(self, table_path=None, where_clause=None):
        super().__init__(table_path, where_clause)

    def load(self, table_path=None, where_clause=None):
        return super().load(table_path, where_clause)


# ---- names a user tool on this server has no access to
_NO_ACCESS = "a user tool runs fenced on this server, with no network and no access to its portal, licensing or " \
             "enterprise geodatabases (ADR-223 §5.3)"
_REFUSED_NAMES = {name: _NO_ACCESS for name in (
    "AcceptConnections", "AddDataStoreItem", "ArcSDESQLExecute", "ClearCredentials", "CreateAGSServerConnection",
    "CreateGPSDDraft", "CreateGeocodeSDDraft", "CreateImageSDDraft", "CreateSTACConnectionFile", "DisconnectUser",
    "GetActivePortalURL", "GetPortalDescription", "GetPortalInfo", "GetSigninToken", "GetSTACInfo", "ImportCredentials",
    "ListDataStoreItems", "ListPortalURLs", "ListUsers", "LogUsageMetering", "RemoveDataStoreItem",
    "SignInToPortal", "ValidateDataStoreItem", "EncryptPYT", "DecryptPYT", "ListInstallations", "SetProduct",
    "FromScriptingArcObject",
    "convertArcObjectToPythonObject", "GeoProcessor", "SetupDebugger")}
_NO_APPLICATION = "it acts on ArcGIS Pro's application — its maps, printers, drawing or symbology — which a run on " \
                  "this server does not have"
_REFUSED_NAMES.update({name: _NO_APPLICATION for name in ("ListPrinterNames", "PauseDrawing", "RefreshLayer",
                                                          "SetParameterSymbology")})


from . import analysis, cartography, conversion, da, ddd, edit, ga, graticula, ia, management, na, nax, sa, stats  # noqa: E402 — they import the names above
from .management import AlterAliasName, FieldMap, FieldMappings  # noqa: E402,F401 — arcpy's own names


# ---- round 4: names that need the modules above -------------------------------------------------------------------

SearchNeighborhoodStandard = ga.SearchNeighborhoodStandard
SearchNeighborhoodSmooth = ga.SearchNeighborhoodSmooth
SearchNeighborhoodStandardCircular = ga.SearchNeighborhoodStandardCircular
SearchNeighborhoodSmoothCircular = ga.SearchNeighborhoodSmoothCircular
SearchNeighborhoodStandard3D = ga.SearchNeighborhoodStandard3D


def _parameter_index(value):
    from ._runtime import PARAMETERS
    if isinstance(value, int):
        return value
    for i, parameter in enumerate(PARAMETERS):
        if getattr(parameter, "name", None) == value:
            return i
    raise ValueError(f"No parameter is named '{value}'.")


def CopyParameter(from_param, to_param):
    """The value of one script tool parameter given to another, each by index or name."""
    from ._runtime import PARAMETERS
    PARAMETERS[_parameter_index(to_param)].value = PARAMETERS[_parameter_index(from_param)].value


_OBJECTS = {"array": Array, "extent": Extent, "featureset": lambda *a: FeatureSet(*a), "field": Field,
            "fieldinfo": FieldInfo, "fieldmap": FieldMap, "fieldmappings": FieldMappings, "geometry": Geometry,
            "point": Point, "recordset": lambda *a: RecordSet(*a), "result": Result, "spatialreference": SpatialReference,
            "valuetable": ValueTable}


def CreateObject(name, *options):
    """The geoprocessing object of that name, built as its class is: CreateObject("ValueTable", 2) is ValueTable(2).
    ArcSDESQLExecute and NetCDFFileProperties are refused: the first needs an enterprise geodatabase a fenced tool
    cannot reach, the second is not built."""
    key = str(name).replace(" ", "").lower()
    if key in ("arcsdesqlexecute", "netcdffileproperties"):
        raise NotBuiltError(f"arcpy.CreateObject: '{name}' is not built in grapy.")
    if key not in _OBJECTS:
        raise ValueError(f"arcpy.CreateObject: '{name}' is not an object it creates.")
    return _OBJECTS[key](*options)


class SeverityLevel:
    """A severity level for a ``with`` block or a decorated function. As SetSeverityLevel, only 2 — a tool raises on
    an error, arcpy's default — is built; 0 and 1 would change how every tool ends."""

    def __init__(self, severity_level=2):
        self._level = int(severity_level)
        SetSeverityLevel(self._level)

    def __enter__(self):
        return self

    def __exit__(self, *_):
        return False

    def __call__(self, function):
        return function


def IsBeingEdited(dataset):
    """Whether the dataset's GeoPackage is in a da.Editor session in this run."""
    try:
        file, _ = _gpkg.split(dataset)
    except ValueError:
        file = os.fspath(dataset)
    return da._file_key(file) in da._EDITING


def IsSynchronous(tool_name):
    """True: "all non-server tools are synchronous", and a user tool here runs only grapy's own tools — it cannot import
    a server's toolbox (ImportToolbox is refused)."""
    return True


class Value:
    """What GetParameterInfo and GetParameter answer for some parameters: ``value`` and ``isEmpty``."""

    def __init__(self, value=None):
        self.value = value

    @property
    def isEmpty(self):
        return self.value in (None, "")

    def __str__(self):
        return "" if self.value is None else str(self.value)


# ---- the legacy cursors (Cursor, Row, SearchCursor, UpdateCursor, InsertCursor) -----------------------------------
#
# Superseded by arcpy.da's and kept "for use in legacy scripts". They are built on the da cursors. INFERRED: a row's
# shape is read and written under the GeoPackage's geometry column name and under "Shape" (any case), the name a
# geodatabase gives it and legacy scripts use.


class Row:
    """A row of a legacy cursor: getValue, setValue, isNull, setNull, and each field as an attribute."""

    def __init__(self, names, values, geometry):
        object.__setattr__(self, "_names", list(names))
        object.__setattr__(self, "_values", dict(zip([n.lower() for n in names], values)))
        object.__setattr__(self, "_geometry", geometry)

    def _key(self, name):
        key = str(name).lower()
        if key == "shape" and self._geometry is not None:
            return self._geometry.lower()
        if key not in self._values:
            raise RuntimeError(f"Row: no field '{name}'.")
        return key

    def getValue(self, field_name):
        return self._values[self._key(field_name)]

    def setValue(self, field_name, value):
        self._values[self._key(field_name)] = value

    def isNull(self, field_name):
        return self.getValue(field_name) is None

    def setNull(self, field_name):
        self.setValue(field_name, None)

    def __getattr__(self, name):
        if name.startswith("_"):
            raise AttributeError(name)
        try:
            return self.getValue(name)
        except RuntimeError as error:
            raise AttributeError(str(error)) from None

    def __setattr__(self, name, value):
        self.setValue(name, value)

    def _ordered(self):
        return [self._values[n.lower()] for n in self._names]


def _legacy_fields(dataset, fields):
    """The OID column, the fields asked (all by default) and the geometry column."""
    connection, table = _gpkg.table_of(dataset)
    try:
        columns, _, _, geometry = _gpkg.describe(connection, table)
        oid = _gpkg.oid_column(connection, table)
    finally:
        connection.close()
    names = [c.name for c in columns]
    if fields not in (None, "", "*"):
        wanted = {f.strip().lower() for f in (fields.split(";") if isinstance(fields, str) else fields)}
        names = [n for n in names if n.lower() in wanted]
    return oid, names, geometry


def _order_by(sort_fields):
    """``"A A;B D"`` as an SQL ORDER BY: A ascending, then B descending."""
    if sort_fields in (None, ""):
        return None
    parts = []
    for item in str(sort_fields).split(";"):
        bits = item.split()
        if bits:
            parts.append(f'"{bits[0]}" {"DESC" if len(bits) > 1 and bits[1].upper().startswith("D") else "ASC"}')
    return "ORDER BY " + ", ".join(parts)


class Cursor:
    """A legacy cursor: ``next()`` answers the next Row, or None after the last; it can be iterated."""

    def __init__(self):
        self._rows = iter(())

    def next(self):
        return next(self._rows, None)

    def __iter__(self):
        return self

    def __next__(self):
        row = self.next()
        if row is None:
            raise StopIteration
        return row

    def reset(self):
        raise NotBuiltError("arcpy.Cursor.reset is not built in grapy; open a new cursor.")

    def newRow(self):
        raise RuntimeError("newRow is an insert cursor's.")

    def insertRow(self, row):
        raise RuntimeError("insertRow is an insert cursor's.")

    def updateRow(self, row):
        raise RuntimeError("updateRow is an update cursor's.")

    def deleteRow(self, row):
        raise RuntimeError("deleteRow is an update cursor's.")

    def _close(self):
        pass

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self._close()
        return False

    def __del__(self):
        try:
            self._close()
        except Exception:
            pass


class _SearchCursor(Cursor):
    def __init__(self, dataset, where_clause=None, spatial_reference=None, fields=None, sort_fields=None):
        oid, names, geometry = _legacy_fields(dataset, fields)
        self._cursor = da.SearchCursor(dataset, ["OID@", *names, "SHAPE@"], where_clause or None, spatial_reference,
                                       sql_clause=(None, _order_by(sort_fields)))
        labels = [oid, *names, geometry]
        self._rows = (Row(labels, list(values), geometry) for values in self._cursor)

    def _close(self):
        cursor = getattr(self, "_cursor", None)
        if cursor is not None:
            cursor._close()


class _UpdateCursor(Cursor):
    def __init__(self, dataset, where_clause=None, spatial_reference=None, fields=None, sort_fields=None):
        if spatial_reference not in (None, ""):
            raise NotBuiltError("arcpy.UpdateCursor: spatial_reference is not built in grapy; edit in the data's own "
                                "reference.")
        if sort_fields not in (None, ""):
            raise NotBuiltError("arcpy.UpdateCursor: sort_fields is not built in grapy.")
        oid, names, geometry = _legacy_fields(dataset, fields)
        self._labels = [oid, *names, geometry]
        self._cursor = da.UpdateCursor(dataset, ["OID@", *names, "SHAPE@"], where_clause or None)
        self._geometry = geometry
        self._rows = (Row(self._labels, list(values), geometry) for values in self._cursor)

    def updateRow(self, row):
        self._cursor.updateRow(row._ordered())

    def deleteRow(self, row):
        self._cursor.deleteRow()

    def _close(self):
        cursor = getattr(self, "_cursor", None)
        if cursor is not None:
            cursor._close()


class _InsertCursor(Cursor):
    def __init__(self, dataset, spatial_reference=None):
        if spatial_reference not in (None, ""):
            raise NotBuiltError("arcpy.InsertCursor: spatial_reference is not built in grapy; insert in the data's own "
                                "reference.")
        _, names, geometry = _legacy_fields(dataset, None)
        self._labels = [*names, geometry]
        self._cursor = da.InsertCursor(dataset, [*names, "SHAPE@"])
        self._geometry = geometry

    def next(self):
        return None

    def newRow(self):
        return Row(self._labels, [None] * len(self._labels), self._geometry)

    def insertRow(self, row):
        return self._cursor.insertRow(row._ordered())

    def _close(self):
        cursor = getattr(self, "_cursor", None)
        if cursor is not None:
            cursor._close()


def SearchCursor(dataset, where_clause=None, spatial_reference=None, fields=None, sort_fields=None):
    """Legacy: Row objects of a table, filtered, projected, narrowed to ``fields`` ("a;b") and sorted ("a A;b D")."""
    return _SearchCursor(dataset, where_clause, spatial_reference, fields, sort_fields)


def UpdateCursor(dataset, where_clause=None, spatial_reference=None, fields=None, sort_fields=None):
    """Legacy: Row objects to change with updateRow or remove with deleteRow. spatial_reference and sort_fields are
    refused."""
    return _UpdateCursor(dataset, where_clause, spatial_reference, fields, sort_fields)


def InsertCursor(dataset, spatial_reference=None):
    """Legacy: newRow() makes an empty Row, insertRow(row) writes it. spatial_reference is refused."""
    return _InsertCursor(dataset, spatial_reference)


# ---- the remaining names, each refused with its reason ---------------------------------------------------------------

_NO_ARCGIS = "it answers about an ArcGIS installation, license or product, and grapy is not ArcGIS"
_NO_TOOLBOXES = "it reads Esri's toolbox catalogue — tool parameters, usage text, toolbox lists — which grapy does not reproduce"
_NO_NETWORK_STORAGE = "it reaches cloud storage, and a fenced user tool has no network (ADR-223 §5.4)"
_ESRI_CATALOGUE = "it answers from Esri's own catalogue of coordinate systems, transformations or raster products, " \
                  "which grapy does not hold"
_REFUSED_NAMES.update({name: _NO_ARCGIS for name in ("GetInstallInfo", "ProductInfo", "ListInstallations", "SetProduct",
                                                     "GetPackageInfo")})
_REFUSED_NAMES.update({name: _NO_TOOLBOXES for name in ("AddToolbox", "ImportToolbox", "RemoveToolbox", "ListToolboxes",
                                                        "ListTools", "Usage", "GetParameterInfo", "GetParameterValue",
                                                        "Command")})
_REFUSED_NAMES.update({name: _NO_NETWORK_STORAGE for name in ("AIO", "AIOFileOpen", "CloudPathType")})
_REFUSED_NAMES.update({name: _ESRI_CATALOGUE for name in ("ListSpatialReferences", "ListTransformations",
                                                          "ListRasterProducts")})
_REFUSED_NAMES.update({name: _NO_APPLICATION for name in (
    "Chart", "Schema", "GetLogHistory", "SetLogHistory", "LogHistory",
    "GetLogMetadata", "SetLogMetadata", "LogMetadata")})
_REFUSED_NAMES["SetMessageLevels"] = "its message categories are the internal diagnostics of Esri's own tools, which "                                      "grapy's tools do not write"
_REFUSED_NAMES.update({
    "Annotation": "annotation features are a geodatabase data model this server does not hold (ADR-224 group 2)",
    "Dimension": "dimension features are a geodatabase data model this server does not hold (ADR-224 group 2)",
    "Multipatch": "a multipatch is a 3D geometry, and grapy's geometries hold no z values",
    "AttachLocator": "locators answer from Esri's geocoding data and services (ADR-224 group 3)",
    "ListLasDatasetFiles": "LAS datasets are a data model this server does not hold (ADR-224 group 2)",
    "CrossValidationResult": "it reads a geostatistical layer, a model object grapy does not have",
    "GeostatisticalDatasets": "it reads a geostatistical layer, a model object grapy does not have",
    "GetIDMessage": "its text is Esri's message catalogue, which is not published as data",
    "GetImageEXIFProperties": "its answer's keys and conversions are not documented closely enough to reproduce",
    "GetRasterKeyMetadata": "Esri's raster key metadata names are not documented as a mapping to GDAL's metadata",
    "SetRasterKeyMetadata": "Esri's raster key metadata names are not documented as a mapping to GDAL's metadata",
    "GenerateOptimalCoordinateSystem": "how it chooses a projection and its parameters is not documented",
    "Render": "it applies display symbology — band combinations, stretches and colour maps — for a notebook's view, "
              "and the stretches' rules and the rendered raster it saves are not specified closely enough to reproduce",
    "NetCDFFileProperties": "GDAL's netCDF driver, the one grapy's rasters read through, reports attributes and extra "
                            "dimensions as metadata text, not the typed variables and dimensions the class answers "
                            "(getFieldType, getDimensionsByVariable, getSpatialReference); answering through it would "
                            "be approximate",
    **{name: "its documentation could not be found (looked under the arcpy functions and classes alphabetical lists, "
             "its pro.arcgis.com address, and the arcpy.mp, arcpy.ia and arcpy.sharing sections)"
       for name in ("GetUTMFromLocation", "GetPCSFromDataSource", "Guide", "Mensuration", "TimeBinningProperties",
                    "ViewContext")},
    "RandomNumberGenerator": "its generators' streams are Esri's, and a seeded stream must match to be the same answer",
    "CreateRandomValueGenerator": "its generators' streams are Esri's, and a seeded stream must match to be the same "
                                  "answer",
})


# ADR-224 §5.1: modules that answer from an Esri data model this server does not hold (group 2), and modules that
# answer from Esri's own data or cloud services (group 3). Both are refused with the reason, not merely "not built".
_DATA_MODEL = "needs a data model this server does not hold yet (ADR-224 group 2)"
_ESRI_SERVICE = "answers from Esri's own data or cloud services, which grapy cannot reproduce (ADR-224 group 3)"
_REFUSED_MODULES = {
    "un": f"the utility network {_DATA_MODEL}", "parcel": f"the parcel fabric {_DATA_MODEL}",
    "nd": f"network diagrams, the schematic views of a utility or trace network, {_DATA_MODEL}",
    "topographic": f"the topographic production model {_DATA_MODEL}", "locref": f"linear referencing networks {_DATA_MODEL}",
    "ba": f"Business Analyst {_ESRI_SERVICE}: its demographics are Esri's data",
    "agolservices": f"ArcGIS Online's analysis services {_ESRI_SERVICE}",
    "geoai": f"GeoAI's pretrained models {_ESRI_SERVICE}",
    "geocoding": f"geocoding against Esri's World Geocoder {_ESRI_SERVICE}",
}


class _Refused:
    """A module grapy refuses as a whole: any tool asked of it raises NotBuiltError with the module's reason."""

    def __init__(self, module, reason):
        self._module, self._reason = module, reason

    def __getattr__(self, name):
        if name.startswith("_"):
            raise AttributeError(name)
        raise NotBuiltError(f"arcpy.{self._module}.{name}: {self._reason}.")


def __getattr__(name):
    """A name arcpy documents and grapy has not built: said by name, not as an AttributeError."""
    if name.startswith("_"):
        raise AttributeError(name)
    if name in _REFUSED_MODULES:
        return _Refused(name, _REFUSED_MODULES[name])
    if name in _REFUSED_NAMES:
        raise NotBuiltError(f"arcpy.{name} is not built in grapy — {_REFUSED_NAMES[name]}.")
    raise NotBuiltError(f"arcpy.{name} is not built in grapy yet.")
