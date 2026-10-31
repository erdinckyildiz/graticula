"""Terrain datasets held in a GeoPackage feature dataset, thinned into pyramid levels — ADR-234.

A terrain dataset in ArcGIS is a multiresolution surface kept in a geodatabase feature dataset: the feature classes
that take part in it (referenced, or embedded inside it), the pyramid levels defined by a z-tolerance or a window size
and a reference scale, and the thinned points of each level that Build Terrain stores. Esri's pyramid storage is not
published and is not reproduced. grapy's terrain is its own tables in the GeoPackage (INFERRED, ADR-234 §5.2),
registered in ``gpkg_extensions`` as the ``grapy_terrain`` extension:

- ``grapy_terrain``: name, feature dataset, reference, the CreateTerrain settings, whether it is built, its extent.
- ``grapy_terrain_level``: each pyramid level's resolution (a z-tolerance or a window size) and reference scale.
- ``grapy_terrain_source``: each feature class that takes part: its name in the feature dataset (or its embedded name),
  height field, surface feature type, group, resolution bounds, overview, whether embedded, whether anchor points.
- ``grapy_terrain_point``: the points of the embedded feature classes.
- ``grapy_terrain_pyramid``: the mass points of each level as Build Terrain thinned them; level 0 is full resolution.
- ``grapy_terrain_feature``: the breaklines and polygons as Build Terrain read them.

A level's surface is a TIN (ADR-229's ``_tin``) triangulated from that level's points and the lines and polygons
enforced at it, so every TIN tool's measurement reads a terrain at any level.

**Window size pyramids** are built as Add Terrain Pyramid Level documents them: the area is cut into square windows
of the level's size, and one or two points are kept in each by the window size method — the lowest (ZMIN), the highest
(ZMAX), the one closest to the window's mean (ZMEAN), or the lowest and highest (ZMINMAX). Each level is thinned from
the one before it, as the page says a level is populated from the preceding level.

**Z-tolerance pyramids** keep "only the data necessary to achieve an approximate vertical tolerance": the page names
the bound — the maximum deviation from the full-resolution elevation — but not how the points are chosen. grapy builds
it as an **equivalent** (ADR-224 §5.6), greedy insertion (Garland and Heckbert 1995; Heller 1990): starting from the
convex hull of the points and the anchor points, the point farthest from the surface in each triangle is inserted
until every full-resolution point lies within the tolerance. The bound the page states is therefore met exactly; which
points meet it may differ from Pro's.
"""
from __future__ import annotations

import datetime
import math
import os

import numpy as np
import shapely

from . import _gpkg, _tin
from ._result import Result
from ._runtime import AddWarning, ExecuteError, NotBuiltError, env
from ._runtime import _is_given, _is_set  # noqa: E402 — a Raster is never compared with ==

EXTENSION = "grapy_terrain"
DEFINITION = "Graticula ADR-234: grapy's terrain datasets"
TABLES = ("grapy_terrain", "grapy_terrain_level", "grapy_terrain_source", "grapy_terrain_point", "grapy_terrain_pyramid",
          "grapy_terrain_feature")
WINDOW_METHODS = ("ZMIN", "ZMAX", "ZMEAN", "ZMINMAX")
Z_TOLERANCE_EQUIVALENT = ("greedy insertion to a vertical tolerance", "Garland and Heckbert 1995; Heller 1990")


def _refuse(tool, parameter, value, why=""):
    raise NotBuiltError(f"arcpy.ddd.{tool}: {parameter} '{value}' is not built in grapy{': ' + why if why else ''}.")


def _given(value):
    return _is_given(value)


def _text(value):
    return os.fspath(value) if isinstance(value, os.PathLike) else str(value)


# ---- where a terrain is


def locate(path):
    """(file, feature dataset, name) of a terrain path ``<file>.gpkg/<feature dataset>/<terrain>``."""
    from ._result import Result as _R
    if isinstance(path, _R):
        path = str(path)
    file, table = _gpkg.split(_text(path))
    if not table or "/" not in table:
        raise ValueError(f"'{path}' is no terrain: write <file>.gpkg/<feature dataset>/<terrain>.")
    dataset, name = table.rsplit("/", 1)
    return file, dataset, name


def ensure(connection):
    from ._topology import register
    connection.execute("""create table if not exists grapy_terrain (name text primary key, dataset text not null,
        srid integer not null, point_spacing double not null, max_overview_size integer, pyramid_type text not null,
        window_method text not null, secondary_method text not null, secondary_threshold double,
        triangulation text not null, built integer not null default 0, xmin double, ymin double, xmax double,
        ymax double, built_at text)""")
    connection.execute("""create table if not exists grapy_terrain_level (terrain text not null, resolution double not null,
        reference_scale integer not null)""")
    connection.execute("""create table if not exists grapy_terrain_source (terrain text not null, seq integer not null,
        name text not null, feature_class text, height_field text, sf_type text not null, grp integer,
        min_resolution double, max_resolution double, overview integer not null default 0,
        embedded integer not null default 0, anchor integer not null default 0)""")
    connection.execute("""create table if not exists grapy_terrain_point (terrain text not null, source text not null,
        x double not null, y double not null, z double not null)""")
    connection.execute("""create table if not exists grapy_terrain_pyramid (terrain text not null, resolution double not null,
        x double not null, y double not null, z double not null, source integer not null)""")
    connection.execute("""create table if not exists grapy_terrain_feature (terrain text not null, source integer not null,
        sf_type text not null, tag integer not null, height double, pending integer not null, shape blob not null)""")
    register(connection, TABLES, EXTENSION, DEFINITION)


def _held(connection, name):
    if not connection.execute("select 1 from sqlite_master where name = 'grapy_terrain'").fetchone():
        return None
    return connection.execute("select dataset from grapy_terrain where name = ?", (name,)).fetchone()


def exists(path):
    try:
        file, dataset, name = locate(path)
    except (ValueError, TypeError):
        return False
    if not os.path.isfile(file):
        return False
    connection = _gpkg.connect(file)
    try:
        row = _held(connection, name)
        return row is not None and row[0] == dataset
    finally:
        connection.close()


