"""Arcade's math, text, logical, array, dictionary and debugging functions (ADR-235 §5.3), each from its page of the
function reference. Function parameters cast as the *Type casting* page says.
"""
from __future__ import annotations

import decimal
import json
import math
import random
import re
import urllib.parse
import uuid

from ._errors import ArcadeError
from ._library import builtin
from ._values import (Date, DateOnly, Dictionary, Feature, Function, Geometry, Time, equal, from_json, is_number,
                      to_bool, to_json, to_number, to_text, type_of)


def arity(name, args, low, high=None):
    high = low if high is None else high
    if not low <= len(args) <= (high if high >= 0 else 10 ** 9):
        if low == high:
            wanted = f"{low}"
        elif high < 0:
            wanted = f"at least {low}"
        else:
            wanted = f"{low} to {high}"
        raise ArcadeError(f"{name} takes {wanted} argument{'s' if wanted != '1' else ''}; {len(args)} "
                          f"{'were' if len(args) != 1 else 'was'} given.")


def call(interpreter, function, *args):
    if not isinstance(function, Function):
        raise ArcadeError(f"A function was expected; a {type_of(function)} was given.")
    return interpreter.invoke(function, list(args))


def first_is(*kinds):
    return lambda args: bool(args) and isinstance(args[0], kinds)


def _numbers(name, args):
    """The values of an array or of the arguments, cast to numbers."""
    values = args[0] if len(args) == 1 and isinstance(args[0], list) else args
    return [to_number(v) for v in values]


# ---- math ------------------------------------------------------------------------------------------------------------


def _unary(name, function, domain_nan=False):
    @builtin(name)
    def run(_, args):
        arity(name, args, 1)
        value = to_number(args[0])
        try:
            return float(function(value))
        except (ValueError, OverflowError):
            return math.nan
    return run


_unary("Abs", abs)
_unary("Acos", math.acos)
_unary("Asin", math.asin)
_unary("Atan", math.atan)
_unary("Cos", math.cos)
_unary("Sin", math.sin)
_unary("Tan", math.tan)
_unary("Exp", lambda x: math.exp(x) if x < 710 else math.inf)
_unary("Sqrt", lambda x: math.sqrt(x) if x >= 0 else math.nan)


@builtin("Atan2")
def _atan2(_, args):
    arity("Atan2", args, 2)
    return math.atan2(to_number(args[0]), to_number(args[1]))


@builtin("Log")
def _log(_, args):
    arity("Log", args, 1, 2)
    value = to_number(args[0])
    if len(args) == 1:
        if value == 0:
            return -math.inf
        return math.log(value) if value > 0 else math.nan
    base = to_number(args[1])
    if value <= 0 or base <= 0 or base == 1 or math.isnan(value) or math.isnan(base):
        return math.nan
    return math.log(value) / math.log(base)


@builtin("Pow")
def _pow(_, args):
    arity("Pow", args, 2)
    try:
        return float(math.pow(to_number(args[0]), to_number(args[1])))
    except (ValueError, OverflowError):
        return math.nan


@builtin("Constrain")
def _constrain(_, args):
    arity("Constrain", args, 3)
    value, low, high = (to_number(a) for a in args)
    return min(max(value, low), high)


def _rounded(value, places, mode):
    """``value`` to ``places`` decimals, worked in decimal arithmetic on its shortest text so that 2316.2562 rounds to
    2316.26 and not by binary noise."""
    if math.isnan(value) or math.isinf(value):
        return value
    quantum = decimal.Decimal(1).scaleb(-int(places))
    exact = decimal.Decimal(repr(value))
    if mode == "round":
        # INFERRED (ADR-235 §5.8): a half rounds up, toward +infinity, as Round(2.5) = 3 and Round(-2.5) = -2.
        mode = decimal.ROUND_HALF_UP if exact >= 0 else decimal.ROUND_HALF_DOWN
    return float(exact.quantize(quantum, rounding=mode))


@builtin("Round")
def _round(_, args):
    arity("Round", args, 1, 2)
    return _rounded(to_number(args[0]), to_number(args[1]) if len(args) > 1 else 0, "round")


