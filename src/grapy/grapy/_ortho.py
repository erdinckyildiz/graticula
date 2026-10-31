"""Ortho mapping on grapy's mosaic datasets: frame camera and RPC sensor models, tie and control points, block
adjustment and orthorectification — ADR-234.

ArcGIS keeps an ortho mapping workspace in Esri's own project format, and the sensor models of a mosaic dataset's items
in its raster types; neither is published, and neither is reproduced. What is published, and what grapy builds on:

- **The frames and cameras tables** (ArcGIS Pro's "Frames table schema" and "Cameras table schema" pages): an image's
  perspective centre and Omega, Phi, Kappa; a camera's focal length, principal point and pixel size in microns, its
  affine and distortion coefficients. A mosaic dataset of the Frame Camera raster type is added from them.
- **The collinearity condition** of a frame camera (Wolf, Dewitt and Wilkinson, *Elements of Photogrammetry*, 4th ed.,
  §11.4 and Appendix C): image coordinates are the projection of a ground point through the perspective centre, rotated
  by M = M(kappa) M(phi) M(omega).
- **Rational polynomial coefficients** (RPC00B, NITF STDI-0002 and OGC 06-042), which a raster file may carry and
  rasterio reads: line and sample are ratios of cubic polynomials of normalised latitude, longitude and height.

grapy's storage is its own table in the mosaic dataset's GeoPackage (INFERRED, ADR-234 §5.2), registered as the
``grapy_ortho`` extension: ``grapy_ortho_item`` holds each Frame Camera item's model as JSON — its exterior and interior
orientation — and the model it was added with, so Apply Block Adjustment can RESET it. Stereo pairs are a feature
table named ``<mosaic>_Stereo``. Control points are ordinary point feature classes whose fields are listed in
``CONTROL_FIELDS``.

Where a page names an operation without defining it, the standard published method is built as an equivalent
(ADR-224 §5.6) and says so: Harris corners (Harris and Stephens 1988) matched by normalised cross-correlation (Lewis
1995) in object space (Helava 1988) for tie and control points; a bundle adjustment by sparse Levenberg–Marquardt least
squares on the collinearity equations (Triggs et al. 2000) for Compute Block Adjustment.
"""
from __future__ import annotations

import csv
import json
import math
import os
import warnings

import numpy as np
import shapely

from . import _gpkg, _mosaic
from ._runtime import ExecuteError, NotBuiltError
from ._runtime import _is_given  # noqa: E402 — a Raster is never compared with ==

EXTENSION = "grapy_ortho"
DEFINITION = "Graticula ADR-234: grapy's ortho mapping sensor models"

#: The fields of grapy's control point feature class (INFERRED, ADR-234 §5.9 (h)): one row per image observation of a
#: point, or a ground-only row (ImageID null) for a control point not yet measured on an image. The names follow the
#: JSON control points of MatchControlPoints' page (pointId, xyAccuracy, zAccuracy, imagePoints' imageId, x, y).
CONTROL_FIELDS = (("PointID", "Integer"), ("Type", "Integer"), ("Status", "Integer"), ("ImageID", "Integer"),
                  ("ImageX", "Double"), ("ImageY", "Double"), ("XYAccuracy", "Double"), ("ZAccuracy", "Double"),
                  ("Tag", "String"))
TIE, GCP, CHECK = 1, 2, 3


def _refuse(tool, parameter, value, why=""):
    raise NotBuiltError(f"arcpy.management.{tool}: {parameter} '{value}' is not built in grapy{': ' + why if why else ''}.")


def _given(value):
    return _is_given(value)


def _word(value, default):
    return str(value).upper() if _given(value) else default


# ---- the frame camera: collinearity (Wolf, Dewitt and Wilkinson 2014, §11.4; Appendix C)


def rotation(omega, phi, kappa):
    """M = M(kappa) M(phi) M(omega), angles in degrees (Wolf et al., eq. C-33)."""
    o, p, k = np.radians([omega, phi, kappa])
    so, co, sp, cp, sk, ck = math.sin(o), math.cos(o), math.sin(p), math.cos(p), math.sin(k), math.cos(k)
    return np.array([[cp * ck, so * sp * ck + co * sk, -co * sp * ck + so * sk],
                     [-cp * sk, -so * sp * sk + co * ck, co * sp * sk + so * ck],
                     [sp, -so * cp, co * cp]])