class Terrain:
    """A terrain's row of grapy_terrain with its levels and sources."""

    def __init__(self, connection, path):
        self.path = _text(path)
        self.file, self.dataset, self.name = locate(path)
        row = connection.execute("select srid, point_spacing, max_overview_size, pyramid_type, window_method, "
                                 "secondary_method, secondary_threshold, triangulation, built, xmin, ymin, xmax, ymax "
                                 "from grapy_terrain where name = ?", (self.name,)).fetchone()
        (self.srid, self.point_spacing, self.max_overview_size, self.pyramid_type, self.window_method,
         self.secondary_method, self.secondary_threshold, self.triangulation, built, *box) = row
        self.built = bool(built)
        self.extent = tuple(box) if box[0] is not None else None
        self.levels = [(float(r), int(s)) for r, s in connection.execute(
            "select resolution, reference_scale from grapy_terrain_level where terrain = ? order by resolution",
            (self.name,))]
        self.sources = [dict(zip(("seq", "name", "feature_class", "height_field", "sf_type", "group", "min_resolution",
                                  "max_resolution", "overview", "embedded", "anchor"), r)) for r in connection.execute(
            "select seq, name, feature_class, height_field, sf_type, grp, min_resolution, max_resolution, overview, "
            "embedded, anchor from grapy_terrain_source where terrain = ? order by seq", (self.name,))]

    def source(self, name, tool):
        key = str(name).replace("\\", "/").rsplit("/", 1)[-1].lower()
        for s in self.sources:
            if s["name"].lower() == key:
                return s
        raise ExecuteError(f"ERROR 000732: {name} does not take part in the terrain {self.name} ({tool}).")


def open_terrain(path, tool):
    """(connection, Terrain), refused by name when the path is no terrain grapy holds."""
    if not exists(path):
        raise ExecuteError(f"ERROR 000732: Input Terrain: Dataset {path} does not exist or is not supported ({tool}).")
    file, _, _ = locate(path)
    connection = _gpkg.connect(file)
    return connection, Terrain(connection, path)


def _invalidate(connection, name):
    connection.execute("update grapy_terrain set built = 0 where name = ?", (name,))


def _dataset_srid(file, dataset):
    if not os.path.isfile(file):
        return None
    connection = _gpkg.connect(file)
    try:
        if not connection.execute("select 1 from sqlite_master where name = 'grapy_feature_datasets'").fetchone():
            return None
        row = connection.execute("select srs_id from grapy_feature_datasets where name = ?", (dataset,)).fetchone()
        return None if row is None else int(row[0])
    finally:
        connection.close()


# ---- the tools that make and change a terrain


def CreateTerrain(in_feature_dataset, out_terrain_name, average_point_spacing, max_overview_size=None,
                  config_keyword=None, pyramid_type="WINDOWSIZE", windowsize_method="ZMIN",
                  secondary_thinning_method="NONE", secondary_thinning_threshold=1, triangulation_method="DELAUNAY"):
    """A new, empty terrain dataset in a feature dataset, with the settings CreateTerrain documents: the average point
    spacing, the pyramid type (``WINDOWSIZE``, the default, or ``ZTOLERANCE``), and the window size method (``ZMIN``,
    the default, ``ZMAX``, ``ZMEAN``, ``ZMINMAX``). Add Terrain Pyramid Level, Add Feature Class To Terrain and Build
    Terrain finish it, in that order, as the page says.

    Refused: ``secondary_thinning_method`` other than ``NONE`` — the page names MILD, MODERATE and STRONG and what each
    suits, not which points they remove. ``CONSTRAINED_DELAUNAY`` triangulates every pyramid level as CreateTin's
    option does (ADR-229 condition 6). ``config_keyword`` is an enterprise geodatabase's and is ignored.

    INFERRED (ADR-234 §5.9): ``max_overview_size`` is kept and reported, and grapy builds no overview of its own — the
    overview is a display level, not one a tool reads by resolution."""
    tool = "CreateTerrain"
    path = _text(in_feature_dataset).replace("\\", "/").rstrip("/")
    try:
        file, dataset = _gpkg.split(path)
    except ValueError:
        raise ExecuteError(f"ERROR 000732: Input Feature Dataset: {in_feature_dataset} is not a feature dataset.")
    srid = _dataset_srid(file, dataset) if dataset else None
    if not dataset or "/" in dataset or srid is None:
        raise ExecuteError(f"ERROR 000732: Input Feature Dataset: {in_feature_dataset} is not a feature dataset "
                           "(CreateFeatureDataset makes one).")
    kind = str(pyramid_type or "WINDOWSIZE").upper().replace("_", "")
    if kind not in ("WINDOWSIZE", "ZTOLERANCE"):
        _refuse(tool, "pyramid_type", pyramid_type)
    method = str(windowsize_method or "ZMIN").upper()
    if method not in WINDOW_METHODS:
        _refuse(tool, "windowsize_method", windowsize_method)
    secondary = str(secondary_thinning_method or "NONE").upper()
    if secondary != "NONE":
        _refuse(tool, "secondary_thinning_method", secondary_thinning_method,
                "the page names MILD, MODERATE and STRONG and what each suits, but not which points each removes "
                "(ADR-234 §5.4)")
    triangulation = str(triangulation_method or "DELAUNAY").upper()
    if triangulation not in ("DELAUNAY", "CONSTRAINED_DELAUNAY"):
        _refuse(tool, "triangulation_method", triangulation_method)
    spacing = float(average_point_spacing)
    if spacing <= 0:
        raise ExecuteError("ERROR 000854: the average point spacing must be greater than 0.")
    name = str(out_terrain_name)
    connection = _gpkg.connect(file)
    try:
        ensure(connection)
        if _held(connection, name) is not None or connection.execute(
                "select 1 from gpkg_contents where table_name in (?, ?)", (name, f"{dataset}/{name}")).fetchone():
            raise ExecuteError(f"ERROR 000258: Output {name} already exists.")
        connection.execute("insert into grapy_terrain (name, dataset, srid, point_spacing, max_overview_size, pyramid_type, "
                           "window_method, secondary_method, secondary_threshold, triangulation) values "
                           "(?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
                           (name, dataset, srid, spacing, int(max_overview_size) if _given(max_overview_size) else None,
                            kind, method, secondary, float(secondary_thinning_threshold)
                            if _given(secondary_thinning_threshold) else 1.0, triangulation))
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_feature_dataset).rstrip("/\\") + "/" + name)


def _level_pairs(definition):
    """[(resolution, reference scale)] of a pyramid level definition: "20 24000" strings, lists or a ;-joined string."""
    if isinstance(definition, str):
        definition = [d for d in definition.split(";") if d.strip()]
    pairs = []
    for entry in definition or []:
        parts = list(entry) if isinstance(entry, (list, tuple)) else str(entry).replace(",", " ").split()
        if len(parts) != 2:
            raise ExecuteError(f"ERROR 000800: '{entry}' is not a pyramid level: write its resolution and reference scale.")
        resolution, scale = float(parts[0]), float(parts[1])
        if resolution <= 0:
            raise ExecuteError(f"ERROR 000800: a pyramid level's resolution must be greater than 0, not {parts[0]}.")
        if scale != int(scale) or scale <= 0:
            raise ExecuteError(f"ERROR 000800: a reference scale is a whole number, not {parts[1]}.")
        pairs.append((resolution, int(scale)))
    return pairs


