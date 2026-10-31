"""arcpy.na — network datasets and their analysis layers, by their documented names and parameters (ADR-226).

The network model is ``grapy._network``: grapy's tables in the GeoPackage. An analysis layer (MakeRouteAnalysisLayer and
the rest) is a name held in this process, as a layer is (INFERRED), with its sublayers as tables of a GeoPackage:
``analysis_geodatabase`` when it names one, else ``env.scratchGDB``, each table ``<layer name>/<sublayer>``.
GetNASublayer answers a sublayer's path, which the cursors read.

Solve is an *equivalent* (ADR-224 §5.6, ADR-226 §5.4): Dijkstra's shortest path over the travel mode's impedance —
over the edge-expanded network when there are turns (``grapy._turns``) — and, for the vehicle routing, last mile
delivery and waste collection layers, the published heuristics of ``grapy._fleet`` (ADR-239).
"""
from __future__ import annotations

import json
import os

from . import _gpkg, _network
from ._result import Result
from ._runtime import ExecuteError, NotBuiltError, equivalent

_TEXT, _LONG, _SHORT, _DOUBLE = "String", "Integer", "SmallInteger", "Double"
_LOCATION = [("SourceID", _LONG), ("SourceOID", _LONG), ("PosAlong", _DOUBLE), ("SideOfEdge", _SHORT), ("SnapX", _DOUBLE),
             ("SnapY", _DOUBLE), ("DistanceToNetworkInMeters", _DOUBLE)]


def _given(value):
    return value is not None and str(value).strip() not in ("", "#")


# ---- network datasets ---------------------------------------------------------------------------------------------

def CreateNetworkDataset(feature_dataset, out_name, source_feature_class_names, elevation_model="ELEVATION_FIELDS"):
    """A network dataset in a feature dataset over its line feature classes, to be built with BuildNetwork, as
    documented. INFERRED (the page gives no defaults): sources connect at their end points; a cost ``Length`` in the
    data's linear unit and a travel mode ``Length`` that minimises it. ``ELEVATION_FIELDS`` with no elevation fields
    connects as ``NO_ELEVATION`` does. A turn feature class (one with Edge1End) is a turn source (ADR-239).
    ``Z_COORDINATES`` and point (junction) sources are not built."""
    from .management import _feature_dataset_of, _list
    file, dataset = _gpkg.split(str(feature_dataset))
    if not dataset or _feature_dataset_of(f"{file}/{dataset}/x") is None:
        raise ExecuteError("ERROR 000732: a network dataset is created in a feature dataset (CreateFeatureDataset).")
    model = str(elevation_model or "ELEVATION_FIELDS").upper()
    if model == "Z_COORDINATES":
        raise NotBuiltError("arcpy.na.CreateNetworkDataset: Z_COORDINATES needs Z values, which grapy's shapes do not hold.")
    sources, srid, turns = [], 0, []
    for name in _list(source_feature_class_names):
        table = name if "/" in str(name) else f"{dataset}/{name}"
        connection = _gpkg.connect(file)
        try:
            row = connection.execute("select geometry_type_name, srs_id from gpkg_geometry_columns where table_name = ?",
                                     (table,)).fetchone()
        finally:
            connection.close()
        if row is None:
            raise ExecuteError(f"ERROR 000732: '{name}' is not a feature class of the feature dataset.")
        from . import _turns
        if "LINESTRING" in row[0].upper() and _turns.is_turn_class(f"{file}/{table}"):
            turns.append(f"{file}/{table}")
            continue
        if "LINESTRING" not in row[0].upper():
            raise NotBuiltError(f"arcpy.na.CreateNetworkDataset: '{name}' is not a line class; point (junction) sources "
                                "are not built in grapy.")
        sources.append(table)
        srid = int(row[1] or 0)
    if not sources:
        raise ExecuteError("ERROR 000735: a network dataset needs a line feature class.")
    _network.create(file, dataset, str(out_name), sources, model, srid)
    out = str(feature_dataset).rstrip("/\\") + "/" + str(out_name)
    for turn in turns:
        _turns.add_turn_source(out, turn)
    return Result(out)


def BuildNetwork(in_network_dataset, force_full_build="NO_FORCE_FULL_BUILD"):
    """The network's edges and junctions built from its sources (``grapy._network.build``); grapy always builds the whole
    network, which answers both settings of ``force_full_build``."""
    _network.build(str(in_network_dataset))
    return Result(str(in_network_dataset))


def MakeNetworkDatasetLayer(in_network_dataset, out_network_dataset_layer):
    """A name for a network dataset, held in this process, usable wherever the dataset's path is."""
    _network.locate(str(in_network_dataset))
    _network.LAYERS[str(out_network_dataset_layer)] = str(_network.LAYERS.get(str(in_network_dataset), in_network_dataset))
    return Result(str(out_network_dataset_layer))


class TravelMode:
    """arcpy.na.TravelMode: a travel mode's settings, from a network's travel mode, another TravelMode, or its JSON."""

    def __init__(self, travel_mode_object=None):
        source = travel_mode_object
        if isinstance(source, TravelMode):
            source = json.loads(str(source))
        elif isinstance(source, str):
            source = json.loads(source)
        source = dict(source or {})
        self.name = source.get("name", "")
        self.impedance = source.get("impedance") or source.get("impedanceAttributeName")
        self.timeAttributeName = source.get("timeAttributeName")
        self.distanceAttributeName = source.get("distanceAttributeName")
        self.restrictions = list(source.get("restrictions") or source.get("restrictionAttributeNames") or [])
        self.attributeParameters = dict(source.get("attributeParameters") or {})
        self.uTurns = source.get("uTurns", "ALLOW_UTURNS")
        self.useHierarchy = bool(source.get("useHierarchy", False))
        self.simplificationTolerance = source.get("simplificationTolerance")
        self.type = source.get("type", "OTHER")
        self.description = source.get("description", "")

    def __str__(self):
        return json.dumps({"name": self.name, "impedanceAttributeName": self.impedance,
                           "timeAttributeName": self.timeAttributeName, "distanceAttributeName": self.distanceAttributeName,
                           "restrictionAttributeNames": self.restrictions, "attributeParameters": self.attributeParameters,
                           "uTurns": self.uTurns, "useHierarchy": self.useHierarchy,
                           "simplificationTolerance": self.simplificationTolerance, "type": self.type,
                           "description": self.description})


def GetTravelModes(network_dataset):
    """The network's travel modes, by name, as TravelMode objects."""
    return {name: TravelMode(mode) for name, mode in _network.travel_modes(str(network_dataset)).items()}


def _refuse_mode(mode):
    """A travel mode grapy honours: no hierarchy, U-turns allowed everywhere, no parameters, no simplification."""
    if mode is None or isinstance(mode, str) and not mode.strip().startswith("{"):
        return
    travel = TravelMode(mode)
    if travel.useHierarchy:
        raise NotBuiltError("Hierarchy is refused until it is defined (ADR-226 §5.3).")
    from ._turns import policy
    policy(travel.uTurns)                     # every documented U-turn policy is honoured at junctions (ADR-239)
    if travel.attributeParameters:
        raise NotBuiltError("Travel mode attribute parameters are not built in grapy: no evaluator takes parameters.")


# ---- analysis layers ----------------------------------------------------------------------------------------------

class _AnalysisLayer:
    def __init__(self, name, solver, network, travel_mode, settings, workspace, classes):
        self.name = self.longName = name
        self.solver, self.network, self.travel_mode = solver, network, travel_mode
        self.settings, self.workspace, self.classes = settings, workspace, classes
        self.isNetworkAnalystLayer = True
        self.isNetworkDatasetLayer = False

    def __str__(self):
        return self.name


ANALYSIS_LAYERS: dict = {}

_SCHEMAS = {
    "Route": {
        "Stops": ("Point", [("Name", _TEXT), ("RouteName", _TEXT), ("Sequence", _LONG), ("CurbApproach", _SHORT),
                            ("LocationType", _SHORT), *_LOCATION, ("Status", _SHORT)]),
        "Routes": ("Polyline", [("Name", _TEXT), ("FirstStopID", _LONG), ("LastStopID", _LONG), ("StopCount", _LONG)]),
        "PointBarriers": ("Point", [("Name", _TEXT)]), "PolylineBarriers": ("Polyline", [("Name", _TEXT)]),
        "PolygonBarriers": ("Polygon", [("Name", _TEXT)]),
    },
    "Closest Facility": {
        "Facilities": ("Point", [("Name", _TEXT), ("CurbApproach", _SHORT), *_LOCATION, ("Status", _SHORT)]),
        "Incidents": ("Point", [("Name", _TEXT), ("TargetFacilityCount", _LONG), ("CurbApproach", _SHORT), *_LOCATION,
                                ("Status", _SHORT)]),
        "CFRoutes": ("Polyline", [("Name", _TEXT), ("FacilityID", _LONG), ("FacilityRank", _LONG), ("IncidentID", _LONG)]),
        "PointBarriers": ("Point", [("Name", _TEXT)]), "PolylineBarriers": ("Polyline", [("Name", _TEXT)]),
        "PolygonBarriers": ("Polygon", [("Name", _TEXT)]),
    },
    "Service Area": {
        "Facilities": ("Point", [("Name", _TEXT), ("CurbApproach", _SHORT), *_LOCATION, ("Status", _SHORT)]),
        "SAPolygons": ("Polygon", [("Name", _TEXT), ("FacilityID", _LONG), ("FromBreak", _DOUBLE), ("ToBreak", _DOUBLE)]),
        "SALines": ("Polyline", [("FacilityID", _LONG), ("SourceID", _LONG), ("SourceOID", _LONG), ("FromPosition", _DOUBLE),
                                 ("ToPosition", _DOUBLE)]),
        "PointBarriers": ("Point", [("Name", _TEXT)]), "PolylineBarriers": ("Polyline", [("Name", _TEXT)]),
        "PolygonBarriers": ("Polygon", [("Name", _TEXT)]),
    },
    "Location-Allocation": {
        "Facilities": ("Point", [("Name", _TEXT), ("FacilityType", _SHORT), ("Weight", _DOUBLE), ("Capacity", _DOUBLE),
                                 ("CurbApproach", _SHORT), *_LOCATION, ("Status", _SHORT), ("DemandCount", _LONG),
                                 ("DemandWeight", _DOUBLE)]),
        "DemandPoints": ("Point", [("Name", _TEXT), ("GroupName", _TEXT), ("Weight", _DOUBLE), ("CurbApproach", _SHORT),
                                   *_LOCATION, ("Status", _SHORT), ("FacilityID", _LONG), ("AllocatedWeight", _DOUBLE)]),
        "LALines": ("Polyline", [("Name", _TEXT), ("FacilityID", _LONG), ("DemandID", _LONG), ("Weight", _DOUBLE)]),
        "PointBarriers": ("Point", [("Name", _TEXT)]), "PolylineBarriers": ("Polyline", [("Name", _TEXT)]),
        "PolygonBarriers": ("Polygon", [("Name", _TEXT)]),
    },
    "OD Cost Matrix": {
        "Origins": ("Point", [("Name", _TEXT), ("TargetDestinationCount", _LONG), ("CurbApproach", _SHORT), *_LOCATION,
                              ("Status", _SHORT)]),
        "Destinations": ("Point", [("Name", _TEXT), ("CurbApproach", _SHORT), *_LOCATION, ("Status", _SHORT)]),
        "ODLines": ("Polyline", [("Name", _TEXT), ("OriginID", _LONG), ("DestinationID", _LONG), ("DestinationRank", _LONG)]),
        "PointBarriers": ("Point", [("Name", _TEXT)]), "PolylineBarriers": ("Polyline", [("Name", _TEXT)]),
        "PolygonBarriers": ("Polygon", [("Name", _TEXT)]),
    },
}
# The sublayer names the layers show (GetNAClassNames' values). INFERRED for the barrier and output names.
_SUBLAYER_NAMES = {"Stops": "Stops", "Routes": "Routes", "Facilities": "Facilities", "Incidents": "Incidents",
                   "CFRoutes": "Routes", "SAPolygons": "Polygons", "SALines": "Lines", "Origins": "Origins",
                   "Destinations": "Destinations", "ODLines": "Lines",
                   "DemandPoints": "Demand Points", "LALines": "Lines", "PointBarriers": "Point Barriers",
                   "PolylineBarriers": "Line Barriers", "PolygonBarriers": "Polygon Barriers"}
