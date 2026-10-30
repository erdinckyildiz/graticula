"""arcpy.analysis's tools grapy builds, by their documented names and parameters — ADR-223 §5.2.

Each reads and writes feature classes through the cursors, so a tool's output is written exactly as a toolbox's own
InsertCursor would write it. Distances are in the data's units, as arcpy's are for a projected reference; a geographic
reference is buffered in metres on the ground, as arcpy's planar tools are not — said in the name of the parameter
that takes a linear unit (``"100 Meters"``).
"""
from __future__ import annotations

import math

from . import _gpkg, _units, da
from ._result import Result
from ._runtime import NotBuiltError



def _schema(path):
    connection, table = _gpkg.table_of(path)
    try:
        return _gpkg.describe(connection, table)
    finally:
        connection.close()


def _distance(value, srid):
    """A linear unit such as ``"100 Meters"`` in the data's units, or a number as it is. A bare ``Feet``, ``Miles``,
    ``Yards``, ``Inches`` or ``NauticalMiles`` is the US survey unit and ``FeetInt`` … the international one, as Pro's
    Buffer measures them (D-323)."""
    if isinstance(value, (int, float)):
        return float(value)
    parts = str(value).split()
    number = float(parts[0])
    if len(parts) == 1:
        return number
    if parts[1].upper() in ("UNKNOWN", "DECIMALDEGREES"):
        return number
    per = _units.linear(parts[1])
    if per is None:
        raise ValueError(f"'{value}' is not a linear unit: '{parts[1]}' is not a unit ArcGIS names.")
    metres = number * per
    from ._reference import SpatialReference
    if srid > 0 and SpatialReference(srid).type == "Geographic":
        raise ValueError("A buffer of a geographic reference in metres needs the data projected first (management.Project).")
    return metres


def Buffer(in_features, out_feature_class, buffer_distance_or_field, line_side="FULL", line_end_type="ROUND",
           dissolve_option="NONE", dissolve_field=None, method="PLANAR", *_, **__):
    columns, _, srid, _ = _schema(in_features)
    names = [c.name for c in columns]
    by_field = isinstance(buffer_distance_or_field, str) and buffer_distance_or_field in names
    _gpkg.create(out_feature_class if ".gpkg" in str(out_feature_class).lower() else out_feature_class + ".gpkg",
                 "Polygon", srid, [] if dissolve_option.upper() == "ALL" else columns)
    out = out_feature_class if ".gpkg" in str(out_feature_class).lower() else out_feature_class + ".gpkg"
    shapes = []
    with da.SearchCursor(in_features, [*names, "SHAPE@"]) as rows:
        for row in rows:
            if row[-1] is None:
                continue
            distance = float(row[names.index(buffer_distance_or_field)]) if by_field else _distance(buffer_distance_or_field, srid)
            kwargs = {"cap_style": "flat" if line_end_type.upper() == "FLAT" else "round"}
            if line_side.upper() in ("LEFT", "RIGHT"):
                kwargs["single_sided"] = True
                distance = distance if line_side.upper() == "LEFT" else -distance
            shape = row[-1]._shape.buffer(distance, **kwargs)
            if line_side.upper() == "OUTSIDE_ONLY":
                shape = shape.difference(row[-1]._shape)
            shapes.append((row[:-1], shape))
    with da.InsertCursor(out, [*([] if dissolve_option.upper() == "ALL" else names), "SHAPE@"]) as written:
        if dissolve_option.upper() == "ALL":
            from shapely import union_all
            written.insertRow((union_all([s for _, s in shapes]),))
        else:
            for values, shape in shapes:
                written.insertRow((*values, shape))
    return Result(out)


def _overlay(in_features, other, out_feature_class, operation):
    columns, shape, srid, _ = _schema(in_features)
    names = [c.name for c in columns]
    out = out_feature_class if ".gpkg" in str(out_feature_class).lower() else out_feature_class + ".gpkg"
    with da.SearchCursor(other, ["SHAPE@"]) as rows:
        from shapely import union_all
        mask = union_all([r[0]._shape for r in rows if r[0] is not None])
    _gpkg.create(out, shape, srid, columns)
    with da.SearchCursor(in_features, [*names, "SHAPE@"]) as rows, da.InsertCursor(out, [*names, "SHAPE@"]) as written:
        for row in rows:
            if row[-1] is None:
                continue
            cut = operation(row[-1]._shape, mask)
            if not cut.is_empty:
                written.insertRow((*row[:-1], cut))
    return Result(out)


def Clip(in_features, clip_features, out_feature_class, cluster_tolerance=None):
    return _overlay(in_features, clip_features, out_feature_class, lambda a, b: a.intersection(b))


def Erase(in_features, erase_features, out_feature_class, cluster_tolerance=None):
    return _overlay(in_features, erase_features, out_feature_class, lambda a, b: a.difference(b))


def _out(path):
    return path if ".gpkg" in str(path).lower() else str(path) + ".gpkg"


def _read(path, srid=None):
    """Every feature of a feature class as (oid, values, shapely shape), in ``srid`` when one is given."""
    from ._reference import SpatialReference
    columns, _, own, _ = _schema(path)
    names = [c.name for c in columns]
    target = SpatialReference(srid) if srid and own > 0 and own != srid else None
    with da.SearchCursor(path, ["OID@", *names, "SHAPE@"]) as rows:
        return [(r[0], list(r[1:-1]), (r[-1].projectAs(target) if target else r[-1])._shape)
                for r in rows if r[-1] is not None]


def _stem(path):
    import os
    file, table = _gpkg.split(_out(path))
    return table or os.path.splitext(os.path.basename(file))[0]


def _joined_columns(*schemas):
    """The output columns of an overlay: ``FID_<input>`` and each input's fields, a repeated name given ``_1``, ``_2``."""
    columns, taken = [], set()
    for stem, own in schemas:
        for column in [_gpkg.Column(f"FID_{stem}", "Integer"), *own]:
            name, i = column.name, 0
            while name.lower() in taken:
                i += 1
                name = f"{column.name}_{i}"
            taken.add(name.lower())
            columns.append(_gpkg.Column(name, column.type))
    return columns


def _dimension(shape):
    return {"Point": 0, "MultiPoint": 0, "LineString": 1, "MultiLineString": 1, "Polygon": 2, "MultiPolygon": 2}.get(
        shape.geom_type, -1)


def _of_dimension(shape, dimension):
    """The part of an overlay's result with the dimension asked, as one geometry, or None when it has none."""
    import shapely
    if shape is None or shape.is_empty:
        return None
    if _dimension(shape) == dimension:
        return shape
    parts = [p for p in getattr(shape, "geoms", [shape]) if _dimension(p) == dimension]
    if not parts:
        return None
    return shapely.union_all(parts)


_KIND_OF = {0: "Point", 1: "Polyline", 2: "Polygon"}
_DIMENSION_OF = {"Point": 0, "Multipoint": 0, "Polyline": 1, "Polygon": 2}


def _pairwise_overlay(a_path, b_path, out_feature_class, pieces, output_type="INPUT"):
    """The overlay of two feature classes: ``pieces(a, b)`` answers each output piece for a pair, or for one side alone."""
    from shapely.strtree import STRtree
    a_columns, a_kind, srid, _ = _schema(a_path)
    b_columns, b_kind, _, _ = _schema(b_path)
    a, b = _read(a_path), _read(b_path, srid)
    dimension = min(_DIMENSION_OF.get(a_kind, 2), _DIMENSION_OF.get(b_kind, 2))
    if str(output_type).upper() == "LINE":
        dimension = 1
    elif str(output_type).upper() == "POINT":
        dimension = 0
    columns = _joined_columns((_stem(a_path), a_columns), (_stem(b_path), b_columns))
    out = _out(out_feature_class)
    _gpkg.create(out, _KIND_OF[dimension], srid, columns)
    with da.InsertCursor(out, [*[c.name for c in columns], "SHAPE@"]) as written:
        for left, right, shape in pieces(a, b, STRtree([s for _, _, s in b]) if b else None):
            kept = _of_dimension(shape, dimension)
            if kept is None:
                continue
            a_values = [left[0], *left[1]] if left else [-1, *[None] * len(a_columns)]
            b_values = [right[0], *right[1]] if right else [-1, *[None] * len(b_columns)]
            written.insertRow((*a_values, *b_values, kept))
    return Result(out)


def _intersections(a, b, tree):
    for left in a:
        for i in (tree.query(left[2], predicate="intersects") if tree is not None else []):
            yield left, b[int(i)], left[2].intersection(b[int(i)][2])


def _outside(features, others, tree):
    """Each feature less every one it meets in ``others``."""
    import shapely
    for feature in features:
        near = [others[int(i)][2] for i in (tree.query(feature[2], predicate="intersects") if tree is not None else [])]
        yield feature, (feature[2].difference(shapely.union_all(near)) if near else feature[2])


def _fold(inputs, out_feature_class, step):
    """A tool of two inputs over many, as arcpy's Intersect and Union take any number: each result is the next input."""
    import os
    import tempfile
    paths = _list(inputs)
    if len(paths) == 1:
        raise ValueError("This tool needs two inputs or more.")
    current = paths[0]
    scratch = tempfile.mkdtemp(dir=os.path.dirname(os.path.abspath(_gpkg.split(_out(out_feature_class))[0])))
    for i, path in enumerate(paths[1:]):
        last = i == len(paths) - 2
        current = step(current, path, _out(out_feature_class) if last else os.path.join(scratch, f"step{i}.gpkg")).getOutput(0)
    return Result(current)


def _list(value):
    from .management import _list as listed
    return listed(value)


def Intersect(in_features, out_feature_class, join_attributes="ALL", cluster_tolerance=None, output_type="INPUT"):
    """Where the inputs overlap, each piece with every input's attributes."""
    return _fold(in_features, out_feature_class,
                 lambda a, b, out: _pairwise_overlay(a, b, out, _intersections, output_type))


def Union(in_features, out_feature_class, join_attributes="ALL", cluster_tolerance=None, gaps="GAPS"):
    """Every piece of either polygon input: the overlaps with both inputs' attributes, the rest with one side's and
    ``FID_`` -1 for the other, as arcpy writes them."""
    def pieces(a, b, tree):
        from shapely.strtree import STRtree
        yield from _intersections(a, b, tree)
        for left, rest in _outside(a, b, tree):
            yield left, None, rest
        back = STRtree([s for _, _, s in a]) if a else None
        for right, rest in _outside(b, a, back):
            yield None, right, rest
    return _fold(in_features, out_feature_class, lambda a, b, out: _pairwise_overlay(a, b, out, pieces))