class FrameModel:
    """A frame camera image's orientation: perspective centre (X, Y, Z) in map units, Omega, Phi, Kappa in degrees,
    and the camera — focal length, principal point and pixel size in microns, rows and columns, and optionally the
    affine from image to film and radial and tangential distortion.

    INFERRED (ADR-234 §5.9 (a)): pixel coordinates are (column, row) from the upper-left corner of the image, a pixel's
    centre at +0.5; without an affine, film x is (column − columns/2) × pixel size and film y (rows/2 − row) × pixel size
    (FCS 1, X_Right_Y_Up), measured from the fiducial centre; the default AngleDirection (−1) takes the angles as Wolf's
    and +1 negates them; the default Polarity (−1) is Wolf's positive image and +1 mirrors it; Radial K0;K1;K2;K3 and
    Tangential P1;P2 are Brown's model in millimetres, distortion added to ideal film coordinates."""

    kind = "frame"

    def __init__(self, data):
        self.data = dict(data)
        d = self.data
        self.X, self.Y, self.Z = float(d["X"]), float(d["Y"]), float(d["Z"])
        self.omega, self.phi, self.kappa = float(d["omega"]), float(d["phi"]), float(d["kappa"])
        self.f = float(d["focal_length"])
        self.x0, self.y0 = float(d.get("principal_x") or 0), float(d.get("principal_y") or 0)
        self.ps = float(d["pixel_size"])
        self.rows, self.columns = int(d["rows"]), int(d["columns"])
        self.affine = d.get("affine")
        self.radial = d.get("radial") or [0, 0, 0, 0]
        self.tangential = d.get("tangential") or [0, 0]
        self.sign = -1.0 if int(d.get("angle_direction") or -1) == 1 else 1.0
        self.mirror = -1.0 if int(d.get("polarity") or -1) == 1 else 1.0
        self.average_z = float(d.get("average_z") or 0)

    def matrix(self):
        return rotation(self.sign * self.omega, self.sign * self.phi, self.sign * self.kappa)

    # film (microns, from the fiducial centre) <-> pixel

    def film_of_pixel(self, col, row):
        col, row = np.asarray(col, float), np.asarray(row, float)
        if self.affine:
            a0, a1, a2, b0, b1, b2 = self.affine
            return a0 + a1 * col + a2 * row, b0 + b1 * col + b2 * row
        return (col - self.columns / 2) * self.ps, (self.rows / 2 - row) * self.ps

    def pixel_of_film(self, x, y):
        x, y = np.asarray(x, float), np.asarray(y, float)
        if self.affine:
            a0, a1, a2, b0, b1, b2 = self.affine
            det = a1 * b2 - a2 * b1
            dx, dy = x - a0, y - b0
            return (b2 * dx - a2 * dy) / det, (a1 * dy - b1 * dx) / det
        return x / self.ps + self.columns / 2, self.rows / 2 - y / self.ps

    def _distortion(self, x, y):
        """Brown's radial and tangential distortion at ideal film coordinates (microns), in microns."""
        k0, k1, k2, k3 = (float(v) for v in (list(self.radial) + [0, 0, 0, 0])[:4])
        p1, p2 = (float(v) for v in (list(self.tangential) + [0, 0])[:2])
        if not any((k0, k1, k2, k3, p1, p2)):
            return 0.0, 0.0
        xm, ym = x / 1000.0, y / 1000.0
        r2 = xm * xm + ym * ym
        radial = k0 + k1 * r2 + k2 * r2 ** 2 + k3 * r2 ** 3
        dx = xm * radial + p1 * (r2 + 2 * xm * xm) + 2 * p2 * xm * ym
        dy = ym * radial + p2 * (r2 + 2 * ym * ym) + 2 * p1 * xm * ym
        return dx * 1000.0, dy * 1000.0

    # ground <-> pixel

    def project(self, X, Y, Z):
        """(column, row) of ground points: the collinearity equations (Wolf et al., eqs. 11-1 and 11-2)."""
        m = self.matrix()
        d = np.stack([np.asarray(X, float) - self.X, np.asarray(Y, float) - self.Y, np.asarray(Z, float) - self.Z])
        u, v, w = m @ d.reshape(3, -1)
        x = self.x0 - self.f * u / w
        y = self.y0 - self.f * v / w
        dx, dy = self._distortion(x - self.x0, y - self.y0)
        x, y = x + dx, y + dy
        col, row = self.pixel_of_film(self.mirror * x, self.mirror * y)
        shape = np.shape(X)
        return col.reshape(shape), row.reshape(shape)

    def ray(self, col, row):
        """Unit-free direction vectors in ground space of pixels (the inverse of ``project`` without the depth)."""
        x, y = self.film_of_pixel(col, row)
        x, y = self.mirror * x, self.mirror * y
        xi, yi = x - self.x0, y - self.y0
        for _ in range(5):   # undistort by fixed-point iteration
            dx, dy = self._distortion(xi, yi)
            xi, yi = x - self.x0 - dx, y - self.y0 - dy
        film = np.stack([np.ravel(xi), np.ravel(yi), np.full(np.size(xi), -self.f)])
        return self.matrix().T @ film

    def to_ground(self, col, row, Z):
        """Ground (X, Y) of pixels at heights ``Z``: the ray from the perspective centre, through the image point,
        to the plane Z — the collinearity equations solved for X and Y at a known Z."""
        d = self.ray(col, row)
        Z = np.broadcast_to(np.asarray(Z, float), np.shape(np.ravel(col))).ravel()
        t = (Z - self.Z) / d[2]
        return (self.X + t * d[0]).reshape(np.shape(col)), (self.Y + t * d[1]).reshape(np.shape(col))

    def gsd(self, z=None):
        """Ground sample distance at a height: pixel size × distance / focal length."""
        return self.ps * abs(self.Z - (self.average_z if z is None else z)) / self.f

    def corners(self):
        return np.array([0, self.columns, self.columns, 0], float), np.array([0, 0, self.rows, self.rows], float)


