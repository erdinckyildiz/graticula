"""Copying, appending, exporting, importing and checking parcels — ADR-250.

The tools here read one fabric and write another, a feature dataset of exports, or a check's output class. Choices the
pages leave open are INFERRED in ADR-250 §5.8 and named where they are made.
"""
from __future__ import annotations

import math
import os
import re

import shapely
from shapely.geometry import LineString, Point

from . import _gpkg
from . import _parcel as pf
from ._parcel_build import Snap, related_lines
from ._runtime import AddMessage, AddWarning, ExecuteError, NotBuiltError

_TRACKING = ("created_user", "created_date", "last_edited_user", "last_edited_date")


# ---- what a parcel fabric parameter carries ----------------------------------------------------------------------


class Parcels:
    """The features a fabric parameter names: every feature of the fabric, or — for a layer over one of its polygon
    classes with a selection — the selected parcels, their lines, the points at their corners and lines' ends, the
    connection lines that meet those points, and the records all of these name (the pages of Copy Parcels and Append
    Parcels; which lines are a parcel's is INFERRED, §5.8 (k))."""

    def __init__(self, fab, layer, table, tool):
        self.fab = fab
        self.whole = True
        self.features = {}
        chosen = pf.selection_of(layer.name) if layer is not None else None
        if chosen is None:
            for each in fab.types:
                self.features[each.polygons] = pf.read(fab.file, each.polygons)
                self.features[each.lines] = pf.read(fab.file, each.lines)
            self.features[fab.points] = pf.read(fab.file, fab.points)
            self.features[fab.connections] = pf.read(fab.file, fab.connections)
            self.features[fab.records] = pf.read(fab.file, fab.records)
            return
        self.whole = False
        if fab.role_of(table) != "polygons":
            raise ExecuteError(f"ERROR 000800: {tool}: a selection is given by selecting parcel polygons; lines and "
                               "points are not taken on their own.")
        parcel_type = fab.type_of(table)
        polygons = [p for p in pf.read(fab.file, table) if p.oid in chosen]
        lines = pf.read(fab.file, parcel_type.lines)
        picked = {}
        for polygon in polygons:
            if polygon.shape is not None:
                for ln in related_lines(fab, parcel_type, polygon, lines):
                    picked[ln.oid] = ln
        places = Snap(fab.tolerance)
        for polygon in polygons:
            for ring in pf.rings(polygon.shape) if polygon.shape is not None else []:
                for xy in ring:
                    places.add(xy)
        for ln in picked.values():
            for xy in pf.ends(ln.shape):
                places.add(xy)
        points = [p for p in pf.read(fab.file, fab.points) if p.shape is not None
                  and places.find((p.shape.x, p.shape.y)) is not None]
        connections = [c for c in pf.read(fab.file, fab.connections) if c.shape is not None
                       and any(places.find(xy) is not None for xy in pf.ends(c.shape))]
        for each in fab.types:
            self.features[each.polygons] = polygons if each.polygons == table else []
            self.features[each.lines] = list(picked.values()) if each.lines == parcel_type.lines else []
        self.features[fab.points] = points
        self.features[fab.connections] = connections
        guids = set()
        for rows in self.features.values():
            for row in rows:
                for name in ("CreatedByRecord", "RetiredByRecord"):
                    if row.get_ci(name):
                        guids.add(str(row.get_ci(name)).upper())
        self.features[fab.records] = [r for r in pf.read(fab.file, fab.records)
                                      if str(r.get_ci("GlobalID")).upper() in guids]


def _row(feature, columns, skip=()):
    """A Feature's values for the columns a target has (by name, ignoring case), and its shape."""
    held = {k.lower(): k for k in feature}
    out = {}
    for column in columns:
        key = held.get(column.name.lower())
        if key is not None and column.name.lower() not in skip:
            out[column.name] = feature[key]
    out["shape"] = feature.shape
    return out


# ---- copying schema that is not a field --------------------------------------------------------------------------


def _schema_extras(source_file, source_table, target_file, target_table):
    """A class's domains (field and subtype), subtypes and defaults copied to another class, in the same or another
    GeoPackage."""
    from . import _subtypes
    from .management import _domain_tables
    connection = _gpkg.connect(target_file)
    try:
        _domain_tables(connection)
        _subtypes.ensure(connection)
        same = os.path.normcase(os.path.abspath(source_file)) == os.path.normcase(os.path.abspath(target_file))
        schema = "main" if same else "src"
        if not same:
            connection.execute("attach database ? as src", (source_file,))
        try:
            held = {r[0] for r in connection.execute(f"select name from {schema}.sqlite_master")}
            if "gpkg_data_columns" in held:
                rows = connection.execute(f"select column_name, name, title, description, mime_type, constraint_name "
                                          f"from {schema}.gpkg_data_columns where table_name = ?", (source_table,)).fetchall()
                domains = {r[5] for r in rows if r[5]}
                if "grapy_subtype_domain" in held:
                    domains |= {r[0] for r in connection.execute(
                        f"select domain_name from {schema}.grapy_subtype_domain where table_name = ?", (source_table,))}
                if not same:
                    for domain in domains:
                        if connection.execute("select 1 from main.grapy_domains where name = ?", (domain,)).fetchone():
                            continue
                        connection.execute("insert into main.grapy_domains select * from src.grapy_domains where name = ?",
                                           (domain,))
                        connection.execute("insert or ignore into main.gpkg_data_column_constraints select * from "
                                           "src.gpkg_data_column_constraints where constraint_name = ?", (domain,))
                for row in rows:
                    connection.execute("insert or replace into main.gpkg_data_columns (table_name, column_name, name, "
                                       "title, description, mime_type, constraint_name) values (?, ?, ?, ?, ?, ?, ?)",
                                       (target_table, *row))
            if "grapy_subtype_field" in held:
                for name in _subtypes.TABLES:
                    if name not in held:
                        continue
                    columns = [r[1] for r in connection.execute(f"pragma {schema}.table_info({name})")]
                    others = ", ".join(c for c in columns if c != "table_name")
                    connection.execute(f"delete from main.{name} where table_name = ?", (target_table,))
                    connection.execute(f"insert into main.{name} (table_name, {others}) select ?, {others} from "
                                       f"{schema}.{name} where table_name = ?", (target_table, source_table))
            connection.commit()
        finally:
            if not same:
                connection.execute("detach database src")
    finally:
        connection.close()


