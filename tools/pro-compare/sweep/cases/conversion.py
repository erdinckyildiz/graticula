"""arcpy.conversion against ArcGIS Pro 3.7: one case for each name grapy builds.

A tool that writes a file format both sides can read (shapefile, dBASE, JSON, GPX, ASCII and float grids, Excel,
Parquet, LAS, COLLADA) is compared by reading what it wrote back in the case, with the standard library (or pyarrow,
which both Pythons hold), and answering what was read; a tool that writes a feature class, table or raster is
compared by the harness's dump. Inputs a tool needs beyond the shared fixtures (a grid file, a GPX, a KML, GTFS text,
an Excel workbook, a LAS file, a multipatch) are written here, the same bytes on both sides.

What only the input's own storage decides is taken out before comparing, since the shared fixtures are a file
geodatabase on Pro's side and a GeoPackage on grapy's: the ObjectID field's name (OBJECTID or fid), the shape column's
name (Shape or geom) and the geodatabase's own Shape_Length and Shape_Area fields.
"""
import json
import os
import struct
import xml.etree.ElementTree as ET
import zipfile

CASES = {}


def case(name):
    def register(function):
        CASES[name] = function
        return function
    return register


def _r(value, digits=4):
    return round(float(value), digits) if value is not None else None


# ---- inputs written in the case ------------------------------------------------------------------------------------

def _folder(O, name):
    """A folder output, emptied first: the harness keeps its work folders between runs."""
    import shutil
    folder = O(name, "folder")
    for entry in os.listdir(folder):
        target = os.path.join(folder, entry)
        shutil.rmtree(target) if os.path.isdir(target) else os.remove(target)
    return folder


def _write(path, text):
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    return path


def _ascii_grid(folder):
    rows = ["1 2 3 4 5", "6 -9999 8 9 10", "11 12 13 14 15", "16 17 18 19 20.5"]
    return _write(os.path.join(folder, "grid.asc"),
                  "ncols 5\nnrows 4\nxllcorner 500000\nyllcorner 4500000\ncellsize 10\nNODATA_value -9999\n"
                  + "\n".join(rows) + "\n")


def _float_grid(folder):
    import numpy as np
    values = np.arange(20, dtype="<f4").reshape(4, 5) * 1.5
    values[1, 2] = -9999
    path = os.path.join(folder, "grid.flt")
    values.tofile(path)
    _write(os.path.join(folder, "grid.hdr"), "ncols 5\nnrows 4\nxllcorner 500000\nyllcorner 4500000\ncellsize 10\n"
                                              "NODATA_value -9999\nbyteorder LSBFIRST\n")
    return path


GPX = """<?xml version="1.0" encoding="UTF-8"?>
<gpx version="1.1" creator="sweep" xmlns="http://www.topografix.com/GPX/1/1">
  <wpt lat="40.6501" lon="27.0012"><ele>12.5</ele><time>2026-01-02T03:04:05Z</time><name>alpha</name><desc>first</desc></wpt>
  <wpt lat="40.6523" lon="27.0045"><ele>20</ele><time>2026-01-02T03:14:05Z</time><name>beta</name><desc>second</desc></wpt>
  <trk><name>walk</name><trkseg>
    <trkpt lat="40.6510" lon="27.0020"><ele>14</ele><time>2026-01-02T04:00:00Z</time></trkpt>
    <trkpt lat="40.6515" lon="27.0030"><ele>15</ele><time>2026-01-02T04:01:00Z</time></trkpt>
    <trkpt lat="40.6520" lon="27.0041"><ele>17</ele><time>2026-01-02T04:02:00Z</time></trkpt>
  </trkseg></trk>
</gpx>
"""