_INPUTS = {"Stops", "Facilities", "Incidents", "Origins", "Destinations", "DemandPoints"}


def _make(solver, network_data_source, layer_name, travel_mode, settings, analysis_geodatabase, cost_fields):
    from ._runtime import env
    source = str(network_data_source)
    if source.lower().startswith(("http://", "https://")):
        raise NotBuiltError("A routing service as network_data_source reaches a portal, and a fenced tool has no network "
                            "(ADR-223 §5.3).")
    _refuse_mode(travel_mode)
    graph = _network.Graph(source, travel_mode)
    name = str(layer_name) if _given(layer_name) else solver
    workspace = str(analysis_geodatabase) if _given(analysis_geodatabase) else env.scratchGDB
    if not workspace.lower().endswith(".gpkg"):
        raise NotBuiltError("An analysis geodatabase other than a GeoPackage is not built in grapy.")
    if not os.path.exists(workspace):
        from .management import CreateSQLiteDatabase
        CreateSQLiteDatabase(workspace, "GEOPACKAGE")
    classes = {}
    for key, (shape, fields) in _SCHEMAS[solver].items():
        extra = cost_fields(key, graph)
        path = f"{workspace}/{name}/{key}"
        _gpkg.create(path, shape, graph.srid, [_gpkg.Column(f, t) for f, t in fields + extra])
        classes[key] = path
    ANALYSIS_LAYERS[name] = _AnalysisLayer(name, solver, source, travel_mode, settings, workspace, classes)
    return Result(ANALYSIS_LAYERS[name])


def _accumulate(value):
    from .management import _list
    return [str(a) for a in _list(value)] if _given(value) else []


def _common_refusals(tool, time_of_day=None, generate_directions=None, sequence=None):
    if _given(time_of_day):
        raise NotBuiltError(f"arcpy.na.{tool}: time_of_day needs traffic, refused until it is defined (ADR-226 §5.3).")
    if _given(generate_directions) and str(generate_directions).upper() in ("DIRECTIONS", "TRUE"):
        raise NotBuiltError(f"arcpy.na.{tool}: directions are written by Esri's undocumented direction rules; pass "
                            "NO_DIRECTIONS.")


def MakeRouteAnalysisLayer(network_data_source, layer_name=None, travel_mode=None, sequence="USE_CURRENT_ORDER",
                           time_of_day=None, time_zone="LOCAL_TIME_AT_LOCATIONS", line_shape="ALONG_NETWORK",
                           accumulate_attributes=None, generate_directions_on_solve="DIRECTIONS",
                           time_zone_for_time_fields="LOCAL_TIME_AT_LOCATIONS", ignore_invalid_locations="SKIP",
                           analysis_geodatabase=None, analysis_feature_dataset_name=None):
    """A route analysis layer: Stops and Routes, with the documented fields. ``FIND_BEST_ORDER`` and the ``PRESERVE_``
    orders solve the travelling salesman problem (``grapy._network.best_order``, ADR-239); time of day is refused
    (traffic); directions are refused, so ``generate_directions_on_solve``, whose default is ``DIRECTIONS``, must be
    ``NO_DIRECTIONS``. INFERRED: ``line_shape`` defaults to ``ALONG_NETWORK``."""
    if str(sequence or "USE_CURRENT_ORDER").upper() not in ("USE_CURRENT_ORDER", "FIND_BEST_ORDER", "PRESERVE_BOTH",
                                                            "PRESERVE_FIRST", "PRESERVE_LAST"):
        raise ExecuteError(f"ERROR 030000: '{sequence}' is not a route sequence.")
    _common_refusals("MakeRouteAnalysisLayer", time_of_day, generate_directions_on_solve)
    accumulate = _accumulate(accumulate_attributes)

    def costs(key, graph):
        if key == "Stops":
            return [(f"Cumul_{graph.impedance}", _DOUBLE)]
        if key == "Routes":
            return [(f"Total_{a}", _DOUBLE) for a in [graph.impedance, *[x for x in accumulate if x != graph.impedance]]]
        return []

    return _make("Route", network_data_source, layer_name, travel_mode,
                 {"line_shape": line_shape, "accumulate": accumulate, "ignore": ignore_invalid_locations,
                  "sequence": str(sequence or "USE_CURRENT_ORDER").upper()},
                 analysis_geodatabase, costs)


def MakeClosestFacilityAnalysisLayer(network_data_source, layer_name=None, travel_mode=None, travel_direction="TO_FACILITIES",
                                     cutoff=None, number_of_facilities_to_find=1, time_of_day=None,
                                     time_zone="LOCAL_TIME_AT_LOCATIONS", time_of_day_usage="START_TIME",
                                     line_shape="ALONG_NETWORK", accumulate_attributes=None,
                                     generate_directions_on_solve="NO_DIRECTIONS", ignore_invalid_locations="SKIP",
                                     analysis_geodatabase=None, analysis_feature_dataset_name=None):
    """A closest facility analysis layer: Facilities, Incidents and Routes, with the documented fields and defaults
    (toward the facilities, one facility, no cutoff). INFERRED: ``line_shape`` defaults to ``ALONG_NETWORK``."""
    _common_refusals("MakeClosestFacilityAnalysisLayer", time_of_day, generate_directions_on_solve)
    accumulate = _accumulate(accumulate_attributes)

    def costs(key, graph):
        if key == "CFRoutes":
            return [(f"Total_{a}", _DOUBLE) for a in [graph.impedance, *[x for x in accumulate if x != graph.impedance]]]
        return []

    return _make("Closest Facility", network_data_source, layer_name, travel_mode,
                 {"direction": travel_direction, "cutoff": float(cutoff) if _given(cutoff) else None,
                  "count": int(number_of_facilities_to_find or 1), "line_shape": line_shape, "accumulate": accumulate,
                  "ignore": ignore_invalid_locations}, analysis_geodatabase, costs)


def MakeServiceAreaAnalysisLayer(network_data_source, layer_name=None, travel_mode=None, travel_direction="FROM_FACILITIES",
                                 cutoffs=None, time_of_day=None, time_zone="LOCAL_TIME_AT_LOCATIONS", output_type="POLYGONS",
                                 polygon_detail="STANDARD", geometry_at_overlaps="OVERLAP", geometry_at_cutoffs="RINGS",
                                 polygon_trim_distance="100 Meters", exclude_sources_from_polygon_generation=None,
                                 accumulate_attributes=None, ignore_invalid_locations="SKIP", analysis_geodatabase=None,
                                 analysis_feature_dataset_name=None):
    """A service area analysis layer: Facilities, Polygons and Lines, with the documented fields and defaults. Polygons
    are an equivalent (``grapy._network.solve_service_area``). INFERRED: with no cutoffs, 5, 10 and 15, the dialog's.
    ``GENERALIZED`` and ``HIGH`` detail, ``DISSOLVE`` and ``SPLIT`` overlaps, excluded sources, accumulated attributes
    and lines toward the facilities are not built."""
    from .management import _list
    _common_refusals("MakeServiceAreaAnalysisLayer", time_of_day)
    for name, value, allowed in (("polygon_detail", polygon_detail, "STANDARD"), ("geometry_at_overlaps", geometry_at_overlaps, "OVERLAP")):
        if _given(value) and str(value).upper() != allowed:
            raise NotBuiltError(f"arcpy.na.MakeServiceAreaAnalysisLayer: {name} '{value}' is not built in grapy.")
    if _given(exclude_sources_from_polygon_generation) or _given(accumulate_attributes):
        raise NotBuiltError("arcpy.na.MakeServiceAreaAnalysisLayer: excluded sources and accumulated attributes are not "
                            "built in grapy.")
    kind = str(output_type or "POLYGONS").upper()
    if kind != "POLYGONS" and str(travel_direction or "FROM_FACILITIES").upper() == "TO_FACILITIES":
        raise NotBuiltError("arcpy.na.MakeServiceAreaAnalysisLayer: lines toward the facilities are not built in grapy.")
    breaks = [float(c) for c in _list(cutoffs)] if _given(cutoffs) else [5.0, 10.0, 15.0]

    def costs(key, graph):
        if key == "SALines":
            return [(f"FromCumul_{graph.impedance}", _DOUBLE), (f"ToCumul_{graph.impedance}", _DOUBLE)]
        return []

    return _make("Service Area", network_data_source, layer_name, travel_mode,
                 {"direction": travel_direction, "breaks": breaks, "output": kind,
                  "rings": str(geometry_at_cutoffs or "RINGS").upper() == "RINGS", "trim": polygon_trim_distance,
                  "ignore": ignore_invalid_locations}, analysis_geodatabase, costs)


def MakeODCostMatrixAnalysisLayer(network_data_source, layer_name=None, travel_mode=None, cutoff=None,
                                  number_of_destinations_to_find=None, time_of_day=None, time_zone="LOCAL_TIME_AT_LOCATIONS",
                                  line_shape="STRAIGHT_LINES", accumulate_attributes=None, ignore_invalid_locations="SKIP",
                                  analysis_geodatabase=None, analysis_feature_dataset_name=None):
    """An OD cost matrix analysis layer: Origins, Destinations and Lines, with the documented fields and defaults
    (no cutoff, every destination, straight lines)."""
    _common_refusals("MakeODCostMatrixAnalysisLayer", time_of_day)
    accumulate = _accumulate(accumulate_attributes)

    def costs(key, graph):
        if key == "ODLines":
            return [(f"Total_{a}", _DOUBLE) for a in [graph.impedance, *[x for x in accumulate if x != graph.impedance]]]
        return []

    return _make("OD Cost Matrix", network_data_source, layer_name, travel_mode,
                 {"cutoff": float(cutoff) if _given(cutoff) else None,
                  "count": int(number_of_destinations_to_find) if _given(number_of_destinations_to_find) else None,
                  "line_shape": line_shape, "accumulate": accumulate, "ignore": ignore_invalid_locations},
                 analysis_geodatabase, costs)


def MakeLocationAllocationAnalysisLayer(network_data_source, layer_name=None, travel_mode=None,
                                        travel_direction="FROM_FACILITIES", problem_type="MINIMIZE_IMPEDANCE", cutoff=None,
                                        number_of_facilities_to_find=1, decay_function_type="LINEAR",
                                        decay_function_parameter_value=None, target_market_share=None, capacity=None,
                                        time_of_day=None, time_zone="LOCAL_TIME_AT_LOCATIONS", line_shape="STRAIGHT_LINES",
                                        accumulate_attributes=None, ignore_invalid_locations="SKIP",
                                        analysis_geodatabase=None, analysis_feature_dataset_name=None):
    """A location-allocation analysis layer: Facilities, Demand Points and Lines, with the documented fields and
    defaults (away from the facilities, minimize impedance, one facility, no cutoff, linear decay, straight lines).
    Solve chooses the facilities as nax.LocationAllocation does, an equivalent: Teitz and Bart's vertex substitution
    (``grapy.nax._allocate``). Only ``MINIMIZE_IMPEDANCE`` and ``LINEAR`` are built; the other problem types are
    refused. INFERRED: the Lines sublayer's class name, ``LALines``."""
    if str(problem_type or "MINIMIZE_IMPEDANCE").upper() != "MINIMIZE_IMPEDANCE":
        raise NotBuiltError(f"arcpy.na.MakeLocationAllocationAnalysisLayer: problem_type '{problem_type}' is not built in "
                            "grapy; MINIMIZE_IMPEDANCE is.")
    if str(decay_function_type or "LINEAR").upper() != "LINEAR":
        raise NotBuiltError(f"arcpy.na.MakeLocationAllocationAnalysisLayer: decay_function_type '{decay_function_type}' "
                            "is not built in grapy; LINEAR is.")
    _common_refusals("MakeLocationAllocationAnalysisLayer", time_of_day)
    accumulate = _accumulate(accumulate_attributes)

    def costs(key, graph):
        names = [graph.impedance, *[x for x in accumulate if x != graph.impedance]]
        if key in ("Facilities", "LALines"):
            return [(f"{p}_{a}", _DOUBLE) for p in ("Total", "TotalWeighted") for a in names]
        return []

    return _make("Location-Allocation", network_data_source, layer_name, travel_mode,
                 {"direction": travel_direction, "cutoff": float(cutoff) if _given(cutoff) else None,
                  "count": int(number_of_facilities_to_find or 1), "line_shape": line_shape, "accumulate": accumulate,
                  "ignore": ignore_invalid_locations}, analysis_geodatabase, costs)


