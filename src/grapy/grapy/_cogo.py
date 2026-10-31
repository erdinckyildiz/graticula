"""arcpy.edit.UpdateCOGO on ADR-240's COGO-enabled lines — D-329's sweep.

UpdateCOGO was refused as *"COGO-enabled lines, a data model this server does not hold"*; ADR-240 made EnableCOGO
mark a line class and add its five fields. The page (doc.esri.com …/editing/update-cogo, read 2026-10-31) says the
tool sets a COGO-enabled line's Distance, Radius and Arc Length from its shape's length, and its Direction from its
shape's direction, with four update modes and two ground-to-grid corrections. What it leaves open is INFERRED, Q-209 (i).
"""
from __future__ import annotations

import math

from ._result import Result
from ._runtime import ExecuteError
from ._runtime import _is_given  # noqa: E402 — a Raster is never compared with ==

_MODES = ("OVERWRITE", "UPDATE_NULL_ONLY", "USE_MINIMUM_DIFFERENCE", "DO_NOT_UPDATE")


def _mode(value, name):
    word = str(value or "OVERWRITE").upper()
    if word not in _MODES:
        raise ExecuteError(f"ERROR 000800: arcpy.edit.UpdateCOGO: {name} is one of {', '.join(_MODES)}, not '{value}'.")
    return word


def _factor(value, attributes, tool):
    """A number, or an Arcade expression evaluated on the feature (``$feature``), as the page allows."""
    if not _is_given(value):
        return None
    if isinstance(value, (int, float)):
        return float(value)
    try:
        return float(str(value))
    except ValueError:
        pass
    from ._arcade import evaluate
    from ._arcade._values import Dictionary, Feature
    record = Dictionary()
    for key, v in attributes.items():
        record.set(key, v)
    answer = evaluate(str(value), {"$feature": Feature(record, None, immutable=True)})
    try:
        return float(answer)
    except (TypeError, ValueError):
        raise ExecuteError(f"ERROR: arcpy.edit.{tool}: the expression '{value}' answered '{answer}', not a number.") \
            from None


def _azimuth(shape):
    import shapely
    parts = list(shapely.get_parts(shape))
    first = shapely.get_coordinates(parts[0])[0]
    last = shapely.get_coordinates(parts[-1])[-1]
    dx, dy = last[0] - first[0], last[1] - first[1]
    if dx == 0 and dy == 0:
        return None
    return math.degrees(math.atan2(dx, dy)) % 360.0


def UpdateCOGO(in_line_features, distances_type="OVERWRITE", distance_tolerance=None, direction_type="OVERWRITE",
               minimum_direction_difference=0, minimum_direction_lateral_offset=None, combined_scale_factor=None,
               direction_offset=None):
    """The COGO fields of a COGO-enabled line class (EnableCOGO, ADR-240) set from each line's shape, as documented:
    Distance from its length and Direction from its direction, each by its mode — OVERWRITE (the default),
    UPDATE_NULL_ONLY, USE_MINIMUM_DIFFERENCE (only where the stored value differs by more than the minimum: a
    distance, or for a direction an angle in seconds or the lateral offset between the shape's end and the end the
    stored direction draws) or DO_NOT_UPDATE; ``combined_scale_factor`` and ``direction_offset`` (seconds) — a number
    or an Arcade expression on the feature — are the ground-to-grid corrections. The input is modified.

    INFERRED (Q-209 (i)): the direction is the north azimuth in decimal degrees, as EnableCOGO's page describes the
    field, from the line's first vertex to its last; the stored (ground) distance is the shape's (grid) length divided by
    the combined scale factor, and the stored direction the shape's less the offset — the corrections being
    ground-to-grid, their inverse takes the grid shape to ground; a GeoPackage line is straight segments with no true
    arc, so its Radius, Radius2 and ArcLength are set null where the distance mode would write them; with
    USE_MINIMUM_DIFFERENCE a direction is updated when either minimum is exceeded."""
    from . import da
    from . import _behaviours, _gpkg
    from .analysis import _distance, _schema
    tool = "UpdateCOGO"
    columns, kind, srid, _ = _schema(in_line_features)
    if kind != "Polyline":
        raise ExecuteError(f"ERROR 000365: arcpy.edit.{tool}: '{in_line_features}' is not a line feature class.")
    from .management import _source
    path, _ = _source(in_line_features)
    connection, table = _gpkg.table_of(path)
    try:
        enabled = _behaviours.cogo_enabled(connection, table)
    finally:
        connection.close()
    if not enabled:
        raise ExecuteError(f"ERROR: arcpy.edit.{tool}: '{in_line_features}' is not COGO enabled, as the tool needs "
                           "(EnableCOGO).")
    distance_mode, direction_mode = _mode(distances_type, "distances_type"), _mode(direction_type, "direction_type")
    tolerance = float(_distance(distance_tolerance, srid)) if _is_given(distance_tolerance) else 0.0
    seconds = float(minimum_direction_difference) if _is_given(minimum_direction_difference) else 0.0
    lateral = float(_distance(minimum_direction_lateral_offset, srid)) \
        if _is_given(minimum_direction_lateral_offset) else 0.0
    names = {c.name.lower(): c.name for c in columns}
    cogo = [names[f.lower()] for f in _behaviours.COGO_FIELDS]
    direction_f, distance_f, arc_f, radius_f, radius2_f = cogo
    others = [c.name for c in columns]
    count = 0
    with da.UpdateCursor(in_line_features, [*others, "SHAPE@"]) as rows:
        for row in rows:
            shape = row[-1]
            if shape is None:
                continue
            values = dict(zip(others, row[:-1]))
            length = float(shape._shape.length)
            scale = _factor(combined_scale_factor, values, tool)
            offset = _factor(direction_offset, values, tool)
            ground = length / scale if scale else length
            azimuth = _azimuth(shape._shape)
            if azimuth is not None and offset:
                azimuth = (azimuth - offset / 3600.0) % 360.0
            changed = False
            old = values[distance_f]
            write_distance = distance_mode == "OVERWRITE" or (distance_mode == "UPDATE_NULL_ONLY" and old is None) or \
                (distance_mode == "USE_MINIMUM_DIFFERENCE" and (old is None or abs(float(old) - ground) > tolerance))
            if write_distance:
                values[distance_f] = ground
                values[arc_f] = values[radius_f] = values[radius2_f] = None
                changed = True
            old = values[direction_f]
            if azimuth is not None and direction_mode != "DO_NOT_UPDATE":
                if direction_mode == "OVERWRITE" or old is None:
                    write = direction_mode != "UPDATE_NULL_ONLY" or old is None
                elif direction_mode == "UPDATE_NULL_ONLY":
                    write = False
                else:
                    delta = (float(old) - azimuth + 180.0) % 360.0 - 180.0
                    gap = ground * abs(math.sin(math.radians(delta)))
                    write = abs(delta) * 3600.0 > seconds or (lateral > 0 and gap > lateral)
                if write:
                    values[direction_f] = azimuth
                    changed = True
            if changed:
                rows.updateRow([values[n] for n in others] + [shape])
                count += 1
    from ._runtime import AddMessage
    AddMessage(f"{count} line(s) updated.")
    return Result(in_line_features)
