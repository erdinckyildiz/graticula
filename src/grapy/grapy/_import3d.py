"""Import3DFiles: 3D model files read into multipatch features — ADR-238.

The tool's page lists five formats. grapy reads the three whose specifications are public and were read:

- **Wavefront OBJ** (``.obj``) — Wavefront's *Advanced Visualizer* appendix B1, "Object Files": ``v`` vertices and
  ``f`` faces (``v``, ``v/vt``, ``v//vn``, ``v/vt/vn``, negative indexes counting back);
- **COLLADA** (``.dae``) — Khronos COLLADA 1.4.1 / 1.5 (ISO/PAS 17506): ``<mesh>`` geometries of ``triangles``,
  ``polylist``, ``polygons``, ``tristrips`` and ``trifans`` placed by the visual scene's node transforms
  (``matrix``, ``translate``, ``rotate``, ``scale``), in the asset's ``<unit>`` and ``<up_axis>``;
- **VRML97 and GeoVRML** (``.wrl``) — ISO/IEC 14772-1:1997 and GeoVRML 1.1: ``IndexedFaceSet`` and ``ElevationGrid``
  shapes under ``Transform``, ``Group``, ``Anchor``, ``Collision``, ``Billboard``, ``LOD`` and ``Switch``, ``DEF`` and
  ``USE``; GeoVRML's ``GeoCoordinate`` in its GD, UTM and GC systems.

Refused by name: **3D Studio (.3ds)** — Autodesk has never published the format, and the descriptions in circulation are
reverse-engineered, which the clean room does not read (CLAUDE.md §5); **OpenFlight (.flt)** — its specification is
published (OGC 19-065, OpenFlight 16.0), but OGC's portal answered a web page instead of the document on 2026-10-31, so
its record layouts were not read and grapy does not guess them. As the page says, Box, Cone, Cylinder, Extrusion,
PointSet and Text nodes, and points and lines, are not imported: a multipatch holds neither.
"""
from __future__ import annotations

import math
import os
import re
import xml.etree.ElementTree as ET

import numpy as np
import shapely

from . import _multipatch as M
from ._result import Result
from ._runtime import AddWarning, ExecuteError, NotBuiltError

_SUFFIXES = {"3DS": ".3ds", "WRL": ".wrl", "FLT": ".flt", "DAE": ".dae", "OBJ": ".obj"}
_REFUSED = {
    ".3ds": "Autodesk has never published the 3D Studio format, and the descriptions in circulation are "
            "reverse-engineered, which the clean room does not read (CLAUDE.md §5)",
    ".flt": "OpenFlight's specification is published (OGC 19-065), but OGC's portal answered a web page instead of the "
            "document on 2026-10-31, so its record layouts were not read and grapy does not guess them",
}


def _refuse_format(path):
    ext = os.path.splitext(path)[1].lower()
    raise NotBuiltError(f"arcpy.ddd.Import3DFiles: '{os.path.basename(path)}' is not read by grapy: {_REFUSED[ext]}.")


# ---- faces


def _face(points):
    """A polygon face from 3D points: one face when they lie in a plane, else a fan of triangles (INFERRED)."""
    pts = np.asarray(points, float)
    if len(pts) < 3:
        return []
    if len(pts) == 3:
        return [shapely.Polygon(pts)]
    closed = np.vstack([pts, pts[:1]])
    normal = M._newell(closed)
    size = np.linalg.norm(normal)
    if size > 0:
        offsets = (pts - pts[0]) @ (normal / size)
        span = np.ptp(pts, axis=0).max()
        if np.abs(offsets).max() <= 1e-9 * max(span, 1.0):
            return [shapely.Polygon(pts)]
    return [shapely.Polygon([pts[0], pts[i], pts[i + 1]]) for i in range(1, len(pts) - 1)]


# ---- OBJ