def _solve_location_allocation(graph, layer, inputs):
    """Solve's location-allocation branch: lines written, the facilities' and demand points' outputs updated."""
    from shapely.geometry import LineString
    from .nax import _allocate
    from ._runtime import AddWarning, _SAID_EQUIVALENT
    if "na.Solve location-allocation" not in _SAID_EQUIVALENT:
        _SAID_EQUIVALENT.add("na.Solve location-allocation")
        AddWarning("arcpy.na.Solve chooses location-allocation facilities by an equivalent, not ArcGIS Pro's own "
                   "computation: Teitz-Bart vertex substitution for the p-median problem (Teitz and Bart 1968).")
    s = layer.settings
    facilities, demands = inputs["Facilities"], inputs["DemandPoints"]
    away = str(s["direction"] or "FROM_FACILITIES").upper() == "FROM_FACILITIES"
    solver, chosen, required, weights, served = _allocate(graph, facilities, demands, away, s["cutoff"], s["count"],
                                                          "arcpy.na.Solve")
    names = [graph.impedance, *[x for x in s["accumulate"] if x != graph.impedance]]
    lines, by_facility, demand_updates = [], {}, {}
    for d, demand in enumerate(demands):
        if d not in served:
            demand_updates[demand["oid"]] = {"FacilityID": None, "AllocatedWeight": None}
            continue
        f, path = served[d]
        facility = facilities[f]
        totals = {a: solver.total(path, a) for a in names}
        by_facility.setdefault(f, []).append((weights[d], totals))
        demand_updates[demand["oid"]] = {"FacilityID": facility["oid"], "AllocatedWeight": weights[d]}
        line = {"Name": f"{facility['name']} - {demand['name']}" if away else f"{demand['name']} - {facility['name']}",
                "FacilityID": facility["oid"], "DemandID": demand["oid"], "Weight": weights[d]}
        line.update({f"Total_{a}": v for a, v in totals.items()})
        line.update({f"TotalWeighted_{a}": v * weights[d] for a, v in totals.items()})
        if str(s["line_shape"] or "STRAIGHT_LINES").upper() != "NO_LINES":
            ends = [facility["point"], demand["point"]] if away else [demand["point"], facility["point"]]
            line["SHAPE"] = LineString([(p.x, p.y) for p in ends])
        lines.append(line)
    facility_updates = {}
    for f, facility in enumerate(facilities):
        allocated = by_facility.get(f, [])
        row = {"FacilityType": 3 if f in chosen and f not in required else int(facility.get("FacilityType") or 0),
               "DemandCount": len(allocated), "DemandWeight": sum(w for w, _ in allocated)}
        for a in names:
            row[f"Total_{a}"] = sum(t[a] for _, t in allocated)
            row[f"TotalWeighted_{a}"] = sum(w * t[a] for w, t in allocated)
        facility_updates[facility["oid"]] = row
    _write(layer.classes["LALines"], lines)
    _update(layer.classes["Facilities"], facility_updates)
    _update(layer.classes["DemandPoints"], demand_updates)
    return bool(lines)


def _layer(value):
    layer = ANALYSIS_LAYERS.get(str(value))
    if layer is None:
        raise ExecuteError(f"ERROR 000732: '{value}' is not a network analysis layer.")
    return layer


def GetNAClassNames(network_analyst_layer, network_analyst_class_type=None):
    """The layer's network analysis classes, by class name, with the sublayer each is shown as; ``INPUT`` or
    ``OUTPUT`` keeps only those. INFERRED: barrier and output sublayer names as ``_SUBLAYER_NAMES`` gives them."""
    layer = _layer(network_analyst_layer)
    kind = str(network_analyst_class_type or "").upper()
    out = {}
    for key in layer.classes:
        is_input = key in _input_keys(layer) or key.endswith("Barriers")
        if kind == "INPUT" and not is_input or kind == "OUTPUT" and is_input:
            continue
        out[key] = _SUBLAYER_NAMES[key]
    return out


def GetNASublayer(network_analyst_layer, network_analyst_class):
    """The path of a sublayer's table, by class name or sublayer name."""
    layer = _layer(network_analyst_layer)
    for key, path in layer.classes.items():
        if str(network_analyst_class) in (key, _SUBLAYER_NAMES[key]):
            return path
    raise ExecuteError(f"ERROR 000732: the layer has no sublayer '{network_analyst_class}'.")


class NAClassFieldMap:
    """How one property of a sublayer is filled: from an input field (``mappedFieldName``) or a constant
    (``defaultValue``)."""

    def __init__(self, mappedFieldName=None, defaultValue=None):
        self.mappedFieldName = mappedFieldName
        self.defaultValue = defaultValue


class NAClassFieldMappings(dict):
    """The field maps of a sublayer, by property name: each property mapped by default to the candidate field of the same
    name. Network location fields (``use_location_fields``) are not built: grapy always locates."""

    def __init__(self, network_analyst_layer, sub_layer_name, use_location_fields=False, list_candidate_fields=None):
        super().__init__()
        if use_location_fields:
            raise NotBuiltError("arcpy.na.NAClassFieldMappings: loading network location fields is not built in grapy; "
                                "grapy locates every input.")
        layer = _layer(network_analyst_layer)
        key = next(k for k in layer.classes if sub_layer_name in (k, _SUBLAYER_NAMES[k]))
        candidates = {getattr(f, "name", str(f)).lower(): getattr(f, "name", str(f)) for f in (list_candidate_fields or [])}
        for name, _ in _SCHEMAS[layer.solver][key][1]:
            if name in dict(_LOCATION) or name == "Status":
                continue
            self[name] = NAClassFieldMap(candidates.get(name.lower()))


def AddFieldToAnalysisLayer(in_network_analysis_layer, sub_layer, field_name, field_type, field_precision=None,
                            field_scale=None, field_length=None, field_alias=None, field_is_nullable="NULLABLE"):
    """A field added to a sublayer of an analysis layer."""
    from .management import AddField
    AddField(GetNASublayer(in_network_analysis_layer, sub_layer), field_name, field_type)
    return Result(str(in_network_analysis_layer))


def _point_rows(in_table, sort_field):
    from . import da
    from .management import _schema, _source
    path, where = _source(in_table)
    columns, kind, srid, _ = _schema(path)
    if kind not in ("Point",):
        raise NotBuiltError("Locating line or polygon inputs is not built in grapy; give point features.")
    names = [c.name for c in columns]
    order = f'order by "{sort_field}"' if _given(sort_field) else None
    with da.SearchCursor(path, ["OID@", *names, "SHAPE@"], where, sql_clause=(None, order)) as rows:
        return srid, [(r[0], dict(zip(names, r[1:-1])), r[-1]) for r in rows]


def AddLocations(in_network_analysis_layer, sub_layer, in_table, field_mappings=None, search_tolerance=None, sort_field=None,
                 search_criteria=None, match_type=None, append="APPEND", snap_to_position_along_network="NO_SNAP",
                 snap_offset=None, exclude_restricted_elements=None, search_query=None, allow_auto_relocate="ALLOW"):
    """Input features added to a sublayer and located on the network: each point on the closest point of the nearest
    traversable edge within the search tolerance, as documented, writing the network location fields and ``Status``
    (0 located, 1 not). Fields are mapped by name, or by NAClassFieldMappings. INFERRED: the default search tolerance
    is 5000 meters, as nax's solvers document; a stop with no name is "Location <n>". Barriers, snapping, search
    criteria and search queries are not built."""
    from . import da
    from .management import _linear
    layer = _layer(in_network_analysis_layer)
    path = GetNASublayer(layer, sub_layer)
    key = next(k for k, p in layer.classes.items() if p == path)
    if key.endswith("Barriers"):
        raise NotBuiltError("arcpy.na.AddLocations: barriers are not built in grapy yet.")
    if key not in _input_keys(layer):
        raise ExecuteError(f"ERROR 000800: '{sub_layer}' is an output sublayer.")
    if layer.solver in _FLEET_INPUTS and key not in _FLEET_LOCATED:
        return _add_rows(layer, path, in_table, field_mappings, sort_field, append)       # ADR-239
    for name, value in (("search_criteria", search_criteria), ("search_query", search_query)):
        if _given(value):
            raise NotBuiltError(f"arcpy.na.AddLocations: {name} is not built in grapy; every source is searched.")
    if str(snap_to_position_along_network or "NO_SNAP").upper() in ("SNAP", "TRUE"):
        raise NotBuiltError("arcpy.na.AddLocations: SNAP is not built in grapy; the input's shape is kept.")
    graph = _network.Graph(layer.network, layer.travel_mode)
    srid, rows = _point_rows(in_table, sort_field)
    tolerance = _linear(search_tolerance, graph.srid, "AddLocations") if _given(search_tolerance) else \
        5000.0 / (_network._per_unit(graph.srid) or 1.0)
    if str(append or "APPEND").upper() in ("CLEAR", "FALSE"):
        from .management import DeleteRows
        DeleteRows(path)
    from .management import _schema
    fields = [c.name for c in _schema(path)[0]]
    mapping = field_mappings if isinstance(field_mappings, dict) else {}
    with da.SearchCursor(path, ["OID@"]) as existing:
        count = sum(1 for _ in existing)
    with da.InsertCursor(path, [*fields, "SHAPE@"]) as written:
        for oid, values, shape in rows:
            if shape is not None and srid != graph.srid and srid > 0 and graph.srid > 0:
                from ._reference import SpatialReference
                shape = shape.projectAs(SpatialReference(graph.srid))
            point = shape._shape if shape is not None else None
            located = graph.locate(point, tolerance) if point is not None else None
            location = graph.location_fields(located, point)
            lower = {k.lower(): v for k, v in values.items()}
            record = {}
            for field in fields:
                if field in mapping:
                    m = mapping[field]
                    record[field] = lower.get(str(m.mappedFieldName).lower()) if m.mappedFieldName else m.defaultValue
                else:
                    record[field] = lower.get(field.lower())
            count += 1
            if not record.get("Name"):
                record["Name"] = f"Location {count}"
            record.update({k: v for k, v in location.items() if not k.startswith("_")})
            record["Status"] = 0 if located is not None else 1
            written.insertRow((*[record.get(f) for f in fields], point))
    return Result(str(layer))


def _add_rows(layer, path, in_table, field_mappings, sort_field, append):
    """A fleet layer's table or zone class filled from a table or feature class, fields mapped by name (ADR-239)."""
    from . import da
    from .management import _schema, _source
    source, where = _source(in_table)
    columns, kind, srid, _ = _schema(source)
    names = [c.name for c in columns]
    order = f'order by "{sort_field}"' if _given(sort_field) else None
    has_shape = kind not in (None, "Geometry", "Table") and srid is not None
    tokens = ["SHAPE@"] if has_shape else []
    with da.SearchCursor(source, [*names, *tokens], where, sql_clause=(None, order)) as cursor:
        rows = [list(r) for r in cursor]
    _clear_unless(path, append)
    fields = [c.name for c in _schema(path)[0]]
    mapping = field_mappings if isinstance(field_mappings, dict) else {}
    out = []
    for row in rows:
        lower = {n.lower(): v for n, v in zip(names, row)}
        record = {}
        for field in fields:
            m = mapping.get(field)
            record[field] = (lower.get(str(m.mappedFieldName).lower()) if m.mappedFieldName else m.defaultValue) \
                if m is not None else lower.get(field.lower())
        if has_shape and row[-1] is not None:
            shape = row[-1]
            if srid and srid > 0 and srid != _network.Graph(layer.network, layer.travel_mode).srid:
                from ._reference import SpatialReference
                shape = shape.projectAs(SpatialReference(_network.Graph(layer.network, layer.travel_mode).srid))
            record["SHAPE"] = shape._shape
        out.append(record)
    _append(path, out)
    return Result(str(layer))


def _items(layer, key, graph):
    """The located rows of an input sublayer, for the solvers."""
    from . import da
    from .management import _schema
    path = layer.classes[key]
    fields = [c.name for c in _schema(path)[0]]
    items = []
    with da.SearchCursor(path, ["OID@", *fields, "SHAPE@"]) as rows:
        for row in rows:
            values = dict(zip(fields, row[1:-1]))
            point = row[-1]._shape if row[-1] is not None else None
            for field in ("CurbApproach", "LocationType"):
                if values.get(field) not in (None, 0) and not (field == "CurbApproach" and values[field] == 3 and
                                                               layer.solver in ("Closest Facility", "OD Cost Matrix")):
                    raise NotBuiltError(f"arcpy.na.Solve: {field} {values[field]} is not built in grapy: its solvers "
                                        "approach every location from either side and take no waypoints.")
            located = None
            if values.get("SourceOID") is not None and point is not None:
                located = graph.locate(point, float("inf"))
            items.append({"key": (key, row[0]), "oid": row[0], "name": values.get("Name") or f"Location {row[0]}",
                          "point": point, "located": located, **values})
    return items


