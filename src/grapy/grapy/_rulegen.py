"""The template-rule generators of arcpy.management — ADR-235 §5.4.

Each tool writes a .csv file holding one attribute rule, in ExportAttributeRules' twenty columns, for
ImportAttributeRules to add. The page of each tool says what the rule does; the Arcade script Esri's tool writes is its
own and is not published, so each is an **equivalent** (ADR-224 §5.6): grapy writes a script of its own, from the
public Arcade functions grapy's interpreter builds, that does what the page says the rule does.

INFERRED (ADR-235 §5.8): the file is ``<out_folder>/<table>/<table>.csv``, or ``<table>_1.csv``, ``_2`` … when that
is taken, as the pages say each run adds another file to a folder named after the input; the rule's name is the
tool's subject (``Calculate geometry fields``, ``Generate ID <field>`` …); a distance given with a unit is converted
to the input's spatial reference units when the rule is written, and refused on a geographic reference.
"""
from __future__ import annotations

import csv
import json
import os

from . import _gpkg, _runtime
from ._result import Result
from ._runtime import ExecuteError, NotBuiltError
from ._ruletools import _EXPORT, _flag, _list, _table, _text


def _literal(value):
    """An Arcade literal of a Python value."""
    if value is None:
        return "null"
    if isinstance(value, bool):
        return "true" if value else "false"
    if isinstance(value, (int, float)):
        return repr(float(value)) if not float(value).is_integer() else str(int(value))
    return json.dumps(str(value), ensure_ascii=False)


def _file(out_folder, table):
    folder = os.path.join(_text(out_folder), table.replace("/", "_"))
    os.makedirs(folder, exist_ok=True)
    base = table.replace("/", "_")
    path, n = os.path.join(folder, f"{base}.csv"), 0
    while os.path.exists(path):
        n += 1
        path = os.path.join(folder, f"{base}_{n}.csv")
    return path


def _write(out_folder, table, rule):
    """The rule written as ExportAttributeRules writes one."""
    path = _file(out_folder, table)
    events = rule.get("events", [])
    row = {"NAME": rule["name"], "DESCRIPTION": rule.get("description", ""), "TYPE": "CALCULATION", "SUBTYPE": "",
           "FIELD": rule.get("field") or "", "ISEDITABLE": _flag(True), "TRIGGERINSERT": _flag("INSERT" in events),
           "TRIGGERDELETE": _flag("DELETE" in events), "TRIGGERUPDATE": _flag("UPDATE" in events),
           "SCRIPTEXPRESSION": rule["script"], "ERRORNUMBER": "", "ERRORMESSAGE": "",
           "EXCLUDECLIENTEVALUATION": _flag(rule.get("exclude", False)), "ISENABLED": _flag(True),
           "BATCH": _flag(False), "SEVERITY": "", "TAGS": "", "CATEGORY": "", "CHECKPARAMETERS": "",
           "TRIGGERINGFIELDS": json.dumps(rule.get("triggering", []))}
    with open(path, "w", encoding="utf-8", newline="") as written:
        out = csv.writer(written)
        out.writerow(_EXPORT)
        out.writerow([row[c] for c in _EXPORT])
    return path


def _describe(tool, in_table):
    """(connection, table, columns {lower: Column}, shape, srid, geometry column, metres per unit, geographic)."""
    from ._reference import SpatialReference
    connection, table = _table(tool, in_table)
    columns, shape, srid, geometry = _gpkg.describe(connection, table)
    reference = SpatialReference(srid) if srid > 0 else None
    geographic = reference is not None and reference.type == "Geographic"
    per_unit = reference.metersPerUnit if reference is not None and not geographic else 1.0
    return connection, table, {c.name.lower(): c for c in columns}, shape, srid, geometry, per_unit, geographic


def _distance(tool, value, per_unit, geographic):
    """A linear unit value ("20 Feet") in the input's units; a bare number is in them already."""
    from . import _units
    if value in (None, "", "#"):
        return None
    if isinstance(value, (int, float)):
        return float(value)
    parts = str(value).split()
    number = float(parts[0])
    if len(parts) == 1 or parts[1].lower() == "unknown":
        return number
    metres = _units.linear(parts[1].replace("_", ""))
    if metres is None:
        raise ExecuteError(f"ERROR 000800: {tool}: '{value}' is not a linear distance.")
    if geographic:
        raise ExecuteError(f"ERROR 000800: {tool}: a distance in {parts[1]} needs a projected spatial reference; this "
                           "one is geographic.")
    return number * metres / per_unit


def _rows(value):
    """A value table as a list of lists; '#' is an empty value."""
    if value in (None, ""):
        return []
    rows = value if isinstance(value, (list, tuple)) else [r.split() for r in _list(value)]
    out = []
    for row in rows:
        row = list(row) if isinstance(row, (list, tuple)) else str(row).split()
        out.append([None if (v in ("#", "") or v is None) else v for v in row])
    return out


def _where(where_clause, otherwise):
    """A guard that returns ``otherwise`` for a feature the where clause leaves out."""
    if where_clause in (None, "", "#"):
        return ""
    return f"if (!FeatureInFilter($feature, {_literal(where_clause)})) {{ return {otherwise} }}\n"


