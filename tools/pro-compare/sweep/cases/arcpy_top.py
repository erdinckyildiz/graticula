"""The top-level arcpy names (functions and classes) against ArcGIS Pro 3.7.

Most of these answer values rather than write datasets, so a case returns ``flat(...)``: a flat list alternating a
label and a scalar, which compare.py checks element by element (numbers within 1e-6). Values a case cannot compare
across the two formats (a path, a timestamp) are left out, each with a comment saying why.
"""
import math
import os
import re
import sys

CASES = {}


def case(name):
    def register(function):
        CASES[name] = function
        return function
    return register


def _scalar(v):
    if v is None or isinstance(v, (bool, int, float, str)):
        return v
    return str(v)


def flat(pairs):
    """[label, value, …] from a dict or a list of pairs; nested lists, tuples and dicts become label[i] / label.key."""
    out = []

    def add(label, value):
        if isinstance(value, dict):
            for k in value:
                add(f"{label}.{k}", value[k])
        elif isinstance(value, (list, tuple)):
            out.extend([f"{label}#len", len(value)])
            for i, v in enumerate(value):
                add(f"{label}[{i}]", v)
        else:
            out.extend([label, _scalar(value)])

    for label, value in (pairs.items() if isinstance(pairs, dict) else pairs):
        add(label, value)
    return out


def _try(function, *args, **kwargs):
    """A call's answer, or the type of what it raised — for checks where an error is itself the answer."""
    try:
        return function(*args, **kwargs)
    except Exception as error:  # noqa: BLE001
        return f"raised {type(error).__name__}"


def _r(v, n=6):
    return None if v is None else round(float(v), n)


def _bufratio(area, exact):
    """A buffer's area over the exact area of its true curves, to 0.5 %: Pro's buffers carry true arcs and grapy's
    densify them, which the sweep's polygon tolerance (0.5 % of the area) allows."""
    return round(area / exact / 0.005) * 0.005


def _pt(p):
    return None if p is None else [_r(p.X, 4), _r(p.Y, 4)]


def _box(ap, I, x0, y0, x1, y1):
    X0, Y0 = I["x0"], I["y0"]
    a = ap.Array([ap.Point(X0 + x0, Y0 + y0), ap.Point(X0 + x0, Y0 + y1), ap.Point(X0 + x1, Y0 + y1),
                  ap.Point(X0 + x1, Y0 + y0), ap.Point(X0 + x0, Y0 + y0)])
    return ap.Polygon(a, ap.SpatialReference(I["srid"]))


def _line(ap, I, coords):
    X0, Y0 = I["x0"], I["y0"]
    return ap.Polyline(ap.Array([ap.Point(X0 + x, Y0 + y) for x, y in coords]), ap.SpatialReference(I["srid"]))


def _geom_common(g):
    e = g.extent
    return {"type": g.type, "isMultipart": g.isMultipart, "partCount": g.partCount, "pointCount": g.pointCount,
            "area": _r(g.area, 4), "length": _r(g.length, 4), "centroid": _pt(g.centroid),
            "trueCentroid": _pt(g.trueCentroid), "firstPoint": _pt(g.firstPoint), "lastPoint": _pt(g.lastPoint),
            "extent": [_r(e.XMin, 4), _r(e.YMin, 4), _r(e.XMax, 4), _r(e.YMax, 4)], "hasCurves": g.hasCurves,
            "srid": g.spatialReference.factoryCode if g.spatialReference else None}


# ---- geometry -----------------------------------------------------------------------------------------------------


@case("arcpy.Point")
def _(ap, I, O):
    p = ap.Point(1.5, 2.5, 3.5, 4.5, 7)
    q = ap.Point()
    return flat({"p": [p.X, p.Y, p.Z, p.M, p.ID], "empty": [q.X, q.Y, q.Z, q.M, q.ID],
                 "equals": p.equals(ap.Point(1.5, 2.5, 3.5, 4.5)), "within": ap.Point(1, 1).within(
                     ap.Extent(0, 0, 2, 2)), "contains": ap.Point(1, 1).contains(ap.Point(1, 1)),
                 "disjoint": ap.Point(1, 1).disjoint(ap.Point(2, 2))})


@case("arcpy.PointGeometry")
def _(ap, I, O):
    sr = ap.SpatialReference(I["srid"])
    a = ap.PointGeometry(ap.Point(I["x0"] + 100, I["y0"] + 100), sr)
    b = ap.PointGeometry(ap.Point(I["x0"] + 130, I["y0"] + 140), sr)
    wgs = a.projectAs(ap.SpatialReference(4326))
    angle, distance = a.angleAndDistanceTo(b, "PLANAR")
    moved = a.pointFromAngleAndDistance(90, 50, "PLANAR")
    return flat({"common": _geom_common(a), "WKT": a.WKT, "distanceTo": _r(a.distanceTo(b), 6),
                 "buffer.area": _bufratio(a.buffer(10).area, math.pi * 100), "projected": [_r(wgs.firstPoint.X, 7),
                                                                       _r(wgs.firstPoint.Y, 7)],
                 "angleAndDistance": [_r(angle, 6), _r(distance, 6)], "pointFromAngle": _pt(moved.firstPoint),
                 "equals": a.equals(ap.PointGeometry(ap.Point(I["x0"] + 100, I["y0"] + 100), sr)),
                 "disjoint": a.disjoint(b)})


@case("arcpy.Polyline")
def _(ap, I, O):
    line = _line(ap, I, [(0, 0), (300, 0), (300, 400)])
    other = _line(ap, I, [(150, -50), (150, 50)])
    seg = line.segmentAlongLine(100, 400)
    pos = line.positionAlongLine(0.5, True)
    q = line.queryPointAndDistance(ap.PointGeometry(ap.Point(I["x0"] + 320, I["y0"] + 100), line.spatialReference))
    cut = line.cut(_line(ap, I, [(200, -10), (200, 10)]))
    dens = line.densify("DISTANCE", 50)
    return flat({"common": _geom_common(line), "WKT": line.WKT, "segment": [_r(seg.length, 4), _pt(seg.firstPoint),
                 _pt(seg.lastPoint)], "position": _pt(pos.firstPoint),
                 "measureOnLine": _r(line.measureOnLine(ap.Point(I["x0"] + 300, I["y0"] + 100)), 4),
                 "query": [_pt(q[0].firstPoint), _r(q[1], 4), _r(q[2], 4), q[3]],
                 "crosses": line.crosses(other), "intersect": _r(line.intersect(other, 1).firstPoint.X - I["x0"], 4),
                 "cut": sorted(_r(c.length, 4) for c in cut), "densify.pointCount": dens.pointCount,
                 "generalize.pointCount": _line(ap, I, [(0, 0), (100, 1), (200, 0)]).generalize(5).pointCount,
                 "buffer.area": _bufratio(other.buffer(10).area, 2000 + math.pi * 100), "boundary": _r(line.boundary().pointCount, 0),
                 "getPart.count": line.getPart(0).count})