def _write(path, rows, extra_fields=()):
    from . import da
    from .management import DeleteRows, _schema
    DeleteRows(path)
    fields = [c.name for c in _schema(path)[0]]
    with da.InsertCursor(path, [*fields, "SHAPE@"]) as written:
        for row in rows:
            written.insertRow((*[row.get(f) for f in fields], row.get("SHAPE")))


def _update(path, updates):
    from . import da
    fields = sorted({f for u in updates.values() for f in u})
    if not fields:
        return
    with da.UpdateCursor(path, ["OID@", *fields]) as rows:
        for row in rows:
            change = updates.get(row[0])
            if change:
                rows.updateRow([row[0], *[change.get(f, v) for f, v in zip(fields, row[1:])]])


@equivalent(*_network.DIJKSTRA)
def Solve(in_network_analysis_layer, ignore_invalids=None, terminate_on_solve_error="TERMINATE",
          simplification_tolerance=None, overrides=None):
    """EQUIVALENT: Dijkstra's shortest path over the travel mode's impedance (Dijkstra 1959), as ADR-226 §5.4 decides —
    on a network whose shortest paths are unique the costs are those the documentation defines; where two paths cost
    the same, Pro may choose the other. The outputs are written to the layer's output sublayers, and the inputs' Status,
    and a route's stops' Sequence and Cumul_, updated. ``HALT`` fails on an unlocated input. A simplification tolerance
    is not built. The outputs are the layer and ``true`` or ``false``."""
    from .management import _linear
    layer = _layer(in_network_analysis_layer)
    if _given(simplification_tolerance):
        raise NotBuiltError("arcpy.na.Solve: simplification_tolerance is not built in grapy.")
    graph = _network.Graph(layer.network, layer.travel_mode)
    if layer.solver in _FLEET_INPUTS:                                                     # ADR-239
        try:
            succeeded = _fleet_solve(layer, graph)
        except ExecuteError:
            if str(terminate_on_solve_error or "TERMINATE").upper() == "TERMINATE":
                raise
            succeeded = False
        if not succeeded and str(terminate_on_solve_error or "TERMINATE").upper() == "TERMINATE":
            raise ExecuteError("ERROR 030024: Solve returned a failure: no route was given an order.")
        return Result(layer, "true" if succeeded else "false")
    halt = str(ignore_invalids or layer.settings.get("ignore") or "SKIP").upper() in ("HALT", "FALSE")
    inputs = {key: _items(layer, key, graph) for key in layer.classes if key in _INPUTS}
    if halt and any(item["located"] is None for items in inputs.values() for item in items):
        raise ExecuteError("ERROR 030024: an input is not located on the network, and invalid locations halt the solve.")
    s = layer.settings
    try:
        if layer.solver == "Route":
            routes, results = _network.solve_route(graph, inputs["Stops"], s["accumulate"], s["line_shape"],
                                                   s.get("sequence", "USE_CURRENT_ORDER"))
            _write(layer.classes["Routes"], routes)
            _update(layer.classes["Stops"], {k[1]: {f: x for f, x in v.items() if not f.startswith("_")}
                                             for k, v in results.items()})
            succeeded = bool(routes)
        elif layer.solver == "Closest Facility":
            routes = _network.solve_closest_facility(graph, inputs["Facilities"], inputs["Incidents"], s["direction"],
                                                     s["cutoff"], s["count"], s["accumulate"], s["line_shape"])
            _write(layer.classes["CFRoutes"], routes)
            succeeded = bool(routes)
        elif layer.solver == "OD Cost Matrix":
            lines = _network.solve_od_cost_matrix(graph, inputs["Origins"], inputs["Destinations"], s["cutoff"], s["count"],
                                                  s["accumulate"], s["line_shape"])
            _write(layer.classes["ODLines"], lines)
            succeeded = bool(lines)
        elif layer.solver == "Location-Allocation":
            succeeded = _solve_location_allocation(graph, layer, inputs)
        else:
            trim = _linear(s["trim"], graph.srid, "Solve") if _given(s["trim"]) else 0.0
            lines, polygons = _network.solve_service_area(graph, inputs["Facilities"], s["breaks"], s["direction"],
                                                          s["rings"], trim)
            _write(layer.classes["SALines"], lines if s["output"] in ("LINES", "POLYGONS_AND_LINES") else [])
            _write(layer.classes["SAPolygons"], polygons if s["output"] in ("POLYGONS", "POLYGONS_AND_LINES") else [])
            succeeded = bool(polygons)
    except ExecuteError:
        if str(terminate_on_solve_error or "TERMINATE").upper() == "TERMINATE":
            raise
        succeeded = False
    if not succeeded and str(terminate_on_solve_error or "TERMINATE").upper() == "TERMINATE":
        raise ExecuteError("ERROR 030024: Solve returned a failure: no solution was found.")
    return Result(layer, "true" if succeeded else "false")


# ---- more network tools --------------------------------------------------------------------------------------------

def CalculateLocations(in_point_features, in_network_dataset=None, search_tolerance="5000 Meters", search_criteria=None,
                       match_type=None, source_ID_field="SourceID", source_OID_field="SourceOID", position_field="PosAlong",
                       side_field="SideOfEdge", snap_X_field="SnapX", snap_Y_field="SnapY",
                       distance_field="DistanceToNetworkInMeters", snap_Z_field="SnapZ", location_field="Locations",
                       exclude_restricted_elements=None, search_query=None, travel_mode=None):
    """Each point's network location written to its own fields — SourceID, SourceOID, PosAlong, SideOfEdge, SnapX,
    SnapY and DistanceToNetworkInMeters by default, as documented — and ``Status`` (0 located, 1 not) when the input has
    that Long field; within 5000 meters by default, as documented. Fields not there are added (INFERRED). SnapZ is not
    written: grapy's networks are 2D, as the page writes it only for z connectivity. Line and polygon inputs, search
    criteria and queries are not built."""
    from . import da
    from .management import AddField, _linear, _schema, _source
    for name, value in (("search_criteria", search_criteria), ("search_query", search_query)):
        if _given(value):
            raise NotBuiltError(f"arcpy.na.CalculateLocations: {name} is not built in grapy; every source is searched.")
    path, where = _source(in_point_features)
    columns, kind, srid, _ = _schema(path)
    if kind != "Point":
        raise NotBuiltError("arcpy.na.CalculateLocations: line and polygon inputs are not built in grapy.")
    graph = _network.Graph(str(in_network_dataset), travel_mode)
    tolerance = _linear(search_tolerance, graph.srid, "CalculateLocations") if _given(search_tolerance) else \
        5000.0 / (_network._per_unit(graph.srid) or 1.0)
    targets = [("SourceID", source_ID_field, "LONG"), ("SourceOID", source_OID_field, "LONG"),
               ("PosAlong", position_field, "DOUBLE"), ("SideOfEdge", side_field, "LONG"),
               ("SnapX", snap_X_field, "DOUBLE"), ("SnapY", snap_Y_field, "DOUBLE"),
               ("DistanceToNetworkInMeters", distance_field, "DOUBLE")]
    have = {c.name.lower(): c for c in columns}
    for _, field, kind_word in targets:
        if str(field).lower() not in have:
            AddField(path, str(field), kind_word)
    status = "status" in have and have["status"].type in ("Integer", "SmallInteger")
    fields = [str(f) for _, f, _ in targets] + (["Status"] if status else [])
    with da.UpdateCursor(path, [*fields, "SHAPE@"], where) as rows:
        for row in rows:
            point = row[-1]._shape if row[-1] is not None else None
            located = graph.locate(point, tolerance) if point is not None else None
            location = graph.location_fields(located, point)
            values = [location[name] for name, _, _ in targets] + ([0 if located else 1] if status else [])
            rows.updateRow([*values, row[-1]])
    return Result(str(in_point_features))


def DeleteNetworkAnalysisLayer(in_network_analysis_layers):
    """Each analysis layer and its sublayers' tables deleted; the network itself stays, as documented."""
    from .management import Delete, _list
    for each in _list(in_network_analysis_layers) if not isinstance(in_network_analysis_layers, _AnalysisLayer) \
            else [in_network_analysis_layers]:
        layer = _layer(each)
        for path in layer.classes.values():
            Delete(path)
        del ANALYSIS_LAYERS[layer.name]
    return Result("true")


def CopyNetworkAnalysisLayer(in_network_analysis_layer, out_layer_name=None):
    """A copy of an analysis layer: its sublayers' tables copied, its settings and network kept, as documented — into
    ``env.scratchGDB`` when the current workspace is no GeoPackage (INFERRED: grapy's analysis data lives in
    GeoPackages), else the original's. INFERRED: with no name, the copy is "<name> 1", "<name> 2" ….
    """
    from .management import Copy
    layer = _layer(in_network_analysis_layer)
    name = str(out_layer_name) if _given(out_layer_name) else next(
        f"{layer.name} {n}" for n in range(1, 10000) if f"{layer.name} {n}" not in ANALYSIS_LAYERS)
    classes = {}
    for key, path in layer.classes.items():
        target = f"{layer.workspace}/{name}/{key}"
        Copy(path, target)
        classes[key] = target
    ANALYSIS_LAYERS[name] = _AnalysisLayer(name, layer.solver, layer.network, layer.travel_mode, dict(layer.settings),
                                           layer.workspace, classes)
    return Result(ANALYSIS_LAYERS[name])


def ExportNetworkTravelModes(in_network_dataset, out_travel_mode_file):
    """The network's travel modes written to a .json file: an object of travel mode name → its properties, the default
    first, as documented. INFERRED: a travel mode's properties as TravelMode's JSON writes them."""
    modes = GetTravelModes(in_network_dataset)
    with open(str(out_travel_mode_file), "w", encoding="utf-8") as written:
        json.dump({name: json.loads(str(mode)) for name, mode in modes.items()}, written, indent=2)
    return Result(str(out_travel_mode_file))


def ReplaceNetworkTravelModes(in_travel_mode_file, in_network_dataset):
    """The network's travel modes replaced by a .json file's, as documented. INFERRED: every existing travel mode is
    removed first; the file's first is then the default; a mode's impedance and restrictions must be attributes of the
    network."""
    with open(str(in_travel_mode_file), encoding="utf-8") as read:
        modes = json.load(read)
    connection, _, _, name = _network.open_network(str(in_network_dataset))
    try:
        connection.execute("delete from grapy_network_travel_mode where network = ?", (name,))
        connection.commit()
    finally:
        connection.close()
    for mode_name, properties in modes.items():
        mode = TravelMode(properties)
        _refuse_mode(properties)
        _network.add_travel_mode(str(in_network_dataset), mode_name, mode.impedance, mode.restrictions,
                                 mode.timeAttributeName, mode.distanceAttributeName, mode.uTurns)
    return Result(str(in_network_dataset))


def UpgradeNetwork(in_network_dataset):
    """A network dataset's schema brought to the current release. grapy's network tables have one schema, so a network
    made by grapy is current already and nothing changes; it must be built, as the page's sample builds it after."""
    _network.open_network(str(in_network_dataset))[0].close()
    return Result(str(in_network_dataset))


# ---- fleet routing: vehicle routing, last mile delivery and waste collection (ADR-239) -------------------------------

_DATE, _TABLE = "Date", "Table"
_NINE = range(1, 10)
_WINDOW = [("TimeWindowStart", _DATE), ("TimeWindowEnd", _DATE)]
_FLEET_LOCATION = [*_LOCATION, ("CurbApproach", _SHORT)]
_VISIT_OUT = [("FromPrevTravelTime", _DOUBLE), ("FromPrevDistance", _DOUBLE), ("CumulTravelTime", _DOUBLE),
              ("CumulDistance", _DOUBLE), ("CumulTime", _DOUBLE), ("ArriveTime", _DATE), ("DepartTime", _DATE),
              ("ArriveTimeUTC", _DATE), ("DepartTimeUTC", _DATE), ("WaitTime", _DOUBLE), ("CumulWaitTime", _DOUBLE),
              ("CumulViolationTime", _DOUBLE)]