KML = """<?xml version="1.0" encoding="UTF-8"?>
<kml xmlns="http://www.opengis.net/kml/2.2"><Document><name>sweep</name>
  <Placemark><name>well</name><description>a point</description><Point><coordinates>27.0012,40.6501,0</coordinates></Point></Placemark>
  <Placemark><name>path</name><LineString><coordinates>27.0012,40.6501,0 27.0045,40.6523,0 27.0060,40.6530,0</coordinates></LineString></Placemark>
  <Placemark><name>field</name><Polygon><outerBoundaryIs><LinearRing><coordinates>27.000,40.650,0 27.002,40.650,0 27.002,40.652,0 27.000,40.652,0 27.000,40.650,0</coordinates></LinearRing></outerBoundaryIs></Polygon></Placemark>
</Document></kml>
"""

STOPS = """stop_id,stop_code,stop_name,stop_desc,stop_lat,stop_lon,zone_id,location_type,parent_station,wheelchair_boarding
S1,101,Harbour,By the water,40.6501,27.0012,Z1,0,,1
S2,102,Market,,40.6523,27.0045,Z1,0,,0
S3,,Station,Main hall,40.6530,27.0060,Z2,1,,
"""

SHAPES = """shape_id,shape_pt_lat,shape_pt_lon,shape_pt_sequence,shape_dist_traveled
A,40.6501,27.0012,1,0
A,40.6523,27.0045,2,370.5
A,40.6530,27.0060,3,520
B,40.6400,27.0100,2,100
B,40.6390,27.0090,1,0
"""

ESRI_JSON = {
    "displayFieldName": "", "geometryType": "esriGeometryPolygon", "spatialReference": {"wkid": 32635},
    "fields": [{"name": "OBJECTID", "type": "esriFieldTypeOID", "alias": "OBJECTID"},
               {"name": "name", "type": "esriFieldTypeString", "alias": "name", "length": 20},
               {"name": "n", "type": "esriFieldTypeInteger", "alias": "n"},
               {"name": "w", "type": "esriFieldTypeDouble", "alias": "w"}],
    "features": [
        {"attributes": {"OBJECTID": 1, "name": "one", "n": 3, "w": 1.25},
         "geometry": {"rings": [[[500000, 4500000], [500000, 4500100], [500100, 4500100], [500100, 4500000],
                                 [500000, 4500000]]]}},
        {"attributes": {"OBJECTID": 2, "name": "two", "n": None, "w": 2.5},
         "geometry": {"rings": [[[500200, 4500000], [500200, 4500050], [500300, 4500000], [500200, 4500000]]]}},
    ]}


def _xlsx(path, sheet, rows):
    """A minimal workbook of one sheet: text as inline strings, numbers as numbers."""
    def cell(ref, value):
        if isinstance(value, (int, float)):
            return f'<c r="{ref}"><v>{value}</v></c>'
        return f'<c r="{ref}" t="inlineStr"><is><t>{value}</t></is></c>'
    body = "".join(f'<row r="{i}">' + "".join(cell(f"{chr(65 + j)}{i}", v) for j, v in enumerate(row) if v is not None)
                   + "</row>" for i, row in enumerate(rows, start=1))
    parts = {
        "[Content_Types].xml": '<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/'
        'package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.'
        'relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/'
        'workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>'
        '<Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.'
        'spreadsheetml.worksheet+xml"/></Types>',
        "_rels/.rels": '<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/'
        'package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/'
        '2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>',
        "xl/workbook.xml": '<?xml version="1.0" encoding="UTF-8"?><workbook xmlns="http://schemas.openxmlformats.org/'
        'spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">'
        f'<sheets><sheet name="{sheet}" sheetId="1" r:id="rId1"/></sheets></workbook>',
        "xl/_rels/workbook.xml.rels": '<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.'
        'openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.'
        'org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>',
        "xl/worksheets/sheet1.xml": '<?xml version="1.0" encoding="UTF-8"?><worksheet xmlns="http://schemas.'
        f'openxmlformats.org/spreadsheetml/2006/main"><sheetData>{body}</sheetData></worksheet>',
    }
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
        for name, text in parts.items():
            z.writestr(name, text)
    return path


