"""arcpy.nax — the Network Analyst solver objects, by their documented names, over grapy's network model (ADR-226).

A solver object (Route, ClosestFacility, ServiceArea, OriginDestinationCostMatrix, LocationAllocation, and since ADR-239
VehicleRoutingProblem, LastMileDelivery and WasteCollection) holds its inputs in memory, locates
them on the network when it solves, and answers a result object that counts, reads and exports its outputs. Every
``solve()`` is an *equivalent* (ADR-224 §5.6, ADR-226 §5.4): Dijkstra's shortest path over the travel mode's impedance,
said once in the run's messages and marked on each solver class as ``__grapy_equivalent__``.

Output totals follow the Route page's documented naming: ``Total_Minutes`` (and ``Total_<timeUnits>``) from the travel
mode's time attribute, ``Total_Miles`` and ``Total_Kilometers`` (and ``Total_<distanceUnits>``) from its distance
attribute, ``Total_Other`` when the impedance is neither; ``Cumul_`` the same on stops. INFERRED: the closest facility,
OD cost matrix and service area outputs, whose pages answered 404 when checked (2026-10-30), take the same totals and
the ID fields of their ``arcpy.na`` sublayers.
"""
from __future__ import annotations

import datetime
import enum
import os

from . import _gpkg, _network
from ._runtime import ExecuteError, NotBuiltError

# Every enumeration below is an IntEnum with ArcGIS Pro's members, values and order — measured in ArcGIS Pro 3.7,
# 2026-11-12 (Pro sweep): until then they held their names as string values, and four had members Pro does not
# (CurbApproach's ...OfVehicle names, TimeOfDayUsage's StartTime/EndTime, ClosestFacilityOutputDataType.RouteEdges and
# VehicleRoutingProblemOutputDataType's direction points and lines) or lacked one (MessageSeverity.Info,
# ClosestFacilityOutputDataType.ClosestFacilities, CurbApproach.Unknown).


class DistanceUnits(enum.IntEnum):
    Feet = 3
    Yards = 4
    Miles = 5
    NauticalMiles = 6
    Meters = 9
    Kilometers = 10
    Inches = 11
    Centimeters = 12
    Millimeters = 13
    Decimeters = 14


class TimeUnits(enum.IntEnum):
    Seconds = 20
    Minutes = 21
    Hours = 22
    Days = 23


class TravelDirection(enum.IntEnum):
    FromFacility = 0
    ToFacility = 1


class RouteShapeType(enum.IntEnum):
    NoLine = 0
    StraightLine = 1
    TrueShape = 2
    TrueShapeWithMeasures = 3


class LineShapeType(enum.IntEnum):
    NoLine = 0
    StraightLine = 1


class ServiceAreaOutputType(enum.IntEnum):
    Polygons = 0
    Lines = 1
    PolygonsAndLines = 2


class ServiceAreaPolygonCutoffGeometry(enum.IntEnum):
    Disks = 0
    Rings = 1


class ServiceAreaOverlapGeometry(enum.IntEnum):
    Split = 0
    Overlap = 1
    Dissolve = 2


class ServiceAreaPolygonDetail(enum.IntEnum):
    Generalized = 0
    Standard = 1
    High = 2


class TimeZoneUsage(enum.IntEnum):
    LocalTimeAtLocations = 0
    UTC = 1


class MessageSeverity(enum.IntEnum):
    All = 0
    Warning = 1
    Error = 2
    Info = 3


class RouteInputDataType(enum.IntEnum):
    Stops = 0
    PointBarriers = 1
    LineBarriers = 2
    PolygonBarriers = 3


class RouteOutputDataType(enum.IntEnum):
    Stops = 0
    Routes = 1
    RouteEdges = 2
    DirectionPoints = 3
    DirectionLines = 4
    Directions = 5
    RouteJunctions = 6
    RouteTurns = 7


class ClosestFacilityInputDataType(enum.IntEnum):
    Incidents = 0
    Facilities = 1
    PointBarriers = 2
    LineBarriers = 3
    PolygonBarriers = 4


class ClosestFacilityOutputDataType(enum.IntEnum):
    Incidents = 0
    Facilities = 1
    Routes = 2
    ClosestFacilities = 3
    DirectionPoints = 4
    DirectionLines = 5
    Directions = 6


class ServiceAreaInputDataType(enum.IntEnum):
    Facilities = 0
    PointBarriers = 1
    LineBarriers = 2
    PolygonBarriers = 3


class ServiceAreaOutputDataType(enum.IntEnum):
    Facilities = 0
    Lines = 1
    Polygons = 2


class OriginDestinationCostMatrixInputDataType(enum.IntEnum):
    Origins = 0
    Destinations = 1
    PointBarriers = 2
    LineBarriers = 3
    PolygonBarriers = 4


class OriginDestinationCostMatrixOutputDataType(enum.IntEnum):
    Origins = 0
    Destinations = 1
    Lines = 2


_MINUTES = {"SECONDS": 1 / 60, "MINUTES": 1.0, "HOURS": 60.0, "DAYS": 1440.0}


def _kind(units):
    key = str(units or "").upper().replace(" ", "")
    if key in _MINUTES:
        return "time"
    if key in _network._LINEAR:
        return "distance"
    return "other"


# ---- what ArcGIS Pro's solvers read and write, measured in ArcGIS Pro 3.7, 2026-11-12 (Pro sweep) -------------------
#
# A solver's ``fieldNames(input_type)``: its input's properties, in Pro's order (no ObjectID or Shape). With
# ``use_location_fields`` Pro adds SourceID, SourceOID, PosAlong and SideOfEdge before CurbApproach (else before
# Bearing), and a line or polygon barrier's Locations after its Name.

_BARRIER_FIELDS = {
    "PointBarriers": ["Name", "CurbApproach", "FullEdge", "BarrierType", "AdditionalCost", "Additional_Time",
                      "Additional_Distance", "Bearing", "BearingTol", "NavLatency"],
    "LineBarriers": ["Name", "BarrierType", "ScaledCostFactor", "ScaledTimeFactor", "ScaledDistanceFactor"],
    "PolygonBarriers": ["Name", "BarrierType", "ScaledCostFactor", "ScaledTimeFactor", "ScaledDistanceFactor"],
}
_PRO_INPUT_FIELDS = {
    "Route": {"Stops": ["Name", "RouteName", "Sequence", "TimeWindowStart", "TimeWindowEnd", "LocationType",
                        "CurbApproach", "AdditionalTime", "AdditionalDistance", "AdditionalCost", "Bearing", "BearingTol",
                        "NavLatency"]},
    "ClosestFacility": {
        "Incidents": ["Name", "TargetFacilityCount", "CurbApproach", "ID", "AdditionalTime", "AdditionalDistance",
                      "AdditionalCost", "Cutoff", "Bearing", "BearingTol", "NavLatency"],
        "Facilities": ["Name", "CurbApproach", "ID", "AdditionalTime", "AdditionalDistance", "AdditionalCost", "Cutoff",
                       "Bearing", "BearingTol", "NavLatency"]},
    "ServiceArea": {"Facilities": ["Name", "CurbApproach", "Breaks", "AdditionalTime", "AdditionalDistance",
                                   "AdditionalCost", "Bearing", "BearingTol", "NavLatency"]},
    "WasteCollection": {
        "Stops": ["Name", "ServiceTime", "Bins_1", "Weight_1", "Volume_1", "Bins_2", "Weight_2", "Volume_2",
                  "CollectionMode", "AnchorRule", "AssignmentRule", "RouteName", "Sequence", "Bearing", "BearingTol",
                  "NavLatency"],
        "Depots": ["Name", "Bearing", "BearingTol", "NavLatency"],
        "Routes": ["Name", "StartDepotName", "EndDepotName", "StartDepotServiceTime", "EndDepotServiceTime",
                   "StartTime", "MaxBins_1", "MaxWeight_1", "MaxVolume_1", "MaxBins_2", "MaxWeight_2", "MaxVolume_2",
                   "OvertimeStartTime", "MaxTotalTime", "MaxTotalDistance", "AssignmentRule"],
        "Renewals": ["Name", "Fraction", "Bearing", "BearingTol", "NavLatency"],
        "RouteRenewals": ["RouteName", "RenewalName", "ServiceTime"],
        "RenewalVisits": ["RouteName", "RenewalName", "Sequence"]},
    "OriginDestinationCostMatrix": {
        "Origins": ["Name", "TargetDestinationCount", "CurbApproach", "Cutoff", "Bearing", "BearingTol", "NavLatency"],
        "Destinations": ["Name", "CurbApproach", "Bearing", "BearingTol", "NavLatency"]},
    "LocationAllocation": {
        "Facilities": ["Name", "FacilityType", "Weight", "Capacity", "CurbApproach", "Bearing", "BearingTol",
                       "NavLatency"],
        "DemandPoints": ["Name", "Weight", "GroupName", "ImpedanceTransformation", "ImpedanceParameter", "CurbApproach",
                         "TimeCutoff", "DistanceCutoff", "Bearing", "BearingTol", "NavLatency", "Cutoff"]},
}

# The value a property holds when a row does not give one (an insertCursor's row: Pro writes these into its outputs),
# and the defaultValue of its NAClassFieldMap; every other property's is None. BearingTol's 30 is the field map's only.
_DEFAULTS = {"CurbApproach": 0, "LocationType": 0, "AdditionalTime": 0.0, "AdditionalDistance": 0.0,
             "AdditionalCost": 0.0, "FacilityType": 0, "Weight": 1.0}
_MAP_DEFAULTS = {**_DEFAULTS, "BearingTol": 30.0}
# Properties no Pro output echoes (measured 2026-11-12): read by the solve, not written back.
_NOT_ECHOED = {"AdditionalCost", "TimeCutoff", "DistanceCutoff"}


def _with_locations(names):
    names = list(names)
    if "CurbApproach" not in names and "Bearing" not in names:
        return names[:1] + ["Locations"] + names[1:]
    at = names.index("CurbApproach") if "CurbApproach" in names else names.index("Bearing")
    return names[:at] + ["SourceID", "SourceOID", "PosAlong", "SideOfEdge"] + names[at:]


def _curb(side, forward):
    """The side of the vehicle a location is on, CurbApproach's numbers (1 right, 2 left): its SideOfEdge (of the
    digitised direction) when travelling along the edge, the other side against it - as Pro's ArriveCurbApproach and
    DepartCurbApproach answer (measured in ArcGIS Pro 3.7, 2026-11-12)."""
    if side is None or forward is None:
        return None
    return int(side) if forward else 3 - int(side)


_ROUTE_FIELDS = ["ObjectID", "Name", "FirstStopID", "LastStopID", "StopCount", "StartTime", "EndTime", "StartTimeUTC",
                 "EndTimeUTC", "Total_Miles", "Total_Kilometers", "Total_Minutes", "TotalWait_Minutes",
                 "TotalViolation_Minutes", "FirstStopOID", "LastStopOID"]
_CLOSEST_FIELDS = ["ObjectID", "FacilityRank", "Name", "IncidentCurbApproach", "FacilityCurbApproach", "StartTime",
                   "EndTime", "StartTimeUTC", "EndTimeUTC", "FacilityID", "IncidentID", "OutputFacilityOID",
                   "OutputIncidentOID", "FacilityOID", "IncidentOID", "Total_Miles", "Total_Kilometers", "Total_Minutes"]


def _ordered(row, names):
    """A row's fields in Pro's order (what ``fieldNames`` answers), the rest after."""
    out = {name: row[name] for name in names if name in row}
    out.update((k, v) for k, v in row.items() if k not in out)
    return out


