"""arcpy.management against ArcGIS Pro 3.7, part 1: the first 126 built names in sorted order (AddAttachments through
EnableAttributeRules).

Tools that change a dataset in place change a copy made inside the case (an ``O()`` output), so the dumped output
shows the effect; tools that answer a value, or whose effect is a schema property (a domain, a subtype, a rule), return
it. Containers a case needs and does not compare (a feature dataset, a topology, a mosaic dataset, a mobile
geodatabase) are named ``<case>_<name>`` in the shared workspace through ``N()``, so nothing is dumped for them.

Where Pro needs a mobile geodatabase (archiving, full-text indexes) the case asks ``local_db``, which makes one on
Pro's side and grapy's workspace (a GeoPackage, as CreateFileGDB makes it) on grapy's: grapy refuses CreateMobileGDB
by name, and that difference is the fixtures' own (a file geodatabase against a GeoPackage).
"""
import os
import struct

CASES = {}


def case(name):
    def register(function):
        CASES[name] = function
        return function
    return register


# ---- helpers --------------------------------------------------------------------------------------------------------

def is_pro(ap):
    return ap.__name__ == "arcpy"


def N(I, case_name, name):
    """A path in the shared workspace for a container or helper dataset that is not dumped."""
    return os.path.join(I["gdb"], f"{case_name}_{name}")


def cp(ap, I, O, src="polys", name="out"):
    return ap.management.CopyFeatures(I[src], O(name))[0]


def ctab(ap, I, O, name="out"):
    return ap.management.CopyRows(I["table"], O(name))[0]


SYSTEM = ("shape_length", "shape_area", "objectid", "fid", "oid", "shape", "geom")


def fields(ap, path):
    """Each user field: name, type, alias, length (text only), nullable, domain, default."""
    out = []
    for f in ap.ListFields(path):
        if f.type in ("OID", "Geometry") or f.name.lower() in SYSTEM:
            continue
        out.append([f.name, f.type, f.aliasName, f.length if f.type == "String" else None, bool(f.isNullable),
                    f.domain or "", f.defaultValue])
    return out


def doms(ap, ws, names):
    out = []
    for d in ap.da.ListDomains(ws):
        if d.name not in names:
            continue
        coded = sorted([[k, v] for k, v in (d.codedValues or {}).items()], key=str) if d.codedValues else None
        out.append({"name": d.name, "description": d.description, "domainType": d.domainType, "type": d.type,
                    "codedValues": coded, "range": list(d.range) if d.range else None,
                    "splitPolicy": d.splitPolicy, "mergePolicy": d.mergePolicy})
    return sorted(out, key=lambda x: x["name"])


def rules(ap, path):
    out = []
    for r in ap.Describe(path).attributeRules:
        out.append({k: (str(getattr(r, k, None)) if not isinstance(getattr(r, k, None), (int, float, bool, str,
                                                                                         type(None))) else
                        getattr(r, k, None))
                    for k in ("name", "type", "fieldName", "isEnabled", "scriptExpression", "triggeringEvents",
                              "errorNumber", "errorMessage", "description", "batch", "severity", "tags",
                              "evaluationOrder", "subtypeCode")})
    return sorted(out, key=lambda x: x["name"])


def cvs(ap, path):
    out = []
    for c in ap.da.ListContingentValues(path):
        out.append({"group": c.fieldGroupName, "subtype": c.subtype, "isRetired": c.isRetired,
                    "values": sorted([[v.name, v.type, v.code, list(v.range) if v.range else None] for v in c.values],
                                      key=str)})
    return sorted(out, key=lambda x: (x["group"], str(x["values"])))


def subtypes(ap, path):
    st = ap.da.ListSubtypes(path)
    return sorted([[code, v["Name"], v["Default"], v["SubtypeField"]] for code, v in st.items()], key=str)


def local_db(ap, I, name):
    """A mobile geodatabase on Pro's side, grapy's GeoPackage workspace on grapy's (see the module's note)."""
    if is_pro(ap):
        return ap.management.CreateMobileGDB(I["work"], name)[0]
    return ap.management.CreateFileGDB(I["work"], name)[0]


def geopackage(ap, I, name):
    return ap.management.CreateSQLiteDatabase(os.path.join(I["work"], name + ".gpkg"), "GEOPACKAGE")[0]


def new_fc(ap, I, path_or_ws, name, shape, field_defs, rows, has_z="DISABLED"):
    """A feature class built from WKT rows ``(*values, wkt)``."""
    sr = ap.SpatialReference(I["srid"])
    path = ap.management.CreateFeatureclass(path_or_ws, name, shape, has_z=has_z, spatial_reference=sr)[0]
    for field, kind in field_defs:
        ap.management.AddField(path, field, kind)
    with ap.da.InsertCursor(path, [f for f, _ in field_defs] + ["SHAPE@"]) as cursor:
        for *values, wkt in rows:
            cursor.insertRow((*values, ap.FromWKT(wkt, sr) if wkt else None))
    return path


def split(path):
    return os.path.dirname(path), os.path.basename(path)


def sq(x0, y0, x1, y1, X=500000.0, Y=4500000.0):
    return f"(({X + x0} {Y + y0}, {X + x0} {Y + y1}, {X + x1} {Y + y1}, {X + x1} {Y + y0}, {X + x0} {Y + y0}))"


def write_las(path, points, scale=0.01):
    """A minimal LAS 1.2 file, point format 0: ``points`` is [(x, y, z, classification)]."""
    xs, ys, zs = [p[0] for p in points], [p[1] for p in points], [p[2] for p in points]
    ox, oy, oz = min(xs), min(ys), 0.0
    header = struct.pack(
        "<4sHH16sBB32s32sHHHIIBHI5I3d3d6d", b"LASF", 0, 0, b"\0" * 16, 1, 2, b"sweep".ljust(32, b"\0"),
        b"sweep".ljust(32, b"\0"), 1, 2024, 227, 227, 0, 0, 20, len(points), len(points), 0, 0, 0, 0,
        scale, scale, scale, ox, oy, oz, max(xs), min(xs), max(ys), min(ys), max(zs), min(zs))
    assert len(header) == 227
    with open(path, "wb") as f:
        f.write(header)
        for x, y, z, cls in points:
            f.write(struct.pack("<iiiHBBbBH", round((x - ox) / scale), round((y - oy) / scale),
                                round((z - oz) / scale), 100, 0b00001001, cls, 0, 0, 1))
    return path


def las_points(x0, y0, n=5, z0=100.0):
    return [(x0 + 10 * i + 5, y0 + 10 * j + 5, z0 + i + j, 2 if (i + j) % 2 else 1) for i in range(n) for j in range(n)]


def mosaic(ap, I, case_name, rasters=("dem",)):
    sr = ap.SpatialReference(I["srid"])
    md = ap.management.CreateMosaicDataset(I["gdb"], f"{case_name}_md", sr)[0]
    ap.management.AddRastersToMosaicDataset(md, "Raster Dataset", ";".join(I[r] for r in rasters))
    return md


def items(ap, md, names=("Name", "MinPS", "MaxPS", "LowPS", "HighPS")):
    with ap.da.SearchCursor(md, list(names)) as c:
        return sorted([[round(v, 6) if isinstance(v, float) else v for v in row] for row in c], key=str)


def raster_stats(ap, path):
    r = ap.Raster(path)
    return [round(float(v), 4) if v is not None else None for v in (r.minimum, r.maximum, r.mean, r.standardDeviation)]


def with_rule(ap, I, O, name="dbl"):
    fc = cp(ap, I, O)
    ap.management.AddGlobalIDs(fc)
    ap.management.AddAttributeRule(fc, name, "CALCULATION", "return $feature.gid * 2", "EDITABLE", "INSERT;UPDATE",
                                   field="val", description="twice the id")
    return fc


def insert_poly(ap, I, fc, gid):
    sr = ap.SpatialReference(I["srid"])
    with ap.da.InsertCursor(fc, ["gid", "SHAPE@"]) as c:
        c.insertRow((gid, ap.FromWKT("POLYGON " + sq(10, 10, 40, 40), sr)))