@builtin("Ceil")
def _ceil(_, args):
    arity("Ceil", args, 1, 2)
    return _rounded(to_number(args[0]), to_number(args[1]) if len(args) > 1 else 0, decimal.ROUND_CEILING)


@builtin("Floor")
def _floor(_, args):
    arity("Floor", args, 1, 2)
    return _rounded(to_number(args[0]), to_number(args[1]) if len(args) > 1 else 0, decimal.ROUND_FLOOR)


@builtin("Sign")
def _sign(_, args):
    arity("Sign", args, 1)
    value = to_number(args[0]) if not isinstance(args[0], str) or args[0].strip() else math.nan
    if math.isnan(value):
        return math.nan
    if value == 0:
        return value  # -0 stays -0, as documented
    return 1.0 if value > 0 else -1.0


@builtin("Random")
def _random(_, args):
    arity("Random", args, 0)
    return random.random()


def _aggregate(name, reduce):
    @builtin(name)
    def run(_, args):
        values = _numbers(name, args)
        return reduce(values)
    return run


def _mean(values):
    return sum(values) / len(values) if values else math.nan


def _variance(values):
    if not values:
        return math.nan
    mean = _mean(values)
    return sum((v - mean) ** 2 for v in values) / len(values)  # the population variance, as documented


_aggregate("Sum", lambda values: float(math.fsum(values)))
_aggregate("Average", _mean)
_aggregate("Mean", _mean)
_aggregate("Max", lambda values: max(values) if values else math.nan)
_aggregate("Min", lambda values: min(values) if values else math.nan)
_aggregate("Variance", _variance)
_aggregate("Stdev", lambda values: math.sqrt(_variance(values)) if values else math.nan)


@builtin("Product")
def _product(_, args):
    values = args[0] if len(args) == 1 and isinstance(args[0], list) else args
    out = 1.0
    for value in values:
        if not is_number(value):
            return math.nan  # Product([[1,2],[3,1]]) and Product([{a: 10}]) are NaN, as documented
        out *= value
    return out


@builtin("Number")
def _number(_, args):
    arity("Number", args, 1, 2)
    value = args[0]
    if len(args) == 2 and args[1] not in (None, ""):
        from ._patterns import number
        return number(to_text(value), to_text(args[1]))
    if isinstance(value, Date):
        return value.epoch
    if isinstance(value, DateOnly):
        # INFERRED (ADR-235 §5.8): milliseconds since the epoch to the date's midnight, UTC, as the Types page says a
        # DateOnly is stored; a Time, milliseconds since midnight.
        import datetime as _dt
        return _dt.datetime.combine(value.value, _dt.time(), _dt.timezone.utc).timestamp() * 1000.0
    if isinstance(value, Time):
        return float(value.ms)
    return to_number(value)


# ---- logical ---------------------------------------------------------------------------------------------------------


@builtin("Boolean")
def _boolean(_, args):
    arity("Boolean", args, 1)
    return to_bool(args[0])


@builtin("IIf")
def _iif(_, args):
    arity("IIf", args, 3)
    return args[1] if to_bool(args[0]) else args[2]


@builtin("When")
def _when(_, args):
    if len(args) < 3 or len(args) % 2 == 0:
        raise ArcadeError("When takes pairs of a condition and a result, then a default value.")
    for at in range(0, len(args) - 1, 2):
        if to_bool(args[at]):
            return args[at + 1]
    return args[-1]


@builtin("Decode")
def _decode(_, args):
    if len(args) < 4 or len(args) % 2 != 0:
        raise ArcadeError("Decode takes an expression, pairs of a value to compare and a result, then a default.")
    for at in range(1, len(args) - 1, 2):
        if equal(args[0], args[at]):
            return args[at + 1]
    return args[-1]


def _empty(value):
    return value is None or value == ""


def _container_empty(value):
    if isinstance(value, (list, Dictionary)):
        return len(value) == 0
    if isinstance(value, Feature):
        return value.geometry is None and len(value.attributes) == 0
    from ._data import FeatureSet
    if isinstance(value, FeatureSet):
        return value.count() == 0
    return False