def _relationships_of(file, table):
    from . import _relationships
    connection = _gpkg.connect(file)
    try:
        return [rc for rc in _relationships.classes(connection) if table in (rc["origin"], rc["destination"])
                and not rc["name"].endswith("__ATTACHREL")]
    finally:
        connection.close()


def _copy_attachments(source_file, source_table, target_file, target_table):
    from . import _attachments
    connection = _gpkg.connect(source_file)
    try:
        if not _attachments.enabled(connection, source_table):
            return
    finally:
        connection.close()
    connection = _gpkg.connect(target_file)
    try:
        if not _attachments.enabled(connection, target_table):
            _attachments.enable(connection, target_table)
        same = os.path.normcase(os.path.abspath(source_file)) == os.path.normcase(os.path.abspath(target_file))
        if not same:
            connection.execute("attach database ? as src", (source_file,))
        schema = "main" if same else "src"
        attached, theirs = _attachments.attach_table(target_table), _attachments.attach_table(source_table)
        columns = [r[1] for r in connection.execute(f'pragma {schema}.table_info("{theirs}")') if r[1] != "ATTACHMENTID"]
        listed = ", ".join(f'"{c}"' for c in columns)
        connection.execute(f'insert into main."{attached}" ({listed}) select {listed} from {schema}."{theirs}"')
        connection.commit()
        if not same:
            connection.execute("detach database src")
    finally:
        connection.close()


def _user_columns(source_columns, target_columns):
    held = {c.name.lower() for c in target_columns}
    return [c for c in source_columns if c.name.lower() not in held]


# ---- CopyParcels ---------------------------------------------------------------------------------------------------


def copy_parcels(parcels, target_file, dataset_name, fabric_name):
    from . import _ruletools, management
    fab = parcels.fab
    for table in [fab.records, fab.points, fab.connections] + [t for each in fab.types for t in (each.polygons, each.lines)]:
        if _relationships_of(fab.file, table):
            raise NotBuiltError("arcpy.parcel.CopyParcels: a fabric class in a relationship class with a table outside "
                                f"the fabric ('{table}') is not built in grapy: its related table and relationship class "
                                "would have to be copied beside the fabric; copy them with management.Copy.")
    management.CreateFeatureDataset(target_file, dataset_name, fab.srid or None)
    pf.create(target_file, dataset_name, fabric_name)
    new = pf.fabric(f"{target_file}/{dataset_name}/{fabric_name}", "CopyParcels")
    mapping = {fab.records: new.roles["records"], fab.points: new.roles["points"],
               fab.connections: new.roles["connections"]}
    for role in ("adjustment_lines", "adjustment_points", "adjustment_vectors"):
        mapping[fab.roles[role]] = new.roles[role]
    for each in fab.types:
        polygons, lines = pf.add_type(new, each.name, each.administrative, each.strata)
        mapping[each.polygons], mapping[each.lines] = polygons, lines
        new = pf.fabric(new.path, "CopyParcels")
    for source, target in mapping.items():
        extra = _user_columns(pf.columns_of(fab.file, source), pf.columns_of(target_file, target))
        for column in extra:
            _gpkg.add_column(f"{target_file}/{target}", column)
        _schema_extras(fab.file, source, target_file, target)
    copied = 0
    for source, target in mapping.items():
        rows = parcels.features.get(source)
        if rows is None:
            rows = pf.read(fab.file, source) if parcels.whole else []
        columns = pf.columns_of(target_file, target)
        pf.insert(target_file, target, [_row(r, columns, _TRACKING) for r in rows])
        copied += len(rows)
        _copy_attachments(fab.file, source, target_file, target)
    # user topology rules among the fabric's classes; those naming a class outside the fabric are not copied
    _copy_user_rules(fab, new, mapping, target_file)
    # attribute rules, but for those reading a class outside the fabric (by FeatureSetByName)
    short = {t.rsplit("/", 1)[-1].lower() for t in mapping}
    import csv
    import tempfile
    for source, target in mapping.items():
        with tempfile.TemporaryDirectory() as folder:
            exported = os.path.join(folder, "rules.csv")
            _ruletools.ExportAttributeRules(f"{fab.file}/{source}", exported)
            with open(exported, encoding="utf-8", newline="") as read:
                rows = list(csv.reader(read))
            header, body = rows[0], rows[1:]
            script = header.index("SCRIPTEXPRESSION") if "SCRIPTEXPRESSION" in header else None
            kept = [r for r in body if script is None or _reads_only(r[script], short)]
            if not kept:
                continue
            filtered = os.path.join(folder, "kept.csv")
            with open(filtered, "w", encoding="utf-8", newline="") as written:
                out = csv.writer(written)
                out.writerow(header)
                out.writerows(kept)
            existing = _rule_names(target_file, target)
            if existing:
                kept = [r for r in kept if r[0] not in existing]
                with open(filtered, "w", encoding="utf-8", newline="") as written:
                    out = csv.writer(written)
                    out.writerow(header)
                    out.writerows(kept)
            if kept:
                _ruletools.ImportAttributeRules(f"{target_file}/{target}", filtered)
    if fab.enabled:
        pf.enable_topology(pf.fabric(new.path, "CopyParcels"))
    AddMessage(f"{copied} features copied.")
    return new