def topology(ap, I, case_name, srcs=("polys",)):
    sr = ap.SpatialReference(I["srid"])
    fd = ap.management.CreateFeatureDataset(I["gdb"], f"{case_name}_fd", sr)[0]
    fcs = [ap.management.CopyFeatures(I[s], os.path.join(fd, f"{case_name}_{s}"))[0] for s in srcs]
    topo = ap.management.CreateTopology(fd, f"{case_name}_topo")[0]
    return fd, fcs, topo


# ---- the cases ------------------------------------------------------------------------------------------------------

@case("management.AddAttachments")
def _(ap, I, O):
    fc = cp(ap, I, O)
    ap.management.EnableAttachments(fc)
    folder = O("files", "folder")
    match = ap.management.CreateTable(*split(O("match")))[0]
    ap.management.AddField(match, "gid", "LONG")
    ap.management.AddField(match, "path", "TEXT")
    with ap.da.InsertCursor(match, ["gid", "path"]) as c:
        for gid, n in ((1, 10), (2, 20), (2, 30), (5, 40)):
            name = f"g{gid}_{n}.txt"
            with open(os.path.join(folder, name), "w") as f:
                f.write("x" * n)
            c.insertRow((gid, name))
    ap.management.AddAttachments(fc, "gid", match, "gid", "path", folder)
    names = [f.name.upper() for f in ap.ListFields(fc + "__ATTACH")]
    key, own = ("REL_GLOBALID", "GLOBALID@") if "REL_GLOBALID" in names else ("REL_OBJECTID", "OID@")
    gids = {k: gid for gid, k in ap.da.SearchCursor(fc, ["gid", own])}
    with ap.da.SearchCursor(fc + "__ATTACH", [key, "ATT_NAME", "DATA_SIZE", "CONTENT_TYPE"]) as c:
        return {"key": key, "rows": sorted([[gids.get(r[0]), r[1], r[2], r[3]] for r in c], key=str)}


@case("management.AddAttributeRule")
def _(ap, I, O):
    fc = with_rule(ap, I, O)
    insert_poly(ap, I, fc, 7)
    return rules(ap, fc)


@case("management.AddCodedValueToDomain")
def _(ap, I, O):
    ap.management.CreateDomain(I["gdb"], "acv_dom", "codes", "LONG", "CODED")
    ap.management.AddCodedValueToDomain(I["gdb"], "acv_dom", 1, "one")
    ap.management.AddCodedValueToDomain(I["gdb"], "acv_dom", 2, "two")
    return doms(ap, I["gdb"], ["acv_dom"])


@case("management.AddContingentValue")
def _(ap, I, O):
    fc = cp(ap, I, O)
    ap.management.CreateDomain(I["gdb"], "acgv_cat", "cats", "TEXT", "CODED")
    for code in "abc":
        ap.management.AddCodedValueToDomain(I["gdb"], "acgv_cat", code, code.upper())
    ap.management.CreateDomain(I["gdb"], "acgv_val", "vals", "DOUBLE", "RANGE")
    ap.management.SetValueForRangeDomain(I["gdb"], "acgv_val", 0, 10)
    ap.management.AssignDomainToField(fc, "cat", "acgv_cat")
    ap.management.AssignDomainToField(fc, "val", "acgv_val")
    ap.management.CreateFieldGroup(fc, "fg", "cat;val")
    ap.management.AddContingentValue(fc, "fg", [["cat", "CODED_VALUE", "a"], ["val", "RANGE", "0;5"]])
    ap.management.AddContingentValue(fc, "fg", [["cat", "CODED_VALUE", "b"], ["val", "ANY", ""]])
    return cvs(ap, fc)


@case("management.AddFeatureClassToTopology")
def _(ap, I, O):
    fd, (polys, lines), topo = topology(ap, I, "afct", ("polys", "lines"))
    ap.management.AddFeatureClassToTopology(topo, polys, 1, 1)
    ap.management.AddFeatureClassToTopology(topo, lines, 2, 1)
    d = ap.Describe(topo)
    return {"featureClassNames": sorted(os.path.basename(n) for n in d.featureClassNames),
            "clusterTolerance": round(d.clusterTolerance, 6)}


@case("management.AddField")
def _(ap, I, O):
    t = ctab(ap, I, O)
    ap.management.AddField(t, "f_txt", "TEXT", field_length=20, field_alias="Text alias")
    ap.management.AddField(t, "f_dbl", "DOUBLE")
    ap.management.AddField(t, "f_sh", "SHORT")
    ap.management.AddField(t, "f_date", "DATE")
    ap.management.AddField(t, "f_flt", "FLOAT")
    return fields(ap, t)


@case("management.AddFieldConflictFilter")
def _(ap, I, O):
    fc = cp(ap, I, O)
    ap.management.AddFieldConflictFilter(fc, "cat")
    return sorted(ap.da.ListFieldConflictFilters(fc))


@case("management.AddFields")
def _(ap, I, O):
    t = ctab(ap, I, O)
    ap.management.AddFields(t, [["a1", "TEXT", "Alias A", 30, "x", ""], ["a2", "LONG", "", None, 5, ""],
                                ["a3", "DOUBLE"]])
    return fields(ap, t)


@case("management.AddFilesToLasDataset")
def _(ap, I, O):
    folder = O("las", "folder")
    a = write_las(os.path.join(folder, "a.las"), las_points(I["x0"], I["y0"]))
    b = write_las(os.path.join(folder, "b.las"), las_points(I["x0"] + 100, I["y0"], 4))
    lasd = os.path.join(folder, "d.lasd")
    ap.management.CreateLasDataset(a, lasd, spatial_reference=ap.SpatialReference(I["srid"]))
    ap.management.AddFilesToLasDataset(lasd, b)
    ap.management.AddFilesToLasDataset(lasd, b)   # already there: not added twice
    d = ap.Describe(lasd)
    return {"fileCount": d.fileCount, "pointCount": getattr(d, "pointCount", None)}


@case("management.AddFullTextIndex")
def _(ap, I, O):
    ws = local_db(ap, I, "afti")
    t = ap.management.CreateTable(ws, "t")[0]
    ap.management.AddField(t, "txt", "TEXT")
    ap.management.AddFullTextIndex(t, "txt")
    return sorted([sorted(getattr(f, "name", f).lower() for f in i.fields) for i in ap.ListIndexes(t)
                   if any(getattr(f, "name", f).lower() == "txt" for f in i.fields)])


@case("management.AddGeometryAttributes")
def _(ap, I, O):
    fc = cp(ap, I, O)
    ap.management.AddGeometryAttributes(fc, "AREA;PERIMETER_LENGTH;CENTROID;EXTENT;PART_COUNT;POINT_COUNT",
                                        "METERS", "SQUARE_METERS")
    lines = cp(ap, I, O, "lines", "lines")
    ap.management.AddGeometryAttributes(lines, "LENGTH;LINE_START_MID_END;LINE_BEARING", "METERS")
    pts = cp(ap, I, O, "points", "points")
    ap.management.AddGeometryAttributes(pts, "POINT_X_Y_Z_M")


@case("management.AddGlobalIDs")
def _(ap, I, O):
    fc = cp(ap, I, O)
    ap.management.AddGlobalIDs(fc)
    d = ap.Describe(fc)
    values = [g for (g,) in ap.da.SearchCursor(fc, [d.globalIDFieldName])]
    return {"hasGlobalID": d.hasGlobalID, "field": d.globalIDFieldName, "filled": sum(1 for v in values if v),
            "distinct": len(set(values)), "braced": all(str(v).startswith("{") and len(str(v)) == 38 for v in values),
            "fields": fields(ap, fc)}


@case("management.AddIncrementingIDField")
def _(ap, I, O):
    ws = geopackage(ap, I, "aiid")
    t = ap.management.CreateUnRegisteredTable(ws, "ut")[0]
    ap.management.AddIncrementingIDField(t, "myid")
    return fields(ap, t)


