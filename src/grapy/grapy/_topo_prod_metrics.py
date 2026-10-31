"""CalculateMetrics and MGRS for arcpy.topographic (ADR-251).

MGRS follows NGA.STND.0037 (*Universal Grids and Grid Reference Systems*, 2014) §§ on the UTM part of the Military Grid
Reference System: the grid zone designation (UTM zone and 8° latitude band, X 12°, with the Norway and Svalbard
exceptions), the 100 km square letters (column letters cycling every three zones, A–H, J–R, S–Z; row letters A–V
without I and O, offset by five in even zones), and the easting and northing within the square truncated to the
precision. The polar UPS areas are refused by value.
"""
from __future__ import annotations

import math


from . import _topo_prod as tp
from ._result import Result
from ._runtime import AddMessage, ExecuteError, NotBuiltError

_BANDS = "CDEFGHJKLMNPQRSTUVWX"
_COLUMNS = ("ABCDEFGH", "JKLMNPQR", "STUVWXYZ")
_ROWS = "ABCDEFGHJKLMNPQRSTUV"


def utm_zone(latitude, longitude):
    zone = int((longitude + 180) // 6) + 1
    zone = min(zone, 60)
    if 56 <= latitude < 64 and 3 <= longitude < 12:
        zone = 32
    if 72 <= latitude < 84:
        for low, high, z in ((0, 9, 31), (9, 21, 33), (21, 33, 35), (33, 42, 37)):
            if low <= longitude < high:
                zone = z
    return zone


def mgrs(latitude, longitude, digits):
    """An MGRS reference: digits None for the grid zone designation alone, 0 for the 100 km square, 1–5 for 10 km to
    1 m."""
    from pyproj import Transformer
    if not -80 <= latitude < 84:
        raise NotBuiltError(f"arcpy.topographic.CalculateMetrics: MGRS at latitude {latitude:.4f} lies in the polar UPS "
                            "area, which grapy does not encode.")
    zone = utm_zone(latitude, longitude)
    band = _BANDS[min(int((latitude + 80) // 8), 19)]
    gzd = f"{zone}{band}"
    if digits is None:
        return gzd
    code = (32600 if latitude >= 0 else 32700) + zone
    easting, northing = Transformer.from_crs(4326, code, always_xy=True).transform(longitude, latitude)
    column = _COLUMNS[(zone - 1) % 3][int(easting // 100000) - 1]
    row = _ROWS[(int(northing // 100000) + (5 if zone % 2 == 0 else 0)) % 20]
    if digits == 0:
        return f"{gzd}{column}{row}"
    scale = 10 ** (5 - digits)
    e = int((easting % 100000) // scale)
    n = int((northing % 100000) // scale)
    return f"{gzd}{column}{row}{e:0{digits}d}{n:0{digits}d}"


_PRECISION = {"6X8 (4Q)": None, "100KM (4QFJ)": 0, "10KM (4QFJ16)": 1, "1KM (4QFJ1267)": 2, "100M (4QFJ123678)": 3,
              "10M (4QFJ12346789)": 4, "1M (4QFJ1234567890)": 5}
_DEFAULT_FIELDS = {"LENGTH": "LEG;LEN;LEN_;LGN;LZN", "WIDTH": "WID;WID_;WGP", "AREA": "ARA;ARE;ARE_",
                   "ANGLE_OF_ORIENTATION": "AOO;DOF;FEO", "ELEVATION": "ZV2;ZVH", "MGRS": "MGRSValue;MGRS"}
_APPLIES = {"Point": {"ELEVATION", "MGRS", "ANGLE_OF_ORIENTATION"}, "Multipoint": {"ELEVATION", "MGRS"},
            "Polyline": {"LENGTH", "ELEVATION", "MGRS"},
            "Polygon": {"LENGTH", "WIDTH", "AREA", "ANGLE_OF_ORIENTATION", "ELEVATION", "MGRS"}}


def _metric_frame(shape, srid):
    """The shape in metres: as it is on projected data (scaled by the unit), in its UTM zone on geographic data."""
    import numpy as np
    import shapely
    from pyproj import Transformer
    per = tp.per_unit(srid)
    if per is not None:
        return shape if per == 1 else shapely.transform(shape, lambda xy: xy * per)
    c = shape.representative_point()
    to_lonlat = Transformer.from_crs(srid, 4326, always_xy=True)
    lon, lat = to_lonlat.transform(c.x, c.y)
    code = (32600 if lat >= 0 else 32700) + utm_zone(lat, lon)
    move = Transformer.from_crs(srid, code, always_xy=True)
    return shapely.transform(shape, lambda xy: np.column_stack(move.transform(xy[:, 0], xy[:, 1])))


def rectangle(shape):
    """(length, width, azimuth of the long side clockwise from north in [0, 180)) of the minimum-area bounding
    rectangle."""
    box = shape.minimum_rotated_rectangle
    if box.geom_type != "Polygon":
        return shape.length, 0.0, 0.0
    c = list(box.exterior.coords)
    sides = [(math.dist(c[i], c[i + 1]), c[i], c[i + 1]) for i in range(2)]
    sides.sort(key=lambda s: -s[0])
    (long_side, a, b), (short_side, _, _) = sides
    azimuth = math.degrees(math.atan2(b[0] - a[0], b[1] - a[1])) % 180
    return long_side, short_side, azimuth


def _raster_at(raster, x, y):
    cell = raster._grid.cell_of(x, y)
    if cell is None or raster._mask[cell]:
        return None
    return float(raster._array[cell])


def _downhill(raster, x, y):
    """The downhill direction (degrees clockwise from north) at a point: the aspect by Horn's (1981) third-order
    finite difference over the 3×3 cells around it."""
    cell = raster._grid.cell_of(x, y)
    if cell is None:
        return None
    r, c = cell
    a = raster._array
    if not (1 <= r < a.shape[0] - 1 and 1 <= c < a.shape[1] - 1) or raster._mask[r - 1:r + 2, c - 1:c + 2].any():
        return None
    z = a[r - 1:r + 2, c - 1:c + 2].astype(float)
    w, h = raster._grid.width, raster._grid.height
    dzdx = ((z[0, 2] + 2 * z[1, 2] + z[2, 2]) - (z[0, 0] + 2 * z[1, 0] + z[2, 0])) / (8 * w)
    dzdy = ((z[0, 0] + 2 * z[0, 1] + z[0, 2]) - (z[2, 0] + 2 * z[2, 1] + z[2, 2])) / (8 * h)  # row 0 is north
    if dzdx == 0 and dzdy == 0:
        return None
    return math.degrees(math.atan2(-dzdx, -dzdy)) % 360


def calculate_metrics(in_features, in_metric_types, in_length_attributes, in_width_attributes, in_area_attributes,
                      in_angle_attributes, in_elevation_attributes, in_precision, mgrs_attributes, mgrs_precision,
                      elevation_raster, only_populate_null_values):
    import shapely
    from pyproj import Transformer
    tool = "CalculateMetrics"
    metrics = [m.upper() for m in tp.listed(in_metric_types)]
    for m in metrics:
        if m not in _DEFAULT_FIELDS:
            raise ExecuteError(f"ERROR 000800: {tool}: '{m}' is not a metric type.")
    path = tp.dataset(in_features)
    _, kind, srid, _ = tp.schema(path)
    nulls_only = tp.flag(tool, only_populate_null_values, "ONLYPOPNULLVALS", "NO_ONLYPOPNULLVALS",
                         "NO_ONLYPOPNULLVALS")
    given_fields = {"LENGTH": in_length_attributes, "WIDTH": in_width_attributes, "AREA": in_area_attributes,
                    "ANGLE_OF_ORIENTATION": in_angle_attributes, "ELEVATION": in_elevation_attributes,
                    "MGRS": mgrs_attributes}
    fields = {}
    for m in metrics:
        if m not in _APPLIES.get(kind, set()):
            continue
        if m == "ANGLE_OF_ORIENTATION" and kind == "Point" and not tp.given(elevation_raster):
            continue
        wanted = tp.listed(given_fields[m]) if tp.given(given_fields[m]) else tp.listed(_DEFAULT_FIELDS[m])
        present = [tp.find_field(path, f) for f in wanted]
        fields[m] = [f for f in present if f]   # the page: only the fields present are computed
    digits = None
    if "MGRS" in fields and fields["MGRS"]:
        chosen = tp.word(mgrs_precision, "IMAGE CITY MAP (1234)").replace("  ", " ")
        if chosen.startswith("IMAGE CITY MAP"):
            raise NotBuiltError("arcpy.topographic.CalculateMetrics: mgrs_precision 'Image City Map (1234)' is not built "
                                "in grapy — the Image City Map's column and row grid is not on the page or in a "
                                "published standard.")
        if chosen not in _PRECISION:
            raise ExecuteError(f"ERROR 000800: {tool}: '{mgrs_precision}' is not an MGRS precision.")
        digits = _PRECISION[chosen]
    decimals = int(tp.number(in_precision)) if tp.given(in_precision) else None
    raster = None
    if tp.given(elevation_raster):
        from ._raster import as_raster
        raster = as_raster(elevation_raster, tool)
    to_lonlat = Transformer.from_crs(srid, 4326, always_xy=True) if srid > 0 and "MGRS" in fields else None
    if "MGRS" in fields and srid <= 0:
        raise ExecuteError(f"ERROR 000622: {tool}: MGRS needs a coordinate system.")
    names = sorted({f for fs in fields.values() for f in fs})
    changes = {}

    def rounded(v):
        return round(v, decimals) if decimals is not None and v is not None else v

    for row in tp.read(in_features, fields=names):
        shape = row.shape
        if shape is None or shape.is_empty:
            continue
        values = {}
        frame = _metric_frame(shape, srid) if srid > 0 else shape
        if kind == "Polygon" and ({"LENGTH", "WIDTH", "ANGLE_OF_ORIENTATION"} & set(fields)):
            length, width, azimuth = rectangle(frame)
        if "LENGTH" in fields:
            v = frame.length if kind == "Polyline" else length
            values.update({f: rounded(v) for f in fields["LENGTH"]})
        if "WIDTH" in fields:
            values.update({f: rounded(width) for f in fields["WIDTH"]})
        if "AREA" in fields:
            values.update({f: rounded(frame.area) for f in fields["AREA"]})
        if "ANGLE_OF_ORIENTATION" in fields:
            v = azimuth if kind == "Polygon" else _downhill(raster, shape.x, shape.y)
            values.update({f: rounded(v) for f in fields["ANGLE_OF_ORIENTATION"]})
        if "ELEVATION" in fields:
            if raster is not None:
                found = [_raster_at(raster, x, y) for x, y in shapely.get_coordinates(shape)]
                found = [v for v in found if v is not None]
                v = max(found) if found else None
            elif shapely.has_z(shape):
                v = float(max(shapely.get_coordinates(shape, include_z=True)[:, 2]))
            else:
                v = None
            values.update({f: rounded(v) for f in fields["ELEVATION"]})
        if "MGRS" in fields:
            point = shape if shape.geom_type == "Point" else shape.representative_point()
            lon, lat = to_lonlat.transform(point.x, point.y)
            values.update({f: mgrs(lat, lon, digits) for f in fields["MGRS"]})
        if nulls_only:
            values = {f: v for f, v in values.items() if row[f] is None}
        if values:
            changes[row.oid] = values
    tp.update(path, changes)
    AddMessage(f"{tool}: {len(changes)} features measured.")
    return Result(tp.text(in_features))
