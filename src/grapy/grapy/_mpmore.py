"""Multipatch names outside ADR-238's batch, built on its model — D-329.

ADR-238 gave grapy multipatch features; these names still refused them as *"a 3D data model grapy does not hold"*.
Each page was read on doc.esri.com on 2026-10-31 (CLAUDE.md §5: the pages, and the published formats they name):

- ``conversion.MultipatchToCollada`` — a COLLADA document per feature (Khronos COLLADA 1.4.1 and 1.5.0, ISO/PAS
  17506), and a KML placing it when the class is projected (OGC KML 2.2, 07-147r2);
- ``conversion.MultipatchToRaster`` — the highest or lowest face met by a vertical line from each cell centre;
- ``ddd.SkylineBarrier`` — a triangle fan from the observer through the skyline's vertices, closed or projected;
- ``management.Export3DObjects`` and ``Import3DObjects`` — models in COLLADA, Wavefront OBJ and glTF 2.0 (Khronos,
  ISO/IEC 12113:2022) for a 3D object feature class, which in grapy is a multipatch class (INFERRED, Q-209 (f));
- the multipatch outputs of ``ddd.ExtractObjectsFromPointCloud`` and ``PointFileInformation``, and multipatch
  features as ``LocateLasPointsByProximity``'s input — in ``_lastools`` and ``_las``, with their helpers here.

Formats refused by name, each for its own reason (``_FORMATS``): DWG and FBX (Autodesk's, unpublished), IFC (ISO
16739's geometry is implicit — extrusions, B-reps, CSG — and its tessellation is not specified), USDC and USDZ (the
crate binary is defined only by Pixar's implementation, not by a published specification) and CityGML (its geometry
sits in thematic modules at several levels of detail, and neither page says which one a model is).
"""
from __future__ import annotations

import base64
import json
import math
import os
import struct
import xml.etree.ElementTree as ET

import numpy as np
import shapely

from . import _multipatch as M
from ._result import Result
from ._runtime import AddWarning, ExecuteError, NotBuiltError
from ._runtime import _is_given, _is_set  # noqa: E402 — a Raster is never compared with ==

_DWG_FBX = "it is Autodesk's format, and Autodesk has not published it"
_FORMATS = {
    "FMT3D_DWG": _DWG_FBX, "FMT3D_FBX": _DWG_FBX,
    "FMT3D_IFC": "IFC (ISO 16739) holds a building's geometry as extrusions, boundary representations and CSG "
                 "solids, and how those are tessellated into faces is not specified, so grapy's faces could not be "
                 "read back from it nor written into it as the elements it expects",
    "FMT3D_USDC": "USD's crate binary is defined only by Pixar's implementation, not by a published specification",
    "FMT3D_USDZ": "a USDZ package holds USD crate files, whose binary is defined only by Pixar's implementation, not "
                  "by a published specification",
    "FMT3D_GML": "CityGML keeps a model's geometry in thematic modules (Building, Bridge …) at several levels of "
                 "detail, and neither page says which module and level a model is written to or read from",
}
_SUFFIX_FORMAT = {".dwg": "FMT3D_DWG", ".fbx": "FMT3D_FBX", ".ifc": "FMT3D_IFC", ".usdc": "FMT3D_USDC",
                  ".usdz": "FMT3D_USDZ", ".gml": "FMT3D_GML"}
_BUILT = {"FMT3D_DAE": ".dae", "FMT3D_OBJ": ".obj", "FMT3D_GLTF": ".gltf", "FMT3D_GLB": ".glb"}


def _refuse(module, tool, parameter, value, why):
    raise NotBuiltError(f"arcpy.{module}.{tool}: {parameter} '{value}' is not built in grapy: {why}.")


def _list(value):
    if not _is_given(value):
        return []
    if isinstance(value, (list, tuple)):
        return [str(v).strip() for v in value if str(v).strip()]
    return [v.strip().strip("'\"") for v in str(value).split(";") if v.strip()]


def _flag(value, on, default=False):
    if not _is_given(value):
        return default
    if isinstance(value, bool):
        return value
    return str(value).upper() == on


def _length(value, srid):
    from .analysis import _distance
    return float(_distance(value, srid))


# ---- triangles as indexed vertices


def _indexed(tris):
    """(vertices (n, 3), triangles (k, 3) of indexes) of triangles, coincident vertices shared."""
    tris = np.asarray(tris, float).reshape(-1, 3, 3)
    if not len(tris):
        return np.zeros((0, 3)), np.zeros((0, 3), np.int64)
    vertices, inverse = np.unique(tris.reshape(-1, 3), axis=0, return_inverse=True)
    return vertices, inverse.reshape(-1, 3)


def _origin(tris):
    """A model's local origin (INFERRED, Q-209 (f)): its x,y centre at its lowest z, so the model's own coordinates
    are small enough for single precision and its base sits at 0."""
    lo, hi = tris.reshape(-1, 3).min(axis=0), tris.reshape(-1, 3).max(axis=0)
    return np.array([(lo[0] + hi[0]) / 2, (lo[1] + hi[1]) / 2, lo[2]])


# ---- COLLADA


_DAE_NS = {"1.4": ("http://www.collada.org/2005/11/COLLADASchema", "1.4.1"),
           "1.5": ("http://www.collada.org/2008/03/COLLADASchema", "1.5.0")}


def _lonlat(point, srid):
    if not srid:
        return None
    try:
        from pyproj import Transformer
        lon, lat = Transformer.from_crs(srid, 4326, always_xy=True).transform(point[0], point[1])
        return float(lon), float(lat)
    except Exception:  # noqa: BLE001 — a reference pyproj cannot transform places nothing
        return None


