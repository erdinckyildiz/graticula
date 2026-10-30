"""arcpy.nax — the Network Analyst solver objects, by their documented names, over grapy's network model (ADR-226).

A solver object (Route, ClosestFacility, ServiceArea, OriginDestinationCostMatrix) holds its inputs in memory, locates
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

import enum
import os

from . import _gpkg, _network
from ._runtime import ExecuteError, NotBuiltError


class DistanceUnits(enum.Enum):
    Meters = "Meters"
    Kilometers = "Kilometers"
    Feet = "Feet"
    Yards = "Yards"
    Miles = "Miles"
    NauticalMiles = "NauticalMiles"
    Inches = "Inches"
    Centimeters = "Centimeters"
    Millimeters = "Millimeters"
    Decimeters = "Decimeters"


class TimeUnits(enum.Enum):
    Seconds = "Seconds"
    Minutes = "Minutes"
    Hours = "Hours"
    Days = "Days"


class TravelDirection(enum.Enum):
    FromFacility = "FromFacility"
    ToFacility = "ToFacility"


class RouteShapeType(enum.Enum):
    TrueShape = "TrueShape"
    TrueShapeWithMeasures = "TrueShapeWithMeasures"
    StraightLine = "StraightLine"
    NoLine = "NoLine"


class LineShapeType(enum.Enum):
    StraightLine = "StraightLine"
    NoLine = "NoLine"


class ServiceAreaOutputType(enum.Enum):
    Polygons = "Polygons"
    Lines = "Lines"
    PolygonsAndLines = "PolygonsAndLines"


class ServiceAreaPolygonCutoffGeometry(enum.Enum):
    Rings = "Rings"
    Disks = "Disks"


class ServiceAreaOverlapGeometry(enum.Enum):
    Overlap = "Overlap"
    Dissolve = "Dissolve"
    Split = "Split"


class ServiceAreaPolygonDetail(enum.Enum):
    Standard = "Standard"
    Generalized = "Generalized"
    High = "High"


class TimeZoneUsage(enum.Enum):
    LocalTimeAtLocations = "LocalTimeAtLocations"
    UTC = "UTC"


class MessageSeverity(enum.Enum):
    All = "All"
    Warning = "Warning"
    Error = "Error"


class RouteInputDataType(enum.Enum):
    Stops = "Stops"
    PointBarriers = "PointBarriers"
    LineBarriers = "LineBarriers"
    PolygonBarriers = "PolygonBarriers"


class RouteOutputDataType(enum.Enum):
    Routes = "Routes"
    RouteEdges = "RouteEdges"
    RouteJunctions = "RouteJunctions"
    RouteTurns = "RouteTurns"
    Directions = "Directions"
    Stops = "Stops"
    DirectionPoints = "DirectionPoints"
    DirectionLines = "DirectionLines"


class ClosestFacilityInputDataType(enum.Enum):
    Facilities = "Facilities"
    Incidents = "Incidents"
    PointBarriers = "PointBarriers"
    LineBarriers = "LineBarriers"
    PolygonBarriers = "PolygonBarriers"


class ClosestFacilityOutputDataType(enum.Enum):
    Routes = "Routes"
    Facilities = "Facilities"
    Incidents = "Incidents"
    RouteEdges = "RouteEdges"
    Directions = "Directions"
    DirectionPoints = "DirectionPoints"
    DirectionLines = "DirectionLines"


class ServiceAreaInputDataType(enum.Enum):
    Facilities = "Facilities"
    PointBarriers = "PointBarriers"
    LineBarriers = "LineBarriers"
    PolygonBarriers = "PolygonBarriers"


class ServiceAreaOutputDataType(enum.Enum):
    Polygons = "Polygons"
    Lines = "Lines"
    Facilities = "Facilities"


class OriginDestinationCostMatrixInputDataType(enum.Enum):
    Origins = "Origins"
    Destinations = "Destinations"
    PointBarriers = "PointBarriers"
    LineBarriers = "LineBarriers"
    PolygonBarriers = "PolygonBarriers"


class OriginDestinationCostMatrixOutputDataType(enum.Enum):
    Lines = "Lines"
    Origins = "Origins"
    Destinations = "Destinations"


_MINUTES = {"SECONDS": 1 / 60, "MINUTES": 1.0, "HOURS": 60.0, "DAYS": 1440.0}


def _kind(units):
    key = str(units or "").upper().replace(" ", "")
    if key in _MINUTES:
        return "time"
    if key in _network._LINEAR:
        return "distance"
    return "other"


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


class CurbApproach(enum.Enum):
    EitherSideOfVehicle = 0
    RightSideOfVehicle = 1
    LeftSideOfVehicle = 2
    NoUTurn = 3


class TimeOfDayUsage(enum.Enum):
    StartTime = "StartTime"
    EndTime = "EndTime"


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
    junction source's id, its own EID as SOURCEOID, no cost and no restriction. Turns, time of day, live traffic,
    custom evaluators and custom directions are refused."""

    def __init__(self, in_network):
        self._path = str(_network.LAYERS.get(str(in_network), in_network))
        _network.open_network(self._path)[0].close()

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
        return int(self._query("select count(*) from grapy_network_edge where network = ?")[0][0])

    @property
    def junctionCount(self):
        return int(self._query("select count(*) from grapy_network_junction where network = ?")[0][0])

    @property
    def turnCount(self):
        return 0

    @property
    def travelModes(self):
        return GetTravelModes(self._path)

    @property
    def customEvaluators(self):
        return []

    @customEvaluators.setter
    def customEvaluators(self, value):
        if value:
            raise NotBuiltError("arcpy.nax: custom evaluators are script evaluators, refused (ADR-226 §5.1).")

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
        return _network.Graph(self._path, travel_mode)

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

    def turns(self, *_, **__):
        raise NotBuiltError("arcpy.nax.NetworkDataset.turns: turns are refused until they are defined (ADR-226 §5.3).")

    def getLiveTrafficStatus(self, wait_for_update=False):
        raise NotBuiltError("arcpy.nax.NetworkDataset: live traffic is refused (ADR-226 §5.3).")

    def updateTrafficCache(self, timeout_seconds=None):
        raise NotBuiltError("arcpy.nax.NetworkDataset: live traffic is refused (ADR-226 §5.3).")

    def updateNetworkDatasetSchema(self, custom_evaluators=None, custom_directions=None, openlr_traffic_service_id=None,
                                   openlr_traffic_feed_ids=None, travel_modes=None):
        """Only ``travel_modes`` is built: each travel mode given added or replaced. INFERRED: the others are kept."""
        if custom_evaluators or custom_directions or openlr_traffic_service_id or openlr_traffic_feed_ids:
            raise NotBuiltError("arcpy.nax: custom evaluators (script evaluators), custom directions and live traffic are "
                                "refused (ADR-226 §5.1, §5.3).")
        for name, mode in (travel_modes or {}).items():
            travel = TravelMode(mode)
            _network.add_travel_mode(self._path, name, travel.impedance, travel.restrictions, travel.timeAttributeName,
                                     travel.distanceAttributeName)


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
        shape = values.pop("SHAPE@", None) or values.pop("SHAPE@XY", None)
        from .da import _shape_from
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
        with da.SearchCursor(path, [*columns, "SHAPE@"], where) as rows:
            for n, row in enumerate(rows):
                if max_features is not None and n >= int(max_features):
                    break
                values = dict(zip(columns, row[:-1]))
                lower = {k.lower(): v for k, v in values.items()}
                record = {k: v for k, v in values.items()}
                for prop, field_map in mapping.items():
                    record[prop] = lower.get(str(field_map.mappedFieldName).lower()) if field_map.mappedFieldName else \
                        field_map.defaultValue
                record["SHAPE@"] = row[-1]
                self._add(input_type, record)

    def insertCursor(self, input_type, field_names, append=True):
        self._check_type(input_type)
        if not append:
            self._inputs[input_type] = []
        return _Cursor(self, input_type, field_names)

    def count(self, input_type):
        self._check_type(input_type)
        return len(self._inputs[input_type])

    def fieldNames(self, input_type, use_location_fields=False):
        self._check_type(input_type)
        names = ["ObjectID", "Shape", "Name", *self._INPUT_FIELDS.get(input_type.name, [])]
        if use_location_fields:
            names += ["SourceID", "SourceOID", "PosAlong", "SideOfEdge"]
        return names

    def fieldMappings(self, input_type, use_location_fields=False, list_candidate_fields=None):
        """The input's properties, each mapped to the candidate field of the same name. Location fields are not built:
        grapy locates every input when it solves."""
        if use_location_fields:
            raise NotBuiltError("arcpy.nax: loading network location fields is not built in grapy; grapy locates every "
                                "input.")
        candidates = {getattr(f, "name", str(f)).lower(): getattr(f, "name", str(f)) for f in (list_candidate_fields or [])}
        return NAClassFieldMappings({name: NAClassFieldMap(candidates.get(name.lower()))
                                     for name in self.fieldNames(input_type) if name not in ("ObjectID", "Shape")})

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
        return _network.Graph(self.networkDataSource, self.travelMode)

    def _located(self, graph, input_type, prefix):
        per = _network._per_unit(graph.srid) or 1.0
        tolerance = float(self.searchTolerance) * _network._LINEAR[self.searchToleranceUnits.value.upper()] / per
        items = []
        for row in self._inputs[input_type]:
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
                out[f"{prefix}_{self.timeUnits.value}"] = minutes / _MINUTES[self.timeUnits.value.upper()]
        if distance_attribute:
            metres = totals.get(distance_attribute, 0.0) * _network._LINEAR[str(units[distance_attribute]).upper()]
            out[f"{prefix}_Miles"] = metres / 1609.344
            out[f"{prefix}_Kilometers"] = metres / 1000.0
            if self.distanceUnits not in (DistanceUnits.Miles, DistanceUnits.Kilometers):
                out[f"{prefix}_{self.distanceUnits.value}"] = metres / _network._LINEAR[self.distanceUnits.value.upper()]
        if _kind(units[graph.impedance]) == "other":
            out[f"{prefix}_Other" if prefix == "Total" else "Cumul_Cost"] = totals.get(graph.impedance, 0.0)
        for attribute in self.accumulateAttributeNames:
            kind = _kind(units[attribute])
            value = totals.get(attribute, 0.0)
            if kind == "time":
                out[f"{prefix}_{attribute}_{self.timeUnits.value}"] = \
                    value * _MINUTES[str(units[attribute]).upper()] / _MINUTES[self.timeUnits.value.upper()]
            elif kind == "distance":
                out[f"{prefix}_{attribute}_{self.distanceUnits.value}"] = \
                    value * _network._LINEAR[str(units[attribute]).upper()] / _network._LINEAR[self.distanceUnits.value.upper()]
            else:
                out[f"{prefix}_{attribute}_Other"] = value
        return out

    def _inputs_out(self, items, extra=None):
        rows = []
        for item in items:
            location = {k: v for k, v in item.items() if k not in ("key", "located", "point", "oid", "name")}
            row = {"ObjectID": item["oid"], "Name": item["name"], **location, "Status": 0 if item["located"] else 1,
                   "SHAPE": item["point"]}
            row.update((extra or {}).get(item["key"], {}))
            rows.append(row)
        return rows


