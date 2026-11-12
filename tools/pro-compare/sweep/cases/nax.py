"""arcpy.nax against ArcGIS Pro 3.7: one case for each name grapy builds.

Every case runs on a street grid built here, in its own geodatabase (``nax``), so this module does not depend on the
arcpy.na sweep's network:

- ``net/streets``: a 5 × 5 grid of junctions 100 m apart, each block one line feature, with ``minutes`` (the middle
  east–west street is fast, 0.1 min a block; the rest 0.3) and ``name``;
- ``net/nd``: a network dataset over it with ``Length`` (metres, from CreateNetworkDataset) and ``Minutes`` (the
  ``minutes`` field), and two travel modes written with ReplaceNetworkTravelModes: ``Driving`` (Minutes, time) and
  ``Distance`` (Length).

Pro's CreateNetworkDataset makes only ``Length`` and no travel mode, and arcpy has no tool that adds an attribute: on
Pro the ``Minutes`` attribute is added through a template (CreateTemplateFromNetworkDataset, the XML given one more
attribute, CreateNetworkDatasetFromTemplate), on grapy with ``arcpy.graticula.AddNetworkAttribute`` (grapy refuses the
template tools). That is the one place the two sides build differently; everything after is the same calls.
"""
import json
import os
import re
import shutil

CASES = {}
_STATE = {}


def case(name):
    def register(function):
        CASES[name] = function
        return function
    return register


def _pro(ap):
    return not hasattr(ap, "graticula")


def _xy(I, x, y):
    return (I["x0"] + x, I["y0"] + y)


def _streets(ap, I, fd, name="streets"):
    sr = ap.SpatialReference(I["srid"])
    path = ap.management.CreateFeatureclass(fd, name, "POLYLINE", spatial_reference=sr)[0]
    ap.management.AddField(path, "minutes", "DOUBLE")
    ap.management.AddField(path, "name", "TEXT")
    x0, y0 = I["x0"], I["y0"]
    with ap.da.InsertCursor(path, ["minutes", "name", "SHAPE@"]) as cursor:
        for i in range(5):                   # east-west streets, then north-south ones
            y = 100 + 100 * i
            for j in range(4):
                x = 100 + 100 * j
                wkt = f"LINESTRING ({x0 + x} {y0 + y}, {x0 + x + 100} {y0 + y})"
                cursor.insertRow((0.1 if i == 2 else 0.3, f"EW{i}", ap.FromWKT(wkt, sr)))
        for i in range(5):
            x = 100 + 100 * i
            for j in range(4):
                y = 100 + 100 * j
                wkt = f"LINESTRING ({x0 + x} {y0 + y}, {x0 + x} {y0 + y + 100})"
                cursor.insertRow((0.3, f"NS{i}", ap.FromWKT(wkt, sr)))
    return path


_MODES = {
    "Driving": {"name": "Driving", "type": "AUTOMOBILE", "description": "", "impedanceAttributeName": "Minutes",
                "timeAttributeName": "Minutes", "distanceAttributeName": "Length", "restrictionAttributeNames": [],
                "attributeParameterValues": [], "useHierarchy": False, "uturnAtJunctions": "esriNFSBAllowBacktrack",
                "simplificationTolerance": None, "simplificationToleranceUnits": "esriMeters"},
    "Distance": {"name": "Distance", "type": "OTHER", "description": "", "impedanceAttributeName": "Length",
                 "timeAttributeName": "Minutes", "distanceAttributeName": "Length", "restrictionAttributeNames": [],
                 "attributeParameterValues": [], "useHierarchy": False, "uturnAtJunctions": "esriNFSBAllowBacktrack",
                 "simplificationTolerance": None, "simplificationToleranceUnits": "esriMeters"},
}