_VIOLATED = [(f"ViolatedConstraint_{k}", _LONG) for k in range(1, 5)]
_BARRIERS = {"PointBarriers": ("Point", [("Name", _TEXT)]), "PolylineBarriers": ("Polyline", [("Name", _TEXT)]),
             "PolygonBarriers": ("Polygon", [("Name", _TEXT)])}
_ROUTE_TOTALS = [*_VIOLATED, ("OrderCount", _LONG), ("TotalCost", _DOUBLE), ("RegularTimeCost", _DOUBLE),
                 ("OvertimeCost", _DOUBLE), ("DistanceCost", _DOUBLE), ("TotalTime", _DOUBLE),
                 ("TotalOrderServiceTime", _DOUBLE), ("TotalTravelTime", _DOUBLE), ("TotalDistance", _DOUBLE),
                 ("StartTime", _DATE), ("EndTime", _DATE), ("TotalWaitTime", _DOUBLE), ("TotalViolationTime", _DOUBLE)]
_ROUTE_COSTS = [("ArriveDepartDelay", _DOUBLE), *[(f"Capacity_{k}", _DOUBLE) for k in _NINE], ("FixedCost", _DOUBLE),
                ("CostPerUnitTime", _DOUBLE), ("CostPerUnitDistance", _DOUBLE), ("OvertimeStartTime", _DOUBLE),
                ("CostPerUnitOvertime", _DOUBLE), ("MaxOrderCount", _LONG), ("MaxTotalTime", _DOUBLE),
                ("MaxTotalTravelTime", _DOUBLE), ("MaxTotalDistance", _DOUBLE)]
_QUANTITIES = [*[(f"DeliveryQuantity_{k}", _DOUBLE) for k in _NINE], *[(f"PickupQuantity_{k}", _DOUBLE) for k in _NINE]]
_WASTE_MEASURES = [(m, _DOUBLE) for m in ("Bins_1", "Weight_1", "Volume_1", "Bins_2", "Weight_2", "Volume_2")]

_SCHEMAS["Vehicle Routing Problem"] = {
    "Orders": ("Point", [("Name", _TEXT), ("Description", _TEXT), ("ServiceTime", _DOUBLE), *_WINDOW,
                         ("TimeWindowStart2", _DATE), ("TimeWindowEnd2", _DATE), ("MaxViolationTime", _DOUBLE),
                         ("MaxViolationTime2", _DOUBLE), ("InboundArriveTime", _DATE), ("OutboundDepartTime", _DATE),
                         *_QUANTITIES, ("Revenue", _DOUBLE), ("AssignmentRule", _LONG), *_FLEET_LOCATION,
                         ("RouteName", _TEXT), ("Sequence", _LONG), ("Status", _SHORT), *_VIOLATED, *_VISIT_OUT,
                         ("ArriveCurbApproach", _SHORT), ("DepartCurbApproach", _SHORT), ("ViolationTime", _DOUBLE)]),
    "Depots": ("Point", [("Name", _TEXT), ("Description", _TEXT), *_WINDOW, ("TimeWindowStart2", _DATE),
                         ("TimeWindowEnd2", _DATE), *_FLEET_LOCATION, ("Status", _SHORT)]),
    "Routes": ("Polyline", [("Name", _TEXT), ("Description", _TEXT), ("StartDepotName", _TEXT), ("EndDepotName", _TEXT),
                            ("StartDepotServiceTime", _DOUBLE), ("EndDepotServiceTime", _DOUBLE),
                            ("EarliestStartTime", _DATE), ("LatestStartTime", _DATE), *_ROUTE_COSTS,
                            ("AssignmentRule", _LONG), *_ROUTE_TOTALS, ("TotalBreakServiceTime", _DOUBLE),
                            ("StartTimeUTC", _DATE), ("EndTimeUTC", _DATE), ("RenewalCount", _LONG),
                            ("TotalRenewalServiceTime", _DOUBLE)]),
    "Breaks": ("Point", [("RouteName", _TEXT), ("Precedence", _LONG), ("ServiceTime", _DOUBLE), *_WINDOW,
                         ("MaxViolationTime", _DOUBLE), ("MaxTravelTimeBetweenBreaks", _DOUBLE),
                         ("MaxCumulWorkTime", _DOUBLE), ("IsPaid", _SHORT), ("Sequence", _LONG),
                         ("RelativePosition", _DOUBLE), *_VISIT_OUT, ("ViolationTime", _DOUBLE)]),
    "DepotVisits": ("Point", [("DepotName", _TEXT), ("RouteName", _TEXT), ("Sequence", _LONG), ("VisitType", _SHORT),
                              ("ServiceTime", _DOUBLE), *_VISIT_OUT,
                              *[(f"LoadedQuantity_{k}", _DOUBLE) for k in _NINE],
                              *[(f"UnloadedQuantity_{k}", _DOUBLE) for k in _NINE]]),
    "RouteRenewals": (_TABLE, [("DepotName", _TEXT), ("RouteName", _TEXT), ("ServiceTime", _DOUBLE),
                               ("Sequences", _TEXT)]),
    "RouteZones": ("Polygon", [("RouteName", _TEXT), ("IsHardZone", _SHORT)]),
    "OrderPairs": (_TABLE, [("FirstOrderName", _TEXT), ("SecondOrderName", _TEXT), ("MaxTransitTime", _DOUBLE)]),
    "OrderSpecialties": (_TABLE, [("OrderName", _TEXT), ("SpecialtyName", _TEXT)]),
    "RouteSpecialties": (_TABLE, [("RouteName", _TEXT), ("SpecialtyName", _TEXT)]),
    **_BARRIERS,
}
_SCHEMAS["Last Mile Delivery"] = {
    "Orders": ("Point", [("Name", _TEXT), ("ServiceTime", _DOUBLE), *_WINDOW, ("MaxViolationTime", _DOUBLE),
                         ("InboundArriveTime", _DATE), ("OutboundDepartTime", _DATE), *_QUANTITIES,
                         ("AnchorRule", _SHORT), ("AssignmentRule", _SHORT), ("RouteName", _TEXT), ("Sequence", _LONG),
                         *_FLEET_LOCATION, ("Status", _SHORT), *_VIOLATED, ("FromPrevTravelTime", _DOUBLE),
                         ("FromPrevDistance", _DOUBLE), ("ArriveCurbApproach", _SHORT), ("DepartCurbApproach", _SHORT),
                         ("ArriveTime", _DATE), ("DepartTime", _DATE), ("WaitTime", _DOUBLE),
                         ("ViolationTime", _DOUBLE)]),
    "Depots": ("Point", [("Name", _TEXT), *_FLEET_LOCATION, ("Status", _SHORT)]),
    "Routes": ("Polyline", [("Name", _TEXT), ("StartDepotName", _TEXT), ("EndDepotName", _TEXT),
                            ("StartDepotServiceTime", _DOUBLE), ("EndDepotServiceTime", _DOUBLE),
                            ("EarliestStartDate", _DATE), ("EarliestStartTime", _TEXT), ("StartFlexibility", _DOUBLE),
                            *_ROUTE_COSTS, ("ZoneName", _TEXT), ("IsHardZone", _SHORT), ("AssignmentRule", _SHORT),
                            *_ROUTE_TOTALS]),
    "DepotVisits": ("Point", [("DepotName", _TEXT), ("VisitType", _SHORT), ("RouteName", _TEXT), ("Sequence", _LONG),
                              ("ServiceTime", _DOUBLE), ("FromPrevTravelTime", _DOUBLE), ("FromPrevDistance", _DOUBLE),
                              ("ArriveTime", _DATE), ("DepartTime", _DATE),
                              *[(f"LoadedQuantity_{k}", _DOUBLE) for k in _NINE],
                              *[(f"UnloadedQuantity_{k}", _DOUBLE) for k in _NINE]]),
    "Zones": ("Polygon", [("Name", _TEXT)]),
    "OrderSpecialties": (_TABLE, [("OrderName", _TEXT), ("SpecialtyName", _TEXT)]),
    "RouteSpecialties": (_TABLE, [("RouteName", _TEXT), ("SpecialtyName", _TEXT)]),
    **_BARRIERS,
}
_SCHEMAS["Waste Collection"] = {
    "Stops": ("Point", [("Name", _TEXT), ("ServiceTime", _DOUBLE), *_WASTE_MEASURES, ("CollectionMode", _SHORT),
                        ("AnchorRule", _SHORT), ("AssignmentRule", _SHORT), ("RouteName", _TEXT), ("Sequence", _LONG),
                        *_LOCATION, ("Status", _SHORT), ("RouteLineID", _LONG), *_VIOLATED]),
    "Depots": ("Point", [("Name", _TEXT), *_LOCATION, ("Status", _SHORT)]),
    "Routes": ("Polyline", [("Name", _TEXT), ("StartDepotName", _TEXT), ("EndDepotName", _TEXT),
                            ("StartDepotServiceTime", _DOUBLE), ("EndDepotServiceTime", _DOUBLE), ("StartTime", _TEXT),
                            *[(f"Max{m[0]}", _DOUBLE) for m in _WASTE_MEASURES], ("OvertimeStartTime", _DOUBLE),
                            ("MaxTotalTime", _DOUBLE), ("MaxTotalDistance", _DOUBLE), ("AssignmentRule", _SHORT),
                            *_VIOLATED, ("StopCount", _LONG), ("RenewalCount", _LONG),
                            *[(f"Total{m[0]}", _DOUBLE) for m in _WASTE_MEASURES], ("TotalTime", _DOUBLE),
                            ("TotalTravelTime", _DOUBLE), ("TotalStopServiceTime", _DOUBLE), ("TotalDistance", _DOUBLE),
                            ("EndTime", _TEXT)]),
    "Renewals": ("Point", [("Name", _TEXT), ("Fraction", _SHORT), *_LOCATION, ("Status", _SHORT)]),
    "RouteRenewals": (_TABLE, [("RouteName", _TEXT), ("RenewalName", _TEXT), ("ServiceTime", _DOUBLE)]),
    "RenewalVisits": ("Point", [("RouteName", _TEXT), ("RenewalName", _TEXT), ("Sequence", _LONG), ("ArriveTime", _TEXT),
                                ("DepartTime", _TEXT),
                                *[(f"Unloaded{m[0]}", _DOUBLE) for m in _WASTE_MEASURES]]),
    "RouteLines": ("Polyline", [("RouteName", _TEXT), ("CollectionSequence", _LONG), ("Activity", _SHORT),
                                ("ArriveTime", _TEXT), ("DepartTime", _TEXT), ("StopCount", _LONG), *_WASTE_MEASURES,
                                ("ReportGrouping", _LONG)]),
    **_BARRIERS,
}
_SUBLAYER_NAMES.update({"Orders": "Orders", "Depots": "Depots", "Breaks": "Breaks", "DepotVisits": "Depot Visits",
                        "RouteRenewals": "Route Renewals", "RouteZones": "Route Zones", "OrderPairs": "Order Pairs",
                        "OrderSpecialties": "Order Specialties", "RouteSpecialties": "Route Specialties",
                        "Zones": "Zones", "Renewals": "Renewals", "RenewalVisits": "Renewal Visits",
                        "RouteLines": "Route Lines"})
_FLEET_INPUTS = {
    "Vehicle Routing Problem": {"Orders", "Depots", "Routes", "Breaks", "RouteRenewals", "RouteZones", "OrderPairs",
                                "OrderSpecialties", "RouteSpecialties"},
    "Last Mile Delivery": {"Orders", "Depots", "Routes", "Zones", "OrderSpecialties", "RouteSpecialties"},
    "Waste Collection": {"Stops", "Depots", "Routes", "Renewals", "RouteRenewals", "RenewalVisits"},
}
_FLEET_LOCATED = {"Orders", "Depots", "Stops", "Renewals"}


def _input_keys(layer):
    """The input classes of an analysis layer: a fleet layer's from ADR-239, the others' from ``_INPUTS``."""
    return _FLEET_INPUTS.get(layer.solver, _INPUTS)


def _time_impedance(tool, graph):
    units = graph.attributes[graph.impedance][1]
    if str(units or "").upper() not in ("SECONDS", "MINUTES", "HOURS", "DAYS"):
        raise ExecuteError(f"ERROR 030000: arcpy.na.{tool} needs a travel mode whose impedance is in time units.")


