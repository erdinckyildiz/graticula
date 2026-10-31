"""arcpy.na's solver property objects over grapy's analysis layers — ADR-246 (reverses the refusal "grapy's layers
take their settings from the Make tools").

An analysis layer (``na._AnalysisLayer``) holds what its Make tool was given: its ``settings`` and its travel mode.
GetSolverProperties answers the object of the layer's kind, and each property reads or writes those settings, so the
next Solve uses them. The keywords are the pages' (``TRAVEL_FROM``, ``TRUE_LINES_WITH_MEASURES`` …), with True and False
for the two-valued ones, as documented.

Travel mode properties — ``impedance``, ``restrictions``, ``uTurns``, ``timeAttribute``, ``distanceAttribute`` — and
``applyTravelMode`` write the layer's own copy of its travel mode; the network's travel modes do not change.
``attributeParameters`` reads every parameter of the network's attributes with the value a solve would use, and is
written by assigning a new dictionary, as documented. A value grapy's solvers do not build is refused when it is set,
with the reason the Make tool gives for it: time of day (traffic), hierarchy, directions, and the service area and
location-allocation options the Make tools refuse.

INFERRED (ADR-246 §5.6, Q-216): ``orderingType`` is ``PRESERVE_BOTH`` until set while stops keep their order; grapy's
service area polygons answer ``DETAILED_POLYS``; ``defaultCapacity``, ``impedanceParameter``, ``targetMarketShare``,
``timeOfDayUsage``, ``timeZoneUsage``, ``timeZoneUsageForTimeFields``, ``useTimeWindows`` and ``spatialClustering`` are
kept on the layer and change nothing grapy computes.
"""
from __future__ import annotations

import json

from ._runtime import ExecuteError, NotBuiltError

_SOLVER_NAMES = {"Route": "Route Solver", "Closest Facility": "Closest Facility Solver",
                 "Service Area": "Service Area Solver", "OD Cost Matrix": "OD Cost Matrix Solver",
                 "Location-Allocation": "Location-Allocation Solver",
                 "Vehicle Routing Problem": "Vehicle Routing Problem Solver"}


def _word(value, true_word, false_word, allowed=None):
    if value is True:
        return true_word
    if value is False:
        return false_word
    word = str(value).upper().replace(" ", "_")
    choices = allowed or (true_word, false_word)
    if word not in choices:
        raise ValueError(f"'{value}' is not one of {', '.join(choices)}.")
    return word


def _refuse(text):
    raise NotBuiltError(f"arcpy.na solver properties: {text}")


