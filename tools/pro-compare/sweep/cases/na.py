"""arcpy.na against ArcGIS Pro 3.7: one case for each of the 46 names grapy builds.

Every case solves on a network dataset the case module builds with the documented tools, the same way on both sides:
a street grid feature class in a feature dataset (``streets``: a 5 × 5 grid of 150 m blocks over the fixture's
square, each block side bent at its middle by its own offset so that no two routes cost the same), then
CreateNetworkDataset and BuildNetwork. Pro's CreateNetworkDataset makes one cost attribute, ``Length`` in metres, and
no travel mode (measured); the cases that name a travel mode give the network one with ReplaceNetworkTravelModes.

The fleet routing names (vehicle routing, last mile delivery, waste collection) need a time cost, which neither
CreateNetworkDataset nor any documented tool adds on both sides: Pro takes it from a network dataset template
(CreateTemplateFromNetworkDataset of the base network, a ``Minutes`` attribute added to the XML Pro wrote — its
``Length`` evaluators copied with ``!minutes!`` for ``!Shape!`` — and CreateNetworkDatasetFromTemplate), grapy, whose
template tools are refused, from ``arcpy.graticula.AddNetworkAttribute``. The two networks then hold the same
attributes; this is the one place the sides build an input differently.

Sublayers are compared by copying them to the output geodatabase (CopyFeatures / CopyRows) and the harness dumps the
copy; a property answer is returned as JSON.
"""
import json
import os
import re

CASES = {}
_BUILT = {}


def case(name):
    def register(function):
        CASES[name] = function
        return function
    return register


def _grapy(ap):
    return hasattr(ap, "graticula")


def r(v, n=4):
    return round(float(v), n) if v is not None else None


# ---- the network ----------------------------------------------------------------------------------------------------

STEP = 150.0
NODES = 5


def _bend(k):
    """Block side k's bend: its middle moved sideways by 3 … 25 m, a different amount for each side."""
    return 3.0 + (k * 7) % 23 + 0.37 * (k % 5)


def _streets_rows(I):
    x0, y0 = I["x0"], I["y0"]
    rows, k = [], 0
    for j in range(NODES):                 # east–west sides
        for i in range(NODES - 1):
            k += 1
            ax, ay, bx, by = x0 + i * STEP, y0 + j * STEP, x0 + (i + 1) * STEP, y0 + j * STEP
            mx, my = (ax + bx) / 2, ay + _bend(k) * (1 if k % 2 else -1)
            rows.append((k, round(STEP * (1 + k % 4) / 600, 4), f"LINESTRING ({ax} {ay}, {mx} {my}, {bx} {by})"))
    for i in range(NODES):                 # north–south sides
        for j in range(NODES - 1):
            k += 1
            ax, ay, bx, by = x0 + i * STEP, y0 + j * STEP, x0 + i * STEP, y0 + (j + 1) * STEP
            mx, my = ax + _bend(k) * (1 if k % 2 else -1), (ay + by) / 2
            rows.append((k, round(STEP * (1 + k % 4) / 600, 4), f"LINESTRING ({ax} {ay}, {mx} {my}, {bx} {by})"))
    return rows


def _dataset(ap, I, name="nafd"):
    path = os.path.join(I["gdb"], name)
    if not ap.Exists(path):
        ap.management.CreateFeatureDataset(I["gdb"], name, ap.SpatialReference(I["srid"]))
    return path


def _streets(ap, I, fd):
    """The street grid in a feature dataset, named after it: a file geodatabase holds one feature class of a name."""
    name = os.path.basename(fd) + "_streets"
    path = os.path.join(fd, name)
    sr = ap.SpatialReference(I["srid"])
    ap.management.CreateFeatureclass(fd, name, "POLYLINE", spatial_reference=sr)
    for field, kind in (("alt_id", "LONG"), ("minutes", "DOUBLE")):
        ap.management.AddField(path, field, kind)
    with ap.da.InsertCursor(path, ["alt_id", "minutes", "SHAPE@"]) as cursor:
        for k, minutes, wkt in _streets_rows(I):
            cursor.insertRow((1000 + k, minutes, ap.FromWKT(wkt, sr)))
    return path


def network(ap, I):
    """The base network, built: streets, Length, no travel mode."""
    if "base" in _BUILT:
        return _BUILT["base"]
    fd = _dataset(ap, I)
    streets = _streets(ap, I, fd)
    nd = ap.na.CreateNetworkDataset(fd, "streets_nd", [os.path.basename(streets)], "NO_ELEVATION")[0]
    ap.na.BuildNetwork(nd)
    _BUILT["base"] = str(nd)
    return _BUILT["base"]


MODES = {
    "Driving": {"name": "Driving", "type": "AUTOMOBILE", "description": "", "impedanceAttributeName": "Minutes",
                "timeAttributeName": "Minutes", "distanceAttributeName": "Length", "restrictionAttributeNames": [],
                "attributeParameterValues": [], "useHierarchy": False, "uturnAtJunctions": "esriNFSBAllowBacktrack",
                "simplificationTolerance": None, "simplificationToleranceUnits": "esriMeters"},
    "Walk": {"name": "Walk", "type": "WALK", "description": "", "impedanceAttributeName": "Length",
             "timeAttributeName": "", "distanceAttributeName": "Length", "restrictionAttributeNames": [],
             "attributeParameterValues": [], "useHierarchy": False, "uturnAtJunctions": "esriNFSBAllowBacktrack",
             "simplificationTolerance": None, "simplificationToleranceUnits": "esriMeters"},
}
# On a network with a time cost every travel mode names a time attribute (Pro: ERROR 030403, measured).
MODES["Walk timed"] = dict(MODES["Walk"], timeAttributeName="Minutes")


