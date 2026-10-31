"""LandXMLToTin, SurfaceDifference and TinDifference: the TIN tools that need a constrained Delaunay triangulation —
ADR-229 condition 6, on ``_tin._constrain`` (Sloan 1993).

Each is built to its reference page on doc.esri.com, read on 2026-10-31; what the page leaves open is labelled INFERRED.
LandXML is the LandXML 1.2 schema (landxml.org), a public standard: a ``Surface`` whose ``Definition`` (``surfType``
``TIN``) holds ``Pnts`` (``P`` elements, ``id`` and the text *northing easting elevation*) and ``Faces`` (``F``
elements, three point ids; ``i="1"`` marks a face invisible).
"""
from __future__ import annotations

import os
import re
import xml.etree.ElementTree as ET

import numpy as np
import shapely

from . import _gpkg, _tin
from ._result import Result
from ._runtime import AddWarning, ExecuteError, NotBuiltError
from ._runtime import _is_given as _given


def _refuse(tool, parameter, value, why=""):
    raise NotBuiltError(f"arcpy.ddd.{tool}: {parameter} '{value}' is not built in grapy{': ' + why if why else ''}.")


# ---- LandXML


def _local(tag):
    return tag.rsplit("}", 1)[-1]


def _children(element, name):
    return [c for c in element if _local(c.tag) == name]


def read_landxml(path):
    """[(name or None, srid, points {id: (x, y, z)}, faces [(id, id, id, visible)])] of each TIN surface in a LandXML
    file, in document order. ``P`` text is northing, easting, elevation, as the schema orders it."""
    root = ET.parse(path).getroot()
    srid = 0
    for element in root.iter():
        if _local(element.tag) == "CoordinateSystem" and element.get("epsgCode", "").strip().isdigit():
            srid = int(element.get("epsgCode"))
            break
    found = []
    for surface in root.iter():
        if _local(surface.tag) != "Surface":
            continue
        for definition in _children(surface, "Definition"):
            if definition.get("surfType", "TIN").upper() != "TIN":
                continue
            points = {}
            for pnts in _children(definition, "Pnts"):
                for p in _children(pnts, "P"):
                    values = [float(v) for v in (p.text or "").split()]
                    if len(values) < 3:
                        raise ExecuteError(f"ERROR: {path}: point {p.get('id')} has no elevation.")
                    points[p.get("id")] = (values[1], values[0], values[2])
            faces = []
            for block in _children(definition, "Faces"):
                for f in _children(block, "F"):
                    ids = (f.text or "").split()
                    if len(ids) != 3 or any(i not in points for i in ids):
                        raise ExecuteError(f"ERROR: {path}: a face of surface {surface.get('name')!r} does not name "
                                           "three of its points.")
                    faces.append((*ids, f.get("i", "0").strip() != "1"))
            found.append((surface.get("name") or None, srid, points, faces))
    return found


def LandXMLToTin(in_landxml_path, out_tin_folder, tin_basename, tinnames=None):
    """TIN surfaces of a LandXML file as grapy TINs in ``out_tin_folder``, "created as a constrained Delaunay TIN", as
    the page says: every face edge is an enforced hard edge of a CONSTRAINED_DELAUNAY triangulation of the surface's
    points, so the TIN's triangles are the file's faces; an invisible face (``i="1"``) is outside the data area.
    ``tinnames`` picks surfaces by name or by position from 1, separated by ``;``; with none, every TIN surface. If a
    TIN of the base name already exists, nothing is written, as documented.

    INFERRED (Q-196 (s)): each TIN is ``<basename><surface name>`` — the name's characters outside letters, digits and
    ``_`` dropped — or ``<basename><position>`` for an unnamed surface; the TIN is stored as its points (mass points)
    and its visible faces (a hard clip), so EditTin re-triangulates the same faces; the reference is the file's
    ``CoordinateSystem/@epsgCode`` when it has one; ``SourceData`` breaklines and boundaries are not read, the faces
    already holding them."""
    path = os.fspath(in_landxml_path)
    if not os.path.isabs(path):
        from ._runtime import env
        path = os.path.join(env.workspace or "", path)
    if not os.path.exists(path):
        raise ExecuteError(f"ERROR 000732: Input LandXML: {in_landxml_path} does not exist.")
    surfaces = read_landxml(path)
    if not surfaces:
        raise ExecuteError(f"ERROR: {in_landxml_path} holds no TIN surface.")
    chosen = list(range(len(surfaces)))
    if _given(tinnames):
        tokens = [t.strip().strip("'\"") for t in str(tinnames).split(";") if t.strip()]
        if tokens == ["<unnamed>"]:
            raise ExecuteError("ERROR: the <unnamed> keyword cannot be used by itself, as documented.")
        chosen = []
        names = [s[0] for s in surfaces]
        for token in tokens:
            if token.isdigit() and 1 <= int(token) <= len(surfaces):
                chosen.append(int(token) - 1)
            elif token in names:
                chosen.append(names.index(token))
            elif token != "<unnamed>":
                raise ExecuteError(f"ERROR: {in_landxml_path} has no TIN surface {token!r}.")
    folder = os.fspath(out_tin_folder)
    base = str(tin_basename)
    targets = []
    for k in chosen:
        name = surfaces[k][0]
        suffix = re.sub(r"[^A-Za-z0-9_]", "", name) if name else str(k + 1)
        targets.append((k, os.path.join(folder, base + (suffix if len(chosen) > 1 or name else ""))))
    if any(_tin.exists(t) or os.path.exists(t) for _, t in targets):
        AddWarning(f"A TIN named from the base name '{base}' already exists in {folder}; nothing was written.")
        return Result(folder)
    for k, target in targets:
        _, srid, points, faces = surfaces[k]
        cloud = shapely.MultiPoint([shapely.Point(*p) for p in points.values()])
        visible = [shapely.Polygon([points[a], points[b], points[c]]) for a, b, c, show in faces if show]
        inputs = [("masspoints", cloud, 0, None, False)]
        if visible:
            inputs.append(("hardclip", shapely.MultiPolygon(visible), 0, None, False))
        _tin.store(target, srid, inputs, constrained=True)
    return Result(folder)


