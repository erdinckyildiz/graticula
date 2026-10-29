"""The files a run's layers cross in and out as — ADR-223 §5.5.

The server writes each layer input as GeoJSON (RFC 7946, so WGS 84) into the run's directory; the runner turns it into
the GeoPackage feature class the tool is handed. A tool's feature class outputs go back the same way: GeoPackage to
GeoJSON in WGS 84, which the server reads with the parser every upload goes through.
"""
from __future__ import annotations

import json

import shapely
from shapely.geometry import mapping, shape

from . import _gpkg

_KINDS = {"Point": "Point", "MultiPoint": "Multipoint", "LineString": "Polyline", "MultiLineString": "Polyline",
          "Polygon": "Polygon", "MultiPolygon": "Polygon"}


def _type_of(values):
    present = [v for v in values if v is not None]
    if present and all(isinstance(v, bool) for v in present):
        return "SmallInteger"
    if present and all(isinstance(v, int) and not isinstance(v, bool) for v in present):
        return "Integer"
    if present and all(isinstance(v, (int, float)) and not isinstance(v, bool) for v in present):
        return "Double"
    return "String"


def geojson_to_gpkg(source: str, target: str) -> None:
    with open(source, encoding="utf-8") as read:
        collection = json.load(read)
    features = collection.get("features", [])
    names: list[str] = []
    for feature in features:
        for name in (feature.get("properties") or {}):
            if name not in names:
                names.append(name)
    columns = [_gpkg.Column(n, _type_of([(f.get("properties") or {}).get(n) for f in features])) for n in names]
    kinds = {_KINDS.get((f.get("geometry") or {}).get("type")) for f in features} - {None}
    kind = kinds.pop() if len(kinds) == 1 else "Geometry"
    _gpkg.create(target, kind, 4326, columns)
    connection, table = _gpkg.table_of(target)
    try:
        quoted = ", ".join(['"geom"', *[f'"{c.name}"' for c in columns]])
        marks = ", ".join("?" for _ in range(len(columns) + 1))
        for feature in features:
            geometry = shape(feature["geometry"]) if feature.get("geometry") else None
            if geometry is not None and kind in ("Polyline", "Polygon") and not geometry.geom_type.startswith("Multi"):
                geometry = {"Polyline": shapely.MultiLineString, "Polygon": shapely.MultiPolygon}[kind]([geometry])
            values = [(feature.get("properties") or {}).get(c.name) for c in columns]
            values = [json.dumps(v) if isinstance(v, (dict, list)) else v for v in values]
            connection.execute(f'insert into "{table}" ({quoted}) values ({marks})', [_gpkg.encode(geometry, 4326), *values])
        connection.commit()
    finally:
        connection.close()


_RESERVED = {"objectid", "geom", "fid", "globalid"}


def _outgoing(names):
    """The fields' names as the server may import them: one it gives every row itself is renamed, as ArcGIS renames a clash."""
    taken = {n.lower() for n in names}
    renamed = []
    for name in names:
        if name.lower() in _RESERVED:
            candidate, i = f"{name}_1", 1
            while candidate.lower() in taken:
                i += 1
                candidate = f"{name}_{i}"
            taken.add(candidate.lower())
            name = candidate
        renamed.append(name)
    return renamed


def gpkg_to_geojson(source: str, target: str) -> int:
    """A feature class as GeoJSON in WGS 84; answers how many features were written."""
    from . import da
    from ._reference import SpatialReference

    connection, table = _gpkg.table_of(source)
    columns, _, srid, _ = _gpkg.describe(connection, table)
    connection.close()
    names = [c.name for c in columns]
    outgoing = _outgoing(names)
    wgs84 = SpatialReference(4326)
    features = []
    with da.SearchCursor(source, [*names, "SHAPE@"]) as rows:
        for row in rows:
            geometry = row[-1]
            if geometry is not None and srid > 0 and srid != 4326:
                geometry = geometry.projectAs(wgs84)
            features.append({"type": "Feature", "properties": dict(zip(outgoing, row[:-1])),
                             "geometry": mapping(geometry._shape) if geometry is not None else None})
    with open(target, "w", encoding="utf-8") as out:
        json.dump({"type": "FeatureCollection", "features": features}, out, default=str)
    return len(features)


_KINDS_OF_SAMPLES = {"Unsigned8": "uint8", "Signed16": "int16", "Unsigned16": "uint16", "Signed32": "int32",
                     "Real32": "float32", "Real64": "float64"}


def raster_from_raw(header_path: str, target: str) -> None:
    """An image service's band the server wrote as raw doubles beside a header, as the GeoTIFF a tool opens
    (ADR-224 condition 2). Cells holding the band's NoData value stay NoData."""
    import numpy as np
    import os
    import rasterio
    from rasterio.transform import from_origin

    with open(header_path, encoding="utf-8") as read:
        header = json.load(read)
    raw = np.fromfile(os.path.join(os.path.dirname(os.path.dirname(header_path)), header["data"]), dtype="<f8")
    array = raw.reshape(header["height"], header["width"])
    kind = _KINDS_OF_SAMPLES.get(header.get("kind"), "float64")
    nodata = header.get("nodata")
    profile = {"driver": "GTiff", "height": header["height"], "width": header["width"], "count": 1, "dtype": kind,
               "transform": from_origin(header["left"], header["top"], header["cellWidth"], header["cellHeight"])}
    if header.get("srid"):
        profile["crs"] = f"EPSG:{header['srid']}"
    if nodata is not None:
        profile["nodata"] = nodata
    with rasterio.open(target, "w", **profile) as out:
        out.write(array.astype(kind), 1)