def write_dae(path, tris, version="1.5", srid=0, name="model", unit=1.0):
    """A COLLADA document of triangles, z up, in metres (``unit`` metres to the class's unit): the vertices relative to
    the model's origin and one node translating them back to it, so the document holds the class's coordinates;
    COLLADA 1.5's ``geographic_location`` names the origin in WGS 84 when the class has a coordinate system."""
    ns, number = _DAE_NS[version]
    ET.register_namespace("", ns)
    q = lambda tag: f"{{{ns}}}{tag}"  # noqa: E731
    tris = np.asarray(tris, float).reshape(-1, 3, 3)
    origin = _origin(tris) if len(tris) else np.zeros(3)
    vertices, index = _indexed(tris - origin)
    root = ET.Element(q("COLLADA"), version=number)
    asset = ET.SubElement(root, q("asset"))
    contributor = ET.SubElement(asset, q("contributor"))
    ET.SubElement(contributor, q("authoring_tool")).text = "grapy (Graticula)"
    import datetime as dt
    now = dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    ET.SubElement(asset, q("created")).text = now
    ET.SubElement(asset, q("modified")).text = now
    ET.SubElement(asset, q("unit"), name="meter", meter=repr(float(unit)))
    ET.SubElement(asset, q("up_axis")).text = "Z_UP"
    place = _lonlat(origin, srid)
    if version == "1.5" and place is not None:
        location = ET.SubElement(ET.SubElement(asset, q("coverage")), q("geographic_location"))
        ET.SubElement(location, q("longitude")).text = repr(place[0])
        ET.SubElement(location, q("latitude")).text = repr(place[1])
        ET.SubElement(location, q("altitude"), mode="absolute").text = repr(float(origin[2]))
    geometries = ET.SubElement(root, q("library_geometries"))
    geometry = ET.SubElement(geometries, q("geometry"), id="mesh", name=name)
    mesh = ET.SubElement(geometry, q("mesh"))
    source = ET.SubElement(mesh, q("source"), id="mesh-positions")
    ET.SubElement(source, q("float_array"), id="mesh-positions-array", count=str(vertices.size)).text = \
        " ".join(repr(float(v)) for v in vertices.ravel())
    common = ET.SubElement(source, q("technique_common"))
    accessor = ET.SubElement(common, q("accessor"), source="#mesh-positions-array", count=str(len(vertices)),
                             stride="3")
    for axis in "XYZ":
        ET.SubElement(accessor, q("param"), name=axis, type="float")
    verts = ET.SubElement(mesh, q("vertices"), id="mesh-vertices")
    ET.SubElement(verts, q("input"), semantic="POSITION", source="#mesh-positions")
    triangles = ET.SubElement(mesh, q("triangles"), count=str(len(index)))
    ET.SubElement(triangles, q("input"), semantic="VERTEX", source="#mesh-vertices", offset="0")
    ET.SubElement(triangles, q("p")).text = " ".join(str(int(i)) for i in index.ravel())
    scenes = ET.SubElement(root, q("library_visual_scenes"))
    scene = ET.SubElement(scenes, q("visual_scene"), id="scene")
    node = ET.SubElement(scene, q("node"), id="node", name=name)
    ET.SubElement(node, q("translate")).text = " ".join(repr(float(v)) for v in origin)
    ET.SubElement(node, q("instance_geometry"), url="#mesh")
    ET.SubElement(ET.SubElement(root, q("scene")), q("instance_visual_scene"), url="#scene")
    ET.ElementTree(root).write(path, encoding="utf-8", xml_declaration=True)
    return path, origin


def _write_kml(path, dae_name, origin, srid, name):
    """A KML 2.2 placemark whose Model links the COLLADA file at its origin's WGS 84 position (OGC 07-147r2 §10.9)."""
    place = _lonlat(origin, srid)
    if place is None:
        return None
    kml = ET.Element("kml", xmlns="http://www.opengis.net/kml/2.2")
    placemark = ET.SubElement(ET.SubElement(kml, "Document"), "Placemark")
    ET.SubElement(placemark, "name").text = name
    model = ET.SubElement(placemark, "Model")
    ET.SubElement(model, "altitudeMode").text = "absolute"
    location = ET.SubElement(model, "Location")
    ET.SubElement(location, "longitude").text = repr(place[0])
    ET.SubElement(location, "latitude").text = repr(place[1])
    ET.SubElement(location, "altitude").text = repr(float(origin[2]))
    ET.SubElement(ET.SubElement(model, "Link"), "href").text = dae_name
    ET.ElementTree(kml).write(path, encoding="utf-8", xml_declaration=True)
    return path


def _safe(name):
    text = "".join(c if c.isalnum() or c in "-_." else "_" for c in str(name)).strip(".")
    return text or "model"


def _source_name(path):
    return os.path.basename(str(path).rstrip("/\\")).split(".gpkg")[-1].strip("/\\") or "features"


def _features(in_features, tool, module):
    M.require(in_features, module, tool)
    columns, srid, rows = M.read(in_features)
    return columns, srid, [(oid, values, shape) for oid, values, shape in rows if shape is not None]


def _metres(srid):
    """Metres per unit of the class's linear unit (1 for an unknown or geographic system)."""
    if not srid:
        return 1.0
    try:
        from pyproj import CRS
        crs = CRS.from_epsg(srid)
        if crs.is_projected:
            return float(crs.axis_info[0].unit_conversion_factor)
    except Exception:  # noqa: BLE001
        pass
    return 1.0