def Identity(in_features, identity_features, out_feature_class, join_attributes="ALL", cluster_tolerance=None,
             relationship="NO_RELATIONSHIPS"):
    """The input's features cut by the identity features: the overlaps with both attributes, the rest with the input's."""
    def pieces(a, b, tree):
        yield from _intersections(a, b, tree)
        for left, rest in _outside(a, b, tree):
            yield left, None, rest
    return _pairwise_overlay(in_features, identity_features, out_feature_class, pieces)


def SymDiff(in_features, update_features, out_feature_class, join_attributes="ALL", cluster_tolerance=None):
    """What lies in one input and not the other."""
    def pieces(a, b, tree):
        from shapely.strtree import STRtree
        for left, rest in _outside(a, b, tree):
            yield left, None, rest
        back = STRtree([s for _, _, s in a]) if a else None
        for right, rest in _outside(b, a, back):
            yield None, right, rest
    return _pairwise_overlay(in_features, update_features, out_feature_class, pieces)


def Select(in_features, out_feature_class, where_clause=None):
    """The features a where clause chooses, copied with their attributes."""
    columns, shape, srid, _ = _schema(in_features)
    names = [c.name for c in columns]
    out = _out(out_feature_class)
    _gpkg.create(out, shape, srid, columns)
    with da.SearchCursor(in_features, [*names, "SHAPE@"], where_clause) as rows, \
            da.InsertCursor(out, [*names, "SHAPE@"]) as written:
        for row in rows:
            written.insertRow(row)
    return Result(out)


TableSelect = Select


def _match(option, radius):
    """The test SpatialJoin's match option names, between a target shape and a join shape."""
    option = str(option).upper()
    tests = {
        "INTERSECT": lambda t, j: t.intersects(j) or (radius > 0 and t.distance(j) <= radius),
        "WITHIN_A_DISTANCE": lambda t, j: t.distance(j) <= radius,
        "WITHIN_A_DISTANCE_GEODESIC": lambda t, j: t.distance(j) <= radius,
        "CONTAINS": lambda t, j: t.contains(j), "COMPLETELY_CONTAINS": lambda t, j: t.contains_properly(j) or t.contains(j),
        "WITHIN": lambda t, j: t.within(j), "COMPLETELY_WITHIN": lambda t, j: t.within(j),
        "HAVE_THEIR_CENTER_IN": lambda t, j: j.contains(t.centroid),
        "SHARE_A_LINE_SEGMENT_WITH": lambda t, j: t.intersection(j).length > 0 and not t.intersection(j).area,
        "BOUNDARY_TOUCHES": lambda t, j: t.touches(j), "ARE_IDENTICAL_TO": lambda t, j: t.equals(j),
        "CROSSED_BY_THE_OUTLINE_OF": lambda t, j: t.crosses(j.boundary) if j.geom_type.endswith("Polygon") else t.crosses(j),
    }
    if option not in tests and option != "CLOSEST":
        raise ValueError(f"SpatialJoin's match option '{option}' is not built in grapy yet.")
    return tests.get(option)


def SpatialJoin(target_features, join_features, out_feature_class, join_operation="JOIN_ONE_TO_ONE", join_type="KEEP_ALL",
                field_mapping=None, match_option="INTERSECT", search_radius=None, distance_field_name=None, *_, **__):
    """Each target feature with the attributes of the join features that match it. One to one keeps the first match and
    counts them in ``Join_Count``; one to many writes a row per match with its ``JOIN_FID``."""
    from shapely.strtree import STRtree
    t_columns, t_kind, srid, _ = _schema(target_features)
    j_columns, _, _, _ = _schema(join_features)
    radius = _distance(search_radius, srid) if search_radius not in (None, "", "#") else 0.0
    targets, joins = _read(target_features), _read(join_features, srid)
    tree = STRtree([s for _, _, s in joins]) if joins else None
    many = str(join_operation).upper() == "JOIN_ONE_TO_MANY"
    keep_all = str(join_type).upper() == "KEEP_ALL"
    closest = str(match_option).upper() in ("CLOSEST", "CLOSEST_GEODESIC")
    test = None if closest else _match(match_option, radius)
    lead = [_gpkg.Column("Join_Count", "Integer"), _gpkg.Column("TARGET_FID", "Integer")]
    if many:
        lead.append(_gpkg.Column("JOIN_FID", "Integer"))
    taken = {c.name.lower() for c in [*lead, *t_columns]}
    renamed = []
    for column in j_columns:
        name, i = column.name, 0
        while name.lower() in taken:
            i += 1
            name = f"{column.name}_{i}"
        taken.add(name.lower())
        renamed.append(_gpkg.Column(name, column.type))
    extra = [_gpkg.Column(distance_field_name, "Double")] if distance_field_name else []
    columns = [*lead, *t_columns, *renamed, *extra]
    out = _out(out_feature_class)
    _gpkg.create(out, t_kind, srid, columns)
    with da.InsertCursor(out, [*[c.name for c in columns], "SHAPE@"]) as written:
        for oid, values, shape in targets:
            if closest:
                found = []
                if tree is not None:
                    nearest = int(tree.nearest(shape))
                    if radius <= 0 or shape.distance(joins[nearest][2]) <= radius:
                        found = [nearest]
            else:
                window = shape.buffer(radius) if radius > 0 else shape
                found = [int(i) for i in (tree.query(window) if tree is not None else []) if test(shape, joins[int(i)][2])]
                found.sort()
            distance = (lambda j: [shape.distance(joins[j][2])]) if extra else (lambda j: [])
            if not found:
                if keep_all:
                    written.insertRow((0, oid, *([-1] if many else []), *values, *[None] * len(renamed),
                                       *([None] if extra else []), shape))
                continue
            if many:
                for j in found:
                    written.insertRow((1, oid, joins[j][0], *values, *joins[j][1], *distance(j), shape))
            else:
                written.insertRow((len(found), oid, *values, *joins[found[0]][1], *distance(found[0]), shape))
    return Result(out)


def Near(in_features, near_features, search_radius=None, location="NO_LOCATION", angle="NO_ANGLE", method="PLANAR",
         field_names=None, distance_unit=None):
    """``NEAR_FID`` and ``NEAR_DIST`` added to each input feature — -1 for both when nothing is within the radius — and
    ``NEAR_X``, ``NEAR_Y`` and ``NEAR_ANGLE`` when asked. ``NEAR_DIST`` is in ``distance_unit`` when one is named, its
    bare ``Feet``, ``Miles``, ``Yards`` and ``NauticalMiles`` the US survey units, as measured in Pro (D-323)."""
    import math
    from shapely.ops import nearest_points
    from shapely.strtree import STRtree
    _, _, srid, _ = _schema(in_features)
    radius = _distance(search_radius, srid) if search_radius not in (None, "", "#") else 0.0
    factor = _unit_factor(distance_unit, _units.NEAR, srid, "Near", "distance_unit")
    candidates = [c for path in _list(near_features) for c in [(path, *f) for f in _read(path, srid)]]
    tree = STRtree([c[3] for c in candidates]) if candidates else None
    fields = ["NEAR_FID", "NEAR_DIST"]
    if str(location).upper() == "LOCATION":
        fields += ["NEAR_X", "NEAR_Y"]
    if str(angle).upper() == "ANGLE":
        fields.append("NEAR_ANGLE")
    if len(_list(near_features)) > 1:
        fields.append("NEAR_FC")
    existing = {c.name.lower() for c in _schema(in_features)[0]}
    for name in fields:
        if name.lower() not in existing:
            _gpkg.add_column(in_features, _gpkg.Column(name, "String" if name == "NEAR_FC" else
                                                       "Integer" if name == "NEAR_FID" else "Double"))
    with da.UpdateCursor(in_features, [*fields, "SHAPE@"]) as rows:
        for row in rows:
            shape = row[-1]._shape if row[-1] is not None else None
            values = {"NEAR_FID": -1, "NEAR_DIST": -1.0, "NEAR_X": -1.0, "NEAR_Y": -1.0, "NEAR_ANGLE": 0.0, "NEAR_FC": ""}
            if shape is not None and tree is not None:
                nearest = candidates[int(tree.nearest(shape))]
                gap = shape.distance(nearest[3])
                if radius <= 0 or gap <= radius:
                    here, there = nearest_points(shape, nearest[3])
                    values.update(NEAR_FID=nearest[1], NEAR_DIST=gap * factor, NEAR_X=there.x, NEAR_Y=there.y, NEAR_FC=str(nearest[0]),
                                  NEAR_ANGLE=math.degrees(math.atan2(there.y - here.y, there.x - here.x)))
            rows.updateRow([*[values[f] for f in fields], row[-1]])
    return Result(in_features)


def _summary_table(in_table, out_table, groups, statistics, lead, columns_of):
    """A table of one row per group: its fields, ``FREQUENCY`` and each statistic as ``SUM_field``. It is written as a
    feature class with no shapes, which is how a GeoPackage table crosses back to the server."""
    from .management import _statistic, _statistic_type
    out = _out(out_table)
    out_columns = [*lead, _gpkg.Column("FREQUENCY", "Integer"),
                   *[_gpkg.Column(f"{s}_{f}", _statistic_type(s, columns_of.get(f.lower()))) for f, s in statistics]]
    _gpkg.create(out, "Geometry", 0, out_columns)
    with da.InsertCursor(out, [*[c.name for c in out_columns], "SHAPE@"]) as written:
        for key, (count, values) in groups.items():
            written.insertRow((*key, count, *[_statistic(s, v) for (_, s), v in zip(statistics, values)], None))
    return Result(out)


def _grouped(in_table, fields, statistics):
    groups: dict = {}
    with da.SearchCursor(in_table, [*fields, *[f for f, _ in statistics]] or ["OID@"]) as rows:
        for row in rows:
            key = tuple(row[: len(fields)])
            group = groups.setdefault(key, [0, [[] for _ in statistics]])
            group[0] += 1
            for i, value in enumerate(row[len(fields): len(fields) + len(statistics)]):
                group[1][i].append(value)
    return {k: tuple(v) for k, v in groups.items()}


def Statistics(in_table, out_table, statistics_fields, case_field=None, concatenation_separator=""):
    """Summary statistics of fields, ``[[field, SUM|MEAN|MIN|MAX|RANGE|STD|COUNT|FIRST|LAST|MEDIAN|VARIANCE|UNIQUE]]``,
    over the whole table or for each value of the case fields."""
    from .management import _pairs
    columns_of = {c.name.lower(): c for c in _schema(in_table)[0]}
    cases = [columns_of[f.lower()].name for f in _list(case_field)]
    statistics = [(columns_of[f.lower()].name, s.upper()) for f, s in _pairs(statistics_fields)]
    groups = _grouped(in_table, cases, statistics)
    return _summary_table(in_table, out_table, groups, statistics, [columns_of[f.lower()] for f in cases], columns_of)


