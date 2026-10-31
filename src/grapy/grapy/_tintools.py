"""The TIN tools of arcpy.ddd and arcpy.management, over grapy's TIN (``_tin``) — ADR-229.

Each is built to its reference page on pro.arcgis.com, read on 2026-10-30; what the page leaves open is labelled
INFERRED in the tool's docstring, and what grapy cannot do is refused by name. ``arcpy.ddd`` and ``arcpy.management``
answer these functions under their own names.
"""
from __future__ import annotations

import math
import os

import numpy as np
import shapely

from . import _gpkg, _tin
from ._result import Result
from ._runtime import ExecuteError, NotBuiltError
from ._runtime import _is_given  # noqa: E402 — a Raster is never compared with ==

_FLAT = 1e-12


def _refuse(tool, parameter, value, why=""):
    raise NotBuiltError(f"arcpy.ddd.{tool}: {parameter} '{value}' is not built in grapy{': ' + why if why else ''}.")


def _given(value):
    return _is_given(value)


def surface_of(value, tool, pyramid_level_resolution=0):
    """The Surface a tool's ``in_surface`` names: a TIN, a LAS dataset or layer triangulated from its points (as
    PolygonVolume's page says a LAS dataset is processed), or a terrain at a pyramid level (ADR-234); None for anything
    else. A pyramid level other than 0 is refused on a TIN or LAS dataset, which have none."""
    from . import _las, _terrain
    if _terrain.exists(value):
        return _terrain.surface(value, pyramid_level_resolution, tool)
    if _given(pyramid_level_resolution) and float(pyramid_level_resolution) != 0 and is_surface(value):
        _refuse(tool, "pyramid_level_resolution", pyramid_level_resolution,
                "only a terrain has pyramid levels; a TIN or LAS dataset is read at full resolution")
    if _tin.exists(value):
        return _tin.load(value)
    if _las.is_las(value):
        return _las.surface(value, tool)
    return None


def is_surface(value):
    from . import _las, _terrain
    return _tin.exists(value) or _las.is_las(value) or _terrain.exists(value)


def _write(out, kind, srid, columns, has_z, rows):
    """A new feature class at ``out`` (a GeoPackage path; ``.gpkg`` added as analysis tools add it) holding ``rows`` of
    (values…, shapely shape)."""
    from . import da
    from .analysis import _out
    path = _out(out)
    _gpkg.create(path, kind, srid, columns, has_z, False)
    with da.InsertCursor(path, [*(c.name for c in columns), "SHAPE@"]) as cursor:
        for row in rows:
            cursor.insertRow(row)
    return path


def _with_node_z(geometry, surface):
    """A 2D geometry whose vertices are TIN nodes, given each node's z."""
    lookup = {(float(x), float(y)): float(z) for x, y, z in surface.nodes}

    def z_of(_, xy):
        return np.array([lookup.get((float(x), float(y)), np.nan) for x, y in xy])
    return _gpkg.rebuild(geometry, True, False, None, z_of)


# ---- making and editing TINs


def CreateTin(out_tin, spatial_reference=None, in_features=None, constrained_delaunay="DELAUNAY"):
    """A TIN from surface features, triangulated Delaunay with breaklines made conforming by densification points, as
    the DELAUNAY option documents: mass points become nodes; hard and soft lines, and the boundaries of clip, erase,
    replace and value fill polygons, become enforced edges; triangles outside clip polygons or inside erase polygons are
    outside the data area; nodes inside a replace polygon take its constant height; triangles and nodes inside a value
    fill polygon take the integer in its tag field. A feature whose height is ``<None>`` is interpolated from the TIN
    built from the others first, as documented. With no ``in_features``, an empty TIN.

    Refused: CONSTRAINED_DELAUNAY — scipy has no constrained Delaunay triangulation (ADR-229 §5.3).

    INFERRED, the page leaving them open: a replace polygon's height is its height field's value, or with ``Shape.Z`` its
    first vertex's z; a triangle is inside a polygon when its centroid is; densification points split a breakline
    segment at its midpoint; of two inputs at the same x,y the later one's z is kept; where two breaklines cross, the
    crossing takes the first one's z. See ``_tin`` for how a TIN is stored."""
    from ._reference import reference_of
    if str(constrained_delaunay or "DELAUNAY").upper() not in ("DELAUNAY", "FALSE"):
        _refuse("CreateTin", "constrained_delaunay", constrained_delaunay,
                "scipy has no constrained Delaunay triangulation, so grapy's TINs are conforming Delaunay (ADR-229 §5.3)")
    inputs, srid = _tin.gather(in_features, "CreateTin")
    if _given(spatial_reference):
        reference = reference_of(spatial_reference)
        srid = reference.factoryCode if reference is not None else srid
    _tin.store(out_tin, srid, inputs)
    return Result(str(out_tin))


def EditTin(in_tin, in_features, constrained_delaunay="DELAUNAY"):
    """Surface features added to a TIN, which is triangulated again from all its inputs, as CreateTin builds one. The
    value table's columns are EditTin's: feature class, height field, tag field, surface feature type, and ``use_z``
    (False reads the height from m-values), as documented. A delineation made by DelineateTinDataArea is applied again
    (INFERRED).

    Refused: CONSTRAINED_DELAUNAY (ADR-229 §5.3)."""
    if str(constrained_delaunay or "DELAUNAY").upper() not in ("DELAUNAY", "FALSE"):
        _refuse("EditTin", "constrained_delaunay", constrained_delaunay,
                "scipy has no constrained Delaunay triangulation (ADR-229 §5.3)")
    stored, delineation = _tin.inputs_of(in_tin)
    srid = _tin.load(in_tin).srid
    added, own = _tin.gather(in_features, "EditTin", edit=True)
    _tin.store(in_tin, srid or own, stored + added, delineation)
    return Result(str(in_tin))


def CopyTin(in_tin, out_tin, version="CURRENT"):
    """A copy of a TIN. Refused: ``PRE_10.0``, an Esri storage version grapy's TIN has no counterpart of."""
    if str(version or "CURRENT").upper() != "CURRENT":
        _refuse("CopyTin", "version", version, "it is a storage version of Esri's TIN format, which grapy does not write")
    surface = _tin.load(in_tin)
    inputs, delineation = _tin.inputs_of(in_tin)
    _tin.store(out_tin, surface.srid, inputs, delineation, surface)
    return Result(str(out_tin))