def _break(value):
    """A cutoff as Pro writes it in a service area polygon's name: 0, 0.15, 5 (measured 2026-11-12)."""
    return f"{float(value):g}"


# ---- network datasets and travel modes ----------------------------------------------------------------------------

def MakeNetworkDatasetLayer(in_network_dataset, out_network_dataset_layer=None):
    """A network dataset layer, a name for the dataset held in this process; with no name, the dataset's own."""
    name = str(out_network_dataset_layer) if out_network_dataset_layer else _network.locate(str(in_network_dataset))[2]
    _network.LAYERS[name] = str(_network.LAYERS.get(str(in_network_dataset), in_network_dataset))
    from ._result import Result
    return Result(name)


def GetTravelModes(network_dataset):
    """The network's travel modes, by name."""
    from .na import GetTravelModes as travel_modes
    return travel_modes(network_dataset)


from .na import TravelMode  # noqa: E402,F401 — nax documents the same class
from .na import BuildNetwork, CalculateLocations, CreateNetworkDataset  # noqa: E402,F401 — nax documents the same tools
from .na import ExportNetworkTravelModes, ReplaceNetworkTravelModes  # noqa: E402,F401
from .na import NAClassFieldMap  # noqa: E402,F401


class NAClassFieldMappings(dict):
    """A solver input's field maps, by property name, as a solver's ``fieldMappings`` answers them."""


class CurbApproach(enum.IntEnum):
    Unknown = -1
    EitherSide = 0
    RightSide = 1
    LeftSide = 2
    NoUTurn = 3


class TimeOfDayUsage(enum.IntEnum):
    DepartureTime = 0
    ArrivalTime = 1


# ---- the network dataset query API ----------------------------------------------------------------------------------

class _Rows:
    """A network element cursor: iterates tuples, and supports ``with``."""

    def __init__(self, rows):
        self._rows = list(rows)

    def __iter__(self):
        return iter(self._rows)

    def __enter__(self):
        return self

    def __exit__(self, *_):
        return False


_EDGE_PROPERTIES = ("EID", "SOURCEID", "SOURCEOID", "FROMPOSITION", "TOPOSITION", "DIRECTION", "FROMJUNCTION",
                    "TOJUNCTION", "ISRESTRICTED", "COST", "TIME", "DISTANCE")
_JUNCTION_PROPERTIES = ("EID", "SOURCEID", "SOURCEOID", "ISRESTRICTED", "COST", "TIME", "DISTANCE")


def _names(value):
    if value is None:
        return []
    return [value] if isinstance(value, str) else list(value)


class NetworkDataset:
    """arcpy.nax.NetworkDataset: a built network read element by element, over grapy's network tables (ADR-226).
    ``edges()`` and ``junctions()`` answer the documented properties, then the values of the attributes named, under a
    travel mode. INFERRED: an edge is read once in each direction, ``DIRECTION`` True along its digitised direction, its
    positions and junctions in the direction of travel; every junction is a system junction here, with the system
    junction source's id, its own EID as SOURCEOID, no cost and no restriction. ``customEvaluators`` holds temporary
    custom evaluators for the solvers made from this object, and ``updateNetworkDatasetSchema(custom_evaluators=...)``
    persists them (ADR-246). Time of day, live traffic and custom directions are refused."""

    def __init__(self, in_network):
        self._path = str(_network.LAYERS.get(str(in_network), in_network))
        _network.open_network(self._path)[0].close()
        self._evaluators = []

    def __str__(self):
        return self._path

    def _query(self, sql, parameters=()):
        connection, _, _, name = _network.open_network(self._path)
        try:
            return connection.execute(sql, (name, *parameters)).fetchall()
        finally:
            connection.close()

    @property
    def isBuilt(self):
        return bool(self._query("select built from grapy_network where name = ?")[0][0])

    @property
    def buildTimestamp(self):
        import datetime
        text = self._query("select built_at from grapy_network where name = ?")[0][0]
        return datetime.datetime.strptime(text, "%Y-%m-%dT%H:%M:%S.%f") if text else None

    @property
    def edgeCount(self):
        """Edge elements, one for each direction of each edge - as ArcGIS Pro 3.7 counts them (measured 2026-11-12: 40
        street blocks, 80 edges) and as ``edges()`` reads them."""
        return 2 * int(self._query("select count(*) from grapy_network_edge where network = ?")[0][0])

    @property
    def junctionCount(self):
        return int(self._query("select count(*) from grapy_network_junction where network = ?")[0][0])

    @property
    def turnCount(self):
        from . import _turns
        return _turns.turn_count(self._path)

    @property
    def travelModes(self):
        return GetTravelModes(self._path)

    @property
    def customEvaluators(self):
        return list(self._evaluators)

    @customEvaluators.setter
    def customEvaluators(self, value):
        from ._evaluators import AttributeEvaluator
        evaluators = list(value or [])
        for evaluator in evaluators:
            if not isinstance(evaluator, AttributeEvaluator):
                raise TypeError("customEvaluators takes instances of subclasses of arcpy.nax.AttributeEvaluator.")
        self._evaluators = evaluators

    @property
    def directionsCustomizer(self):
        return None

    @directionsCustomizer.setter
    def directionsCustomizer(self, value):
        if value is not None:
            raise NotBuiltError("arcpy.nax: a directions customizer writes Esri's direction text, whose rules are not "
                                "published.")

    def describe(self):
        return _network.describe(self._path)

    def getDataSourceFromSourceID(self, source_id):
        file, _, _ = _network.locate(self._path)
        sources = [r[0] for r in self._query("select class_name from grapy_network_source where network = ? order by rowid")]
        if 1 <= int(source_id) <= len(sources):
            return f"{file}/{sources[int(source_id) - 1]}"
        raise ValueError(f"The network has no source {source_id}.")

    def checkIntersectingFeatures(self, feature_layer, cutoff):
        """True when the features intersect no more than ``cutoff`` edge source features, as documented."""
        import shapely
        from .management import _features
        graph = _network.Graph(self._path)
        area = shapely.union_all([s for _, _, s in _features(feature_layer, graph.srid) if s is not None])
        touched = {(e["source"], e["oid"]) for e in graph.edges if e["shape"].intersects(area)}
        return len(touched) <= int(cutoff)

    def _graph(self, travel_mode, time_of_day):
        if time_of_day is not None:
            raise NotBuiltError("arcpy.nax.NetworkDataset: time_of_day needs traffic, refused until it is defined "
                                "(ADR-226 §5.3).")
        return _network.Graph(self._path, travel_mode, self._evaluators)

    @staticmethod
    def _check(properties, allowed, travel_mode):
        for p in properties:
            if str(p).upper() not in allowed:
                raise ValueError(f"'{p}' is not a property this cursor reads.")
            if str(p).upper() in ("ISRESTRICTED", "COST", "TIME", "DISTANCE") and travel_mode is None:
                raise ValueError(f"'{p}' needs a travel_mode.")

    @staticmethod
    def _within(area_of_interest):
        if area_of_interest is None:
            return None
        shape = getattr(area_of_interest, "_shape", None)
        if shape is None and hasattr(area_of_interest, "polygon"):
            shape = area_of_interest.polygon._shape
        return shape

    def edges(self, edge_properties=None, attribute_names=None, time_of_day=None, eids=None, travel_mode=None,
              area_of_interest=None):
        properties, attributes = _names(edge_properties), _names(attribute_names)
        self._check(properties, _EDGE_PROPERTIES, travel_mode)
        graph = self._graph(travel_mode, time_of_day)
        wanted = None if eids is None else {int(e) for e in _names(eids)}
        area = self._within(area_of_interest)
        mode = graph.mode
        rows = []
        for i, edge in enumerate(graph.edges):
            if wanted is not None and edge["edge_id"] not in wanted:
                continue
            if area is not None and not edge["shape"].intersects(area):
                continue
            for forward in (True, False):
                values = {"EID": edge["edge_id"], "SOURCEID": graph.sources.index(edge["source"]) + 1,
                          "SOURCEOID": edge["oid"], "FROMPOSITION": edge["p0"] if forward else edge["p1"],
                          "TOPOSITION": edge["p1"] if forward else edge["p0"], "DIRECTION": forward,
                          "FROMJUNCTION": edge["u"] if forward else edge["v"],
                          "TOJUNCTION": edge["v"] if forward else edge["u"],
                          "ISRESTRICTED": not graph.passable(i, forward),
                          "COST": graph.cost(graph.impedance, i, forward),
                          "TIME": graph.cost(mode["timeAttributeName"], i, forward) if mode.get("timeAttributeName")
                          else None,
                          "DISTANCE": graph.cost(mode["distanceAttributeName"], i, forward)
                          if mode.get("distanceAttributeName") else None}
                row = [values[str(p).upper()] for p in properties]
                for attribute in attributes:
                    usage = graph.attributes[attribute][0]
                    row.append(graph.restriction_values.get((attribute, i, forward), False) if usage == "Restriction"
                               else graph.cost(attribute, i, forward))
                rows.append(tuple(row))
        return _Rows(rows)

    def junctions(self, junction_properties=None, attribute_names=None, time_of_day=None, eids=None, travel_mode=None,
                  area_of_interest=None):
        from shapely.geometry import Point as ShapelyPoint
        properties, attributes = _names(junction_properties), _names(attribute_names)
        self._check(properties, _JUNCTION_PROPERTIES, travel_mode)
        graph = self._graph(travel_mode, time_of_day)
        wanted = None if eids is None else {int(e) for e in _names(eids)}
        area = self._within(area_of_interest)
        system = len(graph.sources) + 1
        rows = []
        for eid, (x, y) in sorted(graph.junctions.items()):
            if wanted is not None and eid not in wanted:
                continue
            if area is not None and not area.intersects(ShapelyPoint(x, y)):
                continue
            values = {"EID": eid, "SOURCEID": system, "SOURCEOID": eid, "ISRESTRICTED": False, "COST": 0.0,
                      "TIME": 0.0 if graph.mode.get("timeAttributeName") else None,
                      "DISTANCE": 0.0 if graph.mode.get("distanceAttributeName") else None}
            row = [values[str(p).upper()] for p in properties]
            row += [False if graph.attributes[a][0] == "Restriction" else 0.0 for a in attributes]
            rows.append(tuple(row))
        return _Rows(rows)

    def turns(self, turn_properties=None, attribute_names=None, eids=None, travel_mode=None, area_of_interest=None):
        """The network's turn features, by the documented properties (EID, SOURCEID, SOURCEOID, EDGEEIDS,
        ISRESTRICTED, COST, TIME, DISTANCE) and the attributes named, under a travel mode (ADR-239). INFERRED: a turn
        source's id numbers after the system junction source's."""
        from . import _turns
        properties, attributes = _names(turn_properties), _names(attribute_names)
        allowed = ("EID", "SOURCEID", "SOURCEOID", "EDGEEIDS", "ISRESTRICTED", "COST", "TIME", "DISTANCE")
        self._check(properties, allowed, travel_mode)
        if eids is not None and area_of_interest is not None:
            raise ValueError("eids and area_of_interest cannot be used together.")
        graph = self._graph(travel_mode, None)
        wanted = None if eids is None else {int(e) for e in _names(eids)}
        return _Rows(_turns.turn_rows(graph, properties, attributes, wanted, self._within(area_of_interest)))

    def getLiveTrafficStatus(self, wait_for_update=False):
        raise NotBuiltError("arcpy.nax.NetworkDataset: live traffic is refused (ADR-226 §5.3).")

    def updateTrafficCache(self, timeout_seconds=None):
        raise NotBuiltError("arcpy.nax.NetworkDataset: live traffic is refused (ADR-226 §5.3).")

    def updateNetworkDatasetSchema(self, custom_evaluators=None, custom_directions=None, openlr_traffic_service_id=None,
                                   openlr_traffic_feed_ids=None, travel_modes=None):
        """``travel_modes`` - each travel mode given added or replaced - and ``custom_evaluators`` - persisted custom
        evaluators, {attribute: {"class": "folder.module.Class", "sourceNames": [...]}} (ADR-246). INFERRED: the
        others are kept. Custom directions and live traffic are refused."""
        if custom_directions or openlr_traffic_service_id or openlr_traffic_feed_ids:
            raise NotBuiltError("arcpy.nax: custom directions write Esri's direction text, whose rules are not published, "
                                "and live traffic is refused (ADR-226 §5.3).")
        if custom_evaluators:
            from ._evaluators import persist
            persist(self._path, custom_evaluators)
        for name, mode in (travel_modes or {}).items():
            travel = TravelMode(mode)
            _network.add_travel_mode(self._path, name, travel.impedance, travel.restrictions, travel.timeAttributeName,
                                     travel.distanceAttributeName, None, travel.type, travel.description)