def Frequency(in_table, out_table, frequency_fields, summary_fields=None):
    """How many rows have each combination of the frequency fields' values, and the sum of each summary field."""
    columns_of = {c.name.lower(): c for c in _schema(in_table)[0]}
    fields = [columns_of[f.lower()].name for f in _list(frequency_fields)]
    statistics = [(columns_of[f.lower()].name, "SUM") for f in _list(summary_fields)]
    groups = _grouped(in_table, fields, statistics)
    result = _summary_table(in_table, out_table, groups, statistics, [columns_of[f.lower()] for f in fields], columns_of)
    if statistics:
        from .management import AlterField
        for field, _ in statistics:
            AlterField(result.getOutput(0), f"SUM_{field}", field)
    return result


def MultipleRingBuffer(input_features, output_feature_class, distances, buffer_unit="", field_name="distance",
                       dissolve_option="ALL", outside_polygons_only="FULL", method="PLANAR"):
    """Rings around the features at each distance, each ring the band between it and the one inside it, and its
    distance in ``field_name``."""
    import shapely
    columns, _, srid, _ = _schema(input_features)
    scale = _distance(f"1 {buffer_unit}", srid) if buffer_unit else 1.0
    steps = sorted(float(d) for d in _list(distances))
    features = _read(input_features)
    dissolve = str(dissolve_option).upper() == "ALL"
    out = _out(output_feature_class)
    out_columns = ([] if dissolve else columns) + [_gpkg.Column(field_name, "Double")]
    _gpkg.create(out, "Polygon", srid, out_columns)
    groups = [([], shapely.union_all([s for _, _, s in features]))] if dissolve else [(v, s) for _, v, s in features]
    with da.InsertCursor(out, [*[c.name for c in out_columns], "SHAPE@"]) as written:
        for values, shape in groups:
            inner = shape if str(outside_polygons_only).upper() == "OUTSIDE_ONLY" and _dimension(shape) == 2 else None
            for step in steps:
                ring = shape.buffer(step * scale)
                band = ring.difference(inner) if inner is not None else ring
                written.insertRow((*values, step, band))
                inner = ring
    return Result(out)


def PairwiseBuffer(in_features, out_feature_class, buffer_distance_or_field, dissolve_option="NONE", dissolve_field=None,
                   method="PLANAR", max_deviation=None):
    return Buffer(in_features, out_feature_class, buffer_distance_or_field, dissolve_option=dissolve_option,
                  dissolve_field=dissolve_field)


def PairwiseClip(in_features, clip_features, out_feature_class, cluster_tolerance=None):
    return Clip(in_features, clip_features, out_feature_class)


def PairwiseErase(in_features, erase_features, out_feature_class, cluster_tolerance=None):
    return Erase(in_features, erase_features, out_feature_class)


def PairwiseIntersect(in_features, out_feature_class, join_attributes="ALL", cluster_tolerance=None, output_type="INPUT"):
    return Intersect(in_features, out_feature_class, join_attributes, output_type=output_type)


def PairwiseDissolve(in_features, out_feature_class, dissolve_field=None, statistics_fields=None, multi_part="MULTI_PART",
                     concatenation_separator=""):
    from .management import Dissolve
    return Dissolve(in_features, out_feature_class, dissolve_field, statistics_fields, multi_part)



# ---- Tools added for ADR-224. Each refuses by name the parameter values it does not build.

import shapely  # noqa: E402 — the tools below use it throughout

from ._runtime import ExecuteError  # noqa: E402

# The units a tool writes its measures in are each tool's own keywords, measured in Pro: _units (D-323).


def _refuse(tool, parameter, value, why=""):
    raise NotBuiltError(f"arcpy.analysis.{tool}: {parameter} '{value}' is not built in grapy{': ' + why if why else ''}.")


def _unit_factor(unit, table, srid, tool, parameter, power=1):
    """What a measure in the data's units is multiplied by to be in ``unit``; 1 when no unit is named."""
    if unit in (None, "", "#") or str(unit).upper() == "UNKNOWN":
        return 1.0
    name = str(unit).upper()
    if name not in table:
        _refuse(tool, parameter, unit, "it is not one of the units this parameter takes")
    from ._reference import SpatialReference
    if srid <= 0 or SpatialReference(srid).type == "Geographic":
        _refuse(tool, parameter, unit, "a unit for data in degrees needs the data projected first (management.Project)")
    return SpatialReference(srid).metersPerUnit ** power / table[name]


def _pairs(value):
    from .management import _pairs as pairs
    return pairs(value)


def _same(a, b):
    import os
    return os.path.normcase(os.path.abspath(_out(a))) == os.path.normcase(os.path.abspath(_out(b)))


def _table(out_table, columns):
    """A new table of no shapes, as the other summary tools write one."""
    out = _out(out_table)
    _gpkg.create(out, "Geometry", 0, columns)
    return out


def _radius(search_radius, srid):
    return _distance(search_radius, srid) if search_radius not in (None, "", "#") else None


# ---- Proximity


def PointDistance(in_features, near_features, out_table, search_radius=None):
    """The distance from every input point to every near point within the radius (all of them when no radius is
    given): ``INPUT_FID``, ``NEAR_FID``, ``DISTANCE``, in the input's units. A point is not paired with itself when the
    two inputs are the same.

    INFERRED: a near point exactly at the radius is within it; the rows run by input then near ObjectID.
    """
    columns, kind, srid, _ = _schema(in_features)
    if kind != "Point" or _schema(near_features)[1] != "Point":
        raise ExecuteError("ERROR 000365: PointDistance takes points.")
    radius = _radius(search_radius, srid)
    same = _same(in_features, near_features)
    near = _read(near_features, srid)
    out = _table(out_table, [_gpkg.Column("INPUT_FID", "Integer"), _gpkg.Column("NEAR_FID", "Integer"),
                             _gpkg.Column("DISTANCE", "Double")])
    with da.InsertCursor(out, ["INPUT_FID", "NEAR_FID", "DISTANCE", "SHAPE@"]) as written:
        for oid, _, shape in _read(in_features):
            for near_oid, _, near_shape in near:
                if same and near_oid == oid:
                    continue
                gap = shape.distance(near_shape)
                if radius is None or gap <= radius:
                    written.insertRow((oid, near_oid, gap, None))
    return Result(out)


def GenerateNearTable(in_features, near_features, out_table, search_radius=None, location="NO_LOCATION",
                      angle="NO_ANGLE", closest="CLOSEST", closest_count=0, method="PLANAR", distance_unit=None,
                      match_fields=None):
    """A table of the near features of each input feature: ``IN_FID``, ``NEAR_FID``, ``NEAR_DIST`` and ``NEAR_RANK``
    (1 for the closest), with ``FROM_X``, ``FROM_Y``, ``NEAR_X``, ``NEAR_Y`` under ``LOCATION``, ``NEAR_ANGLE`` under
    ``ANGLE`` (planar: 0° east, 90° north, −180°…180°), and ``NEAR_FC`` when there are several near inputs. An input
    with nothing within the radius has no row; a feature is not its own near feature.

    INFERRED: the fields are written in the order above; features at equal distance are ranked in the order of the
    near inputs and then by ObjectID; a near feature exactly at the radius is within it.
    """
    import math
    from shapely.ops import nearest_points
    if str(method).upper() != "PLANAR":
        _refuse("GenerateNearTable", "method", method, "geodesic distances are not built")
    columns, _, srid, _ = _schema(in_features)
    radius = _radius(search_radius, srid)
    factor = _unit_factor(distance_unit, _units.NEAR, srid, "GenerateNearTable", "distance_unit")
    paths = _list(near_features)
    pairs = _pairs(match_fields)
    candidates = []
    for path in paths:
        names = [c.name.lower() for c in _schema(path)[0]]
        for oid, values, shape in _read(path, srid):
            candidates.append((path, oid, {n: v for n, v in zip(names, values)}, shape))
    own_names = [c.name.lower() for c in columns]
    every = str(closest).upper() == "ALL"
    cap = int(closest_count or 0)
    fields = [_gpkg.Column("IN_FID", "Integer"), _gpkg.Column("NEAR_FID", "Integer"), _gpkg.Column("NEAR_DIST", "Double"),
              _gpkg.Column("NEAR_RANK", "Integer")]
    located = str(location).upper() == "LOCATION"
    angled = str(angle).upper() == "ANGLE"
    if located:
        fields += [_gpkg.Column(n, "Double") for n in ("FROM_X", "FROM_Y", "NEAR_X", "NEAR_Y")]
    if angled:
        fields.append(_gpkg.Column("NEAR_ANGLE", "Double"))
    if len(paths) > 1:
        fields.append(_gpkg.Column("NEAR_FC", "String"))
    out = _table(out_table, fields)
    with da.InsertCursor(out, [*[c.name for c in fields], "SHAPE@"]) as written:
        for oid, values, shape in _read(in_features):
            own = dict(zip(own_names, values))
            found = []
            for order, (path, near_oid, near_values, near_shape) in enumerate(candidates):
                if near_oid == oid and _same(path, in_features):
                    continue
                if any(own.get(a.lower()) != near_values.get(b.lower()) for a, b in pairs):
                    continue
                gap = shape.distance(near_shape)
                if radius is None or gap <= radius:
                    found.append((gap, order, path, near_oid, near_shape))
            found.sort(key=lambda f: (f[0], f[1]))
            found = found[:1] if not every else (found[:cap] if cap > 0 else found)
            for rank, (gap, _, path, near_oid, near_shape) in enumerate(found, 1):
                row = [oid, near_oid, gap * factor, rank]
                here, there = nearest_points(shape, near_shape)
                if located:
                    row += [here.x, here.y, there.x, there.y]
                if angled:
                    row.append(0.0 if gap == 0 else math.degrees(math.atan2(there.y - here.y, there.x - here.x)))
                if len(paths) > 1:
                    row.append(str(path))
                written.insertRow((*row, None))
    return Result(out)