class _SolverProperties:
    _accumulates = True

    def __init__(self, layer):
        self._layer = layer

    @property
    def _settings(self):
        return self._layer.settings

    # ---- the travel mode

    def _mode(self):
        from . import _network
        mode = self._layer.travel_mode
        if isinstance(mode, dict):
            return dict(mode)
        return dict(_network.Graph(self._layer.network, mode).mode)

    def _set_mode(self, **values):
        mode = self._mode()
        mode.update(values)
        self._layer.travel_mode = mode
        self._fields()

    def _fields(self):
        """The cost fields the Make tool writes for the impedance and the accumulated attributes, added to the
        sublayers when a new impedance or accumulation asks for them."""
        from . import _gpkg
        from .management import _schema
        impedance = self._mode()["impedance"]
        names = [impedance, *[a for a in self._settings.get("accumulate") or [] if a != impedance]]
        wanted = {"Route": {"Stops": [f"Cumul_{impedance}"], "Routes": [f"Total_{a}" for a in names]},
                  "Closest Facility": {"CFRoutes": [f"Total_{a}" for a in names]},
                  "OD Cost Matrix": {"ODLines": [f"Total_{a}" for a in names]},
                  "Service Area": {"SALines": [f"FromCumul_{impedance}", f"ToCumul_{impedance}"]},
                  "Location-Allocation": {key: [f"{p}_{a}" for p in ("Total", "TotalWeighted") for a in names]
                                          for key in ("Facilities", "LALines")}}.get(self._layer.solver, {})
        for key, fields in wanted.items():
            path = self._layer.classes[key]
            have = {c.name.lower() for c in _schema(path)[0]}
            for field in fields:
                if field.lower() not in have:
                    _gpkg.add_column(path, _gpkg.Column(field, "Double"))

    def _attributes(self):
        from . import _network
        return _network.attributes(self._layer.network)

    @property
    def solverName(self):
        return _SOLVER_NAMES[self._layer.solver]

    @property
    def travelMode(self):
        from .na import TravelMode
        mode = self._mode()
        return TravelMode({"name": mode.get("name", ""), "impedance": mode["impedance"],
                           "timeAttributeName": mode.get("timeAttributeName"),
                           "distanceAttributeName": mode.get("distanceAttributeName"),
                           "restrictions": list(mode.get("restrictions") or []), "uTurns": mode.get("uTurns"),
                           "attributeParameterValues": [{"attributeName": a, "parameterName": p, "value": v}
                                                        for (a, p), v in _pairs(mode.get("attributeParameters"))]})

    def applyTravelMode(self, travel_mode):
        from . import _network
        from .na import TravelMode, _refuse_mode
        if isinstance(travel_mode, str):
            modes = _network.travel_modes(self._layer.network)
            if travel_mode in modes:
                travel_mode = TravelMode(modes[travel_mode])
            else:
                try:
                    travel_mode = TravelMode(json.loads(travel_mode))
                except (ValueError, TypeError) as failed:
                    raise ValueError(f"'{travel_mode}' cannot be read as a travel mode.") from failed
        elif not isinstance(travel_mode, TravelMode):
            raise TypeError("applyTravelMode takes a travel mode object or its string.")
        _refuse_mode(str(travel_mode))
        self._check_mode(travel_mode)
        self._layer.travel_mode = {"name": travel_mode.name, "impedance": travel_mode.impedance,
                                   "timeAttributeName": travel_mode.timeAttributeName,
                                   "distanceAttributeName": travel_mode.distanceAttributeName,
                                   "restrictions": list(travel_mode.restrictions), "uTurns": travel_mode.uTurns,
                                   "attributeParameters": dict(travel_mode.attributeParameters)}
        self._fields()

    def _check_mode(self, travel_mode):
        return None

    @property
    def impedance(self):
        return self._mode()["impedance"]

    @impedance.setter
    def impedance(self, value):
        if self._attributes().get(str(value), {}).get("usage") != "Cost":
            raise ValueError(f"'{value}' is not a cost attribute of the network.")
        self._set_mode(impedance=str(value))

    @property
    def restrictions(self):
        return list(self._mode().get("restrictions") or [])

    @restrictions.setter
    def restrictions(self, value):
        names = [value] if isinstance(value, str) else list(value or [])
        known = self._attributes()
        for name in names:
            if known.get(name, {}).get("usage") != "Restriction":
                raise ValueError(f"'{name}' is not a restriction attribute of the network.")
        self._set_mode(restrictions=names)

    @property
    def uTurns(self):
        return str(self._mode().get("uTurns") or "ALLOW_UTURNS").upper()

    @uTurns.setter
    def uTurns(self, value):
        from ._turns import policy
        word = str(value).upper()
        if word not in ("ALLOW_UTURNS", "NO_UTURNS", "ALLOW_DEAD_ENDS_ONLY", "ALLOW_DEAD_ENDS_AND_INTERSECTIONS_ONLY"):
            raise ValueError(f"'{value}' is not a U-turn policy.")
        policy(word)
        self._set_mode(uTurns=word)

    @property
    def useHierarchy(self):
        return "NO_HIERARCHY"

    @useHierarchy.setter
    def useHierarchy(self, value):
        if _word(value, "USE_HIERARCHY", "NO_HIERARCHY") == "USE_HIERARCHY":
            _refuse("hierarchy is refused until it is defined (ADR-226 §5.3).")

    @property
    def attributeParameters(self):
        from . import _evaluators
        defined = _evaluators.parameters(self._layer.network)
        if not defined:
            return None
        values = {key: spec["value"] for key, spec in defined.items()}
        values.update(_evaluators.parameter_values(self._mode().get("attributeParameters")))
        values.update(self._settings.get("attribute_parameters") or {})
        return values

    @attributeParameters.setter
    def attributeParameters(self, value):
        from . import _evaluators
        defined = _evaluators.parameters(self._layer.network)
        values = _evaluators.parameter_values(value)
        for key in values:
            if key not in defined:
                raise ValueError(f"The network attribute '{key[0]}' has no parameter '{key[1]}'.")
        self._settings["attribute_parameters"] = values

    @property
    def ignoreInvalidLocations(self):
        return "HALT" if str(self._settings.get("ignore") or "SKIP").upper() in ("HALT", "FALSE") else "SKIP"

    @ignoreInvalidLocations.setter
    def ignoreInvalidLocations(self, value):
        self._settings["ignore"] = _word(value, "SKIP", "HALT")

    @property
    def accumulators(self):
        return list(self._settings.get("accumulate") or [])

    @accumulators.setter
    def accumulators(self, value):
        names = [value] if isinstance(value, str) else list(value or [])
        if names and not self._accumulates:
            _refuse("accumulated attributes are not built in grapy for this layer.")
        known = self._attributes()
        for name in names:
            if known.get(name, {}).get("usage") != "Cost":
                raise ValueError(f"'{name}' is not a cost attribute of the network.")
        self._settings["accumulate"] = names
        self._fields()

    @property
    def timeOfDay(self):
        return None

    @timeOfDay.setter
    def timeOfDay(self, value):
        if value is not None:
            _refuse("timeOfDay needs traffic, refused until it is defined (ADR-226 §5.3).")

    @property
    def timeZoneUsage(self):
        return self._settings.get("time_zone_usage", "GEO_LOCAL")

    @timeZoneUsage.setter
    def timeZoneUsage(self, value):
        self._settings["time_zone_usage"] = _word(value, "GEO_LOCAL", "UTC", ("GEO_LOCAL", "UTC"))

    # ---- shared shapes

    def _path_shape(self, allowed):
        shape = str(self._settings.get("line_shape") or allowed[0]).upper()
        return {"ALONG_NETWORK": "TRUE_LINES_WITH_MEASURES"}.get(shape, shape)

    def _set_path_shape(self, value, allowed):
        word = str(value).upper()
        if word not in allowed:
            raise ValueError(f"'{value}' is not one of {', '.join(allowed)}.")
        self._settings["line_shape"] = "ALONG_NETWORK" if word.startswith("TRUE_LINES") else word

    def _streets(self):
        return None

    def _set_streets(self, value):
        if value is not None:
            _refuse("directions are written by Esri's direction rules, which are not published.")