# ---- solver objects -----------------------------------------------------------------------------------------------

class _Cursor:
    def __init__(self, solver, input_type, field_names):
        self._solver, self._type, self._fields = solver, input_type, list(field_names)

    def insertRow(self, row):
        self._solver._add(self._type, dict(zip(self._fields, row)))

    def __enter__(self):
        return self

    def __exit__(self, *_):
        return False


class _Solver:
    _INPUT_TYPES: tuple = ()

    def __init__(self, in_network):
        self._evaluators = list(getattr(in_network, "_evaluators", None) or [])   # a NetworkDataset's temporary ones
        source = str(in_network)
        if source.lower().startswith(("http://", "https://")) or isinstance(in_network, dict):
            raise NotBuiltError("A portal or ArcGIS Server routing service reaches a network, and a fenced tool has none "
                                "(ADR-223 §5.3).")
        self.networkDataSource = str(_network.LAYERS.get(source, source))
        _network.locate(self.networkDataSource)
        self.travelMode = None
        self.accumulateAttributeNames = []
        self.allowAutoRelocate = True
        self.ignoreInvalidLocations = True
        self.searchTolerance = 5000
        self.searchToleranceUnits = DistanceUnits.Meters
        self.timeUnits = TimeUnits.Minutes
        self.distanceUnits = DistanceUnits.Kilometers
        self.timeOfDay = None
        self.timeZone = TimeZoneUsage.LocalTimeAtLocations
        self._inputs = {t: [] for t in self._INPUT_TYPES}

    # inputs

    def _check_type(self, input_type):
        if input_type not in self._inputs:
            raise ValueError(f"{input_type} is not an input of {type(self).__name__}.")
        if "Barriers" in input_type.name:
            raise NotBuiltError(f"arcpy.nax: {input_type.name} are not built in grapy yet.")

    def _add(self, input_type, values):
        self._check_type(input_type)
        from .da import _shape_from
        tokens = [k for k in values if str(k).upper().startswith("SHAPE@")]
        shapes = {str(k).upper(): values.pop(k) for k in tokens}
        shape = next((v for v in shapes.values() if v is not None), None)
        if shapes.get("SHAPE@JSON") is not None:                  # the documented geometry tokens (ADR-239)
            import json
            from ._geometry import AsShape
            shape = AsShape(json.loads(shapes["SHAPE@JSON"]), True)
        point = _shape_from(shape)
        rows = self._inputs[input_type]
        oid = len(rows) + 1
        values.setdefault("Name", None)
        rows.append({"oid": oid, "point": point, **values})

    def load(self, input_type, features, field_mappings=None, append=True, max_features=None):
        """Point features loaded as an input, their fields mapped by name (or by ``field_mappings``, a dictionary of
        property → NAClassFieldMap)."""
        from . import da
        from .management import _schema, _source
        self._check_type(input_type)
        if not append:
            self._inputs[input_type] = []
        path, where = _source(features)
        columns = [c.name for c in _schema(path)[0]]
        mapping = field_mappings or {}
        with da.SearchCursor(path, [*columns, "OID@", "SHAPE@"], where) as rows:
            for n, row in enumerate(rows):
                if max_features is not None and n >= int(max_features):
                    break
                values = dict(zip(columns, row[:-2]))
                lower = {k.lower(): v for k, v in values.items()}
                record = {k: v for k, v in values.items()}
                for prop, field_map in mapping.items():
                    # The mapped field's value; its defaultValue where the input has no such field or it is null - a
                    # property maps to its own name by default, which most inputs do not hold.
                    value = lower.get(str(field_map.mappedFieldName).lower()) if field_map.mappedFieldName else None
                    record[prop] = field_map.defaultValue if value is None else value
                record["SHAPE@"] = row[-1]
                record["_source_oid"] = row[-2]       # Pro's FacilityOID, IncidentOID, FirstStopOID ... (2026-11-12)
                self._add(input_type, record)

    def insertCursor(self, input_type, field_names, append=True):
        self._check_type(input_type)
        if not append:
            self._inputs[input_type] = []
        return _Cursor(self, input_type, field_names)

    def count(self, input_type):
        self._check_type(input_type)
        return len(self._inputs[input_type])

    def _properties(self, input_type):
        measured = _PRO_INPUT_FIELDS.get(type(self).__name__)
        if measured is not None:
            return list(measured.get(input_type.name, []))
        return ["Name", *[f for f in self._INPUT_FIELDS.get(input_type.name, []) if f != "Name"]]

    def fieldNames(self, input_type, use_location_fields=False):
        """The input's properties, in ArcGIS Pro's order and without ObjectID or Shape, as Pro 3.7 answers them
        (measured 2026-11-12); with ``use_location_fields``, its network location fields too."""
        self._check_type(input_type)
        names = self._properties(input_type)
        return _with_locations(names) if use_location_fields else names

    def fieldMappings(self, input_type, use_location_fields=False, list_candidate_fields=None):
        """The input's properties, each mapped to the field of its own name with its default value - whatever the
        candidate fields, as ArcGIS Pro 3.7 answers (measured 2026-11-12: a Route's Stops map AdditionalCost to
        'AdditionalCost', default 0, among candidates with no such field). Location fields are not built: grapy
        locates every input when it solves."""
        if use_location_fields:
            raise NotBuiltError("arcpy.nax: loading network location fields is not built in grapy; grapy locates every "
                                "input.")
        return NAClassFieldMappings({name: NAClassFieldMap(name, _MAP_DEFAULTS.get(name))
                                     for name in self.fieldNames(input_type)})

    def setLocateSettingsOverrides(self, input_type, search_sources=None, allow_auto_relocate=None, search_tolerance=None,
                                   search_tolerance_units=None):
        raise NotBuiltError("arcpy.nax: setLocateSettingsOverrides is not built in grapy; set searchTolerance on the solver.")

    def addFields(self, input_type, field_description):
        self._check_type(input_type)

    # solving

    def _graph(self):
        from .na import _refuse_mode
        if self.timeOfDay is not None:
            raise NotBuiltError("arcpy.nax: timeOfDay needs traffic, refused until it is defined (ADR-226 §5.3).")
        _refuse_mode(self.travelMode)
        return _network.Graph(self.networkDataSource, self.travelMode, self._evaluators)

    def _located(self, graph, input_type, prefix):
        per = _network._per_unit(graph.srid) or 1.0
        tolerance = float(self.searchTolerance) * _network._LINEAR[self.searchToleranceUnits.name.upper()] / per
        items = []
        for row in self._inputs[input_type]:
            for field in ("CurbApproach", "LocationType"):      # as arcpy.na.Solve refuses them (na._items)
                value = getattr(row.get(field), "value", row.get(field))
                if value not in (None, 0) and not (field == "CurbApproach" and value == 3 and
                                                   isinstance(self, (ClosestFacility, OriginDestinationCostMatrix))):
                    raise NotBuiltError(f"arcpy.nax.{type(self).__name__}: {field} {value} is not built in grapy: its "
                                        "solvers approach every location from either side and take no waypoints.")
            located = graph.locate(row["point"], tolerance) if row["point"] is not None else None
            items.append({**row, "key": (prefix, row["oid"]), "name": row.get("Name") or f"Location {row['oid']}",
                          "located": located})
        if not self.ignoreInvalidLocations and any(i["located"] is None for i in items):
            raise ExecuteError("ERROR 030024: an input is not located on the network, and invalid locations halt the solve.")
        return items

    def _say(self):
        from ._runtime import AddWarning, _SAID_EQUIVALENT
        key = f"nax.{type(self).__name__}"
        if key not in _SAID_EQUIVALENT:
            _SAID_EQUIVALENT.add(key)
            AddWarning(f"{key}.solve is an equivalent, not ArcGIS Pro's own computation: {_network.DIJKSTRA[0]} "
                       f"({_network.DIJKSTRA[1]}). Its numbers may differ from Pro's.")

    def _totals(self, graph, totals, prefix):
        """The documented Total_/Cumul_ fields for a row's totals by attribute."""
        mode = graph.mode
        units = {a: u for a, (_, u) in graph.attributes.items()}
        time_attribute = mode.get("timeAttributeName") or (graph.impedance if _kind(units[graph.impedance]) == "time" else None)
        distance_attribute = mode.get("distanceAttributeName") or (
            graph.impedance if _kind(units[graph.impedance]) == "distance" else None)
        out = {}
        if time_attribute:
            minutes = totals.get(time_attribute, 0.0) * _MINUTES[str(units[time_attribute]).upper()]
            out[f"{prefix}_Minutes"] = minutes
            if self.timeUnits is not TimeUnits.Minutes:
                out[f"{prefix}_{self.timeUnits.name}"] = minutes / _MINUTES[self.timeUnits.name.upper()]
        if distance_attribute:
            metres = totals.get(distance_attribute, 0.0) * _network._LINEAR[str(units[distance_attribute]).upper()]
            out[f"{prefix}_Miles"] = metres / _network._LINEAR["MILES"]  # a US survey mile, as Pro's (D-323)
            out[f"{prefix}_Kilometers"] = metres / 1000.0
            if self.distanceUnits not in (DistanceUnits.Miles, DistanceUnits.Kilometers):
                out[f"{prefix}_{self.distanceUnits.name}"] = metres / _network._LINEAR[self.distanceUnits.name.upper()]
        if _kind(units[graph.impedance]) == "other":
            out[f"{prefix}_Other" if prefix == "Total" else "Cumul_Cost"] = totals.get(graph.impedance, 0.0)
        for attribute in self.accumulateAttributeNames:
            kind = _kind(units[attribute])
            value = totals.get(attribute, 0.0)
            if kind == "time":
                out[f"{prefix}_{attribute}_{self.timeUnits.name}"] = \
                    value * _MINUTES[str(units[attribute]).upper()] / _MINUTES[self.timeUnits.name.upper()]
            elif kind == "distance":
                out[f"{prefix}_{attribute}_{self.distanceUnits.name}"] = \
                    value * _network._LINEAR[str(units[attribute]).upper()] / _network._LINEAR[self.distanceUnits.name.upper()]
            else:
                out[f"{prefix}_{attribute}_Other"] = value
        return out

    def _impedance_per(self, graph):
        """How many of the impedance attribute's units one of the solver's cutoff units is: a cutoff is in timeUnits
        when the impedance is a time, in distanceUnits when it is a distance, as ArcGIS Pro 3.7 reads it (measured
        2026-11-12: a service area cutoff of 0.25 on a Length impedance in metres reached 250 m)."""
        units = str(graph.attributes[graph.impedance][1] or "")
        kind = _kind(units)
        if kind == "time":
            return _MINUTES[self.timeUnits.name.upper()] / _MINUTES[units.upper().replace(" ", "")]
        if kind == "distance":
            return _network._LINEAR[self.distanceUnits.name.upper()] / _network._LINEAR[units.upper().replace(" ", "")]
        return 1.0

    def _cutoff(self, graph, value):
        return None if value is None else float(value) * self._impedance_per(graph)

    def _impedance_totals(self, graph, totals, prefix, both=False):
        """The ``prefix``_ fields of the impedance attribute alone - as Pro writes a location-allocation facility's
        Total_ and TotalWeighted_ (in the solver's unit), and, with ``both``, a service area line's FromCumul_ and
        ToCumul_ (Miles and Kilometers both for a distance)."""
        units = str(graph.attributes[graph.impedance][1] or "")
        kind = _kind(units)
        out = self._totals(graph, {**{a: 0.0 for a in graph.attributes}, **totals}, prefix)
        if kind == "time":
            keep = {f"{prefix}_Minutes", f"{prefix}_{self.timeUnits.name}"}
        elif kind == "distance":
            keep = {f"{prefix}_{self.distanceUnits.name}"} | ({f"{prefix}_Miles", f"{prefix}_Kilometers"} if both else set())
        else:
            keep = {f"{prefix}_Other"}
        found = {}
        for k, v in out.items():
            if k in keep:
                found[k] = v
        if kind == "time" or kind == "distance":
            return found
        return {f"{prefix}_Other" if prefix == "Total" else f"{prefix}_Cost": totals.get(graph.impedance, 0.0)}

    @staticmethod
    def _sides(graph, legs):
        """{(stop key, 'arrive' | 'depart'): CurbApproach} of the legs a route travelled."""
        out = {}
        for a, b, arcs in legs:
            if not arcs:
                continue
            for item, (i, t0, t1), what in ((a, arcs[0], "depart"), (b, arcs[-1], "arrive")):
                side = graph.location_fields(item["located"], item["point"])["SideOfEdge"] if item["located"] else None
                out[(item["key"], what)] = _curb(side, t1 > t0 if t1 != t0 else None)
        return out

    def _traverse(self, graph, routes, outputs, wanted):
        """What the routes traversed (ADR-246): each output asked for, written from the paths the solve kept."""
        from . import _traversal
        traversal = _traversal.of_routes(graph, routes)
        rows = _traversal.result_rows(traversal, self._totals)
        for kind, (asked, output_type) in wanted.items():
            if asked:
                outputs[output_type] = rows[kind]
        return traversal

    def _inputs_out(self, items, extra=None, graph=None, input_type=None, oid_field=None):
        """An output that echoes an input: its properties (a row's own values, else their defaults), its network
        location (SourceID, SourceOID, PosAlong, SideOfEdge, SnapX, SnapY, SnapZ, DistanceToNetworkInMeters) and
        Status, the ObjectID of the feature it was loaded from under ``oid_field`` (None from an insertCursor), and the
        solve's ``extra`` fields - Pro's fields, measured in ArcGIS Pro 3.7, 2026-11-12."""
        rows = []
        properties = self._properties(input_type) if input_type is not None else []
        for item in items:
            given = {k: v for k, v in item.items() if k not in ("key", "located", "point", "oid", "name")
                     and not str(k).startswith("_")}
            row = {"ObjectID": item["oid"]}
            for name in properties:
                if name in _NOT_ECHOED:
                    given.pop(name, None)
                    continue
                value = given.pop(name, None)
                value = _DEFAULTS.get(name) if value is None else value
                row[name] = int(value) if isinstance(value, enum.Enum) else value
            row["Name"] = item["name"]
            row.update(given)
            if graph is not None:
                location = graph.location_fields(item["located"], item["point"])
                row.update({k: v for k, v in location.items() if not k.startswith("_")})
                row["SnapZ"] = 0.0 if item["located"] else None
            row["Status"] = 0 if item["located"] else 1
            if oid_field:
                row[oid_field] = item.get("_source_oid")
            row["SHAPE"] = item["point"]
            row.update((extra or {}).get(item["key"], {}))
            rows.append(row)
        return rows