# ---- RPC00B (NITF STDI-0002; OGC 06-042)


def _rpc_terms(L, P, H):
    """The 20 cubic terms of RPC00B, in its order."""
    one = np.ones_like(L)
    return np.stack([one, L, P, H, L * P, L * H, P * H, L * L, P * P, H * H, P * L * H, L ** 3, L * P * P, L * H * H,
                     L * L * P, P ** 3, P * H * H, L * L * H, P * P * H, H ** 3])


class RpcModel:
    """A raster's rational polynomial coefficients: line and sample of a longitude, latitude (WGS 84 degrees) and
    ellipsoidal height. INFERRED (ADR-234 §5.9 (a)): line 0, sample 0 is the centre of the upper-left pixel, so the
    pixel coordinate grapy uses (from the image's corner) is sample + 0.5, line + 0.5."""

    kind = "rpc"

    def __init__(self, rpc):
        self.rpc = rpc

    def project(self, lon, lat, h):
        r = self.rpc
        L = (np.asarray(lon, float) - r.long_off) / r.long_scale
        P = (np.asarray(lat, float) - r.lat_off) / r.lat_scale
        H = (np.asarray(h, float) - r.height_off) / r.height_scale
        terms = _rpc_terms(L, P, H)
        line = np.tensordot(r.line_num_coeff, terms, 1) / np.tensordot(r.line_den_coeff, terms, 1)
        sample = np.tensordot(r.samp_num_coeff, terms, 1) / np.tensordot(r.samp_den_coeff, terms, 1)
        return sample * r.samp_scale + r.samp_off + 0.5, line * r.line_scale + r.line_off + 0.5

    def to_ground(self, col, row, h):
        """(lon, lat) of pixels at heights ``h``, by Newton's method on ``project`` from the offsets."""
        r = self.rpc
        col, row = np.asarray(col, float).ravel(), np.asarray(row, float).ravel()
        h = np.broadcast_to(np.asarray(h, float), col.shape)
        lon, lat = np.full(col.shape, r.long_off, float), np.full(col.shape, r.lat_off, float)
        for _ in range(20):
            c0, r0 = self.project(lon, lat, h)
            e = 1e-6
            c1, r1 = self.project(lon + e, lat, h)
            c2, r2 = self.project(lon, lat + e, h)
            j11, j12, j21, j22 = (c1 - c0) / e, (c2 - c0) / e, (r1 - r0) / e, (r2 - r0) / e
            det = j11 * j22 - j12 * j21
            dc, dr = col - c0, row - r0
            lon = lon + (j22 * dc - j12 * dr) / det
            lat = lat + (j11 * dr - j21 * dc) / det
            if np.nanmax(np.abs(np.concatenate([dc, dr]))) < 1e-8:
                break
        return lon, lat


