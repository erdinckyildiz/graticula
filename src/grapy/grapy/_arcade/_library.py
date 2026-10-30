"""Arcade's function library in grapy (ADR-235 §5.3): the registry of built functions, the constants, and the catalogue
of every function the public function reference documents — each one built, scheduled with its reason, or refused
with its reason. A documented function that is not built raises an error naming it and the reason; it never answers
silently wrong.

The catalogue was read from developers.arcgis.com/arcade/function-reference on 2026-10-30, category by category.
"""
from __future__ import annotations

import math

from ._errors import ArcadeError, NotBuiltFunction
from ._values import Dictionary, Function

_REGISTRY = {}   # lower-case name -> [(accepts or None, implementation)]
_CANONICAL = {}  # lower-case name -> the documented spelling


def builtin(name, accepts=None):
    """Registers an implementation of ``name``. ``accepts(args)`` picks an overload (by the first argument's type, as
    the function reference splits them); one without it answers whatever the others do not."""
    def register(function):
        entries = _REGISTRY.setdefault(name.lower(), [])
        entries.append((accepts, function))
        entries.sort(key=lambda entry: entry[0] is None)
        _CANONICAL[name.lower()] = name
        return function
    return register


def _dispatch(name):
    lower = name.lower()

    def call(interpreter, args):
        for accepts, function in _REGISTRY[lower]:
            if accepts is None or accepts(args):
                return function(interpreter, args)
        not_built(name, interpreter.line, args)
    return call


def functions():
    """{lower-case name: Function} of every built function."""
    _load()
    return {lower: Function(_CANONICAL[lower], call=_dispatch(_CANONICAL[lower])) for lower in _REGISTRY}


def constants():
    return {"PI": math.pi, "Infinity": math.inf, "NaN": math.nan,
            "TextFormatting": Dictionary({"NewLine": "\n", "Tab": "\t", "SingleQuote": "'", "DoubleQuote": '"',
                                          "BackwardSlash": "\\", "ForwardSlash": "/", "LeftParenthesis": "(",
                                          "RightParenthesis": ")", "LeftSquareBracket": "[", "RightSquareBracket": "]",
                                          "LeftCurlyBracket": "{", "RightCurlyBracket": "}", "LessThan": "<",
                                          "GreaterThan": ">", "NewParagraph": "\n\n"}, immutable=True)}


_LOADED = []


def _load():
    if not _LOADED:
        from . import _fn_core, _fn_date, _geometry, _data  # noqa: F401 — each registers its functions
        _LOADED.append(True)


# ---- the catalogue ---------------------------------------------------------------------------------------------------
#
# Every function of the function reference, by category. A name built for one category's overload and not another's
# (Count on a pixel) answers through the overload's own refusal.