class _Result:
    """A solve's outputs, by output type: counted, read with a cursor, exported to a feature class."""

    def __init__(self, outputs, srid, succeeded, traversal=None):
        self._outputs, self._srid = outputs, srid
        self._traversal = traversal
        self.solveSucceeded = succeeded
        self.isPartialSolution = False

    @property
    def spatialReference(self):
        from ._reference import SpatialReference
        return SpatialReference(self._srid) if self._srid > 0 else None

    def _rows(self, output_type):
        if output_type not in self._outputs:
            raise NotBuiltError(f"arcpy.nax: the output {getattr(output_type, 'name', output_type)} is not built in grapy "
                                "(directions are not: Esri's direction rules are not published).")
        return self._outputs[output_type]

    def count(self, output_type):
        return len(self._outputs.get(output_type, []))

    def fieldNames(self, output_type):
        """ObjectID and the output's fields; no Shape, as ArcGIS Pro 3.7 answers (measured 2026-11-12)."""
        rows = self._rows(output_type)
        names = ["ObjectID"]
        for row in rows:
            for name in row:
                if name not in names and name != "SHAPE" and not name.startswith("_"):
                    names.append(name)
        return names

    def searchCursor(self, output_type, field_names, where_clause=None):
        """The rows of an output: fields by name, ``OID@``, and the shape tokens ``SHAPE@``, ``SHAPE@XY``,
        ``SHAPE@WKT``, ``SHAPE@WKB`` and ``SHAPE@JSON``. A where clause is not built."""
        from .da import _shape_token
        if where_clause:
            raise NotBuiltError("arcpy.nax: a where clause on a result cursor is not built in grapy.")
        rows = self._rows(output_type)
        fields = [field_names] if isinstance(field_names, str) else list(field_names)
        reference = self.spatialReference
        out = []
        for n, row in enumerate(rows, start=1):
            values = []
            for field in fields:
                upper = field.upper()
                if upper in ("OID@", "OBJECTID"):
                    values.append(row.get("ObjectID", n))
                elif upper.startswith("SHAPE@"):
                    values.append(_shape_token(upper, row.get("SHAPE"), reference))
                else:
                    values.append(row.get(field))
            out.append(tuple(values))
        return _Rows(out)                     # a context manager too, as Pro's cursor is (measured 2026-11-12)

    def export(self, output_type, output_features):
        from . import da
        from .management import _path
        rows = self._rows(output_type)
        names = [f for f in self.fieldNames(output_type) if f != "ObjectID"]
        kinds = {}
        for row in rows:
            for name in names:
                value = row.get(name)
                if value is not None and name not in kinds:
                    kinds[name] = "Double" if isinstance(value, float) else "Integer" if isinstance(value, int) else                         "Date" if isinstance(value, datetime.datetime) else "String"
        shapes = {r["SHAPE"].geom_type for r in rows if r.get("SHAPE") is not None}
        shape = "Polygon" if shapes & {"Polygon", "MultiPolygon"} else "Polyline" if shapes & {"LineString", "MultiLineString"} \
            else "Point" if shapes else "Geometry"
        out = _path(str(output_features))
        _gpkg.create(out, shape, self._srid if shape != "Geometry" else 0,
                     [_gpkg.Column(n, kinds.get(n, "String")) for n in names])
        with da.InsertCursor(out, [*names, "SHAPE@"]) as written:
            for row in rows:
                # A time of day (a waste collection route's StartTime) is written as its text: no field type holds one.
                written.insertRow((*[v.isoformat() if isinstance(v, datetime.time) else v
                                     for v in (row.get(n) for n in names)], row.get("SHAPE")))

    def extent(self, output_type=None):
        import shapely
        from ._reference import Extent
        rows = [r for t in ([output_type] if output_type else list(self._outputs)) for r in self._outputs.get(t, [])]
        shapes = [r["SHAPE"] for r in rows if r.get("SHAPE") is not None]
        if not shapes:
            raise RuntimeError("The solve produced no shapes.")
        return Extent(*shapely.union_all(shapes).bounds, self.spatialReference)

    def solverMessages(self, severity):
        return []

    def saveAsLayerFile(self, file_name):
        raise NotBuiltError("arcpy.nax: saveAsLayerFile writes an ArcGIS Pro layer file, which a server tool does not hold.")

    def saveRouteData(self, file_name):
        raise NotBuiltError("arcpy.nax: saveRouteData writes Esri's route data package, whose format is not published.")


_EQUIVALENT = f"{_network.DIJKSTRA[0]} ({_network.DIJKSTRA[1]})"


class Route(_Solver):
    """EQUIVALENT: routes through stops by Dijkstra's shortest path (ADR-226 §5.4); see ``_network.solve_route``.
    ``findBestSequence`` (with ``preserveFirstStop`` and ``preserveLastStop``) and ``returnToStart`` solve the
    travelling salesman problem by ``_network.best_order`` (ADR-239). ``returnDirections`` is refused (Esri's direction
    rules are not published). ``returnRouteEdges``, ``returnRouteJunctions`` and ``returnRouteTurns`` keep what the
    routes traversed as RouteEdges, RouteJunctions and RouteTurns (ADR-246), True by default as documented for a
    network dataset."""

    __grapy_equivalent__ = _EQUIVALENT
    _INPUT_TYPES = tuple(RouteInputDataType)
    _INPUT_FIELDS = {"Stops": ["RouteName", "Sequence", "CurbApproach", "LocationType"]}

    def __init__(self, in_network):
        super().__init__(in_network)
        self.findBestSequence = False
        self.preserveFirstStop = False
        self.preserveLastStop = False
        self.returnToStart = False
        self.returnDirections = False
        self.returnRouteEdges = self.returnRouteJunctions = self.returnRouteTurns = True
        self.routeShapeType = RouteShapeType.TrueShapeWithMeasures

    def solve(self):
        if self.returnDirections:
            raise NotBuiltError("arcpy.nax.Route: returnDirections writes Esri's direction text, whose rules are not "
                                "published.")
        self._say()
        graph = self._graph()
        stops = self._located(graph, RouteInputDataType.Stops, "Stops")
        shape = {RouteShapeType.NoLine: "NO_LINES", RouteShapeType.StraightLine: "STRAIGHT_LINES"}.get(
            self.routeShapeType, "ALONG_NETWORK")
        order = "USE_CURRENT_ORDER"
        if self.findBestSequence:
            order = {(True, True): "PRESERVE_BOTH", (True, False): "PRESERVE_FIRST", (False, True): "PRESERVE_LAST",
                     (False, False): "FIND_BEST_ORDER"}[(bool(self.preserveFirstStop), bool(self.preserveLastStop))]
        routes, results = _network.solve_route(graph, stops, [], shape, order, bool(self.returnToStart))
        source = {s["oid"]: s.get("_source_oid") for s in stops}
        sides = {}
        for n, route in enumerate(routes, start=1):
            route["ObjectID"] = n
            route.pop(f"Total_{graph.impedance}", None)      # before the totals: an impedance may be named Minutes
            route.update(self._totals(graph, route.pop("_totals"), "Total"))
            # Pro's fields (measured 2026-11-12): the stops' source ObjectIDs (None from an insertCursor), and times,
            # waits and violations, None in a time-neutral solve.
            route["FirstStopOID"], route["LastStopOID"] = source.get(route["FirstStopID"]), source.get(route["LastStopID"])
            route.update(StartTime=None, EndTime=None, StartTimeUTC=None, EndTimeUTC=None, TotalWait_Minutes=None,
                         TotalViolation_Minutes=None)
            sides.update(self._sides(graph, route.get("_legs", [])))
        routes[:] = [_ordered(route, _ROUTE_FIELDS) for route in routes]
        extra = {}
        for key, result in results.items():
            cumul = result.pop("_cumul", {})
            result.pop(f"Cumul_{graph.impedance}", None)
            extra[key] = {**result, **(self._totals(graph, cumul, "Cumul") if cumul else {}),
                          "ArriveCurbApproach": sides.get((key, "arrive")),
                          "DepartCurbApproach": sides.get((key, "depart")), "ArriveTime": None, "DepartTime": None,
                          "ArriveTimeUTC": None, "DepartTimeUTC": None, "Wait_Minutes": None, "CumulWait_Minutes": None,
                          "Violation_Minutes": None, "CumulViolation_Minutes": None}
        echoed = self._inputs_out(stops, extra, graph, RouteInputDataType.Stops, "ORIG_FID")
        outputs = {RouteOutputDataType.Routes: routes, RouteOutputDataType.Stops: echoed}
        traversal = self._traverse(graph, routes, outputs, {
            "edges": (self.returnRouteEdges, RouteOutputDataType.RouteEdges),
            "junctions": (self.returnRouteJunctions, RouteOutputDataType.RouteJunctions),
            "turns": (self.returnRouteTurns, RouteOutputDataType.RouteTurns)})
        return _Result(outputs, graph.srid, bool(routes), traversal)