def _modes_file(I, names):
    path = os.path.join(I["work"], "modes_" + "_".join(names).replace(" ", "") + ".json")
    json.dump({MODES[n]["name"]: MODES[n] for n in names}, open(path, "w", encoding="utf-8"))
    return path


def walk_network(ap, I):
    """The base network's twin with the travel mode Walk (Length), from ReplaceNetworkTravelModes."""
    if "walk" in _BUILT:
        return _BUILT["walk"]
    fd = _dataset(ap, I, "walkfd")
    streets = _streets(ap, I, fd)
    nd = ap.na.CreateNetworkDataset(fd, "walk_nd", [os.path.basename(streets)], "NO_ELEVATION")[0]
    ap.na.BuildNetwork(nd)
    ap.na.ReplaceNetworkTravelModes(_modes_file(I, ["Walk"]), nd)
    _BUILT["walk"] = str(nd)
    return _BUILT["walk"]


def _template_network(ap, I, fd, name, streets, parameter=None):
    """Pro's side of a network with a Minutes cost: a template written by CreateTemplateFromNetworkDataset of the base
    network, its Length attribute and evaluators copied as Minutes (``!minutes!`` for ``!Shape!``) — with, given
    ``parameter``, a double attribute parameter of that name (default 1) on Minutes — then
    CreateNetworkDatasetFromTemplate."""
    base = ap.na.CreateNetworkDataset(fd, name, [streets], "NO_ELEVATION")[0]
    xml = os.path.join(I["work"], name + "_template.xml")
    if os.path.exists(xml):
        os.remove(xml)
    ap.na.CreateTemplateFromNetworkDataset(base, xml)
    text = open(xml, encoding="utf-8").read()
    attr = re.search(r"<EvaluatedNetworkAttribute .*?</EvaluatedNetworkAttribute>", text, re.S).group(0)
    minutes = attr.replace("<ID>1</ID>", "<ID>2</ID>").replace("<Name>Length</Name>", "<Name>Minutes</Name>").replace(
        "<Units>Meters</Units>", "<Units>Minutes</Units>")
    if parameter:
        minutes = re.sub(r"<AttributeParameters [^>]*/>",
                         '<AttributeParameters xsi:type="typens:ArrayOfNetworkAttributeParameter">'
                         '<NetworkAttributeParameter xsi:type="typens:NetworkAttributeParameter">'
                         f"<Name>{parameter}</Name><VarType>5</VarType>"
                         '<Value xsi:type="xs:double">1</Value><DefaultValue xsi:type="xs:double">1</DefaultValue>'
                         "</NetworkAttributeParameter></AttributeParameters>", minutes)
    text = text.replace("</EvaluatedNetworkAttributes>", minutes + "</EvaluatedNetworkAttributes>")
    blocks = [b for b in re.findall(r"<NetworkAssignment .*?</NetworkAssignment>", text, re.S)
              if "<NetworkAttributeName>Length</NetworkAttributeName>" in b]
    text = text.replace("</NetworkAssignments>", "".join(
        b.replace("<NetworkAttributeName>Length</NetworkAttributeName>",
                  "<NetworkAttributeName>Minutes</NetworkAttributeName>").replace("!Shape!", "!minutes!")
        for b in blocks) + "</NetworkAssignments>")
    open(xml, "w", encoding="utf-8").write(text)
    ap.management.Delete(base)
    return ap.na.CreateNetworkDatasetFromTemplate(xml, fd)[0]


def timed_network(ap, I, key="timed", parameter=None):
    """A network with Length and Minutes (from the streets' ``minutes``) and the travel modes Driving and Walk timed;
    given ``parameter``, Minutes has an attribute parameter of that name, default 1."""
    if key in _BUILT:
        return _BUILT[key]
    fd = _dataset(ap, I, key + "fd")
    streets = os.path.basename(_streets(ap, I, fd))
    if _grapy(ap):
        nd = ap.na.CreateNetworkDataset(fd, key + "_nd", [streets], "NO_ELEVATION")[0]
        ap.graticula.AddNetworkAttribute(nd, "Minutes", "Cost", "Minutes", [(streets, "BOTH", "FIELD", "minutes")])
        if parameter:
            ap.graticula.AddNetworkAttributeParameter(nd, "Minutes", parameter, 1.0)
    else:
        nd = _template_network(ap, I, fd, key + "_nd", streets, parameter)
    ap.na.BuildNetwork(nd)
    ap.na.ReplaceNetworkTravelModes(_modes_file(I, ["Driving", "Walk timed"]), nd)
    _BUILT[key] = str(nd)
    return _BUILT[key]