def _rule_names(file, table):
    from . import _rules
    connection = _gpkg.connect(file)
    try:
        return {r.name for r in _rules.rules(connection, table)}
    finally:
        connection.close()


def _reads_only(script, names):
    for found in re.findall(r"FeatureSetByName\s*\(\s*\$datastore\s*,\s*['\"]([^'\"]+)['\"]", script or "", re.I):
        if found.rsplit("/", 1)[-1].lower() not in names:
            return False
    return True


def _copy_user_rules(fab, new, mapping, target_file):
    from . import _topology
    system = {(r, o, d) for r, o, d in pf.system_rules(fab)}
    source = _gpkg.connect(fab.file)
    try:
        _topology.ensure(source)
        rules = source.execute("select rule_type, origin, destination, origin_subtype, destination_subtype from "
                               "grapy_topology_rule where topology = ?", (fab.topology,)).fetchall()
        ranks = dict(source.execute("select class_name, xy_rank from grapy_topology_class where topology = ?",
                                    (fab.topology,)).fetchall())
    finally:
        source.close()
    user = [r for r in rules if not ((r[0], r[1], r[2]) in system and r[3] is None and r[4] is None)]
    target = _gpkg.connect(target_file)
    try:
        _topology.ensure(target)
        for rule, origin, destination, os_, ds_ in user:
            if origin not in mapping or (destination is not None and destination not in mapping):
                continue
            for table in (origin, destination):
                if table is None:
                    continue
                if not target.execute("select 1 from grapy_topology_class where topology = ? and class_name = ?",
                                      (new.topology, mapping[table])).fetchone():
                    target.execute("insert into grapy_topology_class (topology, class_name, xy_rank, z_rank) values "
                                   "(?, ?, ?, 1)", (new.topology, mapping[table], ranks.get(table, 1)))
            target.execute("insert into grapy_topology_rule (topology, rule_type, origin, destination, origin_subtype, "
                           "destination_subtype) values (?, ?, ?, ?, ?, ?)",
                           (new.topology, rule, mapping[origin], mapping.get(destination), os_, ds_))
        target.commit()
    finally:
        target.close()


# ---- AppendParcels ----------------------------------------------------------------------------------------------------


def append_parcels(parcels, target, method):
    fab = parcels.fab
    for each in fab.types:
        if parcels.features.get(each.polygons) or parcels.features.get(each.lines):
            if not any(t.name.lower() == each.name.lower() for t in target.types):
                raise ExecuteError(f"ERROR 000800: AppendParcels: the target parcel fabric has no parcel type "
                                   f"'{each.name}'; the parcel types must match.")
    # records: matched by name, updated, or added
    guid_map = {}
    target_records = {str(r.get("Name") or "").strip().lower(): r for r in pf.records(target)}
    columns = pf.columns_of(target.file, target.records)
    updates, inserts = {}, []
    for record in parcels.features[fab.records]:
        key = str(record.get("Name") or "").strip().lower()
        values = _row(record, columns, {"globalid", *_TRACKING})
        found = target_records.get(key)
        if found is not None and key:
            updates[found.oid] = values
            guid_map[str(record.get_ci("GlobalID")).upper()] = found.get_ci("GlobalID")
        else:
            inserts.append((record, values))
    pf.update(target.file, target.records, updates)
    for record, values in inserts:
        oid = pf.insert(target.file, target.records, [values])[0]
        made = pf.read(target.file, target.records, f'"fid" = {int(oid)}')[0]
        guid_map[str(record.get_ci("GlobalID")).upper()] = made.get_ci("GlobalID")

    def remap(values):
        for name in ("CreatedByRecord", "RetiredByRecord"):
            for key in list(values):
                if key.lower() == name.lower() and values[key]:
                    values[key] = guid_map.get(str(values[key]).upper(), values[key])
        return values

    # points, with the coincident-point method
    point_columns = pf.columns_of(target.file, target.points)
    held = Snap(target.tolerance)
    by_place = {}
    for point in pf.read(target.file, target.points):
        if point.shape is not None:
            by_place.setdefault(held.add((point.shape.x, point.shape.y)), point)
    new_points, point_updates = [], {}
    for point in parcels.features[fab.points]:
        values = remap(_row(point, point_columns, {"globalid", *_TRACKING}))
        place = held.find((point.shape.x, point.shape.y)) if point.shape is not None and method != "KEEP_BOTH" else None
        if place is not None and place in by_place:
            if method == "KEEP_SOURCE":
                point_updates[by_place[place].oid] = values
            continue
        new_points.append(values)
    pf.update(target.file, target.points, point_updates)
    pf.insert(target.file, target.points, new_points)
    counts = {"points": len(new_points)}
    for each in fab.types:
        into = next(t for t in target.types if t.name.lower() == each.name.lower())
        for source, destination in ((each.polygons, into.polygons), (each.lines, into.lines)):
            rows = parcels.features.get(source) or []
            columns = pf.columns_of(target.file, destination)
            values = [remap(_row(r, columns, {"globalid", "parentlineid", *_TRACKING})) for r in rows]
            pf.insert(target.file, destination, values)
            counts[destination] = len(values)
    columns = pf.columns_of(target.file, target.connections)
    pf.insert(target.file, target.connections,
              [remap(_row(r, columns, {"globalid", *_TRACKING})) for r in parcels.features[fab.connections]])
    pf.refresh_records(target)
    AddMessage(f"{sum(counts.values())} features appended.")