class ClosestFacility(_Solver):
    """EQUIVALENT: the closest facilities by Dijkstra's shortest path (ADR-226 §5.4); see
    ``_network.solve_closest_facility``."""

    __grapy_equivalent__ = _EQUIVALENT
    _INPUT_TYPES = tuple(ClosestFacilityInputDataType)
    _INPUT_FIELDS = {"Incidents": ["TargetFacilityCount", "CurbApproach"], "Facilities": ["CurbApproach"]}

    def __init__(self, in_network):
        super().__init__(in_network)
        self.defaultTargetFacilityCount = 1
        self.defaultImpedanceCutoff = None
        self.travelDirection = TravelDirection.ToFacility
        self.routeShapeType = RouteShapeType.TrueShapeWithMeasures
        self.returnDirections = False

    def solve(self):
        if self.returnDirections:
            raise NotBuiltError("arcpy.nax.ClosestFacility: returnDirections writes Esri's direction text, whose rules are "
                                "not published.")
        self._say()
        graph = self._graph()
        facilities = self._located(graph, ClosestFacilityInputDataType.Facilities, "Facilities")
        incidents = self._located(graph, ClosestFacilityInputDataType.Incidents, "Incidents")
        shape = {RouteShapeType.NoLine: "NO_LINES", RouteShapeType.StraightLine: "STRAIGHT_LINES"}.get(
            self.routeShapeType, "ALONG_NETWORK")
        direction = "FROM_FACILITIES" if self.travelDirection is TravelDirection.FromFacility else "TO_FACILITIES"
        routes = _network.solve_closest_facility(graph, facilities, incidents, direction,
                                                 self._cutoff(graph, self.defaultImpedanceCutoff),
                                                 self.defaultTargetFacilityCount, [], shape)
        by_oid = {("F", f["oid"]): f for f in facilities} | {("I", i["oid"]): i for i in incidents}
        for n, route in enumerate(routes, start=1):
            route["ObjectID"] = n
            route.pop(f"Total_{graph.impedance}", None)
            route.update(self._totals(graph, route.pop("_totals"), "Total"))
            # Pro's fields (measured 2026-11-12): FacilityID and IncidentID are the inputs' own ID property, the
            # output rows' ObjectIDs are OutputFacilityOID and OutputIncidentOID, FacilityOID and IncidentOID the
            # features' they were loaded from; the curb approaches are the sides the locations are on.
            facility, incident = by_oid[("F", route.pop("FacilityID"))], by_oid[("I", route.pop("IncidentID"))]
            sides = self._sides(graph, route.get("_legs", []))
            route.update(FacilityID=facility.get("ID"), IncidentID=incident.get("ID"),
                         OutputFacilityOID=facility["oid"], OutputIncidentOID=incident["oid"],
                         FacilityOID=facility.get("_source_oid"), IncidentOID=incident.get("_source_oid"),
                         IncidentCurbApproach=sides.get((incident["key"], "depart"), sides.get((incident["key"], "arrive"))),
                         FacilityCurbApproach=sides.get((facility["key"], "arrive"), sides.get((facility["key"], "depart"))),
                         StartTime=None, EndTime=None, StartTimeUTC=None, EndTimeUTC=None)
        routes[:] = [_ordered(route, _CLOSEST_FIELDS) for route in routes]
        outputs = {ClosestFacilityOutputDataType.Routes: routes,
                   ClosestFacilityOutputDataType.Facilities: self._inputs_out(
                       facilities, None, graph, ClosestFacilityInputDataType.Facilities, "FacilityOID"),
                   ClosestFacilityOutputDataType.Incidents: self._inputs_out(
                       incidents, None, graph, ClosestFacilityInputDataType.Incidents, "IncidentOID")}
        # Pro's ClosestFacility has no RouteEdges output (measured 2026-11-12); the traversal is kept for
        # arcpy.graticula.TraversedElements.
        from . import _traversal
        return _Result(outputs, graph.srid, bool(routes), _traversal.of_routes(graph, routes))


class OriginDestinationCostMatrix(_Solver):
    """EQUIVALENT: the cost from each origin to each destination by Dijkstra's shortest path (ADR-226 §5.4); see
    ``_network.solve_od_cost_matrix``."""

    __grapy_equivalent__ = _EQUIVALENT
    _INPUT_TYPES = tuple(OriginDestinationCostMatrixInputDataType)
    _INPUT_FIELDS = {"Origins": ["TargetDestinationCount", "CurbApproach"], "Destinations": ["CurbApproach"]}

    def __init__(self, in_network):
        super().__init__(in_network)
        self.defaultDestinationCount = None
        self.defaultImpedanceCutoff = None
        self.lineShapeType = LineShapeType.StraightLine

    def solve(self):
        self._say()
        graph = self._graph()
        origins = self._located(graph, OriginDestinationCostMatrixInputDataType.Origins, "Origins")
        destinations = self._located(graph, OriginDestinationCostMatrixInputDataType.Destinations, "Destinations")
        lines = _network.solve_od_cost_matrix(graph, origins, destinations,
                                              self._cutoff(graph, self.defaultImpedanceCutoff),
                                              self.defaultDestinationCount, [],
                                              "NO_LINES" if self.lineShapeType is LineShapeType.NoLine else "STRAIGHT_LINES")
        by_oid = {("O", o["oid"]): o for o in origins} | {("D", d["oid"]): d for d in destinations}
        counts = {}
        out = []
        for n, line in enumerate(lines, start=1):
            # Pro's fields (measured 2026-11-12): no Name; Total_Time and Total_Distance in the solver's timeUnits and
            # distanceUnits; the inputs' names; OriginOID and DestinationOID the features' they were loaded from.
            origin, destination = by_oid[("O", line["OriginID"])], by_oid[("D", line["DestinationID"])]
            totals = self._totals(graph, line.pop("_totals"), "Total")
            row = {"ObjectID": n, "DestinationRank": line["DestinationRank"],
                   "Total_Time": totals.get(f"Total_{self.timeUnits.name}"),
                   "Total_Distance": totals.get(f"Total_{self.distanceUnits.name}"),
                   "OriginID": origin["oid"], "OriginOID": origin.get("_source_oid"), "OriginName": origin["name"],
                   "DestinationID": destination["oid"], "DestinationOID": destination.get("_source_oid"),
                   "DestinationName": destination["name"], "SHAPE": line.get("SHAPE")}
            counts[origin["key"]] = counts.get(origin["key"], 0) + 1
            counts[destination["key"]] = counts.get(destination["key"], 0) + 1
            out.append(row)
        return _Result({OriginDestinationCostMatrixOutputDataType.Lines: out,
                        OriginDestinationCostMatrixOutputDataType.Origins: self._inputs_out(
                            origins, {o["key"]: {"DestinationCount": counts.get(o["key"], 0)} for o in origins}, graph,
                            OriginDestinationCostMatrixInputDataType.Origins, "OriginOID"),
                        OriginDestinationCostMatrixOutputDataType.Destinations: self._inputs_out(
                            destinations, {d["key"]: {"OriginCount": counts.get(d["key"], 0)} for d in destinations},
                            graph, OriginDestinationCostMatrixInputDataType.Destinations, "DestinationOID")},
                       graph.srid, bool(lines))


ODCostMatrix = OriginDestinationCostMatrix


class ServiceArea(_Solver):
    """EQUIVALENT: the network reached within each cutoff by Dijkstra's shortest path, and polygons around it (ADR-226
    §5.4); see ``_network.solve_service_area``. With no cutoffs set, 5, 10 and 15, as ArcGIS Pro 3.7's
    ``defaultImpedanceCutoffs`` answers (measured 2026-11-12); cutoffs are in timeUnits or distanceUnits, as the
    impedance is a time or a distance."""

    __grapy_equivalent__ = _EQUIVALENT
    _INPUT_TYPES = tuple(ServiceAreaInputDataType)
    _INPUT_FIELDS = {"Facilities": ["CurbApproach"]}

    def __init__(self, in_network):
        super().__init__(in_network)
        self.defaultImpedanceCutoffs = [5.0, 10.0, 15.0]
        self.travelDirection = TravelDirection.FromFacility
        self.outputType = ServiceAreaOutputType.Polygons
        self.geometryAtCutoff = ServiceAreaPolygonCutoffGeometry.Rings
        self.geometryAtOverlap = ServiceAreaOverlapGeometry.Overlap
        self.polygonDetail = ServiceAreaPolygonDetail.Standard
        self.polygonBufferDistance = 100
        self.polygonBufferDistanceUnits = DistanceUnits.Meters
        self.excludeSourcesFromPolygonGeneration = []

    def solve(self):
        if self.geometryAtOverlap is not ServiceAreaOverlapGeometry.Overlap or \
                self.polygonDetail is not ServiceAreaPolygonDetail.Standard or self.excludeSourcesFromPolygonGeneration:
            raise NotBuiltError("arcpy.nax.ServiceArea: only overlapping, standard polygons from every source are built "
                                "in grapy.")
        if self.outputType is not ServiceAreaOutputType.Polygons and self.travelDirection is TravelDirection.ToFacility:
            raise NotBuiltError("arcpy.nax.ServiceArea: lines toward the facilities are not built in grapy.")
        self._say()
        graph = self._graph()
        facilities = self._located(graph, ServiceAreaInputDataType.Facilities, "Facilities")
        per = _network._per_unit(graph.srid) or 1.0
        trim = float(self.polygonBufferDistance) * _network._LINEAR[self.polygonBufferDistanceUnits.name.upper()] / per
        per_cutoff = self._impedance_per(graph)
        lines, polygons = _network.solve_service_area(
            graph, facilities, [float(c) * per_cutoff for c in (self.defaultImpedanceCutoffs or [5, 10, 15])],
            "FROM_FACILITIES" if self.travelDirection is TravelDirection.FromFacility else "TO_FACILITIES",
            self.geometryAtCutoff is ServiceAreaPolygonCutoffGeometry.Rings, trim)
        echoed = self._inputs_out(facilities, None, graph, ServiceAreaInputDataType.Facilities, "FacilityOID")
        by_oid = {f["oid"]: (f, row) for f, row in zip(facilities, echoed)}
        sources = {s: s.rsplit("/", 1)[-1] for s in graph.sources}
        impedance = graph.impedance
        for n, row in enumerate(lines, start=1):
            # Pro's fields (measured 2026-11-12): the source's name, not its id; the facility's source ObjectID; the
            # cumulative costs in the solver's units, named by unit.
            facility = by_oid[row["FacilityID"]][0]
            k0, k1 = row.pop(f"FromCumul_{impedance}"), row.pop(f"ToCumul_{impedance}")
            row["SourceName"] = sources[graph.edges[row["_edge"]]["source"]]
            row.pop("SourceID", None)
            row["FacilityOID"] = facility.get("_source_oid")
            row.update(self._impedance_totals(graph, {impedance: k0}, "FromCumul", both=True))
            row.update(self._impedance_totals(graph, {impedance: k1}, "ToCumul", both=True))
            row["ObjectID"] = n
        for n, row in enumerate(polygons, start=1):
            # Pro's (measured 2026-11-12): breaks in the solver's units, written as 0, 0.15, 5 in the name, and the
            # facility's Name (as Name_1), Breaks, AdditionalTime and AdditionalDistance joined on.
            facility, echo = by_oid[row["FacilityID"]]
            low, high = row["FromBreak"] / per_cutoff, row["ToBreak"] / per_cutoff
            row.update(Name=f"{facility['name']} : {_break(low)} - {_break(high)}", FromBreak=low, ToBreak=high,
                       FacilityOID=facility.get("_source_oid"), Name_1=facility["name"], Breaks=echo.get("Breaks"),
                       AdditionalTime=echo.get("AdditionalTime"), AdditionalDistance=echo.get("AdditionalDistance"))
            row["ObjectID"] = n
        outputs = {ServiceAreaOutputDataType.Facilities: echoed}
        if self.outputType in (ServiceAreaOutputType.Polygons, ServiceAreaOutputType.PolygonsAndLines):
            outputs[ServiceAreaOutputDataType.Polygons] = polygons
        if self.outputType in (ServiceAreaOutputType.Lines, ServiceAreaOutputType.PolygonsAndLines):
            outputs[ServiceAreaOutputDataType.Lines] = lines
        return _Result(outputs, graph.srid, bool(polygons))