@case("management.AddIndex")
def _(ap, I, O):
    t = ctab(ap, I, O)
    ap.management.AddIndex(t, "rid;cat", "idx_rc", "NON_UNIQUE", "ASCENDING")
    ap.management.AddIndex(t, "val", "idx_v")
    out = []
    for i in ap.ListIndexes(t):
        names = [getattr(f, "name", f).lower() for f in i.fields]
        if names and not set(names) & {"objectid", "fid", "oid"}:
            out.append([i.name.lower(), names, bool(i.isUnique), bool(i.isAscending)])
    return sorted(out, key=str)


@case("management.AddJoin")
def _(ap, I, O):
    lk = ap.management.CreateTable(*split(O("lookup")))[0]
    ap.management.AddField(lk, "code", "TEXT")
    ap.management.AddField(lk, "label", "TEXT")
    with ap.da.InsertCursor(lk, ["code", "label"]) as c:
        c.insertRow(("a", "Alpha"))
        c.insertRow(("b", "Beta"))
    lyr = ap.management.MakeFeatureLayer(I["polys"], "addjoin_all")[0]
    ap.management.AddJoin(lyr, "cat", lk, "code", "KEEP_ALL")
    ap.management.CopyFeatures(lyr, O("all"))
    lyr2 = ap.management.MakeFeatureLayer(I["polys"], "addjoin_common")[0]
    ap.management.AddJoin(lyr2, "cat", lk, "code", "KEEP_COMMON")
    ap.management.CopyFeatures(lyr2, O("common"))
    return [f.name.split(".")[-1] for f in ap.ListFields(lyr) if f.type not in ("OID", "Geometry")
            and f.name.split(".")[-1].lower() not in SYSTEM]


@case("management.AddRastersToMosaicDataset")
def _(ap, I, O):
    md = mosaic(ap, I, "armd", ("dem", "zones"))
    return items(ap, md)


@case("management.AddRuleToRelationshipClass")
def _(ap, I, O):
    origin = cp(ap, I, O, "polys", "origin")
    dest = cp(ap, I, O, "points", "dest")
    ap.management.AddField(dest, "gid", "LONG")
    for t in (origin, dest):
        ap.management.SetSubtypeField(t, "gid" if t == origin else "pid")
        ap.management.AddSubtype(t, 1, "One")
        ap.management.AddSubtype(t, 2, "Two")
    rc = N(I, "artrc", "rc")
    ap.management.CreateRelationshipClass(origin, dest, rc, "SIMPLE", "to points", "to polys", "NONE",
                                          "ONE_TO_MANY", "NONE", "gid", "gid")
    ap.management.AddRuleToRelationshipClass(rc, "One", None, None, "Two", 0, 3)   # subtypes by name, as Pro takes them
    d = ap.Describe(rc)
    return [[r.originSubtypeCode, r.originMinimumCardinality, r.originMaximumCardinality, r.destinationSubtypeCode,
             r.destinationMinimumCardinality, r.destinationMaximumCardinality] for r in d.relationshipRules]


@case("management.AddRuleToTopology")
def _(ap, I, O):
    fd, (polys,), topo = topology(ap, I, "artt")
    ap.management.AddFeatureClassToTopology(topo, polys, 1, 1)
    ap.management.AddRuleToTopology(topo, "Must Not Overlap (Area)", polys)
    ap.management.ValidateTopology(topo)
    ap.management.ExportTopologyErrors(topo, I["gdb"], "artt_err")
    ws = ap.env.workspace
    ap.env.workspace = I["gdb"]
    try:
        names = sorted(ap.ListFeatureClasses("artt_err*"))
    finally:
        ap.env.workspace = ws
    out = []
    for name in names:
        with ap.da.SearchCursor(os.path.join(I["gdb"], name), ["RuleType", "RuleDescription", "SHAPE@"]) as c:
            out += [[r[0], r[1], round(r[2].area, 3) if r[2] else None] for r in c]
    return sorted(out, key=str)


@case("management.AddSpatialIndex")
def _(ap, I, O):
    fc = cp(ap, I, O)
    ap.management.AddSpatialIndex(fc)
    return ap.Describe(fc).hasSpatialIndex


@case("management.AddSubtype")
def _(ap, I, O):
    fc = cp(ap, I, O)
    ap.management.SetSubtypeField(fc, "gid")
    ap.management.AddSubtype(fc, 1, "One")
    ap.management.AddSubtype(fc, 2, "Two")
    return subtypes(ap, fc)


@case("management.AddXY")
def _(ap, I, O):
    ap.management.AddXY(cp(ap, I, O, "points"))


@case("management.Adjust3DZ")
def _(ap, I, O):
    rows = [(i, 10.0 * i, f"POINT Z ({I['x0'] + 10 * i} {I['y0'] + 5 * i} {10.0 * i})") for i in range(1, 6)]
    fc = new_fc(ap, I, *split(O("out")), "POINT", [("pid", "LONG"), ("off", "DOUBLE")], rows, has_z="ENABLED")
    ap.management.Adjust3DZ(fc, "REVERSE", 10, "METERS", "FEET")
    with ap.da.SearchCursor(fc, ["pid", "SHAPE@Z"]) as c:
        return sorted([[p, round(z, 6)] for p, z in c])


@case("management.AlterAttributeRule")
def _(ap, I, O):
    fc = with_rule(ap, I, O)
    ap.management.AlterAttributeRule(fc, "dbl", "a new description", None, None, "t1;t2", "INSERT",
                                     "return $feature.gid * 3")
    insert_poly(ap, I, fc, 7)
    return rules(ap, fc)


@case("management.AlterCodedValueDescription")
def _(ap, I, O):
    ap.management.CreateDomain(I["gdb"], "acvd_dom", "codes", "SHORT", "CODED")
    ap.management.AddCodedValueToDomain(I["gdb"], "acvd_dom", 1, "one")
    ap.management.AlterCodedValueDescription(I["gdb"], "acvd_dom", 1, "uno")
    return doms(ap, I["gdb"], ["acvd_dom"])


@case("management.AlterDomain")
def _(ap, I, O):
    ap.management.CreateDomain(I["gdb"], "ad_dom", "range", "DOUBLE", "RANGE")
    ap.management.AlterDomain(I["gdb"], "ad_dom", "ad_dom2", "a range", "GEOMETRY_RATIO", "SUM_VALUES")
    return doms(ap, I["gdb"], ["ad_dom", "ad_dom2"])


@case("management.AlterField")
def _(ap, I, O):
    t = ctab(ap, I, O)
    ap.management.AlterField(t, "val", "value2", "New alias")
    empty = ap.management.CreateTable(*split(O("empty")))[0]
    ap.management.AddField(empty, "n", "SHORT")
    ap.management.AddField(empty, "s", "TEXT", field_length=10)
    ap.management.AlterField(empty, "n", field_type="LONG")
    ap.management.AlterField(empty, "s", field_length=40)
    return {"t": fields(ap, t), "empty": fields(ap, empty)}


@case("management.AlterFieldGroup")
def _(ap, I, O):
    fc = cp(ap, I, O)
    ap.management.CreateFieldGroup(fc, "fg", "cat;val")
    ap.management.AddContingentValue(fc, "fg", [["cat", "ANY", ""], ["val", "NULL", ""]])
    ap.management.AlterFieldGroup(fc, "fg", "fg2", "cat;gid")
    return cvs(ap, fc)


@case("management.AlterFields")
def _(ap, I, O):
    t = ctab(ap, I, O)
    ap.management.AlterFields(t, [["val", "val2", "Value two", "DOUBLE"], ["cat", "cat2", "", "TEXT", 255]])
    return fields(ap, t)


@case("management.AlterSubtype")
def _(ap, I, O):
    fc = cp(ap, I, O)
    ap.management.SetSubtypeField(fc, "gid")
    ap.management.AddSubtype(fc, 1, "One")
    ap.management.AlterSubtype(fc, 1, "Uno")
    return subtypes(ap, fc)


@case("management.AlterVersion")
def _(ap, I, O):
    ap.management.AlterVersion(I["gdb"], "v1", "v2", "described")
    return sorted(ap.ListVersions(I["gdb"]))


@case("management.Analyze")
def _(ap, I, O):
    fc = cp(ap, I, O)
    ap.management.Analyze(fc, "BUSINESS;FEATURE")
    return True


