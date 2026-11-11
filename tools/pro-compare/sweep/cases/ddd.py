"""arcpy.ddd against ArcGIS Pro 3.7: one case for each of the 129 names grapy builds.

Inputs beyond the shared fixtures are built here with ``ap``: 3D points, lines and polygons (WKT with z), observers, a
TIN from the 3D points, LAS files (written byte by byte by management_1's ``write_las``, so both sides read the same
file) and their LAS datasets, a terrain and multipatches. Pro keeps a TIN as a folder and a multipatch in the file
geodatabase; grapy as it holds them. Containers and helpers a case does not compare are named through ``N()`` or
written into the work folder, so nothing is dumped for them. Shapes are compared with z (compare.py's
``_z_distance``).
"""
import os
import shutil

from cases.management_1 import new_fc, write_las

CASES = {}


def case(name):
    def register(function):
        CASES[name] = function
        return function
    return register


def N(I, case_name, name):
    return os.path.join(I["gdb"], f"{case_name}_{name}")


def W(I, name):
    """A path in the work folder (a TIN, a LAS dataset, a text file) that is not dumped."""
    return os.path.join(I["work"], name)


def fresh(path):
    if os.path.isdir(path):
        shutil.rmtree(path)
    elif os.path.exists(path):
        os.remove(path)
    return path


def r(v, n=4):
    return round(float(v), n) if v is not None else None


def X(I, x, y):
    return I["x0"] + x, I["y0"] + y


# ---- 3D inputs --------------------------------------------------------------------------------------------------

def points3d(ap, I):
    """Twenty-five 3D points on the DEM's surface rounded to 0.1 m, on a 120 m grid, with the z in a field too."""
    import fixtures
    path = os.path.join(I["gdb"], "pts3d")
    if ap.Exists(path):
        return path
    rows = []
    n = 0
    for i in range(5):
        for j in range(5):
            n += 1
            x, y = 60 + 120 * i + (7 * j) % 13, 60 + 120 * j + (5 * i) % 11
            z = round(fixtures.dem_value(x, y), 1)
            rows.append((n, z, f"POINT Z ({I['x0'] + x} {I['y0'] + y} {z})"))
    return new_fc(ap, I, I["gdb"], "pts3d", "POINT", [("pid", "LONG"), ("elev", "DOUBLE")], rows, "ENABLED")


def lines3d(ap, I):
    path = os.path.join(I["gdb"], "lines3d")
    if ap.Exists(path):
        return path
    x0, y0 = I["x0"], I["y0"]
    rows = [
        (1, 5.0, f"LINESTRING Z ({x0 + 50} {y0 + 50} 100, {x0 + 250} {y0 + 120} 130, {x0 + 450} {y0 + 80} 110)"),
        (2, 2.0, f"LINESTRING Z ({x0 + 100} {y0 + 500} 120, {x0 + 300} {y0 + 400} 160, {x0 + 500} {y0 + 450} 140)"),
        (3, 1.0, f"LINESTRING Z ({x0 + 300} {y0 + 50} 105, {x0 + 300} {y0 + 550} 145)"),
    ]
    return new_fc(ap, I, I["gdb"], "lines3d", "POLYLINE", [("lid", "LONG"), ("h", "DOUBLE")], rows, "ENABLED")


def polys3d(ap, I):
    path = os.path.join(I["gdb"], "polys3d")
    if ap.Exists(path):
        return path
    x0, y0 = I["x0"], I["y0"]

    def sq(a, b, c, d, z):
        return (f"POLYGON Z (({x0 + a} {y0 + b} {z}, {x0 + a} {y0 + d} {z}, {x0 + c} {y0 + d} {z}, "
                f"{x0 + c} {y0 + b} {z}, {x0 + a} {y0 + b} {z}))")
    rows = [(1, 10.0, 120.0, sq(50, 50, 250, 250, 120)), (2, 25.0, 140.0, sq(320, 320, 520, 520, 140))]
    return new_fc(ap, I, I["gdb"], "polys3d", "POLYGON", [("gid", "LONG"), ("ht", "DOUBLE"), ("base", "DOUBLE")],
                  rows, "ENABLED")


def observers(ap, I, z=False):
    name = "obs3d" if z else "obs"
    path = os.path.join(I["gdb"], name)
    if ap.Exists(path):
        return path
    x0, y0 = I["x0"], I["y0"]
    if z:
        rows = [(1, 5.0, f"POINT Z ({x0 + 105} {y0 + 105} 120)"), (2, 3.0, f"POINT Z ({x0 + 455} {y0 + 455} 150)")]
    else:
        rows = [(1, 5.0, f"POINT ({x0 + 105} {y0 + 105})"), (2, 3.0, f"POINT ({x0 + 455} {y0 + 455})")]
    return new_fc(ap, I, I["gdb"], name, "POINT", [("oid2", "LONG"), ("offseta", "DOUBLE")], rows,
                  "ENABLED" if z else "DISABLED")


def tin(ap, I, name="tin"):
    """A Delaunay TIN of the 25 3D points (mass points from their z)."""
    path = W(I, name)
    if not os.path.exists(path) and not ap.Exists(path):
        ap.ddd.CreateTin(path, ap.SpatialReference(I["srid"]), f"{points3d(ap, I)} Shape.Z Mass_Points <None>",
                         "DELAUNAY")
    return path


def lasd(ap, I, name, n=8, z0=100.0, step=10.0):
    """A LAS dataset over one LAS file: an n × n grid of points, ground (2) on a tilted plane with every third point
    lifted 6 m and classed unassigned (1)."""
    folder = fresh(W(I, name + "_las"))
    os.makedirs(folder)
    pts = []
    for i in range(n):
        for j in range(n):
            x, y = I["x0"] + 100 + step * i + 2.5, I["y0"] + 100 + step * j + 2.5
            z = z0 + 0.5 * i + 0.25 * j
            lifted = (i * n + j) % 3 == 0
            pts.append((x, y, z + (6.0 if lifted else 0.0), 1 if lifted else 2))
    write_las(os.path.join(folder, "a.las"), pts)
    path = fresh(W(I, name + ".lasd"))
    ap.management.CreateLasDataset(folder, path, spatial_reference=ap.SpatialReference(I["srid"]),
                                   compute_stats="COMPUTE_STATS")
    return path


def read_las(path):
    """Every point of a LAS file written by write_las or by a tool: [(x, y, z, class)] rounded, sorted."""
    import struct
    data = open(path, "rb").read()
    header_size, offset = struct.unpack_from("<HI", data, 94)
    fmt, length = struct.unpack_from("<BH", data, 104)
    count = struct.unpack_from("<I", data, 107)[0]
    if count == 0 and len(data) >= 255:
        count = struct.unpack_from("<Q", data, 247)[0]
    sx, sy, sz, ox, oy, oz = struct.unpack_from("<6d", data, 131)
    out = []
    for k in range(count):
        base = offset + k * length
        xi, yi, zi = struct.unpack_from("<3i", data, base)
        cls = data[base + 15] & 0x1F if fmt < 6 else data[base + 16]
        out.append((round(xi * sx + ox, 2), round(yi * sy + oy, 2), round(zi * sz + oz, 2), cls))
    return sorted(out)