def rpc_of(path):
    """The RpcModel of a raster file, or None when it carries no RPCs."""
    import rasterio
    with warnings.catch_warnings():
        warnings.simplefilter("ignore")
        with rasterio.open(path) as source:
            rpcs = source.rpcs
            rows, columns = source.height, source.width
    if not rpcs:
        return None
    model = RpcModel(rpcs)
    model.rows, model.columns = rows, columns
    return model


# ---- storage


def ensure(connection):
    from ._topology import register
    connection.execute("""create table if not exists grapy_ortho_item (mosaic text not null, item integer not null,
        kind text not null, model text not null, original text not null, primary key (mosaic, item))""")
    register(connection, ("grapy_ortho_item",), EXTENSION, DEFINITION)


def models(connection, mosaic):
    """{OBJECTID: FrameModel} of a mosaic dataset's Frame Camera items."""
    if not connection.execute("select 1 from sqlite_master where name = 'grapy_ortho_item'").fetchone():
        return {}
    return {item: FrameModel(json.loads(model)) for item, model in connection.execute(
        "select item, model from grapy_ortho_item where mosaic = ? order by item", (mosaic,))}


def has_models(connection, mosaic):
    return bool(models(connection, mosaic))


def save_model(connection, mosaic, item, model, original=None):
    ensure(connection)
    if original is None:
        row = connection.execute("select original from grapy_ortho_item where mosaic = ? and item = ?",
                                 (mosaic, item)).fetchone()
        original = row[0] if row else json.dumps(model.data)
    elif not isinstance(original, str):
        original = json.dumps(original)
    connection.execute("insert or replace into grapy_ortho_item values (?, ?, 'frame', ?, ?)",
                       (mosaic, item, json.dumps(model.data), original))


def _frame_items(value, tool):
    """(connection, mosaic name, record, {OBJECTID: FrameModel}, items) of a mosaic dataset of Frame Camera items."""
    connection, name, rec, view = _mosaic.open_mosaic(value, tool)
    found = models(connection, name)
    if not found:
        connection.close()
        raise ExecuteError(f"ERROR: {value} holds no Frame Camera items; {tool} works on a mosaic dataset whose items were "
                           "added with the Frame Camera raster type and a frames table (ADR-234 §5.3).")
    where = view.where if view is not None else None
    items = {i["OBJECTID"]: i for i in _mosaic.items(connection, name, where)}
    found = {k: v for k, v in found.items() if k in items}
    return connection, name, rec, found, items


def image(path):
    """An item's image as one grey band: the mean of its bands, as float (INFERRED, ADR-234 §5.9 (g))."""
    import rasterio
    with warnings.catch_warnings():
        warnings.simplefilter("ignore")
        with rasterio.open(path) as source:
            data = source.read().astype(np.float64)
    return data.mean(axis=0)


def sample(array, col, row):
    """Bilinear samples of an array at pixel coordinates from its corner (a pixel's centre at +0.5); NaN outside."""
    x, y = np.asarray(col, float) - 0.5, np.asarray(row, float) - 0.5
    rows, columns = array.shape
    ok = (x >= 0) & (y >= 0) & (x <= columns - 1) & (y <= rows - 1)
    x0 = np.clip(np.floor(x).astype(np.int64), 0, max(columns - 2, 0))
    y0 = np.clip(np.floor(y).astype(np.int64), 0, max(rows - 2, 0))
    fx, fy = x - x0, y - y0
    x1, y1 = np.minimum(x0 + 1, columns - 1), np.minimum(y0 + 1, rows - 1)
    value = (array[y0, x0] * (1 - fx) * (1 - fy) + array[y0, x1] * fx * (1 - fy) + array[y1, x0] * (1 - fx) * fy +
             array[y1, x1] * fx * fy)
    return np.where(ok, value, np.nan)


def nearest(array, col, row):
    x, y = np.floor(np.asarray(col, float)).astype(np.int64), np.floor(np.asarray(row, float)).astype(np.int64)
    rows, columns = array.shape
    ok = (x >= 0) & (y >= 0) & (x < columns) & (y < rows)
    return np.where(ok, array[np.clip(y, 0, rows - 1), np.clip(x, 0, columns - 1)], np.nan)


# ---- adding Frame Camera items (AddRastersToMosaicDataset's Frame Camera raster type)