# ---- exports ----------------------------------------------------------------------------------------------------------


def _target_dataset(value, tool):
    from .management import _feature_dataset_of
    text = pf._text(value)
    file, dataset = _gpkg.split(text)
    if not dataset or "/" in dataset or _feature_dataset_of(f"{file}/{dataset}/x") is None:
        raise ExecuteError(f"ERROR 000732: {tool}: '{value}' is not a feature dataset.")
    return file, dataset


def _export(source_file, source_table, out_file, out_table, rows, extra=()):
    """Rows written to a class made like the source's (plus ``extra`` fields), or to the class already there after
    its rows are deleted."""
    from ._gpkg import Column
    columns = [c for c in pf.columns_of(source_file, source_table)]
    connection = _gpkg.connect(source_file)
    try:
        shape, srid = _gpkg.describe(connection, source_table)[1:3]
    finally:
        connection.close()
    connection = _gpkg.connect(out_file) if os.path.exists(out_file) else None
    try:
        exists = connection is not None and connection.execute(
            "select 1 from gpkg_contents where table_name = ?", (out_table,)).fetchone()
    finally:
        if connection is not None:
            connection.close()
    if exists:
        pf.delete(out_file, out_table, [f.oid for f in pf.read(out_file, out_table)])
        held = {c.name.lower() for c in pf.columns_of(out_file, out_table)}
        for column in [*columns, *(Column(n, t) for n, t in extra)]:
            if column.name.lower() not in held:
                _gpkg.add_column(f"{out_file}/{out_table}", column)
    else:
        _gpkg.create(f"{out_file}/{out_table}", shape, srid, columns + [Column(n, t) for n, t in extra])
    target_columns = pf.columns_of(out_file, out_table)
    values = []
    for row in rows:
        value = _row(row, target_columns)
        for name, _ in extra:
            value[name] = row.get(name)
        values.append(value)
    pf.insert(out_file, out_table, values)
    return f"{out_file}/{out_table}"


def export_record(fab, record, target):
    """The features a record created or retired, by class, each with its ExportedStatus (INFERRED name and codes,
    §5.8 (m)); every class is made, reused, or emptied, as documented."""
    out_file, dataset = target
    if os.path.normcase(os.path.abspath(out_file)) == os.path.normcase(os.path.abspath(fab.file)) and \
            dataset == fab.dataset:
        raise ExecuteError("ERROR 000800: ExportParcelRecordFeatures: the target feature dataset must not be the "
                           "parcel fabric's own.")
    pf._domains(out_file)
    guid = record.get_ci("GlobalID")
    status = [("ExportedStatus", "Integer")]

    def tagged(rows):
        out = []
        for row in rows:
            created = pf.same_guid(row.get_ci("CreatedByRecord"), guid)
            retired = pf.same_guid(row.get_ci("RetiredByRecord"), guid)
            if created or retired:
                row["ExportedStatus"] = pf.CREATED_AND_RETIRED if created and retired else \
                    (pf.CREATED if created else pf.RETIRED)
                out.append(row)
        return out
    outputs = []
    for each in fab.types:
        for table in (each.polygons, each.lines):
            name = f"{dataset}/{table.rsplit('/', 1)[-1]}"
            outputs.append(_export(fab.file, table, out_file, name, tagged(pf.read(fab.file, table)), status))
    for table in (fab.connections, fab.points):
        name = f"{dataset}/{table.rsplit('/', 1)[-1]}"
        outputs.append(_export(fab.file, table, out_file, name, tagged(pf.read(fab.file, table)), status))
    records_out = _export(fab.file, fab.records, out_file, f"{dataset}/{fab.records.rsplit('/', 1)[-1]}", [record])
    from . import management
    for path in outputs:
        management.AssignDomainToField(path, "ExportedStatus", pf.EXPORT_STATUS[0])
    return records_out


def _sequence_format(pattern):
    """A format such as ``#``, ``##``, ``P#``, ``ABC-##1`` or ``#P``: the run of ``#`` (with any digits that follow it)
    is the number, zero-padded to the run's length; the rest is kept as written (INFERRED from the page's examples,
    §5.8 (n))."""
    text = str(pattern) if pf.given(pattern) else "#"
    match = re.search(r"#+\d*", text)
    if match is None:
        raise ExecuteError(f"ERROR 000800: ExportSequencedParcelFeatures: '{pattern}' has no '#' for the number.")
    width = len(match.group(0))
    return lambda n: text[:match.start()] + str(n).zfill(width) + text[match.end():]


_CORNERS = {"NE": (2, 3), "SE": (2, 1), "SW": (0, 1), "NW": (0, 3)}