def las_files(folder):
    return {name: read_las(os.path.join(folder, name)) for name in sorted(os.listdir(folder))
            if name.lower().endswith(".las")}


def class_counts(ap, lasd_path):
    """Class code → count over the dataset's LAS files (those in the folder lasd() wrote them to)."""
    counts = {}
    for f in las_paths(ap, lasd_path):
        for *_, cls in read_las(f):
            counts[str(cls)] = counts.get(str(cls), 0) + 1
    return dict(sorted(counts.items()))


def las_paths(ap, lasd_path):
    folder = lasd_path[:-5] + "_las"
    return [os.path.join(folder, n) for n in sorted(os.listdir(folder)) if n.lower().endswith(".las")]


def rows_of(ap, path, fields, digits=3):
    with ap.da.SearchCursor(path, fields) as c:
        return sorted([[round(v, digits) if isinstance(v, float) else v for v in row] for row in c], key=str)


# ---- rasters: arcpy.sa's computations, saved -------------------------------------------------------------------

@case("ddd.Slope")
def _(ap, I, O):
    ap.ddd.Slope(I["dem"], O("out", "raster"))


@case("ddd.Aspect")
def _(ap, I, O):
    ap.ddd.Aspect(I["dem"], O("out", "raster"))


@case("ddd.HillShade")
def _(ap, I, O):
    ap.ddd.HillShade(I["dem"], O("out", "raster"))


@case("ddd.Reclassify")
def _(ap, I, O):
    ap.ddd.Reclassify(I["zones"], "Value", "1 10;2 20;3 30;4 40", O("out", "raster"))


@case("ddd.ReclassByTable")
def _(ap, I, O):
    t = N(I, "rbt", "remap")
    ap.management.CreateTable(*os.path.split(t))
    for f in ("lo", "hi", "to"):
        ap.management.AddField(t, f, "LONG")
    with ap.da.InsertCursor(t, ["lo", "hi", "to"]) as c:
        for row in ((1, 2, 5), (3, 4, 7)):
            c.insertRow(row)
    ap.ddd.ReclassByTable(I["zones"], t, "lo", "hi", "to", O("out", "raster"))


@case("ddd.ReclassByASCIIFile")
def _(ap, I, O):
    path = W(I, "remap_ddd.txt")
    with open(path, "w") as f:
        # One format throughout: Pro accepts single values and ranges mixed, grapy (arcpy.sa's parser, shared) refuses
        # them on the tool page's word; that difference is the sa sweep's.
        f.write("1 1 : 100\n2 2 : 200\n3 4 : 300\n")
    ap.ddd.ReclassByASCIIFile(I["zones"], path, O("out", "raster"))


@case("ddd.Slice")
def _(ap, I, O):
    ap.ddd.Slice(I["dem"], O("out", "raster"), 5)


@case("ddd.Plus")
def _(ap, I, O):
    ap.ddd.Plus(I["dem"], I["zones"], O("out", "raster"))


@case("ddd.Minus")
def _(ap, I, O):
    ap.ddd.Minus(I["zones"], 3, O("out", "raster"))


@case("ddd.Times")
def _(ap, I, O):
    ap.ddd.Times(I["zones"], 2.5, O("out", "raster"))


@case("ddd.Float")
def _(ap, I, O):
    ap.ddd.Float(I["zones"], O("out", "raster"))


@case("ddd.Int")
def _(ap, I, O):
    ap.ddd.Int(I["dem"], O("out", "raster"))


@case("ddd.Divide")
def _(ap, I, O):
    # Two integer inputs, one of them negative in places: the page says integer division; the sign rule is measured.
    neg = W(I, "divide_neg.tif")
    ap.ddd.Minus(I["zones"], 3, neg)
    ap.ddd.Divide(neg, 2, O("int", "raster"))
    ap.ddd.Divide(I["zones"], 2.0, O("float", "raster"))
    ap.ddd.Divide(I["zones"], 2.5, O("frac", "raster"))
    ap.ddd.Divide(I["dem"], 2, O("dem", "raster"))


@case("ddd.Idw")
def _(ap, I, O):
    ap.ddd.Idw(I["points"], "val", O("out", "raster"), 20)


@case("ddd.Trend")
def _(ap, I, O):
    ap.ddd.Trend(I["points"], "val", O("out", "raster"), 20, 2)


@case("ddd.Curvature")
def _(ap, I, O):
    ap.ddd.Curvature(I["dem"], O("out", "raster"))


@case("ddd.Contour")
def _(ap, I, O):
    ap.ddd.Contour(I["dem"], O("out"), 10)


@case("ddd.ContourList")
def _(ap, I, O):
    ap.ddd.ContourList(I["dem"], O("out"), [110, 125, 140])


@case("ddd.AddSurfaceInformation")
def _(ap, I, O):
    out = ap.management.CopyFeatures(I["lines"], O("out"))[0]
    ap.ddd.AddSurfaceInformation(out, I["dem"], "Z_MIN;Z_MAX;Z_MEAN;SURFACE_LENGTH;MIN_SLOPE;MAX_SLOPE;AVG_SLOPE")


@case("ddd.Kriging")
def _(ap, I, O):
    ap.ddd.Kriging(I["points"], "val", O("out", "raster"), "Spherical", 20)


@case("ddd.NaturalNeighbor")
def _(ap, I, O):
    ap.ddd.NaturalNeighbor(I["points"], "val", O("out", "raster"), 20)


@case("ddd.Spline")
def _(ap, I, O):
    ap.ddd.Spline(I["points"], "val", O("out", "raster"), 20)


@case("ddd.SplineWithBarriers")
def _(ap, I, O):
    ap.ddd.SplineWithBarriers(I["points"], "val", I["lines"], 20, O("out", "raster"))


@case("ddd.TopoToRaster")
def _(ap, I, O):
    ap.ddd.TopoToRaster(f"{I['points']} val PointElevation", O("out", "raster"), 20)


@case("ddd.TopoToRasterByFile")
def _(ap, I, O):
    params = fresh(W(I, "topo_params.txt"))
    ap.ddd.TopoToRaster(f"{I['points']} val PointElevation", W(I, "topo_first.tif"), 20, out_parameter_file=params)
    ap.ddd.TopoToRasterByFile(params, O("out", "raster"))


@case("ddd.Viewshed")
def _(ap, I, O):
    ap.ddd.Viewshed(I["dem"], observers(ap, I), O("out", "raster"))


