"""Motion imagery metadata in arcpy.ia — ADR-237.

Only ConvertVideoMetadata is built here: it works on a metadata table (.csv) alone. The other motion imagery tools
read or write a video stream inside a container (.ts, .mp4, .avi …), and a user tool on this server has no demuxer,
muxer or decoder for one (ADR-237 §5.6); ``arcpy.ia`` refuses them with that reason.

The target fields are the thirteen the tool's page lists, with the units it requires. Their tags are those of MISB
ST 0601 (*UAS Datalink Local Set*), the published standard the page's MISB names come from.
"""
from __future__ import annotations

import csv
import datetime as _dt
import math
import os
import shlex

from ._result import Result
from ._runtime import AddMessage, ExecuteError
from ._runtime import _is_given  # noqa: E402 — a Raster is never compared with ==

#: (MISB ST 0601 tag, name as the page writes it, the kind of unit it is held in)
FIELDS = [
    # The names as Pro 3.7 lists them (measured 2026-11-08: "Precision Time Stamp", "Platform Pitch Angle (Full)",
    # "Platform Roll Angle (Full)"); grapy wrote the page's table's names until then, which still match by their
    # letters (``_key``) where they are the same words.
    (2, "Precision Time Stamp", "time"),
    (13, "Sensor Latitude", "coordinate"),
    (14, "Sensor Longitude", "coordinate"),
    (104, "Sensor Ellipsoid Height Extended", "length"),
    (15, "Sensor True Altitude", "length"),
    (5, "Platform Heading Angle", "angle"),
    (6, "Platform Pitch Angle (Full)", "angle"),
    (7, "Platform Roll Angle (Full)", "angle"),
    (20, "Sensor Relative Roll Angle", "angle"),
    (19, "Sensor Relative Elevation Angle", "angle"),
    (18, "Sensor Relative Azimuth Angle", "angle"),
    (16, "Sensor Horizontal Field of View", "angle"),
    (17, "Sensor Vertical Field of View", "angle"),
]

#: INFERRED: the source unit keywords, each the factor to the required unit (metres, degrees, microseconds)
UNITS = {
    "length": {"METERS": 1.0, "FEET": 0.3048, "US_FEET": 1200 / 3937, "KILOMETERS": 1000.0, "MILES": 1609.344,
               "NAUTICAL_MILES": 1852.0, "YARDS": 0.9144},
    "angle": {"DEGREES": 1.0, "RADIANS": 180 / math.pi, "SEMICIRCLES": 180 / 2 ** 31},
    "time": {"MICROSECONDS": 1.0, "MILLISECONDS": 1e3, "SECONDS": 1e6, "ISO8601": None},
    # Sensor Latitude and Longitude take XY, DD, DDM or DMS, as Pro 3.7 checks them (measured 2026-11-08: "Degrees" is
    # ERROR 000800, "not a member of XY | DD | DDM | DMS"); grapy took them as angles in DEGREES until then.
    # INFERRED: XY and DD are decimal degrees; DDM is "degrees minutes" and DMS "degrees minutes seconds", separated by
    # spaces or the symbols ° ' ", an S or W making them negative.
    "coordinate": {"XY": 1.0, "DD": 1.0, "DDM": "DDM", "DMS": "DMS"},
}


#: The dates from which UTC was one more leap second behind TAI, 1972-07-01 to 2017-01-01: 27 in all.
_LEAPS = ["1972-07-01", "1973-01-01", "1974-01-01", "1975-01-01", "1976-01-01", "1977-01-01", "1978-01-01",
          "1979-01-01", "1980-01-01", "1981-07-01", "1982-07-01", "1983-07-01", "1985-07-01", "1988-01-01",
          "1990-01-01", "1991-01-01", "1992-07-01", "1993-07-01", "1994-07-01", "1996-01-01", "1997-07-01",
          "1999-01-01", "2006-01-01", "2009-01-01", "2012-07-01", "2015-07-01", "2017-01-01"]


def _leap_seconds(moment):
    """The leap seconds inserted since 1972 up to ``moment`` (UTC)."""
    day = moment.date().isoformat()
    return sum(1 for d in _LEAPS if d <= day)


