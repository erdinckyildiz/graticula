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

#: (MISB ST 0601 tag, name as the page writes it, the kind of unit it is held in)
FIELDS = [
    (2, "Precision Timestamp", "time"),
    (13, "Sensor Latitude", "angle"),
    (14, "Sensor Longitude", "angle"),
    (104, "Sensor Ellipsoid Height Extended", "length"),
    (15, "Sensor True Altitude", "length"),
    (5, "Platform Heading Angle", "angle"),
    (6, "Platform Pitch Angle", "angle"),
    (7, "Platform Roll Angle", "angle"),
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
}


def _key(name):
    return "".join(ch for ch in str(name).upper() if ch.isalnum())


_BY_KEY = {_key(name): (tag, name, kind) for tag, name, kind in FIELDS}


def _rows_of(value):
    """A value table as rows of (target, source, unit): a list of lists, or arcpy's text form, rows split by ';'
    and values by spaces, quoted where they hold one."""
    if value in (None, "", "#"):
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
    factor = UNITS[kind].get(unit)
    if unit not in UNITS[kind]:
        raise ExecuteError(f"{tool}: '{unit}' is not a unit grapy reads for {field}; it reads "
                           f"{', '.join(UNITS[kind])}.")
    if kind == "time" and factor is None:
        moment = _dt.datetime.fromisoformat(text.replace("Z", "+00:00"))
        if moment.tzinfo is None:
            moment = moment.replace(tzinfo=_dt.timezone.utc)
        return str(round((moment - _dt.datetime(1970, 1, 1, tzinfo=_dt.timezone.utc)).total_seconds() * 1e6))
    try:
        number = float(text) * factor
    except ValueError:
        raise ExecuteError(f"{tool}: '{text}' in {field} is not a number.")
    return str(int(round(number))) if kind == "time" else repr(number)


def ConvertVideoMetadata(in_metadata_file, out_metadata_file, out_mapping_file=None, input_field_matching=None):
    """Writes the metadata table with its fields matched to the motion imagery fields and converted to their units:
    a field the value table matches takes the target's name and is converted from its source unit; a field already
    named as a target (INFERRED: without regard to case, spaces or punctuation) is copied as it is, as the page says
    correct data is.

    INFERRED (the page does not give the files' layout): every other field is copied unchanged after them; the
    output's header names the targets as the page's table writes them; the source unit keywords are those of
    ``UNITS`` (length to metres, angle to degrees, time to microseconds since 1970, ISO8601 a date and time read as
    UTC), an empty unit meaning the required one; the mapping file has a header ``Tag,MISB Name,Field Name`` and one
    row per target, its third column the source field matched or found, empty when neither."""
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
    for t, s, u in _rows_of(input_field_matching):
        found = _BY_KEY.get(_key(t))
        if found is None:
            raise ExecuteError(f"{tool}: '{t}' is not one of the motion imagery fields: "
                               f"{', '.join(n for _, n, _ in FIELDS)}.")
        if s not in position:
            raise ExecuteError(f"{tool}: the input has no field '{s}'.")
        unit = str(u).strip().upper().replace(" ", "_") or None
        plan[found[1]] = (position[s], found[2], unit)
    for name in header:
        found = _BY_KEY.get(_key(name))
        if found is not None and found[1] not in plan:
            plan[found[1]] = (position[name], found[2], None)
    used = {column for column, _, _ in plan.values()}
    targets = [name for _, name, _ in FIELDS if name in plan]
    rest = [i for i in range(len(header)) if i not in used]
    out_header = targets + [header[i] for i in rest]
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
    if out_mapping_file not in (None, "", "#"):
        mapping = os.fspath(out_mapping_file)
        with open(mapping, "w", newline="", encoding="utf-8") as f:
            w = csv.writer(f)
            w.writerow(["Tag", "MISB Name", "Field Name"])
            for tag, name, _ in FIELDS:
                w.writerow([tag, name, header[plan[name][0]] if name in plan else ""])
        outputs.append(mapping)
    AddMessage(f"ConvertVideoMetadata: {len(targets)} of the {len(FIELDS)} motion imagery fields matched, "
               f"{len(written)} rows written.")
    return Result(*outputs)