def GenerateOriginDestinationLinks(origin_features, destination_features, out_feature_class, origin_group_field=None,
                                   destination_group_field=None, line_type="PLANAR", num_nearest=None,
                                   search_distance=None, distance_unit=None, aggregate_links="NO_AGGREGATE",
                                   sum_fields=None):
    """A line from each origin to each destination — to its ``num_nearest`` nearest only when given, within
    ``search_distance`` when given — with ``ORIG_FID``, ``ORIG_X``, ``ORIG_Y``, ``DEST_FID``, ``DEST_X``, ``DEST_Y``,
    ``LINK_DIST`` in ``distance_unit``, ``GROUP_ID`` when both group fields are given (only origins and destinations of
    the same group are linked), and ``COLOR_ID``, a random integer from 1 to 8. A line or polygon is linked from its
    centroid; a feature is not linked to itself.

    ``sum_fields`` is used only when overlapping links are aggregated, which is not built.

    The six distance units are the US survey ones where they are imperial, as measured in Pro (D-323).

    INFERRED: ``search_distance`` is inclusive; nearest links are
    chosen by distance, then destination ObjectID; a link of length zero between two different features is not written,
    as the documentation says a zero-length line is a null geometry; the output holds the fields above and no others.
    """
    import random
    if str(line_type).upper() != "PLANAR":
        _refuse("GenerateOriginDestinationLinks", "line_type", line_type, "geodesic links are not built")
    if str(aggregate_links).upper() != "NO_AGGREGATE":
        _refuse("GenerateOriginDestinationLinks", "aggregate_links", aggregate_links)
    grouped = origin_group_field not in (None, "", "#") or destination_group_field not in (None, "", "#")
    if grouped and (origin_group_field in (None, "", "#") or destination_group_field in (None, "", "#")):
        raise ExecuteError("ERROR 000735: origin and destination group fields are given together.")
    o_columns, _, srid, _ = _schema(origin_features)
    d_columns = _schema(destination_features)[0]
    factor = _unit_factor(distance_unit, _units.OD_LINKS, srid, "GenerateOriginDestinationLinks", "distance_unit")
    limit = float(search_distance) / factor if search_distance not in (None, "", "#") else None
    nearest = int(float(num_nearest)) if num_nearest not in (None, "", "#") else None

    def located(path, columns, field):
        index = [c.name.lower() for c in columns].index(field.lower()) if grouped else None
        return [(oid, values[index] if grouped else None, shape.centroid) for oid, values, shape in _read(path, srid)]

    origins = located(origin_features, o_columns, origin_group_field)
    destinations = located(destination_features, d_columns, destination_group_field)
    same = _same(origin_features, destination_features)
    fields = [_gpkg.Column("ORIG_FID", "Integer"), _gpkg.Column("ORIG_X", "Double"), _gpkg.Column("ORIG_Y", "Double"),
              _gpkg.Column("DEST_FID", "Integer"), _gpkg.Column("DEST_X", "Double"), _gpkg.Column("DEST_Y", "Double"),
              _gpkg.Column("LINK_DIST", "Double")]
    if grouped:
        kind = next(c.type for c in o_columns if c.name.lower() == origin_group_field.lower())
        fields.append(_gpkg.Column("GROUP_ID", kind))
    fields.append(_gpkg.Column("COLOR_ID", "Integer"))
    out = _out(out_feature_class)
    _gpkg.create(out, "Polyline", srid, fields)
    from shapely.geometry import LineString
    with da.InsertCursor(out, [*[c.name for c in fields], "SHAPE@"]) as written:
        for oid, group, here in origins:
            links = []
            for d_oid, d_group, there in destinations:
                if (same and d_oid == oid) or (grouped and group != d_group):
                    continue
                gap = here.distance(there)
                if gap == 0 or (limit is not None and gap > limit):
                    continue
                links.append((gap, d_oid, there))
            links.sort(key=lambda link: (link[0], link[1]))
            for gap, d_oid, there in links[:nearest] if nearest is not None else links:
                written.insertRow((oid, here.x, here.y, d_oid, there.x, there.y, gap * factor,
                                   *([group] if grouped else []), random.randint(1, 8), LineString([here, there])))
    return Result(out)


def _neighbors(tool, in_features, out_table, in_fields, area_overlap, both_sides, out_linear_units, out_area_units,
               crossings):
    from shapely.strtree import STRtree
    columns, kind, srid, _ = _schema(in_features)
    if kind != "Polygon":
        raise ExecuteError(f"ERROR 000365: {tool} takes polygons.")
    overlap = str(area_overlap or "NO_AREA_OVERLAP").upper() == "AREA_OVERLAP"
    both = str(both_sides or "BOTH_SIDES").upper() == "BOTH_SIDES"
    linear = _unit_factor(out_linear_units, _units.NEIGHBOR_LINEAR, srid, tool, "out_linear_units")
    areal = _unit_factor(out_area_units, _units.NEIGHBOR_AREAL, srid, tool, "out_area_units", 2) if overlap else 1.0
    by_name = {c.name.lower(): c for c in columns}
    report = [by_name[f.lower()] for f in _list(in_fields)] or [_gpkg.Column("OBJECTID", "Integer")]
    features = _read(in_features)
    keys = [tuple(values[columns.index(c)] for c in report) if _list(in_fields) else (oid,) for oid, values, _ in features]
    if len(set(keys)) != len(keys):
        _refuse(tool, "in_fields", in_fields, "fields that group several polygons are summarized by group, and the "
                "documentation does not say how a group's own inner edges are reported")
    out_columns = [*[_gpkg.Column(f"src_{c.name}", c.type) for c in report],
                   *[_gpkg.Column(f"nbr_{c.name}", c.type) for c in report],
                   *([_gpkg.Column("AREA", "Double")] if overlap else []),
                   _gpkg.Column("LENGTH", "Double"), _gpkg.Column("NODE_COUNT", "Integer")]
    out = _table(out_table, out_columns)
    tree = STRtree([s for _, _, s in features])
    rows = []
    for i, (_, _, a) in enumerate(features):
        for j in sorted(int(k) for k in tree.query(a)):
            if j == i or (not both and j < i):
                continue
            b = features[j][2]
            shared = a.intersection(b)
            if shared.area > 0 and overlap:
                rows.append((i, j, shared.area * areal, 0.0, 0))
                continue
            if shared.area > 0 and not crossings:
                continue
            edge = a.boundary.intersection(b.boundary)
            length = edge.length
            if length > 0:
                rows.append((i, j, 0.0, length * linear, 0))
                continue
            nodes = sum(1 for p in getattr(edge, "geoms", [edge]) if p.geom_type == "Point" and not p.is_empty)
            if nodes:
                rows.append((i, j, 0.0, 0.0, nodes))
    with da.InsertCursor(out, [*[c.name for c in out_columns], "SHAPE@"]) as written:
        for i, j, area, length, nodes in rows:
            written.insertRow((*keys[i], *keys[j], *([area] if overlap else []), length, nodes, None))
    return Result(out)


def PolygonNeighbors(in_features, out_table, in_fields=None, area_overlap="NO_AREA_OVERLAP", both_sides="BOTH_SIDES",
                     cluster_tolerance=None, out_linear_units=None, out_area_units=None):
    """Each pair of polygons that share an edge or a point: ``src_<field>`` and ``nbr_<field>`` for each report field,
    ``LENGTH`` of their shared boundary, ``NODE_COUNT`` of the points where they touch or cross, and under
    ``AREA_OVERLAP`` the ``AREA`` they overlap (and then 0 for the other two). Overlap is judged first, then edges, then
    nodes. Polygons that are not neighbors have no row.

    INFERRED: with no report fields the polygons are reported by ObjectID as ``src_OBJECTID`` and ``nbr_OBJECTID``;
    under ``NO_BOTH_SIDES`` the lower ObjectID is the source; rows run by source, then neighbor. Report fields that do
    not tell every polygon apart are refused (see the message).
    """
    return _neighbors("PolygonNeighbors", in_features, out_table, in_fields, area_overlap, both_sides, out_linear_units,
                      out_area_units, crossings=True)


def PairwisePolygonNeighbors(in_features, out_table, in_fields=None, area_overlap="NO_AREA_OVERLAP",
                             both_sides="BOTH_SIDES", cluster_tolerance=None, out_linear_units=None, out_area_units=None,
                             precision="DEFAULT_PRECISION"):
    """As PolygonNeighbors, except that polygons whose boundaries cross are not node neighbors: only touching counts.
    The computation is in double precision under either ``precision``."""
    return _neighbors("PairwisePolygonNeighbors", in_features, out_table, in_fields, area_overlap, both_sides,
                      out_linear_units, out_area_units, crossings=False)


def CreateThiessenPolygons(in_features, out_feature_class, fields_to_copy="ONLY_FID"):
    """The Voronoi polygon of each input point — every place in it nearer that point than any other — bounded by the
    points' extent plus 10%, or by ``env.extent`` when it is set.

    INFERRED: *plus 10%* is read as each side moved out by a tenth of the extent's width or height; the input's ObjectID
    is written as ``Input_FID`` (and ``ALL`` adds every input field after it); of coincident points only the first gets
    a polygon.
    """
    from shapely.geometry import MultiPoint, box
    from shapely.strtree import STRtree
    from ._runtime import env
    columns, kind, srid, _ = _schema(in_features)
    if kind != "Point":
        raise ExecuteError("ERROR 000365: CreateThiessenPolygons takes points.")
    copy_all = str(fields_to_copy or "ONLY_FID").upper() == "ALL"
    points = _read(in_features)
    if env.extent is not None:
        frame = box(env.extent.XMin, env.extent.YMin, env.extent.XMax, env.extent.YMax)
    else:
        x0, y0, x1, y1 = shapely.MultiPoint([s for _, _, s in points]).bounds
        if x1 - x0 == 0 and y1 - y0 == 0:
            raise ExecuteError("ERROR 000853: CreateThiessenPolygons needs points that are not all at one place.")
        dx, dy = (x1 - x0) * 0.1, (y1 - y0) * 0.1
        frame = box(x0 - dx, y0 - dy, x1 + dx, y1 + dy)
    cells = [c.intersection(frame) for c in shapely.voronoi_polygons(MultiPoint([s for _, _, s in points]),
                                                                       extend_to=frame).geoms]
    tree = STRtree(cells)
    out_columns = [_gpkg.Column("Input_FID", "Integer"), *(columns if copy_all else [])]
    out = _out(out_feature_class)
    _gpkg.create(out, "Polygon", srid, out_columns)
    used = set()
    with da.InsertCursor(out, [*[c.name for c in out_columns], "SHAPE@"]) as written:
        for oid, values, shape in points:
            for k in sorted(int(i) for i in tree.query(shape, predicate="within")):
                if k not in used:
                    used.add(k)
                    written.insertRow((oid, *(values if copy_all else []), cells[k]))
                    break
    return Result(out)


# ---- Overlay