def DelineateTinDataArea(in_tin, max_edge_length, method="PERIMETER_ONLY"):
    """The TIN's data area redefined by edge length, as documented: every triangle is first put back in the data area
    ("This will effectively undo any existing data area classification"); then with ``ALL`` every triangle with an edge
    longer than ``max_edge_length`` (2D) is masked, and with ``PERIMETER_ONLY`` triangles are masked from the outside in
    for as long as a boundary edge exceeds it. INFERRED: "exceed" is strictly longer; the delineation is kept and applied
    again when EditTin re-triangulates."""
    kind = str(method or "PERIMETER_ONLY").upper()
    if kind not in ("PERIMETER_ONLY", "ALL"):
        _refuse("DelineateTinDataArea", "method", method)
    surface = _tin.load(in_tin)
    inputs, _ = _tin.inputs_of(in_tin)
    _tin.delineate(surface, float(max_edge_length), kind)
    _tin.store(in_tin, surface.srid, inputs, (float(max_edge_length), kind), surface)
    return Result(str(in_tin))


def MakeTinLayer(in_tin, out_layer):
    """A layer name over a TIN, which the TIN tools accept in its place for as long as the process runs."""
    if not _tin.exists(in_tin):
        raise ExecuteError(f"ERROR 000732: Input TIN: Dataset {in_tin} does not exist or is not supported.")
    _tin.LAYERS[str(out_layer)] = _tin.LAYERS.get(str(in_tin), in_tin)
    return Result(str(out_layer))


# ---- TIN to features


def TinDomain(in_tin, out_feature_class, out_geometry_type):
    """The TIN's data area, as one z-enabled feature — a polygon, or its boundary as a line — single or multipart, as
    documented. Without clip or erase polygons it is the convex hull of the nodes."""
    kind = str(out_geometry_type or "").upper()
    if kind not in ("LINE", "POLYGON"):
        raise ExecuteError("ERROR 000800: out_geometry_type must be LINE or POLYGON.")
    surface = _tin.load(in_tin)
    area = shapely.normalize(surface.domain())
    shape = area if kind == "POLYGON" else shapely.boundary(area)
    if shape.geom_type == "LineString":
        shape = shapely.MultiLineString([shape])
    elif shape.geom_type == "Polygon":
        shape = shapely.MultiPolygon([shape])
    rows = [] if area.is_empty else [(_with_node_z(shape, surface),)]
    return Result(_write(out_feature_class, "Polygon" if kind == "POLYGON" else "Polyline", surface.srid, [], True, rows))


_EDGE_TYPES = ("DATA", "SOFT", "HARD", "ENFORCED", "REGULAR", "OUTSIDE", "ALL")


def TinEdge(in_tin, out_feature_class, edge_type="DATA"):
    """3D lines of the TIN's edges with ``EdgeType`` — 0 regular, 1 soft, 2 hard, as documented — selected by
    ``edge_type``: ``DATA`` the edges of the interpolation zone, ``SOFT``, ``HARD``, ``ENFORCED`` (not introduced by the
    triangulation), ``REGULAR`` (introduced by it), ``OUTSIDE`` (excluded from the interpolation zone) or ``ALL``.
    INFERRED: an edge is in the interpolation zone when a triangle on either side is; an outside edge keeps its own type
    code; an enforced edge is a piece of a breakline or polygon boundary, densification points included."""
    kind = str(edge_type or "DATA").upper()
    if kind not in _EDGE_TYPES:
        _refuse("TinEdge", "edge_type", edge_type)
    surface = _tin.load(in_tin)
    rows = []
    for (i, j), ts in sorted(surface.all_edges().items()):
        code = int(surface.edges.get((i, j), 0))
        data = any(surface.inside[t] for t in ts)
        keep = {"DATA": data, "SOFT": code == 1, "HARD": code == 2, "ENFORCED": code > 0, "REGULAR": code == 0,
                "OUTSIDE": not data, "ALL": True}[kind]
        if keep:
            rows.append((code, shapely.MultiLineString([[surface.nodes[i], surface.nodes[j]]])))
    return Result(_write(out_feature_class, "Polyline", surface.srid, [_gpkg.Column("EdgeType", "Integer")], True, rows))


def TinNode(in_tin, out_feature_class, spot_field=None, tag_field=None):
    """The TIN's nodes as 3D points, or 2D points with their height in ``spot_field``, and their tag in ``tag_field``
    when one is named (0 for a node without one), as documented. INFERRED: every node is written, densification points
    included."""
    surface = _tin.load(in_tin)
    columns = []
    if _given(spot_field):
        columns.append(_gpkg.Column(str(spot_field), "Double"))
    if _given(tag_field):
        columns.append(_gpkg.Column(str(tag_field), "Integer"))
    rows = []
    for (x, y, z), tag in zip(surface.nodes, surface.node_tags):
        values = ([float(z)] if _given(spot_field) else []) + ([int(tag)] if _given(tag_field) else [])
        point = shapely.Point(x, y) if _given(spot_field) else shapely.Point(x, y, z)
        rows.append((*values, point))
    return Result(_write(out_feature_class, "Point", surface.srid, columns, not _given(spot_field), rows))


def _slopes(surface, z_factor=1.0):
    """Each triangle's slope (radians) and aspect (degrees clockwise from north, downslope; −1 flat), from its plane."""
    a, b, _ = surface.planes(z_factor)
    gradient = np.hypot(a, b)
    slope = np.arctan(gradient)
    aspect = np.where(gradient <= _FLAT, -1.0, np.degrees(np.arctan2(-a, -b)) % 360)
    return slope, aspect, gradient


def _hillshade(text):
    parts = str(text).replace(",", " ").split()
    if not parts or parts[0].upper() != "HILLSHADE":
        raise ExecuteError(f"ERROR: hillshade is written 'HILLSHADE <azimuth>, <angle>', not '{text}'.")
    azimuth = float(parts[1]) if len(parts) > 1 else 315.0
    altitude = float(parts[2]) if len(parts) > 2 else 45.0
    return azimuth, altitude