def _class_name(tool, connection, file, value):
    """The table a class parameter names, which must be in the input's GeoPackage: $datastore reads only that."""
    from . import _layers
    layer = _layers.find(_text(value))
    path = layer.source if layer is not None else _text(value)
    if "/" not in path.replace("\\", "/") and ".gpkg" not in path.lower():
        found = connection.execute("select table_name from gpkg_contents where lower(table_name) = lower(?)",
                                   (path,)).fetchone()
        if found:
            return found[0]
    other_file, table = _gpkg.split(path)
    if os.path.normcase(os.path.abspath(other_file)) != os.path.normcase(os.path.abspath(file)):
        raise ExecuteError(f"ERROR 000800: {tool}: '{value}' is not in the input's GeoPackage, and a rule reads other "
                           "classes through $datastore, which is that GeoPackage.")
    return table


def _file_of(connection):
    return connection.execute("pragma database_list").fetchone()[2]


# ---- CalculateGeometryFieldsAttributeRule ---------------------------------------------------------------------------------

_POINT_COUNT = """function grapyPointCount(g) {
  var t = TypeOf(g);
  if (t == 'Point') { return 1 }
  if (t == 'Multipoint') { return Count(g.points) }
  var parts = null;
  if (t == 'Polygon') { parts = g.rings } else { parts = g.paths }
  var n = 0;
  for (var i in parts) { n += Count(parts[i]) }
  return n
}
function grapyHoleCount(g) {
  if (TypeOf(g) != 'Polygon') { return 0 }
  var n = 0;
  for (var i in g.rings) { if (!RingIsClockwise(g.rings[i])) { n++ } }
  return n
}
function grapyLineStart(g) { return g.paths[0][0] }
function grapyLineEnd(g) { var p = Back(g.paths); return Back(p) }
"""

_REFUSED_PROPERTIES = {
    "CENTROID_Z": "grapy's centroid is two-dimensional", "CENTROID_M": "grapy's centroid carries no m-value",
    "INSIDE_Z": "grapy's inside point is two-dimensional", "INSIDE_M": "grapy's inside point carries no m-value",
    "EXTENT_MIN_Z": "an Arcade extent in grapy is two-dimensional", "EXTENT_MAX_Z": "an Arcade extent in grapy is "
                                                                                   "two-dimensional",
    "LINE_START_Z": "a path's points are read in two dimensions", "LINE_START_M": "a path's points carry no m-value",
    "LINE_END_Z": "a path's points are read in two dimensions", "LINE_END_M": "a path's points carry no m-value",
    "POINT_COORD_NOTATION": "the page names its coordinate formats but its descriptions do not match its keywords, so "
                            "the text each writes is not defined",
}


@_runtime.equivalent("grapy's own Arcade script computing each geometry property with the Arcade geometry functions",
                     "doc.esri.com …/data-management/calculate-geometry-fields-attribute-rule")