def points(ap, I, name, rows):
    """A point class in the geodatabase: rows are (Name, x, y) with x, y from the fixture's corner."""
    path = os.path.join(I["gdb"], name)
    if ap.Exists(path):
        return path
    sr = ap.SpatialReference(I["srid"])
    ap.management.CreateFeatureclass(I["gdb"], name, "POINT", spatial_reference=sr)
    ap.management.AddField(path, "Name", "TEXT")
    with ap.da.InsertCursor(path, ["Name", "SHAPE@"]) as cursor:
        for label, x, y in rows:
            cursor.insertRow((label, ap.FromWKT(f"POINT ({I['x0'] + x} {I['y0'] + y})", sr)))
    return path


def stops(ap, I):
    return points(ap, I, "na_stops", [("s1", 40, 7), ("s2", 520, 290), ("s3", 310, 590)])


def facilities(ap, I):
    return points(ap, I, "na_facilities", [("f1", 75, 160), ("f2", 455, 445)])


def incidents(ap, I):
    return points(ap, I, "na_incidents", [("i1", 10, 590), ("i2", 590, 10), ("i3", 300, 220), ("i4", 590, 520)])


def copy(ap, layer, sublayer, out):
    """A sublayer copied to ``out`` so the harness dumps it."""
    source = ap.na.GetNASublayer(layer, sublayer)
    if ap.Describe(source).dataType in ("Table", "TableView") or not hasattr(ap.Describe(source), "shapeType"):
        ap.management.CopyRows(source, out)
    else:
        ap.management.CopyFeatures(source, out)
    return out


def _layer(result):
    return result.getOutput(0)


def _names(ap, layer, kind=None):
    names = ap.na.GetNAClassNames(layer, kind) if kind else ap.na.GetNAClassNames(layer)
    return {k: str(v) for k, v in sorted(dict(names).items())}


def _fields(ap, path):
    return [f.name for f in ap.ListFields(path) if f.type not in ("OID", "Geometry")
            and f.name.lower() not in ("shape_length", "shape_area")]


def _mode(mode):
    return {"name": mode.name, "impedance": mode.impedance, "timeAttributeName": mode.timeAttributeName,
            "distanceAttributeName": mode.distanceAttributeName, "restrictions": sorted(mode.restrictions or []),
            "useHierarchy": str(mode.useHierarchy), "uTurns": str(mode.uTurns), "type": str(mode.type),
            "description": mode.description}


def route(ap, I, name, nd=None, mode=None):
    """A solved route through the three stops, in their order."""
    layer = _layer(ap.na.MakeRouteAnalysisLayer(nd or network(ap, I), name, mode,
                                                generate_directions_on_solve="NO_DIRECTIONS"))
    ap.na.AddLocations(layer, "Stops", stops(ap, I))
    ap.na.Solve(layer)
    return layer


# ---- network datasets -----------------------------------------------------------------------------------------------

@case("na.CreateNetworkDataset")
def _(ap, I, O):
    d = ap.Describe(network(ap, I))
    return {"attributes": [[a.name, a.usageType, a.units, bool(a.useByDefault)] for a in d.attributes],
            "sources": [[s.name, s.sourceType] for s in d.sources],
            "modes": sorted(ap.na.GetTravelModes(network(ap, I)))}


@case("na.BuildNetwork")
def _(ap, I, O):
    # Built, the network solves: the route's length is BuildNetwork's evidence.
    layer = route(ap, I, "build_route")
    with ap.da.SearchCursor(ap.na.GetNASublayer(layer, "Routes"), ["Total_Length"]) as rows:
        return [r(row[0], 3) for row in rows]


@case("na.MakeNetworkDatasetLayer")
def _(ap, I, O):
    ap.na.MakeNetworkDatasetLayer(network(ap, I), "nd_layer")
    layer = _layer(ap.na.MakeRouteAnalysisLayer("nd_layer", "ndl_route", generate_directions_on_solve="NO_DIRECTIONS"))
    ap.na.AddLocations(layer, "Stops", stops(ap, I))
    ap.na.Solve(layer)
    copy(ap, layer, "Routes", O("routes"))


@case("na.TravelMode")
def _(ap, I, O):
    walk = ap.na.GetTravelModes(walk_network(ap, I))["Walk"]
    copied = ap.na.TravelMode(walk)
    copied.name = "Walk copy"
    return [_mode(copied), _mode(walk), sorted(json.loads(str(copied)))]


@case("na.GetTravelModes")
def _(ap, I, O):
    modes = ap.na.GetTravelModes(timed_network(ap, I))
    return {name: _mode(m) for name, m in sorted(modes.items())}


@case("na.ExportNetworkTravelModes")
def _(ap, I, O):
    out = os.path.join(I["work"], "exported_modes.json")
    if os.path.exists(out):
        os.remove(out)
    ap.na.ExportNetworkTravelModes(timed_network(ap, I), out)
    text = json.load(open(out, encoding="utf-8"))
    return {"type": type(text).__name__, "keys": sorted(text) if isinstance(text, dict) else len(text)}


@case("na.ReplaceNetworkTravelModes")
def _(ap, I, O):
    nd = walk_network(ap, I)
    return {"modes": sorted(ap.na.GetTravelModes(nd)), "default": ap.Describe(nd).defaultTravelModeName}