def _las(path, points):
    """A LAS 1.2 file of point record format 0, every point a single-return ground point; no VLRs."""
    scale, offset = 0.01, (500000.0, 4500000.0, 0.0)
    xs, ys, zs = [p[0] for p in points], [p[1] for p in points], [p[2] for p in points]
    header = struct.pack(
        "<4sHH16sBB32s32sHHHIIBHI5I3d3d6d", b"LASF", 0, 0, b"\0" * 16, 1, 2, b"sweep".ljust(32, b"\0"),
        b"sweep".ljust(32, b"\0"), 1, 2026, 227, 227, 0, 0, 20, len(points), len(points), 0, 0, 0, 0,
        scale, scale, scale, *offset, max(xs), min(xs), max(ys), min(ys), max(zs), min(zs))
    assert len(header) == 227
    with open(path, "wb") as f:
        f.write(header)
        for x, y, z, intensity in points:
            f.write(struct.pack("<iiiHBBbBH", round((x - offset[0]) / scale), round((y - offset[1]) / scale),
                                round((z - offset[2]) / scale), intensity, 9, 2, 0, 0, 1))
    return path


def _las_points(I):
    import fixtures
    out = []
    for i in range(60):
        for j in range(60):
            x, y = 5 + 10 * i + (j % 3), 5 + 10 * j + (i % 2)
            out.append((I["x0"] + x, I["y0"] + y, round(fixtures.dem_value(x, y), 2), (i * 7 + j * 3) % 200))
    return out


OBJ_CUBE = """# a 40 m box, 10 m to 30 m high
v 500100 4500100 10
v 500140 4500100 10
v 500140 4500140 10
v 500100 4500140 10
v 500100 4500100 30
v 500140 4500100 30
v 500140 4500140 30
v 500100 4500140 30
f 1 4 3 2
f 5 6 7 8
f 1 2 6 5
f 2 3 7 6
f 3 4 8 7
f 4 1 5 8
"""


def _multipatch(ap, I, O):
    folder = _folder(O, "models")
    model = _write(os.path.join(folder, "box.obj"), OBJ_CUBE)
    out = os.path.join(I["gdb"], "box_" + os.path.basename(folder))   # an input, so not dumped
    ap.ddd.Import3DFiles(model, out, spatial_reference=ap.SpatialReference(I["srid"]))
    return out


# ---- readers of what a tool wrote ----------------------------------------------------------------------------------

GDB_ONLY = ("shape_length", "shape_area", "shape_leng", "shape_le_1", "shape__length", "shape__area")


def _oid(ap, path):
    return ap.Describe(path).OIDFieldName


def _dbf(path):
    data = open(path, "rb").read()
    count, header, size = struct.unpack("<IHH", data[4:12])
    fields, at = [], 32
    while data[at] != 0x0D:
        name = data[at:at + 11].split(b"\0")[0].decode("latin-1")
        fields.append((name, chr(data[at + 11]), data[at + 16], data[at + 17]))
        at += 32
    rows = []
    keep = [i for i, f in enumerate(fields) if f[0].lower() not in GDB_ONLY]
    for k in range(count):
        record = data[header + k * size: header + (k + 1) * size]
        pos, row = 1, []
        for name, kind, length, _ in fields:
            text = record[pos:pos + length].decode("latin-1").strip()
            pos += length
            if kind in "NF" and text and set(text) != {"*"}:
                row.append(_r(text))
            else:
                row.append(text)
        rows.append([row[i] for i in keep])
    return {"fields": [list(fields[i]) for i in keep], "rows": rows}


def _shapefile(stem):
    head = open(stem + ".shp", "rb").read(100)
    kind = struct.unpack("<i", head[32:36])[0]
    box = [_r(v, 3) for v in struct.unpack("<4d", head[36:68])]
    records = (os.path.getsize(stem + ".shx") - 100) // 8
    return {"shapeType": kind, "box": box, "records": records, "dbf": _dbf(stem + ".dbf"),
            "prj": os.path.exists(stem + ".prj")}