def CalculateGeometryFieldsAttributeRule(in_table, out_folder, geometry_attributes, where_clause=None, length_unit=None,
                                         area_unit=None, coordinate_format=None):
    """EQUIVALENT (ADR-224 §5.6): a calculation rule that keeps fields set to properties of the feature's geometry,
    triggered by edits to the shape and the target fields, as the page says. Area, length and perimeter (planar,
    geodesic and 3D), centroid, inside point, extent, line start, end, bearing and angle, point x, y, z and m, and part,
    point, hole and curve counts (curves: 0, grapy's geometries being linear). Lengths and areas are in the input's
    units unless ``length_unit`` or ``area_unit`` say otherwise, as documented. Refused by name: the z and m of
    centroids, inside points, extents and line ends, and POINT_COORD_NOTATION and ``coordinate_format``, whose formats
    the page does not define. INFERRED (ADR-235 §5.8): LINE_BEARING and LINE_ANGLE are from the line's first point to
    its last; an empty geometry leaves the fields as they are."""
    from . import _units
    tool = "CalculateGeometryFieldsAttributeRule"
    if coordinate_format not in (None, "", "#", "SAME_AS_INPUT"):
        raise NotBuiltError(f"arcpy.management.{tool}: coordinate_format '{coordinate_format}' is refused in grapy: "
                            "the page's descriptions of its formats do not match its keywords, so the text each writes "
                            "is not defined.")
    connection, table, columns, shape, srid, geometry, per_unit, geographic = _describe(tool, in_table)
    connection.close()
    pairs = _rows(geometry_attributes)
    if not pairs:
        raise ExecuteError(f"ERROR 000735: {tool}: name at least one field and geometry property.")
    length_factor = area_factor = None
    if length_unit not in (None, "", "#"):
        metres = _units.linear(str(length_unit).replace("_", ""))
        if metres is None:
            raise ExecuteError(f"ERROR 000800: {tool}: '{length_unit}' is not a length unit.")
        length_factor = metres
    if area_unit not in (None, "", "#"):
        square = _units.areal(str(area_unit))
        if square is None:
            raise ExecuteError(f"ERROR 000800: {tool}: '{area_unit}' is not an area unit.")
        area_factor = square
    for planar in ("AREA", "LENGTH", "LENGTH_3D", "PERIMETER_LENGTH"):
        if geographic and any(p[1] and str(p[1]).upper() == planar for p in pairs) and (length_factor or area_factor):
            raise ExecuteError(f"ERROR 000800: {tool}: a planar {planar} in a unit needs a projected spatial reference.")

    def planar(expression, area):
        factor = area_factor if area else length_factor
        if factor is None:
            return expression
        return f"{expression} * {_literal((per_unit ** (2 if area else 1)) / factor)}"

    def geodesic(expression, area):
        factor = area_factor if area else length_factor
        return expression if factor is None else f"{expression} / {_literal(factor)}"

    expressions = {
        "AREA": planar("Area(g)", True), "AREA_GEODESIC": geodesic("AreaGeodetic(g)", True),
        "LENGTH": planar("Length(g)", False), "LENGTH_GEODESIC": geodesic("LengthGeodetic(g)", False),
        "LENGTH_3D": planar("Length3D(g)", False), "PERIMETER_LENGTH": planar("Length(g)", False),
        "PERIMETER_LENGTH_GEODESIC": geodesic("LengthGeodetic(g)", False),
        "CENTROID_X": "Centroid(g).x", "CENTROID_Y": "Centroid(g).y",
        "INSIDE_X": "Centroid(g, 'labelPoint').x", "INSIDE_Y": "Centroid(g, 'labelPoint').y",
        "PART_COUNT": "Count(MultiPartToSinglePart(g))", "POINT_COUNT": "grapyPointCount(g)",
        "HOLE_COUNT": "grapyHoleCount(g)", "CURVE_COUNT": "0",
        "EXTENT_MIN_X": "Extent(g).xmin", "EXTENT_MIN_Y": "Extent(g).ymin", "EXTENT_MAX_X": "Extent(g).xmax",
        "EXTENT_MAX_Y": "Extent(g).ymax",
        "LINE_START_X": "grapyLineStart(g).x", "LINE_START_Y": "grapyLineStart(g).y",
        "LINE_END_X": "grapyLineEnd(g).x", "LINE_END_Y": "grapyLineEnd(g).y",
        "LINE_BEARING": "Bearing(grapyLineStart(g), grapyLineEnd(g))",
        "LINE_ANGLE": "Angle(grapyLineStart(g), grapyLineEnd(g))",
        "POINT_X": "g.x", "POINT_Y": "g.y", "POINT_Z": "g.z", "POINT_M": "g.m",
    }
    attributes, targets = [], []
    for row in pairs:
        if len(row) < 2 or row[0] is None or row[1] is None:
            raise ExecuteError(f"ERROR 000800: {tool}: each geometry attribute is a field and a property.")
        field, prop = str(row[0]), str(row[1]).upper()
        if field.lower() not in columns:
            raise ExecuteError(f"ERROR 000728: {tool}: field '{field}' does not exist in '{table}'.")
        if prop in _REFUSED_PROPERTIES:
            raise NotBuiltError(f"arcpy.management.{tool}: the property {prop} is refused in grapy: "
                                f"{_REFUSED_PROPERTIES[prop]}.")
        if prop not in expressions:
            raise ExecuteError(f"ERROR 000800: {tool}: '{prop}' is not a geometry property.")
        name = columns[field.lower()].name
        attributes.append(f"{_literal(name)}: {expressions[prop]}")
        targets.append(name)
    script = (_POINT_COUNT + "var g = Geometry($feature);\nif (g == null) { return {} }\n" + _where(where_clause, "{}")
              + "return {result: {attributes: {" + ", ".join(attributes) + "}}}")
    path = _write(out_folder, table, {"name": "Calculate geometry fields", "script": script,
                                      "events": ["INSERT", "UPDATE"], "triggering": [geometry, *targets],
                                      "description": "Geometry properties: " + ", ".join(
                                          f"{r[0]} {str(r[1]).upper()}" for r in pairs)})
    return Result(path)


# ---- GenerateIDAttributeRule -----------------------------------------------------------------------------------------------


@_runtime.equivalent("grapy's own Arcade script drawing the configured database sequence (NextSequenceValue) and "
                     "joining prefix, padded number and suffix with the separator",
                     "doc.esri.com …/data-management/generate-id-attribute-rule")
