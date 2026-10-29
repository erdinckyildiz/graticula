"""arcpy.analysis's tools grapy builds, by their documented names and parameters — ADR-223 §5.2.

Each reads and writes feature classes through the cursors, so a tool's output is written exactly as a toolbox's own
InsertCursor would write it. Distances are in the data's units, as arcpy's are for a projected reference; a geographic
reference is buffered in metres on the ground, as arcpy's planar tools are not — said in the name of the parameter
that takes a linear unit (``"100 Meters"``).
"""
from __future__ import annotations

from . import _gpkg, da
from ._result import Result
from ._runtime import NotBuiltError

_METRES = {"METERS": 1, "KILOMETERS": 1000, "FEET": 0.3048, "MILES": 1609.344, "YARDS": 0.9144, "NAUTICALMILES": 1852,
           "DECIMETERS": 0.1, "CENTIMETERS": 0.01, "MILLIMETERS": 0.001, "INCHES": 0.0254}


def _schema(path):
    connection, table = _gpkg.table_of(path)
    try:
        return _gpkg.describe(connection, table)
    finally:
        connection.close()


def _distance(value, srid):
    """A linear unit such as ``"100 Meters"`` in the data's units, or a number as it is."""
    if isinstance(value, (int, float)):
        return float(value)
    parts = str(value).split()
    number = float(parts[0])
    if len(parts) == 1:
        return number
    metres = number * _METRES.get(parts[1].upper().rstrip("S") + "S", _METRES.get(parts[1].upper(), 1))
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
    ``NEAR_X``, ``NEAR_Y`` and ``NEAR_ANGLE`` when asked."""
    import math
    from shapely.ops import nearest_points
    from shapely.strtree import STRtree
    _, _, srid, _ = _schema(in_features)
    radius = _distance(search_radius, srid) if search_radius not in (None, "", "#") else 0.0
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
                    values.update(NEAR_FID=nearest[1], NEAR_DIST=gap, NEAR_X=there.x, NEAR_Y=there.y, NEAR_FC=str(nearest[0]),
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

# Units a tool writes its measures in. Only units whose size is unambiguous are here: the US survey variants arcpy
# also lists (FEET beside FEET_INTERNATIONAL, MILES beside MILES_INTERNATIONAL …) are refused rather than guessed.
_LINEAR_OUT = {"METERS": 1, "KILOMETERS": 1000, "DECIMETERS": 0.1, "CENTIMETERS": 0.01, "MILLIMETERS": 0.001,
               "MILES_INTERNATIONAL": 1609.344, "NAUTICAL_MILES_INTERNATIONAL": 1852, "YARDS_INTERNATIONAL": 0.9144,
               "FEET_INTERNATIONAL": 0.3048, "INCHES_INTERNATIONAL": 0.0254,
               # GenerateNearTable's spelling of the same units
               "MILESINT": 1609.344, "NAUTICALMILESINT": 1852, "YARDSINT": 0.9144, "FEETINT": 0.3048}
_AREA_OUT = {"SQUARE_METERS": 1, "SQUARE_KILOMETERS": 1e6, "SQUARE_DECIMETERS": 0.01, "SQUARE_CENTIMETERS": 1e-4,
             "SQUARE_MILLIMETERS": 1e-6, "SQUARE_MILES": 2589988.110336, "SQUARE_NAUTICAL_MILES": 3429904,
             "SQUARE_YARDS": 0.83612736, "SQUARE_FEET": 0.09290304, "SQUARE_INCHES": 0.00064516, "HECTARES": 1e4,
             "ACRES": 4046.8564224, "ARES": 100}


def _refuse(tool, parameter, value, why=""):
    raise NotBuiltError(f"arcpy.analysis.{tool}: {parameter} '{value}' is not built in grapy{': ' + why if why else ''}.")


def _unit_factor(unit, table, srid, tool, parameter, power=1):
    """What a measure in the data's units is multiplied by to be in ``unit``; 1 when no unit is named."""
    if unit in (None, "", "#") or str(unit).upper() == "UNKNOWN":
        return 1.0
    name = str(unit).upper()
    if name not in table:
        _refuse(tool, parameter, unit, "its size is not one grapy can be sure of")
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
    factor = _unit_factor(distance_unit, _LINEAR_OUT, srid, "GenerateNearTable", "distance_unit")
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

    INFERRED: the six distance units are the international ones; ``search_distance`` is inclusive; nearest links are
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
    units = {"KILOMETERS": 1000, "METERS": 1, "MILES": 1609.344, "NAUTICALMILES": 1852, "YARDS": 0.9144, "FEET": 0.3048}
    factor = _unit_factor(distance_unit, units, srid, "GenerateOriginDestinationLinks", "distance_unit")
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
    linear = _unit_factor(out_linear_units, _LINEAR_OUT, srid, tool, "out_linear_units")
    areal = _unit_factor(out_area_units, _AREA_OUT, srid, tool, "out_area_units", 2) if overlap else 1.0
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
    factor = _unit_factor(out_units, _AREA_OUT, srid, "TabulateIntersection", "out_units", 2)
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


_REFUSED = {
    "Enrich": "it answers from Esri's demographic data, which this server does not hold (ADR-224 group 3)",
    "EnrichLayer": "it answers from Esri's demographic data, which this server does not hold (ADR-224 group 3)",
    "SummarizeWithin": "the documentation does not name the fields it writes (the count, length, area and statistic "
                       "fields), and a toolbox reads them by name; Graticula's own Summarize Within runs as a hosted "
                       "analysis (ADR-222)",
    "SummarizeNearby": "its driving, trucking and walking distances need Esri's network services (ADR-224 groups 2 and "
                       "3), and for straight-line distance the documentation does not name the fields it writes",
    "RemoveOverlapMultiple": "its CENTER_LINE, THIESSEN and GRID divisions are described in words only, not specified",
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