@case("arcpy.Polygon")
def _(ap, I, O):
    a = _box(ap, I, 0, 0, 200, 200)
    b = _box(ap, I, 100, 100, 300, 300)
    tri = ap.Polygon(ap.Array([ap.Point(I["x0"], I["y0"]), ap.Point(I["x0"] + 100, I["y0"] + 300),
                               ap.Point(I["x0"] + 200, I["y0"])]), a.spatialReference)
    cut = a.cut(_line(ap, I, [(50, -10), (50, 210)]))
    return flat({"common": _geom_common(a), "WKT": a.WKT, "tri.WKT": tri.WKT, "tri.labelPoint": _pt(tri.labelPoint),
                 "intersect": _r(a.intersect(b, 4).area, 4), "union": _r(a.union(b).area, 4),
                 "difference": _r(a.difference(b).area, 4), "symdiff": _r(a.symmetricDifference(b).area, 4),
                 "overlaps": a.overlaps(b), "contains": a.contains(ap.Point(I["x0"] + 10, I["y0"] + 10)),
                 "touches": a.touches(_box(ap, I, 200, 0, 300, 100)), "within": _box(ap, I, 10, 10, 20, 20).within(a),
                 "convexHull": _r(tri.convexHull().area, 4), "boundary.length": _r(a.boundary().length, 4),
                 "boundary.type": a.boundary().type, "distanceTo": _r(a.distanceTo(_box(ap, I, 250, 0, 260, 10)), 4),
                 "cut": sorted(_r(c.area, 4) for c in cut), "clip": _r(a.clip(ap.Extent(
                     I["x0"] + 50, I["y0"] + 50, I["x0"] + 500, I["y0"] + 500)).area, 4),
                 "buffer.area": _bufratio(a.buffer(10).area, 40000 + 8000 + math.pi * 100), "buffer-neg": _r(a.buffer(-10).area, 4),
                 "hullRectangle": [round(float(v), 4) for v in a.hullRectangle.split()],
                 "projectAs.area": _r(a.projectAs(ap.SpatialReference(4326)).area, 12),
                 "getArea.geodesic": _r(a.getArea("GEODESIC", "SQUAREMETERS"), 0),
                 "getLength.geodesic": _r(a.getLength("GEODESIC", "METERS"), 1)})


@case("arcpy.Multipoint")
def _(ap, I, O):
    sr = ap.SpatialReference(I["srid"])
    m = ap.Multipoint(ap.Array([ap.Point(I["x0"] + x, I["y0"] + y) for x, y in ((0, 0), (100, 0), (100, 50),
                                                                                  (40, 80))]), sr)
    return flat({"common": _geom_common(m), "WKT": m.WKT, "convexHull": _r(m.convexHull().area, 4),
                 "buffer.area": _bufratio(m.buffer(1).area, 4 * math.pi), "getPart.X": _r(m.getPart(2).X - I["x0"], 4)})


@case("arcpy.Geometry")
def _(ap, I, O):
    sr = ap.SpatialReference(I["srid"])
    g = ap.Geometry("polyline", ap.Array([ap.Point(I["x0"], I["y0"]), ap.Point(I["x0"] + 30, I["y0"] + 40)]), sr)
    copied = ap.management.CopyFeatures(I["polys"], ap.Geometry())
    return flat({"type": g.type, "length": _r(g.length, 6), "copied.count": len(copied),
                 "copied.areas": sorted(_r(c.area, 4) for c in copied), "copied.type": copied[0].type})


@case("arcpy.Array")
def _(ap, I, O):
    a = ap.Array([ap.Point(1, 1), ap.Point(2, 2)])
    a.add(ap.Point(3, 3))
    a.append(ap.Point(4, 4))
    a.insert(0, ap.Point(0, 0))
    a.replace(1, ap.Point(10, 10))
    a.remove(4)
    first = a.next().X
    nested = ap.Array([ap.Array([ap.Point(0, 0), ap.Point(1, 1)]), ap.Array([ap.Point(5, 5)])])
    return flat({"count": a.count, "xs": [p.X for p in a], "getObject": a.getObject(2).X, "first": first,
                 "nested": [nested.count, nested.getObject(0).count],
                 "removeAll": (a.removeAll(), a.count)[1]})


@case("arcpy.Extent")
def _(ap, I, O):
    e = ap.Extent(0, 0, 100, 50)
    f = ap.Extent(50, 25, 150, 75)
    sr = ap.SpatialReference(I["srid"])
    g = ap.Extent(I["x0"], I["y0"], I["x0"] + 600, I["y0"] + 600, None, None, None, None, sr)
    p = g.projectAs(ap.SpatialReference(4326))
    return flat({"xy": [e.XMin, e.YMin, e.XMax, e.YMax], "wh": [e.width, e.height], "lowerLeft": _pt(e.lowerLeft),
                 "upperRight": _pt(e.upperRight), "contains": e.contains(ap.Point(10, 10)), "overlaps": e.overlaps(f),
                 "disjoint": e.disjoint(ap.Extent(200, 200, 300, 300)), "within": ap.Extent(1, 1, 2, 2).within(e),
                 "polygon.area": e.polygon.area, "projected": [_r(p.XMin, 7), _r(p.YMin, 7), _r(p.XMax, 7),
                                                              _r(p.YMax, 7)], "Z": [e.ZMin, e.ZMax], "M": [e.MMin, e.MMax],
                 "str": str(e)})


@case("arcpy.SpatialReference")
def _(ap, I, O):
    u = ap.SpatialReference(I["srid"])
    g = ap.SpatialReference(4326)
    return flat({"utm": {k: getattr(u, k) for k in (
        "name", "factoryCode", "type", "PCSCode", "PCSName", "GCSCode", "GCSName", "linearUnitName", "linearUnitCode",
        "metersPerUnit", "falseEasting", "falseNorthing", "centralMeridian", "scaleFactor", "latitudeOfOrigin",
        "projectionName", "datumName", "spheroidName", "semiMajorAxis", "flattening", "XYTolerance", "XYResolution",
        "ZTolerance", "MTolerance", "isHighPrecision", "hasXYPrecision")}, "utm.GCS.name": u.GCS.name,
        "wgs": {k: getattr(g, k) for k in ("name", "factoryCode", "type", "angularUnitName", "angularUnitCode",
                                            "radiansPerUnit", "datumName", "datumCode", "spheroidName", "GCSCode",
                                            "primeMeridianName", "XYTolerance", "XYResolution")},
        "byName": ap.SpatialReference("WGS 1984 Web Mercator (auxiliary sphere)").factoryCode,
        "wkt.starts": u.exportToString()[:30]})


@case("arcpy.AsShape")
def _(ap, I, O):
    g = ap.AsShape({"type": "Polygon", "coordinates": [[[0, 0], [0, 10], [10, 10], [10, 0], [0, 0]]]})
    e = ap.AsShape({"x": 5, "y": 6, "spatialReference": {"wkid": 4326}}, True)
    line = ap.AsShape({"type": "LineString", "coordinates": [[0, 0], [3, 4]]})
    return flat({"poly": [g.type, g.area, g.WKT], "esri": [e.type, e.firstPoint.X, e.firstPoint.Y,
                                                           e.spatialReference.factoryCode if e.spatialReference else None],
                 "line": [line.type, line.length]})