def _add_minutes_by_template(ap, fd, nd, folder):
    """Pro only: the network given a Minutes attribute through its XML template."""
    xml = os.path.join(folder, "template.xml")
    if os.path.exists(xml):
        os.remove(xml)
    ap.na.CreateTemplateFromNetworkDataset(nd, xml)
    ap.management.Delete(nd)
    text = open(xml, encoding="utf-8").read()
    attribute = re.search(r"<EvaluatedNetworkAttribute .*?</EvaluatedNetworkAttribute>", text, re.S).group(0)
    minutes = attribute.replace("<ID>1</ID>", "<ID>2</ID>").replace("<Name>Length</Name>", "<Name>Minutes</Name>") \
        .replace("<Units>Meters</Units>", "<Units>Minutes</Units>")
    text = text.replace("</EvaluatedNetworkAttributes>", minutes + "</EvaluatedNetworkAttributes>")
    assignments = re.findall(r"<NetworkAssignment .*?</NetworkAssignment>", text, re.S)
    added = "".join(a.replace("<NetworkAttributeName>Length</NetworkAttributeName>",
                              "<NetworkAttributeName>Minutes</NetworkAttributeName>").replace("!Shape!", "!minutes!")
                    for a in assignments if "<NetworkAttributeName>Length</NetworkAttributeName>" in a)
    text = text.replace("</NetworkAssignments>", added + "</NetworkAssignments>")
    open(xml, "w", encoding="utf-8").write(text)
    ap.na.CreateNetworkDatasetFromTemplate(xml, fd)


def _modes_file(folder):
    path = os.path.join(folder, "modes.json")
    json.dump(_MODES, open(path, "w", encoding="utf-8"))
    return path


def _net(ap, I):
    """The street grid's network, built once a run: {gdb, fd, streets, nd, folder}."""
    if "nd" in _STATE:
        return _STATE
    folder = os.path.join(I["work"], "nax")
    shutil.rmtree(folder, ignore_errors=True)
    os.makedirs(folder)
    gdb = ap.management.CreateFileGDB(folder, "nax")[0]
    sr = ap.SpatialReference(I["srid"])
    fd = ap.management.CreateFeatureDataset(gdb, "net", sr)[0]
    streets = _streets(ap, I, fd)
    nd = ap.na.CreateNetworkDataset(fd, "nd", ["streets"], "NO_ELEVATION")[0]
    if _pro(ap):
        _add_minutes_by_template(ap, fd, nd, folder)
        nd = os.path.join(fd, "nd")
    else:
        ap.graticula.AddNetworkAttribute(nd, "Minutes", "Cost", "Minutes", [("streets", "BOTH", "FIELD", "minutes")])
    ap.na.BuildNetwork(nd)
    ap.na.ReplaceNetworkTravelModes(_modes_file(folder), nd)
    _STATE.update(gdb=gdb, fd=fd, streets=streets, nd=str(nd), folder=folder)
    return _STATE


def _r(v, n=4):
    if isinstance(v, float):
        return round(v, n)
    if isinstance(v, (list, tuple)):
        return [_r(x, n) for x in v]
    if isinstance(v, dict):
        return {k: _r(x, n) for k, x in v.items()}
    return v


def _enum(ap, name):
    """An enumeration's members as [name, value], in definition order."""
    return [[m.name, m.value] for m in getattr(ap.nax, name)]


def _points(ap, I, name, coordinates, fields=()):
    """A point feature class in the sweep geodatabase: [(x, y, *values)] relative to the fixture's corner."""
    sr = ap.SpatialReference(I["srid"])
    path = os.path.join(I["gdb"], name)
    if ap.Exists(path):
        ap.management.Delete(path)
    ap.management.CreateFeatureclass(I["gdb"], name, "POINT", spatial_reference=sr)
    for field, kind in fields:
        ap.management.AddField(path, field, kind)
    with ap.da.InsertCursor(path, [f for f, _ in fields] + ["SHAPE@XY"]) as cursor:
        for x, y, *values in coordinates:
            cursor.insertRow((*values, _xy(I, x, y)))
    return path


def _insert(ap, I, solver, input_type, fields, rows):
    """Rows into a solver's input: fields named, the shape last as SHAPE@XY relative to the fixture's corner."""
    with solver.insertCursor(input_type, [*fields, "SHAPE@XY"]) as cursor:
        for *values, (x, y) in rows:
            cursor.insertRow((*values, _xy(I, x, y)))


def _export(ap, result, output_type, path):
    result.export(output_type, path)


# ---- enumerations ----------------------------------------------------------------------------------------------