# ---- fleet routing (ADR-239) ------------------------------------------------------------------------------------------

class Importance(enum.IntEnum):
    Low = 0
    Medium = 1
    High = 2


class DrivingSide(enum.IntEnum):
    """The side of the road vehicles drive on. grapy's network holds one (``arcpy.graticula.SetNetworkDrivingSide``); a
    waste collection pass collects the curb side, which it decides."""
    Unknown = 0
    RightSide = 1
    LeftSide = 2


class StopCollectionMode(enum.IntEnum):
    """How a waste collection route collects a street's two sides: separately or in one pass."""
    OneSide = 1
    BothSides = 2


class VehicleRoutingProblemSchemaVersion(enum.IntEnum):
    One = 1
    Two = 2


class VehicleRoutingProblemInputDataType(enum.IntEnum):
    """Schema version One's inputs."""
    Orders = 0
    Depots = 1
    Routes = 2
    Breaks = 3
    RouteZones = 4
    RouteRenewals = 5
    OrderPairs = 6
    PointBarriers = 7
    LineBarriers = 8
    PolygonBarriers = 9


class VehicleRoutingProblemOutputDataType(enum.IntEnum):
    """Schema version One's outputs."""
    UnassignedStops = 0
    Stops = 1
    Routes = 2
    Directions = 3


class VehicleRoutingProblemInputDataType2(enum.IntEnum):
    """Schema version Two's inputs."""
    Orders = 100
    Depots = 101
    Routes = 102
    Breaks = 103
    RouteZones = 104
    RouteRenewals = 105
    OrderPairs = 106
    PointBarriers = 107
    LineBarriers = 108
    PolygonBarriers = 109
    OrderSpecialties = 110
    RouteSpecialties = 111


class VehicleRoutingProblemOutputDataType2(enum.IntEnum):
    """Schema version Two's outputs."""
    Orders = 100
    Depots = 101
    DepotVisits = 102
    Breaks = 103
    Routes = 104
    DirectionPoints = 105
    DirectionLines = 106


class LastMileDeliveryInputDataType(enum.IntEnum):
    Orders = 200
    Depots = 201
    Routes = 202
    OrderSpecialties = 203
    RouteSpecialties = 204
    Zones = 205
    PointBarriers = 206
    LineBarriers = 207
    PolygonBarriers = 208


class LastMileDeliveryOutputDataType(enum.IntEnum):
    Orders = 250
    Depots = 251
    DepotVisits = 252
    Routes = 253
    DirectionPoints = 254
    DirectionLines = 255


class WasteCollectionInputDataType(enum.IntEnum):
    Stops = 400
    Depots = 401
    Routes = 402
    Renewals = 403
    RouteRenewals = 404
    RenewalVisits = 405
    PointBarriers = 406
    LineBarriers = 407
    PolygonBarriers = 408


class WasteCollectionOutputDataType(enum.IntEnum):
    Stops = 450
    Depots = 451
    RenewalVisits = 452
    RouteLines = 453
    Routes = 454
    Renewals = 455
    DirectionPoints = 456
    DirectionLines = 457


def _fleet_say(name, method):
    from ._runtime import AddWarning, _SAID_EQUIVALENT
    key = f"nax.{name}"
    if key not in _SAID_EQUIVALENT:
        _SAID_EQUIVALENT.add(key)
        AddWarning(f"{key}.solve is an equivalent, not ArcGIS Pro's own computation: {method[0]} ({method[1]}). Its "
                   "numbers may differ from Pro's.")


class _FleetSolver(_Solver):
    """What the three fleet routing solvers share: located inputs by input type name, their equivalence warning, the
    Directions refusal, and a time-based travel mode."""

    _LOCATED = ("Orders", "Depots", "Stops", "Renewals")

    def _fleet_inputs(self, graph):
        inputs = {}
        for input_type, rows in self._inputs.items():
            if "Barriers" in input_type.name:
                continue
            if input_type.name in self._LOCATED:
                items = self._located(graph, input_type, input_type.name)
                for item in items:
                    curb = item.get("CurbApproach")
                    if curb not in (None, 0, CurbApproach.EitherSide):
                        raise NotBuiltError(f"arcpy.nax.{type(self).__name__}: CurbApproach {curb} is not built in "
                                            "grapy; every location is approached from either side.")
                inputs[input_type.name] = items
            else:
                inputs[input_type.name] = [dict(row) for row in rows]
        return inputs

    def _check(self):
        if getattr(self, "returnDirections", False):
            raise NotBuiltError(f"arcpy.nax.{type(self).__name__}: returnDirections writes Esri's direction text, whose "
                                "rules are not published.")
        graph = self._graph()
        from .na import _time_impedance
        _time_impedance(f"nax.{type(self).__name__}", graph)
        return graph

    def _result(self, outputs, enum_type, graph, succeeded):
        rows = {}
        for member in enum_type:
            if member.name in outputs:
                found = outputs[member.name]
                for n, row in enumerate(found, start=1):
                    row.setdefault("ObjectID", n)
                rows[member] = found
        return _Result(rows, graph.srid, succeeded)


class VehicleRoutingProblem(_FleetSolver):
    """EQUIVALENT: the vehicle routing problem by Solomon's insertion heuristic and ruin and recreate (``grapy._fleet``),
    over Dijkstra's travel times. Schema version One (the default, as documented) reads and writes the ArcGIS Desktop
    schema — Stops, UnassignedStops, Routes — and Two the ArcGIS Pro one — Orders, Depots, DepotVisits, Breaks, Routes.
    Directions and barriers are refused; ``spatiallyClusterRoutes`` is accepted and has no term in grapy's objective
    (INFERRED)."""

    __grapy_equivalent__ = None  # set below, from grapy._fleet

    def __init__(self, in_network, version=None):
        self.version = version or VehicleRoutingProblemSchemaVersion.One
        self._INPUT_TYPES = tuple(VehicleRoutingProblemInputDataType if self.version is
                                  VehicleRoutingProblemSchemaVersion.One else VehicleRoutingProblemInputDataType2)
        super().__init__(in_network)
        self.allowSaveLayerFile = False
        self.allowSaveRouteData = False
        self.defaultDate = None
        self.directionsDistanceUnits = None
        self.directionsLanguage = "en"
        self.directionsStyle = None
        self.distanceUnits = DistanceUnits.Kilometers
        self.excessTransitFactor = Importance.Medium
        self.returnDirections = False
        self.returnStopShapes = False
        self.routeShapeType = RouteShapeType.TrueShapeWithMeasures
        self.searchQuery = None
        self.searchSources = None
        self.spatiallyClusterRoutes = True
        self.timeWindowFactor = Importance.Medium
        self.timeZoneForTimeFields = TimeZoneUsage.LocalTimeAtLocations

    @property
    def _INPUT_FIELDS(self):
        if self.version is VehicleRoutingProblemSchemaVersion.One:
            return _VRP_ONE_FIELDS
        return _layer_fields("Vehicle Routing Problem")

    def _properties(self, input_type):
        if self.version is VehicleRoutingProblemSchemaVersion.One:
            return list(_VRP_ONE_FIELDS.get(input_type.name, []))
        return super()._properties(input_type)

    def solve(self):
        from . import _fleet
        _fleet_say("VehicleRoutingProblem", _fleet.METHOD)
        graph = self._check()
        one = self.version is VehicleRoutingProblemSchemaVersion.One
        problem = _fleet.Fleet(graph, self._fleet_inputs(graph), "VRP", "One" if one else "Two", self.timeUnits.name,
                               self.distanceUnits.name, _day(self.defaultDate), self.timeWindowFactor,
                               self.excessTransitFactor, ignore_invalid=self.ignoreInvalidLocations)
        problem.solve()
        shape = {RouteShapeType.NoLine: "NO_LINES", RouteShapeType.StraightLine: "STRAIGHT_LINES"}.get(
            self.routeShapeType, "ALONG_NETWORK")
        outputs = _fleet.fleet_outputs(problem, shape, "One" if one else "Two")
        if one and not self.returnStopShapes:
            for kind in ("Stops", "UnassignedStops"):
                for row in outputs[kind]:
                    row["SHAPE"] = None
        if one:
            _schema_one_as_pro(outputs)
        succeeded = any(r.get("OrderCount") for r in outputs["Routes"])
        return self._result(outputs, VehicleRoutingProblemOutputDataType if one else VehicleRoutingProblemOutputDataType2,
                            graph, succeeded)


class LastMileDelivery(_FleetSolver):
    """EQUIVALENT: the last mile delivery as the vehicle routing problem is solved (``grapy._fleet``), with its own
    inputs and outputs: one time window, anchoring, start flexibility, zones by name, Sequence from 0 at the start
    depot spaced by ``sequenceGap``. Directions and barriers are refused."""

    __grapy_equivalent__ = None
    _INPUT_TYPES = tuple(LastMileDeliveryInputDataType)

    @property
    def _INPUT_FIELDS(self):
        return _layer_fields("Last Mile Delivery")

    def __init__(self, in_network):
        super().__init__(in_network)
        self.allowSaveLayerFile = False
        self.allowSaveRouteData = False
        self.directionsLanguage = "en"
        self.distanceUnits = DistanceUnits.Kilometers
        self.earliestRouteStartDate = None
        self.earliestRouteStartTime = None
        self.maxRouteTotalTime = None
        self.returnDirections = False
        self.routeShapeType = RouteShapeType.StraightLine
        self.searchSources = None
        self.sequenceGap = 1
        self.timeZoneForTimeFields = TimeZoneUsage.LocalTimeAtLocations

    def solve(self):
        from . import _fleet
        if int(self.sequenceGap) < 1:
            raise ValueError("sequenceGap is a positive integer.")
        if self.routeShapeType is RouteShapeType.TrueShape:
            raise ValueError("routeShapeType TrueShape is not supported by LastMileDelivery, as documented.")
        _fleet_say("LastMileDelivery", _fleet.METHOD)
        graph = self._check()
        problem = _fleet.Fleet(graph, self._fleet_inputs(graph), "LMD", "Two", self.timeUnits.name,
                               self.distanceUnits.name, None, "Medium", "Medium", self.earliestRouteStartDate,
                               self.earliestRouteStartTime, self.maxRouteTotalTime,
                               ignore_invalid=self.ignoreInvalidLocations)
        problem.solve()
        shape = {RouteShapeType.NoLine: "NO_LINES", RouteShapeType.StraightLine: "STRAIGHT_LINES"}.get(
            self.routeShapeType, "ALONG_NETWORK")
        outputs = _fleet.fleet_outputs(problem, shape, "LMD", int(self.sequenceGap))
        succeeded = any(r.get("OrderCount") for r in outputs["Routes"])
        return self._result(outputs, LastMileDeliveryOutputDataType, graph, succeeded)