def export_sequenced(fab, parcel_type, polygon, target, corner, points_format, lines_format, separately):
    out_file, dataset = target
    if os.path.normcase(os.path.abspath(out_file)) == os.path.normcase(os.path.abspath(fab.file)) and \
            dataset == fab.dataset:
        raise ExecuteError("ERROR 000800: ExportSequencedParcelFeatures: the target feature dataset must not be the "
                           "parcel fabric's own.")
    for table in (parcel_type.polygons, parcel_type.lines, fab.points):
        if _relationships_of(fab.file, table):
            raise NotBuiltError("arcpy.parcel.ExportSequencedParcelFeatures: a parcel class with related tables "
                                f"('{table}') is not built in grapy: the related rows would have to be exported with "
                                "their own relationship class.")
    from shapely.geometry.polygon import orient
    part = polygon.shape if polygon.shape.geom_type == "Polygon" else max(polygon.shape.geoms, key=lambda g: g.area)
    part = orient(part, sign=-1.0)   # the exterior clockwise
    ring = LineString(part.exterior.coords)
    box = part.bounds
    ix, iy = _CORNERS[corner]
    target_corner = (box[ix], box[iy])
    lines = related_lines(fab, parcel_type, polygon, pf.read(fab.file, parcel_type.lines))
    nodes = []
    for ln in lines:
        nodes.extend(pf.ends(ln.shape))
    if not nodes:
        nodes = [c for c in part.exterior.coords[:-1]]
    start = min(nodes, key=lambda xy: math.hypot(xy[0] - target_corner[0], xy[1] - target_corner[1]))
    origin = ring.project(Point(start))
    total = ring.length

    def along(xy):
        return (ring.project(Point(xy)) - origin) % total if total else 0.0

    line_format, point_format = _sequence_format(lines_format), _sequence_format(points_format)
    ordered = sorted(lines, key=lambda ln: along(ln.shape.interpolate(0.5, normalized=True).coords[0]))
    straight_n = curve_n = 0
    line_rows = []
    for ln in ordered:
        curve = ln.get_ci("Radius") not in (None, 0)
        if separately and curve:
            curve_n += 1
            n = curve_n
        else:
            straight_n += 1
            n = straight_n
        row = pf.Feature(ln)
        row.shape = ln.shape
        row["Sequence"], row["IsCurve"] = line_format(n), 1 if curve else 0
        line_rows.append(row)
    snap = Snap(fab.tolerance)
    for xy in nodes:
        snap.add(xy)
    points = [p for p in pf.read(fab.file, fab.points) if p.shape is not None and snap.find((p.shape.x, p.shape.y))
              and (pf.current(p) == pf.current(polygon))]
    point_rows = []
    for k, p in enumerate(sorted(points, key=lambda p: along((p.shape.x, p.shape.y))), start=1):
        row = pf.Feature(p)
        row.shape = p.shape
        row["Sequence"] = point_format(k)
        point_rows.append(row)
    connections = [c for c in pf.read(fab.file, fab.connections) if c.shape is not None
                   and any(snap.find(xy) is not None for xy in pf.ends(c.shape))]
    records = [r for r in pf.records(fab) if pf.same_guid(r.get_ci("GlobalID"), polygon.get_ci("CreatedByRecord"))]
    short = parcel_type.polygons.rsplit("/", 1)[-1]
    out = [_export(fab.file, parcel_type.polygons, out_file, f"{dataset}/{short}", [polygon]),
           _export(fab.file, parcel_type.lines, out_file, f"{dataset}/{short}_Lines", line_rows,
                   [("Sequence", "String"), ("IsCurve", "Integer")]),
           _export(fab.file, fab.points, out_file, f"{dataset}/{fab.points.rsplit('/', 1)[-1]}", point_rows,
                   [("Sequence", "String")]),
           _export(fab.file, fab.connections, out_file, f"{dataset}/{fab.connections.rsplit('/', 1)[-1]}", connections),
           _export(fab.file, fab.records, out_file, f"{dataset}/{fab.records.rsplit('/', 1)[-1]}", records)]
    return out


# ---- ImportParcelFabricPoints ---------------------------------------------------------------------------------------