DOCUMENTED = {
    "AI": ["TranslateText"],
    "Array": ["All", "Any", "Array", "Back", "Count", "DefaultValue", "Distinct", "Erase", "Filter", "First", "Front",
              "HasValue", "Includes", "IndexOf", "Insert", "Map", "None", "Pop", "Push", "Reduce", "Resize", "Reverse",
              "Slice", "Sort", "Splice", "Top"],
    "Date": ["ChangeTimeZone", "Date", "DateAdd", "DateDiff", "DateOnly", "Day", "Hour", "ISOMonth", "ISOWeek",
             "ISOWeekday", "ISOYear", "Millisecond", "Minute", "Month", "Now", "Second", "Time", "Timestamp",
             "TimeZone", "TimeZoneOffset", "Today", "ToLocal", "ToUTC", "Week", "Weekday", "Year"],
    "Debugging": ["Console", "GetEnvironment"],
    "Dictionary": ["Count", "DefaultValue", "Dictionary", "Erase", "FromJSON", "GetKeys", "GetValues", "HasKey",
                   "HasValue", "Insert"],
    "Enterprise": ["NextSequenceValue"],
    "Feature": ["DefaultValue", "Domain", "DomainCode", "DomainName", "Expects", "Feature", "FeatureInFilter",
                "GdbVersion", "HasKey", "HasValue", "Schema", "SubtypeCode", "SubtypeName", "Subtypes", "TimeReceived"],
    "FeatureSet": ["Area", "AreaGeodetic", "Attachments", "Average", "Contains", "Count", "Crosses", "Distinct",
                   "Domain", "DomainCode", "DomainName", "EnvelopeIntersects", "Expects", "FeatureSet",
                   "FeatureSetByAssociation", "FeatureSetById", "FeatureSetByName", "FeatureSetByRelationshipClass",
                   "FeatureSetByRelationshipName", "Filter", "FilterBySubtypeCode", "First", "GdbVersion",
                   "GetFeatureSet", "GetFeatureSetInfo", "GetUser", "GroupBy", "Intersects", "Length", "Length3D",
                   "LengthGeodetic", "Max", "Mean", "Min", "OrderBy", "Overlaps", "Schema", "Stdev", "Subtypes", "Sum",
                   "Top", "Touches", "Variance", "Within"],
    "Geometry": ["Angle", "Area", "AreaGeodetic", "Bearing", "Buffer", "BufferGeodetic", "Centroid", "Clip", "Contains",
                 "ConvertDirection", "ConvexHull", "Crosses", "Cut", "DefaultValue", "Densify", "DensifyGeodetic",
                 "Difference", "Disjoint", "Distance", "DistanceGeodetic", "DistanceToCoordinate",
                 "EnvelopeIntersects", "Equals", "Extent", "Generalize", "Geometry", "HasValue", "Intersection",
                 "Intersects", "IsSelfIntersecting", "IsSimple", "Length", "Length3D", "LengthGeodetic",
                 "MeasureToCoordinate", "MultiPartToSinglePart", "Multipoint", "NearestCoordinate", "NearestVertex",
                 "Offset", "Overlaps", "Point", "PointToCoordinate", "Polygon", "Polyline", "Relate",
                 "RingIsClockwise", "Rotate", "SetGeometry", "Simplify", "SymmetricDifference", "Touches", "Union",
                 "Within"],
    "Knowledge graph": ["KnowledgeGraphByPortalItem", "QueryGraph"],
    "Logical": ["Boolean", "Decode", "DefaultValue", "Equals", "IIf", "IsEmpty", "IsNan", "TypeOf", "When"],
    "Math": ["Abs", "Acos", "Asin", "Atan", "Atan2", "Average", "Ceil", "Constrain", "Cos", "Exp", "Floor", "Hash",
             "Log", "Max", "Mean", "Min", "Number", "Pow", "Product", "Random", "Round", "Sign", "Sin", "Sqrt", "Stdev",
             "Sum", "Tan", "Variance"],
    "Pixel": ["Count", "DefaultValue", "GetKeys", "GetValues", "HasKey", "HasValue"],
    "Portal": ["FeatureSetByPortalItem", "GetUser", "Portal"],
    "Text": ["Concatenate", "Count", "Find", "FromCharCode", "FromCodePoint", "Guid", "Left", "Lower", "Mid", "Proper",
             "Replace", "Right", "Split", "StandardizeFilename", "StandardizeGuid", "Text", "ToCharCode", "ToCodePoint",
             "ToHex", "Trim", "Upper", "UrlEncode"],
    "Track": ["TrackAccelerationAt", "TrackAccelerationWindow", "TrackCurrentAcceleration", "TrackCurrentDistance",
              "TrackCurrentSpeed", "TrackCurrentTime", "TrackDistanceAt", "TrackDistanceWindow", "TrackDuration",
              "TrackFieldWindow", "TrackGeometryWindow", "TrackIndex", "TrackSpeedAt", "TrackSpeedWindow",
              "TrackStartTime", "TrackWindow"],
    "Voxel": ["Count", "DefaultValue", "GetKeys", "GetValues", "HasKey", "HasValue"],
}

_NETWORK = "a user tool runs fenced, with no network and no portal (ADR-223)"