@case("na.UpgradeNetwork")
def _(ap, I, O):
    fd = _dataset(ap, I, "upfd")
    streets = _streets(ap, I, fd)
    nd = ap.na.CreateNetworkDataset(fd, "up_nd", [os.path.basename(streets)], "NO_ELEVATION")[0]
    ap.na.BuildNetwork(nd)
    try:
        ap.na.UpgradeNetwork(nd)
        return "upgraded"
    except Exception as error:  # noqa: BLE001 — a network made at the current release is refused (Pro, measured)
        return re.findall(r"ERROR \d+", str(error))


@case("na.CheckIntersectingFeatures")
def _(ap, I, O):
    lay = ap.management.MakeFeatureLayer(I["clip"], "na_clip_layer")[0]
    return [bool(ap.na.CheckIntersectingFeatures(network(ap, I), lay, c)) for c in (5, 10, 1000)]


# ---- analysis layers ------------------------------------------------------------------------------------------------

@case("na.MakeRouteAnalysisLayer")
def _(ap, I, O):
    layer = route(ap, I, "mk_route")
    copy(ap, layer, "Routes", O("routes"))
    copy(ap, layer, "Stops", O("stops"))


@case("na.MakeClosestFacilityAnalysisLayer")
def _(ap, I, O):
    layer = _layer(ap.na.MakeClosestFacilityAnalysisLayer(network(ap, I), "mk_cf", None, "TO_FACILITIES", None, 1,
                                                          generate_directions_on_solve="NO_DIRECTIONS"))
    ap.na.AddLocations(layer, "Facilities", facilities(ap, I))
    ap.na.AddLocations(layer, "Incidents", incidents(ap, I))
    ap.na.Solve(layer)
    copy(ap, layer, "CFRoutes", O("routes"))
    copy(ap, layer, "Incidents", O("incidents"))


@case("na.MakeServiceAreaAnalysisLayer")
def _(ap, I, O):
    layer = _layer(ap.na.MakeServiceAreaAnalysisLayer(network(ap, I), "mk_sa", None, "FROM_FACILITIES", [200, 400],
                                                      output_type="POLYGONS_AND_LINES"))
    ap.na.AddLocations(layer, "Facilities", facilities(ap, I))
    ap.na.Solve(layer)
    copy(ap, layer, "SAPolygons", O("polygons"))
    copy(ap, layer, "SALines", O("lines"))


@case("na.MakeODCostMatrixAnalysisLayer")
def _(ap, I, O):
    layer = _layer(ap.na.MakeODCostMatrixAnalysisLayer(network(ap, I), "mk_od"))
    ap.na.AddLocations(layer, "Origins", facilities(ap, I))
    ap.na.AddLocations(layer, "Destinations", incidents(ap, I))
    ap.na.Solve(layer)
    copy(ap, layer, "ODLines", O("lines"))


@case("na.MakeLocationAllocationAnalysisLayer")
def _(ap, I, O):
    layer = _layer(ap.na.MakeLocationAllocationAnalysisLayer(network(ap, I), "mk_la", None, "FROM_FACILITIES",
                                                             "MINIMIZE_IMPEDANCE", None, 1))
    ap.na.AddLocations(layer, "Facilities", facilities(ap, I))
    ap.na.AddLocations(layer, "Demand Points", incidents(ap, I))
    ap.na.Solve(layer)
    copy(ap, layer, "Facilities", O("facilities"))
    copy(ap, layer, "LALines", O("lines"))


def _vrp(ap, I, name):
    layer = _layer(ap.na.MakeVehicleRoutingProblemAnalysisLayer(timed_network(ap, I), name, "Driving",
                                                                default_date="2026-01-05",
                                                                generate_directions_on_solve="NO_DIRECTIONS"))
    ap.na.AddLocations(layer, "Orders", incidents(ap, I))
    ap.na.AddLocations(layer, "Depots", points(ap, I, "na_depot", [("d1", 300, 300)]))
    ap.na.AddVehicleRoutingProblemRoutes(layer, 2, "Truck", "d1", "d1")
    return layer


@case("na.MakeVehicleRoutingProblemAnalysisLayer")
def _(ap, I, O):
    layer = _vrp(ap, I, "mk_vrp")
    ap.na.Solve(layer)
    copy(ap, layer, "Orders", O("orders"))
    with ap.da.SearchCursor(ap.na.GetNASublayer(layer, "Routes"), ["Name", "OrderCount", "TotalTravelTime",
                                                                   "TotalDistance"]) as rows:
        return sorted([row[0], row[1], r(row[2], 3), r(row[3], 3)] for row in rows)


@case("na.MakeLastMileDeliveryAnalysisLayer")
def _(ap, I, O):
    layer = _layer(ap.na.MakeLastMileDeliveryAnalysisLayer(timed_network(ap, I), "mk_lmd", "Driving",
                                                           earliest_route_start_date="2026-01-05",
                                                           earliest_route_start_time="08:00:00",
                                                           max_route_total_time=480))
    ap.na.AddLocations(layer, "Orders", incidents(ap, I))
    ap.na.AddLocations(layer, "Depots", points(ap, I, "na_depot", [("d1", 300, 300)]))
    ap.na.AddVehicleRoutingProblemRoutes(layer, 1, "Van", "d1", "d1")
    ap.na.Solve(layer)
    copy(ap, layer, "Orders", O("orders"))