def AddTerrainPyramidLevel(in_terrain, pyramid_type=None, pyramid_level_definition=None):
    """Pyramid levels added to a terrain, each a resolution — the z-tolerance or window size of the terrain's pyramid
    type — and the reference scale it is displayed to, as documented ("20 24000"). Adding a level invalidates the
    terrain; Build Terrain populates it. ``pyramid_type`` is unused, as the page says (kept for ArcGIS 9.2 scripts).

    A window size level is at least twice the terrain's average point spacing, ArcGIS Pro 3.7's ERROR 050073 (measured
    2026-10-31 at spacings 0.5 to 10). INFERRED: a level of a resolution or reference scale the terrain already has is
    refused."""
    connection, terrain = open_terrain(in_terrain, "AddTerrainPyramidLevel")
    try:
        pairs = _level_pairs(pyramid_level_definition)
        if not pairs:
            raise ExecuteError("ERROR 000735: Pyramid Levels Definition: Value is required.")
        have = list(terrain.levels)
        for resolution, scale in pairs:
            smallest = 2 * float(terrain.point_spacing)
            if terrain.pyramid_type == "WINDOWSIZE" and resolution < smallest - 1e-12:
                raise ExecuteError("ERROR 050073: Minimum window size exceeded. The smallest that can be used with data "
                                   f"of this point spacing is {smallest:g}.")
            if any(math.isclose(resolution, r) for r, _ in have) or any(scale == s for _, s in have):
                raise ExecuteError(f"ERROR: the terrain already has a pyramid level of resolution {resolution:g} or "
                                   f"reference scale {scale}.")
            have.append((resolution, scale))
            connection.execute("insert into grapy_terrain_level values (?, ?, ?)", (terrain.name, resolution, scale))
        _invalidate(connection, terrain.name)
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_terrain))


def RemoveTerrainPyramidLevel(in_terrain, pyramid_level_resolution):
    """A pyramid level removed by its resolution; level 0, full resolution, cannot be, as documented. INFERRED: the
    other levels' points are kept, so a built terrain stays built."""
    connection, terrain = open_terrain(in_terrain, "RemoveTerrainPyramidLevel")
    try:
        resolution = float(pyramid_level_resolution)
        if resolution == 0:
            raise ExecuteError("ERROR: level 0 is the full resolution pyramid and cannot be removed.")
        match = [r for r, _ in terrain.levels if math.isclose(r, resolution)]
        if not match:
            raise ExecuteError(f"ERROR: the terrain has no pyramid level of resolution {pyramid_level_resolution}.")
        connection.execute("delete from grapy_terrain_level where terrain = ? and resolution = ?", (terrain.name, match[0]))
        connection.execute("delete from grapy_terrain_pyramid where terrain = ? and resolution = ?",
                           (terrain.name, match[0]))
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_terrain))


def ChangeTerrainReferenceScale(in_terrain, old_refscale, new_refscale):
    """The reference scale of the pyramid level that has ``old_refscale`` changed to ``new_refscale``. The points are
    unchanged, so the terrain is not invalidated (INFERRED: the page does not say it is)."""
    connection, terrain = open_terrain(in_terrain, "ChangeTerrainReferenceScale")
    try:
        old, new = int(float(old_refscale)), int(float(new_refscale))
        if not any(s == old for _, s in terrain.levels):
            raise ExecuteError(f"ERROR: no pyramid level of the terrain has the reference scale {old_refscale}.")
        if new != old and any(s == new for _, s in terrain.levels):
            raise ExecuteError(f"ERROR: a pyramid level of the terrain already has the reference scale {new_refscale}.")
        connection.execute("update grapy_terrain_level set reference_scale = ? where terrain = ? and reference_scale = ?",
                           (new, terrain.name, old))
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_terrain))


_ADD_COLUMNS = 11   # input features, height field, type, group, min res, max res, overview, embed, embed name,
#                     embed fields, anchor — Add Feature Class To Terrain's value table


def _flag(value, default=False):
    if not _is_given(value):
        return default
    if isinstance(value, bool):
        return value
    return str(value).strip().upper() in ("TRUE", "1", "YES", "OVERVIEW", "EMBED", "ANCHOR", "ANCHORED")


def _bound(value):
    return float(value) if _given(value) else None


def AddFeatureClassToTerrain(in_terrain, in_features):
    """Feature classes added to a terrain, one per row of the value table: input features, height field, surface type
    (masspoints, hard or soft line, clip, erase, replace or value fill), group, minimum and maximum resolution,
    overview, embed, embed name, embed fields, anchor — the columns the page lists. The features must lie in the
    terrain's feature dataset, and the terrain must have a pyramid level, as documented. The terrain must be built
    again.

    An embedded point class is copied into the terrain, as the page on editing terrains says ("the source feature class
    can be deleted after the terrain is built"); an anchor point class's points are kept at every pyramid level.

    Refused: embed fields — lidar attributes are stored in a BLOB whose layout is not documented.

    INFERRED (ADR-234 §5.9 (c)): a blank resolution bound is open, so a line or polygon with no bounds is enforced at
    every level; an embedded class with no embed name is ``<name>_embed``; embed and anchor take only point and
    multipoint classes."""
    tool = "AddFeatureClassToTerrain"
    connection, terrain = open_terrain(in_terrain, tool)
    try:
        if not terrain.levels:
            raise ExecuteError("ERROR: the terrain must already have one or more pyramid levels defined "
                               "(Add Terrain Pyramid Level).")
        rows = _tin.rows_of(in_features, _ADD_COLUMNS)
        if not rows:
            raise ExecuteError("ERROR 000735: Input Feature Class: Value is required.")
        seq = max([s["seq"] for s in terrain.sources], default=-1)
        for row in rows:
            path, height, sf, group, low, high, overview, embed, embed_name, embed_fields, anchor = row[:_ADD_COLUMNS]
            path = _text(path)
            file, table = _gpkg.split(path)
            if os.path.normcase(os.path.abspath(file)) != os.path.normcase(os.path.abspath(terrain.file)) or \
                    not table or table.rsplit("/", 1)[0] != terrain.dataset or "/" not in table:
                raise ExecuteError(f"ERROR: {path} is not in the terrain's feature dataset {terrain.dataset}; input "
                                   "features must reside in the same feature dataset as the terrain, as documented.")
            kind = _tin.sf_type(sf, tool)
            if kind in ("anchorpoints", "extentclip"):
                _refuse(tool, "SF_type", sf)
            from .analysis import _schema
            _, shape_type, _, _ = _schema(path)
            role = _tin._TYPES[kind][0]
            if role in ("clip", "erase", "replace", "valuefill") and shape_type != "Polygon":
                raise ExecuteError(f"ERROR: {path} is {shape_type}; the {sf} type takes polygons.")
            if role == "line" and shape_type not in ("Polyline", "Polygon"):
                raise ExecuteError(f"ERROR: {path} is {shape_type}; the {sf} type takes lines and polygons.")
            name = table.rsplit("/", 1)[1]
            embedded, anchored = _flag(embed), _flag(anchor)
            if _given(embed_fields):
                _refuse(tool, "embed_fields", embed_fields, "lidar attributes of embedded points are kept in a BLOB "
                        "whose layout is not documented")
            if (embedded or anchored) and shape_type not in ("Point", "Multipoint"):
                raise ExecuteError(f"ERROR: only point and multipoint classes can be embedded or anchored; {name} is "
                                   f"{shape_type}.")
            if anchored and kind != "masspoints":
                raise ExecuteError("ERROR: anchor points are mass points.")
            own = (str(embed_name) if _given(embed_name) else f"{name}_embed") if embedded else name
            if any(s["name"].lower() == own.lower() for s in terrain.sources):
                raise ExecuteError(f"ERROR: {own} already takes part in the terrain.")
            seq += 1
            connection.execute("insert into grapy_terrain_source values (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
                               (terrain.name, seq, own, None if embedded else name, str(height or ""), kind,
                                int(group) if _given(group) else None, _bound(low), _bound(high),
                                1 if _flag(overview) else 0, 1 if embedded else 0, 1 if anchored else 0))
            terrain.sources.append({"seq": seq, "name": own})
            if embedded:
                points = _points_of_class(path, height, tool)
                connection.executemany("insert into grapy_terrain_point values (?, ?, ?, ?, ?)",
                                       [(terrain.name, own, float(x), float(y), float(z)) for x, y, z in points])
        _invalidate(connection, terrain.name)
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_terrain))