# Not built, and not going to be: each with its reason.
REFUSED = {
    "TranslateText": f"it calls an AI translation service, and {_NETWORK}",
    "FeatureSetByPortalItem": _NETWORK,
    "Portal": _NETWORK,
    "KnowledgeGraphByPortalItem": f"knowledge graphs are a data model grapy does not hold, and {_NETWORK}",
    "QueryGraph": "knowledge graphs are a data model grapy does not hold",
    "FeatureSetById": "a layer id belongs to a map or a feature service ($map, $layer); an attribute rule's profile "
                      "reaches its GeoPackage through $datastore and FeatureSetByName",
    "FeatureSetByRelationshipName": "a relationship name belongs to a feature service; a GeoPackage's relationship "
                                    "classes are reached with FeatureSetByRelationshipClass",
    "GetFeatureSet": "the source FeatureSet of a feature belongs to map and service profiles; a rule's $feature is "
                     "read from its table, which FeatureSetByName opens",
    "GetFeatureSetInfo": "its answer is a service's or a geodatabase's metadata (item id, service URL, objectClassId), "
                         "which a GeoPackage does not have",
    "Hash": "its hash algorithm is not documented (the page's example value is not Java's or a common string hash), "
            "and a hash that differs from ArcGIS's answers nothing a toolbox can compare",
    **{name: "track functions run only in GeoAnalytics tools on track-aware data, which grapy's tools do not offer"
       for name in DOCUMENTED["Track"]},
}

# Not built in this batch: each with what it waits for. ADR-235 §5.3 lists them.
SCHEDULED = {
    "FeatureSetByAssociation": "utility network associations (ADR-231) are not yet reached from Arcade",
    "FeatureSetByRelationshipClass": "relationship classes (ADR-225) are not yet reached from Arcade",
    "Attachments": "attachments (ADR-228) are not yet reached from Arcade",
    "GroupBy": "its statistics over SQL expressions are not yet built",
    "BufferGeodetic": "a geodesic buffer needs an equivalent method of its own (ADR-224 §5.6)",
    "DensifyGeodetic": "geodesic densification needs an equivalent method of its own (ADR-224 §5.6)",
    "ConvertDirection": "its direction notations (QUADRANT_BEARING, DMS and the rest) are not yet built",
    "Cut": "the left and right parts and the order of the answer are not yet built",
    "DistanceToCoordinate": "not yet built",
    "MeasureToCoordinate": "grapy's 2D shapes carry no m-values through Arcade yet",
    "PointToCoordinate": "not yet built",
    "NearestCoordinate": "sideOfLine is not yet built",
    "NearestVertex": "sideOfLine is not yet built",
    "Offset": "its join types and bevel ratio are not yet built",
    "Generalize": "its simplification method is not documented; it needs an equivalent (ADR-224 §5.6)",
    "Length3D": "grapy's 2D shapes carry no z-values through Arcade yet",
    "FeatureSet": "a FeatureSet built from REST JSON is an in-memory table, which grapy's FeatureSets on a GeoPackage "
                  "table do not yet hold",
}


def status(name):
    """'built', 'scheduled', 'refused' or 'unknown' for a function name."""
    _load()
    lower = name.lower()
    if lower in _REGISTRY:
        return "built"
    for table, word in ((SCHEDULED, "scheduled"), (REFUSED, "refused")):
        if any(k.lower() == lower for k in table):
            return word
    return "unknown"


def not_built(name, line=None, args=None):
    """Raises the error a documented function that is not built answers — or, for an overload not built, that one."""
    lower = name.lower()
    for table, word in ((REFUSED, "is refused in grapy"), (SCHEDULED, "is not built in grapy yet")):
        for key, reason in table.items():
            if key.lower() == lower:
                raise NotBuiltFunction(f"The Arcade function {key} {word}: {reason}.", line)
    if lower in _REGISTRY:
        from ._values import type_of
        shown = ", ".join(type_of(a) for a in (args or []))
        raise NotBuiltFunction(f"The Arcade function {_CANONICAL[lower]} is not built in grapy for ({shown}).", line)
    documented = any(lower == n.lower() for names in DOCUMENTED.values() for n in names)
    if documented:
        raise NotBuiltFunction(f"The Arcade function {name} is not built in grapy yet.", line)
    raise ArcadeError(f"'{name}' is not a function.", line)


def coverage():
    """{documented name: 'built' | 'scheduled' | 'refused'} — what ADR-235 §5.3 counts."""
    names = sorted({n for names in DOCUMENTED.values() for n in names}, key=str.lower)
    return {n: status(n) for n in names}