def _xlsx_rows(path):
    ns = {"m": "http://schemas.openxmlformats.org/spreadsheetml/2006/main"}
    with zipfile.ZipFile(path) as z:
        shared = []
        if "xl/sharedStrings.xml" in z.namelist():
            for si in ET.fromstring(z.read("xl/sharedStrings.xml")).findall("m:si", ns):
                shared.append("".join(t.text or "" for t in si.iter("{%s}t" % ns["m"])))
        sheets = sorted(n for n in z.namelist() if n.startswith("xl/worksheets/sheet"))
        rows = []
        for row in ET.fromstring(z.read(sheets[0])).iter("{%s}row" % ns["m"]):
            values = []
            for c in row.findall("m:c", ns):
                kind, v = c.get("t"), c.find("m:v", ns)
                if kind == "s":
                    values.append(shared[int(v.text)])
                elif kind == "inlineStr":
                    values.append("".join(t.text or "" for t in c.iter("{%s}t" % ns["m"])))
                elif v is not None:
                    values.append(_r(v.text))
                else:
                    values.append(None)
            rows.append(values)
        return {"sheets": len(sheets), "rows": rows}


def _grid_header(lines):
    header = {}
    for line in lines:
        parts = line.split()
        if len(parts) == 2 and parts[0][0].isalpha():
            header[parts[0].lower()] = parts[1] if parts[0].lower() == "byteorder" else _r(parts[1])
    return header


def _stats(values, nodata):
    good = [v for v in values if nodata is None or abs(v - nodata) > 1e-9]
    return {"cells": len(values), "nodata": len(values) - len(good), "sum": _r(sum(good), 2), "min": _r(min(good)),
            "max": _r(max(good)), "first": [_r(v) for v in values[:5]]}


def _ring_area(ring):
    return sum(x0 * y1 - x1 * y0 for (x0, y0, *_), (x1, y1, *_) in zip(ring, ring[1:])) / 2


def _workspace(ap, workspace):
    """Each feature class and table in a workspace — feature datasets walked — by name: rows, fields, shape type."""
    out = {}
    for root, _, names in ap.da.Walk(workspace, datatype=["FeatureClass", "Table"]):
        for name in names:
            path = os.path.join(root, name)
            fields = sorted(f.name.lower() for f in ap.ListFields(path)
                            if f.type not in ("OID", "Geometry") and f.name.lower() not in ("shape_length",
                                                                                            "shape_area"))
            describe = ap.Describe(path)
            # A table's shape type is not compared: grapy's Describe answers "Geometry" for a table, where Pro's has
            # none (an arcpy.Describe difference, not one of these tools').
            # grapy's da.Walk names a class of a feature dataset "Dataset/Class"; the class's own name is compared.
            out[os.path.splitext(name)[0].split("/")[-1].lower()] = {
                "rows": int(ap.management.GetCount(path)[0]), "fields": fields,
                "shapeType": None if describe.dataType == "Table" else getattr(describe, "shapeType", None)}
    return out


# ---- cases ---------------------------------------------------------------------------------------------------------

@case("conversion.ASCIIToRaster")
def _(ap, I, O):
    grid = _ascii_grid(_folder(O, "in"))
    ap.conversion.ASCIIToRaster(grid, O("out", "raster"), "FLOAT")


@case("conversion.ConvertLas")
def _(ap, I, O):
    las = _las(os.path.join(_folder(O, "in"), "ground.las"), _las_points(I)[:500])
    folder = _folder(O, "las")
    ap.conversion.ConvertLas(las, folder, "1.4", 6, define_coordinate_system="FILES_MISSING_PROJECTION",
                             in_coordinate_system=ap.SpatialReference(I["srid"]))
    written = sorted(f for f in os.listdir(folder) if f.lower().endswith(".las"))
    head = open(os.path.join(folder, written[0]), "rb").read(375)
    major, minor = head[24], head[25]
    point_format, record = head[104], struct.unpack("<H", head[105:107])[0]
    legacy = struct.unpack("<I", head[107:111])[0]
    count = struct.unpack("<Q", head[247:255])[0] if (major, minor) >= (1, 4) else legacy
    bounds = [_r(v, 2) for v in struct.unpack("<6d", head[179:227])]
    return {"files": written, "version": f"{major}.{minor}", "pointFormat": point_format & 0x3F,
            "recordLength": record, "points": count, "bounds": bounds}