def _misp(moment):
    """A UTC date and time as a MISB ST 0603 precision time stamp: microseconds since 1970 counting the leap seconds,
    as Pro 3.7 writes it (measured 2026-11-08: 2024-05-01T12:00:00 is 1714564827000000, 27 s after its POSIX time);
    grapy wrote the POSIX microseconds until then."""
    seconds = (moment - _dt.datetime(1970, 1, 1, tzinfo=_dt.timezone.utc)).total_seconds() + _leap_seconds(moment)
    return str(round(seconds * 1e6))


def _time_format(unit):
    """A date-time keyword as Pro 3.7 lists them for Precision Time Stamp (measured 2026-11-08: YYYY-MM-DDTHH:MM:SS.SSS,
    DD/MM/YYYY hh:MM:SS.SSS AM/PM Z, YYYYMMDD HHMMSS.SSS … — day, month and year in three orders, a space or T, 24-
    or 12-hour clock, an optional Z) as a strptime format, or None. INFERRED: Z means UTC and a time without it is UTC
    too."""
    text = str(unit).strip()
    if "YYYY" not in text:
        return None
    cut = min(i for i in (text.find("T"), text.find(" ")) if i >= 0) if ("T" in text or " " in text) else len(text)
    date, rest = text[:cut], text[cut:]
    date = date.replace("YYYY", "%Y").replace("MM", "%m").replace("DD", "%d")
    rest = rest.replace("AM/PM", "%p").replace("SS.SSS", "%S.%f").replace("HH", "%H").replace("hh", "%I")
    return date + rest.replace("MM", "%M")


def _sexagesimal(text, parts):
    import re
    negative = text.strip().startswith("-") or text.strip()[-1:].upper() in ("S", "W")
    numbers = [float(n) for n in re.findall(r"\d+(?:\.\d+)?", text)]
    if len(numbers) != parts:
        raise ValueError(text)
    value = numbers[0] + numbers[1] / 60 + (numbers[2] / 3600 if parts == 3 else 0.0)
    return -value if negative else value


def _key(name):
    return "".join(ch for ch in str(name).upper() if ch.isalnum())


_BY_KEY = {_key(name): (tag, name, kind) for tag, name, kind in FIELDS}


def _rows_of(value):
    """A value table as rows of (target, source, unit): a list of lists, or arcpy's text form, rows split by ';'
    and values by spaces, quoted where they hold one."""
    if not _is_given(value):
        return []
    if isinstance(value, (list, tuple)):
        rows = [list(r) if isinstance(r, (list, tuple)) else shlex.split(str(r)) for r in value]
    else:
        rows = [shlex.split(part) for part in str(value).split(";") if part.strip()]
    return [r + [""] * (3 - len(r)) for r in rows]


def _convert(text, kind, unit, tool, field):
    text = str(text).strip()
    if text == "":
        return ""
    if kind == "time" and _time_format(unit):
        return _misp(_dt.datetime.strptime(text, _time_format(unit)).replace(tzinfo=_dt.timezone.utc))
    factor = UNITS[kind].get(unit)
    if unit not in UNITS[kind]:
        raise ExecuteError(f"{tool}: '{unit}' is not a unit grapy reads for {field}; it reads "
                           f"{', '.join(UNITS[kind])}.")
    if kind == "time" and factor is None:
        moment = _dt.datetime.fromisoformat(text.replace("Z", "+00:00"))
        if moment.tzinfo is None:
            moment = moment.replace(tzinfo=_dt.timezone.utc)
        return _misp(moment.astimezone(_dt.timezone.utc))
    try:
        number = _sexagesimal(text, 2 if factor == "DDM" else 3) if isinstance(factor, str) else float(text) * factor
    except ValueError:
        raise ExecuteError(f"{tool}: '{text}' in {field} is not a number.")
    return str(int(round(number))) if kind == "time" else repr(number)