@case("ddd.Visibility")
def _(ap, I, O):
    ap.ddd.Visibility(I["dem"], observers(ap, I), O("out", "raster"))


@case("ddd.ObserverPoints")
def _(ap, I, O):
    ap.ddd.ObserverPoints(I["dem"], observers(ap, I), O("out", "raster"))


@case("ddd.Viewshed2")
def _(ap, I, O):
    ap.ddd.Viewshed2(I["dem"], observers(ap, I), O("out", "raster"), analysis_target_device="CPU_ONLY")


@case("ddd.Lookup")
def _(ap, I, O):
    ap.ddd.Lookup(I["zones"], "Count", O("out", "raster"))


@case("ddd.CutFill")
def _(ap, I, O):
    a = ap.RasterToNumPyArray(I["dem"]).astype("float32")
    a[10:20, 10:20] += 5.0
    a[40:50, 30:45] -= 3.0
    after = fresh(W(I, "cutfill_after.tif"))
    ap.env.outputCoordinateSystem = ap.SpatialReference(I["srid"])
    ap.NumPyArrayToRaster(a, ap.Point(I["x0"], I["y0"]), I["cell"], I["cell"]).save(after)
    ap.env.outputCoordinateSystem = None
    old = ap.env.scratchWorkspace
    ap.env.scratchWorkspace = I["work"]   # Pro's CutFill fails (010167, in ZonalStatisticsAsTable) without one
    try:
        ap.ddd.CutFill(I["dem"], after, O("out", "raster"))
    finally:
        ap.env.scratchWorkspace = old
    return rows_of(ap, O("out", "raster"), ["VALUE", "COUNT", "AREA", "VOLUME"], 2)


@case("ddd.SurfaceParameters")
def _(ap, I, O):
    ap.ddd.SurfaceParameters(I["dem"], O("out", "raster"))


@case("ddd.ContourWithBarriers")
def _(ap, I, O):
    ap.ddd.ContourWithBarriers(I["dem"], O("out"), None, "POLYLINES", None, "NO_EXPLICIT_VALUES_ONLY", 0, 10)


# ---- 3D features ------------------------------------------------------------------------------------------------

@case("ddd.FeatureTo3DByAttribute")
def _(ap, I, O):
    ap.ddd.FeatureTo3DByAttribute(I["polys"], O("out"), "val")


@case("ddd.AddZInformation")
def _(ap, I, O):
    out = ap.management.CopyFeatures(lines3d(ap, I), O("out"))[0]
    ap.ddd.AddZInformation(out, "Z_MIN;Z_MAX;Z_MEAN;LENGTH_3D;MIN_SLOPE;MAX_SLOPE;AVG_SLOPE;VERTEX_COUNT")


@case("ddd.Near3D")
def _(ap, I, O):
    out = ap.management.CopyFeatures(points3d(ap, I), O("out"))[0]
    ap.ddd.Near3D(out, lines3d(ap, I), None, "LOCATION", "ANGLE", "DELTA")


@case("ddd.InterpolateShape")
def _(ap, I, O):
    ap.ddd.InterpolateShape(I["dem"], I["lines"], O("out"))


@case("ddd.UpdateFeatureZ")
def _(ap, I, O):
    out = ap.management.CopyFeatures(lines3d(ap, I), O("out"))[0]
    ap.ddd.UpdateFeatureZ(out, I["dem"])


@case("ddd.Simplify3DLine")
def _(ap, I, O):
    ap.ddd.Simplify3DLine(lines3d(ap, I), O("out"), "5 Meters")


@case("ddd.GeneratePointsAlong3DLines")
def _(ap, I, O):
    ap.ddd.GeneratePointsAlong3DLines(lines3d(ap, I), O("out"), "DISTANCE", "100 Meters")


@case("ddd.FeatureClassZToASCII")
def _(ap, I, O):
    folder = O("ascii", "folder")
    for f in os.listdir(folder):
        os.remove(os.path.join(folder, f))
    ap.ddd.FeatureClassZToASCII(lines3d(ap, I), folder, "z.txt", "GENERATE", "SPACE", "FIXED", 2)
    # Pro also writes z.txt.xml, its metadata sidecar; only the text file is compared.
    return {f: open(os.path.join(folder, f)).read() for f in sorted(os.listdir(folder)) if f.endswith(".txt")}


@case("ddd.ASCII3DToFeatureClass")
def _(ap, I, O):
    folder = O("ascii", "folder")
    path = os.path.join(folder, "pts.xyz")
    with open(path, "w") as f:
        for k in range(6):
            f.write(f"{I['x0'] + 50 + 40 * k} {I['y0'] + 60 + 30 * k} {100 + 2.5 * k}\n")
    sr = ap.SpatialReference(I["srid"])
    ap.ddd.ASCII3DToFeatureClass(path, "XYZ", O("pts"), "POINT", input_coordinate_system=sr)
    ap.ddd.ASCII3DToFeatureClass(path, "XYZ", O("line"), "POLYLINE", input_coordinate_system=sr)


@case("ddd.ConstructSightLines")
def _(ap, I, O):
    ap.ddd.ConstructSightLines(observers(ap, I, True), points3d(ap, I), O("out"))


@case("ddd.SurfaceLength")
def _(ap, I, O):
    out = ap.management.CopyFeatures(I["lines"], O("out"))[0]
    ap.ddd.SurfaceLength(I["dem"], out)


@case("ddd.SurfaceSpot")
def _(ap, I, O):
    out = ap.management.CopyFeatures(I["points"], O("out"))[0]
    ap.ddd.SurfaceSpot(I["dem"], out)


@case("ddd.StackProfile")
def _(ap, I, O):
    out = ap.ddd.StackProfile(I["lines"], I["dem"], os.path.join(I["gdb"], "stack_out"))[0]
    # SRC_NAME is the raster's whole path, so only its end is compared; rows are answered rounded to the centimetre
    # (Pro's DISTANCE carries noise near 1e-5 m) in line and distance order.
    rows = [[lid, targ, round(d, 2), round(z, 2), src[-13:]] for d, z, lid, src, targ in
            ap.da.SearchCursor(out, ["DISTANCE", "Z", "LINE_ID", "SRC_NAME", "LINE_TARG"])]
    return sorted(rows, key=lambda row: (row[0], row[2]))


@case("ddd.RegularizeBuildingFootprint")
def _(ap, I, O):
    ap.ddd.RegularizeBuildingFootprint(I["polys"], O("out"), "RIGHT_ANGLES", "5 Meters")


@case("ddd.RegularizeAdjacentBuildingFootprint")
def _(ap, I, O):
    ap.ddd.RegularizeAdjacentBuildingFootprint(I["polys"], "cat", O("out"), "RIGHT_ANGLES", "5 Meters")


@case("ddd.SkylineBarrier")
def _(ap, I, O):
    ap.ddd.SkylineBarrier(observers(ap, I, True), lines3d(ap, I), O("out"))