@case("management.AnalyzeControlPoints")
def _(ap, I, O):
    md = mosaic(ap, I, "acp")
    cps = cp(ap, I, O, "points", "cps")
    ap.management.AnalyzeControlPoints(md, cps, O("coverage"), O("overlap"))


@case("management.AnalyzeDatasets")
def _(ap, I, O):
    ap.management.AnalyzeDatasets(I["gdb"], "SYSTEM", "polys")
    return True


@case("management.Append")
def _(ap, I, O):
    target = ap.management.CopyFeatures(I["points"], O("out"))[0]
    ap.management.Append(I["points"], target, "TEST")
    sub = ap.management.CreateFeatureclass(*split(O("notest")), "POINT",
                                           spatial_reference=ap.SpatialReference(I["srid"]))[0]
    ap.management.AddField(sub, "pid", "LONG")
    ap.management.AddField(sub, "extra", "TEXT")
    ap.management.Append(I["points"], sub, "NO_TEST")


@case("management.AppendControlPoints")
def _(ap, I, O):
    a = cp(ap, I, O, "points", "out")
    ap.management.AppendControlPoints(a, I["points"])


@case("management.ApplyBlockAdjustment")
def _(ap, I, O):
    md = mosaic(ap, I, "aba")
    ap.management.ApplyBlockAdjustment(md, "RESET")
    return items(ap, md, ("Name",))


@case("management.AssignDefaultToField")
def _(ap, I, O):
    t = ctab(ap, I, O)
    ap.management.AssignDefaultToField(t, "cat", "z")
    ap.management.AssignDefaultToField(t, "val", 42.5)
    with ap.da.InsertCursor(t, ["rid"]) as c:
        c.insertRow((99,))
    return fields(ap, t)


@case("management.AssignDomainToField")
def _(ap, I, O):
    fc = cp(ap, I, O)
    ap.management.CreateDomain(I["gdb"], "adf_dom", "cats", "TEXT", "CODED")
    ap.management.AddCodedValueToDomain(I["gdb"], "adf_dom", "a", "A")
    ap.management.AssignDomainToField(fc, "cat", "adf_dom")
    return fields(ap, fc)


@case("management.BatchBuildPyramids")
def _(ap, I, O):
    r = ap.management.CopyRaster(I["dem"], O("out", "raster"))[0]
    ap.management.BatchBuildPyramids(r)


@case("management.BatchCalculateStatistics")
def _(ap, I, O):
    r = ap.management.CopyRaster(I["dem"], O("out", "raster"))[0]
    ap.management.BatchCalculateStatistics(r)
    return raster_stats(ap, r)


@case("management.BatchProject")
def _(ap, I, O):
    folder = O("ws", "folder")
    ws = ap.management.CreateFileGDB(folder, "bp")[0]
    ap.management.BatchProject([I["points"], I["lines"]], ws, ap.SpatialReference(4326))
    ws_old = ap.env.workspace
    ap.env.workspace = ws
    try:
        names = sorted(ap.ListFeatureClasses())
    finally:
        ap.env.workspace = ws_old
    for name in names:
        ap.management.CopyFeatures(os.path.join(ws, name), O(name))
    return names


@case("management.BatchUpdateFields")
def _(ap, I, O):
    defs = ap.management.CreateTable(*split(O("defs")))[0]
    for f in ("FNAME", "FSOURCE", "FTYPE", "FALIAS", "FSCRIPT"):
        ap.management.AddField(defs, f, "TEXT")
    ap.management.AddField(defs, "FLEN", "LONG")
    with ap.da.InsertCursor(defs, ["FNAME", "FSOURCE", "FTYPE", "FLEN", "FALIAS", "FSCRIPT"]) as c:
        c.insertRow(("rid2", "rid", "LONG", None, "Row id", None))
        c.insertRow(("val", "val", "DOUBLE", 3, "Value", None))
        c.insertRow(("dbl", None, "DOUBLE", 3, "Twice", "!val! * 2"))
        c.insertRow(("txt", "cat", "TEXT", 5, "Category", None))
    ap.management.BatchUpdateFields(I["table"], O("out"), defs, None, "FNAME", "FSOURCE", "FTYPE", "FLEN", "FALIAS",
                                    "FSCRIPT")


@case("management.BearingDistanceToLine")
def _(ap, I, O):
    t = ap.management.CreateTable(*split(O("in")))[0]
    for f in ("x", "y", "d", "b"):
        ap.management.AddField(t, f, "DOUBLE")
    ap.management.AddField(t, "lab", "TEXT")
    with ap.da.InsertCursor(t, ["x", "y", "d", "b", "lab"]) as c:
        c.insertRow((I["x0"] + 100, I["y0"] + 100, 100, 0, "north"))
        c.insertRow((I["x0"] + 100, I["y0"] + 100, 50, 90, "east"))
        c.insertRow((I["x0"] + 300, I["y0"] + 300, 200, 225, "sw"))
    sr = ap.SpatialReference(I["srid"])
    ap.management.BearingDistanceToLine(t, O("out"), "x", "y", "d", "METERS", "b", "DEGREES", "PLANAR",
                                        spatial_reference=sr, attributes="ATTRIBUTES")


@case("management.BuildBoundary")
def _(ap, I, O):
    md = mosaic(ap, I, "bb")
    ap.management.BuildBoundary(md, None, "OVERWRITE")
    ap.management.ExportMosaicDatasetGeometry(md, O("out"), None, "BOUNDARY")


@case("management.BuildFootprints")
def _(ap, I, O):
    md = mosaic(ap, I, "bf")
    ap.management.BuildFootprints(md, None, "RADIOMETRY", 110, 200)
    ap.management.ExportMosaicDatasetGeometry(md, O("out"), None, "FOOTPRINT")


@case("management.BuildPyramids")
def _(ap, I, O):
    r = ap.management.CopyRaster(I["dem"], O("out", "raster"))[0]
    ap.management.BuildPyramids(r)


@case("management.BuildPyramidsandStatistics")
def _(ap, I, O):
    folder = O("f", "folder")
    r = ap.management.CopyRaster(I["dem"], os.path.join(folder, "r.tif"))[0]
    ap.management.BuildPyramidsandStatistics(folder)
    return raster_stats(ap, r)


@case("management.BuildRasterAttributeTable")
def _(ap, I, O):
    r = ap.management.CopyRaster(I["zones"], O("out", "raster"))[0]
    ap.management.BuildRasterAttributeTable(r, "Overwrite")
    with ap.da.SearchCursor(r, ["Value", "Count"]) as c:
        return sorted([list(row) for row in c])


@case("management.BuildStereoModel")
def _(ap, I, O):
    md = mosaic(ap, I, "bsm")
    ap.management.BuildStereoModel(md)
    return True


@case("management.CalculateCellSizeRanges")
def _(ap, I, O):
    md = mosaic(ap, I, "ccsr")
    ap.management.CalculateCellSizeRanges(md)
    return items(ap, md)


@case("management.CalculateDefaultClusterTolerance")
def _(ap, I, O):
    return float(ap.management.CalculateDefaultClusterTolerance(I["polys"])[0])


@case("management.CalculateEndTime")
def _(ap, I, O):
    import datetime
    t = ap.management.CreateTable(*split(O("out")))[0]
    ap.management.AddField(t, "grp", "LONG")
    ap.management.AddField(t, "t0", "DATE")
    ap.management.AddField(t, "t1", "DATE")
    with ap.da.InsertCursor(t, ["grp", "t0"]) as c:
        for g, day in ((1, 5), (1, 1), (1, 3), (2, 2), (2, 9)):
            c.insertRow((g, datetime.datetime(2024, 1, day, 12)))
    ap.management.CalculateEndTime(t, "t0", "t1", "grp")


@case("management.CalculateField")
def _(ap, I, O):
    t = ctab(ap, I, O)
    ap.management.CalculateField(t, "val", "!val! * 2 + !rid!", "PYTHON3")
    ap.management.CalculateField(t, "lab", "f(!cat!, !rid!)", "PYTHON3", "def f(c, r):\n    return c.upper() + str(r)",
                                 "TEXT")
    ap.management.CalculateField(t, "n", "!rid! % 3", "PYTHON3", None, "LONG")
    return fields(ap, t)