def CountOverlappingFeatures(in_features, out_feature_class, min_overlap_count=1, out_overlap_table=None):
    """The planarized union of the inputs with ``COUNT_``, how many features cover each piece, and ``COUNT_FC``, how
    many of the inputs they come from; only pieces covered at least ``min_overlap_count`` times are written, and no
    other field. The overlap table has a row per covering feature: ``OVERLAP_OID``, ``ORIG_OID`` and, with several
    inputs, ``ORIG_NAME``.

    Polygons and points are built; lines, where only lines overlapping along their length count, are refused.

    INFERRED: a piece is a face of the polygons' noded boundaries, so two adjacent faces are separate features even
    at the same count; coincident points are one output point; ``ORIG_NAME`` is the input's name.
    """
    paths = _list(in_features)
    kinds = {_schema(p)[1] for p in paths}
    if len(kinds) != 1:
        raise ExecuteError("ERROR 000366: CountOverlappingFeatures needs inputs of one shape type.")
    kind = kinds.pop()
    if kind not in ("Polygon", "Point"):
        _refuse("CountOverlappingFeatures", "in_features", kind, "only polygons and points are built")
    srid = _schema(paths[0])[2]
    features = [(k, oid, shape) for k, p in enumerate(paths) for oid, _, shape in _read(p, srid)]
    least = max(int(min_overlap_count or 1), 1)
    pieces = []
    if kind == "Polygon":
        from shapely.strtree import STRtree
        noded = shapely.union_all([s.boundary for _, _, s in features])
        faces = shapely.get_parts(shapely.polygonize(shapely.get_parts(noded)))
        tree = STRtree([s for _, _, s in features])
        for face in faces:
            inside = face.representative_point()
            pieces.append((face, [features[int(i)] for i in sorted(tree.query(inside, predicate="within"))]))
    else:
        at: dict = {}
        for feature in features:
            at.setdefault(feature[2].coords[0], []).append(feature)
        from shapely.geometry import MultiPoint, Point
        pieces = [((MultiPoint([xy]) if len(paths) > 1 else Point(xy)), covering) for xy, covering in at.items()]
    out = _out(out_feature_class)
    _gpkg.create(out, "Multipoint" if kind == "Point" and len(paths) > 1 else kind, srid,
                 [_gpkg.Column("COUNT_", "Integer"), _gpkg.Column("COUNT_FC", "Integer")])
    links = []
    with da.InsertCursor(out, ["COUNT_", "COUNT_FC", "SHAPE@"]) as written:
        for shape, covering in pieces:
            if len(covering) < least:
                continue
            fid = written.insertRow((len(covering), len({k for k, _, _ in covering}), shape))
            links += [(fid, oid, _stem(paths[k])) for k, oid, _ in covering]
    if out_overlap_table not in (None, "", "#"):
        several = len(paths) > 1
        table = _table(out_overlap_table, [_gpkg.Column("OVERLAP_OID", "Integer"), _gpkg.Column("ORIG_OID", "Integer"),
                                           *([_gpkg.Column("ORIG_NAME", "String")] if several else [])])
        with da.InsertCursor(table, ["OVERLAP_OID", "ORIG_OID", *(["ORIG_NAME"] if several else []), "SHAPE@"]) as written:
            for fid, oid, name in links:
                written.insertRow((fid, oid, *([name] if several else []), None))
        return Result(out, table)
    return Result(out)


def Update(in_features, update_features, out_feature_class, keep_borders="BORDERS", cluster_tolerance=None):
    """The input polygons with the update polygons laid over them: where an update polygon lies, it replaces the input,
    carrying its own values for the input's fields (empty where it has no field of that name); elsewhere the input is
    kept as it was.

    ``NO_BORDERS`` — dropping the update polygons' outer border — is refused: the documentation does not say which
    attributes the merged pieces then carry beyond that the update's take precedence.

    INFERRED: the whole of each update polygon is written, including any part outside the input; the input's pieces
    are written first, then the update polygons, each in ObjectID order; a field is matched by name without regard to
    case.
    """
    if str(keep_borders or "BORDERS").upper() != "BORDERS":
        _refuse("Update", "keep_borders", keep_borders)
    columns, kind, srid, _ = _schema(in_features)
    u_columns, u_kind, _, _ = _schema(update_features)
    if kind != "Polygon" or u_kind != "Polygon":
        raise ExecuteError("ERROR 000365: Update takes polygons.")
    updates = _read(update_features, srid)
    mask = shapely.union_all([s for _, _, s in updates])
    u_names = [c.name.lower() for c in u_columns]
    out = _out(out_feature_class)
    _gpkg.create(out, "Polygon", srid, columns)
    with da.InsertCursor(out, [*[c.name for c in columns], "SHAPE@"]) as written:
        for _, values, shape in _read(in_features):
            rest = _of_dimension(shape.difference(mask), 2)
            if rest is not None:
                written.insertRow((*values, rest))
        for _, values, shape in updates:
            written.insertRow((*[values[u_names.index(c.name.lower())] if c.name.lower() in u_names else None
                                 for c in columns], shape))
    return Result(out)


def _valid_name(text):
    """A value as a table name: every character but a letter, digit or underscore becomes an underscore."""
    import re
    return re.sub(r"[^0-9A-Za-z_]", "_", str(text))


def _output_in(workspace, name):
    import os
    if ".gpkg" in str(workspace).lower():
        if not name[:1].isalpha():
            raise ExecuteError(f"ERROR 000354: '{name}' must begin with a letter to name a table in a GeoPackage.")
        return f"{_gpkg.split(workspace)[0]}/{name}"
    if not os.path.isdir(workspace):
        raise ExecuteError(f"ERROR 000732: the workspace {workspace} does not exist.")
    return os.path.join(workspace, f"{name}.gpkg")


def Split(in_features, split_features, split_field, out_workspace, cluster_tolerance=None):
    """The input cut by the split polygons into one feature class per value of ``split_field``, each named by its
    value and holding the input's fields; a value whose polygons meet no input feature makes no output.

    INFERRED: a GeoPackage workspace is treated as a geodatabase, where a name must begin with a letter (a folder
    takes any value, each output its own ``<value>.gpkg``); characters other than letters, digits and underscores in
    a value become underscores, as ``ValidateTableName`` makes them; each piece keeps the input's dimension.
    """
    columns, kind, srid, _ = _schema(in_features)
    s_columns, s_kind, _, _ = _schema(split_features)
    if s_kind != "Polygon":
        raise ExecuteError("ERROR 000365: Split's split features must be polygons.")
    by_name = {c.name.lower(): c for c in s_columns}
    if split_field.lower() not in by_name or by_name[split_field.lower()].type != "String":
        raise ExecuteError(f"ERROR 000728: '{split_field}' is not a text field of the split features.")
    index = list(by_name).index(split_field.lower())
    masks: dict = {}
    for _, values, shape in _read(split_features, srid):
        masks.setdefault(values[index], []).append(shape)
    features = _read(in_features)
    dimension = _DIMENSION_OF.get(kind, 2)
    outputs = []
    for value, shapes in masks.items():
        mask = shapely.union_all(shapes)
        pieces = [(values, _of_dimension(shape.intersection(mask), dimension)) for _, values, shape in features]
        pieces = [(v, s) for v, s in pieces if s is not None]
        if not pieces:
            continue
        out = _output_in(out_workspace, _valid_name(value))
        _gpkg.create(out, kind, srid, columns)
        with da.InsertCursor(out, [*[c.name for c in columns], "SHAPE@"]) as written:
            for values, shape in pieces:
                written.insertRow((*values, shape))
        outputs.append(out)
    return Result(out_workspace)


def SplitByAttributes(Input_Table, Target_Workspace, Split_Fields):
    """One feature class per combination of the split fields' values, each holding the rows that have it, with every
    field.

    INFERRED: the documentation does not say how outputs are named. grapy joins the values with underscores, makes
    every other character than a letter, digit or underscore an underscore, writes an empty value as ``None``, and in a
    GeoPackage puts ``T`` before a name that does not begin with a letter.
    """
    columns, kind, srid, _ = _schema(Input_Table)
    by_name = {c.name.lower(): i for i, c in enumerate(columns)}
    fields = _list(Split_Fields)
    if not fields:
        raise ExecuteError("ERROR 000735: SplitByAttributes needs at least one split field.")
    groups: dict = {}
    with da.SearchCursor(Input_Table, [*[c.name for c in columns], "SHAPE@"]) as rows:
        for row in rows:
            groups.setdefault(tuple(row[by_name[f.lower()]] for f in fields), []).append(row)
    for key, rows in groups.items():
        name = _valid_name("_".join(str(v) for v in key))
        if ".gpkg" in str(Target_Workspace).lower() and not name[:1].isalpha():
            name = "T" + name
        out = _output_in(Target_Workspace, name)
        _gpkg.create(out, kind, srid, columns)
        with da.InsertCursor(out, [*[c.name for c in columns], "SHAPE@"]) as written:
            for row in rows:
                written.insertRow(row)
    return Result(Target_Workspace)


def GraphicBuffer(in_features, out_feature_class, buffer_distance_or_field, line_caps="SQUARE", line_joins="MITER",
                  miter_limit=None, max_deviation=None):
    """A planar buffer drawn as a graphic is: square, butt or round caps on points and lines, mitred, bevelled or round
    joins on lines and polygons. The output keeps the input's fields and writes ``BUFF_DIST`` (the distance, in the data's
    units) and ``ORIG_FID``. A polygon that a negative distance collapses is not written.

    INFERRED: the miter limit is a multiple of the distance, as shapely's is, and is 10 when not given; a round cap or
    join is drawn with enough segments to stay within ``max_deviation`` of the true arc, and with 16 to a quarter circle
    when none is given; a ``BUTT`` cap on a point is refused, since a point has no direction to cut it across.
    """
    import math
    caps = {"SQUARE": "square", "BUTT": "flat", "ROUND": "round"}
    joins = {"MITER": "mitre", "BEVEL": "bevel", "ROUND": "round"}
    cap, join = str(line_caps or "SQUARE").upper(), str(line_joins or "MITER").upper()
    if cap not in caps:
        _refuse("GraphicBuffer", "line_caps", line_caps)
    if join not in joins:
        _refuse("GraphicBuffer", "line_joins", line_joins)
    columns, kind, srid, _ = _schema(in_features)
    from ._reference import SpatialReference
    if srid > 0 and SpatialReference(srid).type == "Geographic":
        _refuse("GraphicBuffer", "in_features", in_features, "a buffer of data in degrees needs it projected first")
    if cap == "BUTT" and kind in ("Point", "Multipoint"):
        _refuse("GraphicBuffer", "line_caps", line_caps, "a point has no direction to cut a butt cap across")
    names = [c.name for c in columns]
    by_field = isinstance(buffer_distance_or_field, str) and buffer_distance_or_field.lower() in [n.lower() for n in names]
    deviation = _distance(max_deviation, srid) if max_deviation not in (None, "", "#", 0, "0") else None
    kept = [c for c in columns if c.name.upper() not in ("BUFF_DIST", "ORIG_FID")]
    out_columns = [*kept, _gpkg.Column("BUFF_DIST", "Double"), _gpkg.Column("ORIG_FID", "Integer")]
    out = _out(out_feature_class)
    _gpkg.create(out, "Polygon", srid, out_columns)
    keep = [names.index(c.name) for c in kept]
    with da.InsertCursor(out, [*[c.name for c in out_columns], "SHAPE@"]) as written:
        for oid, values, shape in _read(in_features):
            raw = values[[n.lower() for n in names].index(buffer_distance_or_field.lower())] if by_field \
                else buffer_distance_or_field
            if raw in (None, ""):
                continue
            distance = _distance(raw, srid)
            quad = 16
            if deviation:
                quad = max(1, math.ceil(math.pi / (4 * math.acos(max(-1.0, 1 - deviation / abs(distance))))))
            drawn = shape.buffer(distance, quad_segs=quad, cap_style=caps[cap], join_style=joins[join],
                                 mitre_limit=float(miter_limit) if miter_limit not in (None, "", "#") else 10.0)
            if drawn.is_empty:
                from ._runtime import AddWarning
                AddWarning(f"GraphicBuffer: feature {oid} collapsed at a distance of {distance} and was not written.")
                continue
            written.insertRow((*[values[i] for i in keep], distance, oid, drawn))
    return Result(out)


