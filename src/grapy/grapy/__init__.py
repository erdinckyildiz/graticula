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
            domains = _field_domains(connection, table)
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
        for field in self.fields:
            field.domain = domains.get(field.name.lower(), "")
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
    object), a folder, or a GeoPackage of several tables (a workspace)."""
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
    return _Described(value)


def ListFields(dataset, wild_card=None, field_type=None):
    import fnmatch
    fields = _Described(dataset).fields
    return [f for f in fields
            if (wild_card is None or fnmatch.fnmatch(f.name.lower(), wild_card.lower()))
            and (field_type in (None, "All") or f.type == field_type)]


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
    "ListDataStoreItems", "ListPortalURLs", "ListUsers", "ListVersions", "LogUsageMetering", "RemoveDataStoreItem",
    "SignInToPortal", "ValidateDataStoreItem", "EncryptPYT", "DecryptPYT", "ListInstallations", "SetProduct",
    "FromScriptingArcObject",
    "convertArcObjectToPythonObject", "GeoProcessor", "SetupDebugger")}
_NO_APPLICATION = "it acts on ArcGIS Pro's application — its maps, printers, drawing or symbology — which a run on " \
                  "this server does not have"
_REFUSED_NAMES.update({name: _NO_APPLICATION for name in ("ListPrinterNames", "PauseDrawing", "RefreshLayer",
                                                          "SetParameterSymbology")})


from . import analysis, cartography, conversion, da, ddd, edit, ga, graticula, ia, management, sa, stats  # noqa: E402 — they import the names above
from .management import AlterAliasName, FieldMap, FieldMappings  # noqa: E402,F401 — arcpy's own names


# ADR-224 §5.1: modules that answer from an Esri data model this server does not hold (group 2), and modules that
# answer from Esri's own data or cloud services (group 3). Both are refused with the reason, not merely "not built".
_DATA_MODEL = "needs a data model this server does not hold yet (ADR-224 group 2)"
_ESRI_SERVICE = "answers from Esri's own data or cloud services, which grapy cannot reproduce (ADR-224 group 3)"
_REFUSED_MODULES = {
    "un": f"the utility network {_DATA_MODEL}", "parcel": f"the parcel fabric {_DATA_MODEL}",
    "na": f"network datasets {_DATA_MODEL}", "nax": f"network datasets {_DATA_MODEL}", "nd": f"network datasets {_DATA_MODEL}",
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