def _points_of_class(path, height, tool):
    """(n, 3) of every vertex of a feature class, z from its height field."""
    features, _, _ = _tin.read_features(path, height, "masspoints", None, tool)
    if any(pending for _, pending, _, _ in features):
        raise ExecuteError(f"ERROR: mass points need heights; {path} was given the height <None>.")
    xyz = [shapely.get_coordinates(shape, include_z=True) for shape, _, _, _ in features]
    return np.concatenate(xyz) if xyz else np.zeros((0, 3))


def RemoveFeatureClassFromTerrain(in_terrain, feature_class):
    """A feature class taken out of a terrain; an embedded one's points are deleted with it, as documented ("This tool
    will only delete embedded features referenced by a terrain dataset"), and a referenced one is left in its feature
    dataset. Removing mass points invalidates the terrain; a line or polygon class leaves it built (INFERRED, the page
    saying a rebuild is needed when the removed features were mass points)."""
    connection, terrain = open_terrain(in_terrain, "RemoveFeatureClassFromTerrain")
    try:
        source = terrain.source(feature_class, "RemoveFeatureClassFromTerrain")
        connection.execute("delete from grapy_terrain_source where terrain = ? and seq = ?", (terrain.name, source["seq"]))
        connection.execute("delete from grapy_terrain_point where terrain = ? and source = ?", (terrain.name, source["name"]))
        connection.execute("delete from grapy_terrain_feature where terrain = ? and source = ?",
                           (terrain.name, source["seq"]))
        if source["sf_type"] == "masspoints":
            _invalidate(connection, terrain.name)
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_terrain))


def ChangeTerrainResolutionBounds(in_terrain, feature_class, lower_pyramid_resolution=None, upper_pyramid_resolution=None,
                                  overview="OVERVIEW"):
    """The range of pyramid levels at which a line or polygon class is enforced, and whether it is in the overview. The
    bounds cannot be set for mass points, and the terrain is not invalidated, as documented. INFERRED: a bound not
    given is left as it was."""
    connection, terrain = open_terrain(in_terrain, "ChangeTerrainResolutionBounds")
    try:
        source = terrain.source(feature_class, "ChangeTerrainResolutionBounds")
        if source["sf_type"] == "masspoints":
            raise ExecuteError("ERROR: pyramid resolution bounds can only be assigned for surface types that are not "
                               "designated as masspoints.")
        low = _bound(lower_pyramid_resolution) if _given(lower_pyramid_resolution) else source["min_resolution"]
        high = _bound(upper_pyramid_resolution) if _given(upper_pyramid_resolution) else source["max_resolution"]
        if low is not None and high is not None and low > high:
            raise ExecuteError("ERROR: the lower pyramid resolution is greater than the upper.")
        connection.execute("update grapy_terrain_source set min_resolution = ?, max_resolution = ?, overview = ? "
                           "where terrain = ? and seq = ?",
                           (low, high, 0 if str(overview or "OVERVIEW").upper() in ("NO_OVERVIEW", "FALSE") else 1,
                            terrain.name, source["seq"]))
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_terrain))


# ---- building the pyramid


def _dedupe(points, sources):
    """Of two points at one x,y the later one kept — as grapy's TINs keep the later input (ADR-229 §5.9 (b))."""
    if not len(points):
        return points, sources
    keys = [(float(x), float(y)) for x, y in points[:, :2]]
    last = {}
    for i, k in enumerate(keys):
        last[k] = i
    keep = np.array(sorted(last.values()), np.int64)
    return points[keep], sources[keep]


#: ArcGIS Pro 3.7's default XY domain origin for the UTM zones and SWEREF99 TM, by EPSG code (read with arcpy's
#: ``SpatialReference(code).domain``, 2026-10-31): north zones (-5120900, -9998100), south zones (-5120900, 1900).
#: Other coordinate systems' defaults follow their own extents and are not tabulated.
_UTM_NORTH = (-5120900.0, -9998100.0)
_UTM_SOUTH = (-5120900.0, 1900.0)
#: An axis whose tile index from the domain's origin reaches this (between 23,169 and 23,170, measured) has its tiles
#: centred on the data instead.
_TILE_INDEX_LIMIT = 23170


def default_xy_origin(srid):
    """Pro's default XY domain origin for ``srid`` where it is known (WGS 84, WGS 72, NAD 83 and ETRS 89 UTM zones,
    SWEREF99 TM), else None."""
    try:
        code = int(srid)
    except (TypeError, ValueError):
        return None
    if 32601 <= code <= 32660 or 32201 <= code <= 32260 or 26901 <= code <= 26923 or 25828 <= code <= 25838 \
            or code == 3006:
        return _UTM_NORTH
    if 32701 <= code <= 32760:
        return _UTM_SOUTH
    return None