for _name in ("AttributeParameterUsage", "AttributeUsage", "ClosestFacilityInputDataType",
              "ClosestFacilityOutputDataType", "CurbApproach", "DecayFunctionType", "DistanceUnits", "DrivingSide",
              "Importance", "LastMileDeliveryInputDataType", "LastMileDeliveryOutputDataType", "LineShapeType",
              "LocationAllocationInputDataType", "LocationAllocationOutputDataType", "LocationAllocationProblemType",
              "MessageSeverity", "NetworkTimeUsage", "OriginDestinationCostMatrixInputDataType",
              "OriginDestinationCostMatrixOutputDataType", "RouteInputDataType", "RouteOutputDataType", "RouteShapeType",
              "ServiceAreaInputDataType", "ServiceAreaOutputDataType", "ServiceAreaOutputType",
              "ServiceAreaOverlapGeometry", "ServiceAreaPolygonCutoffGeometry", "ServiceAreaPolygonDetail",
              "StopCollectionMode", "TimeOfDayUsage", "TimeUnits", "TimeZoneUsage", "TravelDirection",
              "TraversedElementType", "VehicleRoutingProblemInputDataType", "VehicleRoutingProblemInputDataType2",
              "VehicleRoutingProblemOutputDataType", "VehicleRoutingProblemOutputDataType2",
              "VehicleRoutingProblemSchemaVersion", "WasteCollectionInputDataType", "WasteCollectionOutputDataType"):
    CASES[f"nax.{_name}"] = (lambda n: lambda ap, I, O: _enum(ap, n))(_name)


# ---- network dataset tools ---------------------------------------------------------------------------------------

def _fresh_fd(ap, I, name):
    net = _net(ap, I)
    sr = ap.SpatialReference(I["srid"])
    fd = ap.management.CreateFeatureDataset(net["gdb"], name, sr)[0]
    return fd, _streets(ap, I, fd, "s_" + name)


def _attributes(ap, nd):
    return sorted([a.name, a.usageType, a.units] for a in ap.Describe(nd).attributes)


@case("nax.CreateNetworkDataset")
def _(ap, I, O):
    fd, streets = _fresh_fd(ap, I, "cnd")
    nd = ap.nax.CreateNetworkDataset(fd, "cnd_nd", [os.path.basename(streets)], "NO_ELEVATION")[0]
    desc = ap.Describe(nd)
    return {"dataType": desc.dataType, "attributes": _attributes(ap, nd),
            "travelModes": sorted(ap.nax.GetTravelModes(nd)), "isBuilt": ap.nax.NetworkDataset(nd).isBuilt}


@case("nax.BuildNetwork")
def _(ap, I, O):
    fd, streets = _fresh_fd(ap, I, "bnd")
    nd = ap.nax.CreateNetworkDataset(fd, "bnd_nd", [os.path.basename(streets)], "NO_ELEVATION")[0]
    ap.nax.BuildNetwork(nd)
    n = ap.nax.NetworkDataset(nd)
    return {"isBuilt": n.isBuilt, "edgeCount": n.edgeCount, "junctionCount": n.junctionCount,
            "turnCount": n.turnCount}


@case("nax.MakeNetworkDatasetLayer")
def _(ap, I, O):
    net = _net(ap, I)
    result = ap.nax.MakeNetworkDatasetLayer(net["nd"], "nd_layer")
    route = ap.nax.Route("nd_layer")              # the layer is usable where the dataset is
    route.travelMode = "Distance"
    _insert(ap, I, route, ap.nax.RouteInputDataType.Stops, ["Name"], [("a", (100, 103)), ("b", (300, 103))])
    solved = route.solve()
    with_rows = list(solved.searchCursor(ap.nax.RouteOutputDataType.Routes, ["Total_Kilometers"]))
    return {"name": str(result[0]) if not hasattr(result[0], "name") else result[0].name,
            "route": _r([r[0] for r in with_rows])}


@case("nax.GetTravelModes")
def _(ap, I, O):
    net = _net(ap, I)
    modes = ap.nax.GetTravelModes(net["nd"])
    return {name: [mode.name, mode.impedance, mode.timeAttributeName, mode.distanceAttributeName]
            for name, mode in sorted(modes.items())}


@case("nax.TravelMode")
def _(ap, I, O):
    net = _net(ap, I)
    mode = ap.nax.GetTravelModes(net["nd"])["Driving"]
    copy = ap.nax.TravelMode(mode)
    copy.name = "Copy"
    copy.impedance = "Length"
    text = json.loads(str(copy))
    return {"properties": [mode.name, mode.impedance, mode.timeAttributeName, mode.distanceAttributeName,
                           list(mode.restrictions), mode.useHierarchy, str(mode.uTurns), str(mode.type),
                           mode.description, mode.simplificationTolerance],
            "json": {k: text.get(k) for k in ("name", "impedanceAttributeName", "timeAttributeName",
                                              "distanceAttributeName", "restrictionAttributeNames", "useHierarchy",
                                              "uturnAtJunctions", "type")}}


