"""ConvertDirection (ADR-235 §5.3): an angle between direction types (NORTH, SOUTH, POLAR, QUADRANT) and angle types
(DEGREES, DMS, RADIANS, GONS, GRADIANS), as a value or as text with the format tokens of the Geometry functions page.

A direction is held as a north azimuth in degrees: NORTH is clockwise from north, SOUTH clockwise from south, POLAR
counter-clockwise from east, and QUADRANT a meridian (N or S), an angle from it and a direction (E or W).

INFERRED (ADR-235 §5.8): text input is read as its numbers (and, for QUADRANT, its N/S and E/W letters); text output
without a format writes a DEGREES, RADIANS or gradian value as its number, DMS as ``d° m' s"`` and QUADRANT as its
parts joined by spaces; GONS and GRADIANS are the same unit (400 to the circle).
"""
from __future__ import annotations

import math
import re

from ._errors import ArcadeError
from ._fn_core import arity
from ._library import builtin
from ._values import Dictionary, is_number, number_text, to_number, to_text

_ANGLES = ("DEGREES", "DMS", "RADIANS", "GONS", "GRADIANS")
_DIRECTIONS = ("NORTH", "SOUTH", "POLAR", "QUADRANT")
_NUMBER = re.compile(r"[-+]?\d+(?:\.\d+)?")


def _spec(name, value, output=False):
    if not isinstance(value, Dictionary):
        raise ArcadeError(f"ConvertDirection's {name} is a dictionary.")
    angle = to_text(value.get("angleType") or "").upper()
    direction = to_text(value.get("directionType") or "").upper()
    if angle not in _ANGLES:
        raise ArcadeError(f"ConvertDirection: angleType is one of {', '.join(_ANGLES)}.")
    if direction not in _DIRECTIONS:
        raise ArcadeError(f"ConvertDirection: directionType is one of {', '.join(_DIRECTIONS)}.")
    kind = to_text(value.get("outputType") or "value").lower() if output else None
    if output and kind not in ("value", "text"):
        raise ArcadeError("ConvertDirection: outputType is value or text.")
    return angle, direction, kind


def _degrees(value, angle):
    """An angle in degrees from a value of an angle type (DMS: [d, m, s])."""
    if angle == "DMS":
        parts = list(value) + [0, 0]
        d, m, s = (to_number(p) for p in parts[:3])
        sign = -1 if d < 0 else 1
        return sign * (abs(d) + m / 60 + s / 3600)
    number = to_number(value)
    if angle == "RADIANS":
        return number * 180 / math.pi
    if angle in ("GONS", "GRADIANS"):
        return number * 360 / 400
    return number


def _parse(value, angle, direction):
    """(north azimuth in degrees, the angle as given when nothing needs converting)."""
    if direction == "QUADRANT":
        if isinstance(value, str):
            found = re.match(r"^\s*([NnSs])\s*(.*?)\s*([EeWw])\s*$", value)
            if not found:
                raise ArcadeError("ConvertDirection: a QUADRANT text is a meridian, an angle and a direction, as N 30 E.")
            parts = [found.group(1), *[float(n) for n in _NUMBER.findall(found.group(2))], found.group(3)]
        elif isinstance(value, list) and len(value) >= 3:
            parts = value
        else:
            raise ArcadeError("ConvertDirection: a QUADRANT value is [meridian, angle…, direction].")
        meridian, east = to_text(parts[0]).upper()[:1], to_text(parts[-1]).upper()[:1]
        inner = parts[1:-1]
        a = _degrees(inner if angle == "DMS" else inner[0], angle)
        if meridian == "N":
            return (a if east == "E" else 360 - a) % 360
        return (180 - a if east == "E" else 180 + a) % 360
    if isinstance(value, str):
        numbers = [float(n) for n in _NUMBER.findall(value)]
        if not numbers:
            raise ArcadeError("ConvertDirection: the text holds no angle.")
        value = numbers if angle == "DMS" else numbers[0]
    elif angle == "DMS" and not isinstance(value, list):
        raise ArcadeError("ConvertDirection: a DMS value is [degrees, minutes, seconds].")
    a = _degrees(value, angle)
    if direction == "NORTH":
        return a % 360
    if direction == "SOUTH":
        return (a + 180) % 360
    return (90 - a) % 360  # POLAR


def _from_degrees(a, angle):
    if angle == "RADIANS":
        return a * math.pi / 180
    if angle in ("GONS", "GRADIANS"):
        return a * 400 / 360
    return a


def _dms(a):
    d = math.floor(a)
    rest = (a - d) * 60
    m = math.floor(rest)
    s = (rest - m) * 60
    if s > 59.9999999:
        s, m = 0.0, m + 1
    if m >= 60:
        m, d = m - 60, d + 1
    return [float(d), float(m), round(s, 9)]


