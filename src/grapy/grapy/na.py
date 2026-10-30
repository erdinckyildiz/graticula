"""arcpy.na — network datasets and their analysis layers, by their documented names and parameters (ADR-226).

The network model is ``grapy._network``: grapy's tables in the GeoPackage. An analysis layer (MakeRouteAnalysisLayer and
the rest) is a name held in this process, as a layer is (INFERRED), with its sublayers as tables of a GeoPackage:
``analysis_geodatabase`` when it names one, else ``env.scratchGDB``, each table ``<layer name>/<sublayer>``.
GetNASublayer answers a sublayer's path, which the cursors read.

Solve is an *equivalent* (ADR-224 §5.6, ADR-226 §5.4): Dijkstra's shortest path over the travel mode's impedance.
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
    connects as ``NO_ELEVATION`` does. ``Z_COORDINATES``, point (junction) sources and turn sources are not built."""
    from .management import _feature_dataset_of, _list
    file, dataset = _gpkg.split(str(feature_dataset))
    if not dataset or _feature_dataset_of(f"{file}/{dataset}/x") is None:
        raise ExecuteError("ERROR 000732: a network dataset is created in a feature dataset (CreateFeatureDataset).")
    model = str(elevation_model or "ELEVATION_FIELDS").upper()
    if model == "Z_COORDINATES":
        raise NotBuiltError("arcpy.na.CreateNetworkDataset: Z_COORDINATES needs Z values, which grapy's shapes do not hold.")
    sources, srid = [], 0
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
        if "LINESTRING" not in row[0].upper():
            raise NotBuiltError(f"arcpy.na.CreateNetworkDataset: '{name}' is not a line class; junction and turn sources "
                                "are not built in grapy (turns are refused, ADR-226 §5.3).")
        sources.append(table)
        srid = int(row[1] or 0)
    if not sources:
        raise ExecuteError("ERROR 000735: a network dataset needs a line feature class.")
    _network.create(file, dataset, str(out_name), sources, model, srid)
    return Result(str(feature_dataset).rstrip("/\\") + "/" + str(out_name))


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
    if str(travel.uTurns).upper() not in ("ALLOW_UTURNS", "ALLOW_UTURNS_EVERYWHERE", "ALLOW"):
        raise NotBuiltError("U-turn policies other than allowing U-turns everywhere need turns, refused (ADR-226 §5.3).")
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
    name = str(layer_name) if _given(layer_name) else {"Route": "Route", "Closest Facility": "Closest Facility",
                                                       "Service Area": "Service Area", "OD Cost Matrix": "OD Cost Matrix",
                                                       "Location-Allocation": "Location-Allocation"}[solver]
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
    """A route analysis layer: Stops and Routes, with the documented fields. Only ``USE_CURRENT_ORDER`` is built (finding
    the best order is the travelling salesman problem, whose heuristic is not chosen yet); time of day is refused
    (traffic); directions are refused, so ``generate_directions_on_solve``, whose default is ``DIRECTIONS``, must be
    ``NO_DIRECTIONS``. INFERRED: ``line_shape`` defaults to ``ALONG_NETWORK``."""
    if str(sequence or "USE_CURRENT_ORDER").upper() != "USE_CURRENT_ORDER":
        raise NotBuiltError(f"arcpy.na.MakeRouteAnalysisLayer: sequence '{sequence}' is the travelling salesman problem, "
                            "whose heuristic is not chosen yet (ADR-226 §5.2).")
    _common_refusals("MakeRouteAnalysisLayer", time_of_day, generate_directions_on_solve)
    accumulate = _accumulate(accumulate_attributes)

    def costs(key, graph):
        if key == "Stops":
            return [(f"Cumul_{graph.impedance}", _DOUBLE)]
        if key == "Routes":
            return [(f"Total_{a}", _DOUBLE) for a in [graph.impedance, *[x for x in accumulate if x != graph.impedance]]]
        return []

    return _make("Route", network_data_source, layer_name, travel_mode,
                 {"line_shape": line_shape, "accumulate": accumulate, "ignore": ignore_invalid_locations},
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
        is_input = key in _INPUTS or key.endswith("Barriers")
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
    if key not in _INPUTS:
        raise ExecuteError(f"ERROR 000800: '{sub_layer}' is an output sublayer.")
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
                    raise NotBuiltError(f"arcpy.na.Solve: {field} {values[field]} needs turns or waypoints, refused "
                                        "(ADR-226 §5.3).")
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
    halt = str(ignore_invalids or layer.settings.get("ignore") or "SKIP").upper() in ("HALT", "FALSE")
    inputs = {key: _items(layer, key, graph) for key in layer.classes if key in _INPUTS}
    if halt and any(item["located"] is None for items in inputs.values() for item in items):
        raise ExecuteError("ERROR 030024: an input is not located on the network, and invalid locations halt the solve.")
    s = layer.settings
    try:
        if layer.solver == "Route":
            routes, results = _network.solve_route(graph, inputs["Stops"], s["accumulate"], s["line_shape"])
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
                                 mode.timeAttributeName, mode.distanceAttributeName)
    return Result(str(in_network_dataset))


def UpgradeNetwork(in_network_dataset):
    """A network dataset's schema brought to the current release. grapy's network tables have one schema, so a network
    made by grapy is current already and nothing changes; it must be built, as the page's sample builds it after."""
    _network.open_network(str(in_network_dataset))[0].close()
    return Result(str(in_network_dataset))


# ---- refused by name -----------------------------------------------------------------------------------------------

_HEURISTIC = "is an NP-hard problem whose published heuristic is not chosen yet (ADR-226 §5.2)"
_TURNS = "needs turns, refused until they are defined (ADR-226 §5.3)"
_REASONS = {
    _HEURISTIC: ("MakeVehicleRoutingProblemAnalysisLayer",
                 "MakeLastMileDeliveryAnalysisLayer", "MakeWasteCollectionAnalysisLayer", "AddVehicleRoutingProblemBreaks",
                 "AddVehicleRoutingProblemRoutes", "MakeVehicleRoutingProblemLayer"),
    _TURNS: ("CreateTurnFeatureClass", "IncreaseMaximumEdges", "PopulateAlternateIDFields", "TurnTableToTurnFeatureClass",
             "UpdateByAlternateIDFields", "UpdateByGeometry"),
    "writes turn-by-turn directions by Esri's direction rules, which are not published": ("Directions",),
    "reads or writes Esri's network dataset template XML, whose schema is not published": (
        "CreateNetworkDatasetFromTemplate", "CreateTemplateFromNetworkDataset"),
    "dissolves network edges by Esri's rules, which are not published": ("DissolveNetwork",),
    "is a deprecated layer tool; its Analysis Layer successor is built": (
        "MakeRouteLayer", "MakeClosestFacilityLayer", "MakeServiceAreaLayer", "MakeODCostMatrixLayer",
        "MakeLocationAllocationLayer"),
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
    "updates a fleet routing plan — vehicle routing, an NP-hard problem whose published heuristic is not chosen yet "
    "(ADR-226 §5.2)": ("UpdateFleetRoutingAssignments", "UpdateFleetRoutingResults"),
    "copies the network elements a solve traversed, and grapy's solvers keep routes, not their traversed elements": (
        "CopyTraversedSourceFeatures",),
    "has no page grapy could read: doc.esri.com/en/arcgis-pro/latest/tool-reference/network-analyst/"
    "check-intersecting-features.html answered 404 (checked 2026-10-30)": ("CheckIntersectingFeatures",),
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