def import_points(fab, source, match_method, search, update_type, record_name, match_field, conflicts_table,
                  create_option, target_where):
    from . import da
    from ._reference import SpatialReference
    file, table, _ = pf.read_input(source, "ImportParcelFabricPoints")
    from .management import _source
    path, where = _source(source)
    source_columns = pf.columns_of(file, table)
    names = [c.name for c in source_columns]
    connection = _gpkg.connect(file)
    try:
        has_z = _gpkg.dimensions(connection, table)[0]
        srid = _gpkg.describe(connection, table)[2]
    finally:
        connection.close()
    rows = []
    with da.SearchCursor(path, ["OID@", "SHAPE@", *names], where) as cursor:
        for row in cursor:
            geometry = row[1]
            if geometry is None:
                continue
            if srid and fab.srid and srid != fab.srid:
                geometry = geometry.projectAs(SpatialReference(fab.srid))
            shape = geometry._shape
            rows.append((row[0], shape, dict(zip(names, row[2:]))))
    if match_method != "PROXIMITY":
        if not pf.given(match_field):
            raise ExecuteError(f"ERROR 000735: ImportParcelFabricPoints: {match_method} needs a match field.")
        if str(match_field).lower() not in {n.lower() for n in names}:
            raise ExecuteError(f"ERROR 000728: ImportParcelFabricPoints: the field '{match_field}' is not in the source.")
        match_field = next(n for n in names if n.lower() == str(match_field).lower())
    guid = pf.ensure_record(fab, record_name) if pf.given(record_name) else None
    if update_type == "RETIRE_AND_REPLACE" and guid is None:
        raise ExecuteError("ERROR 000735: ImportParcelFabricPoints: RETIRE_AND_REPLACE needs a record name.")
    targets = pf.read(fab.file, fab.points, target_where)
    target_columns = pf.columns_of(fab.file, fab.points)
    shared = [c.name for c in target_columns if c.name.lower() in {n.lower() for n in names}
              and c.name.lower() not in {"globalid", "createdbyrecord", "retiredbyrecord", *_TRACKING}]
    conflicts, creates, updates, moves = [], [], {}, {}
    for oid, shape, values in rows:
        candidates = [t for t in targets if t.shape is not None and
                      math.hypot(t.shape.x - shape.x, t.shape.y - shape.y) <= search]
        if match_method == "NAME_AND_PROXIMITY":
            candidates = [t for t in candidates if str(t.get_ci("Name") or "") == str(values[match_field] or "")]
        elif match_method == "GLOBALID_AND_PROXIMITY":
            candidates = [t for t in candidates if pf.same_guid(t.get_ci("GlobalID"), values[match_field])]
        if len(candidates) > 1:
            AddWarning(f"WARNING: source point {oid} has {len(candidates)} parcel fabric points within the search "
                       "distance; it is not imported.")
            conflicts.extend((oid, t.oid) for t in candidates)
            continue
        new_values = {n: values[next(s for s in names if s.lower() == n.lower())] for n in shared}
        if not candidates:
            if create_option != "UPDATE_ONLY":
                creates.append({**new_values, "CreatedByRecord": guid, "shape": Point(shape.x, shape.y)})
            continue
        if create_option == "CREATE_ONLY":
            continue
        found = candidates[0]
        if found.get_ci("IsFixed") == pf.YES:
            continue
        if update_type == "RETIRE_AND_REPLACE":
            updates[found.oid] = {"RetiredByRecord": guid}
            creates.append({**new_values, "CreatedByRecord": guid, "shape": Point(shape.x, shape.y)})
            continue
        change = dict(new_values) if update_type == "ALL" else {}
        if has_z and shape.has_z and "Z" not in change:
            change["Z"] = shape.z
        change["shape"] = Point(shape.x, shape.y)
        updates[found.oid] = change
        moves[(found.shape.x, found.shape.y)] = (shape.x, shape.y)
    pf.update(fab.file, fab.points, updates)
    pf.insert(fab.file, fab.points, creates)
    _follow(fab, moves)
    if pf.given(conflicts_table):
        out = pf._text(conflicts_table)
        out_file, out_table = _gpkg.split(out if ".gpkg" in out.lower() else out + ".gpkg")
        _gpkg.create(f"{out_file}/{out_table or 'Conflicts'}", "Geometry", 0,
                     [_gpkg.Column("SourceOID", "Integer"), _gpkg.Column("TargetOID", "Integer")])
        connection = _gpkg.connect(out_file)
        try:
            connection.executemany(f'insert into "{out_table or "Conflicts"}" ("SourceOID", "TargetOID") values (?, ?)',
                                   conflicts)
            connection.commit()
        finally:
            connection.close()
    AddMessage(f"{len(updates)} points updated, {len(creates)} created, {len(conflicts)} conflicts.")
    return len(updates), len(creates), len(conflicts)


def _follow(fab, moves):
    """Lines and polygons whose vertices sit on a moved point follow it."""
    if not moves:
        return
    import numpy
    snap = Snap(fab.tolerance)
    for old in moves:
        snap.add(old)

    def move(coordinates):
        out = []
        for c in coordinates.tolist():
            found = snap.find((c[0], c[1]))
            out.append(moves[found] if found is not None else (c[0], c[1]))
        return numpy.array(out)
    tables = fab.line_classes() + [t.polygons for t in fab.types]
    for table in tables:
        changes = {}
        for feature in pf.read(fab.file, table):
            if feature.shape is None:
                continue
            moved = shapely.transform(feature.shape, move)
            if not moved.equals_exact(feature.shape, 0.0):
                changes[feature.oid] = {"shape": moved}
        pf.update(fab.file, table, changes)


# ---- checks --------------------------------------------------------------------------------------------------------


def _copy_out(file, table, features, out):
    """Features copied to a new class with the source's fields."""
    out_file, out_table = _gpkg.split(out if ".gpkg" in out.lower() else out + ".gpkg")
    out_table = out_table or os.path.splitext(os.path.basename(out_file))[0]
    connection = _gpkg.connect(file)
    try:
        columns, shape, srid, _ = _gpkg.describe(connection, table)
    finally:
        connection.close()
    _gpkg.create(f"{out_file}/{out_table}", shape, srid, columns)
    pf.insert(out_file, out_table, [_row(f, columns) for f in features])
    return f"{out_file}/{out_table}"


def adjacent_points(source, out, tolerance):
    file, table, features = pf.read_input(source, "FindAdjacentParcelPoints")
    from shapely.strtree import STRtree
    shapes = [f.shape for f in features]
    tree = STRtree(shapes)
    found = []
    for i, f in enumerate(features):
        if f.shape is None:
            continue
        near = [int(j) for j in tree.query(f.shape, predicate="dwithin", distance=tolerance) if int(j) != i]
        if near:
            found.append(f)
    path = _copy_out(file, table, found, out)
    AddMessage(f"{len(found)} adjacent points found.")
    return path, len(found)


def disconnected_points(source, out, ignore_fixed, ignore_constrained, consider_edge):
    file, table, features = pf.read_input(source, "FindDisconnectedParcelPoints")
    fab = pf.fabric_of_class(file, table)
    if fab is None or table != fab.points:
        raise ExecuteError("ERROR 000800: FindDisconnectedParcelPoints: the input is a parcel fabric's points.")
    ends, interiors = Snap(fab.tolerance), []
    for line_table in fab.line_classes():
        for ln in pf.read(fab.file, line_table):
            if ln.shape is not None:
                for xy in pf.ends(ln.shape):
                    ends.add(xy)
                interiors.append(ln.shape)
    for each in fab.types:
        for p in pf.read(fab.file, each.polygons):
            if p.shape is not None and p.get_ci("IsSeed") != pf.YES:
                interiors.append(p.shape.boundary)
    edges = shapely.union_all(interiors) if interiors else None
    found = []
    for point in features:
        if point.shape is None:
            continue
        if ignore_fixed and point.get_ci("IsFixed") == pf.YES:
            continue
        if ignore_constrained and point.get_ci("AdjustmentConstraint") == pf.XYZ_CONSTRAINED:
            continue
        xy = (point.shape.x, point.shape.y)
        if ends.find(xy) is not None:
            continue
        on_edge = edges is not None and edges.distance(point.shape) <= fab.tolerance
        if on_edge and not consider_edge:
            continue
        found.append(point)
    path = _copy_out(file, table, found, out)
    AddMessage(f"{len(found)} disconnected points found.")
    return path, len(found)