def _pairs(values):
    from . import _evaluators
    return list(_evaluators.parameter_values(values).items())


_TRUE_SHAPES = ("TRUE_LINES_WITH_MEASURES", "TRUE_LINES_WITHOUT_MEASURES", "STRAIGHT_LINES", "NO_LINES")
_STRAIGHT_SHAPES = ("NO_LINES", "STRAIGHT_LINES")


class RouteSolverProperties(_SolverProperties):
    """A route layer's settings."""

    outputPathShape = property(lambda self: self._path_shape(_TRUE_SHAPES),
                               lambda self, v: self._set_path_shape(v, _TRUE_SHAPES))
    streetDirectionsProperties = property(_SolverProperties._streets, _SolverProperties._set_streets)

    @property
    def findBestSequence(self):
        return "USE_INPUT_ORDER" if self._settings.get("sequence", "USE_CURRENT_ORDER") == "USE_CURRENT_ORDER" \
            else "FIND_BEST_ORDER"

    @findBestSequence.setter
    def findBestSequence(self, value):
        word = _word(value, "FIND_BEST_ORDER", "USE_INPUT_ORDER")
        ordering = self.orderingType
        self._settings["ordering"] = ordering
        self._settings["sequence"] = "USE_CURRENT_ORDER" if word == "USE_INPUT_ORDER" else \
            ("FIND_BEST_ORDER" if ordering == "PRESERVE_NONE" else ordering)

    @property
    def orderingType(self):
        sequence = self._settings.get("sequence", "USE_CURRENT_ORDER")
        if sequence == "FIND_BEST_ORDER":
            return "PRESERVE_NONE"
        if sequence.startswith("PRESERVE_"):
            return sequence
        return self._settings.get("ordering", "PRESERVE_BOTH")

    @orderingType.setter
    def orderingType(self, value):
        word = str(value).upper()
        if word not in ("PRESERVE_BOTH", "PRESERVE_FIRST", "PRESERVE_LAST", "PRESERVE_NONE"):
            raise ValueError(f"'{value}' is not an ordering type.")
        self._settings["ordering"] = word
        if self._settings.get("sequence", "USE_CURRENT_ORDER") != "USE_CURRENT_ORDER":
            self._settings["sequence"] = "FIND_BEST_ORDER" if word == "PRESERVE_NONE" else word

    @property
    def timeZoneUsageForTimeFields(self):
        return self._settings.get("time_zone_for_time_fields", "GEO_LOCAL")

    @timeZoneUsageForTimeFields.setter
    def timeZoneUsageForTimeFields(self, value):
        self._settings["time_zone_for_time_fields"] = _word(value, "GEO_LOCAL", "UTC", ("GEO_LOCAL", "UTC"))

    @property
    def useTimeWindows(self):
        return self._settings.get("use_time_windows", "USE_TIMEWINDOWS")

    @useTimeWindows.setter
    def useTimeWindows(self, value):
        self._settings["use_time_windows"] = _word(value, "USE_TIMEWINDOWS", "NO_TIMEWINDOWS")