class _Result:
    """A solve's outputs, by output type: counted, read with a cursor, exported to a feature class."""

    def __init__(self, outputs, srid, succeeded):
        self._outputs, self._srid = outputs, srid
        self.solveSucceeded = succeeded
        self.isPartialSolution = False

    @property
    def spatialReference(self):
        from ._reference import SpatialReference
        return SpatialReference(self._srid) if self._srid > 0 else None

    def _rows(self, output_type):
        if output_type not in self._outputs:
            raise NotBuiltError(f"arcpy.nax: the output {getattr(output_type, 'name', output_type)} is not built in grapy "
                                "(route edges, junctions, turns and directions are not).")
        return self._outputs[output_type]

    def count(self, output_type):
        return len(self._outputs.get(output_type, []))

    def fieldNames(self, output_type):
        rows = self._rows(output_type)
        names = ["ObjectID", "Shape"]
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
        return iter(out)

    def export(self, output_type, output_features):
        from . import da
        from .management import _path
        rows = self._rows(output_type)
        names = [f for f in self.fieldNames(output_type) if f not in ("ObjectID", "Shape")]
        kinds = {}
        for row in rows:
            for name in names:
                value = row.get(name)
                if value is not None and name not in kinds:
                    kinds[name] = "Double" if isinstance(value, float) else "Integer" if isinstance(value, int) else "String"
        shapes = {r["SHAPE"].geom_type for r in rows if r.get("SHAPE") is not None}
        shape = "Polygon" if shapes & {"Polygon", "MultiPolygon"} else "Polyline" if shapes & {"LineString", "MultiLineString"} \
            else "Point" if shapes else "Geometry"
        out = _path(str(output_features))
        _gpkg.create(out, shape, self._srid if shape != "Geometry" else 0,
                     [_gpkg.Column(n, kinds.get(n, "String")) for n in names])
        with da.InsertCursor(out, [*names, "SHAPE@"]) as written:
            for row in rows:
                written.insertRow((*[row.get(n) for n in names], row.get("SHAPE")))

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
    """EQUIVALENT: routes through stops by Dijkstra's shortest path (ADR-226 §5.4); see ``_network.solve_route``. Only
    the current stop order is built: ``findBestSequence`` is the travelling salesman problem. ``returnDirections`` is
    refused (Esri's direction rules are not published)."""

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
        self.routeShapeType = RouteShapeType.TrueShapeWithMeasures

    def solve(self):
        if self.findBestSequence or self.returnToStart:
            raise NotBuiltError("arcpy.nax.Route: findBestSequence and returnToStart are the travelling salesman problem, "
                                "whose heuristic is not chosen yet (ADR-226 §5.2).")
        if self.returnDirections:
            raise NotBuiltError("arcpy.nax.Route: returnDirections writes Esri's direction text, whose rules are not "
                                "published.")
        self._say()
        graph = self._graph()
        stops = self._located(graph, RouteInputDataType.Stops, "Stops")
        shape = {RouteShapeType.NoLine: "NO_LINES", RouteShapeType.StraightLine: "STRAIGHT_LINES"}.get(
            self.routeShapeType, "ALONG_NETWORK")
        routes, results = _network.solve_route(graph, stops, [], shape)
        for n, route in enumerate(routes, start=1):
            route["ObjectID"] = n
            route.update(self._totals(graph, route.pop("_totals"), "Total"))
            route.pop(f"Total_{graph.impedance}", None)
            route["FirstStopOID"], route["LastStopOID"] = route["FirstStopID"], route["LastStopID"]
        extra = {}
        for key, result in results.items():
            cumul = result.pop("_cumul", {})
            result.pop(f"Cumul_{graph.impedance}", None)
            extra[key] = {**result, **(self._totals(graph, cumul, "Cumul") if cumul else {})}
        return _Result({RouteOutputDataType.Routes: routes, RouteOutputDataType.Stops: self._inputs_out(stops, extra)},
                       graph.srid, bool(routes))


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
        routes = _network.solve_closest_facility(graph, facilities, incidents, direction, self.defaultImpedanceCutoff,
                                                 self.defaultTargetFacilityCount, [], shape)
        for n, route in enumerate(routes, start=1):
            route["ObjectID"] = n
            route.pop(f"Total_{graph.impedance}", None)
            route.update(self._totals(graph, route.pop("_totals"), "Total"))
        return _Result({ClosestFacilityOutputDataType.Routes: routes,
                        ClosestFacilityOutputDataType.Facilities: self._inputs_out(facilities),
                        ClosestFacilityOutputDataType.Incidents: self._inputs_out(incidents)}, graph.srid, bool(routes))


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
        lines = _network.solve_od_cost_matrix(graph, origins, destinations, self.defaultImpedanceCutoff,
                                              self.defaultDestinationCount, [],
                                              "NO_LINES" if self.lineShapeType is LineShapeType.NoLine else "STRAIGHT_LINES")
        for n, line in enumerate(lines, start=1):
            line["ObjectID"] = n
            line["OriginOID"], line["DestinationOID"] = line["OriginID"], line["DestinationID"]
            line.pop(f"Total_{graph.impedance}", None)
            line.update(self._totals(graph, line.pop("_totals"), "Total"))
        return _Result({OriginDestinationCostMatrixOutputDataType.Lines: lines,
                        OriginDestinationCostMatrixOutputDataType.Origins: self._inputs_out(origins),
                        OriginDestinationCostMatrixOutputDataType.Destinations: self._inputs_out(destinations)},
                       graph.srid, bool(lines))