@case("nax.ExportNetworkTravelModes")
def _(ap, I, O):
    net = _net(ap, I)
    out = os.path.join(net["folder"], "exported.json")
    if os.path.exists(out):
        os.remove(out)
    ap.nax.ExportNetworkTravelModes(net["nd"], out)
    modes = json.load(open(out, encoding="utf-8"))
    keys = ("name", "type", "impedanceAttributeName", "timeAttributeName", "distanceAttributeName",
            "restrictionAttributeNames", "useHierarchy", "uturnAtJunctions")
    return {"names": list(modes), "modes": {n: {k: m.get(k) for k in keys} for n, m in modes.items()},
            "keys": {n: sorted(m) for n, m in modes.items()}}


@case("nax.ReplaceNetworkTravelModes")
def _(ap, I, O):
    fd, streets = _fresh_fd(ap, I, "rtm")
    nd = ap.nax.CreateNetworkDataset(fd, "rtm_nd", [os.path.basename(streets)], "NO_ELEVATION")[0]
    ap.nax.BuildNetwork(nd)
    path = os.path.join(_net(ap, I)["folder"], "one_mode.json")
    json.dump({"Walk": dict(_MODES["Distance"], name="Walk", timeAttributeName="")}, open(path, "w"))
    ap.nax.ReplaceNetworkTravelModes(path, nd)
    modes = ap.nax.GetTravelModes(nd)
    return {n: [m.impedance, m.distanceAttributeName, m.timeAttributeName or None] for n, m in modes.items()}


@case("nax.CalculateLocations")
def _(ap, I, O):
    net = _net(ap, I)
    points = _points(ap, I, "calc_points", [(150, 104, 1), (196, 300, 2), (420, 466, 3), (40, 40, 4)],
                     [("pid", "LONG")])
    out = O("out")
    ap.management.CopyFeatures(points, out)
    ap.nax.CalculateLocations(out, net["nd"], "5000 Meters")
    return None


def _turn_net(ap, I):
    """A second network with a turn feature class as a source, for the turn tools."""
    if "turn_nd" in _STATE:
        return _STATE["turn_fd"], _STATE["turn_fc"], _STATE["turn_nd"]
    fd, streets = _fresh_fd(ap, I, "tn")
    sr = ap.SpatialReference(I["srid"])
    turns = ap.nax.CreateTurnFeatureClass(fd, "tn_turns", 2, spatial_reference=sr)[0]
    x0, y0 = I["x0"], I["y0"]
    # from the first east-west block (ObjectID 1) left onto the second north-south street's first block (ObjectID 25)
    wkt = f"LINESTRING ({x0 + 150} {y0 + 100}, {x0 + 200} {y0 + 100}, {x0 + 200} {y0 + 150})"
    with ap.da.InsertCursor(turns, ["SHAPE@"]) as cursor:
        cursor.insertRow((ap.FromWKT(wkt, sr),))
    ap.management.AddField(streets, "AltID", "LONG")
    with ap.da.UpdateCursor(streets, ["OID@", "AltID"]) as rows:
        for oid, _ in rows:
            rows.updateRow([oid, oid + 1000])
    nd = ap.nax.CreateNetworkDataset(fd, "tn_nd", [os.path.basename(streets), "tn_turns"], "NO_ELEVATION")[0]
    ap.nax.BuildNetwork(nd)
    _STATE.update(turn_fd=fd, turn_fc=turns, turn_nd=str(nd), turn_streets=streets)
    return fd, turns, str(nd)


def _turn_rows(ap, turns, fields):
    with ap.da.SearchCursor(turns, fields) as rows:
        return [_r(list(r)) for r in rows]


_TURN_FIELDS = ["Edge1End", "Edge1FID", "Edge1Pos", "Edge2FID", "Edge2Pos"]


@case("nax.CreateTurnFeatureClass")
def _(ap, I, O):
    net = _net(ap, I)
    sr = ap.SpatialReference(I["srid"])
    out = ap.nax.CreateTurnFeatureClass(net["fd"], "ctfc_turns", 3, spatial_reference=sr)[0]
    return {"fields": sorted([f.name, f.type] for f in ap.ListFields(out)
                             if f.type not in ("OID", "Geometry") and f.name.lower() != "shape_length"),
            "shapeType": ap.Describe(out).shapeType}