def MultipatchToCollada(in_features, output_folder, prepend_source="PREPEND_NONE", field_name=None,
                        collada_version="1.5"):
    """One COLLADA file per multipatch feature in the output folder, named by ``field_name``'s value (the object ID by
    default), with the source class's name before it under PREPEND_SOURCE_NAME, as documented; a file of the same name
    is replaced, with a warning listing them. When the class is projected, a KML file placing each model in WGS 84 is
    written beside it, as documented (no datum transformation beyond pyproj's default).

    Not written: textures and colours — grapy's multipatch holds none (ADR-238 §5.2 (a)). INFERRED (Q-209 (f)): the
    model's vertices are relative to its x,y centre at its lowest z, one node translating them back; COLLADA 1.5
    names that origin in ``geographic_location``; a source name is joined to the field's value by ``_``; the KML file
    has the COLLADA file's name with ``.kml``."""
    tool = "MultipatchToCollada"
    version = str(collada_version or "1.5").strip()
    version = "1.4" if version.startswith("1.4") else "1.5" if version.startswith("1.5") else None
    if version is None:
        raise ExecuteError(f"ERROR 000800: arcpy.conversion.{tool}: collada_version is 1.5 or 1.4, not "
                           f"'{collada_version}'.")
    columns, srid, rows = _features(in_features, tool, "conversion")
    os.makedirs(output_folder, exist_ok=True)
    prefix = _source_name(in_features) + "_" if _flag(prepend_source, "PREPEND_SOURCE_NAME") else ""
    field = None
    if _is_given(field_name):
        field = next((c.name for c in columns if c.name.lower() == str(field_name).lower()), None)
        if field is None and str(field_name).upper() not in ("OBJECTID", "OID", "FID", "OID@"):
            raise ExecuteError(f"ERROR 000728: the field '{field_name}' does not exist in '{in_features}'.")
    projected = False
    if srid:
        try:
            from pyproj import CRS
            projected = CRS.from_epsg(srid).is_projected
        except Exception:  # noqa: BLE001
            projected = False
    unit = _metres(srid)
    replaced, written = [], []
    for oid, values, shape in rows:
        tris = M.triangles(shape)
        if not len(tris):
            AddWarning(f"{tool}: feature {oid} has no face and is not exported.")
            continue
        name = prefix + _safe(values[field] if field else oid)
        path = os.path.join(output_folder, name + ".dae")
        if os.path.exists(path):
            replaced.append(os.path.basename(path))
        _, origin = write_dae(path, tris, version, srid, name, unit)
        written.append(path)
        if projected:
            _write_kml(os.path.join(output_folder, name + ".kml"), os.path.basename(path), origin, srid, name)
    if replaced:
        AddWarning(f"{tool}: files replaced: {', '.join(replaced)}.")
    return Result(output_folder)


# ---- MultipatchToRaster


def _cell(cell_size, tool):
    from .sa import _cell_size
    return _cell_size(cell_size, tool)


def _heights(tris, xs, ys):
    """Per cell centre, the highest and lowest z of the faces a vertical line through it meets: (max, min), NaN where
    it meets none. Vertical faces are left out, as documented."""
    gx, gy = np.meshgrid(xs, ys)
    high = np.full(gx.shape, -np.inf)
    low = np.full(gx.shape, np.inf)
    width = abs(xs[1] - xs[0]) if len(xs) > 1 else 1.0
    height = abs(ys[1] - ys[0]) if len(ys) > 1 else 1.0
    for t in tris:
        a, b, c = t
        det = (b[1] - c[1]) * (a[0] - c[0]) + (c[0] - b[0]) * (a[1] - c[1])
        if abs(det) <= 1e-12 * max(1.0, np.ptp(t[:, :2]) ** 2):
            continue
        x0, x1 = t[:, 0].min(), t[:, 0].max()
        y0, y1 = t[:, 1].min(), t[:, 1].max()
        c0 = max(int(np.floor((x0 - xs[0]) / width)), 0)
        c1 = min(int(np.ceil((x1 - xs[0]) / width)) + 1, len(xs))
        r0 = max(int(np.floor((ys[0] - y1) / height)), 0)
        r1 = min(int(np.ceil((ys[0] - y0) / height)) + 1, len(ys))
        if c0 >= c1 or r0 >= r1:
            continue
        px, py = gx[r0:r1, c0:c1], gy[r0:r1, c0:c1]
        l1 = ((b[1] - c[1]) * (px - c[0]) + (c[0] - b[0]) * (py - c[1])) / det
        l2 = ((c[1] - a[1]) * (px - c[0]) + (a[0] - c[0]) * (py - c[1])) / det
        l3 = 1 - l1 - l2
        eps = 1e-12
        inside = (l1 >= -eps) & (l2 >= -eps) & (l3 >= -eps)
        z = l1 * a[2] + l2 * b[2] + l3 * c[2]
        high[r0:r1, c0:c1] = np.where(inside, np.maximum(high[r0:r1, c0:c1], z), high[r0:r1, c0:c1])
        low[r0:r1, c0:c1] = np.where(inside, np.minimum(low[r0:r1, c0:c1], z), low[r0:r1, c0:c1])
    return np.where(np.isfinite(high), high, np.nan), np.where(np.isfinite(low), low, np.nan)


def MultipatchToRaster(in_multipatch_features, out_raster, cell_size=None, cell_assignment_method="MAXIMUM_HEIGHT"):
    """The multipatch features' heights as a floating-point raster, as documented: a vertical line from each cell
    centre meets the features' faces, and the cell takes the highest z it meets (MAXIMUM_HEIGHT, the default) or the
    lowest (MINIMUM_HEIGHT); a cell whose centre is outside every footprint is NoData; vertical faces are left out.
    The cell size is the parameter's, else the Cell Size environment's, else the Snap Raster's, else the extent's
    shorter side divided by 250, as documented.

    INFERRED (Q-209 (g)): the grid's top-left corner is the features' extent's, or the snap raster's cell corner at or
    beyond it; a centre on a face's edge meets it."""
    from ._raster import Raster, _Grid, as_raster
    from ._runtime import env
    tool = "MultipatchToRaster"
    method = str(cell_assignment_method or "MAXIMUM_HEIGHT").upper()
    if method not in ("MAXIMUM_HEIGHT", "MINIMUM_HEIGHT"):
        raise ExecuteError(f"ERROR 000800: arcpy.conversion.{tool}: cell_assignment_method is MAXIMUM_HEIGHT or "
                           f"MINIMUM_HEIGHT, not '{cell_assignment_method}'.")
    _, srid, rows = _features(in_multipatch_features, tool, "conversion")
    tris = [M.triangles(s) for _, _, s in rows]
    tris = np.concatenate([t for t in tris if len(t)]) if any(len(t) for t in tris) else np.zeros((0, 3, 3))
    if not len(tris):
        raise ExecuteError(f"ERROR: arcpy.conversion.{tool}: the features have no faces.")
    left, bottom = tris[:, :, 0].min(), tris[:, :, 1].min()
    right, top = tris[:, :, 0].max(), tris[:, :, 1].max()
    snap = as_raster(env.snapRaster, f"arcpy.conversion.{tool}") if getattr(env, "snapRaster", None) else None
    size = _cell(cell_size, f"arcpy.conversion.{tool}")
    if size is None and _is_set(getattr(env, "cellSize", None), "MAXOF", "MINOF"):
        size = _cell(env.cellSize, f"arcpy.conversion.{tool}")
    if size is None and snap is not None:
        size = float(snap._grid.width)
    if size is None:
        size = min(right - left, top - bottom) / 250.0
    if not size or size <= 0:
        raise ExecuteError(f"ERROR: arcpy.conversion.{tool}: the features' extent has no area to derive a cell size "
                           "from; give one.")
    if snap is not None:
        g = snap._grid
        left = g.left + math.floor((left - g.left) / size + 1e-9) * size
        top = g.top - math.floor((g.top - top) / size + 1e-9) * size
    columns = max(1, int(math.ceil((right - left) / size - 1e-9)))
    count = max(1, int(math.ceil((top - bottom) / size - 1e-9)))
    grid = _Grid(left, top, size, size, count, columns, srid)
    xs, ys = grid.centres()
    high, low = _heights(tris, xs, ys)
    values = high if method == "MAXIMUM_HEIGHT" else low
    mask = ~np.isfinite(values)
    raster = Raster._of(np.where(mask, 0.0, values).astype(np.float32), mask, grid)
    return Result(raster.save(out_raster))