def _fleet_make(solver, tool, network_data_source, layer_name, travel_mode, settings, analysis_geodatabase,
                generate_directions="NO_DIRECTIONS"):
    _common_refusals(tool, None, generate_directions)
    _refuse_mode(travel_mode)
    _time_impedance(tool, _network.Graph(str(_network.LAYERS.get(str(network_data_source), network_data_source)),
                                         travel_mode))
    return _make(solver, network_data_source, layer_name, travel_mode, settings, analysis_geodatabase,
                 lambda key, graph: [])


def MakeVehicleRoutingProblemAnalysisLayer(network_data_source, layer_name=None, travel_mode=None, time_units="Minutes",
                                           distance_units="Miles", default_date=None,
                                           time_zone_for_time_fields="LOCAL_TIME_AT_LOCATIONS",
                                           line_shape="ALONG_NETWORK", time_window_factor="Medium",
                                           excess_transit_factor="Medium", generate_directions_on_solve="DIRECTIONS",
                                           spatial_clustering="CLUSTER", ignore_invalid_locations="HALT",
                                           analysis_geodatabase=None, analysis_feature_dataset_name=None):
    """A vehicle routing problem analysis layer with the documented sublayers and fields. Solve is an equivalent
    (``grapy._fleet``): Solomon's insertion, then ruin and recreate. Directions are refused, so
    ``generate_directions_on_solve``, whose default is ``DIRECTIONS``, must be ``NO_DIRECTIONS``. INFERRED: with no
    default date, today; ``spatial_clustering`` is accepted and has no term in grapy's objective; with no time zone
    attribute UTC fields are null and ``UTC`` reads times as given."""
    return _fleet_make("Vehicle Routing Problem", "MakeVehicleRoutingProblemAnalysisLayer", network_data_source,
                       layer_name, travel_mode,
                       {"time_units": time_units, "distance_units": distance_units, "default_date": default_date,
                        "line_shape": line_shape, "time_window_factor": time_window_factor,
                        "excess_transit_factor": excess_transit_factor,
                        "ignore": ignore_invalid_locations}, analysis_geodatabase, generate_directions_on_solve)


def MakeLastMileDeliveryAnalysisLayer(network_data_source, layer_name=None, travel_mode=None, time_units="Minutes",
                                      distance_units="Miles", earliest_route_start_date=None,
                                      earliest_route_start_time=None, max_route_total_time=None,
                                      time_zone_for_time_fields="LOCAL_TIME_AT_LOCATIONS", sequence_gap=1,
                                      ignore_invalid_order_locations="HALT", line_shape="STRAIGHT_LINES",
                                      generate_directions_on_solve="NO_DIRECTIONS", analysis_geodatabase=None,
                                      analysis_feature_dataset_name=None):
    """A last mile delivery analysis layer with the documented sublayers and fields; solved as the vehicle routing
    problem is (``grapy._fleet``), with the last mile delivery's inputs: one time window, anchoring, start flexibility,
    zones by name, delivery or pickup orders but not both."""
    if int(sequence_gap or 1) < 1:
        raise ExecuteError("ERROR 030000: sequence_gap is a positive integer.")
    return _fleet_make("Last Mile Delivery", "MakeLastMileDeliveryAnalysisLayer", network_data_source, layer_name,
                       travel_mode,
                       {"time_units": time_units, "distance_units": distance_units,
                        "earliest_date": earliest_route_start_date, "earliest_time": earliest_route_start_time,
                        "max_route_total_time": max_route_total_time, "sequence_gap": int(sequence_gap or 1),
                        "line_shape": line_shape, "ignore": ignore_invalid_order_locations},
                       analysis_geodatabase, generate_directions_on_solve)


def MakeWasteCollectionAnalysisLayer(network_data_source, layer_name=None, travel_mode=None, time_units="Minutes",
                                     distance_units="Miles", route_start_time=None, max_route_total_time=None,
                                     stop_collection_mode=None, analysis_geodatabase=None,
                                     analysis_feature_dataset_name=None):
    """A waste collection analysis layer with the documented sublayers and fields; Solve routes the collection units
    as ``grapy._fleet.Waste`` does. INFERRED: with no stop collection mode, both sides at once."""
    mode = str(stop_collection_mode or "").upper()
    if mode not in ("", "#", "ONE_SIDE", "BOTH_SIDES"):
        raise ExecuteError(f"ERROR 030000: '{stop_collection_mode}' is not ONE_SIDE or BOTH_SIDES.")
    return _fleet_make("Waste Collection", "MakeWasteCollectionAnalysisLayer", network_data_source, layer_name,
                       travel_mode,
                       {"time_units": time_units, "distance_units": distance_units, "route_start_time": route_start_time,
                        "max_route_total_time": max_route_total_time,
                        "stop_collection_mode": mode if mode not in ("", "#") else None, "ignore": "SKIP"},
                       analysis_geodatabase)


def _value_rows(value):
    """A value table's rows: a ValueTable, its text (rows by semicolons), a list of rows, or one flat row."""
    from ._parameter import _split_rows, _tokens
    if value is None or isinstance(value, str) and value.strip() in ("", "#"):
        return []
    if hasattr(value, "_rows"):
        return [list(r) for r in value._rows]
    if isinstance(value, str):
        return [_tokens(r) for r in _split_rows(value)]
    rows = list(value)
    if rows and not isinstance(rows[0], (list, tuple)):
        rows = [rows]
    return [list(r) for r in rows]


def _cell(row, k):
    if k >= len(row):
        return None
    value = row[k]
    return None if value is None or isinstance(value, str) and value.strip() in ("", "#") else value


def _flag(value, default=True):
    if value is None:
        return default
    if isinstance(value, str):
        return value.strip().upper() in ("TRUE", "1", "YES", "PAID", "IS_PAID")
    return bool(value)


def _append(path, rows):
    from . import da
    from .management import _schema
    fields = [c.name for c in _schema(path)[0]]
    with da.InsertCursor(path, [*fields, "SHAPE@"]) as written:
        for row in rows:
            written.insertRow((*[row.get(f) for f in fields], row.get("SHAPE")))


def _clear_unless(path, append):
    if str(append or "APPEND").upper() in ("CLEAR", "FALSE"):
        from .management import DeleteRows
        DeleteRows(path)


def AddVehicleRoutingProblemBreaks(in_vrp_layer, target_route=None, break_type="TIME_WINDOW_BREAK",
                                   time_window_properties=None, travel_time_properties=None, work_time_properties=None,
                                   append_to_existing_breaks="APPEND"):
    """Breaks appended to a vehicle routing problem layer's Breaks sublayer, one for each row of the break type's
    properties and each route (or the target route), as documented. A row's columns are the page's: Is Paid, Break
    Duration (default 60), then Time Window Start and End and Maximum Violation Time, or Maximum Travel Time Between
    Breaks, or Maximum Cumulative Work Time. INFERRED: a break's Precedence numbers it after the route's existing
    breaks."""
    from . import da
    layer = _layer(in_vrp_layer)
    if layer.solver != "Vehicle Routing Problem":
        raise ExecuteError("ERROR 030000: AddVehicleRoutingProblemBreaks takes a vehicle routing problem layer.")
    kind = str(break_type or "TIME_WINDOW_BREAK").upper()
    rows = _value_rows({"TIME_WINDOW_BREAK": time_window_properties, "MAXIMUM_TRAVEL_TIME_BREAK": travel_time_properties,
                        "MAXIMUM_WORK_TIME_BREAK": work_time_properties}.get(kind, "?"))
    if kind not in ("TIME_WINDOW_BREAK", "MAXIMUM_TRAVEL_TIME_BREAK", "MAXIMUM_WORK_TIME_BREAK"):
        raise ExecuteError(f"ERROR 030000: '{break_type}' is not a break type.")
    if not rows:
        raise ExecuteError(f"ERROR 030000: give the properties of the {kind} breaks.")
    path = layer.classes["Breaks"]
    _clear_unless(path, append_to_existing_breaks)
    with da.SearchCursor(layer.classes["Routes"], ["Name"]) as cursor:
        routes = [r[0] for r in cursor]
    if _given(target_route):
        if str(target_route).lower() not in {str(r).lower() for r in routes}:
            raise ExecuteError(f"ERROR 030000: the layer has no route '{target_route}'.")
        routes = [str(target_route)]
    with da.SearchCursor(path, ["RouteName", "Precedence"]) as cursor:
        existing: dict = {}
        for name, precedence in cursor:
            existing[str(name).lower()] = max(existing.get(str(name).lower(), 0), int(precedence or 0))
    out = []
    for route in routes:
        for row in rows:
            existing[str(route).lower()] = existing.get(str(route).lower(), 0) + 1
            record = {"RouteName": route, "Precedence": existing[str(route).lower()],
                      "IsPaid": int(_flag(_cell(row, 0))), "ServiceTime": float(_cell(row, 1) or 60)}
            if kind == "TIME_WINDOW_BREAK":
                record.update(TimeWindowStart=_cell(row, 2), TimeWindowEnd=_cell(row, 3),
                              MaxViolationTime=None if _cell(row, 4) is None else float(_cell(row, 4)))
            elif kind == "MAXIMUM_TRAVEL_TIME_BREAK":
                record["MaxTravelTimeBetweenBreaks"] = float(_cell(row, 2))
            else:
                record["MaxCumulWorkTime"] = float(_cell(row, 2))
            out.append(record)
    _append(path, out)
    return Result(layer)


def AddVehicleRoutingProblemRoutes(in_vrp_layer, number_of_routes=None, route_name_prefix=None, start_depot_name=None,
                                   end_depot_name=None, earliest_start_time=None, latest_start_time=None,
                                   max_order_count=None, capacities=None, route_constraints=None, costs=None,
                                   additional_route_time=None, append_to_existing_routes="APPEND", date_and_time=None,
                                   waste_capacities=None, start_time=None, route_time_distance_constraints=None,
                                   depot_service_time=None):
    """Routes appended to a vehicle routing problem, last mile delivery or waste collection layer, named with the
    prefix and a number, as documented; the parameters that do not apply to the layer's solver are ignored, as
    documented. Defaults as the page gives them: 8:00 and 10:00 on the layer's default date, 30 orders for a vehicle
    routing problem. INFERRED where the page gives none: one route, the prefix ``Route``."""
    import datetime
    layer = _layer(in_vrp_layer)
    if layer.solver not in _FLEET_INPUTS:
        raise ExecuteError("ERROR 030000: AddVehicleRoutingProblemRoutes takes a fleet routing layer.")
    path = layer.classes["Routes"]
    _clear_unless(path, append_to_existing_routes)
    from . import da
    with da.SearchCursor(path, ["Name"]) as cursor:
        taken = {str(r[0]).lower() for r in cursor}
    prefix = str(route_name_prefix) if _given(route_name_prefix) else "Route"
    count = int(number_of_routes) if _given(number_of_routes) else 1
    first = lambda value: (_value_rows(value) or [[]])[0]  # noqa: E731
    record = {"StartDepotName": start_depot_name if _given(start_depot_name) else None,
              "EndDepotName": end_depot_name if _given(end_depot_name) else None}
    if layer.solver == "Waste Collection":
        caps = first(waste_capacities)
        record.update({f"Max{m[0]}": _cell(caps, k) for k, m in enumerate(_WASTE_MEASURES)})
        timing = first(start_time)
        begin = _cell(timing, 0)
        record["StartTime"] = None if begin is None else str(begin)
        record["OvertimeStartTime"] = _cell(timing, 1)
        limits = first(route_time_distance_constraints)
        record.update(MaxTotalTime=_cell(limits, 0), MaxTotalDistance=_cell(limits, 1))
        service = first(depot_service_time)
        record.update(StartDepotServiceTime=_cell(service, 0), EndDepotServiceTime=_cell(service, 1))
    else:
        caps = first(capacities)
        record.update({f"Capacity_{k}": _cell(caps, k - 1) for k in _NINE})
        limits = first(route_constraints)
        record.update(MaxTotalTime=_cell(limits, 0), MaxTotalTravelTime=_cell(limits, 1),
                      MaxTotalDistance=_cell(limits, 2))
        money = first(costs)
        record.update(FixedCost=_cell(money, 0), CostPerUnitTime=_cell(money, 1) if _cell(money, 1) is not None else 1.0,
                      CostPerUnitDistance=_cell(money, 2), OvertimeStartTime=_cell(money, 3),
                      CostPerUnitOvertime=_cell(money, 4))
        extra = first(additional_route_time)
        record.update(StartDepotServiceTime=_cell(extra, 0), EndDepotServiceTime=_cell(extra, 1),
                      ArriveDepartDelay=_cell(extra, 2))
        if layer.solver == "Vehicle Routing Problem":
            from ._fleet import moment, stamp
            day = layer.settings.get("default_date") or datetime.date.today()
            day = day if isinstance(day, datetime.date) else stamp(moment(day, datetime.date.today())).date()
            record["EarliestStartTime"] = earliest_start_time if _given(earliest_start_time) else \
                datetime.datetime.combine(day, datetime.time(8))
            record["LatestStartTime"] = latest_start_time if _given(latest_start_time) else \
                datetime.datetime.combine(day, datetime.time(10))
            record["MaxOrderCount"] = int(max_order_count) if _given(max_order_count) else 30
        else:
            record["MaxOrderCount"] = int(max_order_count) if _given(max_order_count) else None
            when = first(date_and_time)
            record.update(EarliestStartDate=_cell(when, 0),
                          EarliestStartTime=None if _cell(when, 1) is None else str(_cell(when, 1)),
                          StartFlexibility=_cell(when, 2))
    out, n = [], 0
    while len(out) < count:
        n += 1
        name = f"{prefix}{n}"
        if name.lower() in taken:
            continue
        taken.add(name.lower())
        out.append({**record, "Name": name})
    _append(path, out)
    return Result(layer)