@case("nax.IncreaseMaximumEdges")
def _(ap, I, O):
    net = _net(ap, I)
    sr = ap.SpatialReference(I["srid"])
    out = ap.nax.CreateTurnFeatureClass(net["fd"], "ime_turns", 2, spatial_reference=sr)[0]
    ap.nax.IncreaseMaximumEdges(out, 4)
    return sorted([f.name, f.type] for f in ap.ListFields(out)
                  if f.type not in ("OID", "Geometry") and f.name.lower() != "shape_length")


@case("nax.UpdateByGeometry")
def _(ap, I, O):
    fd, turns, nd = _turn_net(ap, I)
    ap.nax.UpdateByGeometry(turns)
    return _turn_rows(ap, turns, _TURN_FIELDS)


@case("nax.PopulateAlternateIDFields")
def _(ap, I, O):
    fd, turns, nd = _turn_net(ap, I)
    ap.nax.UpdateByGeometry(turns)
    ap.nax.PopulateAlternateIDFields(nd, "AltID")
    streets = _STATE["turn_streets"]
    return {"streets": sorted(f.name for f in ap.ListFields(streets) if "alt" in f.name.lower()),
            "turns": sorted(f.name for f in ap.ListFields(turns) if "alt" in f.name.lower()),
            "streetIds": _turn_rows(ap, streets, ["OID@", "AltID"])[:3]}


@case("nax.UpdateByAlternateIDFields")
def _(ap, I, O):
    fd, turns, nd = _turn_net(ap, I)
    ap.nax.UpdateByGeometry(turns)
    ap.nax.PopulateAlternateIDFields(nd, "AltID")
    ap.management.CalculateField(turns, "Edge1FID", "99")      # stale feature ids, right alternate ids
    ap.management.CalculateField(turns, "Edge2FID", "98")
    ap.nax.UpdateByAlternateIDFields(nd, "AltID")
    return _turn_rows(ap, turns, _TURN_FIELDS)


# ---- the network dataset query API -----------------------------------------------------------------------------------

@case("nax.NetworkDataset")
def _(ap, I, O):
    net = _net(ap, I)
    n = ap.nax.NetworkDataset(net["nd"])
    mode = ap.nax.GetTravelModes(net["nd"])["Driving"]
    edges = [list(r) for r in n.edges(["SOURCEOID", "FROMPOSITION", "TOPOSITION", "DIRECTION", "COST", "TIME",
                                       "DISTANCE"], travel_mode=mode)]
    return {"isBuilt": n.isBuilt, "edgeCount": n.edgeCount, "junctionCount": n.junctionCount,
            "turnCount": n.turnCount, "travelModes": sorted(n.travelModes),
            "edges": _r(sorted(edges, key=lambda e: (e[0], not e[3])))[:6], "edgeRows": len(edges),
            "junctions": len(list(n.junctions(["EID"])))}


@case("nax.NAClassFieldMap")
def _(ap, I, O):
    # Pro's NAClassFieldMap cannot be made directly ("cannot create 'NAClassFieldMap' instances"): it comes from a
    # solver's fieldMappings, and its properties are set there.
    net = _net(ap, I)
    route = ap.nax.Route(net["nd"])
    mappings = route.fieldMappings(ap.nax.RouteInputDataType.Stops)
    m = mappings["Name"]
    before = [m.mappedFieldName, m.defaultValue]
    m.mappedFieldName = "label"
    m.defaultValue = "x"
    return {"before": before, "after": [m.mappedFieldName, m.defaultValue]}


@case("nax.NAClassFieldMappings")
def _(ap, I, O):
    net = _net(ap, I)
    route = ap.nax.Route(net["nd"])
    stops = _points(ap, I, "map_stops", [(100, 103, "a", 2)], [("Name", "TEXT"), ("Sequence", "LONG")])
    mappings = route.fieldMappings(ap.nax.RouteInputDataType.Stops, False, ap.ListFields(stops))
    return {"isDict": isinstance(mappings, dict),
            "maps": {k: [v.mappedFieldName, v.defaultValue] for k, v in sorted(mappings.items())}}


# ---- solvers --------------------------------------------------------------------------------------------------------

def _solver(ap, I, cls, mode="Driving"):
    net = _net(ap, I)
    solver = getattr(ap.nax, cls)(net["nd"])
    solver.travelMode = ap.nax.GetTravelModes(net["nd"])[mode]
    return solver