class _Direction:
    _TO, _FROM = "TRAVEL_TO", "TRAVEL_FROM"

    @property
    def travelDirection(self):
        away = str(self._settings.get("direction") or self._DEFAULT).upper() == "FROM_FACILITIES"
        return self._FROM if away else self._TO

    @travelDirection.setter
    def travelDirection(self, value):
        word = str(value).upper()
        if word not in (self._TO, self._FROM):
            raise ValueError(f"'{value}' is not {self._FROM} or {self._TO}.")
        self._settings["direction"] = "FROM_FACILITIES" if word == self._FROM else "TO_FACILITIES"


def _number(value, kind=float):
    return None if value is None else kind(value)


class ClosestFacilitySolverProperties(_Direction, _SolverProperties):
    """A closest facility layer's settings."""

    _DEFAULT = "TO_FACILITIES"
    outputPathShape = property(lambda self: self._path_shape(_TRUE_SHAPES),
                               lambda self, v: self._set_path_shape(v, _TRUE_SHAPES))
    streetDirectionsProperties = property(_SolverProperties._streets, _SolverProperties._set_streets)
    defaultCutoff = property(lambda self: self._settings.get("cutoff"),
                             lambda self, v: self._settings.__setitem__("cutoff", _number(v)))
    defaultTargetFacilityCount = property(lambda self: self._settings.get("count", 1),
                                          lambda self, v: self._settings.__setitem__("count", int(v)))

    @property
    def timeOfDayUsage(self):
        return self._settings.get("time_of_day_usage", "START_TIME")

    @timeOfDayUsage.setter
    def timeOfDayUsage(self, value):
        self._settings["time_of_day_usage"] = _word(value, "START_TIME", "END_TIME",
                                                    ("START_TIME", "END_TIME", "NOT_USED"))