@case("arcpy.FromWKT")
def _(ap, I, O):
    g = ap.FromWKT("POLYGON ((0 0, 0 10, 10 10, 10 0, 0 0), (2 2, 4 2, 4 4, 2 4, 2 2))", ap.SpatialReference(I["srid"]))
    m = ap.FromWKT("MULTILINESTRING ((0 0, 1 1), (5 5, 6 5))")
    p = ap.FromWKT("POINT Z (1 2 3)")
    return flat({"poly": [g.type, g.area, g.partCount, g.pointCount, g.WKT, g.spatialReference.factoryCode],
                 "mline": [m.type, m.partCount, _r(m.length, 6), m.isMultipart],
                 "pointz": [p.type, p.firstPoint.Z, p.WKT]})


@case("arcpy.FromWKB")
def _(ap, I, O):
    import struct
    point = struct.pack("<BIdd", 1, 1, 10.5, 20.25)
    line = struct.pack("<BII", 1, 2, 3) + struct.pack("<6d", 0, 0, 3, 4, 3, 10)
    p = ap.FromWKB(bytearray(point))
    q = ap.FromWKB(bytearray(line), ap.SpatialReference(4326))
    return flat({"point": [p.type, p.firstPoint.X, p.firstPoint.Y], "line": [q.type, q.length, q.pointCount,
                                                                            q.spatialReference.factoryCode],
                 "roundtrip": ap.FromWKB(q.WKB).WKT})


@case("arcpy.FromGeohash")
def _(ap, I, O):
    e = ap.FromGeohash("sxk9")
    f = ap.FromGeohash("u4pruydqqvj")
    return flat({"sxk9": [_r(e.XMin, 9), _r(e.YMin, 9), _r(e.XMax, 9), _r(e.YMax, 9)],
                 "long": [_r(f.XMin, 9), _r(f.YMax, 9)], "sr": e.spatialReference.factoryCode if e.spatialReference
                 else None})


@case("arcpy.FromCoordString")
def _(ap, I, O):
    out = {}
    for text, kind in (("29.0 40.5", "DD"), ("35TNE0000083000", "MGRS"), ("35T 500000 4483000", "UTM"),
                       ("29 00 00E 40 30 00N", "DMS")):
        g = _try(ap.FromCoordString, text, kind)
        out[kind] = g if isinstance(g, str) else [_r(g.firstPoint.X, 6), _r(g.firstPoint.Y, 6)]
    return flat(out)


@case("arcpy.ArealUnitConversionFactor")
def _(ap, I, O):
    return flat({"m2>ha": ap.ArealUnitConversionFactor("SquareMeters", "Hectares"),
                 "acre>m2": ap.ArealUnitConversionFactor("Acres", "SquareMeters"),
                 "km2>mi2": ap.ArealUnitConversionFactor("SquareKilometers", "SquareMiles")})


@case("arcpy.LinearUnitConversionFactor")
def _(ap, I, O):
    return flat({"ft>m": ap.LinearUnitConversionFactor("Feet", "Meters"),
                 "mi>km": ap.LinearUnitConversionFactor("Miles", "Kilometers"),
                 "usft>m": ap.LinearUnitConversionFactor("FeetUS", "Meters"),
                 "nm>m": ap.LinearUnitConversionFactor("NauticalMiles", "Meters")})


@case("arcpy.GetUTMFromLocation")
def _(ap, I, O):
    out = {}
    for lon, lat in ((29.0, 41.0), (-73.9, 40.7), (151.2, -33.9)):
        s = ap.GetUTMFromLocation(lon, lat)
        s = ap.SpatialReference(s) if isinstance(s, str) and not s.startswith("PROJCS") else s
        sr = s if not isinstance(s, str) else ap.SpatialReference()
        if isinstance(s, str):
            sr.loadFromString(s)
        out[f"{lon},{lat}"] = [sr.name, sr.factoryCode]
    return flat(out)


def gpkg(ap, I, O, name="gp"):
    """A GeoPackage of the case's own holding a copy of the points, and the copy's path. Answers that depend on the
    workspace's kind (name validation, delimiters, indexes, Describe) are compared on a GeoPackage on both sides,
    since that is the workspace grapy writes; on a file geodatabase Pro answers for a format grapy does not write."""
    folder = O(name, "folder")
    db = ap.management.CreateSQLiteDatabase(os.path.join(folder, name + ".gpkg"), "GEOPACKAGE")[0]
    fc = os.path.join(str(db), "points")
    ap.management.CopyFeatures(I["points"], fc)
    return str(db), fc


# ---- fields, tables and their helpers -----------------------------------------------------------------------------


def _field(f):
    return {k: getattr(f, k) for k in ("name", "aliasName", "type", "length", "precision", "scale", "isNullable",
                                       "editable", "required", "domain", "defaultValue")}


@case("arcpy.Field")
def _(ap, I, O):
    # scale is left out: a blank Field's is unset in Pro (-1, 6881395, 6357106 in three runs), not a value.
    f = ap.Field()
    blank = {k: v for k, v in _field(f).items() if k != "scale"}
    f.name, f.type, f.length = "code", "String", 12
    return flat({"blank": blank, "set": {k: v for k, v in _field(f).items() if k != "scale"}})


@case("arcpy.ListFields")
def _(ap, I, O):
    db, fc = gpkg(ap, I, O)
    polys = os.path.join(db, "polys")
    ap.management.CopyFeatures(I["polys"], polys)
    all_fields = ap.ListFields(fc)
    return flat({"names": [f.name for f in all_fields if f.type not in ("OID", "Geometry")],
                 "types": sorted(f.type for f in all_fields), "val": _field(ap.ListFields(fc, "val")[0]),
                 "wild": [f.name for f in ap.ListFields(polys, "c*")],
                 "double": [f.name for f in ap.ListFields(polys, None, "Double")],
                 "fgdb.names": [f.name for f in ap.ListFields(I["points"]) if f.type not in ("OID", "Geometry")]})


@case("arcpy.FieldInfo")
def _(ap, I, O):
    fi = ap.FieldInfo()
    fi.addField("val", "value", "VISIBLE", "NONE")
    fi.addField("cat", "cat", "HIDDEN", "RATIO")
    fi.setVisible(0, "HIDDEN")
    fi.setNewName(1, "category")
    fi.setSplitRule(0, "RATIO")
    out = {"count": fi.count, "names": [fi.getFieldName(i) for i in range(fi.count)],
           "new": [fi.getNewName(i) for i in range(fi.count)], "visible": [fi.getVisible(i) for i in range(fi.count)],
           "split": [fi.getSplitRule(i) for i in range(fi.count)], "find": fi.findFieldByName("cat"),
           "findNew": fi.findFieldByNewName("category"), "export": fi.exportToString()}
    fi.removeField(0)
    out["after"] = fi.count
    return flat(out)


@case("arcpy.FieldMappings")
def _(ap, I, O):
    fms = ap.FieldMappings()
    fms.addTable(I["points"])
    names = [fms.getFieldMap(i).outputField.name for i in range(fms.fieldCount)]
    index = fms.findFieldMapIndex("val")
    fm = fms.getFieldMap(index)
    fm.mergeRule = "Sum"
    fms.replaceFieldMap(index, fm)
    fms.removeFieldMap(fms.findFieldMapIndex("cat"))
    out = O("out")
    ap.conversion.ExportFeatures(I["points"], out, field_mapping=fms)
    return flat({"names": names, "count": fms.fieldCount, "rule": fms.getFieldMap(index).mergeRule,
                 "fields": [f.name for f in fms.fields]})