def GenerateIDAttributeRule(in_table, out_folder, field, where_clause=None, create_seq="CREATE",
                            definition_method="BUILDER", id_builder=None, id_table=None, id_coded_value=None,
                            intersect_class_field=None):
    """EQUIVALENT (ADR-224 §5.6): a calculation rule that gives a new feature an ID from a database sequence: for the
    first configuration whose filter the feature meets, the prefix, the sequence's next value padded with zeros to
    ``Padding`` digits, and the suffix, joined by the separator, as the page describes them. With CREATE the sequences
    are made in the input's GeoPackage (starting value and increment 1 unless given, as documented). The derived
    outputs are the rule's .csv, the sequence definition table (a .csv of the configuration) and a Python script
    that creates the sequences.

    BUILDER and TABLE are built. Refused by name: CODED_VALUES and ``id_coded_value``, *Sequence by lookup*, and
    ``intersect_class_field`` and a table's IntersectClass — the page does not say what sequence, prefix or ID each
    combination or looked-up value gets. INFERRED (ADR-235 §5.8): the rule runs on insert; a feature that arrives with
    an ID keeps it; one no configuration matches is left without one; the separator stands only between parts that
    are present."""
    tool = "GenerateIDAttributeRule"
    method = str(definition_method or "BUILDER").upper()
    if method == "CODED_VALUES" or _list(id_coded_value):
        raise NotBuiltError(f"arcpy.management.{tool}: CODED_VALUES is refused in grapy: the page does not say what "
                            "sequence name, prefix or ID format each combination of coded values gets.")
    if _rows(intersect_class_field):
        raise NotBuiltError(f"arcpy.management.{tool}: intersect_class_field is refused in grapy: the page does not say "
                            "how an intersecting feature's value joins the sequence in the ID.")
    if method not in ("BUILDER", "TABLE"):
        raise ExecuteError(f"ERROR 000800: {tool}: definition_method is BUILDER, TABLE or CODED_VALUES.")
    connection, table, columns, *_ = _describe(tool, in_table)
    try:
        if str(field).lower() not in columns:
            raise ExecuteError(f"ERROR 000728: {tool}: field '{field}' does not exist in '{table}'.")
        target = columns[str(field).lower()].name
        if method == "BUILDER":
            configs = []
            for row in _rows(id_builder):
                row = (row + [None] * 10)[:10]
                if row[9] not in (None, "", "false", "False", False) or any("::lookup" in str(v) for v in row):
                    raise NotBuiltError(f"arcpy.management.{tool}: Sequence by lookup is refused in grapy: the page "
                                        "does not say how a looked-up value names its sequence.")
                configs.append(row[:9])
        else:
            configs = _id_table(tool, id_table)
        if not configs:
            raise ExecuteError(f"ERROR 000735: {tool}: give at least one ID configuration.")
        parsed = []
        for where, description, sequence, start, step, prefix, suffix, padding, separator in configs:
            if not sequence:
                raise ExecuteError(f"ERROR 000735: {tool}: each configuration names its sequence.")
            start = int(float(start)) if start not in (None, "") else 1
            step = int(float(step)) if step not in (None, "") else 1
            if start <= 0:
                raise ExecuteError(f"ERROR 000800: {tool}: a starting value is greater than 0.")
            parsed.append((where or "", description or "", str(sequence), start, step, prefix or "", suffix or "",
                           int(float(padding)) if padding not in (None, "") else 0, separator or ""))
        if str(create_seq or "CREATE").upper() in ("CREATE", "TRUE"):
            from . import _sequences
            for _, _, sequence, start, step, *_ in parsed:
                if not _sequences.exists(connection, sequence):
                    _sequences.create(connection, sequence, start, step)
            connection.commit()
        file = _file_of(connection)
    finally:
        connection.close()
    entries = ", ".join("[" + ", ".join(_literal(v) for v in (w, s, p, x, pad, sep)) + "]"
                        for w, _, s, _, _, p, x, pad, sep in parsed)
    script = (f"var current = $feature[{_literal(target)}];\n"
              "if (!IsEmpty(current)) { return current }\n" + _where(where_clause, "current") +
              f"var configurations = [{entries}];\n"
              "for (var i in configurations) {\n"
              "  var c = configurations[i];\n"
              "  if (c[0] == '' || FeatureInFilter($feature, c[0])) {\n"
              "    var n = NextSequenceValue(c[1]);\n"
              "    var digits = Text(n);\n"
              "    if (c[4] > 0) { digits = Text(n, Concatenate(Array(c[4], '0'))) }\n"
              "    var parts = [];\n"
              "    if (c[2] != '') { Push(parts, c[2]) }\n"
              "    Push(parts, digits);\n"
              "    if (c[3] != '') { Push(parts, c[3]) }\n"
              "    return Concatenate(parts, c[5])\n"
              "  }\n"
              "}\n"
              "return current")
    path = _write(out_folder, table, {"name": f"Generate ID {target}", "field": target, "script": script,
                                      "events": ["INSERT"], "description": "; ".join(p[1] for p in parsed if p[1])})
    stem = os.path.splitext(path)[0]
    with open(stem + "_ids.csv", "w", encoding="utf-8", newline="") as written:
        out = csv.writer(written)
        out.writerow(["WhereClause", "Description", "SequenceName", "StartingValue", "IncrementValue", "Prefix",
                      "Suffix", "Padding", "Separator"])
        out.writerows(parsed)
    with open(stem + "_sequences.py", "w", encoding="utf-8", newline="\n") as written:
        written.write("import arcpy\n\nworkspace = " + json.dumps(file) + "\n")
        for _, _, sequence, start, step, *_ in parsed:
            written.write(f"arcpy.management.CreateDatabaseSequence(workspace, {json.dumps(sequence)}, {start}, {step})\n")
    return Result(path, stem + "_ids.csv", stem + "_sequences.py")


def _id_table(tool, id_table):
    from . import da
    names = ["WhereClause", "Description", "SequenceName", "StartingValue", "IncrementValue", "Prefix", "Suffix",
             "Padding", "Separator"]
    from . import Describe
    have = {f.name.lower(): f.name for f in Describe(_text(id_table)).fields}
    for extra in ("intersectclass", "classidfield", "classfilter"):
        if extra in have:
            with da.SearchCursor(_text(id_table), [have[extra]]) as rows:
                if any(r[0] not in (None, "") for r in rows):
                    raise NotBuiltError(f"arcpy.management.{tool}: an id_table's IntersectClass, ClassIDField and "
                                        "ClassFilter are refused in grapy: the page does not say how an intersecting "
                                        "feature's value joins the sequence in the ID.")
    missing = [n for n in names if n.lower() not in have]
    if missing:
        raise ExecuteError(f"ERROR 000728: {tool}: id_table has no field {missing[0]}.")
    with da.SearchCursor(_text(id_table), [have[n.lower()] for n in names]) as rows:
        return [list(r) for r in rows]