def read_obj(path):
    """The faces of an OBJ file, in its own coordinates."""
    vertices, faces = [], []
    with open(path, encoding="utf-8", errors="replace") as handle:
        for line in handle:
            parts = line.split("#", 1)[0].split()
            if not parts:
                continue
            if parts[0] == "v":
                vertices.append([float(v) for v in parts[1:4]])
            elif parts[0] == "f":
                index = []
                for token in parts[1:]:
                    i = int(token.split("/")[0])
                    index.append(i - 1 if i > 0 else len(vertices) + i)
                faces.extend(_face([vertices[i] for i in index]))
    return [("", faces)]


# ---- COLLADA


def _local(tag):
    return tag.rsplit("}", 1)[-1]


def _children(element, name):
    return [c for c in element if _local(c.tag) == name]


def _child(element, name):
    found = _children(element, name)
    return found[0] if found else None


def _numbers(element):
    return np.array([float(v) for v in (element.text or "").split()]) if element is not None else np.zeros(0)


def _rotation(axis, angle):
    """A 4×4 rotation of ``angle`` radians about ``axis`` (Rodrigues)."""
    axis = np.asarray(axis, float)
    n = np.linalg.norm(axis)
    out = np.eye(4)
    if n == 0 or angle == 0:
        return out
    x, y, z = axis / n
    c, s, t = math.cos(angle), math.sin(angle), 1 - math.cos(angle)
    out[:3, :3] = [[t * x * x + c, t * x * y - s * z, t * x * z + s * y],
                   [t * x * y + s * z, t * y * y + c, t * y * z - s * x],
                   [t * x * z - s * y, t * y * z + s * x, t * z * z + c]]
    return out


def _translation(v):
    out = np.eye(4)
    out[:3, 3] = v
    return out


def _scaling(v):
    return np.diag([*v, 1.0])


def _apply(matrix, points):
    pts = np.asarray(points, float).reshape(-1, 3)
    return pts @ matrix[:3, :3].T + matrix[:3, 3]


def _dae_mesh(geometry):
    """The polygons (lists of point indexes into its positions) and positions of a COLLADA geometry's mesh."""
    mesh = _child(geometry, "mesh")
    if mesh is None:
        return np.zeros((0, 3)), []
    sources = {}
    for source in _children(mesh, "source"):
        values = _numbers(_child(source, "float_array"))
        accessor = source.find(".//{*}accessor")
        stride = int(accessor.get("stride", 3)) if accessor is not None else 3
        sources[source.get("id")] = values.reshape(-1, stride)[:, :3] if stride >= 3 else values.reshape(-1, stride)
    vertices = _child(mesh, "vertices")
    positions = None
    if vertices is not None:
        for i in _children(vertices, "input"):
            if i.get("semantic") == "POSITION":
                positions = sources.get(i.get("source", "").lstrip("#"))
    if positions is None:
        return np.zeros((0, 3)), []
    polygons = []
    for primitive in mesh:
        kind = _local(primitive.tag)
        if kind not in ("triangles", "polylist", "polygons", "tristrips", "trifans"):
            continue
        inputs = _children(primitive, "input")
        stride = max(int(i.get("offset", 0)) for i in inputs) + 1 if inputs else 1
        offset = next((int(i.get("offset", 0)) for i in inputs if i.get("semantic") == "VERTEX"), 0)
        blocks = [np.array([int(v) for v in (p.text or "").split()], np.int64) for p in _children(primitive, "p")]
        index_lists = [b[offset::stride] for b in blocks]
        if kind == "triangles":
            for b in index_lists:
                polygons += [list(b[i:i + 3]) for i in range(0, len(b) - 2, 3)]
        elif kind == "polylist":
            counts = [int(v) for v in (_child(primitive, "vcount").text or "").split()]
            b, at = index_lists[0] if index_lists else np.zeros(0, np.int64), 0
            for n in counts:
                polygons.append(list(b[at:at + n]))
                at += n
        elif kind == "polygons":
            polygons += [list(b) for b in index_lists]
        elif kind == "tristrips":
            for b in index_lists:
                polygons += [[b[i], b[i + 1], b[i + 2]] if i % 2 == 0 else [b[i + 1], b[i], b[i + 2]]
                             for i in range(len(b) - 2)]
        else:
            for b in index_lists:
                polygons += [[b[0], b[i], b[i + 1]] for i in range(1, len(b) - 1)]
    return positions, polygons