ODCostMatrix = OriginDestinationCostMatrix


class ServiceArea(_Solver):
    """EQUIVALENT: the network reached within each cutoff by Dijkstra's shortest path, and polygons around it (ADR-226
    §5.4); see ``_network.solve_service_area``. INFERRED: with no cutoffs, 5, 10 and 15, as the page writes them."""

    __grapy_equivalent__ = _EQUIVALENT
    _INPUT_TYPES = tuple(ServiceAreaInputDataType)
    _INPUT_FIELDS = {"Facilities": ["CurbApproach"]}

    def __init__(self, in_network):
        super().__init__(in_network)
        self.defaultImpedanceCutoffs = []
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
        trim = float(self.polygonBufferDistance) * _network._LINEAR[self.polygonBufferDistanceUnits.value.upper()] / per
        lines, polygons = _network.solve_service_area(
            graph, facilities, list(self.defaultImpedanceCutoffs) or [5, 10, 15],
            "FROM_FACILITIES" if self.travelDirection is TravelDirection.FromFacility else "TO_FACILITIES",
            self.geometryAtCutoff is ServiceAreaPolygonCutoffGeometry.Rings, trim)
        for rows in (lines, polygons):
            for n, row in enumerate(rows, start=1):
                row["ObjectID"] = n
        outputs = {ServiceAreaOutputDataType.Facilities: self._inputs_out(facilities)}
        if self.outputType in (ServiceAreaOutputType.Polygons, ServiceAreaOutputType.PolygonsAndLines):
            outputs[ServiceAreaOutputDataType.Polygons] = polygons
        if self.outputType in (ServiceAreaOutputType.Lines, ServiceAreaOutputType.PolygonsAndLines):
            outputs[ServiceAreaOutputDataType.Lines] = lines
        return _Result(outputs, graph.srid, bool(polygons))