@case("nax.Route")
def _(ap, I, O):
    route = _solver(ap, I, "Route")
    _insert(ap, I, route, ap.nax.RouteInputDataType.Stops, ["Name"],
            [("a", (110, 103)), ("b", (397, 290)), ("c", (250, 498))])
    result = route.solve()
    T = ap.nax.RouteOutputDataType
    for kind, name in ((T.Routes, "routes"), (T.Stops, "stops"), (T.RouteEdges, "edges")):
        _export(ap, result, kind, O(name))
    return {"solveSucceeded": result.solveSucceeded, "counts": [result.count(T.Routes), result.count(T.Stops)],
            "routeFields": result.fieldNames(T.Routes)}


@case("nax.ClosestFacility")
def _(ap, I, O):
    cf = _solver(ap, I, "ClosestFacility")
    T = ap.nax.ClosestFacilityInputDataType
    _insert(ap, I, cf, T.Facilities, ["Name"], [("F1", (100, 410)), ("F2", (480, 200))])
    _insert(ap, I, cf, T.Incidents, ["Name"], [("I1", (150, 103)), ("I2", (397, 450)), ("I3", (300, 290))])
    result = cf.solve()
    U = ap.nax.ClosestFacilityOutputDataType
    for kind, name in ((U.Routes, "routes"), (U.Facilities, "facilities"), (U.Incidents, "incidents")):
        _export(ap, result, kind, O(name))
    return {"solveSucceeded": result.solveSucceeded, "routeFields": result.fieldNames(U.Routes)}


@case("nax.OriginDestinationCostMatrix")
def _(ap, I, O):
    od = _solver(ap, I, "OriginDestinationCostMatrix")
    T = ap.nax.OriginDestinationCostMatrixInputDataType
    _insert(ap, I, od, T.Origins, ["Name"], [("O1", (100, 103)), ("O2", (497, 500))])
    _insert(ap, I, od, T.Destinations, ["Name"], [("D1", (300, 300)), ("D2", (150, 497)), ("D3", (403, 120))])
    result = od.solve()
    U = ap.nax.OriginDestinationCostMatrixOutputDataType
    for kind, name in ((U.Lines, "lines"), (U.Origins, "origins"), (U.Destinations, "destinations")):
        _export(ap, result, kind, O(name))
    return {"solveSucceeded": result.solveSucceeded, "lineFields": result.fieldNames(U.Lines)}


@case("nax.ServiceArea")
def _(ap, I, O):
    sa = _solver(ap, I, "ServiceArea", "Distance")
    sa.defaultImpedanceCutoffs = [0.15, 0.25]        # in distanceUnits (kilometres by default)
    sa.outputType = ap.nax.ServiceAreaOutputType.PolygonsAndLines
    T = ap.nax.ServiceAreaInputDataType
    _insert(ap, I, sa, T.Facilities, ["Name"], [("F1", (300, 300))])
    result = sa.solve()
    U = ap.nax.ServiceAreaOutputDataType
    for kind, name in ((U.Polygons, "polygons"), (U.Lines, "lines"), (U.Facilities, "facilities")):
        _export(ap, result, kind, O(name))
    return {"solveSucceeded": result.solveSucceeded, "counts": [result.count(U.Polygons), result.count(U.Lines)]}


@case("nax.LocationAllocation")
def _(ap, I, O):
    la = _solver(ap, I, "LocationAllocation")
    la.facilityCount = 2
    T = ap.nax.LocationAllocationInputDataType
    _insert(ap, I, la, T.Facilities, ["Name"], [("C1", (100, 100)), ("C2", (300, 300)), ("C3", (500, 500)),
                                                 ("C4", (500, 100))])
    _insert(ap, I, la, T.DemandPoints, ["Name", "Weight"],
            [("D1", 1, (150, 103)), ("D2", 2, (250, 297)), ("D3", 1, (450, 497)), ("D4", 3, (497, 150)),
             ("D5", 1, (103, 450))])
    result = la.solve()
    U = ap.nax.LocationAllocationOutputDataType
    for kind, name in ((U.Facilities, "facilities"), (U.DemandPoints, "demand"), (U.Lines, "lines")):
        _export(ap, result, kind, O(name))
    return {"solveSucceeded": result.solveSucceeded}