# ---- GenerateSpatialJoinAttributeRule --------------------------------------------------------------------------------------

_OPERATORS = {"INTERSECTS": "Intersects(fs, gi)", "CROSSES": "Crosses(fs, gi)", "CONTAINS": "Contains(gi, fs)",
              "ENVELOPE_INTERSECTS": "EnvelopeIntersects(fs, gi)", "OVERLAPS": "Overlaps(fs, gi)",
              "TOUCHES": "Touches(fs, gi)", "WITHIN": "Within(gi, fs)"}
_SHAPES = {"GEOMETRY": "g", "START": "grapyLineStart(g)", "END": "grapyLineEnd(g)", "CENTROID": "Centroid(g)"}
_MERGES = """function grapyMerge(values, rule, delimiter) {
  var present = Filter(values, grapyPresent);
  if (rule == 'FIRST') { return IIf(Count(values) > 0, First(values), null) }
  if (rule == 'LAST') { if (Count(values) == 0) { return null } return Back(values) }
  if (Count(present) == 0) { return IIf(rule == 'COUNT', 0, null) }
  if (rule == 'JOIN') { return Concatenate(present, delimiter) }
  if (rule == 'SUM') { return Sum(present) }
  if (rule == 'MEAN') { return Mean(present) }
  if (rule == 'MIN') { return Min(present) }
  if (rule == 'MAX') { return Max(present) }
  if (rule == 'COUNT') { return Count(present) }
  if (rule == 'STDEV') { if (Count(present) < 2) { return null } return Stdev(present) }
  if (rule == 'MEDIAN') {
    var s = Sort(present); var n = Count(s);
    if (n % 2 == 1) { return s[(n - 1) / 2] }
    return (s[n / 2 - 1] + s[n / 2]) / 2
  }
  if (rule == 'MODE') {
    var best = null; var most = 0;
    for (var i in present) {
      var k = 0;
      for (var j in present) { if (present[j] == present[i]) { k++ } }
      if (k > most) { most = k; best = present[i] }
    }
    return best
  }
  return null
}
function grapyPresent(v) { return !IsEmpty(v) }
"""
_RULES = {"FIRST": "FIRST", "LAST": "LAST", "JOIN": "JOIN", "CONCATENATE": "JOIN", "SUM": "SUM", "MEAN": "MEAN",
          "MEDIAN": "MEDIAN", "MODE": "MODE", "MIN": "MIN", "MINIMUM": "MIN", "MAX": "MAX", "MAXIMUM": "MAX",
          "STDEV": "STDEV", "STANDARD DEVIATION": "STDEV", "STANDARD_DEVIATION": "STDEV", "COUNT": "COUNT"}


@_runtime.equivalent("grapy's own Arcade script querying each join class through $datastore with the documented "
                     "spatial operators and merging the field map's inputs by its merge rules",
                     "doc.esri.com …/data-management/generate-spatial-join-attribute-rule")