# ---- SurfaceDifference


def _surface(value, tool, resolution, method):
    """The Surface of an input: a TIN, a terrain at a pyramid level, or a LAS dataset thinned to one point per window of
    ``resolution`` by ``method`` (CLOSEST_TO_MEAN, MIN, MAX), as the page documents."""
    from . import _las, _tintools
    kind = str(method or "CLOSEST_TO_MEAN").upper()
    if kind not in ("CLOSEST_TO_MEAN", "MIN", "MAX"):
        _refuse(tool, "method", method)
    if _las.is_las(value) and _given(resolution) and float(resolution) > 0:
        return _las.surface(value, tool, thinning=(float(resolution), kind))
    surface = _tintools.surface_of(value, tool, resolution)
    if surface is None:
        _refuse(tool, "in_surface", value, "it is neither a TIN, a LAS dataset nor a terrain grapy holds")
    return surface


def difference(surface, reference):
    """The difference surface: a CONSTRAINED_DELAUNAY triangulation of both surfaces' data-area nodes with every edge
    of both enforced, so that each of its triangles lies inside one triangle of each and both are linear on it; its
    node z is the input's height less the reference's, and its data area is where both surfaces have one."""
    points, segments = [], []
    for s in (surface, reference):
        used = s.triangles[s.inside]
        edges = {(min(i, j), max(i, j)) for p, q, r in used.tolist() for i, j in ((p, q), (q, r), (r, p))}
        segments.extend((tuple(s.nodes[i]), tuple(s.nodes[j]), 2) for i, j in sorted(edges))
        if used.size:
            points.extend(map(tuple, s.nodes[np.unique(used)]))
    merged = _tin.triangulate(points, segments, srid=surface.srid or reference.srid, constrained=True)
    xs, ys = merged.nodes[:, 0], merged.nodes[:, 1]
    d = surface.z_at(xs, ys) - reference.z_at(xs, ys)
    if len(merged.triangles):
        p, q, r = merged.corners()
        cx, cy = (p[:, 0] + q[:, 0] + r[:, 0]) / 3, (p[:, 1] + q[:, 1] + r[:, 1]) / 3
        merged.inside = (surface.locate(cx, cy) >= 0) & (reference.locate(cx, cy) >= 0)
        merged.inside &= np.isfinite(d[merged.triangles]).all(axis=1)
    merged.nodes[:, 2] = np.where(np.isfinite(d), d, 0.0)
    merged.edges = {}
    return merged


def _pieces(diff, surface, region, tolerance):
    """[(code, polygon, volume, input surface area)] of the difference surface's data area: each triangle split along
    its zero line into the part above (1) and below (−1) the reference, or kept whole as the same (0)."""
    from ._tintools import _clip_triangle
    a, b, c = diff.planes()
    planes = surface.planes()
    out = []
    for t in np.nonzero(diff.inside)[0]:
        corners = diff.nodes[diff.triangles[t]]
        if not np.isfinite(a[t]):
            continue
        if np.abs(corners[:, 2]).max() <= tolerance:
            parts = [(0, shapely.Polygon(corners[:, :2]))]
        else:
            parts = [(1, _clip_triangle(corners, a[t], b[t], c[t], 0.0, True)),
                     (-1, _clip_triangle(corners, a[t], b[t], c[t], 0.0, False))]
        for code, piece in parts:
            if region is not None and not piece.is_empty:
                piece = shapely.intersection(piece, region)
            if piece.is_empty or piece.area <= 0:
                continue
            centre = piece.centroid
            depth = a[t] * centre.x + b[t] * centre.y + c[t]
            volume = 0.0 if code == 0 else abs(piece.area * depth)
            at = surface.locate([centre.x], [centre.y])[0]
            sa, sb = (0.0, 0.0) if at < 0 else (planes[0][at], planes[1][at])
            out.append((code, piece, volume, piece.area * float(np.sqrt(1 + sa ** 2 + sb ** 2))))
    return out