def window_anchors(points, spacing, srid, corner):
    """Where each point's windows start, per axis: ArcGIS Pro 3.7's rule, measured 2026-10-31 on 45 terrains (D-339,
    ``tools/pro-compare/adr234-terrain-corner.py``). The data is cut into square tiles of ``round(spacing * √200000)``
    from the XY domain's origin and each tile's windows start at its own corner; an axis whose tile index reaches
    23,170 has its tiles centred on the data's extent instead. Where the domain is not known (``default_xy_origin``),
    INFERRED: one tile from the extent's lower-left ``corner``."""
    n = len(points)
    origin = default_xy_origin(srid)
    if origin is None or not n:
        return np.full(n, float(corner[0])), np.full(n, float(corner[1]))
    tile = float(round(float(spacing) * math.sqrt(200000.0)))
    out = []
    for axis in (0, 1):
        v = points[:, axis]
        index = np.floor((v - origin[axis]) / tile)
        if index.max() >= _TILE_INDEX_LIMIT:
            start = (v.min() + v.max()) / 2 - tile / 2
            out.append(start + np.floor((v - start) / tile) * tile)
        else:
            out.append(origin[axis] + index * tile)
    return out[0], out[1]


def window_thin(points, anchors, size, origin, method):
    """Indexes kept by window size thinning: the area cut into ``size`` squares from ``origin`` — a pair of per-point
    arrays (``window_anchors``), or one corner — and in each the point the window size method selects; of ties, the
    earlier point, ZMEAN's distances to the mean equal within 1e-9 (as ArcGIS Pro 3.7 breaks them, measured
    2026-10-31). Anchor points are always kept."""
    keep = set(np.nonzero(anchors)[0].tolist())
    candidates = np.nonzero(~anchors)[0]
    if not len(candidates):
        return np.array(sorted(keep), np.int64)
    ox = np.broadcast_to(np.asarray(origin[0], np.float64), (len(points),))[candidates]
    oy = np.broadcast_to(np.asarray(origin[1], np.float64), (len(points),))[candidates]
    ix = np.floor((points[candidates, 0] - ox) / size + 1e-12).astype(np.int64)
    iy = np.floor((points[candidates, 1] - oy) / size + 1e-12).astype(np.int64)
    windows: dict = {}
    for k, a, b, i, j in zip(candidates, ox, oy, ix, iy):
        windows.setdefault((float(a), float(b), int(i), int(j)), []).append(int(k))
    for members in windows.values():
        zs = points[members, 2]
        if method in ("ZMIN", "ZMINMAX"):
            keep.add(members[int(np.argmin(zs))])
        if method in ("ZMAX", "ZMINMAX"):
            keep.add(members[int(np.argmax(zs))])
        if method == "ZMEAN":
            distance = np.abs(zs - zs.mean())
            keep.add(members[int(np.nonzero(distance <= distance.min() + 1e-9)[0][0])])
    return np.array(sorted(keep), np.int64)


def _interpolate(kept_xyz, xy):
    """The heights at ``xy`` on the Delaunay triangulation of ``kept_xyz`` (NaN outside it), and each one's simplex."""
    from scipy.spatial import Delaunay
    corner = kept_xyz[:, :2].min(axis=0)   # Qhull drops points on projected coordinates (_tin._delaunay)
    tri = Delaunay(kept_xyz[:, :2] - corner)
    xy = xy - corner
    simplex = tri.find_simplex(xy)
    z = np.full(len(xy), np.nan)
    inside = simplex >= 0
    if inside.any():
        t = tri.transform[simplex[inside]]
        b = np.einsum("ijk,ik->ij", t[:, :2, :], xy[inside] - t[:, 2, :])
        weights = np.column_stack([b, 1 - b.sum(axis=1)])
        z[inside] = (weights * kept_xyz[tri.simplices[simplex[inside]], 2]).sum(axis=1)
    return z, simplex


def ztolerance_thin(points, anchors, tolerance):
    """Indexes kept by the z-tolerance equivalent (greedy insertion, Garland and Heckbert 1995): from the convex hull's
    corners and the anchor points, insert in each triangle the point farthest from the surface, while any point is
    farther than ``tolerance`` from it. On return every point lies within the tolerance of the kept points' surface."""
    from scipy.spatial import ConvexHull, QhullError
    n = len(points)
    if n <= 3:
        return np.arange(n)
    try:
        hull = ConvexHull(points[:, :2]).vertices
    except QhullError:   # every point on one line: nothing to triangulate, so nothing is thinned
        return np.arange(n)
    kept = np.zeros(n, bool)
    kept[hull] = True
    kept |= anchors
    xy = points[:, :2]
    for _ in range(n):
        z, simplex = _interpolate(points[kept], xy)
        error = np.abs(points[:, 2] - z)
        error[kept | np.isnan(error)] = -1.0
        over = np.nonzero(error > tolerance + 1e-12)[0]
        if not len(over):
            break
        worst: dict = {}
        for i in over:
            s = int(simplex[i])
            if s not in worst or error[i] > error[worst[s]]:
                worst[s] = i
        kept[list(worst.values())] = True
    return np.nonzero(kept)[0]


def _referenced_points(terrain, source, tool):
    path = f"{terrain.file}/{terrain.dataset}/{source['feature_class']}"
    return _points_of_class(path, source["height_field"], tool)