@case("nax.VehicleRoutingProblem")
def _(ap, I, O):
    vrp = _solver(ap, I, "VehicleRoutingProblem")
    T = ap.nax.VehicleRoutingProblemInputDataType
    _insert(ap, I, vrp, T.Depots, ["Name"], [("Depot", (300, 300))])
    _insert(ap, I, vrp, T.Orders, ["Name", "ServiceTime"],
            [("O1", 1.0, (150, 103)), ("O2", 1.0, (497, 450)), ("O3", 1.0, (103, 450))])
    with vrp.insertCursor(T.Routes, ["Name", "StartDepotName", "EndDepotName", "MaxOrderCount"]) as cursor:
        cursor.insertRow(("Truck", "Depot", "Depot", 10))
    result = vrp.solve()
    U = ap.nax.VehicleRoutingProblemOutputDataType
    for kind, name in ((U.Routes, "routes"), (U.Stops, "stops")):
        _export(ap, result, kind, O(name))
    return {"version": str(getattr(vrp, "version", None)), "solveSucceeded": result.solveSucceeded}


@case("nax.LastMileDelivery")
def _(ap, I, O):
    lmd = _solver(ap, I, "LastMileDelivery")
    T = ap.nax.LastMileDeliveryInputDataType
    _insert(ap, I, lmd, T.Depots, ["Name"], [("Depot", (300, 300))])
    _insert(ap, I, lmd, T.Orders, ["Name", "ServiceTime"],
            [("O1", 1.0, (150, 103)), ("O2", 1.0, (497, 450)), ("O3", 1.0, (103, 450))])
    with lmd.insertCursor(T.Routes, ["Name", "StartDepotName", "EndDepotName", "MaxOrders"]) as cursor:
        cursor.insertRow(("Truck", "Depot", "Depot", 10))
    result = lmd.solve()
    U = ap.nax.LastMileDeliveryOutputDataType
    for kind, name in ((U.Routes, "routes"), (U.Orders, "orders")):
        _export(ap, result, kind, O(name))
    return {"solveSucceeded": result.solveSucceeded}


@case("nax.WasteCollection")
def _(ap, I, O):
    import datetime
    wc = _solver(ap, I, "WasteCollection")
    wc.routeStartTime = datetime.time(8)
    wc.maxRouteTotalTime = 480
    wc.stopCollectionMode = ap.nax.StopCollectionMode.BothSides    # Pro fails the solve without one (030092)
    T = ap.nax.WasteCollectionInputDataType
    _insert(ap, I, wc, T.Depots, ["Name"], [("Depot", (300, 300))])
    _insert(ap, I, wc, T.Renewals, ["Name"], [("Landfill", (100, 100))])
    _insert(ap, I, wc, T.Stops, ["Name"], [("S1", (150, 103)), ("S2", (250, 97)), ("S3", (497, 450))])
    with wc.insertCursor(T.Routes, ["Name", "StartDepotName", "EndDepotName"]) as cursor:
        cursor.insertRow(("Truck", "Depot", "Depot"))
    result = wc.solve()
    U = ap.nax.WasteCollectionOutputDataType
    for kind, name in ((U.Routes, "routes"), (U.Stops, "stops")):
        _export(ap, result, kind, O(name))
    return {"solveSucceeded": result.solveSucceeded}


# ---- custom evaluators -----------------------------------------------------------------------------------------------

def _evaluated(ap, I):
    """A route solved with a temporary custom evaluator on Length, recording what it was handed."""
    if "evaluated" in _STATE:
        return _STATE["evaluated"]
    log = {}

    class Seven(ap.nax.AttributeEvaluator):
        def attach(self, network_query):
            attribute = network_query.attribute(self.attributeName)
            log["attachTime"] = self.attribute is None
            log["attribute"] = [type(attribute).__name__, str(attribute), int(attribute)]
            log["attributeName"] = self.attributeName
            log["usage"] = str(network_query.attributeUsage(attribute))
            log["parameters"] = [type(p).__name__ for p in network_query.attributeParameters(attribute)]
            log["at"] = attribute
            log["query"] = network_query
            return True

        def edgeValue(self, edge):
            if "edge" not in log:
                q = log["query"]
                junction = q.fromJunction(edge)
                adjacent = q.edges(junction)
                log["edge"] = {"type": type(edge).__name__, "isEdge": edge.isEdge, "isJunction": edge.isJunction,
                               "isTurn": edge.isTurn, "element": isinstance(edge, ap.nax.NetworkElement),
                               "junction": [type(junction).__name__, junction.isJunction],
                               "adjacent": sorted({type(a).__name__ for a in adjacent}),
                               "adjacentIsEdge": all(isinstance(a, ap.nax.Edge) for a in adjacent),
                               "base": round(q.attributeValue(edge, log["at"]), 4)}
            return 7.0

    net = _net(ap, I)
    n = ap.nax.NetworkDataset(net["nd"])
    n.customEvaluators = [Seven("Length")]
    route = ap.nax.Route(n)
    route.travelMode = ap.nax.GetTravelModes(net["nd"])["Distance"]
    _insert(ap, I, route, ap.nax.RouteInputDataType.Stops, ["Name"], [("a", (100, 100)), ("b", (300, 100))])
    result = route.solve()
    log["totals"] = _r([list(r) for r in result.searchCursor(ap.nax.RouteOutputDataType.Routes,
                                                              ["Total_Kilometers", "Total_Minutes"])])
    log.pop("query", None)
    log.pop("at", None)
    _STATE["evaluated"] = log
    return log