def TabulateIntersection(in_zone_features, zone_fields, in_class_features, out_table, class_fields=None, sum_fields=None,
                         xy_tolerance=None, out_units=None):
    """How much of each zone each class covers: a row per zone and class that meet, with the zone fields, the class
    fields, each sum field summed in proportion to the share of each class polygon inside the zone, ``AREA`` of the
    intersection in ``out_units``, and ``PERCENTAGE`` of the zone's area it is.

    Polygon zones with polygon classes are built; line and point classes, whose ``PERCENTAGE`` is a share of the class
    rather than of the zone, are refused.

    INFERRED: a zone's area is the sum of its features' areas and the intersection is summed pair by pair, which is how
    PERCENTAGE can pass 100 where features overlap, as the documentation says it can; a zone and class that do not meet
    have no row; the fields are written in the order above.
    """
    z_columns, z_kind, srid, _ = _schema(in_zone_features)
    c_columns, c_kind, _, _ = _schema(in_class_features)
    if z_kind != "Polygon" or c_kind != "Polygon":
        _refuse("TabulateIntersection", "in_class_features", c_kind, "only polygon zones with polygon classes are built")
    factor = _unit_factor(out_units, _units.NEIGHBOR_AREAL, srid, "TabulateIntersection", "out_units", 2)
    z_by = {c.name.lower(): (i, c) for i, c in enumerate(z_columns)}
    c_by = {c.name.lower(): (i, c) for i, c in enumerate(c_columns)}
    zone_names, class_names, sums = _list(zone_fields), _list(class_fields), _list(sum_fields)
    zone_area: dict = {}
    for _, values, shape in _read(in_zone_features):
        key = tuple(values[z_by[f.lower()][0]] for f in zone_names)
        zone_area[key] = zone_area.get(key, 0.0) + shape.area
    totals: dict = {}
    classes = _read(in_class_features, srid)
    from shapely.strtree import STRtree
    tree = STRtree([s for _, _, s in classes])
    for _, values, zone in _read(in_zone_features):
        z_key = tuple(values[z_by[f.lower()][0]] for f in zone_names)
        for k in sorted(int(i) for i in tree.query(zone, predicate="intersects")):
            _, c_values, shape = classes[k]
            area = zone.intersection(shape).area
            if area <= 0:
                continue
            c_key = tuple(c_values[c_by[f.lower()][0]] for f in class_names)
            total = totals.setdefault((z_key, c_key), [0.0, [0.0] * len(sums)])
            total[0] += area
            for n, field in enumerate(sums):
                value = c_values[c_by[field.lower()][0]]
                if value is not None:
                    total[1][n] += value * area / shape.area
    columns = [*[z_by[f.lower()][1] for f in zone_names], *[c_by[f.lower()][1] for f in class_names],
               *[_gpkg.Column(c_by[f.lower()][1].name, "Double") for f in sums],
               _gpkg.Column("AREA", "Double"), _gpkg.Column("PERCENTAGE", "Double")]
    out = _table(out_table, columns)
    with da.InsertCursor(out, [*[c.name for c in columns], "SHAPE@"]) as written:
        for (z_key, c_key), (area, summed) in totals.items():
            written.insertRow((*z_key, *c_key, *summed, area * factor, 100.0 * area / zone_area[z_key], None))
    return Result(out)


def ApportionPolygon(in_features, apportion_fields, target_features, out_features, method="AREA",
                     estimation_features=None, weight_field=None, maintain_geometries="MAINTAIN_GEOMETRIES"):
    """The targets, each with the input polygons' numeric fields apportioned to it by area: an input's value goes to
    each target in proportion to the share of the input's area that the target overlaps.

    Built: ``AREA`` with ``SUM`` and the target geometries kept. ``MEAN`` and ``MEDIAN``, ``LENGTH``, ``POINTS``,
    estimation features, weights and ``INTERSECT_GEOMETRIES`` are refused: the documentation does not say how a mean is
    weighted, and is inconsistent about where the weight field comes from.

    INFERRED: an apportioned field keeps its name (``_1`` added where a target field has it) and is a double; a target no
    input overlaps gets 0; an input part that no target overlaps is lost, as the 50/50 example implies.
    """
    if str(method or "AREA").upper() != "AREA":
        _refuse("ApportionPolygon", "method", method)
    if estimation_features not in (None, "", "#"):
        _refuse("ApportionPolygon", "estimation_features", estimation_features)
    if weight_field not in (None, "", "#"):
        _refuse("ApportionPolygon", "weight_field", weight_field)
    if str(maintain_geometries or "MAINTAIN_GEOMETRIES").upper() != "MAINTAIN_GEOMETRIES":
        _refuse("ApportionPolygon", "maintain_geometries", maintain_geometries)
    columns, kind, srid, _ = _schema(in_features)
    t_columns, t_kind, t_srid, _ = _schema(target_features)
    if kind != "Polygon" or t_kind != "Polygon":
        raise ExecuteError("ERROR 000365: ApportionPolygon takes polygons.")
    by_name = {c.name.lower(): i for i, c in enumerate(columns)}
    fields = []
    for pair in _pairs(apportion_fields):
        statistic = str(pair[1]).upper() if len(pair) > 1 and pair[1] not in ("", "#") else "SUM"
        if statistic != "SUM":
            _refuse("ApportionPolygon", "statistic", statistic)
        fields.append(columns[by_name[pair[0].lower()]].name)
    taken = {c.name.lower() for c in t_columns}
    names = []
    for field in fields:
        name, i = field, 0
        while name.lower() in taken:
            i += 1
            name = f"{field}_{i}"
        taken.add(name.lower())
        names.append(name)
    sources = _read(in_features, t_srid)
    from shapely.strtree import STRtree
    tree = STRtree([s for _, _, s in sources])
    out_columns = [*t_columns, *[_gpkg.Column(n, "Double") for n in names]]
    out = _out(out_features)
    _gpkg.create(out, "Polygon", t_srid, out_columns)
    with da.InsertCursor(out, [*[c.name for c in out_columns], "SHAPE@"]) as written:
        for _, values, target in _read(target_features):
            shares = [0.0] * len(fields)
            for k in tree.query(target, predicate="intersects"):
                _, s_values, source = sources[int(k)]
                share = source.intersection(target).area / source.area if source.area else 0.0
                for n, field in enumerate(fields):
                    value = s_values[by_name[field.lower()]]
                    if value is not None:
                        shares[n] += value * share
            written.insertRow((*values, *shares, target))
    return Result(out)


def AssignWeightsByPairwiseComparison(input_variables, out_table, add_comparison_matrix="ADD_MATRIX",
                                      comparison_matrix=None):
    """Weights of two to nine variables from their pairwise comparisons, as documented: each column of the comparison
    matrix is divided by its total, and a variable's weight is the mean of its row. The table holds ``Variables`` and
    ``Weights``, and under ``ADD_MATRIX`` a column per variable with the matrix between them. Unset comparisons are 1.

    ``comparison_matrix`` is ``"a b 3;b c 0.5"`` or ``[["a", "b", 3], …]``: *a* compared with *b* is 3, so *b* with *a*
    is 1/3. A matrix read from a file is refused.

    INFERRED: ``"a b 3"`` sets the cell of row *a*, column *b*; the variable column is named ``Variables`` (the
    documentation also writes ``Variable``).
    """
    import numpy
    names = [str(v) for v in _list(input_variables)]
    if not 2 <= len(names) <= 9:
        raise ExecuteError("ERROR 000800: AssignWeightsByPairwiseComparison takes two to nine variables.")
    if isinstance(comparison_matrix, str) and comparison_matrix.strip() and ";" not in comparison_matrix \
            and len(comparison_matrix.split()) != 3:
        _refuse("AssignWeightsByPairwiseComparison", "comparison_matrix", comparison_matrix,
                "a matrix read from a table or file is not built; give the comparisons as 'a b value;…'")
    matrix = numpy.ones((len(names), len(names)))
    index = {n.lower(): i for i, n in enumerate(names)}
    for pair in _pairs(comparison_matrix):
        a, b, value = index[str(pair[0]).lower()], index[str(pair[1]).lower()], float(pair[2])
        matrix[a, b], matrix[b, a] = value, 1.0 / value
    weights = (matrix / matrix.sum(axis=0)).mean(axis=1)
    with_matrix = str(add_comparison_matrix or "ADD_MATRIX").upper() == "ADD_MATRIX"
    columns = [_gpkg.Column("Variables", "String"), *([_gpkg.Column(n, "Double") for n in names] if with_matrix else []),
               _gpkg.Column("Weights", "Double")]
    out = _table(out_table, columns)
    with da.InsertCursor(out, [*[c.name for c in columns], "SHAPE@"]) as written:
        for i, name in enumerate(names):
            written.insertRow((name, *(matrix[i].tolist() if with_matrix else []), float(weights[i]), None))
    return Result(out)


from ._runtime import equivalent as _equivalent  # noqa: E402


@_equivalent("each overlap shared by a Voronoi division — CENTER_LINE between the polygons' non-overlapping parts, "
             "THIESSEN between their centroids", "Okabe, Boots, Sugihara and Chiu, Spatial Tessellations, 2nd ed., 2000")