def TinTriangle(in_tin, out_feature_class, units="PERCENT", z_factor=1, hillshade=None, tag_field=None):
    """The data area's triangles as 3D polygons with ``Slope_Pct`` or ``Slope_Deg`` and ``Aspect`` (degrees clockwise
    from north, −1 for a flat triangle), "based on the plane of the triangle", as documented; with ``hillshade``
    (``"HILLSHADE <azimuth>, <altitude>"``) a brightness 0–255, and with ``tag_field`` the triangle's tag.

    INFERRED: the brightness field is ``Hillshade``, computed with the standard illumination model — 255 × (cos zenith ·
    cos slope + sin zenith · sin slope · cos(azimuth − aspect)), 0 where negative — which the page does not give; slope
    and aspect are doubles, the hillshade and tag integers; a triangle outside the data area is not written."""
    kind = str(units or "PERCENT").upper()
    if kind not in ("PERCENT", "DEGREE"):
        _refuse("TinTriangle", "units", units)
    zf = float(z_factor if _given(z_factor) else 1)
    surface = _tin.load(in_tin)
    slope, aspect, gradient = _slopes(surface, zf)
    columns = [_gpkg.Column("Slope_Pct" if kind == "PERCENT" else "Slope_Deg", "Double"), _gpkg.Column("Aspect", "Double")]
    shade = None
    if _given(hillshade):
        azimuth, altitude = _hillshade(hillshade)
        zenith = math.radians(90 - altitude)
        shade = 255 * (math.cos(zenith) * np.cos(slope) + math.sin(zenith) * np.sin(slope) *
                       np.cos(np.radians(azimuth - np.where(aspect < 0, 0, aspect))))
        shade = np.clip(np.rint(shade), 0, 255).astype(int)
        columns.append(_gpkg.Column("Hillshade", "Integer"))
    if _given(tag_field):
        columns.append(_gpkg.Column(str(tag_field), "Integer"))
    rows = []
    for t in np.nonzero(surface.inside)[0]:
        ring = surface.nodes[surface.triangles[t]]
        values = [float(gradient[t] * 100 if kind == "PERCENT" else math.degrees(slope[t])), float(aspect[t])]
        if shade is not None:
            values.append(int(shade[t]))
        if _given(tag_field):
            values.append(int(surface.triangle_tags[t]))
        rows.append((*values, shapely.MultiPolygon([shapely.Polygon(ring)])))
    return Result(_write(out_feature_class, "Polygon", surface.srid, columns, True, rows))


def TinLine(in_tin, out_feature_class, code_field="Code"):
    """The TIN's breaklines as 3D lines with ``Code`` 1 soft and 2 hard, as documented. INFERRED: connected pieces of
    one code are merged into one line."""
    surface = _tin.load(in_tin)
    rows = []
    for code in (1, 2):
        pieces = [shapely.LineString([surface.nodes[i], surface.nodes[j]]) for (i, j), c in surface.edges.items()
                  if c == code]
        if not pieces:
            continue
        merged = shapely.line_merge(shapely.MultiLineString(pieces))
        for line in getattr(merged, "geoms", [merged]):
            rows.append((code, shapely.MultiLineString([line])))
    return Result(_write(out_feature_class, "Polyline", surface.srid,
                         [_gpkg.Column(str(code_field or "Code"), "Integer")], True, rows))


def _dissolved(surface, codes, which):
    """[(code, polygon)] of contiguous triangles with the same code, each connected area one polygon."""
    out = []
    polygons = surface.polygons()
    for code in sorted(set(codes[which].tolist())):
        merged = shapely.union_all(polygons[which[codes[which] == code]])
        for part in getattr(merged, "geoms", [merged]):
            if not part.is_empty:
                out.append((code, shapely.MultiPolygon([shapely.normalize(part)])))
    return out


def TinPolygonTag(in_tin, out_feature_class, tag_field="Tag_Value"):
    """Polygons of contiguous triangles with the same tag, 0 where none was given, as documented. INFERRED: only the
    data area's triangles; the polygons are 2D."""
    surface = _tin.load(in_tin)
    which = np.nonzero(surface.inside)[0]
    rows = _dissolved(surface, surface.triangle_tags, which)
    return Result(_write(out_feature_class, "Polygon", surface.srid,
                         [_gpkg.Column(str(tag_field or "Tag_Value"), "Integer")], False, rows))


# ---- TIN to raster


def TinRaster(in_tin, out_raster, data_type="FLOAT", method="LINEAR", sample_distance="OBSERVATIONS 250", z_factor=1,
              sample_value=None):
    """A raster of the TIN: each cell whose centre falls in the data area takes the height of the triangle's plane there
    ("Linear interpolation views TIN triangles as planes"), NoData elsewhere; the lower-left cell is centred on the
    TIN's lower-left corner, as documented. ``OBSERVATIONS n`` divides the longer side into n cells (250 by default);
    ``CELLSIZE s`` sets the cell size. ``INT`` rounds to the nearest whole number.

    Refused: ``NATURAL_NEIGHBORS`` (ADR-229 §5.4); the extent environment.

    INFERRED: the grid holds whole cells from that centre to past the upper-right corner."""
    return raster_of(_tin.load(in_tin), out_raster, data_type, method, sample_distance, z_factor, sample_value,
                     "TinRaster")


def raster_of(surface, out_raster, data_type, method, sample_distance, z_factor, sample_value, tool):
    """TinRaster's raster of a Surface — a TIN's, or a terrain's at a pyramid level (TerrainToRaster, ADR-234)."""
    from ._raster import Raster
    if str(method or "LINEAR").upper() != "LINEAR":
        _refuse(tool, "method", method, "natural neighbour interpolation over a TIN is not built (ADR-229 §5.4)")
    kind = str(data_type or "FLOAT").upper()
    if kind not in ("FLOAT", "INT"):
        _refuse(tool, "data_type", data_type)
    grid = _grid_for(surface.extent(), sample_distance, sample_value, surface.srid, centred=True)
    xs, ys = grid.centres()
    gx, gy = np.meshgrid(xs, ys)
    zs = surface.z_at(gx.ravel(), gy.ravel(), float(z_factor if _given(z_factor) else 1)).reshape(gx.shape)
    mask = np.isnan(zs)
    array = np.where(mask, 0, np.rint(zs)).astype(np.int32) if kind == "INT" else np.where(mask, 0, zs).astype(np.float32)
    return Result(Raster._of(array, mask, grid).save(out_raster))