def BuildTerrain(in_terrain, update_extent="NO_UPDATE_EXTENT"):
    """The terrain's pyramid built from its feature classes: every mass point at full resolution (level 0), then each
    level thinned by the terrain's pyramid type — window size coarsest level first, each level's windows choosing from
    full resolution and every coarser level's points kept, as ArcGIS Pro 3.7 builds them (measured 2026-10-31; the
    page's "thinned from the level before" is the same thing for ZMIN, ZMAX and ZMINMAX on nested windows, and not for
    ZMEAN or for windows that do not nest); z-tolerance as the equivalent named in the module (greedy insertion),
    measured against full resolution, which is the bound the page states. The lines and polygons are read and kept for the levels within their resolution bounds. The extent
    grows to the data; ``UPDATE_EXTENT`` computes it again from the data alone, as documented.

    Windows start where Pro 3.7's do (``window_anchors``, measured 2026-10-31): at the corner of each tile of
    ``round(spacing * √200000)`` from the XY domain's origin, which grapy knows for the UTM zones; elsewhere,
    INFERRED (ADR-234 §5.9 (d)), from the extent's lower-left corner. At a seam between two tiles Pro keeps a few
    points fewer than grapy (5 of 193 at one level, measured, D-339). INFERRED: of two points at one x,y the later
    source's is kept; z-tolerance levels each start from full resolution,
    so the bound holds at every level."""
    tool = "BuildTerrain"
    mode = str(update_extent or "NO_UPDATE_EXTENT").upper()
    if mode not in ("NO_UPDATE_EXTENT", "UPDATE_EXTENT"):
        _refuse(tool, "update_extent", update_extent)
    connection, terrain = open_terrain(in_terrain, tool)
    try:
        if not terrain.levels:
            raise ExecuteError("ERROR: the terrain has no pyramid level (Add Terrain Pyramid Level).")
        pieces, origins, anchors, features = [], [], [], []
        for source in terrain.sources:
            if source["sf_type"] == "masspoints":
                if source["embedded"]:
                    xyz = np.array(connection.execute("select x, y, z from grapy_terrain_point where terrain = ? and "
                                                      "source = ? order by rowid", (terrain.name, source["name"]))
                                   .fetchall(), np.float64).reshape(-1, 3)
                else:
                    xyz = _referenced_points(terrain, source, tool)
                pieces.append(xyz)
                origins.append(np.full(len(xyz), source["seq"], np.int64))
                anchors.append(np.full(len(xyz), bool(source["anchor"])))
            else:
                path = f"{terrain.file}/{terrain.dataset}/{source['feature_class']}"
                found, _, _ = _tin.read_features(path, source["height_field"], source["sf_type"], None, tool)
                features.extend((source["seq"], source["sf_type"], t, h, p, s) for s, p, h, t in found)
        points = np.concatenate(pieces) if pieces else np.zeros((0, 3))
        origin = np.concatenate(origins) if origins else np.zeros(0, np.int64)
        anchor = np.concatenate(anchors) if anchors else np.zeros(0, bool)
        if len(points):
            ids = np.arange(len(points))
            points_d, ids_d = _dedupe(points, ids)
            points, origin, anchor = points_d, origin[ids_d], anchor[ids_d]
        boxes = [] if not len(points) else [(points[:, 0].min(), points[:, 1].min(), points[:, 0].max(),
                                             points[:, 1].max())]
        boxes += [shapely.bounds(f[5]).tolist() for f in features]
        data = (min(b[0] for b in boxes), min(b[1] for b in boxes), max(b[2] for b in boxes),
                max(b[3] for b in boxes)) if boxes else None
        if mode == "NO_UPDATE_EXTENT" and terrain.extent and data:
            e = terrain.extent
            box = (min(e[0], data[0]), min(e[1], data[1]), max(e[2], data[2]), max(e[3], data[3]))
        else:
            box = data
        connection.execute("delete from grapy_terrain_pyramid where terrain = ?", (terrain.name,))
        connection.execute("delete from grapy_terrain_feature where terrain = ?", (terrain.name,))

        def store(resolution, which):
            connection.executemany("insert into grapy_terrain_pyramid values (?, ?, ?, ?, ?, ?)",
                                   [(terrain.name, resolution, float(points[i, 0]), float(points[i, 1]),
                                     float(points[i, 2]), int(origin[i])) for i in which])

        store(0.0, range(len(points)))
        if terrain.pyramid_type == "WINDOWSIZE":
            coarser = np.zeros(0, np.int64)
            starts = window_anchors(points, terrain.point_spacing, terrain.srid, box[:2] if box else (0, 0))
            for resolution, _ in sorted(terrain.levels, reverse=True):
                chosen = window_thin(points, anchor, resolution, starts, terrain.window_method)
                coarser = np.union1d(chosen, coarser)
                store(resolution, coarser)
        else:
            for resolution, _ in terrain.levels:
                store(resolution, ztolerance_thin(points, anchor, resolution))
        if terrain.pyramid_type == "ZTOLERANCE" and terrain.levels:
            AddWarning("BuildTerrain's z-tolerance levels are an equivalent, not ArcGIS Pro's own thinning: "
                       f"{Z_TOLERANCE_EQUIVALENT[0]} ({Z_TOLERANCE_EQUIVALENT[1]}). Every point is within the "
                       "tolerance, as documented; which points are kept may differ from Pro's.")
        connection.executemany("insert into grapy_terrain_feature values (?, ?, ?, ?, ?, ?, ?)",
                               [(terrain.name, seq, sf, int(tag), height, int(pending),
                                 shapely.to_wkb(shape, flavor="iso")) for seq, sf, tag, height, pending, shape in features])
        connection.execute("update grapy_terrain set built = 1, xmin = ?, ymin = ?, xmax = ?, ymax = ?, built_at = ? "
                           "where name = ?", (*(box if box else (None,) * 4),
                                              datetime.datetime.now(datetime.timezone.utc).isoformat(), terrain.name))
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_terrain))


# ---- editing a terrain's points


def _area(value, tool):
    """A shapely area of interest from an Extent, an "xmin ymin xmax ymax" string, or polygon features; None for none."""
    from ._reference import Extent
    if not _given(value):
        return None
    if isinstance(value, Extent):
        return shapely.box(value.XMin, value.YMin, value.XMax, value.YMax)
    text = _text(value)
    parts = text.replace(",", " ").split()
    if len(parts) >= 4:
        try:
            return shapely.box(*[float(p) for p in parts[:4]])
        except ValueError:
            pass
    from . import da
    shapes = []
    with da.SearchCursor(value, ["SHAPE@"]) as rows:
        shapes = [shapely.force_2d(r[0]._shape) for r in rows if r[0] is not None]
    if not shapes:
        raise ExecuteError(f"ERROR: {value} holds no polygons for the area of interest ({tool}).")
    return shapely.union_all(shapes)


def _inside(xy, area):
    if area is None:
        return np.ones(len(xy), bool)
    return shapely.covers(area, shapely.points(xy[:, 0], xy[:, 1]))


def _point_source(terrain, name, tool):
    source = terrain.source(name, tool)
    if source["sf_type"] != "masspoints":
        raise ExecuteError(f"ERROR: {source['name']} is not a point source of the terrain ({tool}).")
    return source


def _delete_points(connection, terrain, source, area):
    """Deletes a source's points inside ``area`` (all with None); a multipoint keeps its vertices outside it."""
    if source["embedded"]:
        rows = connection.execute("select rowid, x, y from grapy_terrain_point where terrain = ? and source = ?",
                                  (terrain.name, source["name"])).fetchall()
        if rows:
            xy = np.array([r[1:] for r in rows], np.float64)
            gone = [rows[i][0] for i in np.nonzero(_inside(xy, area))[0]]
            connection.executemany("delete from grapy_terrain_point where rowid = ?", [(g,) for g in gone])
        return
    from . import da
    path = f"{terrain.file}/{terrain.dataset}/{source['feature_class']}"
    connection.commit()
    with da.UpdateCursor(path, ["SHAPE@"]) as rows:
        for row in rows:
            if row[0] is None:
                continue
            xyz = shapely.get_coordinates(row[0]._shape, include_z=True)
            inside = _inside(xyz, area)
            if inside.all():
                rows.deleteRow()
            elif inside.any():
                rows.updateRow([shapely.MultiPoint(xyz[~inside])])