def GenerateSpatialJoinAttributeRule(in_table, out_folder, join_classes, field_map, where_clause=None,
                                     search_options=None):
    """EQUIVALENT (ADR-224 §5.6): a calculation rule that sets each output field of the field map from the join
    features that match the feature: per join class, the part of the feature's geometry the search options name
    (GEOMETRY, START, END, CENTROID), widened by the search distance, matched by the spatial operator (INTERSECTS,
    CROSSES, CONTAINS, ENVELOPE_INTERSECTS, OVERLAPS, TOUCHES, WITHIN); the values merged by the field map's rule
    (First, Last, Join/Concatenate, Sum, Mean, Median, Mode, Min, Max, StDev, Count). INFERRED (ADR-235 §5.8): an
    output field is a field of the input; join features are taken in order of object id; StDev is the sample one and
    null for a single value, as the page says it is not valid for one; the rule runs on insert and update; join
    classes are in the input's GeoPackage."""
    tool = "GenerateSpatialJoinAttributeRule"
    from .management import FieldMappings, _same_table
    if not isinstance(field_map, FieldMappings):
        raise NotBuiltError(f"arcpy.management.{tool}: field mappings as a string are refused in grapy: their string "
                            "form is not documented; pass an arcpy.FieldMappings.")
    connection, table, columns, shape, srid, geometry, per_unit, geographic = _describe(tool, in_table)
    try:
        file = _file_of(connection)
        classes = [(c, _class_name(tool, connection, file, c)) for c in (join_classes if isinstance(join_classes,
                                                                                                   (list, tuple))
                                                                          else _list(join_classes))]
        if not classes:
            raise ExecuteError(f"ERROR 000735: {tool}: name at least one join class.")
        options = {}
        for row in _rows(search_options):
            row = (row + [None] * 4)[:4]
            name = _class_name(tool, connection, file, row[0])
            kind = str(row[1] or "GEOMETRY").upper()
            operator = str(row[2] or "INTERSECTS").upper()
            if kind not in _SHAPES or operator not in _OPERATORS:
                raise ExecuteError(f"ERROR 000800: {tool}: '{kind}' or '{operator}' is not a search option.")
            if kind in ("START", "END") and shape != "Polyline":
                raise ExecuteError(f"ERROR 000800: {tool}: START and END are for polyline inputs.")
            options[name.lower()] = (kind, operator, _distance(tool, row[3], per_unit, geographic))
    finally:
        connection.close()
    outputs, wanted = [], {}
    for field_map in field_map.fieldMappings:
        output = field_map.outputField.name
        if output.lower() not in columns:
            raise ExecuteError(f"ERROR 000728: {tool}: the output field '{output}' is not a field of '{table}'.")
        rule = _RULES.get(str(field_map.mergeRule or "First").upper())
        if rule is None:
            raise ExecuteError(f"ERROR 000800: {tool}: '{field_map.mergeRule}' is not a merge rule.")
        inputs = []
        for index in range(field_map.inputFieldCount):
            source, name = field_map.getInputTableName(index), field_map.getInputFieldName(index)
            for given, class_name in classes:
                if _same_table(source, given) or source.replace("\\", "/").lower().endswith("/" + class_name.lower()):
                    inputs.append((class_name, name))
                    wanted.setdefault(class_name, []).append(name)
        outputs.append((columns[output.lower()].name, rule, field_map.joinDelimiter or "", inputs))
    lines = [_MERGES, _POINT_COUNT, "var g = Geometry($feature);", "if (g == null) { return {} }",
             _where(where_clause, "{}").rstrip("\n"), "var values = {};"]
    for output, *_ in outputs:
        lines.append(f"values[{_literal(output)}] = [];")
    for _, class_name in classes:
        kind, operator, distance = options.get(class_name.lower(), ("GEOMETRY", "INTERSECTS", None))
        fields = sorted(set(wanted.get(class_name, [])))
        lines.append("{")
        lines.append(f"  var gi = {_SHAPES[kind]};")
        if distance:
            lines.append(f"  gi = Buffer(gi, {_literal(distance)});")
        lines.append(f"  var fs = FeatureSetByName($datastore, {_literal(class_name)}, "
                     f"[{', '.join(_literal(f) for f in fields) or _literal('*')}], true);")
        lines.append(f"  var hits = {_OPERATORS[operator]};")
        lines.append("  for (var h in hits) {")
        for output, _, _, inputs in outputs:
            for source, name in inputs:
                if source == class_name:
                    lines.append(f"    Push(values[{_literal(output)}], h[{_literal(name)}]);")
        lines.append("  }")
        lines.append("}")
    lines.append("return {result: {attributes: {" + ", ".join(
        f"{_literal(o)}: grapyMerge(values[{_literal(o)}], {_literal(r)}, {_literal(d)})" for o, r, d, _ in outputs)
        + "}}}")
    script = "\n".join(line for line in lines if line)
    path = _write(out_folder, table, {"name": "Spatial join", "script": script, "events": ["INSERT", "UPDATE"],
                                      "description": "Joins " + ", ".join(c for _, c in classes)})
    return Result(path)


# ---- GenerateSymbolRotationAttributeRule -------------------------------------------------------------------------------------


@_runtime.equivalent("grapy's own Arcade script reading the direction of the intersecting line's segment at the point "
                     "(PointToCoordinate) as an arithmetic or geographic angle",
                     "doc.esri.com …/data-management/generate-symbol-rotation-attribute-rule")