@builtin("IsEmpty")
def _is_empty(_, args):
    arity("IsEmpty", args, 1, 2)
    if _empty(args[0]):
        return True
    return bool(len(args) == 2 and to_bool(args[1]) and _container_empty(args[0]))


@builtin("IsNan")
def _is_nan(_, args):
    arity("IsNan", args, 1)
    return math.isnan(to_number(args[0]))


@builtin("Equals")
def _equals(_, args):
    arity("Equals", args, 2)
    if isinstance(args[0], Geometry) and isinstance(args[1], Geometry):
        return bool(args[0].shape.equals(args[1].shape))
    return equal(args[0], args[1])


@builtin("TypeOf")
def _type_of(_, args):
    arity("TypeOf", args, 1)
    return type_of(args[0])


def _step(container, key):
    """(found, value) one level into a container."""
    if isinstance(container, list):
        if not is_number(key) or key != int(key):
            return False, None
        at = int(key)
        if at < 0:
            at += len(container)
        return (True, container[at]) if 0 <= at < len(container) else (False, None)
    if isinstance(container, Dictionary):
        return (True, container.get(key)) if container.has(to_text(key)) else (False, None)
    if isinstance(container, Feature):
        return (True, container.attributes.get(key)) if container.attributes.has(to_text(key)) else (False, None)
    if isinstance(container, Geometry):
        from ._geometry import properties
        for name, value in properties(container).items():
            if name.lower() == to_text(key).lower():
                return True, value
    return False, None


def _path(container, keys):
    """The value a key or a list of keys reaches, or (False, None)."""
    path = keys if isinstance(keys, list) else [keys]
    value = container
    for key in path:
        found, value = _step(value, key)
        if not found:
            return False, None
    return True, value


@builtin("DefaultValue")
def _default_value(_, args):
    arity("DefaultValue", args, 2, 3)
    if len(args) == 2:
        return args[1] if _empty(args[0]) else args[0]
    container, keys, default = args
    if not isinstance(container, (list, Dictionary, Feature, Geometry)):
        raise ArcadeError(f"DefaultValue with a key takes an array, dictionary, feature or geometry; a "
                          f"{type_of(container)} was given.")
    found, value = _path(container, keys)
    return value if found and not _empty(value) else default


@builtin("HasValue")
def _has_value(_, args):
    arity("HasValue", args, 2)
    found, value = _path(args[0], args[1])
    return bool(found and value is not None)


@builtin("HasKey")
def _has_key(_, args):
    arity("HasKey", args, 2)
    container, key = args
    if isinstance(container, Dictionary):
        return container.has(to_text(key))
    if isinstance(container, Feature):
        return container.attributes.has(to_text(key))
    if isinstance(container, Geometry):
        return _step(container, key)[0]
    raise ArcadeError(f"HasKey takes a dictionary, feature or geometry; a {type_of(container)} was given.")


# ---- text ------------------------------------------------------------------------------------------------------------


@builtin("Count")
def _count(_, args):
    arity("Count", args, 1)
    value = args[0]
    if isinstance(value, (list, Dictionary)):
        return float(len(value))
    if isinstance(value, Feature):
        return float(len(value.attributes))
    from ._data import FeatureSet
    if isinstance(value, FeatureSet):
        return float(value.count())
    return float(len(to_text(value)))


@builtin("Concatenate")
def _concatenate(interpreter, args):
    if not args:
        return ""
    if isinstance(args[0], list):
        arity("Concatenate", args, 1, 3)
        separator = to_text(args[1]) if len(args) > 1 else ""
        pattern = args[2] if len(args) > 2 else None
        from ._format import text
        return separator.join(text(v, pattern) if pattern not in (None, "") else to_text(v) for v in args[0])
    # INFERRED (ADR-235 §5.8): a first argument that is not an array joins every argument with no separator, as the
    # page's Concatenate("#", r, g, b) does.
    return "".join(to_text(v) for v in args)


@builtin("Find")
def _find(_, args):
    arity("Find", args, 2, 3)
    start = int(to_number(args[2])) if len(args) > 2 else 0
    return float(to_text(args[1]).find(to_text(args[0]), max(start, 0)))