@case("management.CalculateFields")
def _(ap, I, O):
    t = ctab(ap, I, O)
    ap.management.CalculateFields(t, "PYTHON3", [["val", "!val! * 3"], ["cat", "!cat!.upper()"], ["lab", "!cat! * 2"],
                                                 ["n", "!rid! * 2", "", "LONG"]])
    return fields(ap, t)


@case("management.CalculateGeometryAttributes")
def _(ap, I, O):
    fc = cp(ap, I, O)
    ap.management.CalculateGeometryAttributes(fc, [["a", "AREA"], ["p", "PERIMETER_LENGTH"], ["cx", "CENTROID_X"],
                                                   ["cy", "CENTROID_Y"], ["n", "POINT_COUNT"]],
                                              "METERS", "SQUARE_METERS")
    lines = cp(ap, I, O, "lines", "lines")
    ap.management.CalculateGeometryAttributes(lines, [["len", "LENGTH"], ["sx", "LINE_START_X"],
                                                      ["b", "LINE_BEARING"]], "KILOMETERS")
    return {"polys": fields(ap, fc), "lines": fields(ap, lines)}


@case("management.CalculateGeometryFieldsAttributeRule")
def _(ap, I, O):
    fc = cp(ap, I, O)
    ap.management.AddGlobalIDs(fc)
    ap.management.AddField(fc, "a", "DOUBLE")
    ap.management.CalculateGeometryFieldsAttributeRule(fc, O("f", "folder"), [["a", "AREA"]], None, None,
                                                       "SQUARE_METERS")
    insert_poly(ap, I, fc, 7)
    return [[r["name"], r["type"], r["fieldName"]] for r in rules(ap, fc)]


@case("management.CalculateStatistics")
def _(ap, I, O):
    r = ap.management.CopyRaster(I["dem"], O("out", "raster"))[0]
    ap.management.CalculateStatistics(r)
    return raster_stats(ap, r)


@case("management.CalculateValue")
def _(ap, I, O):
    a = ap.management.CalculateValue("f(3)", "def f(x):\n    return x * 2", "Long")[0]
    b = ap.management.CalculateValue("str(12) + 'c'", None, "String")[0]
    c = ap.management.CalculateValue("1.5 * 3", None, "Double")[0]
    return [str(a), str(b), str(c)]


@case("management.ChangeVersion")
def _(ap, I, O):
    lyr = ap.management.MakeFeatureLayer(I["polys"], "chv")[0]
    ap.management.ChangeVersion(lyr, "TRANSACTIONAL", "v1")
    return True


@case("management.CheckGeometry")
def _(ap, I, O):
    fc = new_fc(ap, I, *split(O("in")), "POLYGON", [("k", "LONG")],
                [(1, "POLYGON " + sq(0, 0, 100, 100)),
                 (2, None),
                 (3, f"POLYGON (({I['x0']} {I['y0']}, {I['x0'] + 100} {I['y0'] + 100}, {I['x0'] + 100} {I['y0']}, "
                     f"{I['x0']} {I['y0'] + 100}, {I['x0']} {I['y0']}))")])
    out = ap.management.CheckGeometry(fc, O("out"))[0]
    ap.management.DeleteField(out, "CLASS")   # the input's path, machine-specific


@case("management.ClearWorkspaceCache")
def _(ap, I, O):
    return bool(ap.management.ClearWorkspaceCache()[0])


@case("management.Clip")
def _(ap, I, O):
    x, y = I["x0"], I["y0"]
    ap.management.Clip(I["dem"], f"{x + 100} {y + 100} {x + 400} {y + 350}", O("rect", "raster"))
    ap.management.Clip(I["dem"], "#", O("geom", "raster"), I["clip"], None, "ClippingGeometry", "NO_MAINTAIN_EXTENT")


@case("management.Compact")
def _(ap, I, O):
    return bool(ap.management.Compact(I["gdb"])[0])


@case("management.CompositeBands")
def _(ap, I, O):
    out = os.path.join(I["work"], "composite.tif")
    ap.management.CompositeBands([I["dem"], I["zones"]], out)
    for band in (1, 2):
        ap.management.CopyRaster(os.path.join(out, f"Band_{band}"), O(f"b{band}", "raster"))
    return ap.Raster(out).bandCount


@case("management.ComputeBlockAdjustment")
def _(ap, I, O):
    md = mosaic(ap, I, "cba")
    cps = cp(ap, I, O, "points", "cps")
    ap.management.ComputeBlockAdjustment(md, cps, "POLYORDER1", O("solution"))


@case("management.ComputeControlPoints")
def _(ap, I, O):
    md = mosaic(ap, I, "ccp")
    ap.management.ComputeControlPoints(md, I["dem"], O("out"))


@case("management.ComputeTiePoints")
def _(ap, I, O):
    md = mosaic(ap, I, "ctp")
    ap.management.ComputeTiePoints(md, O("out"))


@case("management.ConvertCoordinateNotation")
def _(ap, I, O):
    t = ap.management.CreateTable(*split(O("in")))[0]
    ap.management.AddField(t, "lon", "DOUBLE")
    ap.management.AddField(t, "lat", "DOUBLE")
    with ap.da.InsertCursor(t, ["lon", "lat"]) as c:
        c.insertRow((27.0012345, 40.6543210))
        c.insertRow((-73.9856, 40.7484))
        c.insertRow((151.2093, -33.8688))
        c.insertRow((5.5, -3.25))
        c.insertRow((-0.0000123, 0.9999999999))
    gcs = ap.SpatialReference(4326)
    for fmt in ("DD_2", "DD_1", "DDM_1", "DMS_1", "DMS_2", "DDM_2", "MGRS", "UTM_ZONES", "UTM_BANDS", "GARS", "GEOREF",
                "USNG"):
        ap.management.ConvertCoordinateNotation(t, O(fmt.lower()), "lon", "lat", "DD_2", fmt, None, gcs, gcs)


@case("management.ConvertTimeField")
def _(ap, I, O):
    t = ap.management.CreateTable(*split(O("out")))[0]
    ap.management.AddField(t, "s", "TEXT")
    ap.management.AddField(t, "u", "LONG")
    with ap.da.InsertCursor(t, ["s", "u"]) as c:
        c.insertRow(("2024-03-05 13:45:10", 1700000000))
        c.insertRow(("1999-12-31 23:59:59", 0))
    ap.management.ConvertTimeField(t, "s", "yyyy-MM-dd HH:mm:ss", "d1", "DATE")
    ap.management.ConvertTimeField(t, "s", "yyyy-MM-dd HH:mm:ss", "s2", "TEXT", "dd/MM/yyyy")
    ap.management.ConvertTimeField(t, "u", "unix_s", "d2", "DATE")


@case("management.Copy")
def _(ap, I, O):
    ap.management.Copy(I["polys"], O("out"))
    ap.management.Copy(I["table"], O("table"))


@case("management.CopyFeatures")
def _(ap, I, O):
    ap.management.CopyFeatures(I["lines"], O("out"))
    lyr = ap.management.MakeFeatureLayer(I["points"], "cf_sel", "val > 50")[0]
    ap.management.CopyFeatures(lyr, O("selected"))


@case("management.CopyRaster")
def _(ap, I, O):
    ap.management.CopyRaster(I["dem"], O("out", "raster"))
    ap.management.CopyRaster(I["dem"], O("int16", "raster"), pixel_type="16_BIT_SIGNED")
    ap.management.CopyRaster(I["zones"], O("nodata", "raster"), nodata_value=2)
    import numpy
    ap.env.outputCoordinateSystem = ap.SpatialReference(I["srid"])
    try:
        cells = numpy.array([[300.0, -5.0, 255.5, 254.5], [0.4, 70000.0, -0.6, 12.5]], dtype="float32")
        wide = os.path.join(I["work"], "copyraster_wide.tif")
        ap.NumPyArrayToRaster(cells, ap.Point(I["x0"], I["y0"]), 10, 10).save(wide)
    finally:
        ap.env.outputCoordinateSystem = None
    ap.management.CopyRaster(wide, O("u8", "raster"), pixel_type="8_BIT_UNSIGNED")
    ap.management.CopyRaster(wide, O("s16", "raster"), pixel_type="16_BIT_SIGNED")