@case("na.MakeWasteCollectionAnalysisLayer")
def _(ap, I, O):
    layer = _layer(ap.na.MakeWasteCollectionAnalysisLayer(timed_network(ap, I), "mk_waste", "Driving",
                                                          route_start_time="08:00", max_route_total_time=480,
                                                          stop_collection_mode="BOTH_SIDES"))
    ap.na.AddLocations(layer, "Stops", incidents(ap, I))
    ap.na.AddLocations(layer, "Depots", points(ap, I, "na_depot", [("d1", 300, 300)]))
    ap.na.AddVehicleRoutingProblemRoutes(layer, 1, "Bin", "d1", "d1")
    ap.na.Solve(layer)
    copy(ap, layer, "Stops", O("stops"))


# ---- what an analysis layer answers ---------------------------------------------------------------------------------

@case("na.GetNAClassNames")
def _(ap, I, O):
    out = {}
    for maker, name in (("MakeRouteAnalysisLayer", "cn_route"), ("MakeClosestFacilityAnalysisLayer", "cn_cf"),
                        ("MakeServiceAreaAnalysisLayer", "cn_sa"), ("MakeODCostMatrixAnalysisLayer", "cn_od"),
                        ("MakeLocationAllocationAnalysisLayer", "cn_la")):
        options = {"generate_directions_on_solve": "NO_DIRECTIONS"} if maker == "MakeRouteAnalysisLayer" else {}
        layer = _layer(getattr(ap.na, maker)(network(ap, I), name, **options))
        out[maker] = {"all": _names(ap, layer), "INPUT": _names(ap, layer, "INPUT"),
                      "OUTPUT": _names(ap, layer, "OUTPUT")}
    return out


@case("na.GetNASublayer")
def _(ap, I, O):
    layer = _layer(ap.na.MakeRouteAnalysisLayer(network(ap, I), "sub_route", generate_directions_on_solve="NO_DIRECTIONS"))
    return {k: _fields(ap, ap.na.GetNASublayer(layer, k)) for k in ("Stops", "Routes")}


@case("na.NAClassFieldMappings")
def _(ap, I, O):
    layer = _layer(ap.na.MakeRouteAnalysisLayer(network(ap, I), "fm_route", generate_directions_on_solve="NO_DIRECTIONS"))
    mappings = ap.na.NAClassFieldMappings(layer, "Stops", False, ap.ListFields(stops(ap, I)))
    return {k: mappings[k].mappedFieldName for k in sorted(mappings.keys())}


@case("na.NAClassFieldMap")
def _(ap, I, O):
    layer = _layer(ap.na.MakeRouteAnalysisLayer(network(ap, I), "fmap_route", generate_directions_on_solve="NO_DIRECTIONS"))
    mappings = ap.na.NAClassFieldMappings(layer, "Stops")
    mappings["Name"].defaultValue = "fixed"
    mappings["RouteName"].mappedFieldName = "Name"
    ap.na.AddLocations(layer, "Stops", stops(ap, I), mappings)
    with ap.da.SearchCursor(ap.na.GetNASublayer(layer, "Stops"), ["Name", "RouteName"]) as rows:
        return [list(row) for row in rows]


@case("na.AddFieldToAnalysisLayer")
def _(ap, I, O):
    layer = _layer(ap.na.MakeRouteAnalysisLayer(network(ap, I), "af_route", generate_directions_on_solve="NO_DIRECTIONS"))
    ap.na.AddFieldToAnalysisLayer(layer, "Stops", "Priority", "LONG")
    return [[f.name, f.type] for f in ap.ListFields(ap.na.GetNASublayer(layer, "Stops")) if f.name == "Priority"]


@case("na.AddLocations")
def _(ap, I, O):
    layer = _layer(ap.na.MakeRouteAnalysisLayer(network(ap, I), "al_route", generate_directions_on_solve="NO_DIRECTIONS"))
    ap.na.AddLocations(layer, "Stops", stops(ap, I), search_tolerance="50 Meters")
    ap.na.AddLocations(layer, "Stops", incidents(ap, I), search_tolerance="50 Meters")
    copy(ap, layer, "Stops", O("stops"))


@case("na.Solve")
def _(ap, I, O):
    layer = route(ap, I, "solve_route")
    copy(ap, layer, "Routes", O("routes"))


@case("na.CalculateLocations")
def _(ap, I, O):
    out = ap.management.CopyFeatures(incidents(ap, I), O("out"))[0]
    ap.na.CalculateLocations(out, network(ap, I))


@case("na.DeleteNetworkAnalysisLayer")
def _(ap, I, O):
    layer = _layer(ap.na.MakeRouteAnalysisLayer(network(ap, I), "del_route", generate_directions_on_solve="NO_DIRECTIONS"))
    result = ap.na.DeleteNetworkAnalysisLayer(layer)
    return [str(result.getOutput(0))]


@case("na.CopyNetworkAnalysisLayer")
def _(ap, I, O):
    layer = route(ap, I, "cp_route")
    copied = _layer(ap.na.CopyNetworkAnalysisLayer(layer, "cp_route_copy"))
    copy(ap, copied, "Routes", O("routes"))
    return _names(ap, copied)