def _grid_for(box, sample_distance, sample_value, srid, centred):
    """A grid over ``box``: ``OBSERVATIONS n`` or ``CELLSIZE s`` (or the kind with ``sample_value`` apart)."""
    from ._raster import _Grid
    if box is None:
        raise ExecuteError("ERROR: the surface is empty.")
    parts = str(sample_distance or "OBSERVATIONS").split()
    kind = parts[0].upper()
    value = float(parts[1]) if len(parts) > 1 else (float(sample_value) if _given(sample_value) else
                                                     (250.0 if kind == "OBSERVATIONS" else None))
    xmin, ymin, xmax, ymax = box
    width, height = xmax - xmin, ymax - ymin
    if kind == "OBSERVATIONS":
        size = max(width, height) / value
    elif kind == "CELLSIZE":
        if value is None:
            raise ExecuteError("ERROR 000735: CELLSIZE needs a cell size.")
        size = value
    else:
        raise ExecuteError(f"ERROR 000800: sampling is OBSERVATIONS or CELLSIZE, not '{sample_distance}'.")
    if size <= 0:
        raise ExecuteError("ERROR: the cell size must be positive.")
    if centred:
        columns = int(math.floor(width / size + 1e-9)) + 1
        rows = int(math.floor(height / size + 1e-9)) + 1
        left, bottom = xmin - size / 2, ymin - size / 2
    else:
        columns = max(1, int(math.ceil(width / size - 1e-9)))
        rows = max(1, int(math.ceil(height / size - 1e-9)))
        left, bottom = xmin, ymin
    return _Grid(left, bottom + rows * size, size, size, rows, columns, srid)


# ---- contours


def _levels(lo, hi, interval, base):
    first = math.ceil((lo - base) / interval - 1e-9)
    last = math.floor((hi - base) / interval + 1e-9)
    return [base + k * interval for k in range(first, last + 1)]


def contour_lines(surface, interval, base=0.0, z_factor=1.0):
    """{level: shapely line or multiline} by slicing each data-area triangle's plane, as documented ("each triangle is
    treated as a plane. Portions of individual contours within a triangle are straight"). A node exactly at a level
    counts as above it, so a contour passes through it once."""
    which = np.nonzero(surface.inside)[0]
    if not len(which):
        return {}
    z = surface.nodes[:, 2] * z_factor
    used = np.unique(surface.triangles[which])
    levels = _levels(float(z[used].min()), float(z[used].max()), float(interval), float(base))
    segments: dict = {}
    for t in which:
        corners = surface.triangles[t]
        zs = z[corners]
        lo, hi = zs.min(), zs.max()
        for level in levels:
            if not (lo < level <= hi) and not (lo <= level < hi):
                continue
            points = []
            for i, j in ((0, 1), (1, 2), (2, 0)):
                za, zb = zs[i], zs[j]
                if (za >= level) != (zb >= level):
                    s = (level - za) / (zb - za)
                    pa, pb = surface.nodes[corners[i], :2], surface.nodes[corners[j], :2]
                    points.append(tuple(pa + s * (pb - pa)))
            if len(points) == 2 and points[0] != points[1]:
                segments.setdefault(level, []).append(points)
    return {level: shapely.line_merge(shapely.MultiLineString(pieces)) for level, pieces in segments.items()}


def SurfaceContour(in_surface, out_feature_class, interval, base_contour=0, contour_field="Contour",
                   contour_field_precision=0, index_interval=None, index_interval_field="Index", z_factor=1,
                   pyramid_level_resolution=0):
    """2D contour lines of a TIN or LAS dataset at ``base_contour`` + k × ``interval``, heights multiplied by
    ``z_factor`` first, each triangle treated as a plane, as documented. With ``index_interval``, ``Index`` is 1 on index
    contours and 0 on the rest.

    On a terrain, any of its pyramid levels (ADR-234); a TIN or LAS dataset has only level 0.

    INFERRED: the contour field is ``Contour``; a precision of 0 makes it an integer field and 1–9 a double rounded to
    that many places; an index contour is one whose value less the base is a multiple of the index interval; each
    connected run of a contour is one feature."""
    surface = surface_of(in_surface, "SurfaceContour", pyramid_level_resolution)
    if surface is None:
        _refuse("SurfaceContour", "in_surface", in_surface, "it is neither a TIN, a LAS dataset nor a terrain grapy holds")
    interval = float(interval)
    if interval <= 0:
        raise ExecuteError("ERROR: the contour interval must be positive.")
    base = float(base_contour if _given(base_contour) else 0)
    precision = int(contour_field_precision if _given(contour_field_precision) else 0)
    field = str(contour_field if _given(contour_field) else "Contour")
    columns = [_gpkg.Column(field, "Integer" if precision == 0 else "Double")]
    index = float(index_interval) if _given(index_interval) else None
    if index is not None:
        columns.append(_gpkg.Column(str(index_interval_field or "Index"), "Integer"))
    rows = []
    for level, shape in sorted(contour_lines(surface, interval, base, float(z_factor if _given(z_factor) else 1)).items()):
        value = int(round(level)) if precision == 0 else round(level, precision)
        flag = []
        if index is not None:
            ratio = (level - base) / index
            flag = [1 if abs(ratio - round(ratio)) < 1e-9 else 0]
        for line in getattr(shape, "geoms", [shape]):
            rows.append((value, *flag, shapely.MultiLineString([line])))
    return Result(_write(out_feature_class, "Polyline", surface.srid, columns, False, rows))


def TinContour(in_tin, out_feature_class, interval, base_contour=0, contour_field=None, contour_field_precision=0,
               index_interval=None, index_interval_field="Index", z_factor=1):
    """The legacy name of SurfaceContour on a TIN ("superseded by the Surface Contour tool"); see SurfaceContour."""
    if not _tin.exists(in_tin):
        raise ExecuteError(f"ERROR 000732: Input TIN: Dataset {in_tin} does not exist or is not supported.")
    return SurfaceContour(in_tin, out_feature_class, interval, base_contour, contour_field or "Contour",
                          contour_field_precision, index_interval, index_interval_field, z_factor)


# ---- slope and aspect polygons