def _node_matrix(node):
    matrix = np.eye(4)
    for t in node:
        kind = _local(t.tag)
        v = _numbers(t)
        if kind == "matrix" and len(v) == 16:
            matrix = matrix @ v.reshape(4, 4)
        elif kind == "translate":
            matrix = matrix @ _translation(v[:3])
        elif kind == "rotate":
            matrix = matrix @ _rotation(v[:3], math.radians(v[3]))
        elif kind == "scale":
            matrix = matrix @ _scaling(v[:3])
    return matrix


def read_dae(path):
    """The faces of a COLLADA document's visual scene, in metres and z up."""
    root = ET.parse(path).getroot()
    asset = _child(root, "asset")
    unit, up = 1.0, "Y_UP"
    if asset is not None:
        if _child(asset, "unit") is not None:
            unit = float(_child(asset, "unit").get("meter", 1.0))
        if _child(asset, "up_axis") is not None:
            up = (_child(asset, "up_axis").text or "Y_UP").strip().upper()
    geometries = {g.get("id"): g for g in root.iter() if _local(g.tag) == "geometry"}
    library_nodes = {n.get("id"): n for lib in root.iter() if _local(lib.tag) == "library_nodes" for n in lib
                     if _local(n.tag) == "node"}
    faces = []

    def place(geometry, matrix):
        positions, polygons = _dae_mesh(geometry)
        if not len(positions):
            return
        moved = _apply(matrix, positions)
        for polygon in polygons:
            faces.extend(_face(moved[polygon]))

    def walk(node, parent):
        matrix = parent @ _node_matrix(node)
        for child in node:
            kind = _local(child.tag)
            if kind == "instance_geometry":
                g = geometries.get(child.get("url", "").lstrip("#"))
                if g is not None:
                    place(g, matrix)
            elif kind == "instance_node":
                n = library_nodes.get(child.get("url", "").lstrip("#"))
                if n is not None:
                    walk(n, matrix)
            elif kind == "node":
                walk(child, matrix)

    scenes = [s for s in root.iter() if _local(s.tag) == "visual_scene"]
    chosen = None
    instance = root.find(".//{*}scene/{*}instance_visual_scene")
    if instance is not None:
        chosen = next((s for s in scenes if s.get("id") == instance.get("url", "").lstrip("#")), None)
    chosen = chosen if chosen is not None else (scenes[0] if scenes else None)
    if chosen is None:
        for g in geometries.values():
            place(g, np.eye(4))
    else:
        for node in _children(chosen, "node"):
            walk(node, np.eye(4))
    axes = {"Y_UP": np.array([[1, 0, 0], [0, 0, -1], [0, 1, 0]], float),
            "X_UP": np.array([[0, 1, 0], [0, 0, 1], [1, 0, 0]], float), "Z_UP": np.eye(3)}[up]
    turn = np.eye(4)
    turn[:3, :3] = axes * unit
    return [("", [_transform_face(f, turn) for f in faces])]


def _transform_face(face, matrix):
    if np.allclose(matrix, np.eye(4)):
        return face
    rings = [_apply(matrix, np.asarray(face.exterior.coords)[:, :3])]
    rings += [_apply(matrix, np.asarray(r.coords)[:, :3]) for r in face.interiors]
    return shapely.Polygon(rings[0], rings[1:])


# ---- VRML97 and GeoVRML


_TOKEN = re.compile(r'"(?:[^"\\]|\\.)*"|[{}\[\]]|[^\s{}\[\],"#]+')


def _tokens(text):
    return _TOKEN.findall(_strip_comments(text))