@builtin("Left")
def _left(_, args):
    arity("Left", args, 2)
    count = int(to_number(args[1]))
    return to_text(args[0])[:max(count, 0)]


@builtin("Right")
def _right(_, args):
    arity("Right", args, 2)
    count = int(to_number(args[1]))
    text = to_text(args[0])
    return text[len(text) - count:] if 0 < count else ""


@builtin("Mid")
def _mid(_, args):
    arity("Mid", args, 2, 3)
    text = to_text(args[0])
    start = max(int(to_number(args[1])), 0)
    if len(args) == 2:
        return text[start:]
    return text[start:start + max(int(to_number(args[2])), 0)]


@builtin("Lower")
def _lower(_, args):
    arity("Lower", args, 1)
    return to_text(args[0]).lower()


@builtin("Upper")
def _upper(_, args):
    arity("Upper", args, 1)
    return to_text(args[0]).upper()


@builtin("Trim")
def _trim(_, args):
    arity("Trim", args, 1)
    return to_text(args[0]).strip(" ")


@builtin("Proper")
def _proper(_, args):
    """INFERRED (ADR-235 §5.8): the letters after the first of a word are made lower case, and a word begins after
    whitespace."""
    arity("Proper", args, 1, 2)
    text = to_text(args[0])
    which = to_text(args[1]).lower() if len(args) > 1 else "everyword"
    if which not in ("everyword", "firstword"):
        raise ArcadeError("Proper's second argument is 'everyword' or 'firstword'.")
    if which == "firstword":
        return text[:1].upper() + text[1:].lower()
    return re.sub(r"(^|\s)(\S)(\S*)", lambda m: m.group(1) + m.group(2).upper() + m.group(3).lower(), text)


@builtin("Replace")
def _replace(_, args):
    arity("Replace", args, 3, 4)
    every = to_bool(args[3]) if len(args) > 3 else True
    text, find, put = to_text(args[0]), to_text(args[1]), to_text(args[2])
    if find == "":
        return text
    return text.replace(find, put) if every else text.replace(find, put, 1)


@builtin("Split")
def _split(_, args):
    arity("Split", args, 2, 4)
    text, separator = to_text(args[0]), to_text(args[1])
    limit = int(to_number(args[2])) if len(args) > 2 and args[2] is not None else -1
    remove = to_bool(args[3]) if len(args) > 3 else False
    parts = list(text) if separator == "" else text.split(separator)
    if remove:
        parts = [p for p in parts if p != ""]
    return parts[:limit] if limit >= 0 else parts


@builtin("StandardizeFilename")
def _standardize_filename(_, args):
    arity("StandardizeFilename", args, 1)
    text = to_text(args[0])
    for bad, good in (("/", "-"), ("\\", "-"), ("|", "-"), ("*", "_"), ("<", "_"), (">", "_"), ("?", "_"),
                      (":", ", ")):
        text = text.replace(bad, good)
    return text


_GUID_FORMATS = {"digits": "{0}", "digits-hyphen": "{1}", "digits-hyphen-braces": "{{{1}}}",
                 "digits-hyphen-parentheses": "({1})"}


def _guid_text(value, form):
    form = form.lower()
    if form not in _GUID_FORMATS:
        raise ArcadeError(f"'{form}' is not a GUID format: digits, digits-hyphen, digits-hyphen-braces or "
                          "digits-hyphen-parentheses.")
    return _GUID_FORMATS[form].format(value.hex, str(value))


@builtin("Guid")
def _guid(_, args):
    arity("Guid", args, 0, 1)
    return _guid_text(uuid.uuid4(), to_text(args[0]) if args else "digits-hyphen-braces")


@builtin("StandardizeGuid")
def _standardize_guid(_, args):
    arity("StandardizeGuid", args, 2)
    try:
        value = uuid.UUID(to_text(args[0]).strip().strip("{}()"))
    except ValueError:
        return ""
    return _guid_text(value, to_text(args[1]))


@builtin("Text")
def _text(_, args):
    arity("Text", args, 1, 2)
    from ._format import text
    return text(args[0], args[1] if len(args) > 1 else None)