_SLOPE_PERCENT = [(1, 1.0), (2, 2.15), (3, 4.64), (4, 10.0), (5, 21.5), (6, 46.4), (7, 100.0), (8, 1000.0), (9, math.inf)]
_SLOPE_DEGREE = [(1, 0.57), (2, 1.43), (3, 2.66), (4, 5.71), (5, 12.13), (6, 24.89), (7, 45.0), (8, 84.29), (9, 90.0)]
_ASPECT = [(1, 22.5), (2, 67.5), (3, 112.5), (4, 157.5), (5, 202.5), (6, 247.5), (7, 292.5), (8, 337.5), (9, 360.0)]


def _breaks(table, default):
    """[(code, upper break)] from a class breaks table — its first column the breaks, its second (optional) the codes,
    names ignored, as documented — or the default classes."""
    if not _given(table):
        return default
    connection, name = _gpkg.table_of(table) if ".gpkg" in str(table).lower() else (None, None)
    if connection is None:
        raise ExecuteError(f"ERROR 000732: class_breaks_table {table} is not a table grapy reads.")
    try:
        columns = [r[1] for r in connection.execute(f'pragma table_info("{name}")') if not r[5] and r[1] != _gpkg.GEOMETRY]
        rows = connection.execute(f'select {", ".join(chr(34) + c + chr(34) for c in columns[:2])} from "{name}" '
                                  f'order by 1').fetchall()
    finally:
        connection.close()
    return [(int(r[1]) if len(r) > 1 and r[1] is not None else i, float(r[0])) for i, r in enumerate(rows, start=1)]


def _classify(values, breaks, lowest=-math.inf):
    """The code of the class whose range holds each value: ``(previous break, break]``, the first from ``lowest``
    inclusive (INFERRED: the page does not say which end of a range is inclusive); None above the last break."""
    out = []
    for v in values:
        code, previous = None, lowest
        for c, upper in breaks:
            if previous <= v <= upper if previous == lowest else previous < v <= upper:
                code = c
                break
            previous = upper
        out.append(code)
    return np.array([-999999 if c is None else c for c in out])


def SurfaceSlope(in_surface, out_feature_class, units="PERCENT", class_breaks_table=None, slope_field="SlopeCode",
                 z_factor=1, pyramid_level_resolution=0):
    """Polygons of slope classes: each triangle's slope from its surface normal, as documented, classed by the page's
    nine default classes (or a class breaks table), contiguous triangles of one class merged into one polygon.

    On a terrain, any of its pyramid levels (ADR-234); a TIN or LAS dataset has only level 0.

    INFERRED: the field is ``SlopeCode``; a slope on a break belongs to the lower class; only the data area is
    written."""
    kind = str(units or "PERCENT").upper()
    if kind not in ("PERCENT", "DEGREE"):
        _refuse("SurfaceSlope", "units", units)
    surface = surface_of(in_surface, "SurfaceSlope", pyramid_level_resolution)
    if surface is None:
        _refuse("SurfaceSlope", "in_surface", in_surface, "it is neither a TIN, a LAS dataset nor a terrain grapy holds")
    slope, _, gradient = _slopes(surface, float(z_factor if _given(z_factor) else 1))
    values = gradient * 100 if kind == "PERCENT" else np.degrees(slope)
    codes = _classify(values, _breaks(class_breaks_table, _SLOPE_PERCENT if kind == "PERCENT" else _SLOPE_DEGREE), 0.0)
    which = np.nonzero(surface.inside & (codes != -999999))[0]
    rows = _dissolved(surface, codes, which)
    return Result(_write(out_feature_class, "Polygon", surface.srid,
                         [_gpkg.Column(str(slope_field or "SlopeCode"), "Integer")], False, rows))


def SurfaceAspect(in_surface, out_feature_class, class_breaks_table=None, aspect_field="AspectCode",
                  pyramid_level_resolution=0):
    """Polygons of aspect classes — −1 flat, then 1 N (0–22.5) … 8 NW (292.5–337.5) and 9 N (337.5–360), as the page's
    default classes — from each triangle's plane, contiguous areas with the same code merged into one feature.

    INFERRED: the field is ``AspectCode``; an aspect on a break belongs to the lower class; a flat triangle is one whose
    plane has no gradient."""
    surface = surface_of(in_surface, "SurfaceAspect", pyramid_level_resolution)
    if surface is None:
        _refuse("SurfaceAspect", "in_surface", in_surface, "it is neither a TIN, a LAS dataset nor a terrain grapy holds")
    _, aspect, _ = _slopes(surface)
    codes = np.where(aspect < 0, -1, _classify(aspect, _breaks(class_breaks_table, _ASPECT), 0.0))
    which = np.nonzero(surface.inside & (codes != -999999))[0]
    rows = _dissolved(surface, codes, which)
    return Result(_write(out_feature_class, "Polygon", surface.srid,
                         [_gpkg.Column(str(aspect_field or "AspectCode"), "Integer")], False, rows))


# ---- volumes


def _clip_triangle(xyz, a, b, c, height, above):
    """The part of a triangle on one side of the plane z = height, as a 2D polygon (Sutherland–Hodgman against one
    plane)."""
    sign = 1.0 if above else -1.0
    values = [sign * (a * x + b * y + c - height) for x, y, _ in xyz]
    out = []
    for k in range(3):
        p, q = xyz[k], xyz[(k + 1) % 3]
        vp, vq = values[k], values[(k + 1) % 3]
        if vp >= 0:
            out.append((p[0], p[1]))
        if (vp >= 0) != (vq >= 0):
            s = vp / (vp - vq)
            out.append((p[0] + s * (q[0] - p[0]), p[1] + s * (q[1] - p[1])))
    return shapely.Polygon(out) if len(out) >= 3 else shapely.Polygon()