# ---- SkylineBarrier


def _fan(observer, vertices, near, far):
    """The fan's near and far ends along each ray from the observer through a skyline vertex: the far end at the
    vertex, moved out to ``near`` or in to ``far`` when it is nearer or farther (0 for no bound); the near end at the
    observer, or ``near`` along the ray."""
    rays = vertices - observer
    length = np.linalg.norm(rays, axis=1)
    unit = rays / np.where(length > 0, length, 1)[:, None]
    reach = length.copy()
    if near > 0:
        reach = np.maximum(reach, near)
    if far > 0:
        reach = np.minimum(reach, far)
    start = observer + unit * near if near > 0 else np.repeat(observer[None], len(vertices), axis=0)
    return start, observer + unit * reach[:, None]


def _planes(observer, vertices, near, far):
    """PROJECT_TO_PLANE: each ray's ends where it meets two vertical planes square to the skyline's mean horizontal
    direction from the observer, at horizontal distances ``near`` (else the observer's) and ``far`` (else the farthest
    skyline vertex's) along it (INFERRED, Q-209 (h))."""
    rays = vertices - observer
    flat = rays[:, :2]
    direction = flat.sum(axis=0)
    if not np.any(direction):
        raise ExecuteError("ERROR: SkylineBarrier: the skyline surrounds the observer, so its ends have no plane to be "
                           "projected onto.")
    direction /= np.linalg.norm(direction)
    along = flat @ direction
    if np.any(along <= 0):
        raise ExecuteError("ERROR: SkylineBarrier: a skyline vertex is behind the observer's plane; PROJECT_TO_PLANE "
                           "needs the skyline in front of it.")
    far_d = far if far > 0 else float(along.max())
    start = observer + rays * (near / along)[:, None] if near > 0 else np.repeat(observer[None], len(vertices), axis=0)
    return start, observer + rays * (far_d / along)[:, None]


def _barrier(observer, vertices, near, far, closed, base, project):
    """The barrier's triangles (k, 3, 3)."""
    vertices = np.asarray(vertices, float)
    keep = np.concatenate([[True], np.linalg.norm(np.diff(vertices, axis=0), axis=1) > 0])
    vertices = vertices[keep]
    if len(vertices) < 2:
        return np.zeros((0, 3, 3))
    start, end = (_planes if project else _fan)(observer, vertices, near, far)
    apex = not project and near <= 0
    plan = shapely.Polygon(([tuple(observer[:2])] if apex else [tuple(p[:2]) for p in start[::-1]])
                           + [tuple(p[:2]) for p in end])
    if plan.is_valid and plan.exterior.is_ccw is False:
        start, end = start[::-1], end[::-1]
    tris = []
    for i in range(len(end) - 1):
        if apex:
            tris.append([observer, end[i], end[i + 1]])
        else:
            tris.append([start[i], end[i], end[i + 1]])
            tris.append([start[i], end[i + 1], start[i + 1]])
    tris = np.array(tris, float)
    if not closed:
        return tris
    if apex:
        boundary = [observer, *end]
    else:
        boundary = [*start[::-1], *end]
    boundary = np.array(boundary, float)
    under = boundary.copy()
    under[:, 2] = base
    walls = []
    n = len(boundary)
    for i in range(n):
        a, b = boundary[i], boundary[(i + 1) % n]
        a0, b0 = under[i], under[(i + 1) % n]
        walls.append([a, a0, b0])
        walls.append([a, b0, b])
    floor = []
    ring = shapely.Polygon(under[:, :2])
    for t in getattr(shapely.constrained_delaunay_triangles(ring if ring.is_valid else shapely.make_valid(ring)),
                     "geoms", []):
        xy = list(t.exterior.coords)[:3]
        if shapely.Polygon(xy).exterior.is_ccw:
            xy = xy[::-1]
        floor.append([(x, y, base) for x, y in xy])
    solid = np.concatenate([tris, np.array(walls, float), np.array(floor, float).reshape(-1, 3, 3)])
    if M.signed_volume(solid) < 0:
        solid = solid[:, [0, 2, 1]]
    return solid