def _table_rows(path):
    """The rows of a frames or cameras table — a CSV file or a GeoPackage table — as dicts with lower-case keys."""
    text = os.fspath(path) if isinstance(path, os.PathLike) else str(path)
    if text.lower().endswith((".csv", ".txt")):
        with open(text, newline="", encoding="utf-8-sig") as handle:
            return [{k.strip().lower(): (v.strip() if isinstance(v, str) else v) for k, v in row.items() if k}
                    for row in csv.DictReader(handle)]
    from . import da
    from .analysis import _schema
    columns, _, _, _ = _schema(text)
    names = [c.name for c in columns]
    with da.SearchCursor(text, names) as rows:
        return [{n.lower(): v for n, v in zip(names, row)} for row in rows]


def _number(row, key, default=None):
    value = row.get(key)
    if value in (None, ""):
        return default
    return float(value)


def _numbers(row, key):
    value = row.get(key)
    if value in (None, ""):
        return None
    return [float(v) for v in str(value).replace(",", ";").replace(" ", ";").split(";") if v.strip()]


def frame_model(frame, camera, base, srid, tool):
    """The FrameModel of one frames table row, the camera's fields under the frame's own (the frame's take precedence,
    as the frames table page says)."""
    row = dict(camera or {})
    row.update({k: v for k, v in frame.items() if v not in (None, "")})
    for key in ("focallength", "pixelsize"):
        if row.get(key) in (None, ""):
            raise ExecuteError(f"ERROR: the frame {frame.get('raster')} has no {key} in the frames or cameras table "
                               f"({tool}).")
    if str(row.get("orientationtype") or "OPK").upper() == "MATRIX" or (row.get("matrix") and row.get("omega") in (None, "")):
        raise NotBuiltError(f"arcpy.management.{tool}: the Matrix orientation is not built in grapy: the frames table "
                            "page gives its nine values but not their order (ADR-234 §5.4).")
    if str(row.get("applyecc") or "FALSE").upper() in ("TRUE", "1"):
        raise NotBuiltError(f"arcpy.management.{tool}: ApplyECC is not built in grapy: the cameras table page names the "
                            "Earth curvature correction without its formula (ADR-234 §5.4).")
    if row.get("centerlongitude") not in (None, "") or row.get("centerlatitude") not in (None, ""):
        raise NotBuiltError(f"arcpy.management.{tool}: a Local Tangent Plane (CenterLongitude, CenterLatitude) is not "
                            "built in grapy; give the perspective centres in the mosaic dataset's projected system.")
    if str(row.get("distortiontype") or "DistortionModel").lower() == "distortiontable":
        raise NotBuiltError(f"arcpy.management.{tool}: the DistortionTable type is not built in grapy: how distortion is "
                            "read between the tabulated radial distances is not documented (ADR-234 §5.4).")
    fcs = int(float(row.get("fcs") or row.get("filmcoordinatesystem") or 1))
    affine = [_number(row, k) for k in ("a0", "a1", "a2", "b0", "b1", "b2")]
    affine = affine if all(v is not None for v in affine) else None
    if affine and int(float(row.get("affinedirection") or 1)) == -1:
        raise NotBuiltError(f"arcpy.management.{tool}: AffineDirection −1 (film to image) is not built in grapy; give "
                            "the image-to-film affine, the default.")
    if fcs != 1 and not affine:
        raise NotBuiltError(f"arcpy.management.{tool}: film coordinate system {fcs} without an affine is not built in "
                            "grapy: how a rotated scan's rows and columns map to film x and y is not documented.")
    srs = row.get("srs")
    if srs not in (None, "") and srid and str(srs).split(";")[0].strip() not in (str(srid), ""):
        raise NotBuiltError(f"arcpy.management.{tool}: the frame's SRS {srs} differs from the mosaic dataset's "
                            f"EPSG:{srid}; projecting perspective centres is not built in grapy.")
    raster = str(row.get("raster") or "")
    if not raster:
        raise ExecuteError(f"ERROR: a frames table row has no Raster ({tool}).")
    path = raster if os.path.isabs(raster) else os.path.normpath(os.path.join(base, raster))
    import rasterio
    with warnings.catch_warnings():
        warnings.simplefilter("ignore")
        with rasterio.open(path) as source:
            rows, columns, count, dtype = source.height, source.width, source.count, source.dtypes[0]
    data = {"X": _number(row, "perspectivex"), "Y": _number(row, "perspectivey"), "Z": _number(row, "perspectivez"),
            "omega": _number(row, "omega", 0.0), "phi": _number(row, "phi", 0.0), "kappa": _number(row, "kappa", 0.0),
            "focal_length": _number(row, "focallength"), "principal_x": _number(row, "principalx", 0.0),
            "principal_y": _number(row, "principaly", 0.0), "pixel_size": _number(row, "pixelsize"),
            "rows": int(_number(row, "nrows", rows)), "columns": int(_number(row, "ncolumns", columns)),
            "affine": affine, "radial": _numbers(row, "radial"), "tangential": _numbers(row, "tangential"),
            "angle_direction": int(float(row.get("angledirection") or -1)),
            "polarity": int(float(row.get("polarity") or -1)), "average_z": _number(row, "averagez", 0.0),
            "camera_id": str(row.get("cameraid") or ""), "strip": str(row.get("stripname") or ""),
            "prior": {k: _number(row, "prioraccuracy" + k) for k in ("x", "y", "z", "omega", "phi", "kappa")},
            "raster": path, "name": str(row.get("name") or os.path.splitext(os.path.basename(path))[0]),
            "tag": str(row.get("tag") or "Dataset"), "group": str(row.get("groupname") or ""),
            "bands": count, "dtype": dtype}
    if None in (data["X"], data["Y"], data["Z"]):
        raise ExecuteError(f"ERROR: the frame {raster} has no PerspectiveX, PerspectiveY or PerspectiveZ ({tool}).")
    return FrameModel(data)