# ---- TINs (EditTin's rows are in_features, height_field, tag_field, SF_type: tag before type, unlike CreateTin's) -------------------------------------------------------------------------------------------------------

def points3d_b(ap, I):
    """The 25 3D points again, z raised 4 m on every third and lowered 3 m on every fifth."""
    path = os.path.join(I["gdb"], "pts3d_b")
    if ap.Exists(path):
        return path
    rows = []
    with ap.da.SearchCursor(points3d(ap, I), ["pid", "SHAPE@X", "SHAPE@Y", "SHAPE@Z"]) as c:
        for pid, x, y, z in c:
            z2 = z + (4 if pid % 3 == 0 else 0) - (3 if pid % 5 == 0 else 0)
            rows.append((pid, z2, f"POINT Z ({x} {y} {z2})"))
    return new_fc(ap, I, I["gdb"], "pts3d_b", "POINT", [("pid", "LONG"), ("elev", "DOUBLE")], rows, "ENABLED")


def tin_b(ap, I):
    path = W(I, "tin_b")
    if not os.path.exists(path):
        ap.ddd.CreateTin(path, ap.SpatialReference(I["srid"]), f"{points3d_b(ap, I)} Shape.Z Mass_Points <None>",
                         "DELAUNAY")
    return path


def tin_copy(ap, I, name):
    path = fresh(W(I, name))
    ap.ddd.CopyTin(tin(ap, I), path)
    return path


@case("ddd.CreateTin")
def _(ap, I, O):
    path = fresh(W(I, "ct_tin"))
    ap.ddd.CreateTin(path, ap.SpatialReference(I["srid"]), f"{points3d(ap, I)} Shape.Z Mass_Points <None>",
                     "DELAUNAY")
    ap.ddd.TinNode(path, O("nodes"))
    d = ap.Describe(path)
    return {"dataType": d.dataType, "srid": d.spatialReference.factoryCode}


@case("ddd.CopyTin")
def _(ap, I, O):
    ap.ddd.TinNode(tin_copy(ap, I, "copy_tin"), O("nodes"))


@case("ddd.EditTin")
def _(ap, I, O):
    path = tin_copy(ap, I, "edit_tin")
    ap.ddd.EditTin(path, f"{lines3d(ap, I)} Shape <None> Hard_Line", "CONSTRAINED_DELAUNAY")
    ap.ddd.TinEdge(path, O("edges"), "DATA")


@case("ddd.DelineateTinDataArea")
def _(ap, I, O):
    path = tin_copy(ap, I, "dda_tin")
    ap.ddd.DelineateTinDataArea(path, 130, "PERIMETER_ONLY")
    ap.ddd.TinDomain(path, O("domain"), "POLYGON")


@case("ddd.TinDomain")
def _(ap, I, O):
    ap.ddd.TinDomain(tin(ap, I), O("poly"), "POLYGON")
    ap.ddd.TinDomain(tin(ap, I), O("line"), "LINE")


@case("ddd.TinEdge")
def _(ap, I, O):
    ap.ddd.TinEdge(tin(ap, I), O("out"), "DATA")


@case("ddd.TinNode")
def _(ap, I, O):
    ap.ddd.TinNode(tin(ap, I), O("out"))


@case("ddd.TinTriangle")
def _(ap, I, O):
    ap.ddd.TinTriangle(tin(ap, I), O("out"), "DEGREE", 1, "HILLSHADE 310,45")


@case("ddd.TinLine")
def _(ap, I, O):
    path = tin_copy(ap, I, "line_tin")
    ap.ddd.EditTin(path, f"{lines3d(ap, I)} Shape lid Hard_Line", "CONSTRAINED_DELAUNAY")
    ap.ddd.TinLine(path, O("out"))


@case("ddd.TinPolygonTag")
def _(ap, I, O):
    path = tin_copy(ap, I, "tag_tin")
    ap.ddd.EditTin(path, f"{I['polys']} <None> gid Hardvalue_Fill", "DELAUNAY")
    ap.ddd.TinPolygonTag(path, O("out"))


@case("ddd.TinRaster")
def _(ap, I, O):
    ap.ddd.TinRaster(tin(ap, I), O("out", "raster"), "FLOAT", "LINEAR", "CELLSIZE 20")


@case("ddd.TinContour")
def _(ap, I, O):
    ap.ddd.TinContour(tin(ap, I), O("out"), 10)


@case("ddd.SurfaceContour")
def _(ap, I, O):
    ap.ddd.SurfaceContour(tin(ap, I), O("out"), 10)


@case("ddd.SurfaceSlope")
def _(ap, I, O):
    ap.ddd.SurfaceSlope(tin(ap, I), O("out"))


@case("ddd.SurfaceAspect")
def _(ap, I, O):
    ap.ddd.SurfaceAspect(tin(ap, I), O("out"))


@case("ddd.TinSlope")
def _(ap, I, O):
    ap.ddd.TinSlope(tin(ap, I), O("out"))


@case("ddd.TinAspect")
def _(ap, I, O):
    ap.ddd.TinAspect(tin(ap, I), O("out"))


@case("ddd.SurfaceVolume")
def _(ap, I, O):
    path = fresh(W(I, "surface_volume.txt"))
    ap.ddd.SurfaceVolume(tin(ap, I), path, "ABOVE", 120)
    lines = [line for line in open(path).read().split("\n") if line.strip()]
    # The first column is the surface's path; the rest is compared.
    return [line.split(",")[1:] for line in lines]


@case("ddd.PolygonVolume")
def _(ap, I, O):
    out = ap.management.CopyFeatures(polys3d(ap, I), O("out"))[0]
    ap.ddd.PolygonVolume(tin(ap, I), out, "base", "BELOW")


@case("ddd.TinPolygonVolume")
def _(ap, I, O):
    out = ap.management.CopyFeatures(polys3d(ap, I), O("out"))[0]
    ap.ddd.TinPolygonVolume(tin(ap, I), out, "base", "ABOVE")


@case("ddd.TinDifference")
def _(ap, I, O):
    ap.ddd.TinDifference(tin(ap, I), tin_b(ap, I), O("out"))


@case("ddd.SurfaceDifference")
def _(ap, I, O):
    ap.ddd.SurfaceDifference(tin_b(ap, I), tin(ap, I), O("out"))