def ConvertVideoMetadata(in_metadata_file, out_metadata_file, out_mapping_file=None, input_field_matching=None):
    """Writes the metadata table with its fields matched to the motion imagery fields and converted to their units:
    a field the value table matches takes the target's name and is converted from its source unit; a field already
    named as a target (INFERRED: without regard to case, spaces or punctuation) is copied as it is, as the page says
    correct data is.

    Measured in Pro 3.7 on 2026-11-08: the output holds the motion imagery fields alone, named as Pro lists them;
    a date and time becomes a MISB precision time stamp, counting leap seconds; the mapping file is MISB Tag, LDS
    Name, Local metadata heading, Units, a row per field matched. Not copied: Pro wrote an altitude given in Feet
    unconverted (1000 for 1000 ft), where the page requires metres.

    INFERRED (the page does not give the files' layout): the source unit keywords are those of ``UNITS`` (length to
    metres, angle to degrees, time to microseconds, ISO8601 a date and time read as UTC) and Pro's date-time keywords,
    an empty unit meaning the required one."""
    tool = "arcpy.ia.ConvertVideoMetadata"
    source = os.fspath(in_metadata_file)
    if not source.lower().endswith(".csv"):
        raise ExecuteError(f"{tool}: the input metadata file must be a .csv, as documented.")
    if not os.path.exists(source):
        raise ExecuteError(f"{tool}: '{source}' does not exist.")
    target = os.fspath(out_metadata_file)
    if not target.lower().endswith(".csv"):
        raise ExecuteError(f"{tool}: the output metadata file must be a .csv, as documented.")
    with open(source, newline="", encoding="utf-8-sig") as f:
        reader = csv.reader(f)
        try:
            header = next(reader)
        except StopIteration:
            raise ExecuteError(f"{tool}: '{source}' is empty.")
        rows = list(reader)
    position = {name: i for i, name in enumerate(header)}
    plan = {}  # target name -> (source column, kind, unit or None)
    given_units = {}
    for t, s, u in _rows_of(input_field_matching):
        found = _BY_KEY.get(_key(t))
        if found is None:
            raise ExecuteError(f"{tool}: '{t}' is not one of the motion imagery fields: "
                               f"{', '.join(n for _, n, _ in FIELDS)}.")
        if s not in position:
            raise ExecuteError(f"{tool}: the input has no field '{s}'.")
        unit = str(u).strip()
        unit = (unit if found[2] == "time" and _time_format(unit) else unit.upper().replace(" ", "_")) or None
        plan[found[1]] = (position[s], found[2], unit)
        given_units[found[1]] = str(u).strip()
    for name in header:
        found = _BY_KEY.get(_key(name))
        if found is not None and found[1] not in plan:
            plan[found[1]] = (position[name], found[2], None)
    used = {column for column, _, _ in plan.values()}
    targets = [name for _, name, _ in FIELDS if name in plan]
    # Only the motion imagery fields are written, as Pro 3.7 writes them (measured 2026-11-08); grapy copied the other
    # fields after them until then.
    rest = []
    out_header = targets
    written = []
    for row in rows:
        row = row + [""] * (len(header) - len(row))
        out = []
        for name in targets:
            column, kind, unit = plan[name]
            value = row[column]
            out.append(_convert(value, kind, unit, tool, name) if unit else value)
        out.extend(row[i] for i in rest)
        written.append(out)
    os.makedirs(os.path.dirname(os.path.abspath(target)), exist_ok=True)
    with open(target, "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(out_header)
        w.writerows(written)
    outputs = [target]
    if _is_given(out_mapping_file):
        mapping = os.fspath(out_mapping_file)
        with open(mapping, "w", newline="", encoding="utf-8") as f:
            w = csv.writer(f)
            # Pro 3.7's layout (measured 2026-11-08): MISB Tag (10000 plus the ST 0601 tag), LDS Name, the source
            # field and the unit given, a row for each field matched.
            w.writerow(["MISB Tag", "LDS Name", "Local metadata heading", "Units"])
            for tag, name, _ in FIELDS:
                if name in plan:
                    w.writerow([10000 + tag, name, header[plan[name][0]], given_units.get(name, "")])
        outputs.append(mapping)
    AddMessage(f"ConvertVideoMetadata: {len(targets)} of the {len(FIELDS)} motion imagery fields matched, "
               f"{len(written)} rows written.")
    return Result(*outputs)