def footprint(model, z=None):
    """An image's footprint: its corners projected to the height ``z`` (its AverageZ by default)."""
    cols, rows = model.corners()
    xs, ys = model.to_ground(cols, rows, model.average_z if z is None else z)
    return shapely.Polygon(np.column_stack([xs, ys])).normalize()


def add_frames(in_mosaic_dataset, input_path, aux_inputs, duplicates, tool):
    """Frame Camera items from a frames table — see AddRastersToMosaicDataset."""
    from ._mosaictools import _insert, _same, _update
    from .management import _list
    tables = _list(input_path)
    cameras = {}
    for entry in _list(aux_inputs) if _given(aux_inputs) else []:
        parts = str(entry).split(maxsplit=1) if not isinstance(entry, (list, tuple)) else list(entry)
        if len(parts) == 2 and "camera" in str(parts[0]).lower():
            for row in _table_rows(parts[1]):
                cameras[str(row.get("cameraid") or "")] = row
        else:
            _refuse(tool, "aux_inputs", entry, "of a Frame Camera raster type grapy takes only a cameras table, as "
                    "CameraFile <table>")
    connection, mosaic, rec, _ = _mosaic.open_mosaic(in_mosaic_dataset, tool)
    added = []
    try:
        ensure(connection)
        existing = _mosaic.items(connection, mosaic)
        bands, kind = rec.bands, rec.pixel_type
        for table in tables:
            base = os.path.dirname(os.path.abspath(str(table))) if not str(table).lower().count(".gpkg") else \
                os.path.dirname(os.path.abspath(_gpkg.split(str(table))[0]))
            for frame in _table_rows(table):
                model = frame_model(frame, cameras.get(str(frame.get("cameraid") or "")), base, rec.srid, tool)
                d = model.data
                if bands is None:
                    bands = d["bands"]
                    connection.execute("update grapy_mosaic set bands = ? where name = ?", (bands, mosaic))
                elif d["bands"] != bands:
                    _refuse(tool, "input_path", d["raster"], f"it has {d['bands']} bands and the mosaic dataset {bands}")
                if kind is None:
                    kind = next((k for k, v in _mosaic.PIXEL_TYPES.items() if np.dtype(v) == np.dtype(d["dtype"])), None)
                    connection.execute("update grapy_mosaic set pixel_type = ? where name = ?", (kind, mosaic))
                same = [i for i in existing if _same(i.get("Raster"), d["raster"])]
                if same and duplicates == "EXCLUDE_DUPLICATES":
                    continue
                shape = footprint(model)
                cell = model.gsd()
                values = {"Raster": d["raster"], "Name": d["name"], "LowPS": cell, "HighPS": cell, "Category": 1,
                          "Tag": d["tag"], "GroupName": d["group"], "CenterX": model.X, "CenterY": model.Y}
                if same and duplicates == "OVERWRITE_DUPLICATES":
                    oid = same[0]["OBJECTID"]
                    _update(connection, mosaic, rec.srid, oid, shape, values)
                else:
                    oid = _insert(connection, mosaic, rec.srid, shape, values)
                    existing.append({"OBJECTID": oid, "Raster": d["raster"]})
                connection.execute("insert or replace into grapy_mosaic_item (mosaic, item, mtime, nodata) values "
                                   "(?, ?, ?, null)", (mosaic, oid, os.path.getmtime(d["raster"])))
                connection.execute("delete from grapy_ortho_item where mosaic = ? and item = ?", (mosaic, oid))
                save_model(connection, mosaic, oid, model, model.data)
                added.append(oid)
    finally:
        connection.commit()
        connection.close()
    return added