LANDXML = """<?xml version="1.0" encoding="UTF-8"?>
<LandXML xmlns="http://www.landxml.org/schema/LandXML-1.2" version="1.2" date="2026-11-08" time="00:00:00">
  <Units><Metric areaUnit="squareMeter" linearUnit="meter" volumeUnit="cubicMeter" temperatureUnit="celsius"
    pressureUnit="milliBars"/></Units>
  <Surfaces>
    <Surface name="ground">
      <Definition surfType="TIN">
        <Pnts>
          <P id="1">{y0} {x0} 100</P>
          <P id="2">{y0} {x1} 110</P>
          <P id="3">{y1} {x1} 130</P>
          <P id="4">{y1} {x0} 105</P>
          <P id="5">{ym} {xm} 125</P>
        </Pnts>
        <Faces>
          <F>1 2 5</F>
          <F>2 3 5</F>
          <F>3 4 5</F>
          <F>4 1 5</F>
        </Faces>
      </Definition>
    </Surface>
  </Surfaces>
</LandXML>
"""


@case("ddd.LandXMLToTin")
def _(ap, I, O):
    folder = O("tins", "folder")
    for f in os.listdir(folder):
        fresh(os.path.join(folder, f))
    xml = W(I, "ground.xml")
    x0, y0 = I["x0"] + 100, I["y0"] + 100
    with open(xml, "w") as f:
        f.write(LANDXML.format(x0=x0, y0=y0, x1=x0 + 200, y1=y0 + 200, xm=x0 + 100, ym=y0 + 100))
    ap.ddd.LandXMLToTin(xml, folder, "lx", "1")
    made = sorted(f for f in os.listdir(folder) if os.path.isdir(os.path.join(folder, f)))
    for k, name in enumerate(made):
        ap.ddd.TinNode(os.path.join(folder, name), O(f"nodes{k}"))
    return made


@case("ddd.TINToMultipatch")
def _(ap, I, O):
    ap.ddd.TINToMultipatch(tin(ap, I), O("out"))


@case("ddd.InterpolatePolyToPatch")
def _(ap, I, O):
    ap.ddd.InterpolatePolyToPatch(tin(ap, I), I["polys"], O("out"))


@case("ddd.ExtrudeBetween")
def _(ap, I, O):
    ap.ddd.ExtrudeBetween(tin(ap, I), tin_b(ap, I), I["clip"], O("out"))


# ---- LAS datasets -----------------------------------------------------------------------------------------------
# lasd() writes one 8 × 8 LAS file at 10 m spacing from (102.5, 102.5): ground (2) on a plane rising 0.5 m east and
# 0.25 m north from 100 m, every third point lifted 6 m and unassigned (1). Tools that change a dataset's points change
# that case's own copy; what is compared is the points read back from the LAS files (x, y, z, class).


def empty(folder):
    folder = fresh(folder)
    os.makedirs(folder)
    return folder


def flat_raster(ap, I, name, value):
    import numpy as np
    path = fresh(W(I, name + ".tif"))
    ap.env.outputCoordinateSystem = ap.SpatialReference(I["srid"])
    ap.NumPyArrayToRaster(np.full((60, 60), value, "float32"), ap.Point(I["x0"], I["y0"]), I["cell"],
                          I["cell"]).save(path)
    ap.env.outputCoordinateSystem = None
    return path


def lasd_box(ap, I):
    """A polygon over the LAS points' south-west quarter (x, y 100 to 140)."""
    path = os.path.join(I["gdb"], "las_box")
    if ap.Exists(path):
        return path
    x0, y0 = I["x0"], I["y0"]
    return new_fc(ap, I, I["gdb"], "las_box", "POLYGON", [("bid", "LONG")],
                  [(1, f"POLYGON (({x0 + 100} {y0 + 100}, {x0 + 100} {y0 + 140}, {x0 + 140} {y0 + 140}, "
                       f"{x0 + 140} {y0 + 100}, {x0 + 100} {y0 + 100}))")])


@case("ddd.LasDatasetToTin")
def _(ap, I, O):
    d = lasd(ap, I, "l2t")
    tin_path = fresh(W(I, "l2t_tin"))
    ap.ddd.LasDatasetToTin(d, tin_path)
    ap.ddd.TinNode(tin_path, O("nodes"))


@case("ddd.LASToMultipoint")
def _(ap, I, O):
    d = lasd(ap, I, "l2m")
    ap.ddd.LASToMultipoint(las_paths(ap, d)[0], O("out"), 5, input_coordinate_system=ap.SpatialReference(I["srid"]))


@case("ddd.ExtractLas")
def _(ap, I, O):
    d = lasd(ap, I, "xl")
    folder = empty(W(I, "xl_out"))
    x0, y0 = I["x0"], I["y0"]
    ap.ddd.ExtractLas(d, folder, f"{x0 + 100} {y0 + 100} {x0 + 140} {y0 + 140}")
    return las_files(folder)


@case("ddd.ChangeLasClassCodes")
def _(ap, I, O):
    d = lasd(ap, I, "clc")
    ap.ddd.ChangeLasClassCodes(d, "1 6")
    return class_counts(ap, d)


@case("ddd.ClassifyLasByHeight")
def _(ap, I, O):
    d = lasd(ap, I, "clh")
    ap.ddd.ClassifyLasByHeight(d, "GROUND", "3 1;4 3;5 25")
    return class_counts(ap, d)


@case("ddd.SetLasClassCodesUsingFeatures")
def _(ap, I, O):
    d = lasd(ap, I, "slf")
    ap.ddd.SetLasClassCodesUsingFeatures(d, f"{lasd_box(ap, I)} 0 9")
    return class_counts(ap, d)


@case("ddd.LasPointStatsByArea")
def _(ap, I, O):
    d = lasd(ap, I, "psa")
    out = ap.management.CopyFeatures(lasd_box(ap, I), O("out"))[0]
    ap.ddd.LasPointStatsByArea(d, out, "Z_MIN;Z_MAX;Z_MEAN;POINT_COUNT")


@case("ddd.PointFileInformation")
def _(ap, I, O):
    d = lasd(ap, I, "pfi")
    ap.ddd.PointFileInformation(las_paths(ap, d)[0], O("out"), "LAS", "las", ap.SpatialReference(I["srid"]))


@case("ddd.ClassifyLasGround")
def _(ap, I, O):
    d = lasd(ap, I, "clg")
    ap.ddd.ClassifyLasGround(d, "STANDARD", "RECLASSIFY_GROUND")
    return class_counts(ap, d)


@case("ddd.ColorizeLas")
def _(ap, I, O):
    d = lasd(ap, I, "col")
    folder = empty(W(I, "col_out"))
    ap.ddd.ColorizeLas(d, I["zones"], "RED Band_1;GREEN Band_1;BLUE Band_1", folder)
    return las_files(folder)


@case("ddd.ThinLas")
def _(ap, I, O):
    d = lasd(ap, I, "thin")
    folder = empty(W(I, "thin_out"))
    ap.ddd.ThinLas(d, folder, "2D", "20 Meters")
    return las_files(folder)


@case("ddd.TileLas")
def _(ap, I, O):
    d = lasd(ap, I, "tile")
    folder = empty(W(I, "tile_out"))
    ap.ddd.TileLas(d, folder, "t", tile_width="40 Meters", tile_height="40 Meters")
    return {k: len(v) for k, v in las_files(folder).items()}