def _append_points(connection, terrain, source, in_point_features, area, tool):
    from . import da
    from .analysis import _schema
    _, kind, _, _ = _schema(in_point_features)
    if kind not in ("Point", "Multipoint"):
        raise ExecuteError(f"ERROR: {in_point_features} is {kind}; the input must be points or multipoints ({tool}).")
    xyz = _points_of_class(_text(in_point_features), "Shape.Z", tool)
    xyz = xyz[_inside(xyz, area)] if len(xyz) else xyz
    if source["embedded"]:
        connection.executemany("insert into grapy_terrain_point values (?, ?, ?, ?, ?)",
                               [(terrain.name, source["name"], float(x), float(y), float(z)) for x, y, z in xyz])
        return len(xyz)
    path = f"{terrain.file}/{terrain.dataset}/{source['feature_class']}"
    _, target, _, _ = _schema(path)
    connection.commit()
    with da.InsertCursor(path, ["SHAPE@"]) as written:
        if target == "Multipoint":
            if len(xyz):
                written.insertRow([shapely.MultiPoint(xyz)])
        else:
            for p in xyz:
                written.insertRow([shapely.Point(*p)])
    return len(xyz)


def AppendTerrainPoints(in_terrain, terrain_feature_class, in_point_features, polygon_features_or_extent=None):
    """Points or multipoints appended to one of the terrain's point sources — an embedded one, or a referenced point
    class, which gains the rows — limited to the area of interest when one is given; the terrain is invalidated, as
    documented. INFERRED: the heights are the input's z; a point on the area's edge is inside it."""
    tool = "AppendTerrainPoints"
    connection, terrain = open_terrain(in_terrain, tool)
    try:
        source = _point_source(terrain, terrain_feature_class, tool)
        _append_points(connection, terrain, source, in_point_features, _area(polygon_features_or_extent, tool), tool)
        _invalidate(connection, terrain.name)
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_terrain))


def ReplaceTerrainPoints(in_terrain, terrain_feature_class, in_point_features, polygon_features_or_extent=None):
    """A point source's points replaced by the input's: within the area of interest when one is given — the source's
    points there deleted and the input's points there added — or every point without one; the terrain is invalidated,
    as documented. INFERRED: how replacement is limited by the area (the page says only that it limits where points are
    replaced)."""
    tool = "ReplaceTerrainPoints"
    connection, terrain = open_terrain(in_terrain, tool)
    try:
        source = _point_source(terrain, terrain_feature_class, tool)
        area = _area(polygon_features_or_extent, tool)
        _delete_points(connection, terrain, source, area)
        _append_points(connection, terrain, source, in_point_features, area, tool)
        _invalidate(connection, terrain.name)
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_terrain))


def DeleteTerrainPoints(in_terrain, data_source, polygon_features_or_extent):
    """The points of one or more of the terrain's point sources that lie in an area — polygons or an extent — deleted;
    a multipoint keeps its vertices outside it. The terrain is invalidated, as documented."""
    tool = "DeleteTerrainPoints"
    area = _area(polygon_features_or_extent, tool)
    if area is None:
        raise ExecuteError("ERROR 000735: Area of Interest: Value is required.")
    connection, terrain = open_terrain(in_terrain, tool)
    try:
        from .management import _list
        for name in _list(data_source):
            _delete_points(connection, terrain, _point_source(terrain, name, tool), area)
        _invalidate(connection, terrain.name)
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_terrain))


def RemoveTerrainPoints(in_terrain, data_source, aoi_extents):
    """The deprecated form of DeleteTerrainPoints: embedded sources only, and a rectangular area of interest — only the
    multipoint vertices inside it deleted — as documented."""
    tool = "RemoveTerrainPoints"
    area = _area(aoi_extents, tool)
    if area is None or not math.isclose(area.area, shapely.envelope(area).area):
        raise ExecuteError("ERROR: the area of interest must be rectangular.")
    connection, terrain = open_terrain(in_terrain, tool)
    try:
        from .management import _list
        for name in _list(data_source):
            source = _point_source(terrain, name, tool)
            if not source["embedded"]:
                raise ExecuteError(f"ERROR: {source['name']} is not embedded in the terrain; this tool works only on "
                                   "embedded feature classes.")
            _delete_points(connection, terrain, source, area)
        _invalidate(connection, terrain.name)
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_terrain))


# ---- reading a level


def _resolution(terrain, value, tool):
    resolution = float(value) if _given(value) else 0.0
    if resolution == 0:
        return 0.0
    for r, _ in terrain.levels:
        if math.isclose(r, resolution):
            return r
    raise ExecuteError(f"ERROR: {value} is not a pyramid level of the terrain {terrain.name} ({tool}); its levels are "
                       f"0 and {', '.join(f'{r:g}' for r, _ in terrain.levels)}.")


def _built(terrain, tool):
    if not terrain.built:
        raise ExecuteError(f"ERROR: the terrain {terrain.name} is not built — it was changed since Build Terrain ran, or "
                           f"never was ({tool}).")


def level_points(connection, terrain, resolution, source=None):
    """(n, 3) points and their source seq at a level."""
    sql = "select x, y, z, source from grapy_terrain_pyramid where terrain = ? and resolution = ?"
    params = [terrain.name, resolution]
    if source is not None:
        sql += " and source = ?"
        params.append(source)
    rows = np.array(connection.execute(sql + " order by rowid", params).fetchall(), np.float64).reshape(-1, 4)
    return rows[:, :3], rows[:, 3].astype(np.int64)


def level_inputs(connection, terrain, resolution, area=None):
    """A level as the stored inputs of a TIN, ``[(sf, shape, tag, height, pending)]``: its mass points as one multipoint
    and the lines and polygons enforced at it (INFERRED: a bound that is not set is open)."""
    points, _ = level_points(connection, terrain, resolution)
    if area is not None and len(points):
        points = points[_inside(points, area)]
    inputs = [("masspoints", shapely.MultiPoint(points), 0, None, False)] if len(points) else []
    bounds = {s["seq"]: (s["min_resolution"], s["max_resolution"]) for s in terrain.sources}
    for seq, sf, tag, height, pending, blob in connection.execute(
            "select source, sf_type, tag, height, pending, shape from grapy_terrain_feature where terrain = ? "
            "order by rowid", (terrain.name,)):
        low, high = bounds.get(seq, (None, None))
        if resolution and ((low is not None and resolution < low - 1e-12) or (high is not None and resolution > high + 1e-12)):
            continue
        shape = shapely.from_wkb(blob)
        if area is not None:
            shape = shapely.intersection(shape, area)
            if shape.is_empty:
                continue
        inputs.append((sf, shape, tag, height, bool(pending)))
    return inputs