@case("na.CopyTraversedSourceFeatures")
def _(ap, I, O):
    layer = route(ap, I, "trav_route")
    ap.na.CopyTraversedSourceFeatures(layer, I["gdb"], "CopyTraversedSourceFeatures_edges",
                                      "CopyTraversedSourceFeatures_junctions", "CopyTraversedSourceFeatures_turns")
    # EID is the network's internal element id, and an analysis class's SourceName is Pro's internal table name
    # (Stops plus a random suffix): neither is compared.
    for name in ("edges", "junctions", "turns"):
        with ap.da.UpdateCursor(O(name), ["EID", "SourceName", "SourceType"]) as rows:
            for eid, source, kind in rows:
                rows.updateRow([-1, "Stops" if kind == "NA_CLASS" and str(source).startswith("Stops") else source,
                                kind])


@case("na.UpdateAnalysisLayerAttributeParameter")
def _(ap, I, O):
    nd = timed_network(ap, I, "param", "Factor")
    layer = _layer(ap.na.MakeRouteAnalysisLayer(nd, "uap_route", "Driving", generate_directions_on_solve="NO_DIRECTIONS"))
    ap.na.UpdateAnalysisLayerAttributeParameter(layer, "Minutes", "Factor", 2)
    values = ap.na.GetSolverProperties(layer).attributeParameters
    return sorted([list(k), r(v, 6)] for k, v in dict(values).items())


# ---- solver properties ----------------------------------------------------------------------------------------------

def _props(sp, names):
    out = {}
    for n in names:
        try:
            v = getattr(sp, n)
        except Exception as error:  # noqa: BLE001 — a property one side does not answer
            v = f"<{type(error).__name__}>"
        if hasattr(v, "name") and hasattr(v, "impedance"):
            v = _mode(v)
        elif isinstance(v, (list, tuple)):
            v = [str(x) for x in v]
        elif v is not None and not isinstance(v, (bool, int, float, str)):
            v = str(v)
        out[n] = v
    return out


_COMMON = ["solverName", "impedance", "accumulators", "restrictions", "uTurns", "useHierarchy", "ignoreInvalidLocations",
           "timeOfDay"]


@case("na.GetSolverProperties")
def _(ap, I, O):
    layer = _layer(ap.na.MakeRouteAnalysisLayer(network(ap, I), "gsp_route", generate_directions_on_solve="NO_DIRECTIONS"))
    sp = ap.na.GetSolverProperties(layer)
    return _props(sp, _COMMON)


@case("na.RouteSolverProperties")
def _(ap, I, O):
    layer = _layer(ap.na.MakeRouteAnalysisLayer(network(ap, I), "rsp", generate_directions_on_solve="NO_DIRECTIONS"))
    sp = ap.na.GetSolverProperties(layer)
    sp.findBestSequence = True
    return _props(sp, _COMMON + ["findBestSequence", "orderingType", "useTimeWindows"])


@case("na.ClosestFacilitySolverProperties")
def _(ap, I, O):
    layer = _layer(ap.na.MakeClosestFacilityAnalysisLayer(network(ap, I), "cfsp"))
    sp = ap.na.GetSolverProperties(layer)
    sp.defaultTargetFacilityCount = 2
    return _props(sp, _COMMON + ["travelDirection", "defaultCutoff", "defaultTargetFacilityCount", "timeOfDayUsage"])


@case("na.ServiceAreaSolverProperties")
def _(ap, I, O):
    layer = _layer(ap.na.MakeServiceAreaAnalysisLayer(network(ap, I), "sasp", None, "FROM_FACILITIES", [100, 300]))
    sp = ap.na.GetSolverProperties(layer)
    return _props(sp, _COMMON + ["travelDirection", "defaultBreaks", "lineType", "polygonType", "polygonMerge",
                                 "polygonNesting", "splitLinesAtBreaks", "trimPolygonDistance"])


@case("na.ODCostMatrixSolverProperties")
def _(ap, I, O):
    layer = _layer(ap.na.MakeODCostMatrixAnalysisLayer(network(ap, I), "odsp", None, 500, 3))
    sp = ap.na.GetSolverProperties(layer)
    return _props(sp, _COMMON + ["defaultCutoff", "defaultTargetDestinationCount", "outputPathShape"])


@case("na.LocationAllocationSolverProperties")
def _(ap, I, O):
    layer = _layer(ap.na.MakeLocationAllocationAnalysisLayer(network(ap, I), "lasp", None, "FROM_FACILITIES",
                                                             "MINIMIZE_IMPEDANCE", 400, 2))
    sp = ap.na.GetSolverProperties(layer)
    return _props(sp, _COMMON + ["travelDirection", "problemType", "defaultCutoff", "facilitiesToFind",
                                 "decayFunctionType", "decayFunctionParameterValue", "outputPathShape"])


@case("na.VehicleRoutingProblemSolverProperties")
def _(ap, I, O):
    layer = _layer(ap.na.MakeVehicleRoutingProblemAnalysisLayer(timed_network(ap, I), "vrpsp", "Driving",
                                                                generate_directions_on_solve="NO_DIRECTIONS"))
    sp = ap.na.GetSolverProperties(layer)
    # Pro's vehicle routing properties answer no impedance, accumulators or time of day (AttributeError, measured).
    return _props(sp, [n for n in _COMMON if n not in ("impedance", "accumulators", "timeOfDay")] +
                  ["timeFieldUnits", "distanceFieldUnits"])