def RemoveOverlapMultiple(in_features, out_feature_class, method="CENTER_LINE", join_attributes="ALL"):
    """EQUIVALENT (INFERRED construction): every polygon keeps what no other covers; each area two or more cover is
    divided among them by a Voronoi division (Okabe et al. 2000) —

    - CENTER_LINE: a point goes to the covering polygon whose own non-overlapping part is nearest, so the border runs
      midway between them, "evenly distributing" the overlap. Computed from points every √(area)/50 along those parts'
      boundaries (their Voronoi cells), so the border is placed to within that spacing.
    - THIESSEN: a point goes to the covering polygon whose centroid is nearest — straight borders, the perpendicular
      bisectors of the centroids.

    GRID is refused: the page shows it only as a picture. The inputs are read together; the output carries ``FID_<input>``
    for each input (−1 when the polygon came from another) and the inputs' fields, merged by name (ALL), the fields
    alone (NO_FID), or the FIDs alone (ONLY_FID).
    """
    import math
    import os

    import numpy as np
    import shapely
    from shapely.geometry import MultiPoint, MultiPolygon
    from shapely.ops import polygonize, unary_union
    tool = "RemoveOverlapMultiple"
    method = str(method or "CENTER_LINE").upper()
    if method == "GRID":
        _refuse_analysis(tool, "method", method, "the page shows its grid division only as a picture")
    if method not in ("CENTER_LINE", "THIESSEN"):
        raise ValueError(f"arcpy.analysis.{tool}: method is CENTER_LINE, THIESSEN or GRID.")
    paths = _list(in_features)
    polygons, all_columns, srid = [], [], None
    for path in paths:
        columns, kind, own_srid, _ = _schema(path)
        if kind != "Polygon":
            raise ValueError(f"arcpy.analysis.{tool} takes polygon features.")
        srid = own_srid if srid is None else srid
        for oid, values, shape in _read(path, srid):
            polygons.append((path, oid, dict(zip([c.name for c in columns], values)), shape))
        for column in columns:
            if column.name.lower() not in {c.name.lower() for c in all_columns}:
                all_columns.append(column)
    shapes = [p[3] for p in polygons]
    exclusive = [s.difference(unary_union([o for j, o in enumerate(shapes) if j != i])) if len(shapes) > 1 else s
                 for i, s in enumerate(shapes)]
    pieces = [[e] if not e.is_empty else [] for e in exclusive]
    faces = list(polygonize(unary_union([s.boundary for s in shapes])))
    for face in faces:
        point = face.representative_point()
        cover = [i for i, s in enumerate(shapes) if s.contains(point)]
        if len(cover) < 2:
            continue
        if method == "THIESSEN":
            sites = MultiPoint([shapes[i].centroid for i in cover])
            cells = shapely.voronoi_polygons(sites, extend_to=face.envelope.buffer(face.length))
            for cell in cells.geoms:
                owner = min(cover, key=lambda i: (cell.representative_point().distance(shapes[i].centroid), i))
                part = cell.intersection(face)
                if not part.is_empty:
                    pieces[owner].append(part)
        else:
            spacing = math.sqrt(face.area) / 50
            sites, owners = [], []
            for i in cover:
                generator = exclusive[i] if not exclusive[i].is_empty else shapes[i]
                for ring in getattr(generator, "geoms", [generator]):
                    for line in [ring.exterior, *ring.interiors]:
                        for coordinate in shapely.segmentize(line, spacing).coords:
                            sites.append(coordinate[:2])
                            owners.append(i)
            unique, first = np.unique(np.round(np.array(sites), 9), axis=0, return_index=True)
            owners = [owners[k] for k in first]
            cells = shapely.voronoi_polygons(MultiPoint([tuple(p) for p in unique]),
                                             extend_to=face.envelope.buffer(face.length))
            tree = shapely.STRtree([shapely.Point(p) for p in unique])
            for cell in cells.geoms:
                site = int(tree.nearest(cell.representative_point()))
                part = cell.intersection(face)
                if not part.is_empty:
                    pieces[owners[site]].append(part)
    rule = str(join_attributes or "ALL").upper()
    stems = []
    for path in paths:
        file, table = _gpkg.split(_out(path))
        stem = table or os.path.splitext(os.path.basename(file))[0]
        stems.append(f"FID_{stem}")
    fid_columns = [_gpkg.Column(name, "Integer") for name in stems] if rule != "NO_FID" else []
    value_columns = all_columns if rule != "ONLY_FID" else []
    out = _out(out_feature_class)
    _gpkg.create(out, "Polygon", srid, [*fid_columns, *value_columns])
    with da.InsertCursor(out, [*[c.name for c in fid_columns], *[c.name for c in value_columns], "SHAPE@"]) as written:
        for (path, oid, values, _), parts in zip(polygons, pieces):
            if not parts:
                continue
            merged = unary_union(parts)
            polygonal = [g for g in getattr(merged, "geoms", [merged]) if g.geom_type == "Polygon" and g.area > 0]
            if not polygonal:
                continue
            fids = [oid if paths[k] == path else -1 for k in range(len(paths))] if fid_columns else []
            written.insertRow((*fids, *[values.get(c.name) for c in value_columns], MultiPolygon(polygonal)))
    return Result(out)


# ---- Summarize Within: the server's own summary, by the names Q-193 decided ----------------------------------------

# The server's units and sizes, international throughout (_units.SERVER_LENGTH/SERVER_AREA); Pro's own SummarizeWithin
# reads FEET, YARDS and MILES as US survey units, which D-323 records for the server rather than grapy deciding it.
_SUM_LENGTH_UNITS = {key: (name, _units.SERVER_LENGTH[key]) for key, name in
                     [("METERS", "Meters"), ("KILOMETERS", "Kilometers"), ("FEET", "Feet"), ("YARDS", "Yards"),
                      ("MILES", "Miles")]}
_SUM_AREA_UNITS = {key: (name, _units.SERVER_AREA[key]) for key, name in
                   [("SQUAREMETERS", "SquareMeters"), ("SQUAREKILOMETERS", "SquareKilometers"),
                    ("HECTARES", "Hectares"), ("ACRES", "Acres"), ("SQUAREMILES", "SquareMiles"),
                    ("SQUAREYARDS", "SquareYards"), ("SQUAREFEET", "SquareFeet")]}
_SUM_STATISTICS = {"SUM": "Sum", "MEAN": "Mean", "MIN": "Min", "MAX": "Max", "STDDEV": "Stddev",
                   "MINIMUM": "Min", "MAXIMUM": "Max", "STANDARD_DEVIATION": "Stddev"}


def _column_name_for(name):
    """The server's ColumnNameFor: lower case, every character but an ASCII letter or digit made ``_``."""
    safe = "".join(c if c.isascii() and c.isalnum() else "_" for c in name.lower())
    if len(safe) > 60:
        raise ValueError(f"arcpy.analysis.SummarizeWithin: the field name '{safe}' is longer than 60 characters, which "
                         "the server shortens by a digest grapy does not reproduce.")
    return safe


def _unclaimed(name, taken):
    """The server's Unclaimed: the name, or with _1, _2 … after it, as ArcGIS renames a clash."""
    candidate, i = name, 1
    while candidate.lower() in taken or candidate.lower() in ("objectid", "geom"):
        candidate = _column_name_for(f"{name}_{i}")
        i += 1
    taken.add(candidate.lower())
    return candidate


def _on_the_ground(srid):
    """A function measuring a shapely geometry's length or area on the reference's ellipsoid, in metres, as the server
    measures ``ST_Length``/``ST_Area`` of the geography — or planar for a feature class with no reference."""
    if srid <= 0:
        return lambda shape, area: shape.area if area else shape.length
    import numpy as np
    from pyproj import CRS, Transformer
    crs = CRS.from_epsg(srid)
    geographic = crs.geodetic_crs
    geod = geographic.get_geod()
    move = Transformer.from_crs(crs, geographic, always_xy=True).transform if crs.is_projected else None

    def measure(shape, area):
        if shape.is_empty:
            return 0.0
        moved = shapely.transform(shape, lambda xy: np.column_stack(move(xy[:, 0], xy[:, 1]))) if move else shape
        return abs(geod.geometry_area_perimeter(moved)[0]) if area else geod.geometry_length(moved)

    return measure


def _summary_statistic(statistic, values, parts, wholes, weighted):
    """One statistic, as the server's SQL takes it: nulls left out; for points sum, mean, minimum, maximum and the sample
    standard deviation; for lines and polygons the sum of each value times its inside fraction, the mean and the
    (population) standard deviation weighted by the inside measure, and the minimum and maximum of the features that
    reach inside."""
    rows = [(v, p, w) for v, p, w in zip(values, parts, wholes) if v is not None]
    if not rows:
        return None
    if statistic == "Min":
        return min(v for v, _, _ in rows)
    if statistic == "Max":
        return max(v for v, _, _ in rows)
    if not weighted:
        numbers = [float(v) for v, _, _ in rows]
        if statistic == "Sum":
            return sum(numbers)
        if statistic == "Mean":
            return sum(numbers) / len(numbers)
        if len(numbers) < 2:
            return None
        mean = sum(numbers) / len(numbers)
        return math.sqrt(sum((x - mean) ** 2 for x in numbers) / (len(numbers) - 1))
    if statistic == "Sum":
        return sum(float(v) * (p / w) for v, p, w in rows if w > 0)
    total = sum(p for _, p, _ in rows)
    if total == 0:
        return None
    mean = sum(float(v) * p for v, p, _ in rows) / total
    if statistic == "Mean":
        return mean
    return math.sqrt(max(sum(p * float(v) ** 2 for v, p, _ in rows) / total - mean ** 2, 0.0))