def _strip_comments(text):
    out, quoted = [], False
    i = 0
    while i < len(text):
        ch = text[i]
        if ch == '"' and (i == 0 or text[i - 1] != "\\"):
            quoted = not quoted
        if ch == "#" and not quoted:
            while i < len(text) and text[i] != "\n":
                i += 1
            continue
        out.append(ch)
        i += 1
    return "".join(out)


def _is_number(token):
    return bool(re.fullmatch(r"[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?|0[xX][0-9a-fA-F]+", token))


class _Node:
    def __init__(self, kind, name=None):
        self.kind, self.name, self.fields = kind, name, {}


class _Parser:
    def __init__(self, tokens):
        self.tokens, self.at, self.defs = tokens, 0, {}

    def peek(self, k=0):
        return self.tokens[self.at + k] if self.at + k < len(self.tokens) else None

    def take(self):
        token = self.peek()
        self.at += 1
        return token

    def nodes(self):
        out = []
        while self.peek() is not None:
            if self.peek() in ("PROTO", "EXTERNPROTO"):
                self._skip_proto()
                continue
            if self.peek() == "ROUTE":
                self.at += 4
                continue
            node = self.node()
            if node is not None:
                out.append(node)
        return out

    def _skip_proto(self):
        kind = self.take()
        self.take()
        self._skip_block("[", "]")
        if kind == "PROTO":
            self._skip_block("{", "}")
        else:
            while self.peek() is not None and self.peek().startswith('"'):
                self.take()

    def _skip_block(self, open_, close):
        if self.peek() != open_:
            return
        depth = 0
        while self.peek() is not None:
            t = self.take()
            depth += t == open_
            depth -= t == close
            if depth == 0:
                return

    def node(self):
        token = self.take()
        if token == "NULL":
            return None
        if token == "USE":
            return self.defs.get(self.take())
        name = None
        if token == "DEF":
            name = self.take()
            token = self.take()
        node = _Node(token, name)
        if name:
            self.defs[name] = node
        if self.peek() != "{":
            return node
        self.take()
        while self.peek() not in ("}", None):
            field = self.take()
            if field in ("ROUTE",):
                self.at += 3
                continue
            if field in ("PROTO", "EXTERNPROTO"):
                self.at -= 1
                self._skip_proto()
                continue
            node.fields[field] = self.value()
        self.take()
        return node

    def _starts_node(self):
        t = self.peek()
        return t in ("DEF", "USE", "NULL") or (t is not None and self.peek(1) == "{" and not _is_number(t))

    def value(self):
        if self.peek() == "[":
            self.take()
            items = []
            while self.peek() not in ("]", None):
                items.append(self.node() if self._starts_node() else self._atom(self.take()))
            self.take()
            return items
        if self._starts_node():
            return self.node()
        items = []
        while self.peek() is not None and (_is_number(self.peek()) or self.peek() in ("TRUE", "FALSE")
                                           or self.peek().startswith('"')):
            items.append(self._atom(self.take()))
        return items if len(items) != 1 else items[0]

    @staticmethod
    def _atom(token):
        if token.startswith('"'):
            return token[1:-1]
        if token in ("TRUE", "FALSE"):
            return token == "TRUE"
        return float(int(token, 16)) if token.lower().startswith("0x") else float(token)


def _floats(value):
    if isinstance(value, list):
        return np.array([float(v) for v in value if not isinstance(v, (_Node, str))], float)
    if isinstance(value, (int, float)):
        return np.array([float(value)])
    return np.zeros(0)