# ---- fleet routing --------------------------------------------------------------------------------------------------

@case("na.AddVehicleRoutingProblemRoutes")
def _(ap, I, O):
    layer = _layer(ap.na.MakeVehicleRoutingProblemAnalysisLayer(timed_network(ap, I), "avr", "Driving",
                                                                default_date="2026-01-05",
                                                                generate_directions_on_solve="NO_DIRECTIONS"))
    ap.na.AddVehicleRoutingProblemRoutes(layer, 3, "Truck", "d1", "d1", capacities="10 5", max_order_count=12)
    copy(ap, layer, "Routes", O("routes"))


@case("na.AddVehicleRoutingProblemBreaks")
def _(ap, I, O):
    layer = _layer(ap.na.MakeVehicleRoutingProblemAnalysisLayer(timed_network(ap, I), "avb", "Driving",
                                                                default_date="2026-01-05",
                                                                generate_directions_on_solve="NO_DIRECTIONS"))
    ap.na.AddVehicleRoutingProblemRoutes(layer, 2, "Truck", "d1", "d1")
    ap.na.AddVehicleRoutingProblemBreaks(layer, None, "MAXIMUM_TRAVEL_TIME_BREAK",
                                         travel_time_properties="true 30 120")
    copy(ap, layer, "Breaks", O("breaks"))


def _waste(ap, I, name):
    layer = _layer(ap.na.MakeWasteCollectionAnalysisLayer(timed_network(ap, I), name, "Driving",
                                                          route_start_time="08:00", max_route_total_time=480,
                                                          stop_collection_mode="BOTH_SIDES"))
    ap.na.AddLocations(layer, "Stops", incidents(ap, I))
    ap.na.AddLocations(layer, "Depots", points(ap, I, "na_depot", [("d1", 300, 300)]))
    ap.na.AddVehicleRoutingProblemRoutes(layer, 2, "Bin", "d1", "d1")
    ap.na.Solve(layer)
    return layer


@case("na.UpdateFleetRoutingResults")
def _(ap, I, O):
    layer = _waste(ap, I, "ufr")
    with ap.da.UpdateCursor(ap.na.GetNASublayer(layer, "Stops"), ["Name", "RouteName"]) as rows:
        for row in rows:
            rows.updateRow([row[0], "Bin1"])
    ap.na.UpdateFleetRoutingResults(layer)
    copy(ap, layer, "Stops", O("stops"))


@case("na.UpdateFleetRoutingAssignments")
def _(ap, I, O):
    layer = _waste(ap, I, "ufa")
    # The selection is the analysis layer's own Stops sublayer's (Pro: ERROR 030399 "No stops are selected" for one
    # made with MakeFeatureLayer over it).
    ap.management.SelectLayerByAttribute(ap.na.GetNASublayer(layer, "Stops"), "NEW_SELECTION", "Name = 'i1'")
    ap.na.UpdateFleetRoutingAssignments(layer, "NOT_ASSIGNED")
    ap.management.SelectLayerByAttribute(ap.na.GetNASublayer(layer, "Stops"), "CLEAR_SELECTION")   # copy every stop
    copy(ap, layer, "Stops", O("stops"))


# ---- turns ----------------------------------------------------------------------------------------------------------

def turn_network(ap, I):
    """A network with a turn source: CreateTurnFeatureClass given the network, then one turn from the first east–west
    side onto the first north–south side at the corner (0, 0)... drawn along them."""
    if "turns" in _BUILT:
        return _BUILT["turns"]
    fd = _dataset(ap, I, "turnfd")
    streets = _streets(ap, I, fd)
    nd = ap.na.CreateNetworkDataset(fd, "turn_nd", [os.path.basename(streets)], "NO_ELEVATION")[0]
    turns = ap.na.CreateTurnFeatureClass(fd, os.path.basename(fd) + "_turns", 2, nd)[0]
    ap.na.BuildNetwork(nd)
    _BUILT["turns"] = (str(nd), str(turns), str(streets))
    return _BUILT["turns"]


def _turn_line(I):
    """A turn drawn along the network from the middle (the bend) of side 2, (150, 0) → (300, 0), through the junction
    (300, 0) to the middle of side 29, (300, 0) → (300, 150)."""
    x0, y0 = I["x0"], I["y0"]
    return (f"LINESTRING ({x0 + 225} {y0 - _bend(2)}, {x0 + 300} {y0}, {x0 + 300 + _bend(29)} {y0 + 75})")


def _insert_turn(ap, I, turns, sr):
    """The turn line inserted in an edit session (Pro edits a turn class that is a network source only in one)."""
    with ap.da.Editor(I["gdb"]):
        with ap.da.InsertCursor(turns, ["SHAPE@"]) as cursor:
            cursor.insertRow((ap.FromWKT(_turn_line(I), sr),))


@case("na.CreateTurnFeatureClass")
def _(ap, I, O):
    out = ap.na.CreateTurnFeatureClass(_dataset(ap, I, "turnonly"), "ctfc_turns", 3)[0]
    ap.management.CopyFeatures(out, O("out"))
    return str(ap.Describe(out).shapeType)