@case("management.CopyRows")
def _(ap, I, O):
    ap.management.CopyRows(I["polys"], O("out"))
    view = ap.management.MakeTableView(I["table"], "cr_view", "rid > 6")[0]
    ap.management.CopyRows(view, O("view"))


@case("management.CreateDatabaseSequence")
def _(ap, I, O):
    ap.management.CreateDatabaseSequence(I["gdb"], "cds_seq", 5, 2)
    return [[s.name, s.startValue, s.incrementValue] for s in ap.da.ListDatabaseSequences(I["gdb"])
            if s.name == "cds_seq"]


@case("management.CreateDatabaseView")
def _(ap, I, O):
    view = ap.management.CreateDatabaseView(I["gdb"], "cdv_view", "select rid, val, cat from rows where val > 5")[0]
    ap.management.CopyRows(view, O("out"))


@case("management.CreateDomain")
def _(ap, I, O):
    ap.management.CreateDomain(I["gdb"], "cd_coded", "a coded one", "TEXT", "CODED")
    ap.management.CreateDomain(I["gdb"], "cd_range", "a range", "LONG", "RANGE", "DUPLICATE", "SUM_VALUES")
    ap.management.CreateDomain(I["gdb"], "cd_dflt", "defaults")
    return doms(ap, I["gdb"], ["cd_coded", "cd_range", "cd_dflt"])


@case("management.CreateFeatureDataset")
def _(ap, I, O):
    fd = ap.management.CreateFeatureDataset(I["gdb"], "cfd_fd", ap.SpatialReference(I["srid"]))[0]
    fc = ap.management.CreateFeatureclass(fd, "cfd_fc", "POINT")[0]
    d = ap.Describe(fd)
    return {"dataType": d.dataType, "srid": d.spatialReference.factoryCode,
            "child_srid": ap.Describe(fc).spatialReference.factoryCode}


@case("management.CreateFeatureclass")
def _(ap, I, O):
    sr = ap.SpatialReference(I["srid"])
    a = ap.management.CreateFeatureclass(*split(O("templ")), "POLYGON", I["polys"], spatial_reference=sr)[0]
    b = ap.management.CreateFeatureclass(*split(O("z")), "POLYLINE", None, "ENABLED", "ENABLED", sr)[0]
    out = {}
    for k, p in (("templ", a), ("z", b)):
        d = ap.Describe(p)
        out[k] = [d.shapeType, d.hasZ, d.hasM, d.spatialReference.factoryCode, fields(ap, p)]
    return out


@case("management.CreateFieldGroup")
def _(ap, I, O):
    fc = cp(ap, I, O)
    ap.management.CreateFieldGroup(fc, "fg", "cat;val", "RESTRICT")
    ap.management.AddContingentValue(fc, "fg", [["cat", "ANY", ""], ["val", "NULL", ""]])
    return cvs(ap, fc)


@case("management.CreateFileGDB")
def _(ap, I, O):
    folder = O("f", "folder")
    ws = ap.management.CreateFileGDB(folder, "made")[0]
    d = ap.Describe(ws)
    return {"exists": ap.Exists(ws), "dataType": d.dataType, "workspaceType": d.workspaceType}


@case("management.CreateFishnet")
def _(ap, I, O):
    ap.env.outputCoordinateSystem = ap.SpatialReference(I["srid"])
    try:
        x, y = I["x0"], I["y0"]
        out = O("out")
        ap.management.CreateFishnet(out, f"{x} {y}", f"{x} {y + 10}", 100, 50, 3, 4, None, "LABELS", None,
                                    "POLYGON")
        ap.management.CopyFeatures(out + "_label", O("label"))
        ap.management.CreateFishnet(O("lines"), f"{x} {y}", f"{x + 10} {y + 10}", 50, 80, 2, 2, None, "NO_LABELS",
                                    None, "POLYLINE")
        ap.management.CreateFishnet(O("corner"), f"{x} {y}", f"{x} {y + 10}", 0, 0, 2, 3, f"{x + 300} {y + 100}",
                                    "NO_LABELS", None, "POLYLINE")
    finally:
        ap.env.outputCoordinateSystem = None


@case("management.CreateFolder")
def _(ap, I, O):
    ap.management.CreateFolder(I["work"], "cf_folder")
    return os.path.isdir(os.path.join(I["work"], "cf_folder"))


@case("management.CreateLasDataset")
def _(ap, I, O):
    folder = O("las", "folder")
    write_las(os.path.join(folder, "a.las"), las_points(I["x0"], I["y0"]))
    write_las(os.path.join(folder, "b.las"), las_points(I["x0"] + 100, I["y0"], 4))
    lasd = os.path.join(I["work"], "cld.lasd")
    ap.management.CreateLasDataset(folder, lasd, spatial_reference=ap.SpatialReference(I["srid"]),
                                   compute_stats="COMPUTE_STATS")
    d = ap.Describe(lasd)
    e = d.extent
    return {"fileCount": d.fileCount, "pointCount": getattr(d, "pointCount", None),
            "extent": [round(v, 3) for v in (e.XMin, e.YMin, e.XMax, e.YMax)]}


@case("management.CreateMosaicDataset")
def _(ap, I, O):
    md = ap.management.CreateMosaicDataset(I["gdb"], "cmd_md", ap.SpatialReference(I["srid"]), 1, "32_BIT_FLOAT")[0]
    d = ap.Describe(md)
    return {"dataType": d.dataType, "srid": d.spatialReference.factoryCode, "items": len(items(ap, md, ("Name",)))}


@case("management.CreateOrthoCorrectedRasterDataset")
def _(ap, I, O):
    ap.management.CreateOrthoCorrectedRasterDataset(I["dem"], O("out", "raster"), "CONSTANT_ELEVATION", 0)


@case("management.CreateRandomPoints")
def _(ap, I, O):
    folder, name = split(N(I, "crp", "pts"))
    # minimum_allowed_distance is left out: grapy refuses it by name (the page does not say how the count is reduced).
    pts = ap.management.CreateRandomPoints(folder, name, I["polys"], None, 5)[0]
    per = {}
    with ap.da.SearchCursor(pts, ["CID"]) as c:
        for (cid,) in c:
            per[cid] = per.get(cid, 0) + 1
    return {"fields": [f.name for f in ap.ListFields(pts) if f.type not in ("OID", "Geometry")],
            "perCID": sorted(per.items())}


@case("management.CreateRandomRaster")
def _(ap, I, O):
    x, y = I["x0"], I["y0"]
    r = ap.management.CreateRandomRaster(I["work"], "crr.tif", "UNIFORM 0.0 1.0",
                                         ap.Extent(x, y, x + 200, y + 100), 10)[0]
    ras = ap.Raster(r)
    return {"shape": [ras.height, ras.width], "pixelType": ras.pixelType,
            "inRange": bool(ras.minimum >= 0 and ras.maximum <= 1)}


@case("management.CreateReferencedMosaicDataset")
def _(ap, I, O):
    md = mosaic(ap, I, "crmd", ("dem", "zones"))
    ref = N(I, "crmd", "ref")
    ap.management.CreateReferencedMosaicDataset(md, ref, where_clause="Name = 'dem'")
    return items(ap, ref, ("Name",))


@case("management.CreateRelationshipClass")
def _(ap, I, O):
    origin = cp(ap, I, O, "polys", "origin")
    dest = cp(ap, I, O, "points", "dest")
    rc = N(I, "crc", "rc")
    ap.management.CreateRelationshipClass(origin, dest, rc, "COMPOSITE", "pts", "poly", "FORWARD", "ONE_TO_MANY",
                                          "NONE", "gid", "pid")
    d = ap.Describe(rc)
    return {"cardinality": d.cardinality, "isComposite": d.isComposite, "forward": d.forwardPathLabel,
            "backward": d.backwardPathLabel, "notification": d.notification,
            "origin": [os.path.basename(n) for n in d.originClassNames],
            "destination": [os.path.basename(n) for n in d.destinationClassNames],
            "originKeys": [list(k) for k in d.originClassKeys], "isAttributed": d.isAttributed}