@case("conversion.ExcelToTable")
def _(ap, I, O):
    book = _xlsx(os.path.join(_folder(O, "in"), "book.xlsx"), "Data",
                 [["name", "count", "share"], ["alpha", 3, 0.25], ["beta", 7, 0.5], ["gamma", None, 1.75]])
    ap.conversion.ExcelToTable(book, O("out"), "Data")


@case("conversion.ExportFeatures")
def _(ap, I, O):
    ap.conversion.ExportFeatures(I["points"], O("out"), "val > 50")


@case("conversion.ExportTable")
def _(ap, I, O):
    ap.conversion.ExportTable(I["table"], O("out"), "val > 5")


@case("conversion.ExportToParquet")
def _(ap, I, O):
    import shutil
    import tempfile
    import pyarrow.parquet as pq
    _folder(O, "pq")
    # Pro 3.7 writes nothing — and reports success, with "General function failure" for every feature — when the output
    # path is longer than about 130 characters (measured 2026-11-08: 126 wrote, 136 did not), and the harness's folders
    # are longer than that; so the file goes to a short folder of the system's temporary directory and is removed.
    folder = tempfile.mkdtemp(prefix="pq")
    try:
        path = os.path.join(folder, "polys.parquet")
        ap.conversion.ExportToParquet(I["polys"], path)
        table = pq.read_table(path)
    finally:
        shutil.rmtree(folder, ignore_errors=True)
    meta = json.loads((table.schema.metadata or {}).get(b"geo", b"{}"))
    oid, shape = _oid(ap, I["polys"]), meta.get("primary_column")
    plain = [n for n in table.column_names if n not in (oid, shape) and n.lower() not in GDB_ONLY]
    return {"columns": plain, "types": [str(table.schema.field(n).type) for n in plain], "rows": table.num_rows,
            "oid": table.column(oid).to_pylist() if oid in table.column_names else None,
            "shape": str(table.schema.field(shape).type) if shape in table.column_names else None,
            "values": {n: table.column(n).to_pylist() for n in plain},
            "encodings": sorted(c.get("encoding") for c in meta.get("columns", {}).values())}


@case("conversion.FeatureClassToFeatureClass")
def _(ap, I, O):
    out = O("out")
    ap.conversion.FeatureClassToFeatureClass(I["polys"], os.path.dirname(out), os.path.basename(out), "val > 3")


@case("conversion.FeatureClassToGeodatabase")
def _(ap, I, O):
    gdb = ap.management.CreateFileGDB(_folder(O, "ws"), "copy")[0]
    ap.conversion.FeatureClassToGeodatabase([I["points"], I["polys"]], gdb)
    return _workspace(ap, gdb)


@case("conversion.FeatureClassToShapefile")
def _(ap, I, O):
    folder = _folder(O, "shp")
    ap.conversion.FeatureClassToShapefile([I["polys"], I["points"]], folder)
    return {name: _shapefile(os.path.join(folder, name)) for name in ("polys", "points")}


@case("conversion.FeatureToRaster")
def _(ap, I, O):
    ap.conversion.FeatureToRaster(I["polys"], "gid", O("out", "raster"), 10)


@case("conversion.FeaturesToGPX")
def _(ap, I, O):
    path = os.path.join(_folder(O, "gpx"), "points.gpx")
    ap.conversion.FeaturesToGPX(I["points"], path, "cat", None, "val")
    root = ET.parse(path).getroot()
    ns = root.tag.split("}")[0] + "}" if root.tag.startswith("{") else ""
    points = []
    for wpt in root.iter(ns + "wpt"):
        points.append([_r(wpt.get("lat"), 7), _r(wpt.get("lon"), 7),
                       *[(wpt.findtext(ns + tag) or "").strip() for tag in ("name", "desc")],
                       _r(wpt.findtext(ns + "ele"), 3) if wpt.findtext(ns + "ele") else None])
    return {"version": root.get("version"), "waypoints": points,
            "tracks": len(list(root.iter(ns + "trk"))), "routes": len(list(root.iter(ns + "rte")))}


