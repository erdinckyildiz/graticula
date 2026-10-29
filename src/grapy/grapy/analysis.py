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


def __getattr__(name):
    if name.startswith("_"):
        raise AttributeError(name)

    def missing(*_, **__):
        raise NotBuiltError(f"arcpy.analysis.{name} is not built in grapy yet.")

    return missing