def GenerateSymbolRotationAttributeRule(in_table, out_folder, field, line_classes, where_clause=None,
                                        rotation_options=None):
    """EQUIVALENT (ADR-224 §5.6): a calculation rule that sets a point's rotation field from the line it lies on: the
    lines of the line classes that intersect the point, the one with the lowest (MIN, the default) or highest (MAX)
    orientation field when several do, and the direction of its segment at the point — ARITHMETIC (the default) from
    east counter-clockwise, or GEOGRAPHIC from north clockwise — plus the additional rotation, as documented. A rotation
    option's point class expression limits the points it applies to; the first option a point meets is used.
    INFERRED (ADR-235 §5.8): the direction is the segment's from its start vertex to its end vertex; the result is
    taken modulo 360; a point no line meets keeps its value; the rule runs on insert and update."""
    tool = "GenerateSymbolRotationAttributeRule"
    connection, table, columns, shape, *_ = _describe(tool, in_table)
    try:
        if shape not in ("Point", "Multipoint"):
            raise ExecuteError(f"ERROR 000800: {tool}: the input is a point feature class, as documented.")
        if str(field).lower() not in columns:
            raise ExecuteError(f"ERROR 000728: {tool}: field '{field}' does not exist in '{table}'.")
        target = columns[str(field).lower()].name
        file = _file_of(connection)
        lines_given = []
        for row in _rows(line_classes):
            row = (row + [None, None])[:2]
            name = _class_name(tool, connection, file, row[0])
            if row[1] in (None, ""):
                raise ExecuteError(f"ERROR 000735: {tool}: each line class names its orientation field.")
            lines_given.append((name, str(row[1])))
        if not lines_given:
            raise ExecuteError(f"ERROR 000735: {tool}: name at least one line class.")
    finally:
        connection.close()
    options = []
    for row in _rows(rotation_options) or [[None, "ARITHMETIC", "MIN", 0]]:
        row = (row + [None] * 4)[:4]
        style = str(row[1] or "ARITHMETIC").upper()
        towards = str(row[2] or "MIN").upper()
        if style not in ("ARITHMETIC", "GEOGRAPHIC") or towards not in ("MIN", "MAX"):
            raise ExecuteError(f"ERROR 000800: {tool}: rotation style is ARITHMETIC or GEOGRAPHIC, and rotate towards "
                               "MIN or MAX.")
        options.append((row[0] or "", style, towards, float(row[3]) if row[3] not in (None, "") else 0.0))
    entries = ", ".join("[" + ", ".join(_literal(v) for v in o) + "]" for o in options)
    classes = ", ".join(f"[{_literal(c)}, {_literal(f)}]" for c, f in lines_given)
    script = (f"var current = $feature[{_literal(target)}];\n" + _where(where_clause, "current") +
              "var g = Geometry($feature);\nif (g == null) { return current }\n"
              f"var options = [{entries}];\nvar option = null;\n"
              "for (var i in options) { if (options[i][0] == '' || FeatureInFilter($feature, options[i][0])) "
              "{ option = options[i]; break } }\n"
              "if (option == null) { return current }\n"
              f"var classes = [{classes}];\nvar chosen = null; var key = null;\n"
              "for (var i in classes) {\n"
              "  var fs = FeatureSetByName($datastore, classes[i][0], [classes[i][1]], true);\n"
              "  for (var line in Intersects(fs, g)) {\n"
              "    var value = line[classes[i][1]];\n"
              "    if (chosen == null || (option[2] == 'MIN' && value < key) || (option[2] == 'MAX' && value > key)) "
              "{ chosen = line; key = value }\n"
              "  }\n"
              "}\n"
              "if (chosen == null) { return current }\n"
              "var shape = Geometry(chosen);\n"
              "var at = PointToCoordinate(shape, g);\n"
              "var path = shape.paths[at.partId];\n"
              "var a = path[at.segmentId]; var b = path[at.segmentId + 1];\n"
              "var angle = 0;\n"
              "if (option[1] == 'GEOGRAPHIC') { angle = Bearing(a, b) } else { angle = Angle(a, b) }\n"
              "return (angle + option[3]) % 360")
    path = _write(out_folder, table, {"name": f"Symbol rotation {target}", "field": target, "script": script,
                                      "events": ["INSERT", "UPDATE"],
                                      "description": "Rotation from " + ", ".join(c for c, _ in lines_given)})
    return Result(path)


# ---- GenerateUpdateRelatedAttributeRule ---------------------------------------------------------------------------------------

_UN = {"UN_CONNECTED": "connected", "UN_CONTAINER": "container", "UN_CONTENT": "content", "UN_STRUCTURE": "structure",
       "UN_ATTACHED": "attached", "UN_JUNCTION_EDGE": "junctionEdge", "UN_MIDSPAN": "midspan"}


@_runtime.equivalent("grapy's own Arcade script finding the target rows (relationship class, matching keys, "
                     "intersection or utility network association) and returning the documented edit dictionary",
                     "doc.esri.com …/data-management/generate-update-related-attribute-rule")