# ---- refused by name -----------------------------------------------------------------------------------------------

_HEURISTIC = "is an NP-hard problem whose published heuristic is not chosen yet (ADR-226 §5.2)"
_REASONS = {
    _HEURISTIC: ("LocationAllocation", "LocationAllocationResult", "VehicleRoutingProblem", "VehicleRoutingProblemResult",
                 "LastMileDelivery", "LastMileDeliveryResult", "WasteCollection", "WasteCollectionResult",
                 "LocationAllocationInputDataType", "LocationAllocationOutputDataType", "LocationAllocationProblemType",
                 "VehicleRoutingProblemInputDataType", "VehicleRoutingProblemOutputDataType"),
    "reaches a portal for a web tool's settings, and a fenced tool has no network (ADR-223 §5.3)": ("GetWebToolInfo",),
    "lists the languages of Esri's direction text, whose rules are not published": ("ListDirectionsLanguages",),
}
_REASONS[_HEURISTIC] += ("LastMileDeliveryInputDataType", "LastMileDeliveryOutputDataType", "WasteCollectionInputDataType",
                         "WasteCollectionOutputDataType", "VehicleRoutingProblemInputDataType2",
                         "VehicleRoutingProblemOutputDataType2", "VehicleRoutingProblemSchemaVersion", "DecayFunctionType",
                         "StopCollectionMode")