class ServiceAreaSolverProperties(_Direction, _SolverProperties):
    """A service area layer's settings. Only what the Make tool builds can be set: overlapping lines and polygons from
    every source, split at breaks, trimmed."""

    _DEFAULT = "FROM_FACILITIES"
    _accumulates = False

    def _outputs(self):
        output = str(self._settings.get("output") or "POLYGONS").upper()
        return "POLYGONS" in output, "LINES" in output

    def _set_outputs(self, polygons, lines):
        if lines and self._settings.get("direction", "FROM_FACILITIES").upper() == "TO_FACILITIES":
            _refuse("lines toward the facilities are not built in grapy.")
        self._settings["output"] = "POLYGONS_AND_LINES" if polygons and lines else "LINES" if lines else \
            "POLYGONS" if polygons else "NONE"

    @property
    def defaultBreaks(self):
        return list(self._settings.get("breaks") or [])

    @defaultBreaks.setter
    def defaultBreaks(self, value):
        self._settings["breaks"] = sorted(float(v) for v in ([value] if isinstance(value, (int, float)) else value))

    @property
    def excludeSources(self):
        return []

    @excludeSources.setter
    def excludeSources(self, value):
        if value:
            _refuse("excluded sources are not built in grapy.")

    @property
    def includeNetworkSourceFields(self):
        return "LINES_SOURCE_FIELDS"

    @includeNetworkSourceFields.setter
    def includeNetworkSourceFields(self, value):
        if _word(value, "LINES_SOURCE_FIELDS", "NO_LINES_SOURCE_FIELDS") != "LINES_SOURCE_FIELDS":
            _refuse("grapy's service area lines always carry their source fields.")

    @property
    def lineOverlap(self):
        return "OVERLAP"

    @lineOverlap.setter
    def lineOverlap(self, value):
        if _word(value, "OVERLAP", "NON_OVERLAP") != "OVERLAP":
            _refuse("non-overlapping lines are not built in grapy.")

    @property
    def lineType(self):
        return "TRUE_LINES" if self._outputs()[1] else "NO_LINES"

    @lineType.setter
    def lineType(self, value):
        word = str(value).upper()
        if word not in ("NO_LINES", "TRUE_LINES", "TRUE_LINES_WITH_MEASURES"):
            raise ValueError(f"'{value}' is not a service area line type.")
        self._set_outputs(self._outputs()[0], word != "NO_LINES")

    @property
    def polygonType(self):
        return "DETAILED_POLYS" if self._outputs()[0] else "NO_POLYS"

    @polygonType.setter
    def polygonType(self, value):
        word = str(value).upper()
        if word == "SIMPLE_POLYS":
            _refuse("generalized polygons are not built in grapy.")
        if word not in ("DETAILED_POLYS", "NO_POLYS"):
            raise ValueError(f"'{value}' is not a service area polygon type.")
        self._set_outputs(word == "DETAILED_POLYS", self._outputs()[1])

    @property
    def polygonMerge(self):
        return "NO_MERGE"

    @polygonMerge.setter
    def polygonMerge(self, value):
        if str(value).upper() != "NO_MERGE":
            _refuse(f"polygon merge '{value}' is not built in grapy; overlapping polygons (NO_MERGE) are.")

    @property
    def polygonNesting(self):
        return "RINGS" if self._settings.get("rings", True) else "DISKS"

    @polygonNesting.setter
    def polygonNesting(self, value):
        self._settings["rings"] = _word(value, "RINGS", "DISKS") == "RINGS"

    @property
    def splitLinesAtBreaks(self):
        return "SPLIT"

    @splitLinesAtBreaks.setter
    def splitLinesAtBreaks(self, value):
        if _word(value, "SPLIT", "NO_SPLIT") != "SPLIT":
            _refuse("grapy's service area lines are always split at the breaks.")

    @property
    def trimDistance(self):
        return str(self._settings.get("trim") or "100 Meters")

    @trimDistance.setter
    def trimDistance(self, value):
        self._settings["trim"] = str(value)

    @property
    def trimPolygons(self):
        return "TRIM_POLYS"

    @trimPolygons.setter
    def trimPolygons(self, value):
        if _word(value, "TRIM_POLYS", "NO_TRIM_POLYS") != "TRIM_POLYS":
            _refuse("grapy's polygons are always the reached lines buffered by the trim distance.")