def _second(value):
    """A time of day to the nearest second, as Pro writes a waste collection route's EndTime (measured 2026-11-12:
    08:03:36 where grapy's arithmetic gave 08:03:35.999999)."""
    if not isinstance(value, datetime.time):
        return value
    moment = datetime.datetime.combine(datetime.date(2000, 1, 1), value) + datetime.timedelta(microseconds=500000)
    return moment.replace(microsecond=0).time()


class WasteCollection(_FleetSolver):
    """EQUIVALENT: waste collection as ``grapy._fleet.Waste`` routes it: stops grouped by street edge (and side, when
    collected one side at a time), each group a task served by traversing its edge, routes minimised then time.
    One-side passes collect the curb side of the network's driving side (INFERRED: right-hand traffic unless set).
    Directions, barriers and preassigned renewal visits are refused."""

    __grapy_equivalent__ = None
    _INPUT_TYPES = tuple(WasteCollectionInputDataType)

    @property
    def _INPUT_FIELDS(self):
        return _layer_fields("Waste Collection")

    def __init__(self, in_network):
        super().__init__(in_network)
        self.directionsLanguage = "en"
        self.distanceUnits = DistanceUnits.Kilometers
        self.maxRouteTotalTime = None
        self.routeStartTime = None
        self.searchSources = None
        self.stopCollectionMode = None

    def _waste_as_pro(self, graph, outputs):
        """The outputs as ArcGIS Pro 3.7 writes them (measured 2026-11-12): Routes echo every route property and carry
        no shape (RouteLines do); a total no stop gives a value for, and a count of no renewals, is null; Stops echo
        their properties and their network location."""
        from . import _fleet
        stops = self._inputs[WasteCollectionInputDataType.Stops]
        given = {m for m in _fleet.Waste.MEASURES if any(r.get(m) is not None for r in stops)}
        route_fields = self._properties(WasteCollectionInputDataType.Routes)
        for row in outputs["Routes"]:
            for name in route_fields:
                row.setdefault(name, None)
            row["SHAPE"] = None
            for m in _fleet.Waste.MEASURES:
                if m not in given:
                    row[f"Total{m}"] = None
            if not row.get("RenewalCount"):
                row["RenewalCount"] = None
            row["EndTime"] = _second(row.get("EndTime"))
        items = {item["oid"]: item for item in self._located(graph, WasteCollectionInputDataType.Stops, "Stops")}
        stop_fields = [f for f in self._properties(WasteCollectionInputDataType.Stops)
                       if f not in ("Bearing", "BearingTol", "NavLatency")]
        for row in outputs["Stops"]:
            item = items.get(row["ObjectID"])
            if item is None:
                continue
            for name in stop_fields:
                row.setdefault(name, item.get(name))
            location = graph.location_fields(item["located"], item["point"])
            row.update({k: v for k, v in location.items() if not k.startswith("_")})
            row["SnapZ"] = 0.0 if item["located"] else None

    def solve(self):
        from . import _fleet, _turns
        _fleet_say("WasteCollection", _fleet.WASTE_METHOD)
        graph = self._check()
        mode = None if self.stopCollectionMode is None else (
            "ONE_SIDE" if self.stopCollectionMode is StopCollectionMode.OneSide else "BOTH_SIDES")
        problem = _fleet.Waste(graph, self._fleet_inputs(graph), self.timeUnits.name, self.distanceUnits.name,
                               self.routeStartTime, self.maxRouteTotalTime, mode,
                               _turns.driving_side(self.networkDataSource))
        problem.solve()
        outputs = _fleet.waste_outputs(problem)
        self._waste_as_pro(graph, outputs)
        succeeded = any(r.get("StopCount") for r in outputs["Routes"])
        return self._result(outputs, WasteCollectionOutputDataType, graph, succeeded)


# Schema version One's Routes, as ArcGIS Pro 3.7 writes them (measured 2026-11-12): the solve's fields, not the input's.
_VRP_ONE_ROUTE_FIELDS = ["ObjectID", "Name", "ViolatedConstraints", "ViolatedConstraint_1", "ViolatedConstraint_2",
                         "ViolatedConstraint_3", "ViolatedConstraint_4", "OrderCount", "TotalCost", "RegularTimeCost",
                         "OvertimeCost", "DistanceCost", "TotalTime", "TotalOrderServiceTime", "TotalBreakServiceTime",
                         "TotalTravelTime", "TotalDistance", "StartTime", "EndTime", "StartTimeUTC", "EndTimeUTC",
                         "TotalWaitTime", "TotalViolationTime", "RenewalCount", "TotalRenewalServiceTime", "SHAPE"]


def _schema_one_as_pro(outputs):
    """A schema version One solve's outputs as ArcGIS Pro 3.7 writes them (measured 2026-11-12): Routes hold the
    solve's fields only, ViolatedConstraints 0 when none is violated; a network with no time zone writes its UTC times
    as its local ones; the start depot's FromPrevDistance is 0."""
    for n, row in enumerate(outputs["Routes"]):
        row = {k: row.get(k) for k in _VRP_ONE_ROUTE_FIELDS}
        if row["ViolatedConstraints"] is None and not any(row[f"ViolatedConstraint_{k}"] for k in range(1, 5)):
            row["ViolatedConstraints"] = 0
        row["StartTimeUTC"], row["EndTimeUTC"] = row["StartTime"], row["EndTime"]
        outputs["Routes"][n] = row
    for row in outputs["Stops"]:
        row["ArriveTimeUTC"], row["DepartTimeUTC"] = row.get("ArriveTime"), row.get("DepartTime")
        if row.get("FromPrevDistance") is None and row.get("FromPrevTravelTime") == 0:
            row["FromPrevDistance"] = 0.0


# Schema version One's inputs, in ArcGIS Pro 3.7's order (measured 2026-11-12).
_VRP_ONE_FIELDS = {
    "Orders": ["Name", "Description", "ServiceTime", "TimeWindowStart2", "TimeWindowEnd2", "MaxViolationTime2",
               "InboundArriveTime", "OutboundDepartTime", "Revenue", "AssignmentRule", "RouteName", "Sequence",
               "CurbApproach", "Bearing", "BearingTol", "NavLatency", "DeliveryQuantities", "PickupQuantities",
               "TimeWindowStart1", "TimeWindowEnd1", "MaxViolationTime1", "SpecialtyNames"],
    "Depots": ["Name", "Description", "TimeWindowStart2", "TimeWindowEnd2", "CurbApproach", "Bearing", "BearingTol",
               "NavLatency", "TimeWindowStart1", "TimeWindowEnd1"],
    "Routes": ["Name", "Description", "StartDepotName", "EndDepotName", "StartDepotServiceTime", "EndDepotServiceTime",
               "EarliestStartTime", "LatestStartTime", "ArriveDepartDelay", "FixedCost", "CostPerUnitTime",
               "CostPerUnitDistance", "OvertimeStartTime", "CostPerUnitOvertime", "MaxOrderCount", "MaxTotalTime",
               "MaxTotalTravelTime", "MaxTotalDistance", "AssignmentRule", "Capacities", "SpecialtyNames"],
    "Breaks": ["RouteName", "Precedence", "ServiceTime", "TimeWindowStart", "TimeWindowEnd", "MaxViolationTime",
               "MaxTravelTimeBetweenBreaks", "MaxCumulWorkTime", "IsPaid", "Sequence"],
    "RouteZones": ["RouteName", "IsHardZone"],
    "RouteRenewals": ["RouteName", "DepotName", "ServiceTime", "Sequences"],
    "OrderPairs": ["FirstOrderName", "SecondOrderName", "MaxTransitTime"],
}


def _layer_fields(solver):
    """A fleet solver's input fields: its analysis layer's, less the outputs and the network location fields."""
    from . import na
    outputs = {f for f, _ in na._VISIT_OUT + na._VIOLATED + na._ROUTE_TOTALS + na._LOCATION}
    outputs |= {"Status", "ViolationTime", "ArriveCurbApproach", "DepartCurbApproach", "RelativePosition",
                "TotalBreakServiceTime", "StartTimeUTC", "EndTimeUTC", "RenewalCount", "TotalRenewalServiceTime",
                "RouteLineID", "StopCount", "TotalTime", "TotalTravelTime", "TotalStopServiceTime", "TotalDistance",
                "EndTime", "Name"}
    outputs |= {f"Total{m}" for m in ("Bins_1", "Weight_1", "Volume_1", "Bins_2", "Weight_2", "Volume_2")}
    return {key: [f for f, _ in fields if f not in outputs]
            for key, (_, fields) in na._SCHEMAS[solver].items() if not key.endswith("Barriers")}


def _day(value):
    from .na import _date
    return _date(value)


def _mark_fleet():
    from . import _fleet
    for solver, method in ((VehicleRoutingProblem, _fleet.METHOD), (LastMileDelivery, _fleet.METHOD),
                           (WasteCollection, _fleet.WASTE_METHOD)):
        solver.__grapy_equivalent__ = f"{method[0]} ({method[1]})"


_mark_fleet()
VehicleRoutingProblemResult = LastMileDeliveryResult = WasteCollectionResult = _Result

from .na import (CreateTurnFeatureClass, IncreaseMaximumEdges, PopulateAlternateIDFields,  # noqa: E402,F401
                 UpdateByAlternateIDFields, UpdateByGeometry)

# ---- custom evaluators and traversed elements (ADR-246) -------------------------------------------------------------

from . import _evaluators, _traversal  # noqa: E402

AttributeEvaluator = _evaluators.AttributeEvaluator
NetworkElement = _evaluators.NetworkElement
Edge = _evaluators.Edge
AdjacentEdge = _evaluators.AdjacentEdge
Junction = _evaluators.Junction
Turn = _evaluators.Turn
Attribute = _evaluators.Attribute
AttributeParameter = _evaluators.AttributeParameter
AttributeUsage = _evaluators.AttributeUsage
AttributeParameterUsage = _evaluators.AttributeParameterUsage
NetworkTimeUsage = _evaluators.NetworkTimeUsage
TraversedElement = _traversal.TraversedElement
TraversedEdge = _traversal.TraversedEdge
TraversedJunction = _traversal.TraversedJunction
TraversedTurn = _traversal.TraversedTurn
TraversedElementType = _traversal.TraversedElementType


# ---- refused by name -----------------------------------------------------------------------------------------------