def volume(surface, height, above, region=None, z_factor=1.0):
    """(2D area, 3D area, volume) of the data area's part above (or below) the plane at ``height``, within ``region``
    when one is given. Exact for a TIN: the part of a triangle on one side of the plane is a convex polygon, and a plane's
    integral over any polygon is its area times the plane's height at the polygon's centroid."""
    a, b, c = surface.planes(z_factor)
    which = np.nonzero(surface.inside)[0]
    if region is not None:
        hits = surface.tree().query(region, predicate="intersects")
        which = np.intersect1d(which, hits)
    area2d = area3d = total = 0.0
    nodes = surface.nodes.copy()
    nodes[:, 2] *= z_factor
    for t in which:
        if not np.isfinite(a[t]):
            continue
        piece = _clip_triangle(nodes[surface.triangles[t]], a[t], b[t], c[t], height, above)
        if region is not None and not piece.is_empty:
            piece = shapely.intersection(piece, region)
        if piece.is_empty or piece.area == 0:
            continue
        centre = piece.centroid
        depth = a[t] * centre.x + b[t] * centre.y + c[t] - height
        area2d += piece.area
        area3d += piece.area * math.sqrt(1 + a[t] ** 2 + b[t] ** 2)
        total += piece.area * (depth if above else -depth)
    return area2d, area3d, total


def SurfaceVolume(in_surface, out_text_file=None, reference_plane="ABOVE", base_z=0, z_factor=1,
                  pyramid_level_resolution=0):
    """The 2D area, 3D area and volume of a TIN above or below a plane, as documented, appended to a comma-delimited text
    file when one is named; the result's messages say the same. Exact over the TIN's planes (see ``volume``).

    Refused: raster surfaces — the page says a raster "is evaluated using the extent of the center point of each cell"
    and no more, so how a cell's volume is formed is not specified (ADR-224 §5.4). A terrain is read at its pyramid
    level (ADR-234).

    INFERRED: ``base_z`` defaults to 0; the file's header is ``Dataset,Plane_Height,Reference,Z_Factor,Area_2D,Area_3D,
    Volume``, written once when the file is new; numbers are written with two decimals, as the page's example shows;
    the areas are of the part above (or below) the plane."""
    from ._runtime import AddMessage
    from . import _terrain
    from ._tin import exists
    if not exists(in_surface) and not _terrain.exists(in_surface):
        _refuse("SurfaceVolume", "in_surface", in_surface, "only a TIN or a terrain is built: a raster's cell volume is "
                "not specified on the page (ADR-229 §5.6)")
    kind = str(reference_plane or "ABOVE").upper()
    if kind not in ("ABOVE", "BELOW"):
        _refuse("SurfaceVolume", "reference_plane", reference_plane)
    surface = surface_of(in_surface, "SurfaceVolume", pyramid_level_resolution)
    zf = float(z_factor if _given(z_factor) else 1)
    plane = float(base_z if _given(base_z) else 0)
    area2d, area3d, total = volume(surface, plane, kind == "ABOVE", None, zf)
    line = f"{in_surface},{plane:.2f},{kind.title()},{zf:g},{area2d:.2f},{area3d:.2f},{total:.2f}"
    AddMessage(line)
    if _given(out_text_file):
        new = not os.path.exists(out_text_file)
        with open(out_text_file, "a", encoding="utf-8", newline="\n") as written:
            if new:
                written.write("Dataset,Plane_Height,Reference,Z_Factor,Area_2D,Area_3D,Volume\n")
            written.write(line + "\n")
    return Result(str(out_text_file) if _given(out_text_file) else "")


def PolygonVolume(in_surface, in_feature_class, in_height_field, reference_plane="BELOW", out_volume_field="Volume",
                  surface_area_field="SArea", pyramid_level_resolution=0, out_volume_above_field="Above_Vol",
                  out_volume_below_field="Below_Vol", surface_area_above_field="Above_SA",
                  surface_area_below_field="Below_SA"):
    """The volume and surface area of a TIN (or of a LAS dataset, triangulated) between each polygon's plane, at its
    ``in_height_field``, and the surface, over "all surface triangles and portions of triangles that fall within the
    intersected polygon", as documented: ``ABOVE`` the region between the plane and the underside of the surface,
    ``BELOW`` between the plane and its topside; ``BOTH`` writes the four above and below fields. The polygons are
    updated in place.

    INFERRED: ``BOTH`` leaves the Volume and SArea fields out; fields are doubles, overwritten when they exist."""
    from . import da
    kind = str(reference_plane or "BELOW").upper()
    if kind not in ("ABOVE", "BELOW", "BOTH"):
        _refuse("PolygonVolume", "reference_plane", reference_plane)
    surface = surface_of(in_surface, "PolygonVolume", pyramid_level_resolution)
    if surface is None:
        _refuse("PolygonVolume", "in_surface", in_surface, "it is neither a TIN, a LAS dataset nor a terrain grapy holds")
    fields = ([out_volume_field or "Volume", surface_area_field or "SArea"] if kind != "BOTH" else
              [out_volume_above_field or "Above_Vol", out_volume_below_field or "Below_Vol",
               surface_area_above_field or "Above_SA", surface_area_below_field or "Below_SA"])
    _ensure_fields(in_feature_class, fields)
    with da.UpdateCursor(in_feature_class, [in_height_field, *fields, "SHAPE@"]) as rows:
        for row in rows:
            if row[-1] is None or row[0] is None:
                continue
            region = shapely.force_2d(row[-1]._shape)
            plane = float(row[0])
            if kind == "BOTH":
                _, sa_up, v_up = volume(surface, plane, True, region)
                _, sa_down, v_down = volume(surface, plane, False, region)
                values = [v_up, v_down, sa_up, sa_down]
            else:
                _, area3d, total = volume(surface, plane, kind == "ABOVE", region)
                values = [total, area3d]
            rows.updateRow([row[0], *values, row[-1]])
    return Result(str(in_feature_class))


def TinPolygonVolume(in_tin, in_feature_class, in_height_field, reference_plane="BELOW", out_volume_field="Volume",
                     surface_area_field="SArea"):
    """The legacy form of PolygonVolume on a TIN, with ABOVE or BELOW; see PolygonVolume."""
    if str(reference_plane or "BELOW").upper() not in ("ABOVE", "BELOW"):
        _refuse("TinPolygonVolume", "reference_plane", reference_plane)
    if not _tin.exists(in_tin):
        raise ExecuteError(f"ERROR 000732: Input TIN: Dataset {in_tin} does not exist or is not supported.")
    return PolygonVolume(in_tin, in_feature_class, in_height_field, reference_plane, out_volume_field,
                         surface_area_field)


def _ensure_fields(path, names, kind="Double"):
    from .analysis import _schema
    columns, _, _, _ = _schema(path)
    have = {c.name.lower() for c in columns}
    for name in names:
        if name.lower() not in have:
            _gpkg.add_column(path, _gpkg.Column(name, kind))