def _geo_srid(system):
    """The EPSG code and coordinate order of a GeoVRML geoSystem (GeoVRML 1.1 §5): GD (latitude, longitude,
    elevation unless "longitude_first"), UTM (northing, easting, elevation unless "easting_first"), GC (geocentric)."""
    words = [str(w).upper() for w in (system if isinstance(system, list) else [system or "GD"])]
    if words and words[0] in ("GD", "GEODETIC"):
        if any(w not in ("GD", "GEODETIC", "WE", "LONGITUDE_FIRST", "LATITUDE_FIRST") for w in words):
            raise NotBuiltError("arcpy.ddd.Import3DFiles: GeoVRML's geodetic system is read on the WGS 84 ellipsoid "
                                f"(WE) only, as EPSG:4979 holds it; {words} names another")
        return 4979, ("LONGITUDE_FIRST" not in words)
    if words and words[0] == "UTM":
        zone = next((int(w[1:]) for w in words if re.fullmatch(r"Z\d+", w)), None)
        if zone is None:
            raise ExecuteError(f"ERROR: GeoVRML UTM system {words} names no zone.")
        south = "S" in words
        return (32700 if south else 32600) + zone, ("EASTING_FIRST" not in words)
    if words and words[0] in ("GC", "GEOCENTRIC"):
        return 4978, False
    raise ExecuteError(f"ERROR: '{words}' is not a GeoVRML geoSystem (GD, UTM or GC).")


def _many(value):
    """An MFNode field's nodes: a bracketed list, or one node written without brackets."""
    if value is None:
        return []
    return value if isinstance(value, list) else [value]


class _Wrl:
    _GROUPS = ("Group", "Transform", "Anchor", "Collision", "Billboard")
    _DROPPED = ("Box", "Cone", "Cylinder", "Extrusion", "PointSet", "Text", "Sphere", "IndexedLineSet")

    def __init__(self):
        self.srid = None
        self.dropped = set()

    def faces(self, node, matrix):
        if node is None:
            return []
        kind = node.kind
        if kind in ("GeoOrigin", "GeoLocation", "GeoElevationGrid", "GeoLOD", "Inline"):
            why = ("it names another file, which a user tool does not open beside the one it was given"
                   if kind == "Inline" else
                   "it places local coordinates on the ellipsoid through GeoVRML's local tangent frame, which grapy "
                   "does not compute; GeoCoordinate points are read")
            raise NotBuiltError(f"arcpy.ddd.Import3DFiles: VRML node {kind} is not read by grapy: {why}.")
        if kind in self._GROUPS:
            if kind == "Transform":
                matrix = matrix @ self._transform(node)
            out = []
            for child in _many(node.fields.get("children")):
                out += self.faces(child if isinstance(child, _Node) else None, matrix)
            return out
        if kind == "LOD":
            levels = _many(node.fields.get("level"))
            return self.faces(levels[0], matrix) if levels else []
        if kind == "Switch":
            choice = int(_floats(node.fields.get("whichChoice", -1))[0]) if node.fields.get("whichChoice") is not None \
                else -1
            choices = _many(node.fields.get("choice"))
            return self.faces(choices[choice], matrix) if 0 <= choice < len(choices) else []
        if kind == "Shape":
            return self.faces(node.fields.get("geometry"), matrix)
        if kind == "IndexedFaceSet":
            return self._indexed(node, matrix)
        if kind == "ElevationGrid":
            return self._grid(node, matrix)
        if kind in self._DROPPED:
            self.dropped.add(kind)
        return []

    @staticmethod
    def _transform(node):
        f = node.fields
        t = _floats(f.get("translation", [0, 0, 0]))
        c = _floats(f.get("center", [0, 0, 0]))
        r = _floats(f.get("rotation", [0, 0, 1, 0]))
        s = _floats(f.get("scale", [1, 1, 1]))
        so = _floats(f.get("scaleOrientation", [0, 0, 1, 0]))
        # ISO/IEC 14772-1 §6.52: P' = T × C × R × SR × S × −SR × −C × P
        return (_translation(t) @ _translation(c) @ _rotation(r[:3], r[3]) @ _rotation(so[:3], so[3]) @ _scaling(s)
                @ _rotation(so[:3], -so[3]) @ _translation(-c))

    def _points(self, coord, matrix):
        if coord is None:
            return np.zeros((0, 3))
        if coord.kind == "GeoCoordinate":
            srid, swap = _geo_srid(coord.fields.get("geoSystem", ["GD", "WE"]))
            if self.srid is None:
                self.srid = srid
            raw = coord.fields.get("point", [])
            text = " ".join(str(v) for v in (raw if isinstance(raw, list) else [raw]))
            pts = np.array([float(v) for v in text.replace(",", " ").split()], float).reshape(-1, 3)
            if swap:
                pts = pts[:, [1, 0, 2]]
            return pts
        pts = _floats(coord.fields.get("point", [])).reshape(-1, 3)
        return _apply(matrix, pts)

    def _indexed(self, node, matrix):
        f = node.fields
        pts = self._points(f.get("coord"), matrix)
        index = [int(v) for v in _floats(f.get("coordIndex", []))]
        ccw = f.get("ccw", True) is not False
        out, current = [], []
        for i in index + [-1]:
            if i == -1:
                if len(current) >= 3:
                    poly = pts[current] if ccw else pts[current[::-1]]
                    out += _face(poly)
                current = []
            else:
                current.append(i)
        return out

    @staticmethod
    def _grid(node, matrix):
        f = node.fields
        nx, nz = int(_floats(f.get("xDimension", 0))[0]), int(_floats(f.get("zDimension", 0))[0])
        dx, dz = float(_floats(f.get("xSpacing", 1))[0]), float(_floats(f.get("zSpacing", 1))[0])
        heights = _floats(f.get("height", [])).reshape(nz, nx)
        grid = np.array([[i * dx, heights[j, i], j * dz] for j in range(nz) for i in range(nx)]).reshape(nz, nx, 3)
        grid = _apply(matrix, grid.reshape(-1, 3)).reshape(nz, nx, 3)
        out = []
        for j in range(nz - 1):
            for i in range(nx - 1):
                # ElevationGrid's faces point up (+y) with ccw TRUE, as ISO/IEC 14772-1 §6.18 orients them.
                a, b, c, d = grid[j, i], grid[j, i + 1], grid[j + 1, i + 1], grid[j + 1, i]
                out += [shapely.Polygon([a, d, c]), shapely.Polygon([a, c, b])]
        return out