def GenerateUpdateRelatedAttributeRule(in_table, out_folder, search_options, where_clause=None):
    """EQUIVALENT (ADR-224 §5.6): a calculation rule that, when a feature is edited, updates the target field of the
    rows each search option finds — RELATIONSHIP_CLASS (rows related through a relationship class the two take part
    in), MATCH_FIELDS (target rows whose match field equals the feature's), SPATIAL_ONLY (target features meeting the
    feature within the search distance; not for tables, as documented), or a utility network association (UN_CONNECTED
    … UN_MIDSPAN) — with the input field's value (or the value itself when it names no field): SET_VALUE replaces it,
    CONCATENATE appends it after a hyphen, BITWISE_OR ors it into the target's number. The rule returns the attribute
    rule dictionary's ``edit`` and is excluded from client evaluation, as that keyword requires.
    INFERRED (ADR-235 §5.8): the rule runs on insert and update; CONCATENATE onto an empty target is the value alone;
    targets are updated by object id, and by global ID for associations; target classes are in the input's
    GeoPackage."""
    tool = "GenerateUpdateRelatedAttributeRule"
    from . import _relationships
    connection, table, columns, shape, srid, geometry, per_unit, geographic = _describe(tool, in_table)
    try:
        file = _file_of(connection)
        searches = []
        for row in _rows(search_options):
            row = (row + [None] * 8)[:8]
            target, target_field, kind, given, operator, distance, target_match, input_match = row
            target = _class_name(tool, connection, file, target)
            kind = str(kind or "").upper().replace(" ", "_")
            operator = str(operator or "SET_VALUE").upper().replace(" ", "_")
            if kind not in ("RELATIONSHIP_CLASS", "MATCH_FIELDS", "SPATIAL_ONLY", *_UN):
                raise ExecuteError(f"ERROR 000800: {tool}: '{kind}' is not a search type.")
            if operator not in ("SET_VALUE", "CONCATENATE", "BITWISE_OR"):
                raise ExecuteError(f"ERROR 000800: {tool}: '{operator}' is not an operator.")
            target_columns = {c.name.lower(): c for c in _gpkg.describe(connection, target)[0]}
            if str(target_field).lower() not in target_columns:
                raise ExecuteError(f"ERROR 000728: {tool}: field '{target_field}' does not exist in '{target}'.")
            target_field = target_columns[str(target_field).lower()].name
            detail = None
            if kind == "RELATIONSHIP_CLASS":
                found = [rc for rc in _relationships.classes(connection)
                         if {rc["origin"].lower(), rc["destination"].lower()} == {table.lower(), target.lower()}]
                if not found:
                    raise ExecuteError(f"ERROR 000800: {tool}: '{table}' and '{target}' take part in no relationship "
                                       "class with each other, as RELATIONSHIP_CLASS requires.")
                detail = found[0]["name"]
            elif kind == "MATCH_FIELDS":
                if not target_match or not input_match:
                    raise ExecuteError(f"ERROR 000735: {tool}: MATCH_FIELDS names the target and input match fields.")
                if str(input_match).lower() not in columns or str(target_match).lower() not in target_columns:
                    raise ExecuteError(f"ERROR 000728: {tool}: a match field does not exist.")
                detail = (target_columns[str(target_match).lower()].name, columns[str(input_match).lower()].name)
            elif kind == "SPATIAL_ONLY":
                if shape == "Geometry" or _gpkg.describe(connection, target)[1] == "Geometry":
                    raise ExecuteError(f"ERROR 000800: {tool}: SPATIAL_ONLY is not for tables, as documented.")
                detail = _distance(tool, distance, per_unit, geographic)
            else:
                exists = connection.execute("select 1 from sqlite_master where name = 'grapy_un_source'").fetchone()
                if not exists or not connection.execute("select 1 from grapy_un_source where lower(class_name) = "
                                                        "lower(?)", (table,)).fetchone():
                    raise ExecuteError(f"ERROR 000800: {tool}: utility network search types need the input to be a "
                                       "utility network class, as documented.")
                detail = _UN[kind]
            value_field = columns[str(given).lower()].name if given is not None and str(given).lower() in columns \
                else None
            searches.append((target, target_field, kind, value_field, given, operator, detail,
                             _gpkg.oid_column(connection, target)))
        if not searches:
            raise ExecuteError(f"ERROR 000735: {tool}: give at least one search option.")
    finally:
        connection.close()
    lines = ["var g = Geometry($feature);", _where(where_clause, "{}").rstrip("\n"), "var edits = [];",
             "function grapyCombine(old, value, operator) {",
             "  if (operator == 'CONCATENATE') { if (IsEmpty(old)) { return value } return old + '-' + value }",
             "  if (operator == 'BITWISE_OR') { return Number(old) | Number(value) }",
             "  return value", "}"]
    for target, target_field, kind, value_field, given, operator, detail, oid in searches:
        value = f"$feature[{_literal(value_field)}]" if value_field else _literal(given)
        lines.append("{")
        lines.append(f"  var value = {value}; var updates = [];")
        if kind == "RELATIONSHIP_CLASS":
            lines.append(f"  var rows = FeatureSetByRelationshipClass($feature, {_literal(detail)}, "
                         f"[{_literal(target_field)}], false);")
        elif kind == "MATCH_FIELDS":
            lines.append(f"  var key = $feature[{_literal(detail[1])}];")
            lines.append(f"  var rows = Filter(FeatureSetByName($datastore, {_literal(target)}, "
                         f"[{_literal(target_field)}, {_literal(detail[0])}], false), "
                         f"{_literal(chr(34) + detail[0] + chr(34) + ' = @key')});")
        elif kind == "SPATIAL_ONLY":
            reach = f"Buffer(g, {_literal(detail)})" if detail else "g"
            lines.append(f"  var rows = []; if (g != null) {{ rows = Intersects(FeatureSetByName($datastore, "
                         f"{_literal(target)}, [{_literal(target_field)}], true), {reach}) }}")
        if kind in _UN:
            lines.append(f"  for (var a in FeatureSetByAssociation($feature, {_literal(detail)})) {{")
            lines.append(f"    if (Lower(a.className) != {_literal(target.rsplit('/', 1)[-1].lower())}) {{ continue }}")
            lines.append("    var gid = a.globalId;")
            lines.append(f"    var t = First(Filter(FeatureSetByName($datastore, {_literal(target)}, "
                         f"[{_literal(target_field)}], false), 'upper(GlobalID) = upper(@gid)'));")
            lines.append("    if (t == null) { continue }")
            lines.append(f"    Push(updates, {{globalID: gid, attributes: {{{_literal(target_field)}: "
                         f"grapyCombine(t[{_literal(target_field)}], value, {_literal(operator)})}}}});")
            lines.append("  }")
        else:
            lines.append("  for (var t in rows) {")
            lines.append(f"    Push(updates, {{objectID: t[{_literal(oid)}], attributes: {{{_literal(target_field)}: "
                         f"grapyCombine(t[{_literal(target_field)}], value, {_literal(operator)})}}}});")
            lines.append("  }")
        lines.append(f"  if (Count(updates) > 0) {{ Push(edits, {{className: {_literal(target)}, updates: updates}}) }}")
        lines.append("}")
    lines.append("if (Count(edits) == 0) { return {} }")
    lines.append("return {edit: edits}")
    script = "\n".join(line for line in lines if line)
    path = _write(out_folder, table, {"name": "Update related", "script": script, "events": ["INSERT", "UPDATE"],
                                      "exclude": True,
                                      "description": "Updates " + ", ".join(f"{s[0]}.{s[1]}" for s in searches)})
    return Result(path)