@case("arcpy.FieldMap")
def _(ap, I, O):
    fm = ap.FieldMap()
    fm.addInputField(I["points"], "val")
    fm.addInputField(I["points"], "pid")
    of = fm.outputField
    of.name, of.aliasName = "total", "total"
    fm.outputField = of
    fm.mergeRule = "Sum"
    fms = ap.FieldMappings()
    fms.addFieldMap(fm)
    ap.conversion.ExportTable(I["points"], O("out"), field_mapping=fms)
    return flat({"inputs": fm.inputFieldCount, "name": fm.getInputFieldName(1), "rule": fm.mergeRule,
                 "type": fm.outputField.type, "joinDelimiter": fm.joinDelimiter})


@case("arcpy.AlterAliasName")
def _(ap, I, O):
    out = O("out")
    ap.management.CopyRows(I["table"], out)
    ap.AlterAliasName(out, "Sweep Rows")
    return flat({"alias": ap.Describe(out).aliasName})


@case("arcpy.AddFieldDelimiters")
def _(ap, I, O):
    db, fc = gpkg(ap, I, O)
    return flat({"gpkg": ap.AddFieldDelimiters(db, "val"), "fc": ap.AddFieldDelimiters(fc, "val"),
                 "folder": ap.AddFieldDelimiters(I["work"], "val")})


@case("arcpy.ParseFieldName")
def _(ap, I, O):
    db, fc = gpkg(ap, I, O)
    return flat({"plain": ap.ParseFieldName("val", db), "qualified": _try(ap.ParseFieldName, "db.own.tab.val", db)})


@case("arcpy.ParseTableName")
def _(ap, I, O):
    db, fc = gpkg(ap, I, O)
    return flat({"plain": ap.ParseTableName("points", db), "qualified": _try(ap.ParseTableName, "db.own.tab", db)})


@case("arcpy.ValidateFieldName")
def _(ap, I, O):
    db, fc = gpkg(ap, I, O)
    names = ("good", "1st", "has space", "a-b", "s\u00e9lect", "", "select")
    return flat({"gpkg": {n: ap.ValidateFieldName(n, db) for n in names},
                 "folder": {n: ap.ValidateFieldName(n, I["work"]) for n in names}})


@case("arcpy.ValidateTableName")
def _(ap, I, O):
    db, fc = gpkg(ap, I, O)
    names = ("good", "1st", "has space", "a-b", "s\u00e9lect", "", "select")
    return flat({"gpkg": {n: ap.ValidateTableName(n, db) for n in names},
                 "folder": {n: ap.ValidateTableName(n, I["work"]) for n in names}})


@case("arcpy.CreateUniqueName")
def _(ap, I, O):
    db, fc = gpkg(ap, I, O)
    return flat({"taken": os.path.basename(ap.CreateUniqueName("points", db)),
                 "free": os.path.basename(ap.CreateUniqueName("nothere", db)),
                 "file": os.path.basename(ap.CreateUniqueName("dem.tif", I["work"]))})


@case("arcpy.CreateScratchName")
def _(ap, I, O):
    # The numbered part is random on both sides; its shape is compared.
    db, fc = gpkg(ap, I, O)
    names = [os.path.basename(ap.CreateScratchName("xx", "", kind, db)) for kind in ("FeatureClass", "Dataset")]
    folder = os.path.basename(ap.CreateScratchName("ras", ".tif", "RasterDataset", I["work"]))
    return flat({"names": [re.sub(r"\d", "9", n) for n in names], "folder": re.sub(r"\d", "9", folder)})


@case("arcpy.Exists")
def _(ap, I, O):
    return flat({"points": ap.Exists(I["points"]), "gdb": ap.Exists(I["gdb"]), "dem": ap.Exists(I["dem"]),
                 "missing": ap.Exists(os.path.join(I["gdb"], "nothere")), "byName": ap.Exists("dem.tif")})


@case("arcpy.TestSchemaLock")
def _(ap, I, O):
    return flat({"points": ap.TestSchemaLock(I["points"])})


@case("arcpy.IsBeingEdited")
def _(ap, I, O):
    return flat({"points": ap.IsBeingEdited(I["points"])})


@case("arcpy.Index")
def _(ap, I, O):
    db, out = gpkg(ap, I, O)
    ap.management.AddIndex(out, ["pid", "cat"], "pid_cat", "UNIQUE", "ASCENDING")
    found = [i for i in ap.ListIndexes(out) if i.name.lower() == "pid_cat"]
    i = found[0]
    return flat({"name": i.name, "fields": [f.name for f in i.fields], "isAscending": i.isAscending,
                 "isUnique": i.isUnique})


@case("arcpy.ListIndexes")
def _(ap, I, O):
    db, out = gpkg(ap, I, O)
    ap.management.AddIndex(out, ["val"], "val_idx")
    return flat({"names": sorted(i.name for i in ap.ListIndexes(out)), "wild": [i.name for i in ap.ListIndexes(
        out, "val*")]})


# ---- describing and listing ---------------------------------------------------------------------------------------


@case("arcpy.Describe")
def _(ap, I, O):
    db, fc = gpkg(ap, I, O)
    d = ap.Describe(fc)
    t = ap.Describe(I["table"])
    r = ap.Describe(I["dem"])
    single = ap.Describe(db).dataType  # a GeoPackage holding one table
    ap.management.CopyRows(I["table"], os.path.join(db, "rows"))
    w = ap.Describe(db)
    e = d.extent
    return flat({"fc": {k: getattr(d, k, "<none>") for k in (
        "dataType", "name", "baseName", "shapeType", "featureType", "hasM", "hasZ", "hasOID", "hasSpatialIndex",
        "OIDFieldName", "shapeFieldName", "datasetType", "canVersion", "isVersioned", "aliasName", "extension")},
        "fc.fields": [f.name for f in d.fields], "fc.sr": d.spatialReference.factoryCode,
        "fc.extent": [_r(e.XMin, 3), _r(e.YMin, 3), _r(e.XMax, 3), _r(e.YMax, 3)],
        "table": [t.dataType, t.hasOID, t.name], "raster": {k: getattr(r, k, "<none>") for k in (
            "dataType", "name", "bandCount", "format", "compressionType", "height", "width", "meanCellHeight",
            "meanCellWidth", "pixelType", "isInteger", "extension")},
        "workspace": [w.dataType, w.workspaceType, w.workspaceFactoryProgID], "single": single})


def _with_workspace(ap, workspace, function):
    old = ap.env.workspace
    ap.env.workspace = workspace
    try:
        return function()
    finally:
        ap.env.workspace = old


@case("arcpy.ListFeatureClasses")
def _(ap, I, O):
    def go():
        return {"poly": sorted(ap.ListFeatureClasses("*", "Polygon")), "p*": sorted(ap.ListFeatureClasses("p*")),
                "point": ap.ListFeatureClasses("p*", "Point"), "line": ap.ListFeatureClasses("l*", "Polyline")}
    return flat(_with_workspace(ap, I["gdb"], go))