_REASONS = {
    "reaches a portal for a web tool's settings, and a fenced tool has no network (ADR-223 §5.3)": ("GetWebToolInfo",),
    "lists the languages of Esri's direction text, whose rules are not published": ("ListDirectionsLanguages",),
}
_REASONS["writes or customises Esri's direction text, landmarks and street names, whose rules are not published"] = (
    "DirectionPoint", "DirectionPointType", "DirectionsCustomizer", "DirectionsFieldMapping", "DirectionsName",
    "DirectionsQuery", "DirectionsStyle", "ReferenceLandmark", "ReferenceLandmarkType", "SpatialLandmark", "LandmarkSide",
    "NameClass")
_REFUSED = {name: reason for reason, names in _REASONS.items() for name in names}
from .na import _REFUSED as _NA_REFUSED  # noqa: E402 — nax documents the same network tools
for _name in ("CreateNetworkDatasetFromTemplate", "CreateTemplateFromNetworkDataset", "DissolveNetwork",
              "ShareAsRouteLayers"):
    _REFUSED[_name] = _NA_REFUSED[_name]


def __getattr__(name):
    if name.startswith("_"):
        raise AttributeError(name)
    reason = _REFUSED.get(name)
    message = f"arcpy.nax.{name} {reason}." if reason else f"arcpy.nax.{name} is not built in grapy yet."

    def missing(*_, **__):
        raise NotBuiltError(message)

    return missing


# ---- location-allocation ---------------------------------------------------------------------------------------------

class LocationAllocationInputDataType(enum.IntEnum):
    Facilities = 0
    DemandPoints = 1
    PointBarriers = 2
    LineBarriers = 3
    PolygonBarriers = 4


class LocationAllocationOutputDataType(enum.IntEnum):
    Facilities = 0
    DemandPoints = 1
    Lines = 2


class LocationAllocationProblemType(enum.IntEnum):
    MaximizeAttendance = 0
    MaximizeCapacitatedCoverage = 1
    MaximizeCoverage = 2
    MaximizeMarketShare = 3
    MinimizeFacilities = 4
    MinimizeImpedance = 5
    TargetMarketShare = 6


class DecayFunctionType(enum.IntEnum):
    Linear = 0
    Power = 1
    Exponential = 2


def _teitz_bart(costs, weights, candidates, required, count):
    """The facilities chosen to minimise the total weighted cost, each demand served by its nearest chosen facility:
    a greedy start, then Teitz and Bart's vertex substitution until no swap of a chosen facility for an unchosen one
    improves. A demand no chosen facility reaches within the cutoff is not served; serving more weight comes first.
    Ties go to the lower facility index."""
    import math

    def score(chosen):
        served, total = 0.0, 0.0
        for d, weight in enumerate(weights):
            best = min((costs[d][f] for f in chosen), default=math.inf)
            if math.isfinite(best):
                served += weight
                total += weight * best
        return (-served, total)

    chosen = list(required)
    while len(chosen) < count:
        options = [f for f in candidates if f not in chosen]
        if not options:
            break
        chosen.append(min(options, key=lambda f: (score(chosen + [f]), f)))
    improved = True
    while improved:
        improved = False
        current = score(chosen)
        for out in [f for f in chosen if f not in required]:
            for into in [f for f in candidates if f not in chosen]:
                trial = [into if f == out else f for f in chosen]
                if score(trial) < current:
                    chosen, current, improved = trial, score(trial), True
                    break
            if improved:
                break
    return sorted(chosen)


def _allocate(graph, facilities, demands, away, cutoff, count, tool):
    """The minimize-impedance allocation shared by nax.LocationAllocation and na's location-allocation layer: the
    network costs from each facility to each demand point (or back, travelling toward the facilities), the facilities
    Teitz–Bart chooses, and each served demand's facility and path, as ``{demand index: (facility index, arcs)}``."""
    import math
    for item in facilities:
        if int(item.get("FacilityType") or 0) == 2:
            raise NotBuiltError(f"{tool}: competitor facilities are for market share problems, not built in grapy.")
    if any(item.get("GroupName") for item in demands):
        raise NotBuiltError(f"{tool}: demand point groups are not built in grapy.")
    solver = graph.solver([(i["key"], i["located"][0], i["located"][1]) for i in facilities + demands if i["located"]],
                          reverse=away)
    costs, paths = [], []
    for demand in demands:
        if demand["located"] is None:
            costs.append([math.inf] * len(facilities))
            paths.append([None] * len(facilities))
            continue
        row, way = [], []
        distances, predecessors = solver.run(demand["key"])
        for facility in facilities:
            cost = float(distances[solver.location_node[facility["key"]]]) if facility["located"] else math.inf
            if cutoff is not None and cost > float(cutoff):
                cost = math.inf
            row.append(cost)
            way.append(solver.path(predecessors, demand["key"], facility["key"]) if math.isfinite(cost) else None)
        costs.append(row)
        paths.append(way)
    weights = [float(d.get("Weight") if d.get("Weight") is not None else 1.0) for d in demands]
    candidates = [i for i, f in enumerate(facilities) if f["located"] and int(f.get("FacilityType") or 0) in (0, 3)]
    required = [i for i, f in enumerate(facilities) if f["located"] and int(f.get("FacilityType") or 0) == 1]
    chosen = _teitz_bart(costs, weights, sorted(set(candidates) | set(required)), required,
                         max(int(count or 1), len(required)))
    served = {}
    for d in range(len(demands)):
        reach = [(costs[d][f], f) for f in chosen if math.isfinite(costs[d][f])]
        if reach:
            f = min(reach)[1]
            served[d] = (f, paths[d][f])
    return solver, chosen, required, weights, served


class LocationAllocation(_Solver):
    """EQUIVALENT: the minimize-impedance problem (the p-median problem) by a greedy start and Teitz and Bart's vertex
    substitution (Teitz and Bart 1968), over Dijkstra's network costs (ADR-226 §5.2, §5.4). Facilities are candidates
    (``FacilityType`` 0) or required (1), as documented; ``facilityCount`` facilities are chosen in all, chosen ones
    written as type 3, each demand point allocated to its nearest chosen facility within the cutoff. Outputs carry the
    documented fields — DemandCount, DemandWeight, FacilityID, AllocatedWeight, Weight, DemandID — and their totals as
    the other nax solvers name them (``Total_`` and ``TotalWeighted_``); line names follow the travel direction, as
    documented. INFERRED: serving more demand weight comes before a lower weighted cost; a demand point's weight is 1
    when null. The other problem types, competitors, group names and decay functions other than linear are not
    built."""

    __grapy_equivalent__ = "Teitz–Bart vertex substitution for the p-median problem (Teitz and Bart 1968)"
    _INPUT_TYPES = tuple(LocationAllocationInputDataType)
    _INPUT_FIELDS = {"Facilities": ["FacilityType", "Weight", "Capacity", "CurbApproach"],
                     "DemandPoints": ["GroupName", "Weight", "CurbApproach"]}

    def __init__(self, in_network):
        super().__init__(in_network)
        self.problemType = LocationAllocationProblemType.MinimizeImpedance
        self.facilityCount = 1
        self.defaultImpedanceCutoff = None
        self.defaultCapacity = 1
        self.decayFunctionType = DecayFunctionType.Linear
        self.decayFunctionParameterValue = 1
        self.targetMarketShare = 10
        self.travelDirection = TravelDirection.FromFacility
        self.lineShapeType = LineShapeType.StraightLine

    def _say(self):
        from ._runtime import AddWarning, _SAID_EQUIVALENT
        if "nax.LocationAllocation" not in _SAID_EQUIVALENT:
            _SAID_EQUIVALENT.add("nax.LocationAllocation")
            AddWarning(f"nax.LocationAllocation.solve is an equivalent, not ArcGIS Pro's own computation: "
                       f"{self.__grapy_equivalent__}. Its numbers may differ from Pro's.")

    def solve(self):
        if self.problemType is not LocationAllocationProblemType.MinimizeImpedance:
            raise NotBuiltError(f"arcpy.nax.LocationAllocation: problemType {self.problemType.name} is not built in "
                                "grapy; MinimizeImpedance is.")
        if self.decayFunctionType is not DecayFunctionType.Linear:
            raise NotBuiltError("arcpy.nax.LocationAllocation: decay functions other than Linear are not built in grapy.")
        self._say()
        graph = self._graph()
        facilities = self._located(graph, LocationAllocationInputDataType.Facilities, "Facilities")
        demands = self._located(graph, LocationAllocationInputDataType.DemandPoints, "DemandPoints")
        away = self.travelDirection is TravelDirection.FromFacility
        solver, chosen, required, weights, served = _allocate(graph, facilities, demands, away,
                                                              self._cutoff(graph, self.defaultImpedanceCutoff),
                                                              self.facilityCount, "arcpy.nax.LocationAllocation")
        lines, by_facility = [], {i: [] for i in chosen}
        demand_rows = {}
        for d, demand in enumerate(demands):
            if d not in served:
                demand_rows[demand["key"]] = {"FacilityID": None, "FacilityOID": None, "AllocatedWeight": None}
                continue
            f, path = served[d]
            facility = facilities[f]
            totals = {a: solver.total(path, a) for a, (usage, _) in graph.attributes.items() if usage == "Cost"}
            by_facility[f].append((weights[d], totals))
            demand_rows[demand["key"]] = {"FacilityID": facility["oid"], "FacilityOID": facility.get("_source_oid"),
                                          "AllocatedWeight": weights[d]}
            name = f"{facility['name']} - {demand['name']}" if away else f"{demand['name']} - {facility['name']}"
            # Pro's fields (measured 2026-11-12): the inputs' source ObjectIDs, and TotalWeighted_ of the impedance only.
            line = {"ObjectID": len(lines) + 1, "Name": name, "FacilityID": facility["oid"], "DemandID": demand["oid"],
                    "FacilityOID": facility.get("_source_oid"), "DemandOID": demand.get("_source_oid"),
                    "Weight": weights[d]}
            line.update(self._totals(graph, totals, "Total"))
            line.update(self._impedance_totals(graph, {a: v * weights[d] for a, v in totals.items()}, "TotalWeighted"))
            if self.lineShapeType is not LineShapeType.NoLine:
                from shapely.geometry import LineString
                ends = [facility["point"], demand["point"]] if away else [demand["point"], facility["point"]]
                line["SHAPE"] = LineString([(p.x, p.y) for p in ends])
            lines.append(line)
        facility_rows = {}
        for f, facility in enumerate(facilities):
            allocated = by_facility.get(f, [])
            row = {"FacilityType": 3 if f in chosen and f not in required else int(facility.get("FacilityType") or 0),
                   "DemandCount": len(allocated), "DemandWeight": sum(w for w, _ in allocated)}
            summed = {}
            weighted = {}
            for w, totals in allocated:
                for a, v in totals.items():
                    summed[a] = summed.get(a, 0.0) + v
                    weighted[a] = weighted.get(a, 0.0) + v * w
            # A facility's totals are of the impedance only, as Pro writes them (measured 2026-11-12).
            row.update(self._impedance_totals(graph, summed, "Total"))
            row.update(self._impedance_totals(graph, weighted, "TotalWeighted"))
            facility_rows[facility["key"]] = row
        return _Result({LocationAllocationOutputDataType.Facilities: self._inputs_out(
                            facilities, facility_rows, graph, LocationAllocationInputDataType.Facilities, "FacilityOID"),
                        LocationAllocationOutputDataType.DemandPoints: self._inputs_out(
                            demands, demand_rows, graph, LocationAllocationInputDataType.DemandPoints, "DemandOID"),
                        LocationAllocationOutputDataType.Lines: lines}, graph.srid, bool(lines))


LocationAllocationResult = _Result

for _built in ("LocationAllocation", "LocationAllocationResult", "LocationAllocationInputDataType", "LocationAllocationOutputDataType",
               "LocationAllocationProblemType", "DecayFunctionType"):
    _REFUSED.pop(_built, None)
