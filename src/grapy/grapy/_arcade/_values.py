"""Arcade's types and the casting rules of the language guide's *Types* and *Type casting* pages (ADR-235).

Python holds them as: Number — ``float`` (an ``int`` read from a table is a Number too); Text — ``str``; Boolean —
``bool``; null — ``None``; Array — ``list``; and the classes below for Dictionary, Feature, Geometry, Date, DateOnly
and Time. FeatureSets are in ``_data``.
"""
from __future__ import annotations

import datetime
import json
import math
import re

from ._errors import ArcadeError

UTC = datetime.timezone.utc
_DECIMAL = re.compile(r"^\s*[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?\s*$")


def is_number(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool)


def number_text(value):
    """A Number as text: integral values without a decimal point, NaN and Infinity by name."""
    value = float(value)
    if math.isnan(value):
        return "NaN"
    if math.isinf(value):
        return "Infinity" if value > 0 else "-Infinity"
    if value == int(value) and abs(value) < 1e21:
        return str(int(value))
    text = repr(value)
    if "e" in text:  # 1e-07 → 1e-7, as decimal representations are written
        mantissa, exponent = text.split("e")
        text = f"{mantissa}e{'+' if not exponent.startswith('-') else '-'}{int(exponent.lstrip('+-'))}"
    return text


class Dictionary:
    """A Dictionary. Keys are case-insensitive, as the Types page says; each keeps the case it was first given."""

    __slots__ = ("_items", "immutable")

    def __init__(self, items=None, immutable=False):
        self._items = {}
        self.immutable = False
        for key, value in (items.items() if isinstance(items, dict) else items or ()):
            self.set(key, value)
        self.immutable = immutable

    def get(self, key, missing=None):
        found = self._items.get(str(key).lower())
        return missing if found is None else found[1]

    def has(self, key):
        return str(key).lower() in self._items

    def set(self, key, value):
        if self.immutable:
            raise ArcadeError("This dictionary cannot be changed: it is immutable.")
        lower = str(key).lower()
        kept = self._items.get(lower)
        self._items[lower] = (kept[0] if kept else str(key), value)

    def erase(self, key):
        if self.immutable:
            raise ArcadeError("This dictionary cannot be changed: it is immutable.")
        if str(key).lower() not in self._items:
            raise ArcadeError(f"The dictionary has no key '{key}'.")
        del self._items[str(key).lower()]

    def keys(self):
        return [k for k, _ in self._items.values()]

    def items(self):
        return list(self._items.values())

    def __len__(self):
        return len(self._items)


class Geometry:
    """An Arcade geometry: a shapely shape and the well-known ID of its spatial reference. ``kind`` is Point,
    Multipoint, Polyline, Polygon or Extent. Geometries are immutable, as the Types page says."""

    __slots__ = ("shape", "wkid", "kind")

    def __init__(self, shape, wkid=None, kind=None):
        self.shape, self.wkid = shape, wkid
        self.kind = kind or _kind_of(shape)


def _kind_of(shape):
    name = shape.geom_type
    return {"Point": "Point", "MultiPoint": "Multipoint", "LineString": "Polyline", "MultiLineString": "Polyline",
            "Polygon": "Polygon", "MultiPolygon": "Polygon", "LinearRing": "Polyline"}.get(name, "Polygon")


class Feature:
    """A Feature: attributes (a Dictionary) and an optional geometry. A profile's ``$feature`` is immutable. ``source``
    is the (datastore, table) it was read from, when it was, so Schema, Domain and Subtype functions can answer."""

    __slots__ = ("attributes", "geometry", "immutable", "source")

    def __init__(self, attributes, geometry=None, immutable=False, source=None):
        self.attributes = attributes if isinstance(attributes, Dictionary) else Dictionary(attributes)
        self.geometry, self.immutable, self.source = geometry, immutable, source

    def field(self, name):
        if not self.attributes.has(name):
            raise ArcadeError(f"The feature has no field '{name}'.")
        return self.attributes.get(name)