def SurfaceDifference(in_surface, in_reference_surface, out_feature_class, pyramid_level_resolution=0,
                      reference_pyramid_level_resolution=0, out_raster=None, raster_cell_size=None, out_tin_folder=None,
                      out_tin_basename=None, method="CLOSEST_TO_MEAN", reference_method="CLOSEST_TO_MEAN", extent=None,
                      boundary=None):
    """The displacement of a surface from a reference, as *How Surface Difference works* describes: the input's
    triangles are compared with the reference's, split where they intersect it, and a difference surface is built "using
    constrained Delaunay triangulation" whose z is the input's height less the reference's — positive above, negative
    below, 0 where they coincide. Contiguous triangles and triangle parts of one classification become a polygon with
    ``Volume`` (the space between the surfaces it bounds, 0 where they are the same), ``SArea`` (the input surface's
    area within it) and ``Code`` (1 above, −1 below, 0 the same). Only where both surfaces overlap. An output raster is
    the difference surface by linear interpolation; an output TIN is the difference surface itself. Exact: both
    surfaces are linear on each triangle of the difference surface (``difference``).

    INFERRED (Q-196 (t)): the raster's cell size defaults to the one TinRaster's OBSERVATIONS 250 gives; one output TIN,
    ``<out_tin_folder>/<out_tin_basename>``, grapy having no node limit; two heights within 1e-9 of the surfaces' z
    span are the same; ``extent`` and ``boundary`` clip the polygons as ExtractLas's area does."""
    from . import _las, _tintools
    surface = _surface(in_surface, "SurfaceDifference", pyramid_level_resolution, method)
    reference = _surface(in_reference_surface, "SurfaceDifference", reference_pyramid_level_resolution,
                         reference_method)
    diff = difference(surface, reference)
    if not diff.inside.any():
        raise ExecuteError("ERROR: the input surfaces do not overlap; the horizontal extent of the input surfaces must "
                           "overlap.")
    region = _las._area_of(extent, boundary) if (_given(extent) or _given(boundary)) else None
    span = max(float(np.ptp(surface.nodes[:, 2])) if len(surface.nodes) else 0.0,
               float(np.ptp(reference.nodes[:, 2])) if len(reference.nodes) else 0.0, 1.0)
    pieces = _pieces(diff, surface, region, span * 1e-9)
    rows = []
    for code in (1, 0, -1):
        mine = [(p, v, s) for k, p, v, s in pieces if k == code]
        if not mine:
            continue
        merged = shapely.union_all([p for p, _, _ in mine])
        parts = list(getattr(merged, "geoms", [merged]))
        tree = shapely.STRtree(parts)
        totals = [[0.0, 0.0] for _ in parts]
        for piece, volume, area in mine:
            hit = tree.query(piece.representative_point(), predicate="intersects")
            if len(hit):
                totals[int(hit[0])][0] += volume
                totals[int(hit[0])][1] += area
        for part, (volume, area) in zip(parts, totals):
            if part.geom_type == "Polygon" and part.area > 0:
                rows.append([volume, area, code, shapely.MultiPolygon([part])])
    columns = [_gpkg.Column("Volume", "Double"), _gpkg.Column("SArea", "Double"), _gpkg.Column("Code", "Integer")]
    path = _tintools._write(out_feature_class, "Polygon", diff.srid, columns, False, rows)
    if _given(out_raster):
        sample = f"CELLSIZE {float(raster_cell_size)}" if _given(raster_cell_size) else "OBSERVATIONS 250"
        _tintools.raster_of(diff, out_raster, "FLOAT", "LINEAR", sample, 1, None, "SurfaceDifference")
    if _given(out_tin_folder):
        if not _given(out_tin_basename):
            raise ExecuteError("ERROR 000735: Output TIN Base Name: value is required with an output TIN folder.")
        target = os.path.join(os.fspath(out_tin_folder), str(out_tin_basename))
        used = np.unique(diff.triangles[diff.inside])
        _tin.store(target, diff.srid, [("masspoints", shapely.MultiPoint([shapely.Point(*diff.nodes[i]) for i in used]),
                                        0, None, False)], surface=diff)
    return Result(path)


def TinDifference(in_tin1, in_tin2, out_feature_class):
    """The legacy tool SurfaceDifference replaces: the polygons of ``in_tin1``'s displacement from ``in_tin2``.
    INFERRED (Q-196 (t)): its page names no fields, and it writes SurfaceDifference's ``Volume``, ``SArea`` and
    ``Code``."""
    return SurfaceDifference(in_tin1, in_tin2, out_feature_class)