@case("arcpy.ListTables")
def _(ap, I, O):
    return flat(_with_workspace(ap, I["gdb"], lambda: {"rows": ap.ListTables("row*"), "none": ap.ListTables("zz*")}))


@case("arcpy.ListRasters")
def _(ap, I, O):
    return flat(_with_workspace(ap, I["work"], lambda: {"d": ap.ListRasters("d*"), "tif": sorted(
        ap.ListRasters("*", "TIF")), "brackets": ap.ListRasters("[dz]*"), "question": ap.ListRasters("de?.tif"),
        "grid": ap.ListRasters("d*", "GRID")}))


@case("arcpy.ListDatasets")
def _(ap, I, O):
    folder = O("ds", "folder")
    gdb = ap.management.CreateFileGDB(folder, "ds")[0]
    ap.management.CreateFeatureDataset(gdb, "fd", ap.SpatialReference(I["srid"]))
    return flat({"gdb": _with_workspace(ap, gdb, lambda: ap.ListDatasets()),
                 "feature": _with_workspace(ap, gdb, lambda: ap.ListDatasets("*", "Feature")),
                 "folder": _with_workspace(ap, I["work"], lambda: sorted(ap.ListDatasets("d*"))),
        "brackets": _with_workspace(ap, I["work"], lambda: sorted(ap.ListDatasets("[dz]*")))})


@case("arcpy.ListWorkspaces")
def _(ap, I, O):
    folder = O("ws", "folder")
    ap.management.CreateFolder(folder, "sub")
    ap.management.CreateFileGDB(folder, "one")
    ap.management.CreateSQLiteDatabase(os.path.join(folder, "two.gpkg"), "GEOPACKAGE")
    return flat({"all": len(_with_workspace(ap, folder, lambda: ap.ListWorkspaces())),
                 "sqlite": len(_with_workspace(ap, folder, lambda: ap.ListWorkspaces("*", "SQLite"))),
                 "folder": [os.path.basename(p) for p in _with_workspace(ap, folder,
                                                                         lambda: ap.ListWorkspaces("*", "Folder"))],
                 "gdb": len(_with_workspace(ap, folder, lambda: ap.ListWorkspaces("*", "FileGDB")))})


@case("arcpy.ListFiles")
def _(ap, I, O):
    folder = O("files", "folder")
    for name in ("a.txt", "b.csv", "c.txt"):
        open(os.path.join(folder, name), "w").write("x")
    return flat(_with_workspace(ap, folder, lambda: {"all": sorted(ap.ListFiles()), "txt": sorted(
        ap.ListFiles("*.txt"))}))


@case("arcpy.ListEnvironments")
def _(ap, I, O):
    names = ap.ListEnvironments()
    some = ("workspace", "scratchWorkspace", "outputCoordinateSystem", "extent", "cellSize", "mask", "snapRaster",
            "overwriteOutput", "XYTolerance", "parallelProcessingFactor")
    return flat({"has": {n: n in names for n in some}, "wild": sorted(ap.ListEnvironments("cell*")),
                 "count": len(names)})


@case("arcpy.ListToolboxes")
def _(ap, I, O):
    names = ap.ListToolboxes()
    return flat({"analysis": [n for n in names if n.lower().startswith("analysis")],
                 "wild": sorted(ap.ListToolboxes("Data*")), "count": len(names)})


@case("arcpy.ListTools")
def _(ap, I, O):
    return flat({"buffer": sorted(ap.ListTools("Buffer*")), "analysis.count": len(ap.ListTools("*_analysis")),
                 "clip": sorted(ap.ListTools("Clip_*"))})


@case("arcpy.ListVersions")
def _(ap, I, O):
    return flat({"gdb": [v for v in ap.ListVersions(I["gdb"])]})


# ---- environments and messages ------------------------------------------------------------------------------------


@case("arcpy.EnvManager")
def _(ap, I, O):
    before = ap.env.cellSize
    with ap.EnvManager(cellSize=30, overwriteOutput=False):
        inside = [ap.env.cellSize, ap.env.overwriteOutput]
    return flat({"before": before, "inside": inside, "after": [ap.env.cellSize, ap.env.overwriteOutput]})


@case("arcpy.ClearEnvironment")
def _(ap, I, O):
    ap.env.cellSize = 30
    ap.ClearEnvironment("cellSize")
    old = ap.env.workspace
    ap.ClearEnvironment("workspace")
    cleared = ap.env.workspace
    ap.env.workspace = old
    return flat({"cellSize": ap.env.cellSize, "workspace": cleared})


@case("arcpy.ResetEnvironments")
def _(ap, I, O):
    old = ap.env.workspace
    ap.env.cellSize, ap.env.XYTolerance = 30, "1 Meters"
    ap.ResetEnvironments()
    out = {"cellSize": ap.env.cellSize, "XYTolerance": ap.env.XYTolerance, "workspace": ap.env.workspace,
           "overwriteOutput": ap.env.overwriteOutput}
    ap.env.workspace, ap.env.overwriteOutput = old, True
    return flat(out)


@case("arcpy.GetSystemEnvironment")
def _(ap, I, O):
    return flat({"OS": ap.GetSystemEnvironment("OS"), "none": ap.GetSystemEnvironment("NO_SUCH_VARIABLE_HERE")})


def _ran(ap, I):
    return ap.management.GetCount(I["points"])


def _words(text):
    """The first two words of a message: what it says, without its timestamp or elapsed time."""
    return " ".join(str(text).split()[:2])


@case("arcpy.GetMessages")
def _(ap, I, O):
    _ran(ap, I)
    return flat({"all": [_words(m) for m in ap.GetMessages().splitlines()], "errors": ap.GetMessages(2)})


@case("arcpy.GetMessage")
def _(ap, I, O):
    _ran(ap, I)
    return flat({"first": _words(ap.GetMessage(0))})


@case("arcpy.GetMessageCount")
def _(ap, I, O):
    _ran(ap, I)
    return flat({"count": ap.GetMessageCount()})


@case("arcpy.GetAllMessages")
def _(ap, I, O):
    _ran(ap, I)
    return flat({"all": [[m[0], m[1], _words(m[2])] for m in ap.GetAllMessages()]})


@case("arcpy.GetMaxSeverity")
def _(ap, I, O):
    _ran(ap, I)
    return flat({"max": ap.GetMaxSeverity()})


@case("arcpy.GetSeverity")
def _(ap, I, O):
    _ran(ap, I)
    return flat({"first": ap.GetSeverity(0)})


@case("arcpy.GetReturnCode")
def _(ap, I, O):
    _ran(ap, I)
    return flat({"first": ap.GetReturnCode(0)})


@case("arcpy.GetSeverityLevel")
def _(ap, I, O):
    return flat({"default": ap.GetSeverityLevel()})


@case("arcpy.SetSeverityLevel")
def _(ap, I, O):
    ap.SetSeverityLevel(2)
    level = ap.GetSeverityLevel()
    zero = _try(ap.SetSeverityLevel, 0)
    after = ap.GetSeverityLevel()
    ap.SetSeverityLevel(2)
    return flat({"set": level, "zero": [zero, after]})