def gaps_and_overlaps(inputs, out, width, between):
    """EQUIVALENT (§5.4): overlaps are the pairwise intersections of the parcels, gaps the holes of their union; each
    is kept when a negative buffer of half the maximum width empties it — it is narrower than the width."""
    layers = []
    srid = 0
    for value in inputs:
        file, table, features = pf.read_input(value, "FindGapsAndOverlaps")
        connection = _gpkg.connect(file)
        try:
            srid = srid or _gpkg.describe(connection, table)[2]
        finally:
            connection.close()
        polygons = [f.shape for f in features if pf.current(f) and f.get_ci("IsSeed") != pf.YES
                    and pf.polygon_of(f.shape) is not None]
        layers.append((pf._text(value).replace("\\", "/").rsplit("/", 1)[-1], polygons))
    found = []

    def narrow(shape):
        return shape.area > 0 and shape.buffer(-width / 2.0).is_empty

    def overlaps(a_shapes, b_shapes, same):
        out = []
        for i, a in enumerate(a_shapes):
            for j, b in enumerate(b_shapes):
                if same and j <= i:
                    continue
                for part in shapely.get_parts(a.intersection(b)):
                    if part.geom_type == "Polygon" and narrow(part):
                        out.append(part)
        return out

    def gaps(shapes):
        out = []
        union = shapely.union_all(shapes) if shapes else None
        for part in shapely.get_parts(union) if union is not None else []:
            if part.geom_type != "Polygon":
                continue
            for ring in part.interiors:
                hole = shapely.geometry.Polygon(ring.coords)
                if narrow(hole):
                    out.append(hole)
        return out
    for name, shapes in layers:
        found.extend(("Overlap", name, None, s) for s in overlaps(shapes, shapes, True))
        found.extend(("Gap", name, None, s) for s in gaps(shapes))
    if between and len(layers) > 1:
        for i, (name_a, a) in enumerate(layers):
            for name_b, b in layers[i + 1:]:
                found.extend(("Overlap", name_a, name_b, s) for s in overlaps(a, b, False))
        reported = [s for kind, _, _, s in found if kind == "Gap"]
        for hole in gaps([s for _, shapes in layers for s in shapes]):
            if any(hole.symmetric_difference(r).area <= 1e-9 * max(hole.area, 1.0) for r in reported):
                continue
            touching = [name for name, shapes in layers if any(s.intersects(hole.buffer(1e-9)) for s in shapes)]
            found.append(("Gap", touching[0] if touching else None, touching[1] if len(touching) > 1 else None, hole))
    out_file, out_table = _gpkg.split(out if ".gpkg" in out.lower() else out + ".gpkg")
    out_table = out_table or os.path.splitext(os.path.basename(out_file))[0]
    columns = [_gpkg.Column("Type", "String"), _gpkg.Column("ParcelInput1", "String"),
               _gpkg.Column("ParcelInput2", "String")]
    _gpkg.create(f"{out_file}/{out_table}", "Polygon", srid, columns)
    pf.insert(out_file, out_table, [{"Type": kind, "ParcelInput1": a if between else None,
                                     "ParcelInput2": b if between else None, "shape": s} for kind, a, b, s in found])
    AddMessage(f"{sum(1 for f in found if f[0] == 'Gap')} gaps and {sum(1 for f in found if f[0] == 'Overlap')} "
               "overlaps found.")
    return f"{out_file}/{out_table}", len(found)