def read_wrl(path, per_root):
    """[(name, faces)] of a VRML97 file: one entry for the file, or one per root node."""
    with open(path, encoding="utf-8", errors="replace") as handle:
        text = handle.read()
    if not text.lstrip().startswith("#VRML V2.0"):
        raise ExecuteError(f"ERROR: '{os.path.basename(path)}' is not a VRML97 file (#VRML V2.0 header).")
    roots = _Parser(_tokens(text)).nodes()
    reader = _Wrl()
    entries = [(r.name or r.kind, reader.faces(r, np.eye(4))) for r in roots]
    if reader.dropped:
        AddWarning(f"Import3DFiles: {', '.join(sorted(reader.dropped))} node(s) in {os.path.basename(path)} are not "
                   "imported, as documented.")
    if per_root:
        return [(name, faces) for name, faces in entries if faces], reader.srid
    return [("", [f for _, faces in entries for f in faces])], reader.srid


# ---- the tool


def _files(in_files, suffix):
    wanted = None if str(suffix or "*").strip() in ("*", "", "#") else _SUFFIXES.get(str(suffix).upper().lstrip("."))
    if str(suffix or "*").strip() not in ("*", "", "#") and wanted is None:
        raise ValueError(f"arcpy.ddd.Import3DFiles: file_suffix '{suffix}' is not one of *, 3DS, WRL, FLT, DAE, OBJ.")
    known = tuple(_SUFFIXES.values())
    paths = []
    items = in_files if isinstance(in_files, (list, tuple)) else [v.strip().strip("'\"")
                                                                   for v in str(in_files).split(";") if v.strip()]
    for item in items:
        item = os.fspath(item)
        if os.path.isdir(item):
            for name in sorted(os.listdir(item)):
                ext = os.path.splitext(name)[1].lower()
                if ext in known and (wanted is None or ext == wanted):
                    paths.append(os.path.join(item, name))
        else:
            paths.append(item)
    return paths