@case("ddd.ClassifyLasOverlap")
def _(ap, I, O):
    d = lasd(ap, I, "clo")
    ap.ddd.ClassifyLasOverlap(d, "5 Meters")
    return class_counts(ap, d)


@case("ddd.LocateLasPointsByProximity")
def _(ap, I, O):
    d = lasd(ap, I, "lpp")
    x0, y0 = I["x0"], I["y0"]
    near = new_fc(ap, I, I["gdb"], "lpp_pts", "POINT", [("k", "LONG")],
                  [(1, f"POINT Z ({x0 + 125} {y0 + 125} 106)")], "ENABLED")
    ap.ddd.LocateLasPointsByProximity(d, near, "8 Meters", "COUNT", O("out"), "MULTIPOINT")


@case("ddd.LocateOutliers")
def _(ap, I, O):
    d = lasd(ap, I, "lo")
    ap.ddd.LocateOutliers(d, O("out"), "APPLY_HARD_LIMIT", 100, 108)


@case("ddd.ExtractObjectsFromPointCloud")
def _(ap, I, O):
    d = lasd(ap, I, "eop")
    ap.ddd.ExtractObjectsFromPointCloud(d, "1", "15 Meters", O("out"), min_points=10)


@case("ddd.ClassifyLasBuilding")
def _(ap, I, O):
    d = lasd(ap, I, "clb")
    ap.ddd.ClassifyLasBuilding(d, "2 Meters", "10 SquareMeters")
    return class_counts(ap, d)


@case("ddd.LasBuildingMultipatch")
def _(ap, I, O):
    d = lasd(ap, I, "lbm")
    ap.ddd.ChangeLasClassCodes(d, "1 6")
    ground = flat_raster(ap, I, "lbm_ground", 100.0)
    ap.ddd.LasBuildingMultipatch(d, lasd_box(ap, I), ground, O("out"))


@case("ddd.SetLasClassCodesUsingRaster")
def _(ap, I, O):
    d = lasd(ap, I, "slr")
    ap.ddd.SetLasClassCodesUsingRaster(d, I["zones"])
    return class_counts(ap, d)


# ---- terrains ---------------------------------------------------------------------------------------------------
# Each case builds its own terrain in its own feature dataset from a copy of the 25 3D points, with two pyramid levels
# (window sizes 20 m and 120 m), since several tools change it. Pro names a terrain's data source by the feature
# class's name, not its path (ERROR 050027 otherwise).

TERRAIN_DESCRIBE = ("dataType",)   # Pro answers none of grapy's other terrain properties by these names


def terrain(ap, I, name, build=True):
    sr = ap.SpatialReference(I["srid"])
    fds = os.path.join(I["gdb"], name + "_fd")
    if ap.Exists(fds):
        ap.management.Delete(fds)
    ap.management.CreateFeatureDataset(I["gdb"], name + "_fd", sr)
    pts = ap.management.CopyFeatures(points3d(ap, I), os.path.join(fds, name + "_pts"))[0]
    t = ap.ddd.CreateTerrain(fds, name + "_terr", 10)[0]
    ap.ddd.AddTerrainPyramidLevel(t, "", ["20 5000", "120 50000"])
    ap.ddd.AddFeatureClassToTerrain(t, [[pts, "Shape", "masspoints", 1, 0, 120, True, False, "", "", False]])
    if build:
        ap.ddd.BuildTerrain(t)
    return t, fds, pts


def describe_terrain(ap, t):
    d = ap.Describe(t)
    return {k: (str(getattr(d, k, "<missing>")) if not isinstance(getattr(d, k, None), (int, float)) else
                getattr(d, k)) for k in TERRAIN_DESCRIBE}


def level_points(ap, I, t, O, name, resolution):
    ap.ddd.TerrainToPoints(t, O(name), resolution, None, "POINT")


@case("ddd.CreateTerrain")
def _(ap, I, O):
    t, _, _ = terrain(ap, I, "ctr")
    level_points(ap, I, t, O, "full", 0)
    return describe_terrain(ap, t)


@case("ddd.AddTerrainPyramidLevel")
def _(ap, I, O):
    t, _, _ = terrain(ap, I, "atpl", build=False)
    ap.ddd.AddTerrainPyramidLevel(t, "", "60 20000")
    ap.ddd.BuildTerrain(t)
    level_points(ap, I, t, O, "l60", 60)


@case("ddd.RemoveTerrainPyramidLevel")
def _(ap, I, O):
    t, _, _ = terrain(ap, I, "rtpl", build=False)
    ap.ddd.RemoveTerrainPyramidLevel(t, 120)
    ap.ddd.BuildTerrain(t)
    level_points(ap, I, t, O, "l20", 20)


@case("ddd.ChangeTerrainReferenceScale")
def _(ap, I, O):
    t, _, _ = terrain(ap, I, "ctrs", build=False)
    ap.ddd.ChangeTerrainReferenceScale(t, 50000, 60000)
    ap.ddd.BuildTerrain(t)
    level_points(ap, I, t, O, "l120", 120)


@case("ddd.AddFeatureClassToTerrain")
def _(ap, I, O):
    t, fds, _ = terrain(ap, I, "afct", build=False)
    line = ap.management.CopyFeatures(lines3d(ap, I), os.path.join(fds, "afct_lines"))[0]
    ap.ddd.AddFeatureClassToTerrain(t, [[line, "Shape", "hardline", 2, 0, 120, True, False, "", "", False]])
    ap.ddd.BuildTerrain(t)
    level_points(ap, I, t, O, "full", 0)


@case("ddd.RemoveFeatureClassFromTerrain")
def _(ap, I, O):
    t, fds, _ = terrain(ap, I, "rfct", build=False)
    line = ap.management.CopyFeatures(lines3d(ap, I), os.path.join(fds, "rfct_lines"))[0]
    ap.ddd.AddFeatureClassToTerrain(t, [[line, "Shape", "hardline", 2, 0, 120, True, False, "", "", False]])
    ap.ddd.RemoveFeatureClassFromTerrain(t, os.path.basename(line))
    ap.ddd.BuildTerrain(t)
    level_points(ap, I, t, O, "full", 0)


@case("ddd.ChangeTerrainResolutionBounds")
def _(ap, I, O):
    t, fds, _ = terrain(ap, I, "ctrb", build=False)
    # Bounds are a breakline's: masspoints take none (grapy refuses them as the page says; Pro fails with 999999).
    line = ap.management.CopyFeatures(lines3d(ap, I), os.path.join(fds, "ctrb_lines"))[0]
    ap.ddd.AddFeatureClassToTerrain(t, [[line, "Shape", "hardline", 2, 0, 120, True, False, "", "", False]])
    ap.ddd.ChangeTerrainResolutionBounds(t, os.path.basename(line), 0, 20)
    ap.ddd.BuildTerrain(t)
    level_points(ap, I, t, O, "l120", 120)