@case("management.CreateSQLiteDatabase")
def _(ap, I, O):
    db = ap.management.CreateSQLiteDatabase(os.path.join(I["work"], "csd.gpkg"), "GEOPACKAGE")[0]
    d = ap.Describe(db)
    kind = [d.dataType, d.workspaceType]
    fc = ap.management.CreateFeatureclass(db, "pts", "POINT", spatial_reference=ap.SpatialReference(I["srid"]))[0]
    return {"exists": ap.Exists(db), "fc": ap.Describe(fc).shapeType, "type": kind}


@case("management.CreateTable")
def _(ap, I, O):
    ap.management.CreateTable(*split(O("out")), I["table"])
    ap.management.CreateTable(*split(O("two")), [I["table"], I["polys"]])


@case("management.CreateTopology")
def _(ap, I, O):
    sr = ap.SpatialReference(I["srid"])
    fd = ap.management.CreateFeatureDataset(I["gdb"], "ct_fd", sr)[0]
    t1 = ap.management.CreateTopology(fd, "ct_topo")[0]
    t2 = ap.management.CreateTopology(fd, "ct_topo2", 0.05)[0]
    return [[ap.Describe(t).dataType, round(ap.Describe(t).clusterTolerance, 6),
             list(ap.Describe(t).featureClassNames)] for t in (t1, t2)]


@case("management.CreateUnRegisteredFeatureclass")
def _(ap, I, O):
    ws = geopackage(ap, I, "curf")
    fc = ap.management.CreateUnRegisteredFeatureclass(ws, "ufc", "POINT", I["points"],
                                                      spatial_reference=ap.SpatialReference(I["srid"]))[0]
    return [[f.name, f.type] for f in ap.ListFields(fc)]


@case("management.CreateUnRegisteredTable")
def _(ap, I, O):
    ws = geopackage(ap, I, "curt")
    t = ap.management.CreateUnRegisteredTable(ws, "ut")[0]
    t2 = ap.management.CreateUnRegisteredTable(ws, "ut2", I["table"])[0]
    return [[[f.name, f.type] for f in ap.ListFields(x)] for x in (t, t2)]


@case("management.CreateVersion")
def _(ap, I, O):
    ap.management.CreateVersion(I["gdb"], "DEFAULT", "v1", "PUBLIC")
    return sorted(ap.ListVersions(I["gdb"]))


@case("management.DefineMosaicDatasetNoData")
def _(ap, I, O):
    md = mosaic(ap, I, "dmdn", ("zones",))
    ap.management.DefineMosaicDatasetNoData(md, 1, "BAND_1 1")
    ap.management.CopyRaster(md, O("out", "raster"))


@case("management.DefineProjection")
def _(ap, I, O):
    fc = cp(ap, I, O, "points")
    ap.management.DefineProjection(fc, ap.SpatialReference(3857))
    r = ap.management.CopyRaster(I["dem"], O("r", "raster"))[0]
    ap.management.DefineProjection(r, ap.SpatialReference(32636))


@case("management.Delete")
def _(ap, I, O):
    fc = ap.management.CopyFeatures(I["polys"], N(I, "del", "fc"))[0]
    r = ap.management.CopyRaster(I["dem"], os.path.join(I["work"], "del.tif"))[0]
    ap.management.Delete(fc)
    ap.management.Delete(r)
    return [ap.Exists(fc), ap.Exists(r), os.path.exists(r)]


@case("management.DeleteAttributeRule")
def _(ap, I, O):
    fc = with_rule(ap, I, O)
    ap.management.AddAttributeRule(fc, "keep", "CALCULATION", "return 1", "EDITABLE", "INSERT", field="gid")
    ap.management.DeleteAttributeRule(fc, "dbl")
    insert_poly(ap, I, fc, 7)
    return [r["name"] for r in rules(ap, fc)]


@case("management.DeleteCodedValueFromDomain")
def _(ap, I, O):
    ap.management.CreateDomain(I["gdb"], "dcv_dom", "codes", "LONG", "CODED")
    for code in (1, 2, 3):
        ap.management.AddCodedValueToDomain(I["gdb"], "dcv_dom", code, f"c{code}")
    ap.management.DeleteCodedValueFromDomain(I["gdb"], "dcv_dom", "1;3")
    return doms(ap, I["gdb"], ["dcv_dom"])


@case("management.DeleteDatabaseSequence")
def _(ap, I, O):
    ap.management.CreateDatabaseSequence(I["gdb"], "dds_seq", 1, 1)
    ap.management.DeleteDatabaseSequence(I["gdb"], "dds_seq")
    return [s.name for s in ap.da.ListDatabaseSequences(I["gdb"]) if s.name == "dds_seq"]


@case("management.DeleteDomain")
def _(ap, I, O):
    ap.management.CreateDomain(I["gdb"], "dd_dom", "x", "LONG", "CODED")
    ap.management.DeleteDomain(I["gdb"], "dd_dom")
    return doms(ap, I["gdb"], ["dd_dom"])


@case("management.DeleteFeatures")
def _(ap, I, O):
    fc = cp(ap, I, O)
    lyr = ap.management.MakeFeatureLayer(fc, "df_lyr", "gid > 3")[0]
    ap.management.DeleteFeatures(lyr)


@case("management.DeleteField")
def _(ap, I, O):
    t = ctab(ap, I, O)
    ap.management.DeleteField(t, "cat")
    k = ctab(ap, I, O, "keep")
    ap.management.DeleteField(k, "val", "KEEP_FIELDS")


@case("management.DeleteFieldGroup")
def _(ap, I, O):
    fc = cp(ap, I, O)
    ap.management.CreateFieldGroup(fc, "fg", "cat;val")
    ap.management.AddContingentValue(fc, "fg", [["cat", "ANY", ""], ["val", "NULL", ""]])
    ap.management.CreateFieldGroup(fc, "fg2", "gid;val")
    ap.management.AddContingentValue(fc, "fg2", [["gid", "ANY", ""], ["val", "ANY", ""]])
    ap.management.DeleteFieldGroup(fc, "fg")
    return cvs(ap, fc)


@case("management.DeleteIdentical")
def _(ap, I, O):
    t = ctab(ap, I, O)
    ap.management.DeleteIdentical(t, "cat")
    p = cp(ap, I, O, "points", "pts")
    ap.management.DeleteIdentical(p, "cat")


@case("management.DeleteMosaicDataset")
def _(ap, I, O):
    md = mosaic(ap, I, "dmd")
    ap.management.DeleteMosaicDataset(md)
    return [ap.Exists(md), ap.Exists(I["dem"])]


@case("management.DeleteMultiple")
def _(ap, I, O):
    a = ap.management.CopyFeatures(I["polys"], N(I, "dm", "a"))[0]
    b = ap.management.CopyRows(I["table"], N(I, "dm", "b"))[0]
    ap.management.DeleteMultiple([a, b])
    return [ap.Exists(a), ap.Exists(b)]


@case("management.DeleteRasterAttributeTable")
def _(ap, I, O):
    r = ap.management.CopyRaster(I["zones"], O("out", "raster"))[0]
    ap.management.BuildRasterAttributeTable(r)
    before = [f.name for f in ap.ListFields(r)]
    ap.management.DeleteRasterAttributeTable(r)
    return [before, [f.name for f in ap.ListFields(r)]]


@case("management.DeleteRows")
def _(ap, I, O):
    t = ctab(ap, I, O)
    view = ap.management.MakeTableView(t, "dr_view", "rid > 6")[0]
    ap.management.DeleteRows(view)


@case("management.DeleteVersion")
def _(ap, I, O):
    ap.management.DeleteVersion(I["gdb"], "v1")
    return sorted(ap.ListVersions(I["gdb"]))