def Import3DFiles(in_files, out_featureClass, root_per_feature="ONE_FILE_ONE_FEATURE", spatial_reference=None,
                  y_is_up="Z_IS_UP", file_suffix="*", in_featureClass=None, symbol_field=None):
    """3D model files (or the matching files of folders) as multipatch features, one per file — or, for VRML, one per
    root node with ONE_ROOT_ONE_FEATURE. Y_IS_UP turns a model whose y is up so that its z is (x, −z, y);
    a COLLADA file's own ``up_axis`` and ``unit`` are honoured as its specification defines them. With placement
    points, each file's model is moved to the point whose ``symbol_field`` names it. The spatial reference is the
    parameter's, else a GeoVRML file's, else the placement points'.

    Refused by name: 3D Studio and OpenFlight files (see the module). INFERRED (ADR-238 §5.2): a ``Name`` field holds
    the file name or the root node's DEF name (the page names no output fields); a face of more than three vertices
    not in one plane is stored as a fan of triangles; a symbol matches a file by its name with or without its
    extension, ignoring case, and a file no point names is skipped with a warning; textures and colours are not kept
    (grapy's multipatch holds geometry only); an ``Inline`` node is refused, a user tool opening only the files it is
    given."""
    from ._gpkg import Column
    from ._reference import reference_of
    per_root = str(root_per_feature or "ONE_FILE_ONE_FEATURE").upper() == "ONE_ROOT_ONE_FEATURE"
    y_up = str(y_is_up or "Z_IS_UP").upper() == "Y_IS_UP"
    paths = _files(in_files, file_suffix)
    if not paths:
        raise ExecuteError("ERROR 000735: Import3DFiles found no 3D file to read.")
    places, place_srid = {}, 0
    if M.given(in_featureClass):
        if not M.given(symbol_field):
            raise ExecuteError("ERROR 000735: Import3DFiles needs symbol_field with placement points.")
        _, place_srid, rows = M.read(in_featureClass)
        for _, values, shape in rows:
            if shape is None:
                continue
            xyz = shapely.get_coordinates(shape, include_z=bool(shapely.has_z(shape)))[0]
            key = str(values[symbol_field] or "").strip().lower()
            places[key] = np.array([xyz[0], xyz[1], xyz[2] if len(xyz) > 2 else 0.0])
    reference = reference_of(spatial_reference) if M.given(spatial_reference) else None
    srid = reference.factoryCode if reference is not None else 0
    rows = []
    for path in paths:
        ext = os.path.splitext(path)[1].lower()
        if ext in _REFUSED:
            _refuse_format(path)
        file_srid = None
        if ext == ".obj":
            entries = read_obj(path)
        elif ext == ".dae":
            entries = read_dae(path)
        elif ext == ".wrl":
            entries, file_srid = read_wrl(path, per_root)
        else:
            raise ExecuteError(f"ERROR: '{os.path.basename(path)}' is not a 3D file Import3DFiles reads.")
        if file_srid and not srid:
            srid = file_srid
        move = None
        if places:
            base = os.path.basename(path).lower()
            move = places.get(base, places.get(os.path.splitext(base)[0]))
            if move is None:
                AddWarning(f"Import3DFiles: no placement point names {os.path.basename(path)}; it is skipped.")
                continue
        for name, faces in entries:
            shapes = []
            for face in faces:
                if y_up and file_srid is None and ext != ".dae":
                    face = _transform_face(face, np.array([[1, 0, 0, 0], [0, 0, -1, 0], [0, 1, 0, 0], [0, 0, 0, 1]],
                                                          float))
                if move is not None:
                    face = _transform_face(face, _translation(move))
                shapes.append(face)
            if shapes:
                rows.append(([name or os.path.splitext(os.path.basename(path))[0]], M.from_faces(shapes)))
    if not srid:
        srid = place_srid
    path = M.write(out_featureClass, srid, [Column("Name", "String")], rows)
    return Result(path)