@case("conversion.FeaturesToJSON")
def _(ap, I, O):
    path = os.path.join(_folder(O, "json"), "polys.json")
    ap.conversion.FeaturesToJSON(I["polys"], path)
    doc = json.load(open(path, encoding="utf-8"))
    oid = [f["name"] for f in doc["fields"] if f["type"] == "esriFieldTypeOID"]
    named = _oid(ap, I["polys"])
    features = []
    for f in doc["features"]:
        attributes = {k: (_r(v) if isinstance(v, float) else v) for k, v in f["attributes"].items()
                      if k not in oid and k.lower() not in ("shape_length", "shape_area", "shape__length",
                                                           "shape__area")}
        rings = f["geometry"]["rings"]
        features.append({"attributes": attributes, "rings": len(rings),
                         "area": _r(sum(_ring_area(r) for r in rings), 3)})
    return {"geometryType": doc["geometryType"], "wkid": doc["spatialReference"].get("wkid"),
            "latestWkid": doc["spatialReference"].get("latestWkid"),
            "fields": [["<OID>" if f["name"] == named else f["name"], f["type"]] for f in doc["fields"]
                       if f["name"].lower() not in GDB_ONLY],
            "features": features}


@case("conversion.FloatToRaster")
def _(ap, I, O):
    ap.conversion.FloatToRaster(_float_grid(_folder(O, "in")), O("out", "raster"))


@case("conversion.GPXtoFeatures")
def _(ap, I, O):
    path = _write(os.path.join(_folder(O, "in"), "trip.gpx"), GPX)
    ap.conversion.GPXtoFeatures(path, O("out"))


@case("conversion.GTFSShapesToFeatures")
def _(ap, I, O):
    path = _write(os.path.join(_folder(O, "in"), "shapes.txt"), SHAPES)
    ap.conversion.GTFSShapesToFeatures(path, O("out"))


@case("conversion.GTFSStopsToFeatures")
def _(ap, I, O):
    path = _write(os.path.join(_folder(O, "in"), "stops.txt"), STOPS)
    ap.conversion.GTFSStopsToFeatures(path, O("out"))


@case("conversion.JSONToFeatures")
def _(ap, I, O):
    path = _write(os.path.join(_folder(O, "in"), "polys.json"), json.dumps(ESRI_JSON))
    ap.conversion.JSONToFeatures(path, O("out"))


@case("conversion.KMLToLayer")
def _(ap, I, O):
    path = _write(os.path.join(_folder(O, "in"), "places.kml"), KML)
    folder = _folder(O, "kml")
    ap.conversion.KMLToLayer(path, folder, "places")
    gdb = [f for f in os.listdir(folder) if f.lower().endswith((".gdb", ".gpkg"))]
    return {"workspace": _workspace(ap, os.path.join(folder, gdb[0])) if gdb else None,
            "files": sorted(os.path.splitext(f)[1].lower() for f in os.listdir(folder))}


@case("conversion.LasDatasetToRaster")
def _(ap, I, O):
    folder = _folder(O, "in")
    las = _las(os.path.join(folder, "ground.las"), _las_points(I))
    dataset = os.path.join(folder, "ground.lasd")
    ap.management.CreateLasDataset(las, dataset, spatial_reference=ap.SpatialReference(I["srid"]))
    ap.conversion.LasDatasetToRaster(dataset, O("out", "raster"), "ELEVATION", "BINNING AVERAGE LINEAR", "FLOAT",
                                     "CELLSIZE", 20)


@case("conversion.MultipatchToCollada")
def _(ap, I, O):
    mp = _multipatch(ap, I, O)
    folder = _folder(O, "dae")
    ap.conversion.MultipatchToCollada(mp, folder)
    files = sorted(os.listdir(folder))
    out = {"files": [os.path.splitext(f)[1].lower() for f in files]}
    for f in files:
        if f.lower().endswith(".dae"):
            root = ET.parse(os.path.join(folder, f)).getroot()
            ns = root.tag.split("}")[0] + "}"
            arrays = [a for a in root.iter(ns + "float_array") if "position" in (a.get("id") or "").lower()]
            values = [float(v) for a in arrays for v in a.text.split()]
            xyz = list(zip(values[0::3], values[1::3], values[2::3]))
            out["version"] = root.get("version")
            out["span"] = [_r(max(c) - min(c), 3) for c in zip(*xyz)] if xyz else None
            out["up"] = root.findtext(f"{ns}asset/{ns}up_axis")
    return out