class ODCostMatrixSolverProperties(_SolverProperties):
    """An OD cost matrix layer's settings."""

    outputPathShape = property(lambda self: self._path_shape(_STRAIGHT_SHAPES[::-1]),
                               lambda self, v: self._set_path_shape(v, _STRAIGHT_SHAPES))
    defaultCutoff = property(lambda self: self._settings.get("cutoff"),
                             lambda self, v: self._settings.__setitem__("cutoff", _number(v)))
    defaultTargetDestinationCount = property(lambda self: self._settings.get("count"),
                                             lambda self, v: self._settings.__setitem__("count", _number(v, int)))


class LocationAllocationSolverProperties(_SolverProperties):
    """A location-allocation layer's settings. Only the minimize impedance problem with linear decay is built."""

    outputPathShape = property(lambda self: self._path_shape(_STRAIGHT_SHAPES[::-1]),
                               lambda self, v: self._set_path_shape(v, _STRAIGHT_SHAPES))
    facilitiesToFind = property(lambda self: self._settings.get("count", 1),
                                lambda self, v: self._settings.__setitem__("count", int(v)))
    impedanceCutoff = property(lambda self: self._settings.get("cutoff"),
                               lambda self, v: self._settings.__setitem__("cutoff", _number(v)))
    defaultCapacity = property(lambda self: self._settings.get("capacity", 1.0),
                               lambda self, v: self._settings.__setitem__("capacity", _number(v)))
    impedanceParameter = property(lambda self: self._settings.get("decay_parameter", 1.0),
                                  lambda self, v: self._settings.__setitem__("decay_parameter", _number(v)))
    targetMarketShare = property(lambda self: self._settings.get("market_share", 10.0),
                                 lambda self, v: self._settings.__setitem__("market_share", _number(v)))

    @property
    def problemType(self):
        return "MINIMIZE_IMPEDANCE"

    @problemType.setter
    def problemType(self, value):
        if str(value).upper() != "MINIMIZE_IMPEDANCE":
            _refuse(f"problem type '{value}' is not built in grapy; MINIMIZE_IMPEDANCE is.")

    @property
    def impedanceTransformation(self):
        return "LINEAR"

    @impedanceTransformation.setter
    def impedanceTransformation(self, value):
        if str(value).upper() != "LINEAR":
            _refuse(f"impedance transformation '{value}' is not built in grapy; LINEAR is.")