class Date:
    """A Date: an aware datetime. INFERRED (ADR-235 §5.8): the execution time zone of a rule run by grapy is UTC, and a
    date read from a GeoPackage, which stores none, is read as UTC."""

    __slots__ = ("value",)

    def __init__(self, value):
        if value.tzinfo is None:
            value = value.replace(tzinfo=UTC)
        self.value = value

    @property
    def epoch(self):
        return self.value.timestamp() * 1000.0

    def iso(self):
        moment = self.value
        text = moment.strftime("%Y-%m-%dT%H:%M:%S")
        if moment.microsecond:
            text += f".{moment.microsecond // 1000:03d}"
        offset = moment.utcoffset() or datetime.timedelta(0)
        minutes = int(offset.total_seconds() // 60)
        sign = "+" if minutes >= 0 else "-"
        return text + f"{sign}{abs(minutes) // 60:02d}:{abs(minutes) % 60:02d}"


class DateOnly:
    __slots__ = ("value",)

    def __init__(self, value):
        self.value = value  # datetime.date

    def iso(self):
        return self.value.isoformat()


class Time:
    __slots__ = ("ms",)

    def __init__(self, ms):
        self.ms = int(ms)  # milliseconds since midnight

    def iso(self):
        seconds, ms = divmod(self.ms, 1000)
        text = f"{seconds // 3600:02d}:{seconds // 60 % 60:02d}:{seconds % 60:02d}"
        return text + (f".{ms:03d}" if ms else "")


class Function:
    """A function value: a user function (``node``) or a built-in (``call``)."""

    __slots__ = ("name", "node", "call")

    def __init__(self, name, node=None, call=None):
        self.name, self.node, self.call = name, node, call


# ---- casting -------------------------------------------------------------------------------------------------------


def to_number(value):
    """Implicit Number casting, as the Type casting page tabulates it."""
    if isinstance(value, bool):
        return 1.0 if value else 0.0
    if is_number(value):
        return float(value)
    if value is None:
        return 0.0
    if isinstance(value, str):
        if value.strip() == "":
            return 0.0
        return float(value) if _DECIMAL.match(value) else math.nan
    return math.nan


def to_bool(value):
    """Implicit Boolean casting: a Number other than 0 and NaN, and lower-case 'true', are true."""
    if isinstance(value, bool):
        return value
    if is_number(value):
        return not (value == 0 or math.isnan(value))
    if isinstance(value, str):
        return value == "true"
    return False


def to_text(value):
    """Implicit Text casting: null is empty text; arrays, dictionaries, features and geometries are JSON."""
    if value is None:
        return ""
    if isinstance(value, str):
        return value
    if isinstance(value, bool):
        return "true" if value else "false"
    if is_number(value):
        return number_text(value)
    if isinstance(value, (Date, DateOnly, Time)):
        return value.iso()
    if isinstance(value, (list, Dictionary, Feature, Geometry)):
        return json.dumps(to_json(value), separators=(",", ":"), ensure_ascii=False)
    if isinstance(value, Function):
        return f"function {value.name}"
    from ._data import FeatureSet
    if isinstance(value, FeatureSet):
        return "FeatureSet"
    return str(value)


def geometry_json(geometry):
    """A geometry as ArcGIS REST JSON, with its spatial reference."""
    from ..conversion import _to_esri
    if geometry.kind == "Extent":
        x0, y0, x1, y1 = geometry.shape.bounds
        out = {"xmin": x0, "ymin": y0, "xmax": x1, "ymax": y1}
    else:
        out = dict(_to_esri(geometry.shape) or {})
        if geometry.kind == "Point" and geometry.shape.has_z:
            out["z"] = geometry.shape.z
    if geometry.wkid:
        out["spatialReference"] = {"wkid": geometry.wkid}
    return _whole(out)


def _whole(value):
    """Integral coordinates written without a decimal point, as Arcade's numbers are."""
    if isinstance(value, dict):
        return {k: _whole(v) for k, v in value.items()}
    if isinstance(value, (list, tuple)):
        return [_whole(v) for v in value]
    if isinstance(value, float) and value.is_integer() and abs(value) < 2 ** 53:
        return int(value)
    return value


def to_json(value):
    """The JSON-able form of a value: what Text, Console and the JSON text cast write."""
    if value is None or isinstance(value, (str, bool)):
        return value
    if is_number(value):
        number = float(value)
        if math.isnan(number) or math.isinf(number):
            return None
        return int(number) if number == int(number) and abs(number) < 2 ** 53 else number
    if isinstance(value, list):
        return [to_json(v) for v in value]
    if isinstance(value, Dictionary):
        return {k: to_json(v) for k, v in value.items()}
    if isinstance(value, Feature):
        return {"geometry": geometry_json(value.geometry) if value.geometry is not None else None,
                "attributes": {k: to_json(v) for k, v in value.attributes.items()}}
    if isinstance(value, Geometry):
        return geometry_json(value)
    if isinstance(value, (Date, DateOnly, Time)):
        return value.iso()
    return to_text(value)


def from_json(value):
    """A parsed JSON value as Arcade's: objects become Dictionaries, numbers Numbers."""
    if isinstance(value, dict):
        return Dictionary({k: from_json(v) for k, v in value.items()})
    if isinstance(value, list):
        return [from_json(v) for v in value]
    if is_number(value):
        return float(value)
    return value


def type_of(value):
    """TypeOf's names."""
    if value is None:
        return "Null"
    if isinstance(value, bool):
        return "Boolean"
    if is_number(value):
        return "Number"
    if isinstance(value, str):
        return "String"
    if isinstance(value, list):
        return "Array"
    if isinstance(value, Dictionary):
        return "Dictionary"
    if isinstance(value, Feature):
        return "Feature"
    if isinstance(value, Geometry):
        return value.kind
    if isinstance(value, Date):
        return "Date"
    if isinstance(value, DateOnly):
        return "DateOnly"
    if isinstance(value, Time):
        return "Time"
    if isinstance(value, Function):
        return "Function"
    from ._data import FeatureSet, FeatureSetCollection
    if isinstance(value, FeatureSet):
        return "FeatureSet"
    if isinstance(value, FeatureSetCollection):
        return "FeatureSetCollection"
    return "Unrecognized Type"


_OBJECTS = (list, Dictionary, Feature, Geometry, Function)


def equal(a, b):
    """``==``: no coercion. Text, dates, numbers and booleans compare by value; objects by identity."""
    if a is None or b is None:
        return a is None and b is None
    if isinstance(a, bool) or isinstance(b, bool):
        return isinstance(a, bool) and isinstance(b, bool) and a == b
    if is_number(a) and is_number(b):
        return float(a) == float(b)
    if isinstance(a, str) and isinstance(b, str):
        return a == b
    if isinstance(a, Date) and isinstance(b, Date):
        return a.value == b.value
    if isinstance(a, DateOnly) and isinstance(b, DateOnly):
        return a.value == b.value
    if isinstance(a, Time) and isinstance(b, Time):
        return a.ms == b.ms
    return a is b


def _scalar(value):
    if isinstance(value, Date):
        return value.epoch
    if isinstance(value, DateOnly):
        return datetime.datetime.combine(value.value, datetime.time(), UTC).timestamp() * 1000.0
    if isinstance(value, Time):
        return float(value.ms)
    return value


def compare(op, a, b):
    """``<``, ``>``, ``<=``, ``>=``: objects answer false; two values of different types are both cast to numbers."""
    if isinstance(a, _OBJECTS) or isinstance(b, _OBJECTS):
        return False
    if (isinstance(a, Time) and isinstance(b, (Date, DateOnly))) or (isinstance(b, Time) and isinstance(a, (Date, DateOnly))):
        raise ArcadeError("A Date or DateOnly cannot be compared with a Time.")
    same = (type(_kind(a)) is type(_kind(b))) and _kind(a) == _kind(b)
    if same and isinstance(a, str):
        x, y = a, b
    elif same and isinstance(a, (Date, DateOnly, Time)):
        x, y = _scalar(a), _scalar(b)
    else:
        x, y = to_number(a), to_number(b)
    if op == "<":
        return x < y
    if op == ">":
        return x > y
    if op == "<=":
        return x <= y
    return x >= y


def _kind(value):
    if isinstance(value, bool):
        return "bool"
    if is_number(value):
        return "number"
    if isinstance(value, str):
        return "text"
    if value is None:
        return "null"
    return type(value).__name__


def require_bool(value, where):
    """A condition or a logical operand: Boolean, with no casting, as the Type casting page says."""
    if not isinstance(value, bool):
        raise ArcadeError(f"{where} must be a Boolean; {type_of(value)} was given.")
    return value