def _units(text):
    return list(text.encode("utf-16-le")[i:i + 2] for i in range(0, len(text.encode("utf-16-le")), 2))


@builtin("ToCharCode")
def _to_char_code(_, args):
    arity("ToCharCode", args, 1, 2)
    units = _units(to_text(args[0]))
    at = int(to_number(args[1])) if len(args) > 1 else 0
    if not 0 <= at < len(units):
        return math.nan
    return float(int.from_bytes(units[at], "little"))


@builtin("ToCodePoint")
def _to_code_point(_, args):
    arity("ToCodePoint", args, 1, 2)
    units = [int.from_bytes(u, "little") for u in _units(to_text(args[0]))]
    at = int(to_number(args[1])) if len(args) > 1 else 0
    if not 0 <= at < len(units):
        return math.nan
    unit = units[at]
    if 0xD800 <= unit <= 0xDBFF and at + 1 < len(units) and 0xDC00 <= units[at + 1] <= 0xDFFF:
        return float(0x10000 + ((unit - 0xD800) << 10) + (units[at + 1] - 0xDC00))
    return float(unit)


@builtin("FromCharCode")
def _from_char_code(_, args):
    data = b"".join(int(to_number(a)).to_bytes(2, "little") for a in args)
    return data.decode("utf-16-le", errors="surrogatepass")


@builtin("FromCodePoint")
def _from_code_point(_, args):
    try:
        return "".join(chr(int(to_number(a))) for a in args)
    except (ValueError, OverflowError):
        raise ArcadeError("FromCodePoint takes code points from 0 to 0x10FFFF.") from None


@builtin("ToHex")
def _to_hex(_, args):
    arity("ToHex", args, 1)
    value = to_number(args[0])
    if math.isnan(value) or math.isinf(value):
        return "NaN"
    number = int(value)
    return ("-" if number < 0 else "") + format(abs(number), "X")


def _url_text(value):
    return urllib.parse.quote(to_text(value), safe="-_.!~*'()")


@builtin("UrlEncode")
def _url_encode(_, args):
    """A dictionary is written ``key=value`` pairs joined by ``&``. INFERRED (ADR-235 §5.8): in the order of the keys,
    sorted as GetKeys sorts them, which is the page's example's order."""
    arity("UrlEncode", args, 1)
    if isinstance(args[0], Dictionary):
        return "&".join(f"{_url_text(k)}={_url_text(v)}" for k, v in sorted(args[0].items(), key=lambda kv: kv[0].lower()))
    return _url_text(args[0])


# ---- arrays ----------------------------------------------------------------------------------------------------------


def _array(name, value):
    if not isinstance(value, list):
        raise ArcadeError(f"{name} takes an array; a {type_of(value)} was given.")
    return value


@builtin("Array")
def _array_make(_, args):
    arity("Array", args, 1, 2)
    if isinstance(args[0], list):
        return _copy(args[0], deep=len(args) > 1 and to_bool(args[1]))
    length = to_number(args[0])
    if math.isnan(length) or length < 0 or length != int(length):
        raise ArcadeError("Array's length is a whole number of zero or more.")
    return [args[1] if len(args) > 1 else None for _ in range(int(length))]


def _copy(value, deep):
    if isinstance(value, list):
        return [_copy(v, True) if deep else v for v in value]
    if isinstance(value, Dictionary):
        return Dictionary([(k, _copy(v, True) if deep else v) for k, v in value.items()])
    return value


@builtin("Back")
def _back(_, args):
    arity("Back", args, 1)
    values = _array("Back", args[0])
    if not values:
        raise ArcadeError("Back of an empty array.")
    return values[-1]


@builtin("Front")
def _front(_, args):
    arity("Front", args, 1)
    values = _array("Front", args[0])
    if not values:
        raise ArcadeError("Front of an empty array.")
    return values[0]


@builtin("First", accepts=first_is(list))
def _first(_, args):
    arity("First", args, 1)
    return args[0][0] if args[0] else None


def _test(name):
    @builtin(name, accepts=first_is(list))
    def run(interpreter, args):
        arity(name, args, 2)
        passed = (to_bool(call(interpreter, args[1], v)) for v in args[0])
        if name == "All":
            return all(passed)
        if name == "Any":
            return any(passed)
        return not any(passed)
    return run