def validate(fab, box):
    """EQUIVALENT (§5.4): the topology validated as ValidateTopology validates it (ADR-225 §5.3); within an extent, only
    the features meeting it are clustered and evaluated, and only the errors meeting it are replaced."""
    from . import _subtypes, _topology as topo, management
    path = f"{fab.file}/{fab.dataset}/{fab.topology}"
    if box is None:
        management._validate_topology(path)
        return
    connection = _gpkg.connect(fab.file)
    try:
        topo.ensure(connection)
        members = connection.execute("select class_name, xy_rank from grapy_topology_class where topology = ? "
                                     "order by class_id", (fab.topology,)).fetchall()
        rules = connection.execute("select rule_id, rule_type, origin, destination, origin_subtype, destination_subtype "
                                   "from grapy_topology_rule where topology = ?", (fab.topology,)).fetchall()
        subtype_fields = {t: (_subtypes.field(connection, t) or (None,))[0] for t, _ in members}
    finally:
        connection.close()
    features, subtype_of = {}, {}
    for table, _ in members:
        rows = [f for f in pf.read(fab.file, table) if f.shape is not None and not f.shape.is_empty
                and f.shape.intersects(box)]
        features[table] = [(f.oid, f.shape) for f in rows]
        field = subtype_fields[table]
        subtype_of[table] = {f.oid: (f.get_ci(field) if field else None) for f in rows}

    def of(table, code):
        if code is None:
            return features[table]
        return [f for f in features[table] if subtype_of[table].get(f[0]) == code]
    moved = topo.cluster([(rank, features[table]) for table, rank in members], fab.tolerance)
    for index, (table, _) in enumerate(members):
        changed = {oid: {"shape": shape} for (i, oid), shape in moved.items() if i == index}
        pf.update(fab.file, table, changed)
        features[table] = [(oid, changed[oid]["shape"] if oid in changed else shape) for oid, shape in features[table]]
    connection = _gpkg.connect(fab.file)
    try:
        for error_id, blob in connection.execute("select error_id, shape from grapy_topology_error where topology = ?",
                                                 (fab.topology,)).fetchall():
            shape = _gpkg.decode(blob)
            if shape is None or shape.intersects(box):
                connection.execute("delete from grapy_topology_error where error_id = ?", (error_id,))
        for rule_id, rule_type, origin, destination, os_, ds_ in rules:
            for shape, origin_oid, destination_oid in topo.evaluate(rule_type, of(origin, os_),
                                                                    of(destination, ds_) if destination else []):
                if not shape.intersects(box):
                    continue
                connection.execute(
                    "insert into grapy_topology_error (topology, rule_id, kind, origin_class, origin_oid, "
                    "destination_class, destination_oid, rule_type, description, is_exception, shape) values "
                    "(?, ?, ?, ?, ?, ?, ?, ?, ?, 0, ?)",
                    (fab.topology, rule_id, topo.kind_of(shape), origin.rsplit("/", 1)[1], int(origin_oid),
                     (destination or origin).rsplit("/", 1)[1] if destination_oid else "", int(destination_oid),
                     topo.rule(rule_type)[1][1], topo.description(rule_type), _gpkg.encode(shape, fab.srid)))
        connection.commit()
    finally:
        connection.close()


# ---- GenerateParcelFabricLinks ---------------------------------------------------------------------------------------


def _when(value, tool, name):
    """A date parameter as the ISO text grapy's moments are kept in (UTC; INFERRED, §5.8 (u))."""
    import datetime
    if isinstance(value, datetime.datetime):
        found = value
    elif isinstance(value, datetime.date):
        found = datetime.datetime(value.year, value.month, value.day)
    else:
        text = str(value).strip()
        found = None
        try:
            found = datetime.datetime.fromisoformat(text)
        except ValueError:
            for pattern in ("%m/%d/%Y %I:%M:%S %p", "%m/%d/%Y %H:%M:%S", "%m/%d/%Y"):
                try:
                    found = datetime.datetime.strptime(text, pattern)
                    break
                except ValueError:
                    continue
        if found is None:
            raise ExecuteError(f"ERROR 000800: {tool}: {name} '{value}' is not a date.")
    if found.tzinfo is not None:
        found = found.astimezone(datetime.timezone.utc).replace(tzinfo=None)
    return found.strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3]


def links(fab, out_links, out_anchors, start, end, minimum, box):
    """EQUIVALENT (§5.4): each point's location at the From date and at the To date (or now) read from its class's
    history — branch versioned or archived (ADR-227) — and a link drawn from the first to the second where the point
    moved at least ``minimum``; an anchor at the first location for every other point. Points that did not exist at
    the From date have no original location and give neither."""
    from . import _attachments, _versions
    connection = _gpkg.connect(fab.file)
    try:
        if _versions.kind(connection, fab.points) is None:
            raise ExecuteError("ERROR 000800: GenerateParcelFabricLinks: the fabric's points keep no history; register "
                               "the fabric's feature dataset as versioned (RegisterAsVersioned) or enable archiving on "
                               "its points (EnableArchiving) before the edits whose links are wanted.")
        _, oid, geometry = _versions.columns(connection, fab.points)
        guid = _attachments.global_id_field(connection, fab.points)
        guid_sql = f'"{guid}"' if guid else "null"
        first = _versions.moment_at(connection, start)
        last = _versions.moment_at(connection, end) if end is not None else _versions.current_moment(connection)

        def state(moment):
            if moment <= 0:
                return {}
            sql = (f'select "{oid}", "{geometry}", {guid_sql} from '
                   f'{_versions.state_sql(connection, fab.points, 0, 0, until=moment)}')
            return {r[0]: (_gpkg.decode(r[1]), r[2]) for r in connection.execute(sql)}
        before, after = state(first), state(last)
    finally:
        connection.close()
    link_rows, anchor_rows = [], []
    for object_id, (shape, point_id) in sorted(before.items()):
        if shape is None or shape.is_empty or (box is not None and not shape.intersects(box)):
            continue
        now = after.get(object_id, (None, None))[0]
        moved = now.distance(shape) if now is not None and not now.is_empty else 0.0
        if moved > 0 and moved >= minimum:
            link_rows.append({"PointID": point_id, "shape": LineString([(shape.x, shape.y), (now.x, now.y)])})
        else:
            anchor_rows.append({"PointID": point_id, "shape": Point(shape.x, shape.y)})
    outputs = []
    for out, shape_type, written in ((out_links, "Polyline", link_rows), (out_anchors, "Point", anchor_rows)):
        out_file, out_table = _gpkg.split(out if ".gpkg" in out.lower() else out + ".gpkg")
        out_table = out_table or os.path.splitext(os.path.basename(out_file))[0]
        _gpkg.create(f"{out_file}/{out_table}", shape_type, fab.srid, [_gpkg.Column("PointID", "String")])
        pf.insert(out_file, out_table, written)
        outputs.append(f"{out_file}/{out_table}")
    AddMessage(f"{len(link_rows)} links and {len(anchor_rows)} anchor points generated.")
    return outputs