def SummarizeWithin(in_polygons, in_sum_features, out_feature_class, keep_all_polygons="KEEP_ALL", sum_fields=None,
                    sum_shape="ADD_SHAPE_SUM", shape_unit=None, group_field=None, add_min_maj="NO_MIN_MAJ",
                    add_group_percent="NO_PERCENT", out_group_table=None):
    """The summary features in each polygon, by the documented parameters and Graticula's own Summarize Within (ADR-222):
    the same names and the same arithmetic, so a toolbox and the server's analysis answer alike.

    Field names — decided by the owner in Q-193 (ADR-222 §5 item 9), lower case as every hosted column: ``point_count``
    for points and ``count`` for lines and polygons; ``sum_length_<unit>`` and ``sum_area_<unit>`` (``meters``,
    ``squarekilometers`` …); ``<statistic>_<field>`` (``sum_``, ``mean_``, ``min_``, ``max_``, ``stddev_``); with a group
    field ``join_id`` (the polygon's object id) and, with ADD_MIN_MAJ, ``majority_<field>`` and ``minority_<field>``; in
    the group table ``join_id``, the group field, the count, the measure and the statistics again, and with ADD_PERCENT
    ``percent_points`` or ``percent_shape``. A name the polygons already hold takes ``_1``, ``_2`` … after it.

    As documented, lines and polygons are summarized in proportion to their part inside each polygon: their count is of
    the features that reach inside with a length or area, the measure is the inside length or area summed, a sum takes
    each value times its inside fraction, and a mean and standard deviation are weighted by the inside measure; a
    minimum and maximum are of the features that reach inside. Points are counted where they meet a polygon, its
    boundary included, and unweighted, the standard deviation the sample one. Nulls are left out of every statistic, as
    documented. Lengths and areas are measured on the reference's ellipsoid, as the server measures the geography — on
    WGS 84 for Web Mercator. The majority is the group value with the largest count (points) or measure, the minority the
    smallest, a tie going to the lower value; a percentage is each value's share of that weight in its polygon.

    The shape unit defaults to METERS for lines and SQUAREMETERS for polygons, as the server's does. Rows are written in
    the polygons' object id order, the group table by polygon and then group value.
    """
    from shapely.strtree import STRtree
    tool = "SummarizeWithin"
    area_columns, area_kind, area_srid, _ = _schema(in_polygons)
    if area_kind != "Polygon":
        raise ValueError(f"arcpy.analysis.{tool}: in_polygons must be polygons.")
    sum_columns, sum_kind, sum_srid, _ = _schema(in_sum_features)
    if sum_kind == "Geometry":
        raise ValueError(f"arcpy.analysis.{tool}: in_sum_features must be points, lines or polygons.")
    if sum_srid != area_srid:
        raise ValueError(f"arcpy.analysis.{tool}: the two inputs must share a spatial reference; project one first.")
    shape = "points" if sum_kind in ("Point", "Multipoint") else "lines" if sum_kind == "Polyline" else "polygons"
    weighted = shape != "points"
    by_name = {c.name.lower(): c for c in sum_columns}

    def word(value):
        return str(value).strip().upper().replace(" ", "_")

    add_shape = sum_shape is True or (sum_shape is not False and word(sum_shape or "ADD_SHAPE_SUM") in
                                      ("ADD_SHAPE_SUM", "TRUE"))
    entries = []
    if sum_fields not in (None, "", "#"):
        rows = [part.split() for part in sum_fields.split(";") if part.strip()] if isinstance(sum_fields, str) \
            else [list(r) for r in sum_fields]
        for row in rows:
            if len(row) != 2 or str(row[0]).lower() not in by_name or word(row[1]) not in _SUM_STATISTICS:
                raise ValueError(f"arcpy.analysis.{tool}: '{row}' is not a summary: write a field and one of Sum, Mean, "
                                 "Min, Max or Stddev.")
            entries.append((by_name[str(row[0]).lower()].name, _SUM_STATISTICS[word(row[1])]))
    if not add_shape and not entries:
        raise ValueError(f"arcpy.analysis.{tool}: with NO_SHAPE_SUM, sum_fields is needed: there would be nothing to "
                         "summarise.")
    taken = {c.name.lower() for c in area_columns}
    count_name = measure_name = None
    per_unit = 1.0
    if add_shape:
        count_name = _unclaimed("point_count" if shape == "points" else "count", taken)
        if shape != "points":
            units = _SUM_LENGTH_UNITS if shape == "lines" else _SUM_AREA_UNITS
            asked = word(shape_unit) if shape_unit not in (None, "", "#") else ("METERS" if shape == "lines"
                                                                                else "SQUAREMETERS")
            if asked not in units:
                raise ValueError(f"arcpy.analysis.{tool}: shape_unit for {shape} is one of {', '.join(units)}.")
            unit, per_unit = units[asked]
            measure_name = _unclaimed(_column_name_for(f"sum_{'length' if shape == 'lines' else 'area'}_{unit}"), taken)
    outputs = [(field, statistic, _unclaimed(_column_name_for(f"{statistic}_{field}"), taken))
               for field, statistic in entries]
    group = None
    if group_field not in (None, "", "#"):
        if str(group_field).lower() not in by_name:
            raise ValueError(f"arcpy.analysis.{tool}: group_field is a field of the summary features; there is no "
                             f"'{group_field}'.")
        if out_group_table in (None, "", "#"):
            raise ValueError(f"arcpy.analysis.{tool}: out_group_table is required with a group_field.")
        group = by_name[str(group_field).lower()].name
        majority = add_min_maj is True or word(add_min_maj or "NO_MIN_MAJ") in ("ADD_MIN_MAJ", "TRUE")
        join_name = "join_id"
        majority_name = _unclaimed(_column_name_for(f"majority_{group}"), taken) if majority else None
        minority_name = _unclaimed(_column_name_for(f"minority_{group}"), taken) if majority else None
        percent = add_group_percent is True or word(add_group_percent or "NO_PERCENT") in ("ADD_PERCENT", "TRUE")
        percent_name = ("percent_points" if shape == "points" else "percent_shape") if percent else None
    keep_all = keep_all_polygons is not False and word(keep_all_polygons or "KEEP_ALL") not in ("ONLY_INTERSECTING",
                                                                                                  "FALSE")
    measure = _on_the_ground(area_srid)
    wanted = sorted({f for f, _ in entries} | ({group} if group else set()))
    with da.SearchCursor(in_sum_features, [*wanted, "SHAPE@"]) as rows:
        summarized = [(dict(zip(wanted, row[:-1])), row[-1]._shape) for row in rows
                      if row[-1] is not None and not row[-1]._shape.is_empty]
    wholes = [measure(s, shape == "polygons") if weighted else 1.0 for _, s in summarized]
    tree = STRtree([s for _, s in summarized]) if summarized else None
    carried = [c.name for c in area_columns]
    with da.SearchCursor(in_polygons, ["OID@", *carried, "SHAPE@"]) as rows:
        areas = [(row[0], row[1:-1], row[-1]) for row in rows if row[-1] is not None]
    written, group_rows = [], []
    for oid, values, geometry in areas:
        polygon = geometry._shape
        members = []
        if tree is not None and not polygon.is_empty:
            for j in sorted(int(j) for j in tree.query(polygon, predicate="intersects")):
                part = measure(polygon.intersection(summarized[j][1]), shape == "polygons") if weighted else 1.0
                if weighted and part <= 0:
                    continue
                members.append((j, part))
        if not members and not keep_all:
            continue

        def summary(chosen):
            row = []
            if count_name:
                row.append(len(chosen))
            if measure_name:
                row.append(sum(p for _, p in chosen) / per_unit)
            for field, statistic, _ in outputs:
                row.append(_summary_statistic(statistic, [summarized[j][0][field] for j, _ in chosen],
                                              [p for _, p in chosen], [wholes[j] for j, _ in chosen], weighted))
            return row

        row = [*values, *summary(members)]
        if group:
            by_value: dict = {}
            for j, part in members:
                by_value.setdefault(summarized[j][0][group], []).append((j, part))
            order = sorted(by_value, key=lambda v: (v is None, v))
            weights = {v: (len(by_value[v]) if shape == "points" else sum(p for _, p in by_value[v])) for v in order}
            row.append(oid)
            if majority_name:
                row.append(min(order, key=lambda v: (-weights[v], v is None, v)) if order else None)
                row.append(min(order, key=lambda v: (weights[v], v is None, v)) if order else None)
            total = sum(weights.values())
            for v in order:
                chosen = by_value[v]
                group_row = [oid, v, len(chosen)] if count_name else [oid, v]
                if measure_name:
                    group_row.append(sum(p for _, p in chosen) / per_unit)
                group_row += summary(chosen)[(1 if count_name else 0) + (1 if measure_name else 0):]
                if percent_name:
                    group_row.append(100.0 * weights[v] / total if total else None)
                group_rows.append(group_row)
        written.append((row, geometry))

    def statistic_type(field, statistic):
        source = by_name[field.lower()].type
        return source if statistic in ("Min", "Max") or (not weighted and statistic == "Sum" and
                                                         source in ("SmallInteger", "Integer")) else "Double"

    new_columns = []
    if count_name:
        new_columns.append(_gpkg.Column(count_name, "Integer"))
    if measure_name:
        new_columns.append(_gpkg.Column(measure_name, "Double"))
    new_columns += [_gpkg.Column(name, statistic_type(field, statistic)) for field, statistic, name in outputs]
    if group:
        new_columns.append(_gpkg.Column(join_name, "Integer"))
        if majority_name:
            source = by_name[group.lower()].type
            new_columns += [_gpkg.Column(majority_name, source), _gpkg.Column(minority_name, source)]
    out = _out(out_feature_class)
    _gpkg.create(out, "Polygon", area_srid, [*area_columns, *new_columns])
    with da.InsertCursor(out, [*carried, *[c.name for c in new_columns], "SHAPE@"]) as cursor:
        for row, geometry in written:
            cursor.insertRow((*row, geometry))
    if not group:
        return Result(out)
    group_columns = [_gpkg.Column(join_name, "Integer"), by_name[group.lower()]]
    if count_name:
        group_columns.append(_gpkg.Column(count_name, "Integer"))
    if measure_name:
        group_columns.append(_gpkg.Column(measure_name, "Double"))
    group_columns += [_gpkg.Column(name, statistic_type(field, statistic)) for field, statistic, name in outputs]
    if percent_name:
        group_columns.append(_gpkg.Column(percent_name, "Double"))
    table = _out(out_group_table)
    _gpkg.create(table, "Geometry", 0, group_columns)
    with da.InsertCursor(table, [*[c.name for c in group_columns], "SHAPE@"]) as cursor:
        for group_row in group_rows:
            cursor.insertRow((*group_row, None))
    return Result(out, table)


def _refuse_analysis(tool, parameter, value, why):
    raise NotBuiltError(f"arcpy.analysis.{tool}: {parameter} '{value}' is not built in grapy — {why}.")


_REFUSED = {
    "Enrich": "it answers from Esri's demographic data, which this server does not hold (ADR-224 group 3)",
    "EnrichLayer": "it answers from Esri's demographic data, which this server does not hold (ADR-224 group 3)",
    "SummarizeNearby": "its driving, trucking and walking distances need Esri's network services (ADR-224 groups 2 and "
                       "3), and for straight-line distance the documentation does not name the fields it writes",
    "PairwiseIntegrate": "the documentation does not say which coordinate a cluster of vertices within the tolerance "
                         "takes",
    "GeographicallyWeightedRegression": "its page is no longer in Esri's documentation (404 on 2026-10-29); the "
                                        "documented regression is arcpy.stats.GWR",
}


def __getattr__(name):
    if name.startswith("_"):
        raise AttributeError(name)
    why = _REFUSED.get(name)

    def missing(*_, **__):
        raise NotBuiltError(f"arcpy.analysis.{name} is not built in grapy" + (f": {why}." if why else " yet."))

    return missing