def _fleet_inputs(layer, graph):
    """A fleet layer's input classes as rows for ``grapy._fleet``: every field, ``oid``, ``point`` and, for the located
    classes, ``located``."""
    from . import da
    from .management import _schema
    inputs = {}
    for key in _input_keys(layer):
        path = layer.classes[key]
        fields = [c.name for c in _schema(path)[0]]
        rows = []
        with da.SearchCursor(path, ["OID@", *fields, "SHAPE@"]) as cursor:
            for row in cursor:
                values = dict(zip(fields, row[1:-1]))
                shape = row[-1]._shape if row[-1] is not None else None
                values.update(oid=row[0], point=shape)
                if key in _FLEET_LOCATED:
                    curb = values.get("CurbApproach")
                    if curb not in (None, 0):
                        raise NotBuiltError(f"arcpy.na.Solve: CurbApproach {curb} on a fleet routing input is not built "
                                            "in grapy; every location is approached from either side.")
                    values["located"] = graph.locate(shape, float("inf")) \
                        if values.get("SourceOID") is not None and shape is not None else None
                rows.append(values)
        inputs[key] = rows
    return inputs


def _rewrite(path, rows):
    """The output values of an input class written back to its rows, by ObjectID, shape included."""
    from . import da
    from .management import _schema
    fields = [c.name for c in _schema(path)[0]]
    by_oid = {row["ObjectID"]: row for row in rows}
    with da.UpdateCursor(path, ["OID@", *fields, "SHAPE@"]) as cursor:
        for row in cursor:
            new = by_oid.get(row[0])
            if new is None:
                continue
            values = [new.get(f, old) for f, old in zip(fields, row[1:-1])]
            shape = new["SHAPE"] if "SHAPE" in new and new["SHAPE"] is not None else row[-1]
            cursor.updateRow([row[0], *values, shape])


def _fleet_solve(layer, graph):
    """Solve's fleet branch: the problem built from the sublayers, solved, and its outputs written."""
    from . import _fleet
    from ._runtime import AddWarning, _SAID_EQUIVALENT
    s = layer.settings
    inputs = _fleet_inputs(layer, graph)
    halt = str(s.get("ignore") or "HALT").upper() in ("HALT", "FALSE")
    method = _fleet.WASTE_METHOD if layer.solver == "Waste Collection" else _fleet.METHOD
    key = f"na.Solve {layer.solver}"
    if key not in _SAID_EQUIVALENT:
        _SAID_EQUIVALENT.add(key)
        AddWarning(f"arcpy.na.Solve solves a {layer.solver.lower()} by an equivalent, not ArcGIS Pro's own computation: "
                   f"{method[0]} ({method[1]}).")
    if layer.solver == "Waste Collection":
        from . import _turns
        problem = _fleet.Waste(graph, inputs, s["time_units"], s["distance_units"], s.get("route_start_time"),
                               s.get("max_route_total_time"), s.get("stop_collection_mode"),
                               _turns.driving_side(layer.network))
        problem.solve()
        return _fleet_write_waste(layer, problem, s)
    kind = "LMD" if layer.solver == "Last Mile Delivery" else "VRP"
    problem = _fleet.Fleet(graph, inputs, kind, "Two", s["time_units"], s["distance_units"],
                           _date(s.get("default_date")), s.get("time_window_factor", "Medium"),
                           s.get("excess_transit_factor", "Medium"), s.get("earliest_date"), s.get("earliest_time"),
                           s.get("max_route_total_time"), ignore_invalid=not halt)
    problem.solve()
    out = _fleet.fleet_outputs(problem, s.get("line_shape"), "LMD" if kind == "LMD" else "Two",
                               s.get("sequence_gap", 1))
    for key in ("Orders", "Depots", "Routes"):
        _rewrite(layer.classes[key], out[key])
    if "Breaks" in out:
        _rewrite(layer.classes["Breaks"], out["Breaks"])
    _write(layer.classes["DepotVisits"], out["DepotVisits"])
    return any(r.get("OrderCount") for r in out["Routes"])


def _fleet_write_waste(layer, problem, s):
    from . import _fleet
    out = _fleet.waste_outputs(problem, s.get("line_shape"))
    for key in ("Stops", "Depots", "Renewals", "Routes"):
        rows = out[key]
        if key == "Routes":
            for row in rows:
                if row.get("EndTime") is not None:
                    row["EndTime"] = str(row["EndTime"])
        _rewrite(layer.classes[key], rows)
    for row in out["RenewalVisits"] + out["RouteLines"]:
        for field in ("ArriveTime", "DepartTime"):
            if row.get(field) is not None:
                row[field] = str(row[field])
    _write(layer.classes["RenewalVisits"], out["RenewalVisits"])
    _write(layer.classes["RouteLines"], out["RouteLines"])
    for row in out["Routes"]:
        codes = [row.get(f"ViolatedConstraint_{k}") for k in range(1, 5)]
        codes = [c for c in codes if c is not None]
        if codes:
            from ._runtime import AddWarning
            AddWarning(f"Route '{row['Name']}' violates constraints {codes}.")
    return any(r.get("StopCount") for r in out["Routes"])


def _date(value):
    import datetime
    from ._fleet import moment, stamp
    if value is None or isinstance(value, str) and value.strip() in ("", "#"):
        return None
    if isinstance(value, datetime.datetime):
        return value.date()
    if isinstance(value, datetime.date):
        return value
    special = {"TODAY": datetime.date.today()}
    if str(value).upper() in special:
        return special[str(value).upper()]
    return stamp(moment(value, datetime.date.today())).date()


def _fleet_resequence():
    from ._fleet import RESEQUENCE
    return RESEQUENCE


def _waste_layer(tool, value):
    layer = _layer(value)
    if layer.solver != "Waste Collection":
        raise ExecuteError(f"ERROR 030000: arcpy.na.{tool} works with waste collection layers only.")
    return layer


def _waste_problem(layer):
    from . import _fleet, _turns
    graph = _network.Graph(layer.network, layer.travel_mode)
    s = layer.settings
    inputs = _fleet_inputs(layer, graph)
    problem = _fleet.Waste(graph, inputs, s["time_units"], s["distance_units"], s.get("route_start_time"),
                           s.get("max_route_total_time"), s.get("stop_collection_mode"),
                           _turns.driving_side(layer.network))
    return problem, inputs


def _current_assignment(problem):
    """The units' routes as the Stops' RouteName says, a unit going with its first stop that names a route."""
    assignment = {r: [] for r in problem.routes}
    for k, unit in enumerate(problem.units):
        names = [str(s.get("RouteName")).lower() for s in unit["stops"] if s.get("RouteName")]
        if names and problem.route_index.get(names[0]) in assignment and k not in problem.excluded:
            assignment[problem.route_index[names[0]]].append(k)
    for r in assignment:
        assignment[r].sort(key=lambda k: min((s.get("Sequence") or 0) for s in problem.units[k]["stops"]))
    return assignment


@equivalent(*_fleet_resequence())
def UpdateFleetRoutingResults(in_fleet_routing_layer):
    """A waste collection layer's results recalculated for the route assignments edited in its Stops' RouteName: the
    stops of each route resequenced, never moved to another route, and its outputs rewritten, as documented. Maximum
    time, distance and capacity may be violated, each reported as a warning."""
    layer = _waste_layer("UpdateFleetRoutingResults", in_fleet_routing_layer)
    problem, _ = _waste_problem(layer)
    problem.resequence(_current_assignment(problem))
    _fleet_write_waste(layer, problem, layer.settings)
    return Result(layer)


@equivalent(*_fleet_resequence())
def UpdateFleetRoutingAssignments(in_fleet_routing_layer, assignment_operation="SINGLE_ROUTE", route_names=None):
    """The selected stops of a waste collection layer taken off their routes (``NOT_ASSIGNED``) or moved to one route
    (``SINGLE_ROUTE``), a whole collection edge at a time, then the results recalculated as UpdateFleetRoutingResults
    does. INFERRED: the selection is that of a layer made over the layer's Stops sublayer (MakeFeatureLayer on
    GetNASublayer's path), grapy's sublayers being tables."""
    from .management import _LAYERS
    layer = _waste_layer("UpdateFleetRoutingAssignments", in_fleet_routing_layer)
    operation = str(assignment_operation or "SINGLE_ROUTE").upper()
    if operation not in ("SINGLE_ROUTE", "NOT_ASSIGNED"):
        raise ExecuteError(f"ERROR 030000: '{assignment_operation}' is not SINGLE_ROUTE or NOT_ASSIGNED.")
    stops_path = layer.classes["Stops"]
    selected = set()
    for each in _LAYERS.values():
        if str(getattr(each, "source", "")) == stops_path and getattr(each, "selection", None):
            selected |= {int(i) for i in each.selection}
    if not selected:
        raise ExecuteError("ERROR 030000: select the stops to update in a layer over the Stops sublayer first.")
    problem, _ = _waste_problem(layer)
    assignment = _current_assignment(problem)
    moved = {k for k, unit in enumerate(problem.units) if any(s["oid"] in selected for s in unit["stops"])}
    for units in assignment.values():
        units[:] = [k for k in units if k not in moved]
    if operation == "SINGLE_ROUTE":
        if not _given(route_names):
            raise ExecuteError("ERROR 030000: SINGLE_ROUTE needs one route name.")
        target = problem.route_index.get(str(route_names).lower())
        if target is None or target not in assignment:
            raise ExecuteError(f"ERROR 030000: the layer has no route '{route_names}' that can take stops.")
        assignment[target] += sorted(moved)
    problem.resequence(assignment)
    _fleet_write_waste(layer, problem, layer.settings)
    return Result(layer)


# ---- turn feature classes (ADR-239) ---------------------------------------------------------------------------------

_TEMPLATE_SKIPPED = {"NODE_", "NODE#", "JUNCTION", "F_EDGE", "T_EDGE", "F-EDGE", "T-EDGE", "ARC1_", "ARC2_", "ARC1#",
                     "ARC2#", "ARC1-ID", "ARC2-ID", "AZIMUTH", "ANGLE"}


def CreateTurnFeatureClass(out_location, out_feature_class_name, maximum_edges=5, in_network_dataset=None,
                           in_template_feature_class=None, spatial_reference=None, config_keyword=None,
                           spatial_grid_1=None, spatial_grid_2=None, spatial_grid_3=None, has_z=None):
    """A turn feature class: Edge1End and Edge#FCID, Edge#FID and Edge#Pos for each of up to ``maximum_edges`` edges
    (default 5, at most 50), a template's fields less the documented turn table names, and — given a network — a turn
    source of it (built again with BuildNetwork). The spatial grids are ignored, as documented; ``config_keyword``
    applies to enterprise geodatabases only."""
    from .management import AddField, CreateFeatureclass, ListFields
    count = _turns_check(maximum_edges)
    created = CreateFeatureclass(out_location, out_feature_class_name, "POLYLINE", None, "DISABLED",
                                 "ENABLED" if str(has_z or "").upper() in ("ENABLED", "TRUE") else "DISABLED",
                                 spatial_reference)[0]
    for column in _turns_module().turn_columns(count):
        AddField(created, column.name, {"String": "TEXT", "Integer": "LONG", "Double": "DOUBLE"}[column.type])
    if _given(in_template_feature_class):
        have = {f.lower() for f in _turns_module().fields_of(created)}
        for field in ListFields(in_template_feature_class):
            if field.type in ("OID", "Geometry") or field.name.upper() in _TEMPLATE_SKIPPED or field.name.lower() in have:
                continue
            AddField(created, field.name, {"String": "TEXT", "Integer": "LONG", "SmallInteger": "SHORT",
                                           "Double": "DOUBLE", "Single": "FLOAT", "Date": "DATE"}.get(field.type, "TEXT"))
    if _given(in_network_dataset):
        _turns_module().add_turn_source(str(_network.LAYERS.get(str(in_network_dataset), in_network_dataset)), created)
    return Result(created)