@case("conversion.MultipatchToRaster")
def _(ap, I, O):
    ap.conversion.MultipatchToRaster(_multipatch(ap, I, O), O("out", "raster"), 10)


@case("conversion.PointToRaster")
def _(ap, I, O):
    ap.conversion.PointToRaster(I["points"], "val", O("out", "raster"), "MEAN", None, 50)


@case("conversion.PolygonToRaster")
def _(ap, I, O):
    ap.conversion.PolygonToRaster(I["polys"], "gid", O("out", "raster"), "CELL_CENTER", None, 10)


@case("conversion.PolylineToRaster")
def _(ap, I, O):
    ap.conversion.PolylineToRaster(I["lines"], "lid", O("out", "raster"), "MAXIMUM_LENGTH", None, 10)


@case("conversion.RasterToASCII")
def _(ap, I, O):
    path = os.path.join(_folder(O, "asc"), "dem.asc")
    ap.conversion.RasterToASCII(I["dem"], path)
    lines = open(path, encoding="ascii").read().splitlines()
    header = _grid_header(lines[:6])
    values = [float(v) for line in lines[len(header):] for v in line.split()]
    return {"header": header, "stats": _stats(values, header.get("nodata_value"))}


@case("conversion.RasterToFloat")
def _(ap, I, O):
    import numpy as np
    path = os.path.join(_folder(O, "flt"), "dem.flt")
    ap.conversion.RasterToFloat(I["dem"], path)
    header = _grid_header(open(path[:-4] + ".hdr", encoding="ascii").read().splitlines())
    order = "<" if str(header.get("byteorder", "LSBFIRST")).upper() == "LSBFIRST" else ">"
    values = np.fromfile(path, dtype=order + "f4").astype(float).tolist()
    return {"header": header, "stats": _stats(values, header.get("nodata_value"))}


@case("conversion.RasterToPoint")
def _(ap, I, O):
    import numpy as np
    small = os.path.join(_folder(O, "in"), "small.tif")
    values = (np.arange(36).reshape(6, 6) % 7).astype("int32")
    values[2, 3] = -1
    ap.env.outputCoordinateSystem = ap.SpatialReference(I["srid"])
    ap.NumPyArrayToRaster(values, ap.Point(I["x0"], I["y0"]), 10, 10, -1).save(small)
    ap.env.outputCoordinateSystem = None
    ap.conversion.RasterToPoint(small, O("out"))


@case("conversion.RasterToPolygon")
def _(ap, I, O):
    ap.conversion.RasterToPolygon(I["zones"], O("out"), "NO_SIMPLIFY")


@case("conversion.TableToDBASE")
def _(ap, I, O):
    folder = _folder(O, "dbf")
    ap.conversion.TableToDBASE(I["table"], folder)
    return _dbf(os.path.join(folder, "rows.dbf"))


@case("conversion.TableToExcel")
def _(ap, I, O):
    path = os.path.join(_folder(O, "xls"), "rows.xlsx")
    ap.conversion.TableToExcel(I["table"], path)
    read = _xlsx_rows(path)
    oid = _oid(ap, I["table"])
    read["rows"][0] = ["<OID>" if v == oid else v for v in read["rows"][0]]
    return read


@case("conversion.TableToGeodatabase")
def _(ap, I, O):
    gdb = ap.management.CreateFileGDB(_folder(O, "ws"), "copy")[0]
    ap.conversion.TableToGeodatabase(I["table"], gdb)
    return _workspace(ap, gdb)


@case("conversion.TableToTable")
def _(ap, I, O):
    out = O("out")
    ap.conversion.TableToTable(I["table"], os.path.dirname(out), os.path.basename(out), "val > 5")