@case("management.Dice")
def _(ap, I, O):
    import math
    ring = [(I["x0"] + 300 + 200 * math.cos(2 * math.pi * k / 120), I["y0"] + 300 + 200 * math.sin(2 * math.pi * k / 120))
            for k in range(120)]
    wkt = "POLYGON ((" + ", ".join(f"{x} {y}" for x, y in ring + ring[:1]) + "))"
    fc = new_fc(ap, I, *split(N(I, "dice", "in")), "POLYGON", [("k", "LONG")], [(1, wkt)])
    ap.management.Dice(fc, O("out"), 40)


@case("management.DisableArchiving")
def _(ap, I, O):
    ws = local_db(ap, I, "darch")
    fc = new_fc(ap, I, ws, "fc", "POINT", [("a", "LONG")], [(1, f"POINT ({I['x0']} {I['y0']})")])
    ap.management.EnableArchiving(fc)
    with ap.da.UpdateCursor(fc, ["a"]) as c:
        for row in c:
            c.updateRow([2])
    ap.management.DisableArchiving(fc, "PRESERVE")
    d = ap.da.Describe(fc)
    ws_old = ap.env.workspace
    ap.env.workspace = ws
    try:
        names = sorted(n.split(".")[-1] for n in ap.ListFeatureClasses() + ap.ListTables())
    finally:
        ap.env.workspace = ws_old
    return {"isArchived": d.get("isArchived"), "names": names}


@case("management.DisableAttachments")
def _(ap, I, O):
    fc = cp(ap, I, O)
    ap.management.EnableAttachments(fc)
    ap.management.DisableAttachments(fc)
    return [ap.Exists(fc + "__ATTACH"), ap.Exists(fc + "__ATTACHREL")]


@case("management.DisableAttributeRules")
def _(ap, I, O):
    fc = with_rule(ap, I, O)
    ap.management.DisableAttributeRules(fc, "dbl")
    insert_poly(ap, I, fc, 7)
    return [[r["name"], r["isEnabled"]] for r in rules(ap, fc)]


@case("management.DisableCOGO")
def _(ap, I, O):
    fc = cp(ap, I, O, "lines")
    ap.management.EnableCOGO(fc)
    ap.management.DisableCOGO(fc)
    return {"isCOGOEnabled": ap.Describe(fc).isCOGOEnabled, "fields": fields(ap, fc)}


@case("management.DisableEditorTracking")
def _(ap, I, O):
    fc = cp(ap, I, O)
    ap.management.EnableEditorTracking(fc, "cr", "crd", "ed", "edd", "ADD_FIELDS", "UTC")
    ap.management.DisableEditorTracking(fc, "DISABLE_CREATOR", "NO_DISABLE_CREATION_DATE", "DISABLE_LAST_EDITOR",
                                        "NO_DISABLE_LAST_EDIT_DATE")
    d = ap.Describe(fc)
    return {"enabled": d.editorTrackingEnabled, "creator": d.creatorFieldName, "createdAt": d.createdAtFieldName,
            "editor": d.editorFieldName, "editedAt": d.editedAtFieldName,
            "fields": [f[0] for f in fields(ap, fc)]}


@case("management.DisableLastEditTime")
def _(ap, I, O):
    ws = local_db(ap, I, "dlet")   # a file geodatabase answers ERROR 004038, last edit time is not supported
    fc = new_fc(ap, I, ws, "fc", "POINT", [("a", "LONG")], [(1, f"POINT ({I['x0']} {I['y0']})")])
    ap.management.EnableLastEditTime(fc)
    ap.management.DisableLastEditTime(fc)
    return True


@case("management.Dissolve")
def _(ap, I, O):
    ap.management.Dissolve(I["polys"], O("out"), "cat", [["val", "SUM"], ["val", "MEAN"], ["gid", "COUNT"]])
    ap.management.Dissolve(I["polys"], O("all"), None, None, "SINGLE_PART")
    ap.management.Dissolve(I["lines"], O("lines"), "kind", None, "MULTI_PART", "UNSPLIT_LINES")


@case("management.DomainToTable")
def _(ap, I, O):
    ap.management.CreateDomain(I["gdb"], "dtt_dom", "codes", "LONG", "CODED")
    for code, text in ((3, "three"), (1, "one"), (2, "two")):
        ap.management.AddCodedValueToDomain(I["gdb"], "dtt_dom", code, text)
    ap.management.DomainToTable(I["gdb"], "dtt_dom", O("out"), "code", "descr")


@case("management.DowngradeAttachments")
def _(ap, I, O):
    fc = cp(ap, I, O)
    ap.management.EnableAttachments(fc)
    ap.management.DowngradeAttachments(fc)
    return [[f.name, f.type] for f in ap.ListFields(fc + "__ATTACH")]


@case("management.Eliminate")
def _(ap, I, O):
    rows = [(1, "POLYGON " + sq(0, 0, 200, 200)), (2, "POLYGON " + sq(200, 0, 450, 200)),
            (3, "POLYGON " + sq(0, 200, 300, 210)), (4, "POLYGON " + sq(0, 210, 450, 400))]
    fc = new_fc(ap, I, *split(O("in")), "POLYGON", [("k", "LONG")], rows)
    lyr = ap.management.MakeFeatureLayer(fc, "elim_lyr")[0]
    ap.management.SelectLayerByAttribute(lyr, "NEW_SELECTION", "k = 3")
    ap.management.Eliminate(lyr, O("length"), "LENGTH")
    ap.management.Eliminate(lyr, O("area"), "AREA")


@case("management.EliminatePolygonPart")
def _(ap, I, O):
    x, y = I["x0"], I["y0"]
    outer = f"({x} {y}, {x} {y + 300}, {x + 300} {y + 300}, {x + 300} {y}, {x} {y})"
    hole = f"({x + 100} {y + 100}, {x + 110} {y + 100}, {x + 110} {y + 110}, {x + 100} {y + 110}, {x + 100} {y + 100})"
    big_hole = (f"({x + 150} {y + 150}, {x + 250} {y + 150}, {x + 250} {y + 250}, {x + 150} {y + 250}, "
                f"{x + 150} {y + 150})")
    island = f"(({x + 400} {y}, {x + 400} {y + 20}, {x + 420} {y + 20}, {x + 420} {y}, {x + 400} {y}))"
    wkt = f"MULTIPOLYGON (({outer}, {hole}, {big_hole}), {island})"
    fc = new_fc(ap, I, *split(O("in")), "POLYGON", [("k", "LONG")], [(1, wkt)])
    ap.management.EliminatePolygonPart(fc, O("contained"), "AREA", "500 SquareMeters", 0, "CONTAINED_ONLY")
    ap.management.EliminatePolygonPart(fc, O("any"), "AREA", "500 SquareMeters", 0, "ANY")
    ap.management.EliminatePolygonPart(fc, O("percent"), "PERCENT", None, 5, "CONTAINED_ONLY")


@case("management.EnableArchiving")
def _(ap, I, O):
    ws = local_db(ap, I, "earch")
    fc = new_fc(ap, I, ws, "fc", "POINT", [("a", "LONG")], [(1, f"POINT ({I['x0']} {I['y0']})")])
    ap.management.EnableArchiving(fc)
    return {"isArchived": ap.da.Describe(fc).get("isArchived"),
            "fields": sorted(f.name for f in ap.ListFields(fc) if f.type not in ("OID", "Geometry"))}


@case("management.EnableAttachments")
def _(ap, I, O):
    fc = cp(ap, I, O)
    ap.management.EnableAttachments(fc)
    return {"attach": [[f.name, f.type] for f in ap.ListFields(fc + "__ATTACH") if f.type != "OID"],
            "rel": ap.Exists(fc + "__ATTACHREL"), "hasGlobalID": ap.Describe(fc).hasGlobalID}


@case("management.EnableAttributeRules")
def _(ap, I, O):
    fc = with_rule(ap, I, O)
    ap.management.DisableAttributeRules(fc, "dbl")
    ap.management.EnableAttributeRules(fc, "dbl")
    insert_poly(ap, I, fc, 7)
    return [[r["name"], r["isEnabled"]] for r in rules(ap, fc)]