def _turns_module():
    from . import _turns
    return _turns


def _turns_check(value):
    return _turns_module().check_maximum(5 if not _given(value) else value)


def IncreaseMaximumEdges(in_turn_features, maximum_edges):
    """The turn feature class's maximum edges raised — three fields more for each edge, and never lowered, as
    documented."""
    from .management import _source
    path = _source(in_turn_features)[0]
    _turns_module().increase_maximum_edges(path, maximum_edges)
    return Result(str(in_turn_features))


def PopulateAlternateIDFields(in_network_dataset, alternate_ID_field_name):
    """AltID1 … AltID<maximum edges> on every turn source of the network, from the edge sources' unique ID field, as
    documented."""
    network = str(_network.LAYERS.get(str(in_network_dataset), in_network_dataset))
    _turns_module().populate_alternate_ids(network, alternate_ID_field_name)
    return Result(str(in_network_dataset))


def UpdateByAlternateIDFields(in_network_dataset, alternate_ID_field_name):
    """Every turn's Edge#FID found again through its AltID# values, as documented; build the network again after."""
    network = str(_network.LAYERS.get(str(in_network_dataset), in_network_dataset))
    _turns_module().update_by_alternate_ids(network, alternate_ID_field_name)
    return Result(str(in_network_dataset))


def UpdateByGeometry(in_turn_features):
    """The turn features' edge references set from their shapes, by the network edges they coincide with, as
    documented; the turn class must be a turn source of a built network, and the network is built again after."""
    from .management import _source
    _turns_module().update_by_geometry(_source(in_turn_features)[0])
    return Result(str(in_turn_features))


def TurnTableToTurnFeatureClass(in_turn_table, reference_line_features, out_feature_class_name,
                                reference_nodes_table=None, maximum_edges=5, config_keyword=None, spatial_grid_1=None,
                                spatial_grid_2=None, spatial_grid_3=None):
    """An ArcView turn table's turns as a turn feature class in the reference line features' workspace, as documented:
    each row's from and to edges (``F_EDGE``/``ARC1_``/``ARC1#``/``ARC1-ID``/``F-EDGE`` and their ``T``/``ARC2``
    names), INFERRED to be the line features' object ids, joined at the end point they share; the row's other fields
    are kept, all but the turn table's node, azimuth and angle fields. A turn line runs from the middle of its first
    edge through the junction to the middle of its second. As the page says of a table without a nodes table, U-turns
    and turns between edges joined at both ends are not made. A reference nodes table (ArcView's nodes.dbf, whose
    layout is not published) and ArcInfo Workstation INFO turn tables (``.trn``) are refused."""
    from shapely.geometry import LineString
    from shapely.ops import substring
    from . import da
    from .management import _schema, _source
    from ._runtime import AddWarning
    if _given(reference_nodes_table):
        raise NotBuiltError("arcpy.na.TurnTableToTurnFeatureClass: a reference nodes table is ArcView's nodes.dbf, whose "
                            "layout is not published; grapy finds each turn's junction from its two edges.")
    table_path = _source(in_turn_table)[0]
    if str(table_path).lower().endswith(".trn") or os.path.isdir(str(table_path)):
        raise NotBuiltError("arcpy.na.TurnTableToTurnFeatureClass: ArcInfo Workstation INFO turn tables are stored in a "
                            "format that is not published.")
    lines_path = _source(reference_line_features)[0]
    file, table = _gpkg.split(lines_path)
    folder = table.rsplit("/", 1)[0] if "/" in table else None
    location = f"{file}/{folder}" if folder else file
    columns = _schema(table_path)[0]
    names = {c.name.upper(): c.name for c in columns}
    pick = lambda options: next((names[o] for o in options if o in names), None)  # noqa: E731
    from_field = pick(("F_EDGE", "F-EDGE", "ARC1_", "ARC1#", "ARC1-ID"))
    to_field = pick(("T_EDGE", "T-EDGE", "ARC2_", "ARC2#", "ARC2-ID"))
    if from_field is None or to_field is None:
        raise ExecuteError("ERROR 030000: the turn table has no from-edge and to-edge fields.")
    kept = [c for c in columns if c.name.upper() not in _TEMPLATE_SKIPPED]
    created = CreateTurnFeatureClass(location, out_feature_class_name, maximum_edges)[0]
    from .management import AddField
    for column in kept:
        AddField(created, column.name, {"String": "TEXT", "Integer": "LONG", "SmallInteger": "SHORT", "Double": "DOUBLE",
                                        "Single": "FLOAT", "Date": "DATE"}.get(column.type, "TEXT"))
    with da.SearchCursor(lines_path, ["OID@", "SHAPE@"]) as cursor:
        lines = {oid: shape._shape for oid, shape in cursor if shape is not None}
    source_id = _source_id_of(lines_path)
    skipped = 0
    out = []
    with da.SearchCursor(table_path, [from_field, to_field, *[c.name for c in kept]]) as cursor:
        for row in cursor:
            first, second = lines.get(_as_int(row[0])), lines.get(_as_int(row[1]))
            if first is None or second is None or _as_int(row[0]) == _as_int(row[1]):
                skipped += 1
                continue
            ends_a = [tuple(first.coords[0][:2]), tuple(first.coords[-1][:2])]
            ends_b = [tuple(second.coords[0][:2]), tuple(second.coords[-1][:2])]
            shared = [p for p in ends_a if p in ends_b]
            if len(set(shared)) != 1:
                skipped += 1
                continue
            junction = shared[0]
            at_end_a = junction == ends_a[1]
            at_start_b = junction == ends_b[0]
            half_a = substring(first, 0.5, 1.0, normalized=True) if at_end_a else \
                LineString(list(substring(first, 0.0, 0.5, normalized=True).coords)[::-1])
            half_b = substring(second, 0.0, 0.5, normalized=True) if at_start_b else \
                LineString(list(substring(second, 0.5, 1.0, normalized=True).coords)[::-1])
            coordinates = list(half_a.coords) + list(half_b.coords)[1:]
            record = {"Edge1End": "Y" if at_end_a else "N", "Edge1FCID": source_id, "Edge1FID": _as_int(row[0]),
                      "Edge1Pos": 1.0 if at_end_a else 0.0, "Edge2FCID": source_id, "Edge2FID": _as_int(row[1]),
                      "Edge2Pos": 0.0 if at_start_b else 1.0, "SHAPE": LineString(coordinates)}
            record.update({c.name: v for c, v in zip(kept, row[2:])})
            out.append(record)
    _append(created, out)
    if skipped:
        AddWarning(f"TurnTableToTurnFeatureClass: {skipped} turn(s) are U-turns, name missing edges, or join edges that "
                   "do not meet at one end point, and were not made.")
    return Result(created)


def _as_int(value):
    try:
        return int(value)
    except (TypeError, ValueError):
        return None


def _source_id_of(lines_path):
    """The network source id of a line class, when it is an edge source of a network in its geodatabase."""
    file, table = _gpkg.split(lines_path)
    connection = _gpkg.connect(file)
    try:
        if not connection.execute("select 1 from sqlite_master where name = 'grapy_network_source'").fetchone():
            return None
        row = connection.execute("select network from grapy_network_source where class_name = ?", (table,)).fetchone()
        if row is None:
            return None
        sources = [r[0] for r in connection.execute("select class_name from grapy_network_source where network = ? "
                                                    "order by rowid", (row[0],))]
        return sources.index(table) + 1
    finally:
        connection.close()


# ---- CheckIntersectingFeatures --------------------------------------------------------------------------------------

def CheckIntersectingFeatures(network_dataset_path, feature_layer, cutoff=5000):
    """True when the edge source features of the network dataset that the layer's features intersect number no more
    than ``cutoff`` (default 5000), False otherwise, as the function's page documents
    (arcpy/network-analyst/checkintersectingfeatures.html, read 2026-10-31). The layer's definition query and selection
    are honoured, as documented. INFERRED: an edge source feature is counted once however many of the layer's features
    touch it, the page counting "edge source features" and not intersections; a layer in another coordinate system
    than the network is refused rather than projected."""
    from . import da
    import shapely
    from .management import _schema, _source
    connection, file, dataset, name = _network.open_network(network_dataset_path)
    try:
        sources = [r[0] for r in connection.execute(
            "select class_name from grapy_network_source where network = ? order by rowid", (name,))]
    finally:
        connection.close()
    limit = 5000 if not _given(cutoff) else int(cutoff)
    path, where = _source(feature_layer)
    srid = _schema(path)[2]
    with da.SearchCursor(path, ["SHAPE@"], where) as rows:
        shapes = [r[0]._shape for r in rows if r[0] is not None and not r[0]._shape.is_empty]
    if not shapes:
        return True
    tree = shapely.STRtree(shapes)
    count = 0
    for table in sources:
        source = f"{file}/{table}"
        if srid and _schema(source)[2] and _schema(source)[2] != srid:
            raise NotBuiltError("arcpy.na.CheckIntersectingFeatures: a feature layer in another coordinate system than "
                                "the network dataset is not built in grapy; project it first.")
        with da.SearchCursor(source, ["SHAPE@"]) as rows:
            for (shape,) in rows:
                if shape is None or shape._shape.is_empty:
                    continue
                if len(tree.query(shape._shape, predicate="intersects")):
                    count += 1
                    if count > limit:
                        return False
    return True


# ---- refused by name -----------------------------------------------------------------------------------------------

_REASONS = {
    "writes turn-by-turn directions by Esri's direction rules, which are not published": ("Directions",),
    "reads or writes Esri's network dataset template XML, whose schema is not published": (
        "CreateNetworkDatasetFromTemplate", "CreateTemplateFromNetworkDataset"),
    "dissolves network edges by Esri's rules, which are not published": ("DissolveNetwork",),
    "is a deprecated layer tool; its Analysis Layer successor is built": (
        "MakeRouteLayer", "MakeClosestFacilityLayer", "MakeServiceAreaLayer", "MakeODCostMatrixLayer",
        "MakeLocationAllocationLayer", "MakeVehicleRoutingProblemLayer"),
    "shares route layers to a portal, and a fenced tool has no network (ADR-223 §5.3)": ("ShareAsRouteLayers",),
    "sets an attribute parameter, and grapy's evaluators take none": ("UpdateAnalysisLayerAttributeParameter",),
    "edits a layer through Esri's solver property objects; grapy's layers take their settings from the Make tools": (
        "GetSolverProperties", "ClosestFacilitySolverProperties", "LocationAllocationSolverProperties",
        "ODCostMatrixSolverProperties", "RouteSolverProperties", "ServiceAreaSolverProperties",
        "VehicleRoutingProblemSolverProperties"),
    "writes or lists Esri's direction text and styles, whose rules are not published": (
        "GenerateDirectionsFeatures", "ListDirectionsLanguages", "ListDirectionsStyleNames", "StreetDirectionsProperties"),
    "reaches a portal for a web tool's settings, and a fenced tool has no network (ADR-223 §5.3)": ("GetWebToolInfo",),
    "solves through ArcGIS Online's routing service, and a fenced tool has no network (ADR-223 §5.3)": (
        "SolveVehicleRoutingProblem",),
    "copies the network elements a solve traversed, and grapy's solvers keep routes, not their traversed elements": (
        "CopyTraversedSourceFeatures",),
}
_REFUSED = {name: reason for reason, names in _REASONS.items() for name in names}


def __getattr__(name):
    if name.startswith("_"):
        raise AttributeError(name)
    reason = _REFUSED.get(name)
    message = f"arcpy.na.{name} {reason}." if reason else f"arcpy.na.{name} is not built in grapy yet."

    def missing(*_, **__):
        raise NotBuiltError(message)

    return missing