@case("arcpy.SeverityLevel")
def _(ap, I, O):
    with ap.SeverityLevel(2):
        inside = ap.GetSeverityLevel()
    return flat({"inside": inside, "after": ap.GetSeverityLevel()})


def _outside_tool(ap, I, call):
    """A messaging or progress call made outside a tool: what it answers, and that a tool's messages are unchanged."""
    _ran(ap, I)
    answer = call()
    return flat({"answer": answer, "count": ap.GetMessageCount(), "max": ap.GetMaxSeverity()})


@case("arcpy.AddMessage")
def _(ap, I, O):
    return _outside_tool(ap, I, lambda: ap.AddMessage("hello"))


@case("arcpy.AddWarning")
def _(ap, I, O):
    return _outside_tool(ap, I, lambda: ap.AddWarning("careful"))


@case("arcpy.AddError")
def _(ap, I, O):
    return _outside_tool(ap, I, lambda: ap.AddError("broken"))


@case("arcpy.AddIDMessage")
def _(ap, I, O):
    return _outside_tool(ap, I, lambda: ap.AddIDMessage("WARNING", 12, "out"))


@case("arcpy.AddReturnMessage")
def _(ap, I, O):
    return _outside_tool(ap, I, lambda: ap.AddReturnMessage(0))


@case("arcpy.SetProgressor")
def _(ap, I, O):
    return _outside_tool(ap, I, lambda: ap.SetProgressor("step", "working", 0, 10, 1))


@case("arcpy.SetProgressorLabel")
def _(ap, I, O):
    return _outside_tool(ap, I, lambda: ap.SetProgressorLabel("label"))


@case("arcpy.SetProgressorPosition")
def _(ap, I, O):
    return _outside_tool(ap, I, lambda: ap.SetProgressorPosition(3))


@case("arcpy.ResetProgressor")
def _(ap, I, O):
    return _outside_tool(ap, I, lambda: ap.ResetProgressor())


# ---- script tool parameters, outside a tool run: they read sys.argv -----------------------------------------------


def _argv(function):
    saved = sys.argv
    sys.argv = ["script.py", "first value", "5", "#"]
    try:
        return function()
    finally:
        sys.argv = saved


@case("arcpy.GetArgumentCount")
def _(ap, I, O):
    return flat({"count": _argv(ap.GetArgumentCount)})


@case("arcpy.GetParameterCount")
def _(ap, I, O):
    return flat({"buffer": ap.GetParameterCount("Buffer_analysis"), "clip": ap.GetParameterCount("analysis.Clip")})


@case("arcpy.GetParameterAsText")
def _(ap, I, O):
    return flat(_argv(lambda: {"0": ap.GetParameterAsText(0), "1": ap.GetParameterAsText(1),
                               "2": ap.GetParameterAsText(2)}))


@case("arcpy.GetParameter")
def _(ap, I, O):
    return flat(_argv(lambda: {"0": ap.GetParameter(0), "1": ap.GetParameter(1), "2": ap.GetParameter(2)}))


@case("arcpy.SetParameter")
def _(ap, I, O):
    return flat({"answer": _try(ap.SetParameter, 0, "x")})


@case("arcpy.SetParameterAsText")
def _(ap, I, O):
    return flat({"answer": _try(ap.SetParameterAsText, 0, "x")})


@case("arcpy.CopyParameter")
def _(ap, I, O):
    return flat({"answer": _try(ap.CopyParameter, 0, 1)})


@case("arcpy.Parameter")
def _(ap, I, O):
    p = ap.Parameter(name="dist", displayName="Distance", direction="Input", datatype="GPLinearUnit",
                     parameterType="Required")
    q = ap.Parameter("choice", "Choice", "Input", "GPString", "Optional")
    q.filter.type = "ValueList"
    q.filter.list = ["a", "b"]
    q.value = "a"
    return flat({"p": {k: getattr(p, k) for k in ("name", "displayName", "direction", "datatype", "parameterType",
                                                    "enabled", "multiValue", "altered", "hasBeenValidated", "category",
                                                    "valueAsText", "value")},
                 "q": [q.valueAsText, q.filter.type, q.filter.list, q.parameterType, q.datatype]})


@case("arcpy.Filter")
def _(ap, I, O):
    p = ap.Parameter("n", "N", "Input", "GPLong", "Required")
    f = p.filter
    before = [f.type, f.list]
    f.type = "Range"
    f.list = [1, 10]
    return flat({"before": before, "after": [f.type, f.list]})


@case("arcpy.Value")
def _(ap, I, O):
    # arcpy.Value is not constructed by a script (Pro: "CreateObject Value not valid"); it is what a parameter of
    # some data types answers as its value.
    p = ap.Parameter("d", "D", "Input", "GPLinearUnit", "Optional")
    empty = p.value
    p.value = "5 Meters"
    v = p.value
    return flat({"empty": [type(empty).__name__, str(empty) if empty is not None else None],
                 "set": [type(v).__name__, str(v), p.valueAsText]})


@case("arcpy.ValueTable")
def _(ap, I, O):
    vt = ap.ValueTable(2)
    vt.addRow("a 1")
    vt.addRow(["b", 2])
    vt.setValue(0, 1, "7")
    vt.addRow("'c d' 3")
    out = {"rows": vt.rowCount, "cols": vt.columnCount, "get": vt.getValue(1, 0), "set": vt.getValue(0, 1),
           "row": vt.getRow(2),
           "export": vt.exportToString()}
    vt.removeRow(0)
    out["after"] = [vt.rowCount, vt.getValue(0, 0)]
    vt2 = ap.ValueTable(["GPString", "GPLong"])
    vt2.loadFromString("x 1;y 2")
    out["loaded"] = [vt2.rowCount, vt2.getValue(1, 0), vt2.columnCount]
    return flat(out)


# ---- toolboxes and tools ------------------------------------------------------------------------------------------


_PYT = '''try:
    import arcpy
except ImportError:  # grapy's side: a user toolbox imports grapy under arcpy's name
    import grapy as arcpy


class Toolbox:
    def __init__(self):
        self.label = "Sweep toolbox"
        self.alias = "sweeptbx"
        self.tools = [Double]


class Double:
    def __init__(self):
        self.label = "Double a number"
        self.description = "Doubles it."

    def getParameterInfo(self):
        a = arcpy.Parameter(name="number", displayName="Number", direction="Input", datatype="GPDouble",
                            parameterType="Required")
        b = arcpy.Parameter(name="factor", displayName="Factor", direction="Input", datatype="GPLong",
                            parameterType="Optional")
        b.value = 2
        c = arcpy.Parameter(name="result", displayName="Result", direction="Output", datatype="GPDouble",
                            parameterType="Derived")
        return [a, b, c]

    def execute(self, parameters, messages):
        parameters[2].value = parameters[0].value * (parameters[1].value or 2)
        messages.addMessage("doubled")
'''


def _toolbox(O):
    folder = O("tbx", "folder")
    path = os.path.join(folder, "Sweep.pyt")
    open(path, "w", encoding="utf-8").write(_PYT)
    return path