_test("All")
_test("Any")
_test("None")


@builtin("Filter", accepts=first_is(list))
def _filter(interpreter, args):
    arity("Filter", args, 2)
    return [v for v in args[0] if to_bool(call(interpreter, args[1], v))]


@builtin("Map")
def _map(interpreter, args):
    arity("Map", args, 2)
    return [call(interpreter, args[1], v) for v in _array("Map", args[0])]


@builtin("Reduce")
def _reduce(interpreter, args):
    arity("Reduce", args, 2, 3)
    values = list(_array("Reduce", args[0]))
    if len(args) == 3:
        accumulated = args[2]
    elif values:
        accumulated = values.pop(0)
    else:
        return None
    for value in values:
        accumulated = call(interpreter, args[1], accumulated, value)
    return accumulated


def _distinct(values):
    out = []
    for value in values:
        if not any(equal(value, kept) for kept in out):
            out.append(value)
    return out


@builtin("Distinct")
def _distinct_values(_, args):
    if len(args) == 1 and isinstance(args[0], list):
        return _distinct(args[0])
    return _distinct(args)


@builtin("Erase", accepts=first_is(list))
def _erase(_, args):
    arity("Erase", args, 2)
    values = args[0]
    at = int(to_number(args[1]))
    if at < 0:
        at += len(values)
    if not 0 <= at < len(values):
        raise ArcadeError("Erase: the index is out of the array's bounds.")
    del values[at]
    return None


@builtin("Insert", accepts=first_is(list))
def _insert(_, args):
    arity("Insert", args, 3)
    values = args[0]
    at = int(to_number(args[1]))
    if at < 0:
        at += len(values)
    if not 0 <= at <= len(values):
        raise ArcadeError("Insert: the index is out of the array's bounds.")
    values.insert(at, args[2])
    return None


@builtin("Includes")
def _includes(_, args):
    arity("Includes", args, 2)
    return any(equal(v, args[1]) for v in _array("Includes", args[0]))


@builtin("IndexOf")
def _index_of(_, args):
    arity("IndexOf", args, 2)
    for at, value in enumerate(_array("IndexOf", args[0])):
        if equal(value, args[1]):
            return float(at)
    return -1.0


@builtin("Pop")
def _pop(_, args):
    arity("Pop", args, 1)
    values = _array("Pop", args[0])
    if not values:
        raise ArcadeError("Pop of an empty array.")
    return values.pop()


@builtin("Push")
def _push(_, args):
    arity("Push", args, 2)
    values = _array("Push", args[0])
    values.append(args[1])
    return float(len(values))


@builtin("Resize")
def _resize(_, args):
    arity("Resize", args, 2, 3)
    values = _array("Resize", args[0])
    size = int(to_number(args[1]))
    if size < 0:
        raise ArcadeError("Resize: the size is zero or more.")
    del values[size:]
    values.extend([args[2] if len(args) > 2 else None] * (size - len(values)))
    return None


@builtin("Reverse")
def _reverse(_, args):
    arity("Reverse", args, 1)
    values = _array("Reverse", args[0])
    values.reverse()
    return values


@builtin("Slice")
def _slice(_, args):
    arity("Slice", args, 1, 3)
    values = _array("Slice", args[0])
    start = int(to_number(args[1])) if len(args) > 1 else 0
    end = int(to_number(args[2])) if len(args) > 2 else len(values)
    return values[start:end]


def _sort_key(value):
    """Without a comparator: numbers by value, everything else by its text, nulls last. INFERRED (ADR-235 §5.8): a
    mixed array is ordered by text, which gives the page's ['$', 1, 'A', 'a']."""
    return to_text(value)