_REASONS["writes or customises Esri's direction text, landmarks and street names, whose rules are not published"] = (
    "DirectionPoint", "DirectionPointType", "DirectionsCustomizer", "DirectionsFieldMapping", "DirectionsName",
    "DirectionsQuery", "DirectionsStyle", "ReferenceLandmark", "ReferenceLandmarkType", "SpatialLandmark", "LandmarkSide",
    "NameClass", "Importance")
_REASONS["belongs to custom evaluators — Python code computing an attribute's value for each element during a solve — "
         "which are script evaluators, refused (ADR-226 §5.1); NetworkDataset's edges() and junctions() read the "
         "elements"] = (
    "NetworkElement", "Edge", "Junction", "AdjacentEdge", "Attribute", "AttributeEvaluator", "AttributeParameter",
    "AttributeParameterUsage", "AttributeUsage", "NetworkTimeUsage")
_REASONS["is a turn, refused until turns are defined (ADR-226 §5.3)"] = ("Turn",)
_REASONS["is the elements a solve traversed, and grapy's solvers keep routes, not their traversed elements"] = (
    "TraversedEdge", "TraversedElement", "TraversedElementType", "TraversedJunction", "TraversedTurn")
_REASONS["needs turns and driving sides, refused until they are defined (ADR-226 §5.3)"] = ("DrivingSide",)
_REFUSED = {name: reason for reason, names in _REASONS.items() for name in names}
from .na import _REFUSED as _NA_REFUSED  # noqa: E402 — nax documents the same network tools
for _name in ("CreateNetworkDatasetFromTemplate", "CreateTemplateFromNetworkDataset", "CreateTurnFeatureClass",
              "DissolveNetwork", "IncreaseMaximumEdges", "PopulateAlternateIDFields", "ShareAsRouteLayers",
              "UpdateByAlternateIDFields", "UpdateByGeometry"):
    _REFUSED[_name] = _NA_REFUSED[_name]


def __getattr__(name):
    if name.startswith("_"):
        raise AttributeError(name)
    reason = _REFUSED.get(name)
    message = f"arcpy.nax.{name} {reason}." if reason else f"arcpy.nax.{name} is not built in grapy yet."

    def missing(*_, **__):
        raise NotBuiltError(message)

    return missing