@case("arcpy.ImportToolbox")
def _(ap, I, O):
    path = _toolbox(O)
    module = ap.ImportToolbox(path)
    result = ap.Double_sweeptbx(4.5, 3)
    return flat({"tools": sorted(ap.ListTools("*_sweeptbx")), "result": result.getOutput(0),
                 "module.has": hasattr(module, "Double")})


@case("arcpy.AddToolbox")
def _(ap, I, O):
    path = _toolbox(O)
    ap.AddToolbox(path)
    return flat({"tools": sorted(ap.ListTools("*_sweeptbx")), "result": ap.Double_sweeptbx(2).getOutput(0)})


@case("arcpy.RemoveToolbox")
def _(ap, I, O):
    path = _toolbox(O)
    ap.ImportToolbox(path)
    before = sorted(ap.ListTools("*_sweeptbx"))
    ap.RemoveToolbox(path)
    return flat({"before": before, "after": sorted(ap.ListTools("*_sweeptbx"))})


@case("arcpy.GetParameterInfo")
def _(ap, I, O):
    ap.ImportToolbox(_toolbox(O))
    params = ap.GetParameterInfo("Double_sweeptbx")
    return flat({"params": [[p.name, p.datatype, p.direction, p.parameterType] for p in params]})


@case("arcpy.GetParameterValue")
def _(ap, I, O):
    ap.ImportToolbox(_toolbox(O))
    return flat({"factor": ap.GetParameterValue("Double_sweeptbx", 1),
                 "buffer.dissolve": ap.GetParameterValue("Buffer_analysis", 4)})


@case("arcpy.Usage")
def _(ap, I, O):
    ap.ImportToolbox(_toolbox(O))
    return flat({"double": ap.Usage("Double_sweeptbx"), "buffer": ap.Usage("Buffer_analysis")})


@case("arcpy.IsSynchronous")
def _(ap, I, O):
    return flat({"buffer": ap.IsSynchronous("Buffer_analysis")})


@case("arcpy.Command")
def _(ap, I, O):
    return flat({"answer": _try(ap.Command, f"GetCount_management {I['points']}")})


@case("arcpy.Result")
def _(ap, I, O):
    r = ap.management.GetCount(I["points"])
    return flat({"output": r.getOutput(0), "outputCount": r.outputCount, "status": r.status,
                 "inputCount": _try(lambda: r.inputCount), "maxSeverity": _try(lambda: r.maxSeverity),
                 "messageCount": _try(lambda: r.messageCount), "severity0": _try(lambda: r.getSeverity(0))})


@case("arcpy.CreateObject")
def _(ap, I, O):
    vt = ap.CreateObject("ValueTable", 3)
    sr = ap.CreateObject("SpatialReference", I["srid"])
    p = ap.CreateObject("Point")
    return flat({"vt": [type(vt).__name__, vt.columnCount], "sr": sr.factoryCode, "point": type(p).__name__,
                 "unknown": _try(ap.CreateObject, "NoSuchThing")})


# ---- licences -----------------------------------------------------------------------------------------------------


@case("arcpy.CheckProduct")
def _(ap, I, O):
    return flat({p: ap.CheckProduct(p) for p in ("ArcInfo", "ArcEditor", "ArcView", "Nonsense")})


@case("arcpy.CheckExtension")
def _(ap, I, O):
    return flat({e: ap.CheckExtension(e) for e in ("Spatial", "3D", "Nonsense")})


@case("arcpy.CheckOutExtension")
def _(ap, I, O):
    return flat({"spatial": ap.CheckOutExtension("Spatial")})


@case("arcpy.CheckInExtension")
def _(ap, I, O):
    ap.CheckOutExtension("Spatial")
    return flat({"spatial": ap.CheckInExtension("Spatial")})


# ---- cursors (the legacy ones) ------------------------------------------------------------------------------------


@case("arcpy.SearchCursor")
def _(ap, I, O):
    rows = ap.SearchCursor(I["points"], "pid < 6", None, "pid;val", "val D")
    out = [[row.getValue("pid"), row.getValue("val")] for row in rows]
    del rows
    return flat({"rows": out})


@case("arcpy.Cursor")
def _(ap, I, O):
    cursor = ap.SearchCursor(I["table"], "rid <= 3")
    values = [row.rid for row in cursor]
    end = cursor.next()
    del cursor
    return flat({"values": values, "end": end})


@case("arcpy.Row")
def _(ap, I, O):
    out = O("out")
    ap.management.CopyRows(I["table"], out)
    cursor = ap.UpdateCursor(out, "rid = 2")
    row = cursor.next()
    state = {"get": row.getValue("val"), "attr": row.cat, "isNull": row.isNull("cat")}
    row.setNull("cat")
    state["afterNull"] = row.isNull("cat")
    row.setValue("val", 99.0)
    cursor.updateRow(row)
    del row, cursor
    return flat(state)


@case("arcpy.UpdateCursor")
def _(ap, I, O):
    out = O("out")
    ap.management.CopyRows(I["table"], out)
    cursor = ap.UpdateCursor(out)
    for row in cursor:
        if row.getValue("rid") % 3 == 0:
            cursor.deleteRow(row)
        else:
            row.setValue("val", row.getValue("val") * 2)
            cursor.updateRow(row)
    del cursor


@case("arcpy.InsertCursor")
def _(ap, I, O):
    out = O("out")
    ap.management.CopyFeatures(I["points"], out)
    cursor = ap.InsertCursor(out)
    row = cursor.newRow()
    row.setValue("pid", 99)
    row.setValue("val", 1.25)
    row.setValue("cat", "z")
    row.setValue("Shape", ap.Point(I["x0"] + 5, I["y0"] + 5))
    cursor.insertRow(row)
    del row, cursor


# ---- feature and record sets --------------------------------------------------------------------------------------


def _set_json(fs):
    import json
    j = json.loads(fs.JSON)
    feats = j.get("features", [])
    return {"geometryType": j.get("geometryType"), "fields": [f["name"] for f in j.get("fields", [])
                                                                if f.get("type") not in ("esriFieldTypeOID",)],
            "count": len(feats), "first": {k: v for k, v in sorted(feats[0]["attributes"].items())
                                           if k.lower() not in ("objectid", "fid", "oid")} if feats else None,
            "wkid": (j.get("spatialReference") or {}).get("wkid")}


@case("arcpy.FeatureSet")
def _(ap, I, O):
    fs = ap.FeatureSet(I["polys"])
    out = O("out")
    fs.save(out)
    empty = ap.FeatureSet()
    loaded = ap.FeatureSet()
    loaded.load(I["points"])
    return flat({"json": _set_json(fs), "loaded": _set_json(loaded)["count"], "empty.JSON": bool(empty.JSON)})


@case("arcpy.RecordSet")
def _(ap, I, O):
    rs = ap.RecordSet(I["table"])
    rs.save(O("out"))
    return flat({"json": _set_json(rs)})


# ---- rasters ------------------------------------------------------------------------------------------------------