# ---- matching: Harris corners, normalised cross-correlation in object space (equivalent, ADR-224 §5.6)

MATCHING = ("Harris corners matched by normalised cross-correlation in object space",
            "Harris and Stephens 1988; Lewis 1995; Helava 1988")
_SIMILARITY = {"LOW": 0.6, "MEDIUM": 0.7, "HIGH": 0.8}
_DENSITY = {"LOW": 4, "MEDIUM": 8, "HIGH": 16}
_SEARCH = {"MEDIUM": 12, "HIGH": 4}
_HALF = 7


def harris(array, sigma=1.5, k=0.04):
    """The Harris corner response (Harris and Stephens 1988) of an image."""
    from scipy import ndimage
    gy, gx = np.gradient(ndimage.gaussian_filter(array, 1.0))
    a = ndimage.gaussian_filter(gx * gx, sigma)
    b = ndimage.gaussian_filter(gy * gy, sigma)
    c = ndimage.gaussian_filter(gx * gy, sigma)
    return a * b - c * c - k * (a + b) ** 2


def _ncc(template, window):
    """Normalised cross-correlation (Lewis 1995) of a template at every position in a larger window."""
    from numpy.lib.stride_tricks import sliding_window_view
    views = sliding_window_view(window, template.shape)
    t = template - template.mean()
    tn = math.sqrt(float((t * t).sum()))
    v = views - views.mean(axis=(-1, -2), keepdims=True)
    vn = np.sqrt((v * v).sum(axis=(-1, -2)))
    with np.errstate(invalid="ignore", divide="ignore"):
        return (v * t).sum(axis=(-1, -2)) / (vn * tn)


def _peak(score):
    """The best position of a correlation surface with a parabola's sub-cell offset on each axis."""
    if not np.isfinite(score).any():
        return None
    i, j = np.unravel_index(int(np.nanargmax(score)), score.shape)

    def refine(a, b, c):
        d = a - 2 * b + c
        return 0.0 if not np.isfinite(d) or d >= 0 else 0.5 * (a - c) / d

    di = refine(score[i - 1, j], score[i, j], score[i + 1, j]) if 0 < i < score.shape[0] - 1 else 0.0
    dj = refine(score[i, j - 1], score[i, j], score[i, j + 1]) if 0 < j < score.shape[1] - 1 else 0.0
    return i + di, j + dj, float(score[i, j])


class Sampler:
    """Image values at ground points: a Frame Camera item through its model, or a georeferenced raster."""

    def __init__(self, array, model=None, grid=None):
        self.array, self.model, self.grid = array, model, grid

    def __call__(self, X, Y, Z):
        if self.model is not None:
            col, row = self.model.project(X, Y, Z)
        else:
            g = self.grid
            col, row = (np.asarray(X) - g.left) / g.width, (g.top - np.asarray(Y)) / g.height
        return sample(self.array, col, row)


def match(a, b, X, Y, Z, cell, search, threshold):
    """The ground offset at which ``b`` best matches ``a`` around (X, Y, Z): both sampled on a ground grid of ``cell``
    — object-space correlation (Helava 1988) — and correlated (Lewis 1995). (dX, dY, score) or None."""
    h, r = _HALF, search
    offsets = (np.arange(-h, h + 1)) * cell
    gx, gy = np.meshgrid(X + offsets, Y - offsets)
    template = a(gx, gy, np.full(gx.shape, Z))
    if np.isnan(template).any() or template.std() < 1e-6:
        return None
    wide = (np.arange(-h - r, h + r + 1)) * cell
    wx, wy = np.meshgrid(X + wide, Y - wide)
    window = b(wx, wy, np.full(wx.shape, Z))
    if np.isnan(window).any():
        return None
    found = _peak(_ncc(template, window))
    if found is None or found[2] < threshold:
        return None
    i, j, score = found
    return (j - r) * cell, -(i - r) * cell, score