@case("ddd.BuildTerrain")
def _(ap, I, O):
    t, _, _ = terrain(ap, I, "bt")
    level_points(ap, I, t, O, "l20", 20)
    level_points(ap, I, t, O, "l120", 120)


def extra_points(ap, I, name, z):
    x0, y0 = I["x0"], I["y0"]
    return new_fc(ap, I, I["gdb"], name, "MULTIPOINT", [("k", "LONG")],
                  [(1, f"MULTIPOINT Z (({x0 + 240} {y0 + 240} {z}), ({x0 + 360} {y0 + 360} {z + 2}))")], "ENABLED")


@case("ddd.AppendTerrainPoints")
def _(ap, I, O):
    t, _, pts = terrain(ap, I, "atp", build=False)
    ap.ddd.AppendTerrainPoints(t, os.path.basename(pts), extra_points(ap, I, "atp_more", 150))
    ap.ddd.BuildTerrain(t)
    level_points(ap, I, t, O, "full", 0)


@case("ddd.ReplaceTerrainPoints")
def _(ap, I, O):
    t, _, pts = terrain(ap, I, "rtp", build=False)
    ap.ddd.ReplaceTerrainPoints(t, os.path.basename(pts), extra_points(ap, I, "rtp_more", 150))
    ap.ddd.BuildTerrain(t)
    level_points(ap, I, t, O, "full", 0)


@case("ddd.DeleteTerrainPoints")
def _(ap, I, O):
    t, _, pts = terrain(ap, I, "dtp", build=False)
    x0, y0 = I["x0"], I["y0"]
    ap.ddd.DeleteTerrainPoints(t, os.path.basename(pts), f"{x0} {y0} {x0 + 250} {y0 + 250}")
    ap.ddd.BuildTerrain(t)
    level_points(ap, I, t, O, "full", 0)


@case("ddd.RemoveTerrainPoints")
def _(ap, I, O):
    # Points are removed only from an embedded class, and Pro embeds only multipoints.
    t, fds, _ = terrain(ap, I, "rmtp", build=False)
    more = ap.management.CopyFeatures(extra_points(ap, I, "rmtp_more", 150), os.path.join(fds, "rmtp_mp"))[0]
    ap.ddd.AddFeatureClassToTerrain(t, [[more, "Shape", "masspoints", 2, 0, 120, False, True, "rmtp_emb", "", False]])
    ap.ddd.BuildTerrain(t)
    x0, y0 = I["x0"], I["y0"]
    ap.ddd.RemoveTerrainPoints(t, "rmtp_emb", f"{x0 + 200} {y0 + 200} {x0 + 300} {y0 + 300}")
    ap.ddd.BuildTerrain(t)
    level_points(ap, I, t, O, "full", 0)


@case("ddd.TerrainToPoints")
def _(ap, I, O):
    t, _, _ = terrain(ap, I, "ttp")
    ap.ddd.TerrainToPoints(t, O("multi"), 20)
    ap.ddd.TerrainToPoints(t, O("single"), 120, None, "POINT")


@case("ddd.TerrainToTin")
def _(ap, I, O):
    t, _, _ = terrain(ap, I, "tttin")
    out = fresh(W(I, "tttin_tin"))
    ap.ddd.TerrainToTin(t, out, 20)
    ap.ddd.TinNode(out, O("nodes"))


@case("ddd.TerrainToRaster")
def _(ap, I, O):
    t, _, _ = terrain(ap, I, "ttr")
    ap.ddd.TerrainToRaster(t, O("out", "raster"), "FLOAT", "LINEAR", "CELLSIZE 20")


# ---- multipatches -----------------------------------------------------------------------------------------------
# Boxes are written as OBJ files and imported with Import3DFiles (a file geodatabase multipatch on Pro's side, a
# GeoPackage one on grapy's); shapes are compared as their distinct vertices (harness._patch_vertices).


def obj_box(x0, y0, z0, x1, y1, z1):
    v = [(x0, y0, z0), (x1, y0, z0), (x1, y1, z0), (x0, y1, z0), (x0, y0, z1), (x1, y0, z1), (x1, y1, z1), (x0, y1, z1)]
    lines = [f"v {x} {y} {z}" for x, y, z in v]
    lines += ["f 1 4 3 2", "f 5 6 7 8", "f 1 2 6 5", "f 2 3 7 6", "f 3 4 8 7", "f 4 1 5 8"]
    return "\n".join(lines) + "\n"


def boxes(ap, I, name, specs):
    """A multipatch class of boxes [(x0, y0, z0, x1, y1, z1)] in fixture coordinates, one feature per box."""
    path = os.path.join(I["gdb"], name)
    if ap.Exists(path):
        return path
    folder = empty(W(I, name + "_obj"))
    for k, (a, b, c, d, e, f) in enumerate(specs, start=1):
        with open(os.path.join(folder, f"b{k}.obj"), "w") as written:
            written.write(obj_box(I["x0"] + a, I["y0"] + b, c, I["x0"] + d, I["y0"] + e, f))
    ap.ddd.Import3DFiles(folder, path, spatial_reference=ap.SpatialReference(I["srid"]), file_suffix="obj")
    return path


def two_boxes(ap, I):
    return boxes(ap, I, "box2", [(100, 100, 0, 200, 200, 30), (150, 150, 10, 250, 250, 50)])


def box_a(ap, I):
    return boxes(ap, I, "box_a", [(100, 100, 0, 200, 200, 30)])


def box_b(ap, I):
    return boxes(ap, I, "box_b", [(150, 150, 10, 250, 250, 50)])


@case("ddd.Import3DFiles")
def _(ap, I, O):
    folder = empty(W(I, "i3d_obj"))
    with open(os.path.join(folder, "b.obj"), "w") as written:
        written.write(obj_box(I["x0"] + 10, I["y0"] + 10, 5, I["x0"] + 50, I["y0"] + 30, 25))
    ap.ddd.Import3DFiles(os.path.join(folder, "b.obj"), O("out"), spatial_reference=ap.SpatialReference(I["srid"]))


@case("ddd.IsClosed3D")
def _(ap, I, O):
    out = ap.management.CopyFeatures(two_boxes(ap, I), O("out"))[0]
    ap.ddd.IsClosed3D(out)


@case("ddd.MultiPatchFootprint")
def _(ap, I, O):
    ap.ddd.MultiPatchFootprint(two_boxes(ap, I), O("out"))


@case("ddd.MinimumBoundingVolume")
def _(ap, I, O):
    ap.ddd.MinimumBoundingVolume(points3d(ap, I), "Shape.Z", O("out"), "CONVEX_HULL", "ALL", None, "MBV_FIELDS")