@case("na.IncreaseMaximumEdges")
def _(ap, I, O):
    out = ap.na.CreateTurnFeatureClass(_dataset(ap, I, "turnonly"), "ime_turns", 2)[0]
    ap.na.IncreaseMaximumEdges(out, 4)
    ap.management.CopyFeatures(out, O("out"))


def copy_turns(ap, turns, out):
    """A turn class copied without its Edge#FCID fields: Pro writes there the geodatabase's own object class id of the
    edge source (48, 56 … in the sweep's file geodatabase), which a GeoPackage has no counterpart of."""
    ap.management.CopyFeatures(turns, out)
    fcid = [f.name for f in ap.ListFields(out) if f.name.lower().endswith("fcid")]
    if fcid:
        ap.management.DeleteField(out, fcid)


@case("na.UpdateByGeometry")
def _(ap, I, O):
    nd, turns, streets = turn_network(ap, I)
    sr = ap.SpatialReference(I["srid"])
    _insert_turn(ap, I, turns, sr)
    ap.na.UpdateByGeometry(turns)
    copy_turns(ap, turns, O("out"))


@case("na.PopulateAlternateIDFields")
def _(ap, I, O):
    fd = _dataset(ap, I, "altfd")
    streets = _streets(ap, I, fd)
    nd = ap.na.CreateNetworkDataset(fd, "alt_nd", [os.path.basename(streets)], "NO_ELEVATION")[0]
    turns = ap.na.CreateTurnFeatureClass(fd, os.path.basename(fd) + "_turns", 2, nd)[0]
    ap.na.BuildNetwork(nd)
    sr = ap.SpatialReference(I["srid"])
    _insert_turn(ap, I, turns, sr)
    ap.na.UpdateByGeometry(turns)
    ap.na.PopulateAlternateIDFields(nd, "alt_id")
    copy_turns(ap, turns, O("out"))


@case("na.UpdateByAlternateIDFields")
def _(ap, I, O):
    fd = _dataset(ap, I, "altfd2")
    streets = _streets(ap, I, fd)
    nd = ap.na.CreateNetworkDataset(fd, "alt2_nd", [os.path.basename(streets)], "NO_ELEVATION")[0]
    turns = ap.na.CreateTurnFeatureClass(fd, os.path.basename(fd) + "_turns", 2, nd)[0]
    ap.na.BuildNetwork(nd)
    sr = ap.SpatialReference(I["srid"])
    _insert_turn(ap, I, turns, sr)
    ap.na.UpdateByGeometry(turns)
    ap.na.PopulateAlternateIDFields(nd, "alt_id")
    # The edge references blanked, then found again from AltID1 and AltID2.
    with ap.da.Editor(I["gdb"]):
        with ap.da.UpdateCursor(turns, ["Edge1FID", "Edge2FID"]) as rows:
            for row in rows:
                rows.updateRow([0, 0])
    ap.na.UpdateByAlternateIDFields(nd, "alt_id")
    copy_turns(ap, turns, O("out"))


def write_dbf(path, fields, rows):
    """A dBASE III table written byte by byte: fields are (name, 'N' or 'C', length, decimals)."""
    import struct
    record = 1 + sum(f[2] for f in fields)
    out = bytearray(struct.pack("<BBBBIHH20x", 3, 126, 11, 12, len(rows), 32 + 32 * len(fields) + 1, record))
    for name, kind, length, decimals in fields:
        out += struct.pack("<11sc4xBB14x", name.encode("ascii"), kind.encode("ascii"), length, decimals)
    out += b"\r"
    for row in rows:
        out += b" "
        for (name, kind, length, decimals), value in zip(fields, row):
            text = f"{value:.{decimals}f}" if kind == "N" else str(value)
            out += (text.rjust(length) if kind == "N" else text.ljust(length)).encode("ascii")[:length]
    out += b"\x1a"
    with open(path, "wb") as written:
        written.write(bytes(out))
    return path


@case("na.TurnTableToTurnFeatureClass")
def _(ap, I, O):
    fd = _dataset(ap, I, "ttfd")
    streets = _streets(ap, I, fd)
    nd = ap.na.CreateNetworkDataset(fd, "tt_nd", [os.path.basename(streets)], "NO_ELEVATION")[0]
    ap.na.BuildNetwork(nd)                      # the reference lines must be a network source (the tool's page)
    # An ArcView turn table: NODE_, then the from and to edges (the line features' ids) and a cost; side 2 ends at
    # (300, 0), where side 29 starts, and side 1 ends at (150, 0), where side 2 starts. Pro refuses a table with no
    # NODE_ (ERROR 000591, measured).
    table = write_dbf(os.path.join(I["work"], "turntable.dbf"),
                      [("NODE_", "N", 10, 0), ("ARC1_", "N", 10, 0), ("ARC2_", "N", 10, 0), ("MINUTES", "N", 10, 2)],
                      [(5, 2, 29, 0.5), (2, 1, 2, 0.0)])
    out = ap.na.TurnTableToTurnFeatureClass(table, streets, "tt_turns")[0]
    copy_turns(ap, out, O("out"))