def intersect(models, observations):
    """The ground point closest to the rays of several images' observations (least squares, Wolf et al. §11.10)."""
    a, rhs = np.zeros((3, 3)), np.zeros(3)
    for model, (col, row) in zip(models, observations):
        d = model.ray(np.array([col]), np.array([row]))[:, 0]
        d = d / np.linalg.norm(d)
        p = np.eye(3) - np.outer(d, d)
        a += p
        rhs += p @ np.array([model.X, model.Y, model.Z])
    return np.linalg.solve(a, rhs)


def corners_in(model, array, region, z, n, distribution, response):
    """Candidate pixels of one image inside a ground region: its strongest Harris corner in each of n × n cells of the
    region (REGULAR), or its n × n strongest corners anywhere in it (RANDOM — INFERRED: deterministic, the page's
    "random" describing where they fall, not a draw)."""
    margin = _HALF + 2
    rows, cols = np.mgrid[margin:array.shape[0] - margin:2, margin:array.shape[1] - margin:2]
    rows, cols = rows.ravel() + 0.5, cols.ravel() + 0.5
    xs, ys = model.to_ground(cols, rows, z)
    inside = shapely.contains_xy(region, xs, ys)
    strength = response[(rows - 0.5).astype(int), (cols - 0.5).astype(int)]
    keep = inside & (strength > 0)
    cols, rows, xs, ys, strength = cols[keep], rows[keep], xs[keep], ys[keep], strength[keep]
    if not len(cols):
        return []
    if distribution == "REGULAR":
        x0, y0, x1, y1 = region.bounds
        ix = np.minimum(((xs - x0) / max(x1 - x0, 1e-9) * n).astype(int), n - 1)
        iy = np.minimum(((ys - y0) / max(y1 - y0, 1e-9) * n).astype(int), n - 1)
        best = {}
        for k in range(len(cols)):
            key = (ix[k], iy[k])
            if key not in best or strength[k] > strength[best[key]]:
                best[key] = k
        chosen = sorted(best.values())
    else:
        chosen = list(np.argsort(-strength, kind="stable")[: n * n])
    return [(cols[k], rows[k], xs[k], ys[k]) for k in chosen]


# ---- control point feature classes


def create_points(path, srid):
    from .analysis import _out
    out = _out(path)
    _gpkg.create(out, "Point", srid, [_gpkg.Column(n, t) for n, t in CONTROL_FIELDS], True, False)
    return out


def write_points(path, rows):
    """Rows of (PointID, Type, Status, ImageID, ImageX, ImageY, XYAccuracy, ZAccuracy, Tag, (X, Y, Z))."""
    from . import da
    with da.InsertCursor(path, [n for n, _ in CONTROL_FIELDS] + ["SHAPE@"]) as written:
        for row in rows:
            written.insertRow([*row[:-1], shapely.Point(*row[-1])])


def read_points(path):
    """[dict] of a control point feature class's rows; a point class without grapy's fields reads as ground-only GCPs."""
    from . import da
    from .analysis import _schema
    columns, kind, _, _ = _schema(path)
    if kind != "Point":
        raise ExecuteError(f"ERROR: {path} is {kind}; control points are a point feature class.")
    names = {c.name.lower(): c.name for c in columns}
    wanted = [names[n.lower()] for n, _ in CONTROL_FIELDS if n.lower() in names]
    out = []
    with da.SearchCursor(path, ["OID@", *wanted, "SHAPE@"]) as rows:
        for row in rows:
            values = {n: v for n, v in zip([w for w in wanted], row[1:-1])}
            shape = row[-1]._shape if row[-1] is not None else None
            coords = shapely.get_coordinates(shape, include_z=True)[0] if shape is not None else (np.nan,) * 3
            record = {k: values.get(names.get(k.lower(), k)) for k, _ in CONTROL_FIELDS}
            record["OID"] = row[0]
            record["xyz"] = tuple(float(v) for v in coords)
            if "pointid" not in names:
                record.update({"PointID": row[0], "Type": GCP, "Status": 1, "ImageID": None})
            out.append(record)
    return out


def points_by_id(records):
    found: dict = {}
    for r in records:
        found.setdefault(int(r["PointID"]), []).append(r)
    return found