@case("nax.AttributeEvaluator")
def _(ap, I, O):
    log = _evaluated(ap, I)
    return {"totals": log.get("totals"), "attributeName": log.get("attributeName")}


@case("nax.Attribute")
def _(ap, I, O):
    log = _evaluated(ap, I)
    return {"attribute": log.get("attribute"), "usage": log.get("usage")}


@case("nax.AttributeParameter")
def _(ap, I, O):
    return {"parameters": _evaluated(ap, I).get("parameters")}


@case("nax.Edge")
def _(ap, I, O):
    edge = dict(_evaluated(ap, I).get("edge") or {})
    return {k: edge.get(k) for k in ("type", "isEdge", "isJunction", "isTurn", "base")}


@case("nax.NetworkElement")
def _(ap, I, O):
    return {"element": (_evaluated(ap, I).get("edge") or {}).get("element"),
            "subclasses": [issubclass(getattr(ap.nax, n), ap.nax.NetworkElement)
                           for n in ("Edge", "Junction", "Turn", "AdjacentEdge")]}


@case("nax.Junction")
def _(ap, I, O):
    return {"junction": (_evaluated(ap, I).get("edge") or {}).get("junction")}


@case("nax.AdjacentEdge")
def _(ap, I, O):
    edge = _evaluated(ap, I).get("edge") or {}
    return {"adjacent": edge.get("adjacent"), "adjacentIsEdge": edge.get("adjacentIsEdge")}


@case("nax.Turn")
def _(ap, I, O):
    fd, turns, nd = _turn_net(ap, I)
    ap.nax.UpdateByGeometry(turns)
    ap.nax.BuildNetwork(nd)
    n = ap.nax.NetworkDataset(nd)
    log = {}

    class Turns(ap.nax.AttributeEvaluator):
        def attach(self, network_query):
            log["q"] = network_query
            return True

        def turnValue(self, turn):
            q = log["q"]
            seen = {"type": type(turn).__name__, "isTurn": turn.isTurn, "edges": len(list(q.edges(turn))),
                    "atJunction": type(q.atJunction(turn)).__name__}
            if seen not in log.setdefault("turns", []):     # how often Pro calls turnValue is not the point
                log["turns"].append(seen)
            return 0.0

    path = os.path.join(_net(ap, I)["folder"], "turn_mode.json")
    json.dump({"Length": dict(_MODES["Distance"], name="Length", timeAttributeName="")}, open(path, "w"))
    ap.nax.ReplaceNetworkTravelModes(path, nd)
    n.customEvaluators = [Turns("Length")]
    route = ap.nax.Route(n)
    route.travelMode = ap.nax.GetTravelModes(nd)["Length"]
    _insert(ap, I, route, ap.nax.RouteInputDataType.Stops, ["Name"], [("a", (150, 100)), ("b", (200, 150))])
    route.solve()
    return {"turns": log.get("turns"), "count": n.turnCount}


# ---- traversed elements -------------------------------------------------------------------------------------------
# Pro hands TraversedEdge, TraversedJunction and TraversedTurn only to a directions customizer, which grapy refuses
# (Esri's direction rules are not published); what can be compared without one is the classes' hierarchy.

for _name in ("TraversedEdge", "TraversedElement", "TraversedJunction", "TraversedTurn"):
    CASES[f"nax.{_name}"] = (lambda n: lambda ap, I, O: {
        "isClass": isinstance(getattr(ap.nax, n), type),
        "isElement": issubclass(getattr(ap.nax, n), ap.nax.TraversedElement)})(_name)