# ---- features on a TIN


def _crossings(surface):
    """An STRtree of the data area's edges as lines, and the lines."""
    keys = [k for k, ts in surface.all_edges().items() if any(surface.inside[t] for t in ts)]
    lines = shapely.linestrings([[surface.nodes[i, :2], surface.nodes[j, :2]] for i, j in keys]) if keys else []
    return shapely.STRtree(lines), lines


def _sampled_line(xy, tree, lines):
    """A part's vertices with a vertex added where it crosses a triangle edge."""
    out = [tuple(xy[0])]
    for p, q in zip(xy[:-1], xy[1:]):
        segment = shapely.LineString([p, q])
        params = []
        for k in tree.query(segment, predicate="intersects"):
            cut = shapely.intersection(segment, lines[k])
            for point in shapely.get_coordinates(cut):
                d = np.asarray(q) - np.asarray(p)
                length = float(np.dot(d, d))
                s = float(np.dot(point - p, d) / length) if length else 0.0
                if 1e-12 < s < 1 - 1e-12:
                    params.append(s)
        for s in sorted(set(params)):
            out.append(tuple(np.asarray(p) + s * (np.asarray(q) - np.asarray(p))))
        out.append(tuple(q))
    return np.array(out)


def interpolate_shape(in_surface, in_feature_class, out_feature_class, sample_distance, z_factor, method, vertices_only,
                      preserve_features, interpolate_m_value, pyramid_level_resolution=0):
    """InterpolateShape on a TIN or LAS dataset — see ``ddd.InterpolateShape``."""
    from . import da
    from .analysis import _out, _schema
    kind = str(method or "LINEAR").upper()
    if kind == "BILINEAR":  # the raster default, given as the parameter's default: the TIN default is LINEAR
        kind = "LINEAR"
    if kind != "LINEAR":
        _refuse("InterpolateShape", "method", method, "natural neighbour and conflation over a TIN are not built "
                "(ADR-229 §5.4)")
    if str(preserve_features or "EXCLUDE").upper() != "EXCLUDE":
        _refuse("InterpolateShape", "preserve_features", preserve_features, "it applies to a raster's nearest method")
    if str(interpolate_m_value or "NO_INTERPOLATE_M_VALUE").upper() != "NO_INTERPOLATE_M_VALUE":
        _refuse("InterpolateShape", "interpolate_m_value", interpolate_m_value)
    surface = surface_of(in_surface, "InterpolateShape", pyramid_level_resolution)
    zf = float(z_factor if _given(z_factor) else 1)
    every = str(vertices_only or "DENSIFY").upper() != "VERTICES_ONLY"
    tree, lines = _crossings(surface) if every else (None, None)
    columns, shape_kind, srid, _ = _schema(in_feature_class)
    names = [c.name for c in columns]
    out = _out(out_feature_class)
    _gpkg.create(out, shape_kind, srid, columns, True, False)
    with da.SearchCursor(in_feature_class, [*names, "SHAPE@"]) as rows, \
            da.InsertCursor(out, [*names, "SHAPE@"]) as written:
        for row in rows:
            if row[-1] is None:
                continue
            shape = shapely.force_2d(row[-1]._shape)
            if every and _given(sample_distance):
                shape = shapely.segmentize(shape, float(sample_distance))
            missing = [False]

            def heights(xy):
                zs = surface.z_at(xy[:, 0], xy[:, 1], zf)
                if np.isnan(zs).any():
                    missing[0] = True
                return np.nan_to_num(zs)
            built = _resampled(shape, heights, tree, lines, every)
            if missing[0]:
                continue
            written.insertRow((*row[:-1], built))
    return Result(out)


def _resampled(shape, heights, tree, lines, every):
    """The shape rebuilt in 3D: each line part and ring sampled at its vertices, plus where it crosses a triangle edge
    when ``every``."""
    if not every or shape.geom_type in ("Point", "MultiPoint"):
        return _gpkg.rebuild(shape, True, False, None, lambda z, xy: heights(np.asarray(xy)))

    def line(coords):
        xy = _sampled_line(np.asarray(coords)[:, :2], tree, lines)
        return np.column_stack([xy, heights(xy)])

    def part(p):
        if p.geom_type == "Polygon":
            return shapely.Polygon(line(p.exterior.coords), [line(r.coords) for r in p.interiors])
        return shapely.LineString(line(p.coords))
    parts = [part(p) for p in getattr(shape, "geoms", [shape])]
    if shape.geom_type.startswith("Multi"):
        return type(shape)(parts)
    return parts[0]


# ---- AddSurfaceInformation on a TIN, terrain or LAS dataset (ADR-234)

_PROPERTIES = {"Point": ("Z",), "Multipoint": ("Z_MIN", "Z_MAX", "Z_MEAN"),
               "Polyline": ("Z_MIN", "Z_MAX", "Z_MEAN", "SURFACE_LENGTH", "MIN_SLOPE", "MAX_SLOPE", "AVG_SLOPE"),
               "Polygon": ("Z_MIN", "Z_MAX", "Z_MEAN", "SURFACE_AREA", "MIN_SLOPE", "MAX_SLOPE", "AVG_SLOPE")}


def _closest_to_zero(values):
    return float(values[int(np.argmin(np.abs(values)))])