def SkylineBarrier(in_observer_point_features, in_features, out_feature_class, min_radius_value_or_field=None,
                   max_radius_value_or_field=None, closed="NO_CLOSED", base_elevation=None,
                   project_to_plane="NO_PROJECT_TO_PLANE"):
    """A multipatch barrier per skyline line and observer, as documented: a triangle fan from the observer point to
    the skyline's first vertex, swept through all its vertices; ``min_radius_value_or_field`` and
    ``max_radius_value_or_field`` (a distance or a field of the lines, 0 for none) bound the length of the triangles'
    edges from the observer; CLOSED adds a skirt down — or up, when it is higher — to ``base_elevation`` and a
    horizontal base; PROJECT_TO_PLANE makes the barrier run from one vertical plane to another. Fields ``OBSV_PT_ID``
    and ``ORIGFTR_ID``, as documented.

    Refused by value: silhouettes (multipatch input) — its page says a silhouette makes the volume of the shadow cast
    from the observer but not which of the silhouette's edges bound it, and the Skyline tool that writes them is
    refused. INFERRED (Q-209 (h)): a line with an ``OBSV_PT_ID`` field pairs with that observer, any other with every
    observer; ``ORIGFTR_ID`` is the line's own field of that name when it has one, else null; the near end of the
    edges is ``min_radius`` along each ray (the fan's apex at the observer when 0), and a far end nearer than it is
    moved out to it; the planes of PROJECT_TO_PLANE are square to the skyline's mean direction from the observer."""
    from . import _gpkg
    from .analysis import _schema
    tool = "SkylineBarrier"
    columns, kind, srid, _ = _schema(in_features)
    if kind == "MultiPatch":
        _refuse("ddd", tool, "in_features", in_features, "silhouettes make the volume of the shadow cast from the "
                "observer, and the page does not say which of a silhouette's edges bound it; the Skyline tool that "
                "writes them is refused")
    if kind != "Polyline":
        raise ExecuteError(f"ERROR 000800: arcpy.ddd.{tool}: in_features are skyline lines or silhouettes.")
    names = {c.name.lower(): c.name for c in columns}
    _, observers_srid, observers = M.read(in_observer_point_features)
    obs = []
    for oid, _, shape in observers:
        if shape is None:
            continue
        xyz = shapely.get_coordinates(shape, include_z=True)[0]
        obs.append((oid, np.array([xyz[0], xyz[1], 0.0 if np.isnan(xyz[2]) else xyz[2]])))
    if not obs:
        raise ExecuteError(f"ERROR: arcpy.ddd.{tool}: there is no observer point.")

    def amount(value, values):
        if not _is_given(value):
            return 0.0
        if isinstance(value, str) and value.lower() in names:
            v = values[names[value.lower()]]
            return float(v) if v is not None else 0.0
        return _length(value, srid)

    is_closed = _flag(closed, "CLOSED")
    project = _flag(project_to_plane, "PROJECT_TO_PLANE")
    _, _, lines = M.read(in_features)
    out = []
    for oid, values, shape in lines:
        if shape is None:
            continue
        if not shapely.has_z(shape):
            raise ExecuteError(f"ERROR: arcpy.ddd.{tool}: skyline lines need z values.")
        near, far = amount(min_radius_value_or_field, values), amount(max_radius_value_or_field, values)
        if near < 0 or far < 0 or (far > 0 and near > far):
            raise ExecuteError(f"ERROR: arcpy.ddd.{tool}: the radii must be 0 or more, the minimum not above the "
                               "maximum.")
        base = amount(base_elevation, values)
        paired = values.get(names["obsv_pt_id"]) if "obsv_pt_id" in names else None
        original = values.get(names["origftr_id"]) if "origftr_id" in names else None
        for observer_oid, observer in obs:
            if paired is not None and int(paired) != int(observer_oid):
                continue
            parts = []
            for part in shapely.get_parts(shape):
                t = _barrier(observer, shapely.get_coordinates(part, include_z=True), near, far, is_closed, base,
                             project)
                if len(t):
                    parts.append(t)
            if parts:
                out.append(([int(observer_oid), None if original is None else int(original)],
                            M.from_triangles(np.concatenate(parts))))
    path = M.write(out_feature_class, srid or observers_srid,
                   [_gpkg.Column("OBSV_PT_ID", "Integer"), _gpkg.Column("ORIGFTR_ID", "Integer")], out)
    return Result(path)


# ---- glTF 2.0


_GLTF_TYPES = {5120: np.int8, 5121: np.uint8, 5122: np.int16, 5123: np.uint16, 5125: np.uint32, 5126: np.float32}
_SIZES = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT4": 16}
_Y_UP = np.array([[1, 0, 0], [0, 0, -1], [0, 1, 0]], float)  # a y-up model's (x, y, z) is (x, −z, y) with z up


def _gltf_document(tris_by_node, binary):
    """(JSON dict, binary buffer) of meshes, each placed by its node's translation (glTF's y up, metres)."""
    blob = bytearray()
    accessors, views, meshes, nodes = [], [], [], []
    for name, tris, origin in tris_by_node:
        vertices, index = _indexed(tris - origin)
        vertices = (vertices @ np.linalg.inv(_Y_UP).T).astype(np.float32)  # z up → y up
        for array, target, kind, component in ((vertices, 34962, "VEC3", 5126),
                                               (index.astype(np.uint32), 34963, "SCALAR", 5125)):
            while len(blob) % 4:
                blob.append(0)
            data = np.ascontiguousarray(array).tobytes()
            views.append({"buffer": 0, "byteOffset": len(blob), "byteLength": len(data), "target": target})
            blob.extend(data)
            accessor = {"bufferView": len(views) - 1, "componentType": component,
                        "count": int(len(array) if kind == "VEC3" else array.size), "type": kind}
            if kind == "VEC3":
                accessor["min"] = [float(v) for v in array.min(axis=0)]
                accessor["max"] = [float(v) for v in array.max(axis=0)]
            accessors.append(accessor)
        meshes.append({"name": str(name), "primitives": [{"attributes": {"POSITION": len(accessors) - 2},
                                                           "indices": len(accessors) - 1, "mode": 4}]})
        nodes.append({"name": str(name), "mesh": len(meshes) - 1,
                      "translation": [float(v) for v in (_Y_UP.T @ origin)]})
    while len(blob) % 4:
        blob.append(0)
    document = {"asset": {"version": "2.0", "generator": "grapy (Graticula)"}, "scene": 0,
                "scenes": [{"nodes": list(range(len(nodes)))}], "nodes": nodes, "meshes": meshes,
                "accessors": accessors, "bufferViews": views, "buffers": [{"byteLength": len(blob)}]}
    if not binary:
        document["buffers"][0]["uri"] = "data:application/octet-stream;base64," + base64.b64encode(bytes(blob)).decode()
    return document, bytes(blob)