@case("ddd.Intersect3DLineWithMultiPatch")
def _(ap, I, O):
    x0, y0 = I["x0"], I["y0"]
    line = new_fc(ap, I, I["gdb"], "i3l_line", "POLYLINE", [("k", "LONG")],
                  [(1, f"LINESTRING Z ({x0 + 50} {y0 + 120} 15, {x0 + 300} {y0 + 180} 15)")], "ENABLED")
    ap.ddd.Intersect3DLineWithMultiPatch(line, two_boxes(ap, I), "IDS_ONLY", O("pts"), O("lines"))


@case("ddd.Inside3D")
def _(ap, I, O):
    x0, y0 = I["x0"], I["y0"]
    pts = new_fc(ap, I, I["gdb"], "in3_pts", "POINT", [("k", "LONG")],
                 [(1, f"POINT Z ({x0 + 120} {y0 + 120} 5)"), (2, f"POINT Z ({x0 + 220} {y0 + 220} 40)"),
                  (3, f"POINT Z ({x0 + 400} {y0 + 400} 5)")], "ENABLED")
    ap.ddd.Inside3D(pts, two_boxes(ap, I), O("out"))


@case("ddd.Intersect3D")
def _(ap, I, O):
    ap.ddd.Intersect3D(box_a(ap, I), O("out"), box_b(ap, I), "SOLID")


@case("ddd.Union3D")
def _(ap, I, O):
    ap.ddd.Union3D(two_boxes(ap, I), O("out"))


@case("ddd.Difference3D")
def _(ap, I, O):
    ap.ddd.Difference3D(box_a(ap, I), box_b(ap, I), O("out"))


@case("ddd.Buffer3D")
def _(ap, I, O):
    x0, y0 = I["x0"], I["y0"]
    pts = new_fc(ap, I, I["gdb"], "b3_pts", "POINT", [("k", "LONG")], [(1, f"POINT Z ({x0 + 100} {y0 + 100} 10)")],
                 "ENABLED")
    ap.ddd.Buffer3D(pts, O("out"), "10 Meters")


@case("ddd.FenceDiagram")
def _(ap, I, O):
    ap.ddd.FenceDiagram(I["lines"], I["dem"], O("out"))


@case("ddd.SunShadowVolume")
def _(ap, I, O):
    ap.ddd.SunShadowVolume(box_a(ap, I), "6/21/2026 12:00:00 PM", O("out"), "NOT_ADJUSTED_FOR_DST", "UTC")


@case("ddd.SunShadowFrequency")
def _(ap, I, O):
    ground = flat_raster(ap, I, "ssf_ground", 0.0)
    ap.ddd.SunShadowFrequency(box_a(ap, I), ground, O("out", "raster"), 10, "6/21/2026 9:00:00 AM",
                              "6/21/2026 3:00:00 PM", "2 Hours", "UTC")


# ---- point cloud deep learning ----------------------------------------------------------------------------------

@case("ddd.PreparePointCloudTrainingData")
def _(ap, I, O):
    d = lasd(ap, I, "pctd")
    out = fresh(W(I, "pctd.pctd"))
    ap.ddd.PreparePointCloudTrainingData(d, "40 Meters", out, validation_point_cloud=d)
    return sorted(os.listdir(out)) if os.path.isdir(out) else os.path.exists(out)


@case("ddd.PreparePointCloudObjectDetectionTrainingData")
def _(ap, I, O):
    d = lasd(ap, I, "pcod")
    out = fresh(W(I, "pcod.pcotd"))
    ap.ddd.PreparePointCloudObjectDetectionTrainingData(d, las_objects(ap, I), las_objects(ap, I), "40 Meters", out,
                                                        validation_point_cloud=d)
    return sorted(os.listdir(out)) if os.path.isdir(out) else os.path.exists(out)


@case("ddd.TrainPointCloudClassificationModel")
def _(ap, I, O):
    d = lasd(ap, I, "tpcm")
    data = fresh(W(I, "tpcm.pctd"))
    ap.ddd.PreparePointCloudTrainingData(d, "40 Meters", data, validation_point_cloud=d)
    folder = empty(W(I, "tpcm_model"))
    ap.ddd.TrainPointCloudClassificationModel(data, folder, "m", max_epochs=1)
    return sorted(os.listdir(folder))


@case("ddd.TrainPointCloudObjectDetectionModel")
def _(ap, I, O):
    d = lasd(ap, I, "tpod")
    data = fresh(W(I, "tpod.pcotd"))
    ap.ddd.PreparePointCloudObjectDetectionTrainingData(d, las_objects(ap, I), las_objects(ap, I), "40 Meters", data,
                                                        validation_point_cloud=d)
    folder = empty(W(I, "tpod_model"))
    ap.ddd.TrainPointCloudObjectDetectionModel(data, folder, "m", max_epochs=1)
    return sorted(os.listdir(folder))


def las_objects(ap, I):
    """Multipatch boxes over the LAS points' lifted rows, as object detection's training features (Pro wants
    multipatches, ERROR 003917)."""
    return boxes(ap, I, "las_objects", [(100, 100, 100, 140, 140, 115), (140, 140, 100, 180, 180, 115)])


def _model(ap, I, name):
    d = lasd(ap, I, name)
    data = fresh(W(I, name + ".pctd"))
    ap.ddd.PreparePointCloudTrainingData(d, "40 Meters", data, validation_point_cloud=d)
    folder = empty(W(I, name + "_model"))
    ap.ddd.TrainPointCloudClassificationModel(data, folder, "m", max_epochs=1)
    return d, os.path.join(folder, "m", "m.emd")


@case("ddd.ClassifyPointCloudUsingTrainedModel")
def _(ap, I, O):
    d, model = _model(ap, I, "cpcm")
    ap.ddd.ClassifyPointCloudUsingTrainedModel(d, model, "1;2")
    return class_counts(ap, d)


@case("ddd.EvaluatePointCloudClassificationModel")
def _(ap, I, O):
    d, model = _model(ap, I, "epcm")
    folder = empty(W(I, "epcm_eval"))
    ap.ddd.EvaluatePointCloudClassificationModel(model, d, folder, "ev")
    return sorted(os.listdir(folder))


@case("ddd.DetectObjectsFromPointCloudUsingTrainedModel")
def _(ap, I, O):
    d = lasd(ap, I, "dopm")
    data = fresh(W(I, "dopm.pcotd"))
    ap.ddd.PreparePointCloudObjectDetectionTrainingData(d, las_objects(ap, I), las_objects(ap, I), "40 Meters", data,
                                                        validation_point_cloud=d)
    folder = empty(W(I, "dopm_model"))
    ap.ddd.TrainPointCloudObjectDetectionModel(data, folder, "m", max_epochs=1)
    ap.ddd.DetectObjectsFromPointCloudUsingTrainedModel(d, os.path.join(folder, "m", "m.emd"), "1", O("out"))