def _line_properties(shape, surface, tree, lines, zf, step, noise):
    """Z_MIN … AVG_SLOPE of a line on the surface: sampled at its vertices, where it crosses a triangle edge and every
    ``step``; each piece then lies on one triangle, so its slope is exact."""
    zs_all, mids, lengths, slopes, slope_lengths = [], [], [], [], []
    for part in getattr(shape, "geoms", [shape]):
        coords = np.asarray(part.coords)[:, :2]
        if step:
            coords = np.asarray(shapely.segmentize(shapely.LineString(coords), step).coords)[:, :2]
        xy = _sampled_line(coords, tree, lines)
        z = surface.z_at(xy[:, 0], xy[:, 1], zf)
        for k in range(len(xy) - 1):
            if np.isnan(z[k]) or np.isnan(z[k + 1]):
                continue
            flat = float(np.hypot(*(xy[k + 1] - xy[k])))
            if flat == 0:
                continue
            dz = float(z[k + 1] - z[k])
            length = math.hypot(flat, dz)
            zs_all.extend([z[k], z[k + 1]])
            mids.append((z[k] + z[k + 1]) / 2)
            lengths.append(length)
            if noise is None or length >= noise:
                slopes.append(abs(dz) / flat * 100)
                slope_lengths.append(length)
    if not lengths:
        return None
    lengths, slopes, slope_lengths = np.array(lengths), np.array(slopes), np.array(slope_lengths)
    out = {"Z_MIN": float(np.min(zs_all)), "Z_MAX": float(np.max(zs_all)),
           "Z_MEAN": float(np.dot(mids, lengths) / lengths.sum()), "SURFACE_LENGTH": float(lengths.sum())}
    if len(slopes):
        out.update({"MIN_SLOPE": _closest_to_zero(slopes), "MAX_SLOPE": float(slopes.max()),
                    "AVG_SLOPE": float(np.dot(slopes, slope_lengths) / slope_lengths.sum())})
    return out


def _polygon_properties(shape, surface, zf, noise):
    """Z_MIN … AVG_SLOPE of the surface over a polygon: every triangle of the data area clipped to it."""
    a, b, c = surface.planes(zf)
    region = shapely.force_2d(shape)
    candidates = surface.tree().query(region, predicate="intersects")
    zs, means, areas, slopes, slope_areas = [], [], [], [], []
    polygons = surface.polygons()
    for t in candidates:
        if not surface.inside[t]:
            continue
        piece = shapely.intersection(polygons[t], region)
        if piece.area <= 0:
            continue
        grade = math.hypot(a[t], b[t])
        area3d = piece.area * math.sqrt(1 + grade ** 2)
        xy = shapely.get_coordinates(piece)
        zs.extend((a[t] * xy[:, 0] + b[t] * xy[:, 1] + c[t]).tolist())
        centre = piece.centroid
        means.append(a[t] * centre.x + b[t] * centre.y + c[t])
        areas.append(area3d)
        if noise is None or area3d >= noise:
            slopes.append(grade * 100)
            slope_areas.append(area3d)
    if not areas:
        return None
    areas, slopes, slope_areas = np.array(areas), np.array(slopes), np.array(slope_areas)
    out = {"Z_MIN": float(min(zs)), "Z_MAX": float(max(zs)), "Z_MEAN": float(np.dot(means, areas) / areas.sum()),
           "SURFACE_AREA": float(areas.sum())}
    if len(slopes):
        out.update({"MIN_SLOPE": _closest_to_zero(slopes), "MAX_SLOPE": float(slopes.max()),
                    "AVG_SLOPE": float(np.dot(slopes, slope_areas) / slope_areas.sum())})
    return out


def add_surface_information(in_feature_class, in_surface, out_property, method, sample_distance, z_factor,
                            pyramid_level_resolution, noise_filtering):
    """AddSurfaceInformation on a TIN, terrain or LAS dataset, as its page defines each property: ``Z`` at points;
    ``Z_MIN``, ``Z_MAX`` and ``Z_MEAN`` (multipoints: the mean of the points' values; lines: each piece's midpoint z
    weighted by its 3D length; polygons: each triangle's weighted by its 3D area); ``SURFACE_LENGTH`` and
    ``SURFACE_AREA`` in 3D; slopes in percent grade per line piece or triangle — ``MIN_SLOPE`` the one closest to 0,
    ``MAX_SLOPE`` the largest, ``AVG_SLOPE`` weighted by 3D length. Lines are sampled where they cross triangle edges —
    the "natural densification" the page gives as the default — and every ``sample_distance`` when given.
    ``noise_filtering`` leaves line pieces shorter, or triangles smaller, than it out of the slopes, as documented.

    Refused: methods other than ``LINEAR`` (natural neighbours and conflation, ADR-229 §5.4).

    INFERRED (ADR-234 §5.9 (f)): each property is written to a double field named after its keyword, overwritten when it
    exists; a polygon's triangle "midpoint" is its clipped piece's centroid, and its AVG_SLOPE is weighted by 3D area as
    a line's is by length; a feature off the data area gets empty values, and one partly off is measured where it is on
    it; a point's value is its z times ``z_factor``."""
    from . import da
    from .analysis import _schema
    from .management import _list
    tool = "AddSurfaceInformation"
    if str(method or "LINEAR").upper() not in ("LINEAR", "BILINEAR"):
        _refuse(tool, "method", method, "natural neighbour and conflation over a triangulated surface are not built "
                "(ADR-229 §5.4)")
    surface = surface_of(in_surface, tool, pyramid_level_resolution)
    _, kind, _, _ = _schema(in_feature_class)
    allowed = _PROPERTIES.get(kind, ())
    properties = [str(p).upper() for p in _list(out_property)]
    for p in properties:
        if p not in allowed:
            raise ExecuteError(f"ERROR 000800: {p} is not a property of {kind} features; they take "
                               f"{', '.join(allowed) or 'none'}.")
    _ensure_fields(in_feature_class, properties)
    zf = float(z_factor if _given(z_factor) else 1)
    step = float(sample_distance) if _given(sample_distance) else None
    noise = float(noise_filtering) if _given(noise_filtering) else None
    tree, lines = _crossings(surface) if kind == "Polyline" else (None, None)
    with da.UpdateCursor(in_feature_class, [*properties, "SHAPE@"]) as rows:
        for row in rows:
            values = None
            if row[-1] is not None:
                shape = shapely.force_2d(row[-1]._shape)
                if kind in ("Point", "Multipoint"):
                    xy = shapely.get_coordinates(shape)
                    zs = surface.z_at(xy[:, 0], xy[:, 1], zf)
                    zs = zs[~np.isnan(zs)]
                    if len(zs):
                        values = {"Z": float(zs[0]), "Z_MIN": float(zs.min()), "Z_MAX": float(zs.max()),
                                  "Z_MEAN": float(zs.mean())}
                elif kind == "Polyline":
                    values = _line_properties(shape, surface, tree, lines, zf, step, noise)
                else:
                    values = _polygon_properties(shape, surface, zf, noise)
            values = values or {}
            rows.updateRow([*(values.get(p) for p in properties), row[-1]])
    return Result(str(in_feature_class))