def _out_angle(azimuth, direction):
    """(angle in degrees, meridian, east-west) of a north azimuth in an output direction type."""
    if direction == "NORTH":
        return azimuth, None, None
    if direction == "SOUTH":
        return (azimuth - 180) % 360, None, None
    if direction == "POLAR":
        return (90 - azimuth) % 360, None, None
    if azimuth <= 90:
        return azimuth, "N", "E"
    if azimuth <= 180:
        return 180 - azimuth, "S", "E"
    if azimuth < 270:
        return azimuth - 180, "S", "W"
    return 360 - azimuth, "N", "W"


_LONG = {"N": "North", "S": "South", "E": "East", "W": "West"}
_TOKEN = re.compile(r"\[[^\]]*\]|([DRGdmsPpBb])\1*(?:\.\1+)?")


def _formatted(pattern, degrees, meridian, east):
    """Text by the page's format tokens: D, R, G a decimal angle; d, m, s degrees, minutes and seconds; P, p, B, b the
    meridian and the direction, long and short; [ ] escapes. Padding is the token's count before a decimal point,
    precision its count after; the last of d, m and s given is rounded, and a carry moves up."""
    tokens = list(_TOKEN.finditer(pattern))
    kinds = {m.group(1) for m in tokens if m.group(1)}
    precision = {}
    for m in tokens:
        if m.group(1):
            text = m.group(0)
            precision[m.group(1)] = len(text.split(".")[1]) if "." in text else 0
    d_total = degrees
    parts = {}
    if kinds & {"d", "m", "s"}:
        last = "s" if "s" in kinds else "m" if "m" in kinds else "d"
        scale = {"d": 1, "m": 60, "s": 3600}[last]
        places = precision.get(last, 0)
        total = round(d_total * scale, places)
        d = math.floor(total / scale + 1e-12)
        remainder = total - d * scale
        if last == "d":
            parts = {"d": total}
        elif last == "m":
            parts = {"d": d, "m": round(remainder, places)}
        else:
            m = math.floor(remainder / 60 + 1e-12)
            parts = {"d": d, "m": m, "s": round(remainder - m * 60, places)}
        if "d" not in kinds and "m" in parts:
            parts["m"] += parts["d"] * 60
        if "m" not in kinds and "s" in parts:
            parts["s"] += parts.get("m", 0) * 60

    def number(value, token):
        text = token.split(".")
        width = len(text[0])
        places = len(text[1]) if len(text) > 1 else 0
        shown = f"{value:.{places}f}"
        whole, _, fraction = shown.partition(".")
        sign = "-" if whole.startswith("-") else ""
        whole = whole.lstrip("-").rjust(width, "0")
        return sign + whole + ("." + fraction if fraction else "")

    out = []
    at = 0
    for m in tokens:
        out.append(pattern[at:m.start()])
        at = m.end()
        text, kind = m.group(0), m.group(1)
        if not kind:
            out.append(text[1:-1])
        elif kind == "D":
            out.append(number(degrees, text))
        elif kind == "R":
            out.append(number(degrees * math.pi / 180, text))
        elif kind == "G":
            out.append(number(degrees * 400 / 360, text))
        elif kind in "dms":
            out.append(number(parts.get(kind, 0), text))
        elif kind in "Pp":
            out.append("" if meridian is None else (_LONG[meridian] if kind == "P" else meridian))
        else:
            out.append("" if east is None else (_LONG[east] if kind == "B" else east))
    out.append(pattern[at:])
    return "".join(out)


@builtin("ConvertDirection")
def _convert_direction(_, args):
    arity("ConvertDirection", args, 3)
    value, input_spec, output_spec = args
    in_angle, in_direction, _ = _spec("inputSpec", input_spec)
    out_angle, out_direction, kind = _spec("outputSpec", output_spec, output=True)
    same = in_angle == out_angle and in_direction == out_direction and in_direction != "QUADRANT"
    azimuth = _parse(value, in_angle, in_direction)
    degrees, meridian, east = _out_angle(azimuth, out_direction)
    if kind == "text":
        pattern = output_spec.get("format")
        if pattern not in (None, ""):
            if same and in_angle != "DMS" and is_number(value):
                degrees = _degrees(value, in_angle)
            return _formatted(to_text(pattern), degrees, meridian, east)
        if out_angle == "DMS":
            d, m, s = _dms(degrees)
            angle_text = f"{number_text(d)}° {number_text(m)}' {number_text(s)}\""
        else:
            angle_text = number_text(value if same and is_number(value) else _from_degrees(degrees, out_angle))
        return " ".join(p for p in (meridian, angle_text, east) if p)
    if same and is_number(value):
        return float(value)
    angle = _dms(degrees) if out_angle == "DMS" else _from_degrees(degrees, out_angle)
    if out_direction == "QUADRANT":
        return [meridian, *(angle if isinstance(angle, list) else [angle]), east]
    return angle