def write_gltf(path, tris_by_node, binary):
    """A glTF 2.0 file: JSON with its buffer embedded as a data URI (.gltf), or the binary container (.glb)."""
    document, blob = _gltf_document(tris_by_node, binary)
    if not binary:
        with open(path, "w", encoding="utf-8") as f:
            json.dump(document, f)
        return path
    text = json.dumps(document).encode("utf-8")
    text += b" " * (-len(text) % 4)
    total = 12 + 8 + len(text) + 8 + len(blob)
    with open(path, "wb") as f:
        f.write(struct.pack("<4sII", b"glTF", 2, total))
        f.write(struct.pack("<I4s", len(text), b"JSON") + text)
        f.write(struct.pack("<I4s", len(blob), b"BIN\x00") + blob)
    return path


def _quaternion(q):
    x, y, z, w = q
    return np.array([[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
                     [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
                     [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]])


def _node_matrix(node):
    if "matrix" in node:
        return np.array(node["matrix"], float).reshape(4, 4).T  # column-major
    m = np.eye(4)
    s = np.diag(node.get("scale", [1, 1, 1]))
    r = _quaternion(node.get("rotation", [0, 0, 0, 1]))
    m[:3, :3] = r @ s
    m[:3, 3] = node.get("translation", [0, 0, 0])
    return m


def read_gltf(path):
    """The triangles of a glTF 2.0 file (.gltf or .glb), in metres and z up, every node's transform applied."""
    with open(path, "rb") as f:
        raw = f.read()
    chunk = None
    if raw[:4] == b"glTF":
        _, version, _ = struct.unpack_from("<4sII", raw, 0)
        if version != 2:
            raise ExecuteError(f"ERROR: '{os.path.basename(path)}' is glTF {version}; glTF 2.0 is read.")
        at = 12
        document = None
        while at < len(raw):
            length, kind = struct.unpack_from("<I4s", raw, at)
            body = raw[at + 8:at + 8 + length]
            if kind == b"JSON":
                document = json.loads(body.decode("utf-8"))
            elif kind == b"BIN\x00":
                chunk = body
            at += 8 + length
    else:
        document = json.loads(raw.decode("utf-8"))
    if not str(document.get("asset", {}).get("version", "2.0")).startswith("2"):
        raise ExecuteError(f"ERROR: '{os.path.basename(path)}' is not glTF 2.0.")
    buffers = []
    for k, b in enumerate(document.get("buffers", [])):
        uri = b.get("uri")
        if uri is None:
            buffers.append(chunk or b"")
        elif uri.startswith("data:"):
            buffers.append(base64.b64decode(uri.split(",", 1)[1]))
        else:
            from urllib.parse import unquote
            with open(os.path.join(os.path.dirname(path), unquote(uri)), "rb") as f:
                buffers.append(f.read())

    def accessor(i):
        a = document["accessors"][i]
        if "sparse" in a:
            raise NotBuiltError(f"glTF: '{os.path.basename(path)}' holds a sparse accessor, which is not built in "
                                "grapy: grapy reads the geometry of plain accessors only.")
        view = document["bufferViews"][a["bufferView"]]
        dtype = np.dtype(_GLTF_TYPES[a["componentType"]])
        width = _SIZES[a["type"]]
        stride = view.get("byteStride") or dtype.itemsize * width
        start = view.get("byteOffset", 0) + a.get("byteOffset", 0)
        data = buffers[view["buffer"]]
        out = np.empty((a["count"], width), dtype)
        for r in range(a["count"]):
            out[r] = np.frombuffer(data, dtype, width, start + r * stride)
        return out.astype(np.float64 if dtype.kind == "f" else np.int64)

    tris = []

    def place(mesh, matrix):
        for p in document["meshes"][mesh].get("primitives", []):
            mode = p.get("mode", 4)
            if mode not in (4, 5, 6):
                continue
            pos = accessor(p["attributes"]["POSITION"])
            pos = (np.c_[pos, np.ones(len(pos))] @ matrix.T)[:, :3]
            idx = accessor(p["indices"]).ravel() if "indices" in p else np.arange(len(pos))
            if mode == 4:
                faces = idx[:len(idx) // 3 * 3].reshape(-1, 3)
            elif mode == 5:
                faces = np.array([[idx[i], idx[i + 1], idx[i + 2]] if i % 2 == 0 else [idx[i + 1], idx[i], idx[i + 2]]
                                  for i in range(len(idx) - 2)]).reshape(-1, 3)
            else:
                faces = np.array([[idx[0], idx[i], idx[i + 1]] for i in range(1, len(idx) - 1)]).reshape(-1, 3)
            if len(faces):
                tris.append(pos[faces])

    def walk(i, parent):
        node = document["nodes"][i]
        matrix = parent @ _node_matrix(node)
        if "mesh" in node:
            place(node["mesh"], matrix)
        for child in node.get("children", []):
            walk(child, matrix)

    scenes = document.get("scenes")
    if scenes:
        for i in scenes[document.get("scene", 0)].get("nodes", []):
            walk(i, np.eye(4))
    else:
        for m in range(len(document.get("meshes", []))):
            place(m, np.eye(4))
    out = np.concatenate(tris) if tris else np.zeros((0, 3, 3))
    return out @ _Y_UP.T


# ---- Wavefront OBJ


def write_obj(path, tris_by_node):
    """A Wavefront OBJ file, an object (``o``) per model, its vertices in the class's coordinates written y up —
    (x, z, −y) — as Import3DObjects reads an OBJ by default (Y_IS_UP), so a round trip keeps the model where it was
    (INFERRED, Q-209 (f))."""
    with open(path, "w", encoding="utf-8") as f:
        f.write("# grapy (Graticula)\n")
        base = 1
        for name, tris, _ in tris_by_node:
            vertices, index = _indexed(tris)
            f.write(f"o {_safe(name)}\n")
            for x, y, z in vertices.tolist():
                f.write(f"v {x!r} {z!r} {-y!r}\n")
            for a, b, c in index + base:
                f.write(f"f {a} {b} {c}\n")
            base += len(vertices)
    return path


# ---- Export3DObjects and Import3DObjects


def _formats(value, tool):
    chosen = [f.upper() for f in _list(value)]
    if not chosen:
        raise ExecuteError(f"ERROR 000735: arcpy.management.{tool}: formats is required.")
    for f in chosen:
        if f in _FORMATS:
            _refuse("management", tool, "formats", f, _FORMATS[f])
        if f not in _BUILT:
            raise ExecuteError(f"ERROR 000800: arcpy.management.{tool}: '{f}' is not a 3D format keyword.")
    return chosen


def _write_model(path, fmt, models, srid, unit):
    if fmt == "FMT3D_DAE":
        tris = np.concatenate([t for _, t, _ in models])
        return write_dae(path, tris, "1.5", srid, models[0][0] if len(models) == 1 else "models", unit)[0]
    if fmt == "FMT3D_OBJ":
        return write_obj(path, models)
    return write_gltf(path, models, fmt == "FMT3D_GLB")


def Export3DObjects(in_features, target_folder, formats, name_field=None, overwrite="NO_OVERWRITE", merge="NO_MERGE"):
    """Each feature of a 3D object feature class (in grapy, a multipatch class, INFERRED) written in each format
    chosen, in a folder of its own named by ``name_field``'s value, else its object ID, in the existing target folder,
    as documented; NO_OVERWRITE, the default, keeps a model already there, OVERWRITE replaces it; MERGE writes every
    feature into one file per format.

    Built: FMT3D_DAE (COLLADA 1.5), FMT3D_OBJ (Wavefront), FMT3D_GLTF and FMT3D_GLB (glTF 2.0). Refused by name: the
    others (module docstring). INFERRED (Q-209 (f)): a file is named as its folder (or, merged, after the class);
    COLLADA and glTF keep each model's vertices relative to its origin with a node placing it, OBJ its coordinates y
    up; materials and textures are not written, grapy's multipatch holding none."""
    tool = "Export3DObjects"
    chosen = _formats(formats, tool)
    if not os.path.isdir(target_folder):
        raise ExecuteError(f"ERROR 000732: arcpy.management.{tool}: the target folder '{target_folder}' does not exist.")
    columns, srid, rows = _features(in_features, tool, "management")
    field = None
    if _is_given(name_field):
        field = next((c.name for c in columns if c.name.lower() == str(name_field).lower()), None)
        if field is None:
            raise ExecuteError(f"ERROR 000728: the field '{name_field}' does not exist in '{in_features}'.")
    replace = _flag(overwrite, "OVERWRITE")
    unit = _metres(srid)
    models = []
    for oid, values, shape in rows:
        tris = M.triangles(shape)
        if len(tris):
            models.append((_safe(values[field] if field else oid), tris, _origin(tris)))
    groups = [(_safe(_source_name(in_features)), models)] if _flag(merge, "MERGE") else [(m[0], [m]) for m in models]
    for name, group in groups:
        if not group:
            continue
        folder = target_folder if _flag(merge, "MERGE") else os.path.join(target_folder, name)
        os.makedirs(folder, exist_ok=True)
        for fmt in chosen:
            path = os.path.join(folder, name + _BUILT[fmt])
            if os.path.exists(path) and not replace:
                AddWarning(f"{tool}: '{path}' exists and is kept (NO_OVERWRITE).")
                continue
            _write_model(path, fmt, group, srid, unit)
    return Result(target_folder)


_READ = (".dae", ".obj", ".gltf", ".glb")


def _model_files(value):
    out = []
    for item in _list(value):
        if os.path.isdir(item):
            for root, _, files in os.walk(item):
                for name in sorted(files):
                    ext = os.path.splitext(name)[1].lower()
                    if ext in _READ or ext in _SUFFIX_FORMAT:
                        out.append(os.path.join(root, name))
        else:
            out.append(item)
    return out


def _read_model(path, y_up):
    from . import _import3d
    ext = os.path.splitext(path)[1].lower()
    if ext in _SUFFIX_FORMAT:
        _refuse("management", "Import3DObjects", "files_and_folders", os.path.basename(path), _FORMATS[_SUFFIX_FORMAT[ext]])
    if ext == ".dae":
        faces = _import3d.read_dae(path)[0][1]
        return np.concatenate([M.face_triangles(f) for f in faces]) if faces else np.zeros((0, 3, 3))
    if ext == ".obj":
        faces = _import3d.read_obj(path)[0][1]
        tris = np.concatenate([M.face_triangles(f) for f in faces]) if faces else np.zeros((0, 3, 3))
        return tris @ _Y_UP.T if y_up else tris
    if ext in (".gltf", ".glb"):
        return read_gltf(path)
    raise ExecuteError(f"ERROR: '{os.path.basename(path)}' is not a 3D model file Import3DObjects reads.")


def _point(value):
    if not _is_given(value):
        return 0.0, 0.0
    if hasattr(value, "X"):
        return float(value.X), float(value.Y)
    parts = str(value).replace(",", " ").split()
    return float(parts[0]), float(parts[1])


def Import3DObjects(files_and_folders, updated_features, update="UPDATE_EXISTING_ADD_NEW", translate=None,
                    elevation=None, scale=None, rotate=None, y_is_up="Y_IS_UP"):
    """3D models — files, or every model in folders and their subfolders — into a 3D object feature class (in grapy a
    multipatch class, INFERRED), created or updated, as documented: REPLACE_ALL removes the features first,
    UPDATE_EXISTING replaces the models whose file name matches a feature's and skips the others, UPDATE_EXISTING_ADD_NEW
    (the default) also adds them, ADD_ALL adds every model; each model is scaled by ``scale``, turned by ``rotate``
    degrees clockwise from north, moved by ``translate`` in x,y and by ``elevation`` in z; Y_IS_UP reads an OBJ's y as
    height. A new class takes WGS 1984 Web Mercator (EPSG 3857), as the page says of a model without a coordinate
    system.

    Built: COLLADA, OBJ and glTF 2.0 (.gltf, .glb). Refused by name: DWG, FBX, IFC, USDC, USDZ and CityGML files
    (module docstring). INFERRED (Q-209 (f)): a model's name is its file name without the extension, kept in ``Name``
    as Import3DFiles keeps it; scale, then rotation about the model's own origin, then the offsets; an existing class's
    coordinate system is the models'."""
    from . import _gpkg, da
    tool = "Import3DObjects"
    strategy = str(update or "UPDATE_EXISTING_ADD_NEW").upper()
    if strategy not in ("REPLACE_ALL", "UPDATE_EXISTING", "UPDATE_EXISTING_ADD_NEW", "ADD_ALL"):
        raise ExecuteError(f"ERROR 000800: arcpy.management.{tool}: update '{update}' is not an update strategy.")
    paths = _model_files(files_and_folders)
    if not paths:
        raise ExecuteError(f"ERROR 000735: arcpy.management.{tool} found no 3D model to import.")
    y_up = str(y_is_up or "Y_IS_UP").upper() != "Z_IS_UP"
    dx, dy = _point(translate)
    dz = float(elevation) if _is_given(elevation) else 0.0
    k = float(scale) if _is_given(scale) else 1.0
    angle = math.radians(float(rotate)) if _is_given(rotate) else 0.0
    turn = np.array([[math.cos(angle), math.sin(angle), 0], [-math.sin(angle), math.cos(angle), 0], [0, 0, 1]])
    models = []
    for path in paths:
        tris = _read_model(path, y_up)
        if not len(tris):
            AddWarning(f"{tool}: '{os.path.basename(path)}' holds no face and is skipped.")
            continue
        tris = (tris * k) @ turn.T + np.array([dx, dy, dz])
        models.append((os.path.splitext(os.path.basename(path))[0], M.from_triangles(tris)))
    if not M.is_multipatch(updated_features):
        path = M.write(updated_features, 3857, [_gpkg.Column("Name", "String")], [([n], s) for n, s in models])
        return Result(path)
    columns = M.read(updated_features)[0]
    if not any(c.name.lower() == "name" for c in columns):
        raise ExecuteError(f"ERROR: arcpy.management.{tool}: '{updated_features}' has no Name field to match the "
                           "models' file names with.")
    if strategy == "REPLACE_ALL":
        with da.UpdateCursor(updated_features, ["OID@"]) as rows:
            for _ in rows:
                rows.deleteRow()
    by_name = {n.lower(): s for n, s in models}
    done = set()
    if strategy in ("UPDATE_EXISTING", "UPDATE_EXISTING_ADD_NEW"):
        with da.UpdateCursor(updated_features, ["Name", "SHAPE@"]) as rows:
            for name, _ in rows:
                key = str(name or "").lower()
                if key in by_name:
                    rows.updateRow([name, by_name[key]])
                    done.add(key)
    if strategy != "UPDATE_EXISTING":
        with da.InsertCursor(updated_features, ["Name", "SHAPE@"]) as cursor:
            for name, shape in models:
                if strategy == "UPDATE_EXISTING_ADD_NEW" and name.lower() in done:
                    continue
                cursor.insertRow([name, shape])
    return Result(updated_features)


# ---- for the LAS tools


def welzl(points, seed=0):
    """The smallest sphere enclosing 3D points (centre, radius): Welzl's (1991) randomised algorithm in Gärtner's
    move-to-front form (1999), exact; its support set never exceeds four points."""
    pts = np.unique(np.asarray(points, float).reshape(-1, 3), axis=0)
    order = np.random.default_rng(seed).permutation(len(pts))
    pts = [pts[i] for i in order]

    def ball(support):
        if not support:
            return np.zeros(3), -1.0
        p0 = support[0]
        if len(support) == 1:
            return p0, 0.0
        v = np.array([s - p0 for s in support[1:]])
        a = 2 * v @ v.T
        b = np.einsum("ij,ij->i", v, v)
        lam = np.linalg.lstsq(a, b, rcond=None)[0]
        centre = p0 + lam @ v
        return centre, float(np.linalg.norm(support[0] - centre))

    def inside(b, p):
        return b[1] >= 0 and np.linalg.norm(p - b[0]) <= b[1] * (1 + 1e-12) + 1e-12

    def mtf(n, support):
        b = ball(support)
        if len(support) == 4:
            return b
        i = 0
        while i < n:
            if not inside(b, pts[i]):
                p = pts[i]
                b = mtf(i, support + [p])
                del pts[i]
                pts.insert(0, p)
            i += 1
        return b

    centre, radius = mtf(len(pts), [])
    return centre, max(radius, 0.0)


def alpha_hull(points, alpha):
    """The boundary of a 3D alpha shape as outward triangles (Edelsbrunner and Mücke 1994): the Delaunay tetrahedra
    whose circumradius is at most ``alpha``, and their faces that only one of them has. None when no tetrahedron
    is kept."""
    from scipy.spatial import Delaunay, QhullError
    pts = np.unique(np.asarray(points, float).reshape(-1, 3), axis=0)
    if len(pts) < 4:
        return None
    try:
        tetra = Delaunay(pts).simplices
    except QhullError:
        return None
    keep = []
    for t in tetra:
        p = pts[t]
        a = 2 * (p[1:] - p[0])
        b = np.einsum("ij,ij->i", p[1:], p[1:]) - p[0] @ p[0]
        try:
            centre = np.linalg.solve(a, b)
        except np.linalg.LinAlgError:
            continue
        if np.linalg.norm(centre - p[0]) <= alpha:
            keep.append(t)
    if not keep:
        return None
    faces = {}
    for t in keep:
        for skip in range(4):
            face = tuple(sorted(int(v) for k, v in enumerate(t) if k != skip))
            faces.setdefault(face, []).append(int(t[skip]))
    out = []
    for face, opposite in faces.items():
        if len(opposite) != 1:
            continue
        tri = pts[list(face)]
        normal = np.cross(tri[1] - tri[0], tri[2] - tri[0])
        if normal @ (pts[opposite[0]] - tri[0]) > 0:
            tri = tri[[0, 2, 1]]
        out.append(tri)
    return np.array(out) if out else None


def sphere(centre, radius, segments=20):
    """A faceted sphere as outward triangles: the hull of a UV sphere of ``segments`` meridians (Buffer3D's)."""
    from ._mptools import _sphere
    return M.hull(_sphere(centre, radius, segments))


def distance_to_faces(points, tris):
    """The 3D distance from each point to the nearest face (Ericson 2005, §5.1.5)."""
    out = np.full(len(points), np.inf)
    for i, p in enumerate(np.asarray(points, float)):
        if len(tris):
            out[i] = float(M._point_triangle_distance(p, tris).min())
    return out