@builtin("Sort")
def _sort(interpreter, args):
    arity("Sort", args, 1, 2)
    values = _array("Sort", args[0])
    if len(args) == 2 and args[1] is not None:
        import functools

        def order(a, b):
            result = to_number(call(interpreter, args[1], a, b))
            return -1 if result < 0 else 1 if result > 0 else 0
        ordered = sorted(values, key=functools.cmp_to_key(order))
    elif any(isinstance(v, (Dictionary, Feature)) for v in values):
        return values  # dictionaries without a comparator are not sorted, as documented
    else:
        present = [v for v in values if v is not None]
        nulls = [v for v in values if v is None]
        if all(is_number(v) for v in present):
            ordered = sorted(present, key=float) + nulls
        elif all(isinstance(v, (Date, DateOnly, Time)) for v in present) and present:
            from ._values import _scalar
            ordered = sorted(present, key=_scalar) + nulls
        else:
            ordered = sorted(present, key=_sort_key) + nulls
    values[:] = ordered
    return values


@builtin("Splice")
def _splice(_, args):
    out = []
    for value in args:
        if isinstance(value, list):
            out.extend(value)
        else:
            out.append(value)
    return out


@builtin("Top", accepts=first_is(list))
def _top(_, args):
    arity("Top", args, 2)
    return args[0][:max(int(to_number(args[1])), 0)]


# ---- dictionaries ----------------------------------------------------------------------------------------------------


@builtin("Dictionary")
def _dictionary(_, args):
    if len(args) == 1 and isinstance(args[0], str):
        try:
            parsed = json.loads(args[0])
        except ValueError:
            raise ArcadeError("Dictionary: the text is not JSON.") from None
        if not isinstance(parsed, dict):
            raise ArcadeError("Dictionary: the JSON text is not an object.")
        return from_json(parsed)
    if args and isinstance(args[0], Dictionary) and len(args) <= 2:
        return _copy(args[0], deep=len(args) > 1 and to_bool(args[1]))
    if len(args) == 1 and isinstance(args[0], Geometry):
        return from_json(to_json(args[0]))
    if len(args) == 1 and isinstance(args[0], Feature):
        return from_json(to_json(args[0]))
    if len(args) % 2:
        raise ArcadeError("Dictionary takes pairs of a name and a value.")
    out = Dictionary()
    for at in range(0, len(args), 2):
        out.set(to_text(args[at]), args[at + 1])
    return out


@builtin("Erase", accepts=first_is(Dictionary))
def _erase_key(_, args):
    arity("Erase", args, 2)
    args[0].erase(to_text(args[1]))
    return None


@builtin("Insert", accepts=first_is(Dictionary))
def _insert_key(_, args):
    arity("Insert", args, 3)
    args[0].set(to_text(args[1]), args[2])
    return None


@builtin("FromJSON")
def _from_json(_, args):
    arity("FromJSON", args, 1)
    try:
        return from_json(json.loads(to_text(args[0])))
    except ValueError:
        raise ArcadeError("FromJSON: the text is not JSON.") from None


def _keys(name, value):
    if isinstance(value, Dictionary):
        return value.items()
    if isinstance(value, Feature):
        return value.attributes.items()
    raise ArcadeError(f"{name} takes a dictionary; a {type_of(value)} was given.")


@builtin("GetKeys")
def _get_keys(_, args):
    arity("GetKeys", args, 1)
    return sorted((k for k, _ in _keys("GetKeys", args[0])), key=str.lower)


@builtin("GetValues")
def _get_values(_, args):
    arity("GetValues", args, 1)
    return [v for _, v in sorted(_keys("GetValues", args[0]), key=lambda kv: kv[0].lower())]


# ---- debugging -------------------------------------------------------------------------------------------------------


@builtin("Console")
def _console(interpreter, args):
    interpreter.console.append(" ".join(to_text(a) for a in args))
    return None


@builtin("GetEnvironment")
def _get_environment(interpreter, args):
    """INFERRED (ADR-235 §5.8): ``engine`` is "grapy", ``version`` the Arcade version whose documentation grapy was built
    from is not claimed, and the time zone is UTC."""
    arity("GetEnvironment", args, 0)
    out = Dictionary({"engine": "grapy", "engineVersion": "1", "application": "grapy", "locale": "en",
                      "timeZone": "UTC", "userTimeZone": "UTC"})
    wkid = getattr(interpreter, "wkid", None)
    if wkid:
        out.set("spatialReference", Dictionary({"wkid": float(wkid)}))
    return out