class VehicleRoutingProblemSolverProperties(_SolverProperties):
    """A vehicle routing problem layer's settings; its travel mode must be time based, as documented."""

    _accumulates = False
    outputPathShape = property(lambda self: self._path_shape(_TRUE_SHAPES),
                               lambda self, v: self._set_path_shape(v, _TRUE_SHAPES))
    streetDirectionsProperties = property(_SolverProperties._streets, _SolverProperties._set_streets)
    defaultDate = property(lambda self: self._settings.get("default_date"),
                           lambda self, v: self._settings.__setitem__("default_date", v))

    def _check_mode(self, travel_mode):
        units = self._attributes().get(travel_mode.impedance, {}).get("units")
        if str(units or "").upper() not in ("SECONDS", "MINUTES", "HOURS", "DAYS"):
            raise ValueError("A vehicle routing problem takes a time-based travel mode only.")

    @property
    def timeAttribute(self):
        mode = self._mode()
        return mode.get("timeAttributeName") or mode["impedance"]

    @timeAttribute.setter
    def timeAttribute(self, value):
        units = self._attributes().get(str(value), {}).get("units")
        if str(units or "").upper() not in ("SECONDS", "MINUTES", "HOURS", "DAYS"):
            raise ValueError(f"'{value}' is not a time cost attribute of the network.")
        self._set_mode(impedance=str(value), timeAttributeName=str(value))

    @property
    def distanceAttribute(self):
        return self._mode().get("distanceAttributeName")

    @distanceAttribute.setter
    def distanceAttribute(self, value):
        if self._attributes().get(str(value), {}).get("usage") != "Cost":
            raise ValueError(f"'{value}' is not a cost attribute of the network.")
        self._set_mode(distanceAttributeName=str(value))

    @property
    def distanceFieldUnits(self):
        return str(self._settings.get("distance_units") or "Miles")

    @distanceFieldUnits.setter
    def distanceFieldUnits(self, value):
        allowed = ("Centimeters", "Decimeters", "Feet", "Inches", "Kilometers", "Meters", "Miles", "Millimeters",
                   "NauticalMiles", "Yards")
        found = next((u for u in allowed if u.upper() == str(value).upper()), None)
        if found is None:
            raise ValueError(f"'{value}' is not a distance unit.")
        self._settings["distance_units"] = found

    @property
    def timeFieldUnits(self):
        return str(self._settings.get("time_units") or "Minutes")

    @timeFieldUnits.setter
    def timeFieldUnits(self, value):
        found = next((u for u in ("Days", "Hours", "Minutes", "Seconds") if u.upper() == str(value).upper()), None)
        if found is None:
            raise ValueError(f"'{value}' is not a time unit.")
        self._settings["time_units"] = found

    def _importance(self, key):
        return str(getattr(self._settings.get(key, "Medium"), "value", self._settings.get(key, "Medium"))).title()

    def _set_importance(self, key, value):
        word = str(getattr(value, "value", value)).title()
        if word not in ("High", "Medium", "Low"):
            raise ValueError(f"'{value}' is not High, Medium or Low.")
        self._settings[key] = word

    excessTransitTimeImportance = property(lambda self: self._importance("excess_transit_factor"),
                                           lambda self, v: self._set_importance("excess_transit_factor", v))
    timeWindowViolationImportance = property(lambda self: self._importance("time_window_factor"),
                                             lambda self, v: self._set_importance("time_window_factor", v))

    @property
    def spatialClustering(self):
        return self._settings.get("spatial_clustering", "CLUSTER")

    @spatialClustering.setter
    def spatialClustering(self, value):
        self._settings["spatial_clustering"] = _word(value, "CLUSTER", "NO_CLUSTER")

    @property
    def timeZoneUsageForTimeFields(self):
        return self._settings.get("time_zone_for_time_fields", "GEO_LOCAL")

    @timeZoneUsageForTimeFields.setter
    def timeZoneUsageForTimeFields(self, value):
        self._settings["time_zone_for_time_fields"] = _word(value, "GEO_LOCAL", "UTC", ("GEO_LOCAL", "UTC"))


# Location-allocation's direction keywords are its own.
LocationAllocationSolverProperties.travelDirection = property(
    lambda self: "FACILITY_TO_DEMAND" if str(self._settings.get("direction") or "FROM_FACILITIES").upper() ==
    "FROM_FACILITIES" else "DEMAND_TO_FACILITY",
    lambda self, v: self._settings.__setitem__("direction", {"FACILITY_TO_DEMAND": "FROM_FACILITIES",
                                                             "DEMAND_TO_FACILITY": "TO_FACILITIES"}[str(v).upper()]))

_KINDS = {"Route": RouteSolverProperties, "Closest Facility": ClosestFacilitySolverProperties,
          "Service Area": ServiceAreaSolverProperties, "OD Cost Matrix": ODCostMatrixSolverProperties,
          "Location-Allocation": LocationAllocationSolverProperties,
          "Vehicle Routing Problem": VehicleRoutingProblemSolverProperties}


def of(layer):
    kind = _KINDS.get(layer.solver)
    if kind is None:
        raise ExecuteError(f"ERROR 030000: GetSolverProperties does not support a {layer.solver.lower()} layer, as "
                           "documented.")
    return kind(layer)