@case("arcpy.Raster")
def _(ap, I, O):
    r = ap.Raster(I["dem"])
    z = ap.Raster(I["zones"])
    e = r.extent
    return flat({"dem": {k: (_r(getattr(r, k), 4) if isinstance(getattr(r, k), float) else getattr(r, k)) for k in (
        "bandCount", "height", "width", "meanCellWidth", "meanCellHeight", "minimum", "maximum", "mean",
        "standardDeviation", "pixelType", "noDataValue", "isInteger", "format", "name", "hasRAT", "compressionType",
        "isMultidimensional", "isTemporary", "uncompressedSize")}, "extent": [e.XMin, e.YMin, e.XMax, e.YMax],
        "srid": r.spatialReference.factoryCode, "zones": [z.isInteger, z.pixelType, z.minimum, z.maximum, z.hasRAT],
        "read": [round(float(v), 4) for v in r.read(upper_left_corner=(0, 0), nrows=1, ncols=3).ravel()]})


@case("arcpy.RasterToNumPyArray")
def _(ap, I, O):
    a = ap.RasterToNumPyArray(I["dem"], ap.Point(I["x0"] + 100, I["y0"] + 100), 4, 3)
    z = ap.RasterToNumPyArray(I["zones"], nodata_to_value=-1)
    return flat({"shape": list(a.shape), "dtype": str(a.dtype), "cells": [round(float(v), 3) for v in a.ravel()],
                 "zones": [list(z.shape), str(z.dtype), int(z[0, 0]), int(z[-1, -1]), int(z.sum())]})


@case("arcpy.NumPyArrayToRaster")
def _(ap, I, O):
    import numpy as np
    a = np.arange(12, dtype="float32").reshape(3, 4)
    a[1, 1] = -1
    r = ap.NumPyArrayToRaster(a, ap.Point(I["x0"], I["y0"]), 5, 5, -1)
    r.save(O("out", "raster"))
    i = ap.NumPyArrayToRaster(np.array([[1, 2], [3, 4]], dtype="int16"), ap.Point(0, 0), 1)
    return flat({"float": [r.pixelType, r.noDataValue, r.width, r.height], "int": [i.pixelType, i.isInteger,
                                                                                i.minimum, i.maximum]})


@case("arcpy.NetCDFFileProperties")
def _(ap, I, O):
    import numpy as np
    from scipy.io import netcdf_file
    folder = O("nc", "folder")
    path = os.path.join(folder, "t.nc")
    with netcdf_file(path, "w") as nc:
        nc.title = "sweep"
        nc.createDimension("time", 2)
        nc.createDimension("lat", 3)
        nc.createDimension("lon", 4)
        t = nc.createVariable("time", "f8", ("time",))
        t[:] = [0.0, 1.0]
        t.units = "days since 2000-01-01"
        la = nc.createVariable("lat", "f4", ("lat",))
        la[:] = [40.0, 41.0, 42.0]
        la.units = "degrees_north"
        lo = nc.createVariable("lon", "f4", ("lon",))
        lo[:] = [28.0, 29.0, 30.0, 31.0]
        lo.units = "degrees_east"
        v = nc.createVariable("temp", "f4", ("time", "lat", "lon"))
        v[:] = np.arange(24, dtype="float32").reshape(2, 3, 4)
        v.units = "K"
        c = nc.createVariable("code", "i2", ("lat",))
        c[:] = [1, 2, 3]
    p = ap.NetCDFFileProperties(path)
    return flat({"variables": sorted(p.getVariables()), "dimensions": sorted(p.getDimensions()),
                 "dimsOf": list(p.getDimensionsByVariable("temp")), "varsOf": sorted(p.getVariablesByDimension("lat")),
                 "size": p.getDimensionSize("lon"), "types": [p.getFieldType("temp"), p.getFieldType("code"),
                                                               p.getFieldType("time")],
                 "dimValue": _r(p.getDimensionValue("lat", 1), 4), "dimIndex": p.getDimensionIndex("lat", 41.0),
                 "attrNames": sorted(p.getAttributeNames("temp")), "attr": p.getAttributeValue("temp", "units"),
                 "global": sorted(p.getAttributeNames("")), "title": p.getAttributeValue("", "title")})


@case("arcpy.Mensuration")
def _(ap, I, O):
    # Mensuration needs an image with a sensor model (RPCs); no such image is in the sweep's fixtures.
    return flat({"constants": [ap.Mensuration.base_to_top if hasattr(ap.Mensuration, "base_to_top") else None]})


# ---- random numbers -----------------------------------------------------------------------------------------------


@case("arcpy.CreateRandomValueGenerator")
def _(ap, I, O):
    g = ap.CreateRandomValueGenerator(20, "MERSENNE_TWISTER")
    return flat({"type": type(g).__name__, "str": str(g)})


@case("arcpy.RandomNumberGenerator")
def _(ap, I, O):
    g = ap.CreateRandomValueGenerator(7, "STANDARD_C")
    ap.env.randomGenerator = g
    env = str(ap.env.randomGenerator)
    ap.env.randomGenerator = None
    return flat({"type": type(g).__name__, "env": env, "export": _try(lambda: g.exportToString())})


# ---- geostatistics ------------------------------------------------------------------------------------------------


def _search(ap, cls, *args):
    s = getattr(ap, cls)(*args)
    keys = ("nbrType", "majSemiaxis", "minSemiaxis", "angle", "nbrMax", "nbrMin", "sectorType", "smoothFactor",
            "radius", "elevationInflationFactor", "nbrMaxGlobal")
    return flat({"str": str(s), "props": {k: getattr(s, k) for k in keys if hasattr(s, k)}})


@case("arcpy.SearchNeighborhoodStandard")
def _(ap, I, O):
    return _search(ap, "SearchNeighborhoodStandard", 300, 200, 45, 10, 3, "FOUR_SECTORS")


@case("arcpy.SearchNeighborhoodSmooth")
def _(ap, I, O):
    return _search(ap, "SearchNeighborhoodSmooth", 300, 200, 45, 0.5)


@case("arcpy.SearchNeighborhoodStandardCircular")
def _(ap, I, O):
    return _search(ap, "SearchNeighborhoodStandardCircular", 250, 0, 10, 3, "ONE_SECTOR")


@case("arcpy.SearchNeighborhoodSmoothCircular")
def _(ap, I, O):
    return _search(ap, "SearchNeighborhoodSmoothCircular", 250, 0.4)


@case("arcpy.SearchNeighborhoodStandard3D")
def _(ap, I, O):
    return _search(ap, "SearchNeighborhoodStandard3D", 300, 10, 3, "FOUR_SECTORS")


def _ga_layer(ap, I, O):
    folder = O("ga", "folder")
    layer = os.path.join(folder, "idw.lyrx")
    ap.ga.IDW(I["points"], "val", layer, power=2,
              search_neighborhood=ap.SearchNeighborhoodStandardCircular(250, 0, 10, 3, "ONE_SECTOR"))
    return layer


@case("arcpy.CrossValidationResult")
def _(ap, I, O):
    cv = ap.ga.CrossValidation(_ga_layer(ap, I, O))
    return flat({k: _r(getattr(cv, k), 6) for k in ("count", "meanError", "rootMeanSquare")})


@case("arcpy.GeostatisticalDatasets")
def _(ap, I, O):
    g = ap.GeostatisticalDatasets(_ga_layer(ap, I, O))
    return flat({"dataset1": os.path.basename(str(g.dataset1)), "field": g.dataset1Field})