def surface(path, resolution=0, tool="terrain"):
    """The Surface (ADR-229's TIN) of a terrain at a pyramid level."""
    connection, terrain = open_terrain(path, tool)
    try:
        _built(terrain, tool)
        level = _resolution(terrain, resolution, tool)
        inputs = level_inputs(connection, terrain, level)
    finally:
        connection.close()
    return _tin.build(inputs, terrain.srid, constrained=terrain.triangulation == "CONSTRAINED_DELAUNAY")


def _env_area():
    from ._reference import Extent
    value = env.extent
    if not _is_set(value, "MAXOF", "MINOF", "INTERSECTION_OF_INPUTS", "DEFAULT"):
        return None
    if isinstance(value, Extent):
        return shapely.box(value.XMin, value.YMin, value.XMax, value.YMax)
    return shapely.box(*[float(p) for p in str(value).replace(",", " ").split()[:4]])


# ---- terrain to TIN, points and raster


def TerrainToTin(in_terrain, out_tin, pyramid_level_resolution=0, max_nodes=5000000, clip_to_extent="CLIP"):
    """A TIN of a terrain at a pyramid level: the level's points and the lines and polygons enforced at it, triangulated
    as a TIN is (ADR-229). With an extent environment and ``CLIP`` (the default), the points and features outside it
    are left out, as documented; a TIN of more than ``max_nodes`` nodes (5 million by default) fails, as the page says."""
    tool = "TerrainToTin"
    connection, terrain = open_terrain(in_terrain, tool)
    try:
        _built(terrain, tool)
        level = _resolution(terrain, pyramid_level_resolution, tool)
        area = _env_area() if str(clip_to_extent or "CLIP").upper() in ("CLIP", "TRUE") else None
        inputs = level_inputs(connection, terrain, level, area)
    finally:
        connection.close()
    built = _tin.build(inputs, terrain.srid, constrained=terrain.triangulation == "CONSTRAINED_DELAUNAY")
    limit = int(max_nodes) if _given(max_nodes) else 5000000
    if len(built.nodes) > limit:
        raise ExecuteError(f"ERROR: the TIN would have {len(built.nodes)} nodes, more than max_nodes ({limit}); choose a "
                           "coarser pyramid level or a smaller extent.")
    _tin.store(out_tin, terrain.srid, inputs, None, built)
    return Result(_text(out_tin))


def TerrainToPoints(in_terrain, out_feature_class, pyramid_level_resolution=0, source_embedded_feature_class=None,
                    out_geometry_type="MULTIPOINT"):
    """The points of a terrain at a pyramid level, as 3D points or multipoints; with ``source_embedded_feature_class``
    only that embedded class's, as documented; within the extent environment when one is set (the page's "area of
    interest", INFERRED). INFERRED (ADR-234 §5.9 (d)): MULTIPOINT writes one multipoint per source, in the sources'
    order; no attribute is written, grapy's embedded points carrying none."""
    from . import _tintools
    tool = "TerrainToPoints"
    kind = str(out_geometry_type or "MULTIPOINT").upper()
    if kind not in ("MULTIPOINT", "POINT"):
        _refuse(tool, "out_geometry_type", out_geometry_type)
    connection, terrain = open_terrain(in_terrain, tool)
    try:
        _built(terrain, tool)
        level = _resolution(terrain, pyramid_level_resolution, tool)
        only = None
        if _given(source_embedded_feature_class):
            source = terrain.source(source_embedded_feature_class, tool)
            if not source["embedded"]:
                raise ExecuteError(f"ERROR: {source['name']} is not an embedded feature class of the terrain.")
            only = source["seq"]
        points, origin = level_points(connection, terrain, level, only)
    finally:
        connection.close()
    area = _env_area()
    if area is not None and len(points):
        keep = _inside(points, area)
        points, origin = points[keep], origin[keep]
    if kind == "POINT":
        rows = [(shapely.Point(*p),) for p in points]
    else:
        rows = [(shapely.MultiPoint(points[origin == s]),) for s in dict.fromkeys(origin.tolist())]
    return Result(_tintools._write(out_feature_class, "Point" if kind == "POINT" else "Multipoint", terrain.srid, [],
                                   True, rows))


def TerrainToRaster(in_terrain, out_raster, data_type="FLOAT", method="LINEAR", sample_distance="OBSERVATIONS 250",
                    pyramid_level_resolution=0, sample_value=None):
    """A raster of a terrain at a pyramid level, interpolated linearly on the level's triangles as TinRaster does from a
    TIN (ADR-229): ``OBSERVATIONS`` (250 across the longer side by default) or ``CELLSIZE``; ``INT`` rounds to the
    nearest whole number. Refused, as on a TIN: ``NATURAL_NEIGHBORS`` (ADR-229 §5.4)."""
    from . import _tintools
    level_surface = surface(in_terrain, pyramid_level_resolution, "TerrainToRaster")
    return _tintools.raster_of(level_surface, out_raster, data_type, method, sample_distance, 1, sample_value,
                               "TerrainToRaster")


# ---- describe


class Described:
    """What Describe answers for a terrain: dataType ``Terrain``, the Dataset group's names, and the settings
    CreateTerrain takes under the names INFERRED from its parameters (ADR-234 §5.9 (e)): ``pyramidType``,
    ``windowSizeMethod``, ``secondaryThinningMethod``, ``secondaryThinningThreshold``, ``averagePointSpacing``,
    ``maxOverviewSize``; ``pyramidLevels`` (resolution, reference scale) and ``dataSources`` (names), ``isBuilt``."""

    def __init__(self, path, terrain):
        from ._reference import Extent, SpatialReference
        self.catalogPath = _text(path)
        self.name = self.baseName = terrain.name
        self.path = f"{terrain.file}/{terrain.dataset}"
        self.dataType = self.datasetType = "Terrain"
        self.pyramidType = terrain.pyramid_type
        self.windowSizeMethod = terrain.window_method
        self.secondaryThinningMethod = terrain.secondary_method
        self.secondaryThinningThreshold = terrain.secondary_threshold
        self.averagePointSpacing = terrain.point_spacing
        self.maxOverviewSize = terrain.max_overview_size
        self.pyramidLevels = list(terrain.levels)
        self.dataSources = [s["name"] for s in terrain.sources]
        self.isBuilt = terrain.built
        self.spatialReference = SpatialReference(terrain.srid) if terrain.srid > 0 else None
        self.extent = Extent(*terrain.extent, self.spatialReference) if terrain.extent else None


def describe(path):
    try:
        if not exists(path):
            return None
    except (ValueError, TypeError):
        return None
    file, _, _ = locate(path)
    connection = _gpkg.connect(file)
    try:
        return Described(path, Terrain(connection, path))
    finally:
        connection.close()
