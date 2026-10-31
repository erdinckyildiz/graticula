"""arcpy.locref — linear referencing (location referencing networks), by their documented names and parameters (ADR-252).

The model is ``grapy._lrs``: an LRS's Centerline, Centerline Sequence, Calibration Point and Redline classes, its LRS
Networks, events and intersections are ordinary grapy classes under the documented names and fields, and their
registration is grapy's own tables. Measures are ``grapy._lrs_measure``; routes ``grapy._lrs_routes``; events
``grapy._lrs_events``.

GenerateRoutes, GenerateCalibrationPoints, ApplyEventBehaviors, DeriveEventMeasures, OverlayEvents,
TranslateEventMeasures, CalculateIntersectingRouteMeasures, RemoveOverlappingCenterlines and ReverseLineOrders are
*equivalents* (ADR-224 §5.6): the pages name what they do but not how, and each stands on a published method it names.
Conflict prevention is refused by name where a tool would enable it: the pages say it applies only to branch versioned
data published as a feature service. Seven names are refused, each for its own reason (``_REFUSED``).
"""
from __future__ import annotations

import csv
import datetime
import math
import os
import shlex

import shapely

from . import _gpkg
from . import _lrs as L
from . import _lrs_events as E
from . import _lrs_measure as M
from . import _lrs_routes as R
from ._result import Result
from ._runtime import AddWarning, ExecuteError, NotBuiltError, equivalent

_REPORT = ("a report whose rows, columns and order no public page gives (the tool page names only the fields the user "
           "supplies, and the LRS data products page describes a designed product with headers, footers, notes and "
           "logos), so no output grapy wrote could be checked against Pro's")

# Each name not built is refused with a reason of its own (ADR-252); never a schedule.
_REFUSED: dict[str, str] = {
    "ConfigureExternalEventBehaviorsWithLRS": (
        "registers an external event so that an outside system can relocate it through the ArcGIS Server LRS "
        "service's Relocate Event REST operation, and its page requires the LRS to be in an enterprise geodatabase, "
        "which a GeoPackage store is not; a user tool also runs fenced, with no server (ADR-223 §5.3)"),
    "ConfigureExternalEventWithLRS": (
        "registers event data kept in an external system, and the page requires both the event store and the LRS to be "
        "enterprise geodatabases, which a GeoPackage store is not"),
    "ConfigureUtilityNetworkFeatureClass": (
        "is deprecated on its own page in favour of ConfigureUtilityNetworkFeatureClasses, which grapy builds, and "
        "requires a branch versioned enterprise geodatabase connection, which a GeoPackage store is not"),
    "GenerateLRFeatureCount": f"writes {_REPORT}",
    "GenerateLRLengthSummary": f"writes {_REPORT}",
    "GenerateLRRouteLog": f"writes {_REPORT}",
    "GenerateLrsDataProduct": (
        "reads an LRS data template .json written by Pro's Data Product Designer, whose schema Esri does not publish, "
        f"and writes {_REPORT}"),
}


def __getattr__(name):
    if name.startswith("_"):
        raise AttributeError(name)
    reason = _REFUSED.get(name)
    message = f"arcpy.locref.{name} {reason}." if reason else f"arcpy.locref.{name} is not built in grapy yet."

    def missing(*_, **__):
        raise NotBuiltError(message)

    return missing


# ---- shared ----------------------------------------------------------------------------------------------------------

_DATE, _DOUBLE, _TEXT, _SHORT, _LONG = "Date", "Double", "String", "SmallInteger", "Integer"
_NUMERIC = ("Double", "Single", "Integer", "SmallInteger")


def _rows(value):
    if value is None:
        return []
    if isinstance(value, (list, tuple)):
        return list(value)
    out, current, quote = [], "", None
    for c in str(value):
        if quote:
            current += c
            if c == quote:
                quote = None
        elif c in "'\"":
            quote = c
            current += c
        elif c == ";":
            out.append(current)
            current = ""
        else:
            current += c
    out.append(current)
    return [r.strip() for r in out if r.strip()]


def _cells(row):
    return [str(c) for c in row] if isinstance(row, (list, tuple)) else shlex.split(str(row))


def _workspace(in_workspace):
    """(file, feature dataset or None) of a GeoPackage or a feature dataset in one."""
    text = L.text(in_workspace)
    file, table = _gpkg.split(text)
    if not os.path.exists(file):
        raise ExecuteError(f"ERROR 000732: '{file}' does not exist.")
    return file, table


def _dataset_srid(connection, dataset):
    if not L.exists(connection, "grapy_feature_datasets"):
        return None
    row = connection.execute("select srs_id from grapy_feature_datasets where name = ?", (dataset,)).fetchone()
    return int(row[0]) if row else None


def _class(file, dataset, value, connection=None):
    """The table a class parameter names: a path, a layer, a name in the LRS's feature dataset, or one at the root."""
    from . import _layers
    if _layers.find(value) is not None or ".gpkg" in L.text(value).lower():
        f, table, _ = L.resolve(value)
        if os.path.normcase(os.path.abspath(f)) != os.path.normcase(os.path.abspath(file)):
            raise ExecuteError(f"ERROR 000800: '{value}' is not in the LRS's geodatabase.")
        return table
    name = L.text(value).replace("\\", "/")
    own = connection or L.connect(file)
    try:
        for candidate in ([f"{dataset}/{name}"] if dataset and "/" not in name else []) + [name]:
            if L.exists(own, candidate):
                return candidate
    finally:
        if connection is None:
            own.close()
    raise ExecuteError(f"ERROR 000732: '{value}' does not exist.")


def _lrs_named(connection, name):
    return L.lrs_get(connection, name)


def _network(value, tool):
    file, table, where = L.resolve(value)
    connection = L.connect(file)
    try:
        network = L.network_required(connection, table, tool)
        lrs = L.lrs_get(connection, network["lrs"])
    finally:
        connection.close()
    return file, lrs, network, where


def _event(value, tool):
    file, table, where = L.resolve(value)
    connection = L.connect(file)
    try:
        event = L.event_required(connection, table, tool)
        network = L.network_get(connection, event["network"])
        lrs = L.lrs_get(connection, network["lrs"])
    finally:
        connection.close()
    return file, lrs, network, event, where


def _new_class(file, table, shape, srid, columns, has_z, has_m):
    _gpkg.create(L.full(file, table), shape, srid, [_gpkg.Column(n, t) for n, t in columns], has_z, has_m)
    if "/" in table:
        from ._gdbtools import created_in_dataset
        created_in_dataset(L.full(file, table))


def _domain(file, table, field_name, domain):
    """A coded domain (made when missing) set on a field."""
    from . import management
    try:
        management.CreateDomain(file, domain, domain, "SHORT", "CODED")
    except ExecuteError:
        pass  # it is already there: one dLRSNetworks per geodatabase
    management.AssignDomainToField(L.full(file, table), field_name, domain)


def _tolerance_value(value, default):
    if not L.given(value):
        return default
    return float(str(value).split()[0])


def _details(tool, lines):
    return L.details_file(tool, lines)


# ---- creating and modifying an LRS ----------------------------------------------------------------------------------

def CreateLRS(in_workspace, lrs_name, centerline_feature_class_name, calibration_point_feature_class_name,
              redline_feature_class_name, centerline_sequence_table_name, spatial_reference, xy_tolerance=None,
              z_tolerance=None, xy_resolution=None, z_resolution=None):
    """An LRS and its minimum schema, as the data model pages give it: Centerline (polyline, z; CenterlineID),
    Calibration Point (point, z, not m; Measure, FromDate, ToDate, RouteID, NetworkID), Redline (polyline, z;
    FromMeasure, ToMeasure, RouteID, RouteName, EffectiveDate, ActivityType, NetworkID) and the Centerline Sequence
    table (CenterlineID, FromDate, ToDate, RouteID, NetworkID), NetworkID taking the ``dLRSNetworks`` domain and
    ActivityType ``dActivityType``. INFERRED (ADR-252 §5.9 (c)): given a GeoPackage, the classes go into a new feature
    dataset named after the LRS; given a feature dataset, its reference must be the one asked. Tolerances default to a
    millimetre and resolutions to a tenth of that; each tolerance must be at least twice its resolution."""
    from ._reference import reference_of
    from . import management
    file, dataset = _workspace(in_workspace)
    reference = reference_of(spatial_reference) if L.given(spatial_reference) else None
    srid = reference.factoryCode if reference else 0
    xy_tol = _tolerance_value(xy_tolerance, L.XY_TOLERANCE)
    xy_res = _tolerance_value(xy_resolution, L.XY_RESOLUTION)
    z_tol = _tolerance_value(z_tolerance, L.XY_TOLERANCE)
    z_res = _tolerance_value(z_resolution, L.XY_RESOLUTION)
    if xy_tol < 2 * xy_res or z_tol < 2 * z_res:
        raise ExecuteError("ERROR 000800: CreateLRS: a tolerance must be at least twice its resolution.")
    connection = L.connect(file)
    try:
        if str(lrs_name) in L.lrs_names(connection):
            raise ExecuteError(f"ERROR 000258: the LRS '{lrs_name}' already exists.")
        if dataset:
            own = _dataset_srid(connection, dataset)
            if own is None:
                raise ExecuteError(f"ERROR 000732: '{dataset}' is not a feature dataset.")
            if reference and own and own != srid:
                raise ExecuteError(f"ERROR 000622: the feature dataset is EPSG:{own}; the LRS asks EPSG:{srid}.")
            srid = srid or own
        names = [centerline_feature_class_name, calibration_point_feature_class_name, redline_feature_class_name,
                 centerline_sequence_table_name]
        if len({str(n).lower() for n in names}) != 4:
            raise ExecuteError("ERROR 000800: CreateLRS: the four names must be unique.")
        connection.commit()
    finally:
        connection.close()
    if not dataset:
        management.CreateFeatureDataset(file, str(lrs_name), srid or None)
        dataset = str(lrs_name)
    centerline = f"{dataset}/{centerline_feature_class_name}"
    calibration = f"{dataset}/{calibration_point_feature_class_name}"
    redline = f"{dataset}/{redline_feature_class_name}"
    sequence = str(centerline_sequence_table_name)
    connection = _gpkg.connect(file)
    try:
        for table in (centerline, calibration, redline, sequence):
            if L.exists(connection, table):
                raise ExecuteError(f"ERROR 000258: '{table}' already exists.")
    finally:
        connection.close()
    _new_class(file, centerline, "Polyline", srid, [("CenterlineID", "GUID")], True, False)
    _new_class(file, calibration, "Point", srid, [("Measure", _DOUBLE), ("FromDate", _DATE), ("ToDate", _DATE),
                                                  ("RouteID", _TEXT), ("NetworkID", _SHORT)], True, False)
    _new_class(file, redline, "Polyline", srid, [("FromMeasure", _DOUBLE), ("ToMeasure", _DOUBLE), ("RouteID", _TEXT),
                                                 ("RouteName", _TEXT), ("EffectiveDate", _DATE),
                                                 ("ActivityType", _SHORT), ("NetworkID", _SHORT)], True, False)
    _new_class(file, sequence, "Table", 0, [("CenterlineID", "GUID"), ("FromDate", _DATE), ("ToDate", _DATE),
                                            ("RouteID", _TEXT), ("NetworkID", _SHORT)], False, False)
    for table in (calibration, redline, sequence):
        _domain(file, table, "NetworkID", "dLRSNetworks")
    _domain(file, redline, "ActivityType", "dActivityType")
    settings = {"name": str(lrs_name), "dataset": dataset, "srid": srid, "centerline": centerline,
                "centerline_id": "CenterlineID", "sequence": sequence, "seq_centerline_id": "CenterlineID",
                "seq_route_id": "RouteID", "seq_from": "FromDate", "seq_to": "ToDate", "seq_network_id": "NetworkID",
                "calibration": calibration, "cp_measure": "Measure", "cp_from": "FromDate", "cp_to": "ToDate",
                "cp_route_id": "RouteID", "cp_network_id": "NetworkID", "redline": redline,
                "rl_from_measure": "FromMeasure", "rl_to_measure": "ToMeasure", "rl_route_id": "RouteID",
                "rl_route_name": "RouteName", "rl_effective_date": "EffectiveDate", "rl_activity_type": "ActivityType",
                "rl_network_id": "NetworkID", "xy_tolerance": xy_tol, "z_tolerance": z_tol, "xy_resolution": xy_res,
                "z_resolution": z_res, "conflict_prevention": "DISABLE"}
    connection = L.connect(file)
    try:
        L.lrs_put(connection, settings)
        connection.commit()
    finally:
        connection.close()
    return Result(f"{file}/{dataset}", L.full(file, centerline), L.full(file, calibration), L.full(file, redline),
                  L.full(file, sequence))


_LRS_ROLES = (
    # (parameter, settings key, role table key or None, accepted field types)
    ("centerline_centerline_id_field", "centerline_id", "centerline", (_TEXT,)),
    ("centerline_sequence_centerline_id_field", "seq_centerline_id", "sequence", (_TEXT,)),
    ("centerline_sequence_route_id_field", "seq_route_id", "sequence", (_TEXT,)),
    ("centerline_sequence_from_date_field", "seq_from", "sequence", (_DATE,)),
    ("centerline_sequence_to_date_field", "seq_to", "sequence", (_DATE,)),
    ("centerline_sequence_network_id_field", "seq_network_id", "sequence", (_SHORT,)),
    ("calibration_point_measure_field", "cp_measure", "calibration", (_DOUBLE,)),
    ("calibration_point_from_date_field", "cp_from", "calibration", (_DATE,)),
    ("calibration_point_to_date_field", "cp_to", "calibration", (_DATE,)),
    ("calibration_point_route_id_field", "cp_route_id", "calibration", (_TEXT,)),
    ("calibration_point_network_id_field", "cp_network_id", "calibration", (_SHORT,)),
    ("redline_from_measure_field", "rl_from_measure", "redline", (_DOUBLE,)),
    ("redline_to_measure_field", "rl_to_measure", "redline", (_DOUBLE,)),
    ("redline_route_id_field", "rl_route_id", "redline", (_TEXT,)),
    ("redline_route_name_field", "rl_route_name", "redline", (_TEXT,)),
    ("redline_effective_date_field", "rl_effective_date", "redline", (_DATE,)),
    ("redline_activity_type_field", "rl_activity_type", "redline", (_SHORT,)),
    ("redline_network_id_field", "rl_network_id", "redline", (_SHORT,)),
)
_LRS_CLASSES = (("centerline_feature_class", "centerline", "Polyline"),
                ("centerline_sequence_table", "sequence", None),
                ("calibration_point_feature_class", "calibration", "Point"),
                ("redline_feature_class", "redline", "Polyline"))


def _check_lrs_classes(connection, settings, empty, tool):
    """The role classes' shapes, z, feature dataset and reference, as CreateLRSFromExistingDataset's page requires: one
    feature dataset, polyline or point with z, sharing one reference; empty when ``empty``."""
    datasets = set()
    for _, key, shape in _LRS_CLASSES:
        table = settings[key]
        kind, srid, has_z, has_m = L.shape_info(connection, table)
        if shape is None:
            if kind is not None:
                raise ExecuteError(f"ERROR 000800: {tool}: '{table}' must be a table.")
        else:
            if kind != shape:
                raise ExecuteError(f"ERROR 000800: {tool}: '{table}' must be a {shape.lower()} feature class.")
            if not has_z:
                raise ExecuteError(f"ERROR 000800: {tool}: '{table}' must be z-enabled.")
            if "/" not in table:
                raise ExecuteError(f"ERROR 000800: {tool}: '{table}' must be in a feature dataset.")
            datasets.add(table.rsplit("/", 1)[0])
        if empty and L.count(connection, table):
            raise ExecuteError(f"ERROR 000800: {tool}: '{table}' must be empty.")
        other = L.lrs_of_class(connection, table)
        if other is not None and other["name"] != settings.get("name"):
            raise ExecuteError(f"ERROR 000800: {tool}: '{table}' is used by the LRS '{other['name']}'.")
    if len(datasets) != 1:
        raise ExecuteError(f"ERROR 000800: {tool}: the feature classes must be in one feature dataset.")
    return datasets.pop()


def CreateLRSFromExistingDataset(lrs_name, centerline_feature_class, centerline_centerline_id_field,
                                 centerline_sequence_table, centerline_sequence_centerline_id_field,
                                 centerline_sequence_route_id_field, centerline_sequence_from_date_field,
                                 centerline_sequence_to_date_field, centerline_sequence_network_id_field,
                                 calibration_point_feature_class, calibration_point_measure_field,
                                 calibration_point_from_date_field, calibration_point_to_date_field,
                                 calibration_point_route_id_field, calibration_point_network_id_field,
                                 redline_feature_class, redline_from_measure_field, redline_to_measure_field,
                                 redline_route_id_field, redline_route_name_field, redline_effective_date_field,
                                 redline_activity_type_field, redline_network_id_field):
    """An LRS registered on existing, empty classes in one geodatabase and feature dataset, their fields of the data
    model's types (a GUID is a text field in a GeoPackage). Calibration points may carry m here, as this page allows
    (INFERRED, §5.9 (c): the data model page says they cannot; registering tolerates it, creating follows the model)."""
    given = dict(locals())
    file, first, _ = L.resolve(centerline_feature_class)
    connection = L.connect(file)
    try:
        if str(lrs_name) in L.lrs_names(connection):
            raise ExecuteError(f"ERROR 000258: the LRS '{lrs_name}' already exists.")
        settings = {"name": str(lrs_name)}
        for parameter, key, _ in _LRS_CLASSES:
            f, table, _ = L.resolve(given[parameter])
            if os.path.normcase(os.path.abspath(f)) != os.path.normcase(os.path.abspath(file)):
                raise ExecuteError("ERROR 000800: CreateLRSFromExistingDataset: the inputs must be in one geodatabase.")
            settings[key] = table
        dataset = _check_lrs_classes(connection, settings, True, "CreateLRSFromExistingDataset")
        srids = {L.shape_info(connection, settings[k])[1] for k in ("centerline", "calibration", "redline")}
        if len(srids) != 1:
            raise ExecuteError("ERROR 000800: CreateLRSFromExistingDataset: the classes must share one spatial "
                               "reference.")
        for parameter, key, role, kinds in _LRS_ROLES:
            settings[key] = L.field(connection, settings[role], given[parameter], kinds, parameter)
        settings.update(dataset=dataset, srid=srids.pop(), xy_tolerance=L.XY_TOLERANCE, z_tolerance=L.XY_TOLERANCE,
                        xy_resolution=L.XY_RESOLUTION, z_resolution=L.XY_RESOLUTION, conflict_prevention="DISABLE")
        L.lrs_put(connection, settings)
        connection.commit()
    finally:
        connection.close()
    return Result(file)


def ModifyLRS(in_workspace, current_lrs_name, new_lrs_name=None, centerline_feature_class=None,
              centerline_centerline_id_field=None, centerline_sequence_table=None,
              centerline_sequence_centerline_id_field=None, centerline_sequence_route_id_field=None,
              centerline_sequence_from_date_field=None, centerline_sequence_to_date_field=None,
              centerline_sequence_network_id_field=None, calibration_point_feature_class=None,
              calibration_point_measure_field=None, calibration_point_from_date_field=None,
              calibration_point_to_date_field=None, calibration_point_route_id_field=None,
              calibration_point_network_id_field=None, redline_feature_class=None, redline_from_measure_field=None,
              redline_to_measure_field=None, redline_route_id_field=None, redline_route_name_field=None,
              redline_effective_date_field=None, redline_activity_type_field=None, redline_network_id_field=None,
              conflict_prevention="AS_IS", move_to_feature_dataset="DO_NOT_MOVE"):
    """An LRS renamed, or a role remapped to another class or field (each given one; the rest kept). As the page says,
    route and centerline IDs and network IDs are not checked. ``conflict_prevention`` ENABLE is refused: the page says
    conflict prevention is available only on branch versioned data, which a GeoPackage store is not. MOVE renames a
    remapped class into the LRS's feature dataset."""
    given = dict(locals())
    prevention = L.choose("ModifyLRS", "conflict_prevention", conflict_prevention, ("AS_IS", "ENABLE", "DISABLE"),
                          "AS_IS")
    if prevention == "ENABLE":
        raise NotBuiltError("arcpy.locref.ModifyLRS: conflict_prevention ENABLE is not built in grapy: the page says "
                            "conflict prevention is available only when editing or geoprocessing branch versioned "
                            "data, and a GeoPackage store holds none.")
    move = L.choose("ModifyLRS", "move_to_feature_dataset", move_to_feature_dataset, ("MOVE", "DO_NOT_MOVE"),
                    "DO_NOT_MOVE") == "MOVE"
    file, _ = _workspace(in_workspace)
    connection = L.connect(file)
    try:
        settings = L.lrs_get(connection, current_lrs_name)
        old_name = settings["name"]
        for parameter, key, _ in _LRS_CLASSES:
            if L.given(given[parameter]):
                table = _class(file, settings["dataset"], given[parameter], connection)
                settings[key] = table
        connection.commit()
    finally:
        connection.close()
    if move:
        from . import management
        for _, key, shape in _LRS_CLASSES:
            table = settings[key]
            if shape and table.rsplit("/", 1)[0] != settings["dataset"]:
                target = f"{settings['dataset']}/{table.rsplit('/', 1)[-1]}"
                management.Rename(L.full(file, table), L.full(file, target))
                settings[key] = target
    connection = L.connect(file)
    try:
        _check_lrs_classes(connection, settings, False, "ModifyLRS")
        for parameter, key, role, kinds in _LRS_ROLES:
            if L.given(given[parameter]):
                settings[key] = L.field(connection, settings[role], given[parameter], kinds, parameter)
            else:
                settings[key] = L.field(connection, settings[role], settings[key], kinds, parameter)
        if prevention == "DISABLE":
            settings["conflict_prevention"] = "DISABLE"
        if L.given(new_lrs_name) and str(new_lrs_name) != old_name:
            if str(new_lrs_name) in L.lrs_names(connection):
                raise ExecuteError(f"ERROR 000258: the LRS '{new_lrs_name}' already exists.")
            connection.execute("delete from grapy_lrs where name = ?", (old_name,))
            connection.execute("update grapy_lrs_network set lrs = ? where lrs = ?", (str(new_lrs_name), old_name))
            settings["name"] = str(new_lrs_name)
        L.lrs_put(connection, settings)
        connection.commit()
    finally:
        connection.close()
    return Result(L.text(in_workspace))


# ---- networks --------------------------------------------------------------------------------------------------------

def _network_settings(table, lrs, network_id, route_id, route_name, from_date, to_date, line, line_id, line_name,
                      line_order, derived_from, unit, configuration, individual):
    return {"table": table, "lrs": lrs, "network_id": network_id, "route_id": route_id, "route_name": route_name,
            "from_date": from_date, "to_date": to_date, "line": line, "line_id": line_id, "line_name": line_name,
            "line_order": line_order, "derived_from": derived_from, "derived": None, "unit": unit,
            "route_id_configuration": configuration, "individual_fields": individual,
            "calibration_rule": "STEPPING_INCREMENT", "calibration_offset": 0.0, "update_measure_cartorealign": False,
            "padding": {}, "lookups": {}, "dominance": []}


def _next_network_id(connection, lrs):
    # one dLRSNetworks domain per geodatabase, so a network ID is unique in it (INFERRED, §5.9 (a))
    used = [r[0] for r in connection.execute("select network_id from grapy_lrs_network")]
    return max(used, default=0) + 1


def _register_network(file, settings, network_name):
    from . import management
    connection = L.connect(file)
    try:
        if settings.get("derived_from"):
            parent = L.network_get(connection, settings["derived_from"])
            parent["derived"] = settings["table"]
            L.network_put(connection, parent)
        L.network_put(connection, settings)
        connection.commit()
    finally:
        connection.close()
    try:
        management.CreateDomain(file, "dLRSNetworks", "dLRSNetworks", "SHORT", "CODED")
    except ExecuteError:
        pass  # CreateLRS made it
    management.AddCodedValueToDomain(file, "dLRSNetworks", settings["network_id"], str(network_name))


def _line_network(connection, lrs, name, tool):
    for network in L.networks(connection, lrs["name"]):
        if network["table"].rsplit("/", 1)[-1].lower() == str(name).lower():
            if not network.get("line"):
                raise ExecuteError(f"ERROR 000800: {tool}: '{name}' is not a line network.")
            if network.get("derived"):
                raise ExecuteError(f"ERROR 000800: {tool}: the line network '{name}' already has a derived network.")
            return network["table"]
    raise ExecuteError(f"ERROR 000732: {tool}: there is no line network '{name}' in the LRS.")


def CreateLRSNetwork(in_path, lrs_name, network_name, route_id_field, route_name_field, from_date_field, to_date_field,
                     derive_from_line_network="DO_NOT_DERIVE", line_network_name=None,
                     include_fields_to_support_lines="DO_NOT_INCLUDE", line_id_field=None, line_name_field=None,
                     line_order_field=None, measure_unit="MILES"):
    """An empty LRS Network: a polyline class with z and m in the LRS's feature dataset (INFERRED, §5.9 (c): the page
    does not say; its routes carry measures and the centerlines' z), with the route ID (text), route name (text), from
    and to date fields and, with INCLUDE, a line network's LineID (text), LineName (text) and LineOrder (long). DERIVE
    makes it the derived network of ``line_network_name``. Its network ID is the next free code of ``dLRSNetworks``.
    INFERRED (§5.9 (g)): a network made here takes its route IDs from the loaded data (single field)."""
    tool = "CreateLRSNetwork"
    file, _ = _workspace(in_path)
    L.name_check(network_name, tool)
    unit = L.choose(tool, "measure_unit", measure_unit, tuple(L.UNITS), "MILES")
    derive = L.choose(tool, "derive_from_line_network", derive_from_line_network, ("DERIVE", "DO_NOT_DERIVE"),
                      "DO_NOT_DERIVE") == "DERIVE"
    line = L.choose(tool, "include_fields_to_support_lines", include_fields_to_support_lines,
                    ("INCLUDE", "DO_NOT_INCLUDE"), "DO_NOT_INCLUDE") == "INCLUDE"
    if derive and line:
        raise ExecuteError(f"ERROR 000800: {tool}: a derived network is not itself a line network.")
    names = [n for n in (route_id_field, route_name_field, from_date_field, to_date_field) if L.given(n)]
    if line:
        if not all(L.given(n) for n in (line_id_field, line_name_field, line_order_field)):
            raise ExecuteError(f"ERROR 000735: {tool}: a line network needs its line ID, name and order fields.")
        names += [line_id_field, line_name_field, line_order_field]
    if len({str(n).lower() for n in names}) != len(names):
        raise ExecuteError(f"ERROR 000800: {tool}: a field maps to one role only.")
    connection = L.connect(file)
    try:
        lrs = L.lrs_get(connection, lrs_name)
        table = f"{lrs['dataset']}/{network_name}"
        if L.exists(connection, table):
            raise ExecuteError(f"ERROR 000258: '{table}' already exists.")
        parent = _line_network(connection, lrs, line_network_name, tool) if derive else None
        network_id = _next_network_id(connection, lrs["name"])
        connection.commit()
    finally:
        connection.close()
    columns = [(route_id_field, _TEXT)] + ([(route_name_field, _TEXT)] if L.given(route_name_field) else []) + \
        [(from_date_field, _DATE), (to_date_field, _DATE)]
    if line:
        columns += [(line_id_field, _TEXT), (line_name_field, _TEXT), (line_order_field, _LONG)]
    _new_class(file, table, "Polyline", lrs["srid"], [(str(n), t) for n, t in columns], True, True)
    settings = _network_settings(table, lrs["name"], network_id, str(route_id_field),
                                 str(route_name_field) if L.given(route_name_field) else None, str(from_date_field),
                                 str(to_date_field), line, str(line_id_field) if line else None,
                                 str(line_name_field) if line else None, str(line_order_field) if line else None,
                                 parent, unit, "SINGLE_FIELD_ROUTE_ID", [])
    _register_network(file, settings, network_name)
    return Result(L.full(file, table))


def _unit_of(srid):
    """A network's measure unit from its reference's linear unit (INFERRED, §5.9 (b): the page derives it from the
    XY:M tolerance ratio, and a GeoPackage keeps no m tolerance, so the ratio is 1)."""
    from ._reference import SpatialReference
    metres = SpatialReference(int(srid)).metersPerUnit if srid and int(srid) > 0 else 1.0
    for name, size in L.UNITS.items():
        if abs(size - metres) <= 1e-12 * max(1.0, size):
            return name
    return "METERS"


def CreateLRSNetworkFromExistingDataset(in_feature_class, lrs_name, route_id_field, route_name_field=None,
                                        from_date_field=None, to_date_field=None,
                                        derive_from_line_network="DO_NOT_DERIVE", line_network_name=None,
                                        include_fields_to_support_lines="DO_NOT_INCLUDE", line_id_field=None,
                                        line_name_field=None, line_order_field=None,
                                        route_id_configuration="AUTOGENERATED_ROUTE_ID",
                                        individual_route_id_fields=None):
    """An existing, empty polyline class in the LRS's feature dataset registered as a network; the data is not
    altered. AUTOGENERATED_ROUTE_ID gives a loaded route with no ID a GUID; MULTI_FIELD_ROUTE_ID composes it from
    ``individual_route_id_fields`` (text, short or long), which, like SINGLE_FIELD, the page allows only on a network
    that is not a line network."""
    tool = "CreateLRSNetworkFromExistingDataset"
    if not (L.given(from_date_field) and L.given(to_date_field)):
        raise ExecuteError(f"ERROR 000735: {tool}: from_date_field and to_date_field are required.")
    configuration = L.choose(tool, "route_id_configuration", route_id_configuration,
                             ("AUTOGENERATED_ROUTE_ID", "SINGLE_FIELD_ROUTE_ID", "MULTI_FIELD_ROUTE_ID"),
                             "AUTOGENERATED_ROUTE_ID")
    derive = L.choose(tool, "derive_from_line_network", derive_from_line_network, ("DERIVE", "DO_NOT_DERIVE"),
                      "DO_NOT_DERIVE") == "DERIVE"
    line = L.choose(tool, "include_fields_to_support_lines", include_fields_to_support_lines,
                    ("INCLUDE", "DO_NOT_INCLUDE"), "DO_NOT_INCLUDE") == "INCLUDE"
    if line and configuration != "AUTOGENERATED_ROUTE_ID":
        raise ExecuteError(f"ERROR 000800: {tool}: single and multiple field route IDs apply to networks that are not "
                           "line networks.")
    file, table, _ = L.resolve(in_feature_class)
    L.name_check(table.rsplit("/", 1)[-1], tool)
    connection = L.connect(file)
    try:
        lrs = L.lrs_get(connection, lrs_name)
        kind, srid, _, _ = L.shape_info(connection, table)
        if kind != "Polyline":
            raise ExecuteError(f"ERROR 000800: {tool}: the input must be a polyline feature class.")
        if "/" not in table or table.rsplit("/", 1)[0] != lrs["dataset"]:
            raise ExecuteError(f"ERROR 000800: {tool}: the input must be in the LRS feature dataset.")
        if L.count(connection, table):
            raise ExecuteError(f"ERROR 000800: {tool}: the input must be empty.")
        if L.registered_anywhere(connection, table):
            raise ExecuteError(f"ERROR 000800: {tool}: the input is already registered with an LRS.")
        f = lambda n, kinds, role: L.field(connection, table, n, kinds, role) if L.given(n) else None  # noqa: E731
        route_id = f(route_id_field, (_TEXT,), "route ID field")
        route_name = f(route_name_field, (_TEXT,), "route name field")
        start, end = f(from_date_field, (_DATE,), "from date field"), f(to_date_field, (_DATE,), "to date field")
        line_fields = (None, None, None)
        if line:
            line_fields = (f(line_id_field, (_TEXT,), "line ID field"), f(line_name_field, (_TEXT,), "line name field"),
                           f(line_order_field, (_LONG, _SHORT), "line order field"))
            if None in line_fields:
                raise ExecuteError(f"ERROR 000735: {tool}: a line network needs its line ID, name and order fields.")
        individual = []
        if configuration == "MULTI_FIELD_ROUTE_ID":
            individual = [L.field(connection, table, n, (_TEXT, _SHORT, _LONG), "route ID part")
                          for n in _rows(individual_route_id_fields)]
            if not individual:
                raise ExecuteError(f"ERROR 000735: {tool}: MULTI_FIELD_ROUTE_ID needs individual_route_id_fields.")
        parent = _line_network(connection, lrs, line_network_name, tool) if derive else None
        network_id = _next_network_id(connection, lrs["name"])
        connection.commit()
    finally:
        connection.close()
    settings = _network_settings(table, lrs["name"], network_id, route_id, route_name, start, end, line, *line_fields,
                                 parent, _unit_of(srid), configuration, individual)
    _register_network(file, settings, table.rsplit("/", 1)[-1])
    return Result(L.full(file, table))


def ModifyLRSNetwork(in_feature_class, route_id_field=None, route_name_field=None, from_date_field=None,
                     to_date_field=None, derive_from_line_network="AS_IS", line_network_name=None,
                     include_fields_to_support_lines="AS_IS", line_id_field=None, line_name_field=None,
                     line_order_field=None, route_id_configuration="AS_IS", individual_route_id_fields=None):
    """A network's registration changed; its data is not, and nothing is deleted. A newly named field must exist."""
    tool = "ModifyLRSNetwork"
    file, table, _ = L.resolve(in_feature_class)
    connection = L.connect(file)
    try:
        network = L.network_required(connection, table, tool)
        lrs = L.lrs_get(connection, network["lrs"])
        for parameter, key, kinds in ((route_id_field, "route_id", (_TEXT,)), (route_name_field, "route_name", (_TEXT,)),
                                      (from_date_field, "from_date", (_DATE,)), (to_date_field, "to_date", (_DATE,)),
                                      (line_id_field, "line_id", (_TEXT,)), (line_name_field, "line_name", (_TEXT,)),
                                      (line_order_field, "line_order", (_LONG, _SHORT))):
            if L.given(parameter):
                network[key] = L.field(connection, table, parameter, kinds, key)
        include = L.choose(tool, "include_fields_to_support_lines", include_fields_to_support_lines,
                           ("AS_IS", "INCLUDE", "DO_NOT_INCLUDE"), "AS_IS")
        if include != "AS_IS":
            network["line"] = include == "INCLUDE"
            if network["line"] and not all(network.get(k) for k in ("line_id", "line_name", "line_order")):
                raise ExecuteError(f"ERROR 000735: {tool}: a line network needs its line ID, name and order fields.")
        derive = L.choose(tool, "derive_from_line_network", derive_from_line_network, ("AS_IS", "DERIVE",
                                                                                         "DO_NOT_DERIVE"), "AS_IS")
        if derive == "DERIVE":
            parent = _line_network(connection, lrs, line_network_name, tool)
            network["derived_from"] = parent
            owner = L.network_get(connection, parent)
            owner["derived"] = table
            L.network_put(connection, owner)
        elif derive == "DO_NOT_DERIVE" and network.get("derived_from"):
            owner = L.network_get(connection, network["derived_from"])
            owner["derived"] = None
            L.network_put(connection, owner)
            network["derived_from"] = None
        configuration = L.choose(tool, "route_id_configuration", route_id_configuration,
                                 ("AS_IS", "AUTOGENERATED_ROUTE_ID", "SINGLE_FIELD_ROUTE_ID", "MULTI_FIELD_ROUTE_ID"),
                                 "AS_IS")
        if configuration != "AS_IS":
            if network.get("line") and configuration != "AUTOGENERATED_ROUTE_ID":
                raise ExecuteError(f"ERROR 000800: {tool}: single and multiple field route IDs apply to networks that "
                                   "are not line networks.")
            network["route_id_configuration"] = configuration
            if configuration == "MULTI_FIELD_ROUTE_ID":
                network["individual_fields"] = [L.field(connection, table, n, (_TEXT, _SHORT, _LONG), "route ID part")
                                                for n in _rows(individual_route_id_fields)]
                if not network["individual_fields"]:
                    raise ExecuteError(f"ERROR 000735: {tool}: MULTI_FIELD_ROUTE_ID needs individual_route_id_fields.")
        L.network_put(connection, network)
        connection.commit()
    finally:
        connection.close()
    return Result(L.full(file, table))


def ModifyNetworkCalibrationRules(in_feature_class, calibration_rule="AS_IS", calibration_offset=None,
                                  update_measure_cartorealign="AS_IS"):
    """How a gap is measured when routes are calibrated afterwards (GenerateCalibrationPoints), and whether a
    cartographic realignment updates measures. The offset is 0 or more, and either 0 or greater than the m tolerance
    (INFERRED: the XY tolerance in measure units, a GeoPackage keeping no m tolerance); a line network allows only 0
    with the increment rules."""
    tool = "ModifyNetworkCalibrationRules"
    rule = L.choose(tool, "calibration_rule", calibration_rule,
                    ("AS_IS", "ADDING_EUCLIDEAN_DISTANCE", "STEPPING_INCREMENT", "ADDING_INCREMENT"), "AS_IS")
    carto = L.choose(tool, "update_measure_cartorealign", update_measure_cartorealign, ("AS_IS", "ENABLE", "DISABLE"),
                     "AS_IS")
    file, table, _ = L.resolve(in_feature_class)
    connection = L.connect(file)
    try:
        network = L.network_required(connection, table, tool)
        lrs = L.lrs_get(connection, network["lrs"])
        if rule != "AS_IS":
            network["calibration_rule"] = rule
        if L.given(calibration_offset):
            network["calibration_offset"] = float(calibration_offset)
        elif rule in ("STEPPING_INCREMENT", "ADDING_INCREMENT"):
            raise ExecuteError(f"ERROR 000735: {tool}: calibration_offset is required with {rule}.")
        offset = float(network.get("calibration_offset") or 0.0)
        m_tolerance = L.tolerance(lrs) * L.unit_ratio(lrs["srid"], network["unit"])
        if offset < 0 or (0 < offset <= m_tolerance):
            raise ExecuteError(f"ERROR 000800: {tool}: the offset is 0 or greater than the m tolerance ({m_tolerance}).")
        if network.get("line") and offset and network["calibration_rule"] != "ADDING_EUCLIDEAN_DISTANCE":
            raise ExecuteError(f"ERROR 000800: {tool}: a line network allows only an offset of 0.")
        if carto != "AS_IS":
            network["update_measure_cartorealign"] = carto == "ENABLE"
        L.network_put(connection, network)
        connection.commit()
    finally:
        connection.close()
    return Result(L.full(file, table))


def ModifyRouteIdPadding(in_feature_class, route_id_padding):
    """Padding of a multiple field route ID's parts, one row per field: ``Field Length VariableLength EnablePadding
    PaddingCharacter PaddingLocation PadIfNull AllowNull``. Pad if Null needs Allow Null; once a field allows null,
    every field after it must; the lengths add up to no more than the route ID's length (INFERRED, §5.9 (g): 255, the
    data model's suggestion, a GeoPackage text field having none)."""
    tool = "ModifyRouteIdPadding"
    file, table, _ = L.resolve(in_feature_class)
    connection = L.connect(file)
    try:
        network = L.network_required(connection, table, tool)
        if network.get("route_id_configuration") != "MULTI_FIELD_ROUTE_ID":
            raise ExecuteError(f"ERROR 000800: {tool}: the network does not use a multiple field route ID.")
        padding = {}
        for row in _rows(route_id_padding):
            cells = _cells(row)
            if len(cells) != 8:
                raise ExecuteError(f"ERROR 000800: {tool}: '{row}' does not have the eight columns.")
            name = L.field(connection, table, cells[0])
            flag = lambda v: str(v).strip().upper() in ("TRUE", "1", "YES")  # noqa: E731
            location = cells[5].upper()
            if location not in ("LEFT", "RIGHT", "LEFT_AND_RIGHT"):
                raise ExecuteError(f"ERROR 000800: {tool}: padding location '{cells[5]}'.")
            character = cells[4] if cells[4] != "#" else " "
            if len(character) != 1:
                raise ExecuteError(f"ERROR 000800: {tool}: the padding character is one character.")
            rule = {"length": int(cells[1]), "variable": flag(cells[2]), "enabled": flag(cells[3]),
                    "character": character, "location": location, "pad_if_null": flag(cells[6]),
                    "allow_null": flag(cells[7])}
            if not 1 <= rule["length"] <= 255:
                raise ExecuteError(f"ERROR 000800: {tool}: '{name}' length must be 1 to 255.")
            if rule["pad_if_null"] and not rule["allow_null"]:
                raise ExecuteError(f"ERROR 000800: {tool}: Pad if Null on '{name}' needs Allow Null.")
            padding[name.lower()] = rule
        seen_null = False
        for name in network["individual_fields"]:
            rule = padding.get(name.lower())
            if rule and rule["allow_null"]:
                seen_null = True
            elif seen_null:
                raise ExecuteError(f"ERROR 000800: {tool}: every field after one that allows null must allow null.")
        if sum(r["length"] for r in padding.values()) > 255:
            raise ExecuteError(f"ERROR 000800: {tool}: the lengths add up to more than the route ID's 255.")
        network["padding"] = padding
        L.network_put(connection, network)
        connection.commit()
    finally:
        connection.close()
    return Result(L.full(file, table))


def ConfigureLookupTable(in_feature_class, lookup_table, field_applied_to, lookup_key, lookup_display=None,
                         allow_any_lookup_value="DO_NOT_ALLOW_ANY_VALUE"):
    """A lookup table for one part of a multiple field route ID; its key has the part's type. The tool adds no rows.
    INFERRED (§5.9 (g)): the lookup is enforced when AppendRoutes composes a route ID."""
    tool = "ConfigureLookupTable"
    allow = L.choose(tool, "allow_any_lookup_value", allow_any_lookup_value,
                     ("ALLOW_ANY_VALUE", "DO_NOT_ALLOW_ANY_VALUE"), "DO_NOT_ALLOW_ANY_VALUE") == "ALLOW_ANY_VALUE"
    if allow and L.given(lookup_display):
        raise ExecuteError(f"ERROR 000800: {tool}: lookup_display is not allowed with ALLOW_ANY_VALUE.")
    file, table, _ = L.resolve(in_feature_class)
    f2, lookup, _ = L.resolve(lookup_table)
    connection = L.connect(file)
    try:
        network = L.network_required(connection, table, tool)
        if network.get("route_id_configuration") != "MULTI_FIELD_ROUTE_ID":
            raise ExecuteError(f"ERROR 000800: {tool}: the network does not use a multiple field route ID.")
        part = L.field(connection, table, field_applied_to)
        if part.lower() not in [n.lower() for n in network["individual_fields"]]:
            raise ExecuteError(f"ERROR 000800: {tool}: '{part}' is not a route ID part.")
        kind = L.columns(connection, table)[part.lower()].type
        other = L.connect(f2)
        try:
            key = L.field(other, lookup, lookup_key, (kind,), "lookup key")
            display = L.field(other, lookup, lookup_display) if L.given(lookup_display) else None
        finally:
            other.close()
        network["lookups"][part.lower()] = {"file": f2, "table": lookup, "key": key, "display": display,
                                            "allow_any": allow}
        L.network_put(connection, network)
        connection.commit()
    finally:
        connection.close()
    return Result(L.full(file, table))


def ConfigureRouteDominanceRules(in_feature_class, configure_type, rule_name, updated_rule_name=None,
                                 source_table_name=None, fields=None, order_method="LESSER", order_type="ALPHANUMERIC",
                                 prioritized_exceptions=None):
    """A route dominance rule added, updated or deleted. The source is the network or a line event on it that does
    not span routes; several fields are joined in the order given; exceptions are checked first, in their order."""
    tool = "ConfigureRouteDominanceRules"
    kind = L.choose(tool, "configure_type", configure_type, ("ADD", "UPDATE", "DELETE"), "ADD")
    method = L.choose(tool, "order_method", order_method, ("LESSER", "GREATER"), "LESSER")
    order = L.choose(tool, "order_type", order_type, ("ALPHANUMERIC", "NUMERIC"), "ALPHANUMERIC")
    if len(str(rule_name)) > 30 or (L.given(updated_rule_name) and len(str(updated_rule_name)) > 30):
        raise ExecuteError(f"ERROR 000800: {tool}: a rule name is at most 30 characters.")
    file, table, _ = L.resolve(in_feature_class)
    connection = L.connect(file)
    try:
        network = L.network_required(connection, table, tool)
        lrs = L.lrs_get(connection, network["lrs"])
        rules = network.get("dominance") or []
        index = next((i for i, r in enumerate(rules) if r["name"].lower() == str(rule_name).lower()), None)
        if kind == "DELETE":
            if index is None:
                raise ExecuteError(f"ERROR 000800: {tool}: there is no rule '{rule_name}'.")
            del rules[index]
        else:
            if kind == "ADD" and index is not None:
                raise ExecuteError(f"ERROR 000800: {tool}: the rule '{rule_name}' already exists.")
            if kind == "UPDATE" and index is None:
                raise ExecuteError(f"ERROR 000800: {tool}: there is no rule '{rule_name}'.")
            source = table
            if L.given(source_table_name):
                source = _class(file, lrs["dataset"], source_table_name, connection)
                if source != table:
                    event = L.event_get(connection, source)
                    if event is None or event["network"] != table or event.get("kind") != "LINE" or event.get("spans"):
                        raise ExecuteError(f"ERROR 000800: {tool}: the source is the network or a line event on it "
                                           "that does not span routes.")
            elif kind == "UPDATE":
                source = rules[index]["source"]
            names = [L.field(connection, source, n) for n in _rows(fields)] if L.given(fields) else \
                (rules[index]["fields"] if kind == "UPDATE" else [])
            if not names:
                raise ExecuteError(f"ERROR 000735: {tool}: fields are required.")
            rule = {"name": str(updated_rule_name) if L.given(updated_rule_name) else str(rule_name), "source": source,
                    "fields": names, "order_method": method, "order_type": order,
                    "exceptions": [e.strip() for e in str(prioritized_exceptions).split(",") if e.strip()]
                    if L.given(prioritized_exceptions) else []}
            if kind == "ADD":
                rules.append(rule)
            else:
                rules[index] = rule
        network["dominance"] = rules
        L.network_put(connection, network)
        connection.commit()
    finally:
        connection.close()
    return Result(L.full(file, table))


# ---- events ----------------------------------------------------------------------------------------------------------

def _event_settings(table, network, kind, event_id, route_id, from_date, to_date, loc_error, measure, to_measure, spans,
                    to_route_id, store, route_name, to_route_name):
    return {"table": table, "network": network, "kind": kind, "event_id": event_id, "route_id": route_id,
            "from_date": from_date, "to_date": to_date, "loc_error": loc_error, "measure": measure,
            "to_measure": to_measure, "spans": spans, "to_route_id": to_route_id, "store_route_name": store,
            "route_name": route_name, "to_route_name": to_route_name, "rules": dict(L.DEFAULT_RULES),
            "referent": None, "stationing": None, "derived": None}


def _event_checks(tool, network, kind, spans, store, to_measure, to_route_id, route_name, to_route_name):
    if network.get("derived_from"):
        raise ExecuteError(f"ERROR 000800: {tool}: events cannot be registered to a derived network.")
    if kind == "LINE" and not L.given(to_measure):
        raise ExecuteError(f"ERROR 000735: {tool}: a line event needs to_measure_field.")
    if spans:
        if kind != "LINE" or not network.get("line"):
            raise ExecuteError(f"ERROR 000800: {tool}: only line events on a line network span routes.")
        if not L.given(to_route_id):
            raise ExecuteError(f"ERROR 000735: {tool}: a spanning event needs to_route_id_field.")
    if store:
        if not network.get("route_name"):
            raise ExecuteError(f"ERROR 000800: {tool}: the parent network stores no route name.")
        if not L.given(route_name) or (spans and not L.given(to_route_name)):
            raise ExecuteError(f"ERROR 000735: {tool}: storing route names needs the route name fields.")


def CreateLRSEvent(parent_network, event_name, geometry_type="POINT", event_id_field=None, route_id_field=None,
                   from_date_field=None, to_date_field=None, loc_error_field=None, measure_field=None,
                   to_measure_field=None, event_spans_routes="NO_SPANS_ROUTES", to_route_id_field=None,
                   store_route_name="NO_STORE_ROUTE_NAME", route_name_field=None, to_route_name_field=None):
    """A new event feature class, registered on its network, with the events data model's fields: event ID, route ID
    and route name (text), from and to dates, LocError (text) and measures (double; INFERRED, §5.9 (c): the page allows
    any numeric type). INFERRED: it is made in the LRS's feature dataset, with z and m. The behaviour rules start at
    the documented defaults."""
    tool = "CreateLRSEvent"
    kind = L.choose(tool, "geometry_type", geometry_type, ("POINT", "LINE"), "POINT")
    spans = L.choose(tool, "event_spans_routes", event_spans_routes, ("SPANS_ROUTES", "NO_SPANS_ROUTES"),
                     "NO_SPANS_ROUTES") == "SPANS_ROUTES"
    store = L.choose(tool, "store_route_name", store_route_name, ("STORE_ROUTE_NAME", "NO_STORE_ROUTE_NAME"),
                     "NO_STORE_ROUTE_NAME") == "STORE_ROUTE_NAME"
    for name, value in (("event_id_field", event_id_field), ("route_id_field", route_id_field),
                        ("from_date_field", from_date_field), ("to_date_field", to_date_field),
                        ("loc_error_field", loc_error_field), ("measure_field", measure_field)):
        if not L.given(value):
            raise ExecuteError(f"ERROR 000735: {tool}: {name} is required.")
    L.name_check(event_name, tool, 160)
    file, table, _ = L.resolve(parent_network)
    connection = L.connect(file)
    try:
        network = L.network_required(connection, table, tool)
        lrs = L.lrs_get(connection, network["lrs"])
        _event_checks(tool, network, kind, spans, store, to_measure_field, to_route_id_field, route_name_field,
                      to_route_name_field)
        target = f"{lrs['dataset']}/{event_name}"
        if L.exists(connection, target):
            raise ExecuteError(f"ERROR 000258: '{target}' already exists.")
    finally:
        connection.close()
    columns = [(event_id_field, _TEXT), (route_id_field, _TEXT)]
    if store:
        columns.append((route_name_field, _TEXT))
    if spans:
        columns.append((to_route_id_field, _TEXT))
        if store:
            columns.append((to_route_name_field, _TEXT))
    columns += [(from_date_field, _DATE), (to_date_field, _DATE), (loc_error_field, _TEXT), (measure_field, _DOUBLE)]
    if kind == "LINE":
        columns.append((to_measure_field, _DOUBLE))
    if len({str(n).lower() for n, _ in columns}) != len(columns):
        raise ExecuteError(f"ERROR 000800: {tool}: a field maps to one role only.")
    _new_class(file, target, "Point" if kind == "POINT" else "Polyline", lrs["srid"],
               [(str(n), t) for n, t in columns], True, True)
    settings = _event_settings(target, table, kind, str(event_id_field), str(route_id_field), str(from_date_field),
                               str(to_date_field), str(loc_error_field), str(measure_field),
                               str(to_measure_field) if kind == "LINE" else None, spans,
                               str(to_route_id_field) if spans else None, store,
                               str(route_name_field) if store else None,
                               str(to_route_name_field) if store and spans else None)
    connection = L.connect(file)
    try:
        L.event_put(connection, settings)
        connection.commit()
    finally:
        connection.close()
    return Result(L.full(file, target))


def _register_event_fields(connection, table, settings, given, tool):
    for key, value, kinds in (("event_id", given["event_id_field"], (_TEXT,)), ("route_id", given["route_id_field"],
                                                                                 (_TEXT,)),
                              ("from_date", given["from_date_field"], (_DATE,)),
                              ("to_date", given["to_date_field"], (_DATE,)),
                              ("loc_error", given["loc_error_field"], (_TEXT,)),
                              ("measure", given["measure_field"], _NUMERIC),
                              ("to_measure", given["to_measure_field"], _NUMERIC),
                              ("to_route_id", given["to_route_id_field"], (_TEXT,)),
                              ("route_name", given["route_name_field"], (_TEXT,)),
                              ("to_route_name", given["to_route_name_field"], (_TEXT,))):
        if L.given(value):
            settings[key] = L.field(connection, table, value, kinds, key.replace("_", " ") + " field")


def CreateLRSEventFromExistingDataset(parent_network, in_feature_class, event_id_field, route_id_field, from_date_field,
                                      to_date_field, loc_error_field, measure_field, to_measure_field=None,
                                      event_spans_routes="NO_SPANS_ROUTES", to_route_id_field=None,
                                      store_route_name="NO_STORE_ROUTE_NAME", route_name_field=None,
                                      to_route_name_field=None):
    """An existing point or polyline class in the LRS's feature dataset registered as an event of the network, its
    fields of the events data model's types."""
    tool = "CreateLRSEventFromExistingDataset"
    given = dict(locals())
    spans = L.choose(tool, "event_spans_routes", event_spans_routes, ("SPANS_ROUTES", "NO_SPANS_ROUTES"),
                     "NO_SPANS_ROUTES") == "SPANS_ROUTES"
    store = L.choose(tool, "store_route_name", store_route_name, ("STORE_ROUTE_NAME", "NO_STORE_ROUTE_NAME"),
                     "NO_STORE_ROUTE_NAME") == "STORE_ROUTE_NAME"
    file, net_table, _ = L.resolve(parent_network)
    f2, table, _ = L.resolve(in_feature_class)
    if os.path.normcase(os.path.abspath(f2)) != os.path.normcase(os.path.abspath(file)):
        raise ExecuteError(f"ERROR 000800: {tool}: the event must be in the LRS's geodatabase.")
    connection = L.connect(file)
    try:
        network = L.network_required(connection, net_table, tool)
        lrs = L.lrs_get(connection, network["lrs"])
        shape = L.shape_info(connection, table)[0]
        if shape not in ("Point", "Polyline"):
            raise ExecuteError(f"ERROR 000800: {tool}: an event is a point or polyline feature class.")
        if "/" not in table or table.rsplit("/", 1)[0] != lrs["dataset"]:
            raise ExecuteError(f"ERROR 000800: {tool}: the event must be in the LRS feature dataset.")
        if L.registered_anywhere(connection, table):
            raise ExecuteError(f"ERROR 000800: {tool}: '{table}' is already registered with an LRS.")
        kind = "POINT" if shape == "Point" else "LINE"
        _event_checks(tool, network, kind, spans, store, to_measure_field, to_route_id_field, route_name_field,
                      to_route_name_field)
        settings = _event_settings(table, net_table, kind, None, None, None, None, None, None, None, spans, None, store,
                                   None, None)
        _register_event_fields(connection, table, settings, given, tool)
        L.event_put(connection, settings)
        connection.commit()
    finally:
        connection.close()
    return Result(L.full(file, table))


def ModifyLRSEvent(in_feature_class, event_id_field, route_id_field, from_date_field, to_date_field, loc_error_field,
                   measure_field, to_measure_field=None, event_spans_routes="AS_IS", to_route_id_field=None,
                   store_route_name="AS_IS", route_name_field=None, to_route_name_field=None):
    """An event's registered fields changed; spanning applies only to line events on a line network."""
    tool = "ModifyLRSEvent"
    given = dict(locals())
    spans = L.choose(tool, "event_spans_routes", event_spans_routes, ("AS_IS", "SPANS_ROUTES", "NO_SPANS_ROUTES"),
                     "AS_IS")
    store = L.choose(tool, "store_route_name", store_route_name, ("AS_IS", "STORE_ROUTE_NAME", "NO_STORE_ROUTE_NAME"),
                     "AS_IS")
    file, table, _ = L.resolve(in_feature_class)
    connection = L.connect(file)
    try:
        event = L.event_required(connection, table, tool)
        network = L.network_get(connection, event["network"])
        _register_event_fields(connection, table, event, given, tool)
        if spans != "AS_IS":
            event["spans"] = spans == "SPANS_ROUTES"
        if store != "AS_IS":
            event["store_route_name"] = store == "STORE_ROUTE_NAME"
        _event_checks(tool, network, event["kind"], event["spans"], event["store_route_name"], event.get("to_measure"),
                      event.get("to_route_id"), event.get("route_name"), event.get("to_route_name"))
        L.event_put(connection, event)
        connection.commit()
    finally:
        connection.close()
    return Result(L.full(file, table))


def ModifyEventBehaviorRules(in_feature_class, calibrate_rule=None, retire_rule=None, extend_rule=None,
                             reassign_rule=None, realign_rule=None, reverse_rule=None, carto_realign_rule=None):
    """An event's behaviour rule for each route edit activity, from the activity's own list; a rule not given is
    kept."""
    tool = "ModifyEventBehaviorRules"
    given = {"calibrate": calibrate_rule, "retire": retire_rule, "extend": extend_rule, "reassign": reassign_rule,
             "realign": realign_rule, "reverse": reverse_rule, "carto_realign": carto_realign_rule}
    file, table, _ = L.resolve(in_feature_class)
    connection = L.connect(file)
    try:
        event = L.event_required(connection, table, tool)
        rules = event.get("rules") or dict(L.DEFAULT_RULES)
        for activity, value in given.items():
            if L.given(value):
                rules[activity] = L.choose(tool, f"{activity}_rule", value, L.RULES[activity], None)
        event["rules"] = rules
        L.event_put(connection, event)
        connection.commit()
    finally:
        connection.close()
    return Result(L.full(file, table))


def _registration(in_feature_class, tool, key, build):
    file, table, _ = L.resolve(in_feature_class)
    connection = L.connect(file)
    try:
        event = L.event_required(connection, table, tool)
        network = L.network_get(connection, event["network"])
        event[key] = build(connection, table, event, network)
        L.event_put(connection, event)
        connection.commit()
    finally:
        connection.close()
    return Result(L.full(file, table))


def EnableDerivedMeasureFields(in_feature_class, derived_route_id_field=None, derived_route_name_field=None,
                               derived_from_measure_field=None, derived_to_measure_field=None):
    """Existing fields registered to hold an event's derived route ID, name and measures (DeriveEventMeasures fills
    them); they must already exist. INFERRED (§5.9 (c)): a field not named is the page's example name, DerivedRouteId,
    DerivedRouteName, DerivedFromMeasure, DerivedToMeasure; a point event uses the from measure only."""
    def build(connection, table, event, network):
        if not network.get("derived"):
            raise ExecuteError("ERROR 000800: EnableDerivedMeasureFields: the event's network has no derived network.")
        out = {"route_id": L.field(connection, table, derived_route_id_field if L.given(derived_route_id_field)
                                   else "DerivedRouteId", (_TEXT,)),
               "from_measure": L.field(connection, table, derived_from_measure_field
                                       if L.given(derived_from_measure_field) else "DerivedFromMeasure", _NUMERIC)}
        if L.given(derived_route_name_field) or "derivedroutename" in L.columns(connection, table):
            out["route_name"] = L.field(connection, table, derived_route_name_field
                                        if L.given(derived_route_name_field) else "DerivedRouteName", (_TEXT,))
        if E.is_line(event):
            out["to_measure"] = L.field(connection, table, derived_to_measure_field if L.given(derived_to_measure_field)
                                        else "DerivedToMeasure", _NUMERIC)
        return out
    return _registration(in_feature_class, "EnableDerivedMeasureFields", "derived", build)


def EnableReferentFields(in_feature_class, from_referent_method_field=None, from_referent_location_field=None,
                         from_referent_offset_field=None, to_referent_method_field=None,
                         to_referent_location_field=None, to_referent_offset_field=None, offset_units="MILES"):
    """Existing referent fields registered (or their registration changed): method (short), location and offset
    (text); a line event's to fields too. Existing values are neither checked nor changed, as documented."""
    units = L.choose("EnableReferentFields", "offset_units", offset_units, tuple(L.UNITS), "MILES")

    def build(connection, table, event, network):
        names = [("from_method", from_referent_method_field, (_SHORT,)),
                 ("from_location", from_referent_location_field, (_TEXT,)),
                 ("from_offset", from_referent_offset_field, (_TEXT,))]
        if E.is_line(event):
            names += [("to_method", to_referent_method_field, (_SHORT,)),
                      ("to_location", to_referent_location_field, (_TEXT,)),
                      ("to_offset", to_referent_offset_field, (_TEXT,))]
        out = {"units": units}
        for key, value, kinds in names:
            if not L.given(value):
                raise ExecuteError(f"ERROR 000735: EnableReferentFields: the {key.replace('_', ' ')} field is required.")
            out[key] = L.field(connection, table, value, kinds, key.replace("_", " ") + " field")
        return out
    return _registration(in_feature_class, "EnableReferentFields", "referent", build)


def EnableStationingFields(in_feature_class, station_field=None, back_station_field=None, station_direction_field=None,
                           station_measure_units="MILES", decreasing_station_values=None):
    """Existing station, back station and station direction fields (text) of a point event registered; one
    stationing event per network. The decreasing values are the direction field's values whose stations decrease."""
    units = L.choose("EnableStationingFields", "station_measure_units", station_measure_units, tuple(L.UNITS), "MILES")

    def build(connection, table, event, network):
        if E.is_line(event):
            raise ExecuteError("ERROR 000800: EnableStationingFields: stationing is for point events.")
        for other in L.events(connection, network["table"]):
            if other["table"] != table and other.get("stationing"):
                raise ExecuteError("ERROR 000800: EnableStationingFields: the network already has a stationing "
                                   f"event, '{other['table']}'.")
        out = {"units": units, "decreasing": [v.strip() for v in str(decreasing_station_values).split(",")
                                              if v.strip()] if L.given(decreasing_station_values) else []}
        for key, value in (("station", station_field), ("back_station", back_station_field),
                           ("direction", station_direction_field)):
            if not L.given(value):
                raise ExecuteError(f"ERROR 000735: EnableStationingFields: the {key.replace('_', ' ')} field is "
                                   "required.")
            out[key] = L.field(connection, table, value, (_TEXT,), key + " field")
        return out
    return _registration(in_feature_class, "EnableStationingFields", "stationing", build)


def _disable(in_feature_class, tool, key, label):
    def build(connection, table, event, network):
        if not event.get(key):
            raise ExecuteError(f"ERROR 000800: {tool}: the {label} fields are not enabled.")
        return None
    return _registration(in_feature_class, tool, key, build)


def DisableDerivedMeasureFields(in_feature_class):
    """The derived measure fields' registration removed; the fields and their values stay."""
    return _disable(in_feature_class, "DisableDerivedMeasureFields", "derived", "derived measure")


def DisableReferentFields(in_feature_class):
    """The referent fields' registration removed; the fields and their values stay."""
    return _disable(in_feature_class, "DisableReferentFields", "referent", "referent")


def DisableStationingFields(in_feature_class):
    """The stationing fields' registration removed; the fields stay (INFERRED, §5.9 (c): as the other Disable tools
    say of theirs)."""
    return _disable(in_feature_class, "DisableStationingFields", "stationing", "stationing")


# ---- intersections ---------------------------------------------------------------------------------------------------

_INTERSECTION_FIELDS = (("intersection_id", "IntersectionId", "GUID"), ("intersection_name", "IntersectionName", _TEXT),
                        ("route_id", "RouteId", _TEXT), ("feature_id", "FeatureId", _TEXT),
                        ("feature_class_name", "FeatureClassName", _TEXT), ("from_date", "FromDate", _DATE),
                        ("to_date", "ToDate", _DATE), ("measure", "Measure", _DOUBLE))


def _intersecting_layers(file, connection, lrs, network, value, tool):
    layers = []
    for row in _rows(value):
        cells = _cells(row)
        if len(cells) < 3:
            raise ExecuteError(f"ERROR 000800: {tool}: '{row}' needs a layer, an ID field and a description field.")
        table = _class(file, lrs["dataset"], cells[0], connection)
        kind, srid, has_z, _ = L.shape_info(connection, table)
        if kind is None:
            raise ExecuteError(f"ERROR 000800: {tool}: '{cells[0]}' is not a feature class.")
        if srid != lrs["srid"]:
            raise ExecuteError(f"ERROR 000800: {tool}: '{cells[0]}' must share the LRS's spatial reference.")
        if not has_z:
            raise ExecuteError(f"ERROR 000800: {tool}: '{cells[0]}' must be z-enabled.")
        separator = cells[3] if len(cells) > 3 else ","
        if table == network["table"] and separator != ",":
            raise ExecuteError(f"ERROR 000800: {tool}: a network intersecting itself takes only the comma separator.")
        layers.append({"table": table, "id_field": L.field(connection, table, cells[1]),
                       "description_field": L.field(connection, table, cells[2]), "separator": separator})
    if not layers:
        raise ExecuteError(f"ERROR 000735: {tool}: intersecting_layers is required.")
    return layers


def _intersection_common(file, net_table, network_description_field, consider_z, z_tolerance, tool):
    connection = L.connect(file)
    try:
        network = L.network_required(connection, net_table, tool)
        lrs = L.lrs_get(connection, network["lrs"])
        from ._reference import SpatialReference
        if not lrs["srid"] or SpatialReference(lrs["srid"]).type != "Projected":
            raise ExecuteError(f"ERROR 000800: {tool}: the LRS must be in a projected coordinate system.")
        description = L.field(connection, net_table, network_description_field)
    finally:
        connection.close()
    z = L.choose(tool, "consider_z", consider_z, ("CONSIDER_Z", "DO_NOT_CONSIDER_Z"), "DO_NOT_CONSIDER_Z") == \
        "CONSIDER_Z"
    tolerance = _tolerance_value(z_tolerance, lrs.get("z_tolerance") or L.XY_TOLERANCE)
    return network, lrs, description, z, tolerance


def CreateLRSIntersection(parent_network, network_description_field, intersection_feature_class_name,
                          intersecting_layers, consider_z="DO_NOT_CONSIDER_Z", z_tolerance=None):
    """An empty intersection class, point with m and z as the page says, with the fields the ModifyLRSIntersection
    page tabulates, registered with the layers that intersect the network (``layer IDField DescriptionField
    Separator`` rows). INFERRED (§5.9 (c)): z_tolerance defaults to the LRS's."""
    tool = "CreateLRSIntersection"
    file, net_table, _ = L.resolve(parent_network)
    network, lrs, description, z, tolerance = _intersection_common(file, net_table, network_description_field,
                                                                   consider_z, z_tolerance, tool)
    L.name_check(intersection_feature_class_name, tool, 160)
    target = f"{lrs['dataset']}/{intersection_feature_class_name}"
    connection = L.connect(file)
    try:
        if L.exists(connection, target):
            raise ExecuteError(f"ERROR 000258: '{target}' already exists.")
        layers = _intersecting_layers(file, connection, lrs, network, intersecting_layers, tool)
    finally:
        connection.close()
    _new_class(file, target, "Point", lrs["srid"], [(n, t) for _, n, t in _INTERSECTION_FIELDS], True, True)
    settings = {"table": target, "network": net_table, "network_description_field": description, "layers": layers,
                "consider_z": z, "z_tolerance": tolerance}
    settings.update({key: name for key, name, _ in _INTERSECTION_FIELDS})
    connection = L.connect(file)
    try:
        L.intersection_put(connection, settings)
        connection.commit()
    finally:
        connection.close()
    return Result(L.full(file, target))


def CreateLRSIntersectionFromExistingDataset(parent_network, network_description_field, in_feature_class,
                                             intersection_id_field, intersection_name_field, route_id_field,
                                             feature_id_field, feature_class_name_field, from_date_field,
                                             to_date_field, intersecting_layers, consider_z="DO_NOT_CONSIDER_Z",
                                             z_tolerance=None, measure_field=None):
    """An existing point class with the intersection fields registered as the network's intersection class. The
    measure field is required, though it follows optional parameters, as the page orders them."""
    tool = "CreateLRSIntersectionFromExistingDataset"
    if not L.given(measure_field):
        raise ExecuteError(f"ERROR 000735: {tool}: measure_field is required.")
    file, net_table, _ = L.resolve(parent_network)
    network, lrs, description, z, tolerance = _intersection_common(file, net_table, network_description_field,
                                                                   consider_z, z_tolerance, tool)
    f2, table, _ = L.resolve(in_feature_class)
    if os.path.normcase(os.path.abspath(f2)) != os.path.normcase(os.path.abspath(file)):
        raise ExecuteError(f"ERROR 000800: {tool}: the intersection class must be in the LRS's geodatabase.")
    given = {"intersection_id": intersection_id_field, "intersection_name": intersection_name_field,
             "route_id": route_id_field, "feature_id": feature_id_field, "feature_class_name": feature_class_name_field,
             "from_date": from_date_field, "to_date": to_date_field, "measure": measure_field}
    connection = L.connect(file)
    try:
        kind, srid, has_z, _ = L.shape_info(connection, table)
        if kind != "Point" or not has_z:
            raise ExecuteError(f"ERROR 000800: {tool}: the intersection class is a z-enabled point class.")
        if srid != lrs["srid"]:
            raise ExecuteError(f"ERROR 000800: {tool}: the intersection class must share the LRS's spatial reference.")
        if L.registered_anywhere(connection, table):
            raise ExecuteError(f"ERROR 000800: {tool}: '{table}' is already registered with an LRS.")
        settings = {"table": table, "network": net_table, "network_description_field": description,
                    "consider_z": z, "z_tolerance": tolerance,
                    "layers": _intersecting_layers(file, connection, lrs, network, intersecting_layers, tool)}
        for key, _, kind_ in _INTERSECTION_FIELDS:
            kinds = (_TEXT,) if kind_ == "GUID" else _NUMERIC if kind_ == _DOUBLE else (kind_,)
            settings[key] = L.field(connection, table, given[key], kinds, key.replace("_", " ") + " field")
        L.intersection_put(connection, settings)
        connection.commit()
    finally:
        connection.close()
    return Result(L.full(file, table))


def ModifyLRSIntersection(in_feature_class, intersection_id_field=None, intersection_name_field=None,
                          route_id_field=None, feature_id_field=None, feature_class_name_field=None,
                          from_date_field=None, to_date_field=None, intersecting_layers=None, measure_field=None):
    """An intersection class's registered fields or intersecting layers changed."""
    tool = "ModifyLRSIntersection"
    given = {"intersection_id": intersection_id_field, "intersection_name": intersection_name_field,
             "route_id": route_id_field, "feature_id": feature_id_field, "feature_class_name": feature_class_name_field,
             "from_date": from_date_field, "to_date": to_date_field, "measure": measure_field}
    file, table, _ = L.resolve(in_feature_class)
    connection = L.connect(file)
    try:
        settings = L.intersection_get(connection, table)
        if settings is None:
            raise ExecuteError(f"ERROR 000800: {tool}: '{table}' is not a registered LRS intersection class.")
        network = L.network_get(connection, settings["network"])
        lrs = L.lrs_get(connection, network["lrs"])
        for key, value in given.items():
            if L.given(value):
                settings[key] = L.field(connection, table, value)
        if L.given(intersecting_layers):
            settings["layers"] = _intersecting_layers(file, connection, lrs, network, intersecting_layers, tool)
        L.intersection_put(connection, settings)
        connection.commit()
    finally:
        connection.close()
    return Result(L.full(file, table))


# ---- address and utility network classes ----------------------------------------------------------------------------

def ConfigureAddressFeatureClasses(in_address_range_features, left_from_address_field, left_to_address_field,
                                   right_from_address_field, right_to_address_field, in_site_address_features,
                                   address_number_field, address_range_road_name_field, site_address_road_name):
    """An Address Range class (the LRS's centerline or a line event) and a Site Address Point class registered with
    the LRS: address fields short or long, road names text, the site's number short, long or text, both classes in
    the LRS feature dataset. OverlayEvents splits address ranges with it."""
    tool = "ConfigureAddressFeatureClasses"
    file, ranges, _ = L.resolve(in_address_range_features)
    f2, sites, _ = L.resolve(in_site_address_features)
    connection = L.connect(file)
    try:
        lrs = L.lrs_of_class(connection, ranges)
        event = L.event_get(connection, ranges)
        if lrs is None or not (ranges == lrs["centerline"] or (event and E.is_line(event))):
            raise ExecuteError(f"ERROR 000800: {tool}: the address ranges are the LRS centerline or a line event.")
        if os.path.normcase(os.path.abspath(f2)) != os.path.normcase(os.path.abspath(file)) or \
                "/" not in sites or sites.rsplit("/", 1)[0] != lrs["dataset"]:
            raise ExecuteError(f"ERROR 000800: {tool}: both classes must be in the LRS feature dataset.")
        if L.shape_info(connection, sites)[0] != "Point":
            raise ExecuteError(f"ERROR 000800: {tool}: the site addresses are points.")
        number = (_SHORT, _LONG)
        address = {"ranges": ranges, "sites": sites,
                   "left_from": L.field(connection, ranges, left_from_address_field, number),
                   "left_to": L.field(connection, ranges, left_to_address_field, number),
                   "right_from": L.field(connection, ranges, right_from_address_field, number),
                   "right_to": L.field(connection, ranges, right_to_address_field, number),
                   "number": L.field(connection, sites, address_number_field, (_SHORT, _LONG, _TEXT)),
                   "range_road": L.field(connection, ranges, address_range_road_name_field, (_TEXT,)),
                   "site_road": L.field(connection, sites, site_address_road_name, (_TEXT,))}
        lrs["address"] = address
        L.lrs_put(connection, lrs)
        connection.commit()
    finally:
        connection.close()
    return Result(L.full(file, ranges), L.full(file, sites))


def ConfigureUtilityNetworkFeatureClasses(in_line_features, line_route_id_field, line_from_measure_field,
                                          line_to_measure_field, in_device_features, device_route_id_field,
                                          device_measure_field, in_junction_features, junction_route_id_field,
                                          junction_measure_field):
    """The Pipeline Line, Device and Junction classes of a utility network (ADR-231) registered with the LRS, the line
    class being the LRS centerline, as the page says it serves. Their route ID fields have the Centerline Sequence
    route ID's type. INFERRED (§5.9 (m)): a class is a utility network's Line, Device or Junction class by its role in
    the network grapy holds; UpdateMeasuresFromLRS and OverlayEvents then take them."""
    tool = "ConfigureUtilityNetworkFeatureClasses"
    from . import _utility
    file, line, _ = L.resolve(in_line_features)
    tables = {"line": line, "device": L.resolve(in_device_features)[1], "junction": L.resolve(in_junction_features)[1]}
    connection = L.connect(file)
    try:
        lrs = L.lrs_of_class(connection, line)
        if lrs is None or lrs["centerline"] != line:
            raise ExecuteError(f"ERROR 000800: {tool}: the Pipeline Line class must be the LRS centerline.")
        _utility.ensure(connection)
        roles = {r[0]: r[1] for r in connection.execute("select class_name, role from grapy_un_source")}
        for key, role in (("line", "Line"), ("device", "Device"), ("junction", "Junction")):
            if roles.get(tables[key]) != role:
                raise ExecuteError(f"ERROR 000800: {tool}: '{tables[key]}' is not a utility network {role} class.")
        kind = L.columns(connection, lrs["sequence"])[lrs["seq_route_id"].lower()].type
        utility = {"line": line, "device": tables["device"], "junction": tables["junction"],
                   "line_route_id": L.field(connection, line, line_route_id_field, (kind,)),
                   "line_from": L.field(connection, line, line_from_measure_field, _NUMERIC),
                   "line_to": L.field(connection, line, line_to_measure_field, _NUMERIC),
                   "device_route_id": L.field(connection, tables["device"], device_route_id_field, (kind,)),
                   "device_measure": L.field(connection, tables["device"], device_measure_field, _NUMERIC),
                   "junction_route_id": L.field(connection, tables["junction"], junction_route_id_field, (kind,)),
                   "junction_measure": L.field(connection, tables["junction"], junction_measure_field, _NUMERIC)}
        lrs["utility"] = utility
        L.lrs_put(connection, lrs)
        connection.commit()
    finally:
        connection.close()
    return Result(L.full(file, line), L.full(file, tables["device"]), L.full(file, tables["junction"]))


def RemoveLRSEntity(in_workspace, lrs_entity_type, lrs_entity_name):
    """An LRS entity unregistered; no data is deleted. An LRS takes its networks, events and intersections with it, a
    network its events, intersections and derived network."""
    tool = "RemoveLRSEntity"
    kind = L.choose(tool, "lrs_entity_type", lrs_entity_type,
                    ("LRS", "NETWORK", "EVENT", "INTERSECTION", "UN_FEATURE_CLASS", "ADDRESS_FEATURE_CLASS"), None)
    file, _ = _workspace(in_workspace)
    name = str(lrs_entity_name)
    connection = L.connect(file)

    def find(kind_table):
        hits = [r[0] for r in connection.execute(f"select table_name from {kind_table}")
                if r[0].lower() == name.lower() or r[0].rsplit("/", 1)[-1].lower() == name.lower()]
        if not hits:
            raise ExecuteError(f"ERROR 000800: {tool}: '{name}' is not a registered {kind.lower()}.")
        return hits[0]

    def drop_network(table):
        network = L.network_get(connection, table)
        for event in L.events(connection, table):
            connection.execute("delete from grapy_lrs_event where table_name = ?", (event["table"],))
        for inter in L.intersections(connection, table):
            connection.execute("delete from grapy_lrs_intersection where table_name = ?", (inter["table"],))
        connection.execute("delete from grapy_lrs_edit_log where network = ?", (table,))
        connection.execute("delete from grapy_lrs_network where table_name = ?", (table,))
        if network.get("derived"):
            drop_network(network["derived"])
        if network.get("derived_from"):
            parent = L.network_get(connection, network["derived_from"])
            if parent:
                parent["derived"] = None
                L.network_put(connection, parent)

    try:
        if kind == "LRS":
            lrs = L.lrs_get(connection, name)
            for network in L.networks(connection, lrs["name"]):
                if L.network_get(connection, network["table"]):
                    drop_network(network["table"])
            connection.execute("delete from grapy_lrs where name = ?", (lrs["name"],))
        elif kind == "NETWORK":
            drop_network(find("grapy_lrs_network"))
        elif kind == "EVENT":
            connection.execute("delete from grapy_lrs_event where table_name = ?", (find("grapy_lrs_event"),))
        elif kind == "INTERSECTION":
            connection.execute("delete from grapy_lrs_intersection where table_name = ?",
                               (find("grapy_lrs_intersection"),))
        else:
            key = "utility" if kind == "UN_FEATURE_CLASS" else "address"
            done = False
            for lrs_name in L.lrs_names(connection):
                lrs = L.lrs_get(connection, lrs_name)
                held = lrs.get(key) or {}
                classes = [held.get(k) for k in (("line", "device", "junction") if key == "utility" else
                                                 ("ranges", "sites"))]
                if any(c and (c.lower() == name.lower() or c.rsplit("/", 1)[-1].lower() == name.lower())
                       for c in classes):
                    lrs[key] = None
                    L.lrs_put(connection, lrs)
                    done = True
            if not done:
                raise ExecuteError(f"ERROR 000800: {tool}: '{name}' is not a registered {kind.lower()}.")
        connection.commit()
    finally:
        connection.close()
    return Result(L.text(in_workspace))


# ---- loading and calibrating routes ----------------------------------------------------------------------------------

def _source_rows(value):
    file, table, where = L.resolve(value)
    connection = L.connect(file)
    try:
        kind, srid, _, _ = L.shape_info(connection, table)
        names = {k: c.name for k, c in L.columns(connection, table).items()}
        dates = [c.name for c in L.columns(connection, table).values() if c.type == _DATE]
        rows = L.read(connection, table, where, dates)
    finally:
        connection.close()
    return file, table, kind, srid, names, rows


def _value(row, name):
    return row.get(name) if L.given(name) else None


def AppendRoutes(source_routes, in_lrs_network, route_id_field, route_name_field, from_date_field=None,
                 to_date_field=None, line_id_field=None, line_name_field=None, line_order_field=None, field_map=None,
                 load_type="ADD", load_field="ROUTE_ID", consider_existing_centerlines="DO_NOT_CONSIDER",
                 allow_partial_loading="DO_NOT_ALLOW"):
    """Routes loaded into a network with their xyz and no m, and their centerlines: new ones with a CenterlineID and a
    Centerline Sequence row each (DO_NOT_CONSIDER), or existing centerlines whose geometry the route matches exactly
    (CONSIDER, forced when the LRS has a utility network or address configuration). ADD logs and skips a route whose
    ID and time are already there; RETIRE_BY_ROUTE_ID retires the overlapping records of the same ID (deletes the
    eclipsed); REPLACE_BY_ROUTE_ID deletes every record of the ID with its calibration points and sequence rows. Line
    orders are above 0 in steps of 100. With DO_NOT_ALLOW one bad route loads nothing; with ALLOW the bad ones go to
    the skipped routes class. INFERRED (§5.9 (g)): no calibration points are made (GenerateCalibrationPoints does
    that); a route with no ID takes a GUID (AUTOGENERATED), its composed ID (MULTI_FIELD) or, by ROUTE_NAME, the ID of
    the route of that name."""
    from .management import _mappings
    tool = "AppendRoutes"
    kind = L.choose(tool, "load_type", load_type, ("ADD", "RETIRE_BY_ROUTE_ID", "REPLACE_BY_ROUTE_ID"), "ADD")
    by = L.choose(tool, "load_field", load_field, ("ROUTE_ID", "ROUTE_NAME"), "ROUTE_ID")
    consider = L.choose(tool, "consider_existing_centerlines", consider_existing_centerlines,
                        ("CONSIDER", "DO_NOT_CONSIDER"), "DO_NOT_CONSIDER") == "CONSIDER"
    partial = L.choose(tool, "allow_partial_loading", allow_partial_loading, ("ALLOW", "DO_NOT_ALLOW"),
                       "DO_NOT_ALLOW") == "ALLOW"
    if by == "ROUTE_NAME" and kind != "ADD":
        raise ExecuteError(f"ERROR 000800: {tool}: ROUTE_NAME is allowed only with ADD.")
    mappings = _mappings(tool, field_map)
    file, lrs, network, _ = _network(in_lrs_network, tool)
    if network.get("derived_from"):
        raise ExecuteError(f"ERROR 000800: {tool}: routes are not loaded into a derived network.")
    if lrs.get("utility") or lrs.get("address"):
        consider = True
    src_file, src_table, shape, srid, names, rows = _source_rows(source_routes)
    if shape != "Polyline":
        raise ExecuteError(f"ERROR 000800: {tool}: the source routes are polylines.")
    if srid != lrs["srid"]:
        raise ExecuteError(f"ERROR 000800: {tool}: the source routes must share the LRS's spatial reference.")
    tolerance = L.tolerance(lrs)
    connection = L.connect(file)
    try:
        existing = L.route_rows(connection, network)
        lines = R.centerlines(connection, lrs) if consider else {}
        target_columns = L.columns(connection, network["table"])
        lookups = network.get("lookups") or {}
        lookup_values = {}
        for part, lookup in lookups.items():
            other = L.connect(lookup["file"])
            try:
                lookup_values[part] = {str(r.get(lookup["key"])) for r in L.read(other, lookup["table"])}
            finally:
                other.close()
    finally:
        connection.close()
    roles = {network[k].lower() for k in ("route_id", "route_name", "from_date", "to_date", "line_id", "line_name",
                                          "line_order") if network.get(k)}
    issues, plan, skipped = [], [], []
    by_name = {}
    for r in existing:
        if network.get("route_name") and r.get(network["route_name"]) is not None:
            by_name.setdefault(str(r.get(network["route_name"])), str(L.route_id_of(network, r)))
    for row in rows:
        problem = None
        record = {}
        route_id = _value(row, route_id_field)
        name = _value(row, route_name_field)
        if by == "ROUTE_NAME" and route_id is None and name is not None:
            route_id = by_name.get(str(name))
        if route_id is None:
            configuration = network.get("route_id_configuration")
            if configuration == "MULTI_FIELD_ROUTE_ID":
                def check(part, value):
                    lookup = lookups.get(part.lower())
                    if lookup and not lookup["allow_any"] and value is not None and \
                            str(value) not in lookup_values[part.lower()]:
                        raise ExecuteError(f"'{value}' is not in the lookup table of '{part}'.")
                try:
                    route_id = R.compose_route_id(network, {k: row.get(k) for k in names}, check)
                except ExecuteError as said:
                    problem = str(said)
            elif configuration == "AUTOGENERATED_ROUTE_ID" or by == "ROUTE_NAME":
                route_id = L.guid()
            else:
                problem = "no route ID"
        start, end = when_of(row, from_date_field), when_of(row, to_date_field)
        if start and end and start >= end:
            problem = "its from date is not before its to date"
        if network.get("line"):
            order = _value(row, line_order_field)
            if order is not None and (int(order) <= 0 or int(order) % 100):
                problem = f"line order {order} is not above 0 in steps of 100"
            record[network["line_id"]] = _value(row, line_id_field)
            record[network["line_name"]] = _value(row, line_name_field)
            record[network["line_order"]] = order
        if row.shape is None or M.length(M.parts(row.shape)) <= tolerance:
            problem = "zero or near-zero length"
        if problem is None and kind == "ADD" and route_id is not None and any(
                str(L.route_id_of(network, r)) == str(route_id) and L.overlaps(
                    r.get(network["from_date"]), r.get(network["to_date"]), start, end) for r in existing):
            problem = "a route of this ID and time is already loaded"
        matched = []
        if problem is None and consider:
            flat = shapely.force_2d(row.shape)
            matched = [(cid, c) for cid, c in lines.items()
                       if shapely.force_2d(c.shape).difference(flat.buffer(tolerance)).length <= tolerance]
            union = shapely.union_all([shapely.force_2d(c.shape) for _, c in matched]) if matched else None
            if not matched:
                problem = "no centerline matches it"
            elif flat.difference(union.buffer(tolerance)).length > tolerance:
                problem = "part of it is not covered by centerlines"
        if problem:
            issues.append(f"Route {route_id}: {problem}.")
            skipped.append(row)
            continue
        record[network["route_id"]] = str(route_id)
        if network.get("route_name"):
            record[network["route_name"]] = name
        record[network["from_date"]] = start
        record[network["to_date"]] = end
        for column in target_columns.values():
            if column.name.lower() in roles:
                continue
            if mappings is not None:
                index = mappings.findFieldMapIndex(column.name)
                if index != -1:
                    record[column.name] = mappings.getFieldMap(index)._value(L.text(source_routes), dict(row))
            elif column.name.lower() in names:
                record[column.name] = row.get(column.name)
        record["SHAPE@"] = M.line([_no_m(p) for p in M.parts(row.shape)])
        plan.append((record, row, matched, start, end))
    if issues and not partial:
        raise ExecuteError(f"ERROR 000800: {tool}: " + " ".join(issues))
    _load_routes(file, lrs, network, kind, plan, issues)
    skipped_path = ""
    if skipped:
        from ._runtime import env
        skipped_path = os.path.join(env.scratchFolder or os.getcwd(), "scratch.gpkg") + "/SkippedRoutes"
        _gpkg.create(skipped_path, "Polyline", srid, [_gpkg.Column(c, "String") for c in ("RouteId", "Reason")], True,
                     False)
        L.insert(*_gpkg.split(skipped_path), [{"RouteId": str(_value(r, route_id_field)), "Reason": i,
                                                "SHAPE@": r.shape} for r, i in zip(skipped, issues)])
    return Result(L.full(file, network["table"]), _details(tool, issues), skipped_path)


def _no_m(part):
    q = part.copy()
    q[:, 3] = math.nan
    return q


def when_of(row, name):
    return L.when(row.get(name)) if L.given(name) else None


def _load_routes(file, lrs, network, kind, plan, issues):
    connection = L.connect(file)
    try:
        existing = L.route_rows(connection, network)
        sequence = L.read(connection, lrs["sequence"], None, (lrs["seq_from"], lrs["seq_to"]))
        points = R.calibration_rows(connection, lrs, network["network_id"])
        connection.commit()
    finally:
        connection.close()
    for record, row, matched, start, end in plan:
        route_id = record[network["route_id"]]
        same = [r for r in existing if str(L.route_id_of(network, r)) == route_id]
        if kind == "REPLACE_BY_ROUTE_ID" and same:
            L.delete(file, network["table"], [r.oid for r in same])
            L.delete(file, lrs["calibration"], [p.oid for p in points if str(p.get(lrs["cp_route_id"])) == route_id])
            L.delete(file, lrs["sequence"], [s.oid for s in sequence if str(s.get(lrs["seq_route_id"])) == route_id and
                                             R._int(s.get(lrs["seq_network_id"])) == network["network_id"]])
            issues.append(f"Route {route_id}: {len(same)} record(s) replaced; recreate its calibration points.")
        elif kind == "RETIRE_BY_ROUTE_ID":
            for r in same:
                rs, re_ = r.get(network["from_date"]), r.get(network["to_date"])
                if not L.overlaps(rs, re_, start, end):
                    continue
                connection = L.connect(file)
                try:
                    L.log(connection, network["table"], route_id, "retire", start, r.shape, None)
                    connection.commit()
                finally:
                    connection.close()
                if (start is None or (rs is not None and rs >= start)) and (end is None or (re_ is not None and
                                                                                            re_ <= end)):
                    L.delete(file, network["table"], [r.oid])
                    issues.append(f"Route {route_id}: an eclipsed record deleted.")
                else:
                    L.update(file, network["table"], r.oid, {network["to_date"]: start})
                    issues.append(f"Route {route_id}: a record retired on {start}.")
        new_lines = []
        if matched:
            for cid, c in matched:
                if c.get(lrs["centerline_id"]) is None:
                    cid = L.guid()
                    L.update(file, lrs["centerline"], c.oid, {lrs["centerline_id"]: cid})
                new_lines.append(cid)
        else:
            cid = L.guid()
            L.insert(file, lrs["centerline"], [{lrs["centerline_id"]: cid, "SHAPE@": shapely.force_3d(
                shapely.force_2d(row.shape)) if not shapely.has_z(row.shape) else row.shape}])
            new_lines.append(cid)
        L.insert(file, lrs["sequence"], [{lrs["seq_centerline_id"]: cid, lrs["seq_route_id"]: route_id,
                                          lrs["seq_from"]: start, lrs["seq_to"]: end,
                                          lrs["seq_network_id"]: network["network_id"]} for cid in new_lines])
        L.insert(file, network["table"], [record])


def _gap_step(rule, offset, before, gap_length):
    """The first measure after a gap (INFERRED, §5.9 (d)): STEPPING_INCREMENT steps up to the next multiple of the
    offset above the measure before the gap; ADDING_INCREMENT adds the offset to it; ADDING_EUCLIDEAN_DISTANCE adds the
    gap's straight-line length. An offset of 0 leaves no measure gap."""
    if rule == "ADDING_EUCLIDEAN_DISTANCE":
        return before + gap_length
    if not offset:
        return before
    if rule == "ADDING_INCREMENT":
        return before + offset
    steps = math.floor(before / offset + 1e-9) + 1
    return steps * offset


@equivalent("calibration points at each part's ends (and a loop's thirds), measured by length with the network's gap "
            "rule", "ArcGIS Pro, Generate Calibration Points; Create a new route; Methods for calibrating routes with "
            "physical gaps")
def GenerateCalibrationPoints(in_polyline_features, route_id_field, from_date_field, to_date_field,
                              in_calibration_point_feature_class, lrs_network, calibration_direction="DIGITIZED_DIRECTION",
                              calibration_method="GEOMETRY_LENGTH", from_measure_field=None, to_measure_field=None):
    """EQUIVALENT: calibration points at the start and end of each part of each polyline, and at a third and two
    thirds of a closed part (a loop), as *Create a new route* places them; measures by length (z counted) from 0 in
    the network's unit (GEOMETRY_LENGTH), scaled to the from and to fields (ATTRIBUTE_FIELDS), or the part ends' own m
    (M_ON_ROUTE); across a gap the network's calibration rule. MEASURE_DIRECTION calibrates against the digitized
    direction where the polyline's m decreases. A calibration point of the same route and network whose time overlaps
    is retired on the new point's from date, or deleted if it began then or later (INFERRED, §5.9 (d))."""
    tool = "GenerateCalibrationPoints"
    direction = L.choose(tool, "calibration_direction", calibration_direction,
                         ("DIGITIZED_DIRECTION", "MEASURE_DIRECTION"), "DIGITIZED_DIRECTION")
    method = L.choose(tool, "calibration_method", calibration_method,
                      ("GEOMETRY_LENGTH", "M_ON_ROUTE", "ATTRIBUTE_FIELDS"), "GEOMETRY_LENGTH")
    if method == "ATTRIBUTE_FIELDS" and not (L.given(from_measure_field) and L.given(to_measure_field)):
        raise ExecuteError(f"ERROR 000735: {tool}: ATTRIBUTE_FIELDS needs the from and to measure fields.")
    file, lrs, network, _ = _network(lrs_network, tool)
    if network.get("derived_from"):
        raise ExecuteError(f"ERROR 000800: {tool}: derived networks are not supported.")
    cp_file, cp_table, _ = L.resolve(in_calibration_point_feature_class)
    if cp_table != lrs["calibration"]:
        raise ExecuteError(f"ERROR 000800: {tool}: '{cp_table}' is not the LRS's calibration point class.")
    _, _, shape, srid, names, rows = _source_rows(in_polyline_features)
    if shape != "Polyline":
        raise ExecuteError(f"ERROR 000800: {tool}: the input is a polyline feature class.")
    ratio = L.unit_ratio(lrs["srid"], network["unit"])
    rule, offset = network.get("calibration_rule", "STEPPING_INCREMENT"), float(network.get("calibration_offset") or 0)
    new, lines = [], []
    for row in rows:
        route_id = row.get(route_id_field)
        if route_id is None or row.shape is None:
            lines.append(f"A polyline with no route ID or shape skipped (OID {row.oid}).")
            continue
        pieces = M.parts(row.shape)
        if direction == "MEASURE_DIRECTION":
            ms = [v for p in pieces for v in p[:, 3] if not math.isnan(v)]
            if len(ms) >= 2 and ms[-1] < ms[0]:
                pieces = [p[::-1].copy() for p in pieces[::-1]]
        start, end = when_of(row, from_date_field), when_of(row, to_date_field)
        points = []
        if method == "M_ON_ROUTE":
            for p in pieces:
                for v in (p[0], p[-1]):
                    if math.isnan(v[3]):
                        lines.append(f"Route {route_id}: a part end has no m; no point made.")
                    else:
                        points.append((v[0], v[1], v[2], float(v[3])))
        else:
            lengths = [M.cumulative(p) for p in pieces]
            total = sum(d[-1] for d in lengths)
            gaps = [math.dist(pieces[i][-1][:2], pieces[i + 1][0][:2]) for i in range(len(pieces) - 1)]
            first = 0.0
            scale = ratio
            if method == "ATTRIBUTE_FIELDS":
                a, b = row.get(from_measure_field), row.get(to_measure_field)
                if a is None or b is None:
                    lines.append(f"Route {route_id}: a null from or to measure; skipped.")
                    continue
                first = float(a)
                steps = sum((g * ratio if rule == "ADDING_EUCLIDEAN_DISTANCE" else offset) for g in gaps)
                scale = (float(b) - float(a) - steps) / total if total else 0.0
            measure = first
            for i, (p, d) in enumerate(zip(pieces, lengths)):
                if i:
                    if method == "ATTRIBUTE_FIELDS":
                        measure += gaps[i - 1] * ratio if rule == "ADDING_EUCLIDEAN_DISTANCE" else offset
                    else:
                        measure = _gap_step(rule, offset, measure, gaps[i - 1] * ratio)
                at = [0.0, d[-1]]
                if np_closed(p):
                    at = [0.0, d[-1] / 3, 2 * d[-1] / 3, d[-1]]
                for dist in at:
                    x, y, z = _at_distance(p, d, dist)
                    points.append((x, y, z, measure + dist * scale))
                measure += d[-1] * scale
        for x, y, z, m in points:
            new.append({lrs["cp_measure"]: m, lrs["cp_from"]: start, lrs["cp_to"]: end,
                        lrs["cp_route_id"]: str(route_id), lrs["cp_network_id"]: network["network_id"],
                        "SHAPE@": shapely.Point(x, y, z)})
        _retire_points(file, lrs, network, str(route_id), start, end, lines)
    L.insert(file, lrs["calibration"], new)
    lines.append(f"{len(new)} calibration points written.")
    return Result(L.full(file, lrs["calibration"]), _details(tool, lines))


def np_closed(part):
    return len(part) > 2 and math.dist(part[0][:2], part[-1][:2]) == 0.0


def _at_distance(part, d, dist):
    for i in range(len(part) - 1):
        if d[i] <= dist <= d[i + 1]:
            t = 0.0 if d[i + 1] == d[i] else (dist - d[i]) / (d[i + 1] - d[i])
            v = part[i, :3] + t * (part[i + 1, :3] - part[i, :3])
            return float(v[0]), float(v[1]), float(v[2])
    v = part[-1]
    return float(v[0]), float(v[1]), float(v[2])


def _retire_points(file, lrs, network, route_id, start, end, lines):
    connection = L.connect(file)
    try:
        old = [p for p in R.calibration_rows(connection, lrs, network["network_id"], route_id)
               if L.overlaps(p.get(lrs["cp_from"]), p.get(lrs["cp_to"]), start, end)]
    finally:
        connection.close()
    for p in old:
        begun = p.get(lrs["cp_from"])
        if start is None or (begun is not None and begun >= start):
            L.delete(file, lrs["calibration"], [p.oid])
        else:
            L.update(file, lrs["calibration"], p.oid, {lrs["cp_to"]: start})
    if old:
        lines.append(f"Route {route_id}: {len(old)} earlier calibration point(s) retired or replaced.")


@equivalent(*M.CALIBRATE)
def GenerateRoutes(in_route_features, record_calibration_changes=None):
    """EQUIVALENT: each route record's shape rebuilt from the centerlines of its Centerline Sequence rows whose time
    overlaps the record's (merged, turned so its calibration points increase along it), its measures interpolated
    between its calibration points and extrapolated past the end ones (the DISTANCE method of Calibrate Routes); then
    a line network's derived network, each line's routes in LineOrder measured from 0. A change of shape or measure is
    logged (calibrate, extend, realign, reverse or cartographic realign; INFERRED, §5.9 (i)) for ApplyEventBehaviors.
    ``record_calibration_changes`` is, as its page says, no longer supported: given, it is said and changes nothing."""
    if L.given(record_calibration_changes):
        AddWarning("GenerateRoutes: record_calibration_changes is no longer supported, as its page says; ignored as "
                   "Pro ignores it.")
    file, lrs, network, where = _network(in_route_features, "GenerateRoutes")
    if network.get("derived_from"):
        raise ExecuteError("ERROR 000800: GenerateRoutes: give the line network; its derived network follows it.")
    lines, changed = R.generate(file, lrs, network, where)
    derived = R.generate_derived(file, lrs, network)
    lines.append(f"{changed} route record(s) generated; {derived} derived route(s).")
    return Result(L.full(file, network["table"]),
                  L.full(file, network["derived"]) if network.get("derived") else "", _details("GenerateRoutes", lines))


def DeleteRoutes(in_route_features, delete_associated_calibration_points="NO_DELETE_CALIBRATION_POINTS",
                 delete_associated_events="NO_DELETE_EVENTS", delete_associated_centerlines="NO_DELETE_CENTERLINES"):
    """The selected routes deleted across time — in a line network every route of their lines — with their Centerline
    Sequence rows and edit log rows, and, as asked, their calibration points, events and the centerlines no other
    route uses. A selection is required."""
    tool = "DeleteRoutes"
    points = L.choose(tool, "delete_associated_calibration_points", delete_associated_calibration_points,
                      ("DELETE_CALIBRATION_POINTS", "NO_DELETE_CALIBRATION_POINTS"),
                      "NO_DELETE_CALIBRATION_POINTS") == "DELETE_CALIBRATION_POINTS"
    with_events = L.choose(tool, "delete_associated_events", delete_associated_events,
                           ("DELETE_EVENTS", "NO_DELETE_EVENTS"), "NO_DELETE_EVENTS") == "DELETE_EVENTS"
    with_lines = L.choose(tool, "delete_associated_centerlines", delete_associated_centerlines,
                          ("DELETE_CENTERLINES", "NO_DELETE_CENTERLINES"), "NO_DELETE_CENTERLINES") == \
        "DELETE_CENTERLINES"
    if not L.selected(in_route_features):
        raise ExecuteError(f"ERROR 000800: {tool}: select the routes to delete.")
    file, lrs, network, where = _network(in_route_features, tool)
    connection = L.connect(file)
    try:
        chosen = L.route_rows(connection, network, where)
        everything = L.route_rows(connection, network)
        ids = {str(L.route_id_of(network, r)) for r in chosen if L.route_id_of(network, r) is not None}
        if network.get("line"):
            line_ids = {str(r.get(network["line_id"])) for r in chosen if r.get(network["line_id"]) is not None}
            ids |= {str(L.route_id_of(network, r)) for r in everything if str(r.get(network["line_id"])) in line_ids}
        records = [r.oid for r in everything if str(L.route_id_of(network, r)) in ids]
        sequence = L.read(connection, lrs["sequence"])
        mine = [s for s in sequence if str(s.get(lrs["seq_route_id"])) in ids
                and R._int(s.get(lrs["seq_network_id"])) == network["network_id"]]
        used_elsewhere = {str(s.get(lrs["seq_centerline_id"])) for s in sequence if s not in mine}
        doomed_lines = {str(s.get(lrs["seq_centerline_id"])) for s in mine} - used_elsewhere
        cps = [p.oid for p in R.calibration_rows(connection, lrs, network["network_id"])
               if str(p.get(lrs["cp_route_id"])) in ids]
        event_rows = []
        for event in L.events(connection, network["table"]):
            hits = [e.oid for e in L.read(connection, event["table"]) if str(e.get(event["route_id"])) in ids or
                    (event.get("to_route_id") and str(e.get(event["to_route_id"])) in ids)]
            event_rows.append((event["table"], hits))
        line_oids = [c.oid for c in L.read(connection, lrs["centerline"])
                     if str(c.get(lrs["centerline_id"])) in doomed_lines]
        connection.execute(f"delete from grapy_lrs_edit_log where network = ? and route_id in "
                           f"({', '.join('?' for _ in ids)})", (network["table"], *ids))
        connection.commit()
    finally:
        connection.close()
    L.delete(file, network["table"], records)
    L.delete(file, lrs["sequence"], [s.oid for s in mine])
    if points:
        L.delete(file, lrs["calibration"], cps)
    if with_events:
        for table, hits in event_rows:
            L.delete(file, table, hits)
    if with_lines:
        L.delete(file, lrs["centerline"], line_oids)
    from . import _layers
    layer = _layers.find(in_route_features)
    if layer is not None:
        layer.selection = set()
    derived = R.generate_derived(file, lrs, network) if network.get("line") else 0
    lines = [f"{len(records)} route record(s) of {len(ids)} route ID(s) deleted.", f"{derived} derived route(s)."]
    return Result(L.full(file, network["table"]), _details(tool, lines),
                  L.full(file, network["derived"]) if network.get("derived") else "")


@equivalent("the line's LineOrder values given back in reverse sequence, the value set kept",
            "ArcGIS Pro, Reverse Line Orders (the page does not say how the new values are computed)")
def ReverseLineOrders(in_route_features):
    """EQUIVALENT: for each line of a selected route, the routes of the line whose time overlaps the selected ones
    take the line's LineOrder values in reverse sequence, so the order reverses and the values stay above 0 in steps
    of 100. INFERRED (§5.9 (j)): the records are changed in place; no time slice is made. Run GenerateRoutes and
    GenerateEvents afterwards, as the page says."""
    tool = "ReverseLineOrders"
    if not L.selected(in_route_features):
        raise ExecuteError(f"ERROR 000800: {tool}: select routes of a line network.")
    file, lrs, network, where = _network(in_route_features, tool)
    if not network.get("line"):
        raise ExecuteError(f"ERROR 000800: {tool}: the network is not a line network.")
    connection = L.connect(file)
    try:
        chosen = L.route_rows(connection, network, where)
        everything = L.route_rows(connection, network)
    finally:
        connection.close()
    changes, lines = [], []
    for line_id in sorted({str(r.get(network["line_id"])) for r in chosen if r.get(network["line_id"]) is not None}):
        picked = [r for r in chosen if str(r.get(network["line_id"])) == line_id]
        members = [r for r in everything if str(r.get(network["line_id"])) == line_id and any(
            L.overlaps(r.get(network["from_date"]), r.get(network["to_date"]), p.get(network["from_date"]),
                       p.get(network["to_date"])) for p in picked)]
        members.sort(key=lambda r: (r.get(network["line_order"]) or 0, r.oid))
        orders = [r.get(network["line_order"]) for r in members]
        for r, value in zip(members, reversed(orders)):
            if r.get(network["line_order"]) != value:
                changes.append((r.oid, value))
        lines.append(f"Line {line_id}: {len(members)} route(s) reversed.")
    for oid, value in changes:
        L.update(file, network["table"], oid, {network["line_order"]: value})
    return Result(L.full(file, network["table"]), _details(tool, lines),
                  L.full(file, network["derived"]) if network.get("derived") else "")


@equivalent("a planar overlay of the centerlines at the XY tolerance (GEOS noding union), each overlap one new "
            "centerline", "ArcGIS Pro, Remove Overlapping Centerlines (\"uses Intersect and Simplify\")")
def RemoveOverlappingCenterlines(in_centerline_features):
    """EQUIVALENT: where centerlines overlap in two dimensions, the overlap becomes one new centerline (new
    CenterlineID) and each centerline keeps its ID on what is left of it; the Centerline Sequence rows are rewritten so
    each route names the pieces it ran on, its dates and network kept. Z is ignored in the overlap and taken back from
    the centerline each piece came from (INFERRED, §5.9 (j)); other fields of a centerline are kept on its remainder
    and lost on an overlap piece, as the page warns."""
    tool = "RemoveOverlappingCenterlines"
    file, table, _ = L.resolve(in_centerline_features)
    connection = L.connect(file)
    try:
        lrs = L.lrs_of_class(connection, table)
        if lrs is None or lrs["centerline"] != table:
            raise ExecuteError(f"ERROR 000800: {tool}: the input is not an LRS centerline class.")
        rows = [r for r in L.read(connection, lrs["centerline"]) if r.shape is not None]
        sequence = L.read(connection, lrs["sequence"], None, (lrs["seq_from"], lrs["seq_to"]))
    finally:
        connection.close()
    tolerance = L.tolerance(lrs)
    pieces = R.overlay_centerlines([r.shape for r in rows], tolerance)
    shared = [(g, cover) for g, cover in pieces if len(cover) > 1]
    if not shared:
        return Result(L.full(file, table), _details(tool, ["No overlapping centerlines."]))
    touched = set().union(*[c for _, c in shared])
    lines, new_sequence = [], []
    by_id = {}
    for g, cover in shared:
        cid = L.guid()
        source = rows[min(cover)]
        L.insert(file, lrs["centerline"], [{lrs["centerline_id"]: cid, "SHAPE@": R.with_z(g, source.shape)}])
        for i in cover:
            by_id.setdefault(str(rows[i].get(lrs["centerline_id"])), []).append(cid)
        lines.append(f"Overlap of {', '.join(str(rows[i].get(lrs['centerline_id'])) for i in sorted(cover))} -> {cid}.")
    for i in touched:
        rest = [g for g, cover in pieces if cover == {i}]
        own = str(rows[i].get(lrs["centerline_id"]))
        if rest:
            L.update(file, lrs["centerline"], rows[i].oid,
                     {"SHAPE@": R.with_z(shapely.union_all(rest), rows[i].shape)})
            by_id.setdefault(own, []).append(own)
        else:
            L.delete(file, lrs["centerline"], [rows[i].oid])
    doomed = []
    for s in sequence:
        cid = str(s.get(lrs["seq_centerline_id"]))
        if cid not in by_id:
            continue
        doomed.append(s.oid)
        for new_id in by_id[cid]:
            new_sequence.append({lrs["seq_centerline_id"]: new_id, lrs["seq_route_id"]: s.get(lrs["seq_route_id"]),
                                 lrs["seq_from"]: s.get(lrs["seq_from"]), lrs["seq_to"]: s.get(lrs["seq_to"]),
                                 lrs["seq_network_id"]: s.get(lrs["seq_network_id"])})
    L.delete(file, lrs["sequence"], doomed)
    L.insert(file, lrs["sequence"], new_sequence)
    return Result(L.full(file, table), _details(tool, lines))


def CalculateRouteConcurrencies(in_route_features, out_dataset, tvd=None, find_dominance="FIND_DOMINANCE",
                                include_geometry="EXCLUDE_GEOMETRY"):
    """A row for each route of each concurrent section — routes of the network sharing a centerline on the temporal
    view date (blank: all time) — with SectionId (one GUID per section), RouteId, FromMeasure, ToMeasure, FromDate,
    ToDate and, with FIND_DOMINANCE, DominantFlag and DominantError by the network's dominance rules. INCLUDE_GEOMETRY
    makes a feature class with the section's shape. INFERRED (§5.9 (h)): the field names beyond the three the page
    gives, a section being one centerline, and a tie broken by the smallest route ID with error 1."""
    tool = "CalculateRouteConcurrencies"
    dominant = L.choose(tool, "find_dominance", find_dominance, ("FIND_DOMINANCE", "NO_FIND_DOMINANCE"),
                        "FIND_DOMINANCE") == "FIND_DOMINANCE"
    geometry = L.choose(tool, "include_geometry", include_geometry, ("INCLUDE_GEOMETRY", "EXCLUDE_GEOMETRY"),
                        "EXCLUDE_GEOMETRY") == "INCLUDE_GEOMETRY"
    file, lrs, network, _ = _network(in_route_features, tool)
    sections = R.concurrencies(file, lrs, network, L.when(tvd), not L.given(tvd))
    columns = [("SectionId", _TEXT), ("RouteId", _TEXT), ("FromMeasure", _DOUBLE), ("ToMeasure", _DOUBLE),
               ("FromDate", _DATE), ("ToDate", _DATE)]
    if dominant:
        columns += [("DominantFlag", _SHORT), ("DominantError", _SHORT)]
    out = _output(out_dataset)
    _gpkg.create(out, "Polyline" if geometry else "Table", lrs["srid"] if geometry else 0,
                 [_gpkg.Column(n, t) for n, t in columns], geometry, geometry)
    records = []
    for section, shape, members, start, end in sections:
        flags = R.dominance(file, network, members) if dominant else {}
        for r, a, b in members:
            record = {"SectionId": section, "RouteId": str(L.route_id_of(network, r)),
                      "FromMeasure": None if math.isnan(a) else a, "ToMeasure": None if math.isnan(b) else b,
                      "FromDate": start, "ToDate": end}
            if dominant:
                record["DominantFlag"], record["DominantError"] = flags[r.oid]
            if geometry:
                record["SHAPE@"] = M.line(M.between(M.parts(r.shape), a, b)) if not math.isnan(a) else shape
            records.append(record)
    L.insert(*_gpkg.split(out), records)
    return Result(out)


def _output(value):
    from .management import _path
    return _path(L.text(value))


# ---- events ----------------------------------------------------------------------------------------------------------

def GenerateEvents(in_event_layer, bypass_events_with_null_lrs_fields="NO_BYPASS_EVENTS_WITH_NULL_LRS_FIELDS"):
    """Each event's shape regenerated from its route and measures, on the route record that holds its from date,
    and its LocError written. BYPASS leaves events with a null route ID or measure unchanged; the page makes it
    available only where the LRS is configured with a utility network (ConfigureUtilityNetworkFeatureClasses)."""
    tool = "GenerateEvents"
    bypass = L.choose(tool, "bypass_events_with_null_lrs_fields", bypass_events_with_null_lrs_fields,
                      ("BYPASS_EVENTS_WITH_NULL_LRS_FIELDS", "NO_BYPASS_EVENTS_WITH_NULL_LRS_FIELDS"),
                      "NO_BYPASS_EVENTS_WITH_NULL_LRS_FIELDS") == "BYPASS_EVENTS_WITH_NULL_LRS_FIELDS"
    file, lrs, network, event, where = _event(in_event_layer, tool)
    if bypass and not lrs.get("utility"):
        raise ExecuteError(f"ERROR 000800: {tool}: BYPASS_EVENTS_WITH_NULL_LRS_FIELDS is available only in a combined "
                           "LRS and utility network deployment.")
    lines = E.generate(file, event, network, bypass, where)
    return Result(L.full(file, event["table"]), _details(tool, lines))


def AppendEvents(in_dataset, in_target_event, field_mapping=None, load_type="ADD",
                 generate_event_ids="NO_GENERATE_EVENT_IDS", generate_shapes=None,
                 append_to_dominant_route="NO_APPEND_TO_DOMINANT_ROUTE",
                 bypass_conflict_prevention="NO_BYPASS_CONFLICT_PREVENTION"):
    """Rows appended to a registered event, its shape made from route and measure. The target's schema is fixed: a
    FieldMappings object maps fields (its merge rules ignored, as the page says), else fields of the same name. With
    no dates mapped an event begins with its route's current record and has no end; a to date without a from date is
    an error. A null route ID is matched by route name. RETIRE_OVERLAPS (line events) retires targets of the same route
    whose measures and time overlap, and deletes the eclipsed; RETIRE_BY_EVENT_ID and REPLACE_BY_EVENT_ID act on the
    same event ID. GENERATE_EVENT_IDS gives a GUID where the ID is null. APPEND_TO_DOMINANT_ROUTE moves each piece on a
    concurrent section to the dominant route. ``generate_shapes`` is, as its page says, no longer supported; conflict
    prevention is never enabled in a GeoPackage store (ModifyLRS refuses it), so there is no lock to bypass."""
    from .management import _mappings
    tool = "AppendEvents"
    kind = L.choose(tool, "load_type", load_type, ("ADD", "RETIRE_OVERLAPS", "RETIRE_BY_EVENT_ID",
                                                    "REPLACE_BY_EVENT_ID"), "ADD")
    ids = L.choose(tool, "generate_event_ids", generate_event_ids, ("GENERATE_EVENT_IDS", "NO_GENERATE_EVENT_IDS"),
                   "NO_GENERATE_EVENT_IDS") == "GENERATE_EVENT_IDS"
    dominant = L.choose(tool, "append_to_dominant_route", append_to_dominant_route,
                        ("APPEND_TO_DOMINANT_ROUTE", "NO_APPEND_TO_DOMINANT_ROUTE"),
                        "NO_APPEND_TO_DOMINANT_ROUTE") == "APPEND_TO_DOMINANT_ROUTE"
    L.choose(tool, "bypass_conflict_prevention", bypass_conflict_prevention,
             ("BYPASS_CONFLICT_PREVENTION", "NO_BYPASS_CONFLICT_PREVENTION"), "NO_BYPASS_CONFLICT_PREVENTION")
    if L.given(generate_shapes):
        AddWarning("AppendEvents: generate_shapes is no longer supported, as its page says; shapes are always made.")
    mappings = _mappings(tool, field_mapping)
    file, lrs, network, event, _ = _event(in_target_event, tool)
    if kind == "RETIRE_OVERLAPS" and not E.is_line(event):
        raise ExecuteError(f"ERROR 000800: {tool}: RETIRE_OVERLAPS is for line events.")
    _, _, shape, _, names, rows = _source_rows(in_dataset)
    if shape is not None and shape != ("Polyline" if E.is_line(event) else "Point"):
        raise ExecuteError(f"ERROR 000800: {tool}: the input and target must have the same geometry type.")
    connection = L.connect(file)
    try:
        target_columns = list(L.columns(connection, event["table"]).values())
        routes = L.route_rows(connection, network)
        existing = E.event_rows(connection, event)
    finally:
        connection.close()
    by_name = {}
    if network.get("route_name"):
        for r in routes:
            by_name.setdefault(str(r.get(network["route_name"])), str(L.route_id_of(network, r)))

    def mapped(row, column):
        if mappings is not None:
            index = mappings.findFieldMapIndex(column)
            if index == -1:
                return None, False
            fmap = mappings.getFieldMap(index)
            first = fmap.getInputFieldName(0) if fmap.inputFieldCount else None
            return (row.get(first) if first else None), first is not None
        return row.get(column), column.lower() in names

    records, issues = [], []
    for row in rows:
        record = {}
        for column in target_columns:
            value, present = mapped(row, column.name)
            if present:
                record[column.name] = L.when(value) if column.type == _DATE and value is not None else value
        start, has_start = mapped(row, event["from_date"])
        _, has_end = mapped(row, event["to_date"])
        if has_end and not has_start:
            raise ExecuteError(f"ERROR 000800: {tool}: a to date is mapped without a from date.")
        route_id = record.get(event["route_id"])
        if route_id is None and event.get("route_name") and record.get(event["route_name"]) is not None:
            route_id = by_name.get(str(record[event["route_name"]]))
            record[event["route_id"]] = route_id
        if not has_start:
            current = [r for r in routes if str(L.route_id_of(network, r)) == str(route_id)
                       and r.get(network["to_date"]) is None]
            record[event["from_date"]] = current[0].get(network["from_date"]) if current else None
            record[event["to_date"]] = None
        if record.get(event["event_id"]) is None and ids:
            record[event["event_id"]] = L.guid()
        records.append(record)
    if dominant:
        records = _to_dominant(file, lrs, network, event, records, issues)
    _load_events(file, event, kind, records, existing, issues)
    E.generate(file, event, network)
    return Result(L.full(file, event["table"]), _details(tool, issues))


def _load_events(file, event, kind, records, existing, issues):
    doomed, retire = set(), {}
    for record in records:
        start, end = record.get(event["from_date"]), record.get(event["to_date"])
        for e in existing:
            if e.oid in doomed:
                continue
            es, ee = e.get(event["from_date"]), e.get(event["to_date"])
            if kind in ("RETIRE_BY_EVENT_ID", "REPLACE_BY_EVENT_ID"):
                if record.get(event["event_id"]) is None or str(e.get(event["event_id"])) != str(
                        record.get(event["event_id"])):
                    continue
                if kind == "REPLACE_BY_EVENT_ID":
                    doomed.add(e.oid)
                    continue
            elif kind == "RETIRE_OVERLAPS":
                if str(e.get(event["route_id"])) != str(record.get(event["route_id"])):
                    continue
                a, b = sorted((record.get(event["measure"]) or 0, record.get(event["to_measure"]) or 0))
                ea, eb = sorted((e.get(event["measure"]) or 0, e.get(event["to_measure"]) or 0))
                if not (ea < b and a < eb):
                    continue
            else:
                continue
            if not L.overlaps(es, ee, start, end):
                continue
            if start is None or (es is not None and es >= start):
                if kind == "RETIRE_OVERLAPS" and not (a <= ea and eb <= b and (end is None or (ee is not None
                                                                                               and ee <= end))):
                    retire[e.oid] = start
                    continue
                doomed.add(e.oid)
            else:
                retire[e.oid] = start
    L.delete(file, event["table"], doomed)
    for oid, moment in retire.items():
        if oid not in doomed and moment is not None:
            L.update(file, event["table"], oid, {event["to_date"]: moment})
    if doomed or retire:
        issues.append(f"{len(doomed)} target event(s) deleted, {len(retire)} retired.")
    L.insert(file, event["table"], records)
    issues.append(f"{len(records)} event(s) appended.")


def _to_dominant(file, lrs, network, event, records, issues):
    """Each event cut at the concurrent sections where its route is not dominant, the cut piece moved to the dominant
    route there with the measures of that route."""
    sections = R.concurrencies(file, lrs, network, None, False)
    owner = {}
    for section, shape, members, start, end in sections:
        flags = R.dominance(file, network, members)
        top = next((r for r, _, _ in members if flags[r.oid][0] == 1), None)
        for r, a, b in members:
            if top is not None and r.oid != top.oid and not math.isnan(a):
                owner.setdefault(str(L.route_id_of(network, r)), []).append((a, b, top, members))
    out = []
    for record in records:
        route_id = str(record.get(event["route_id"]))
        cuts = owner.get(route_id)
        if not cuts:
            out.append(record)
            continue
        if not E.is_line(event):
            m = record.get(event["measure"])
            for a, b, top, members in cuts:
                if m is not None and a <= m <= b:
                    mine = next(x for x in members if str(L.route_id_of(network, x[0])) == route_id)
                    tm = _translate(mine, top, members, m)
                    record = dict(record, **{event["route_id"]: str(L.route_id_of(network, top)), event["measure"]: tm})
                    issues.append(f"Event {record.get(event['event_id'])}: moved to dominant route "
                                  f"{L.route_id_of(network, top)}.")
                    break
            out.append(record)
            continue
        lo, hi = sorted((record.get(event["measure"]), record.get(event["to_measure"])))
        pieces = [(lo, hi, None)]
        for a, b, top, members in sorted(cuts, key=lambda c: c[0]):
            nxt = []
            for p0, p1, where in pieces:
                if where is not None or b <= p0 or a >= p1:
                    nxt.append((p0, p1, where))
                    continue
                if p0 < a:
                    nxt.append((p0, a, None))
                nxt.append((max(p0, a), min(p1, b), (top, members)))
                if b < p1:
                    nxt.append((b, p1, None))
            pieces = nxt
        for i, (p0, p1, where) in enumerate(pieces):
            piece = dict(record)
            if i:
                piece[event["event_id"]] = L.guid()
            if where is not None:
                top, members = where
                mine = next(x for x in members if str(L.route_id_of(network, x[0])) == route_id)
                t0, t1 = sorted((_translate(mine, top, members, p0), _translate(mine, top, members, p1)))
                piece.update({event["route_id"]: str(L.route_id_of(network, top)), event["measure"]: t0,
                              event["to_measure"]: t1})
                issues.append(f"Event {record.get(event['event_id'])}: {p0}-{p1} moved to dominant route "
                              f"{L.route_id_of(network, top)}.")
            else:
                piece.update({event["measure"]: p0, event["to_measure"]: p1})
            out.append(piece)
    return out


def _translate(mine, top, members, m):
    """A measure on a subordinate route's section as the dominant route's measure at the same place."""
    route, a, b = mine
    location = M.at(M.parts(route.shape), m)
    found = M.locate(M.parts(top.shape), location[0], location[1], 1e-6) if location else []
    return found[0][0] if found else m


@equivalent("each route edit's behaviour rule applied by relocating the event's location on the old route onto the new "
            "one (Locate Features Along Routes)", "ArcGIS Pro, What is event behavior? and Apply Event Behaviors")
def ApplyEventBehaviors(in_route_features):
    """EQUIVALENT: for every route edit grapy's tools logged and not yet processed, every event of the network active
    on the edit's date is handled by its rule for that activity: STAY_PUT relocates its old location onto the route
    (split where only part of it is still there, retired where none is); MOVE and HONOR_ROUTE_MEASURE keep its
    measures; RETIRE retires the events STAY_PUT would change; SNAP moves the part off the route onto another route
    there; COVER stretches the event touching a new section over it; HONOR_REFERENT_LOCATION keeps its offset from an
    intersection referent. A changed record is closed on the edit's date and its replacement opened then. The edits
    are then marked processed and the shapes regenerated. INFERRED (§5.9 (l)): reassign is never logged, since no
    arcpy tool reassigns a route."""
    tool = "ApplyEventBehaviors"
    file, lrs, network, _ = _network(in_route_features, tool)
    connection = L.connect(file)
    try:
        entries = L.pending(connection, network["table"])
        events = L.events(connection, network["table"])
    finally:
        connection.close()
    lines = []
    for event in events:
        lines += E.apply(file, lrs, network, event, entries)
    connection = L.connect(file)
    try:
        for entry in entries:
            connection.execute("update grapy_lrs_edit_log set processed = 1 where id = ?", (entry["id"],))
        connection.commit()
    finally:
        connection.close()
    for event in events:
        E.generate(file, event, network)
    lines.append(f"{len(entries)} route edit(s) processed for {len(events)} event class(es).")
    return Result(";".join(L.full(file, e["table"]) for e in events), _details(tool, lines))


@equivalent(*M.LOCATE)
def DeriveEventMeasures(in_route_features, update_all_events="UPDATE_ALL", event_layers=None):
    """EQUIVALENT: each event with derived measure fields enabled, on a line network, located on the derived network's
    routes (Locate Features Along Routes): the derived route ID and name and the measures of its shape there. A route
    selection limits the events to those routes; event selections are ignored, as documented."""
    tool = "DeriveEventMeasures"
    some = L.choose(tool, "update_all_events", update_all_events, ("UPDATE_ALL", "UPDATE_SOME"), "UPDATE_ALL") == \
        "UPDATE_SOME"
    file, lrs, network, where = _network(in_route_features, tool)
    connection = L.connect(file)
    try:
        if network.get("derived_from"):
            network = L.network_get(connection, network["derived_from"])
            where = None
        if not network.get("derived"):
            raise ExecuteError(f"ERROR 000800: {tool}: the network has no derived network.")
        derived = L.network_get(connection, network["derived"])
        routes = {str(L.route_id_of(network, r)) for r in L.route_rows(connection, network, where)}
        wanted = None
        if some:
            wanted = {L.resolve(v)[1] for v in _rows(event_layers)}
        events = [e for e in L.events(connection, network["table"]) if e.get("derived")
                  and (wanted is None or e["table"] in wanted)]
        work = []
        tolerance = L.tolerance(lrs)
        for event in events:
            for row in E.event_rows(connection, event):
                if str(row.get(event["route_id"])) not in routes:
                    continue
                shape, _ = E.shape_of(connection, network, event, row)
                if shape is None:
                    continue
                found = E.locate_on_network(connection, derived, shape, row.get(event["from_date"]), tolerance,
                                            event["kind"])
                if not found:
                    continue
                r, a, b = found[0]
                values = {event["derived"]["route_id"]: str(L.route_id_of(derived, r)),
                          event["derived"]["from_measure"]: a}
                if event["derived"].get("to_measure"):
                    values[event["derived"]["to_measure"]] = b
                if event["derived"].get("route_name") and derived.get("route_name"):
                    values[event["derived"]["route_name"]] = r.get(derived["route_name"])
                work.append((event["table"], row.oid, values))
    finally:
        connection.close()
    for table, oid, values in work:
        L.update(file, table, oid, values)
    return Result(";".join(L.full(file, e["table"]) for e in events),
                  _details(tool, [f"{len(work)} event record(s) derived."]))


def _event_inputs(file, connection, lrs, values, tool):
    """Event layers as [(table, settings-like dict)]: registered events, and the utility network classes the LRS has
    configured (devices and junctions as point events, lines as line events)."""
    out = []
    utility = lrs.get("utility") or {}
    for value in values:
        f, table, where = L.resolve(value)
        event = L.event_get(connection, table)
        if event is None and table in (utility.get("line"), utility.get("device"), utility.get("junction")):
            key = [k for k in ("line", "device", "junction") if utility.get(k) == table][0]
            event = {"table": table, "kind": "LINE" if key == "line" else "POINT",
                     "route_id": utility[f"{key}_route_id"],
                     "measure": utility["line_from"] if key == "line" else utility[f"{key}_measure"],
                     "to_measure": utility["line_to"] if key == "line" else None, "from_date": None, "to_date": None,
                     "event_id": None, "network": None}
        if event is None:
            raise ExecuteError(f"ERROR 000800: {tool}: '{table}' is not a registered LRS event.")
        out.append((table, event, where))
    return out


@equivalent("Overlay Route Events' UNION applied across the event layers, split also at the events' from and to dates",
            "ArcGIS Pro, Overlay Route Events and Overlay Events")
def OverlayEvents(in_route_features, event_layers, output_dataset, include_geometry="EXCLUDE_GEOMETRY",
                  network_fields=None, address_block_split_type="PROPORTIONAL"):
    """EQUIVALENT: the events of the layers cut each other along each route of the target network into segments where
    the same events lie, and in time where their dates differ; one row per segment with Route_ID, From_Date, To_Date,
    From_Measure, To_Measure, Type ("Line" or "Point") and the target network's ``network_fields``, and for each layer
    its fields named ``<layer>_<field>`` from the one event of that layer there (INFERRED, §5.9 (f): the first by
    object ID when several overlap). Events of another network are first located on the target network. An address
    range layer (ConfigureAddressFeatureClasses) has its address numbers split in proportion to measure, or, by
    NEAREST_ADDRESS_POINT, taken from the nearest site address point of the same road."""
    tool = "OverlayEvents"
    geometry = L.choose(tool, "include_geometry", include_geometry, ("INCLUDE_GEOMETRY", "EXCLUDE_GEOMETRY"),
                        "EXCLUDE_GEOMETRY") == "INCLUDE_GEOMETRY"
    split = L.choose(tool, "address_block_split_type", address_block_split_type,
                     ("PROPORTIONAL", "NEAREST_ADDRESS_POINT"), "PROPORTIONAL")
    file, lrs, network, where = _network(in_route_features, tool)
    tolerance = L.tolerance(lrs)
    connection = L.connect(file)
    try:
        inputs = _event_inputs(file, connection, lrs, _rows(event_layers), tool)
        if split == "NEAREST_ADDRESS_POINT" and not lrs.get("address"):
            raise ExecuteError(f"ERROR 000800: {tool}: NEAREST_ADDRESS_POINT needs the address classes "
                               "(ConfigureAddressFeatureClasses).")
        routes = L.route_rows(connection, network, where)
        extra = [L.field(connection, network["table"], n) for n in _rows(network_fields)]
        layer_columns, placed = [], []
        for table, event, layer_where in inputs:
            own = [c for c in L.columns(connection, table).values()]
            prefix = table.rsplit("/", 1)[-1]
            layer_columns.append((prefix, own))
            entries = []
            event_network = L.network_get(connection, event["network"]) if event.get("network") else network
            rows = L.read(connection, table, layer_where, [c.name for c in own if c.type == _DATE])
            for row in rows:
                start = row.get(event["from_date"]) if event.get("from_date") else None
                end = row.get(event["to_date"]) if event.get("to_date") else None
                if event_network is not None and event_network["table"] == network["table"]:
                    a = row.get(event["measure"])
                    b = row.get(event["to_measure"]) if event.get("to_measure") else None
                    if a is None:
                        continue
                    entries.append((str(row.get(event["route_id"])), float(a), None if b is None else float(b),
                                    start, end, row))
                else:
                    shape = row.shape
                    if event.get("network") and event_network is not None:
                        shape, _ = E.shape_of(connection, event_network, event, row)
                    if shape is None:
                        continue
                    for r, a, b in E.locate_on_network(connection, network, shape, start, tolerance, event["kind"]):
                        entries.append((str(L.route_id_of(network, r)), a, b, start, end, row))
            placed.append((prefix, event, entries))
        sites = []
        if lrs.get("address"):
            address = lrs["address"]
            sites = [s for s in L.read(connection, address["sites"]) if s.shape is not None]
    finally:
        connection.close()
    columns = [("Route_ID", _TEXT), ("From_Date", _DATE), ("To_Date", _DATE), ("From_Measure", _DOUBLE),
               ("To_Measure", _DOUBLE), ("Type", _TEXT)]
    columns += [(n, L.columns(_open(file), network["table"])[n.lower()].type) for n in extra]
    for prefix, own in layer_columns:
        columns += [(f"{prefix}_{c.name}", c.type) for c in own]
    out = _output(output_dataset)
    _gpkg.create(out, "Polyline" if geometry else "Table", lrs["srid"] if geometry else 0,
                 [_gpkg.Column(n, t) for n, t in columns], geometry, geometry)
    records = []
    for route in routes:
        route_id = str(L.route_id_of(network, route))
        rs, re_ = route.get(network["from_date"]), route.get(network["to_date"])
        mine = [(prefix, event, [e for e in entries if e[0] == route_id and L.overlaps(e[3], e[4], rs, re_)])
                for prefix, event, entries in placed]
        times = sorted({t for _, _, es in mine for e in es for t in (e[3], e[4]) if t is not None
                        and L.active(rs, re_, t)} | ({rs} if rs else set()))
        windows = list(zip([rs] + times, times + [re_]))
        windows = [(a, b) for a, b in windows if not (a is not None and b is not None and a >= b)]
        windows = list(dict.fromkeys(windows))
        for start, end in windows:
            live = [(prefix, event, [e for e in es if L.overlaps(e[3], e[4], start, end) and
                                     L.active(e[3], e[4], start if start else (e[3] or None))])
                    for prefix, event, es in mine]
            cuts = sorted({m for _, _, es in live for e in es for m in (e[1], e[2]) if m is not None})
            for a, b in zip(cuts, cuts[1:]):
                if b - a <= 1e-12:
                    continue
                hits = {prefix: next((e for e in sorted(es, key=lambda e: e[5].oid) if e[2] is not None and
                                      min(e[1], e[2]) <= a and b <= max(e[1], e[2])), None)
                        for prefix, _, es in live}
                if not any(hits.values()):
                    continue
                records.append(_overlay_record(route, network, extra, layer_columns, hits, route_id, start, end, a, b,
                                               "Line", geometry, lrs, split, sites))
            for prefix, event, es in live:
                for e in es:
                    if e[2] is None:
                        hits = {p: next((x for x in sorted(xs, key=lambda x: x[5].oid) if
                                         (x is e) or (x[2] is not None and min(x[1], x[2]) <= e[1] <= max(x[1], x[2]))),
                                        None) for p, _, xs in live}
                        records.append(_overlay_record(route, network, extra, layer_columns, hits, route_id, start,
                                                       end, e[1], e[1], "Point", geometry, lrs, split, sites))
    L.insert(*_gpkg.split(out), records)
    return Result(out)


def _open(file):
    return L.connect(file)


def _overlay_record(route, network, extra, layer_columns, hits, route_id, start, end, a, b, kind, geometry, lrs, split,
                    sites):
    record = {"Route_ID": route_id, "From_Date": start, "To_Date": end, "From_Measure": a, "To_Measure": b,
              "Type": kind}
    for n in extra:
        record[n] = route.get(n)
    address = lrs.get("address") or {}
    for prefix, own in layer_columns:
        hit = hits.get(prefix)
        for c in own:
            record[f"{prefix}_{c.name}"] = hit[5].get(c.name) if hit else None
        if hit and address and hit[5] is not None and address.get("ranges", "").rsplit("/", 1)[-1] == prefix \
                and hit[2] is not None and kind == "Line":
            lo, hi = sorted((hit[1], hit[2]))
            for side in ("left", "right"):
                f0, f1 = hit[5].get(address[f"{side}_from"]), hit[5].get(address[f"{side}_to"])
                if f0 is None or f1 is None or hi == lo:
                    continue
                new = []
                for m in (a, b):
                    value = f0 + (m - lo) / (hi - lo) * (f1 - f0)
                    if split == "NEAREST_ADDRESS_POINT":
                        at = M.at(M.parts(route.shape), m)
                        road = hit[5].get(address["range_road"])
                        same = [s for s in sites if s.get(address["site_road"]) == road]
                        if at and same:
                            near = min(same, key=lambda s: math.hypot(s.shape.x - at[0], s.shape.y - at[1]))
                            try:
                                value = float(near.get(address["number"]))
                            except (TypeError, ValueError):
                                pass
                    new.append(int(round(value)))
                record[f"{prefix}_{address[f'{side}_from']}"], record[f"{prefix}_{address[f'{side}_to']}"] = new
    if geometry:
        pieces = M.parts(route.shape)
        if kind == "Point":
            found = M.at(pieces, a)
            record["SHAPE@"] = None if found is None else M.line([_degenerate(found, a)])
        else:
            record["SHAPE@"] = M.line(M.between(pieces, a, b))
    return record


def _degenerate(xyz, m):
    import numpy as np
    return np.array([[xyz[0], xyz[1], xyz[2], m], [xyz[0], xyz[1], xyz[2], m]])


@equivalent(*M.LOCATE)
def TranslateEventMeasures(in_source_event, in_target_route_features, out_target_event,
                           in_concurrent_route_matching="ANY"):
    """EQUIVALENT: each source event's shape (from its own route and measures) located on the target network's
    routes, written to a new class with the source's fields, the route ID and measures those of the target, and
    ``Loc_Error``: No Error, Partial Match (only part of it lies on the route), No Match, or Multi-Match (ANY, when
    more than one route was found; INFERRED, §5.9 (f)). ANY takes the first route by ID, ROUTE_ID the route of the
    source's own ID, ALL one record for each."""
    tool = "TranslateEventMeasures"
    mode = L.choose(tool, "in_concurrent_route_matching", in_concurrent_route_matching, ("ANY", "ROUTE_ID", "ALL"),
                    "ANY")
    file, lrs, source_network, event, where = _event(in_source_event, tool)
    f2, target_lrs, target, _ = _network(in_target_route_features, tool)
    tolerance = L.tolerance(lrs)
    connection = L.connect(file)
    try:
        columns = list(L.columns(connection, event["table"]).values())
        kind, srid, _, _ = L.shape_info(connection, event["table"])
        results = []
        for row in E.event_rows(connection, event, where):
            shape, _ = E.shape_of(connection, source_network, event, row)
            found = [] if shape is None else E.locate_on_network(connection, target, shape,
                                                                  row.get(event["from_date"]), tolerance, event["kind"])
            if mode == "ROUTE_ID":
                found = [f for f in found if str(L.route_id_of(target, f[0])) == str(row.get(event["route_id"]))]
            found.sort(key=lambda f: str(L.route_id_of(target, f[0])))
            error = E.NO_MATCH if not found else E.NO_ERROR
            if found and mode == "ANY" and len({str(L.route_id_of(target, f[0])) for f in found}) > 1:
                error = "Multi-Match"
            chosen = found if mode == "ALL" else found[:1]
            for r, a, b in chosen or [(None, None, None)]:
                values = {c.name: row.get(c.name) for c in columns}
                this = error
                if r is not None:
                    values[event["route_id"]] = str(L.route_id_of(target, r))
                    values[event["measure"]] = a
                    if E.is_line(event):
                        values[event["to_measure"]] = b
                        if shape is not None and abs((b - a) - shapely.force_2d(shape).length *
                                                     L.unit_ratio(lrs["srid"], target["unit"])) > tolerance * 10:
                            this = E.PARTIAL if this == E.NO_ERROR else this
                values["Loc_Error"] = this
                values["SHAPE@"] = shape
                results.append(values)
    finally:
        connection.close()
    out = _output(out_target_event)
    _gpkg.create(out, kind or "Point", srid, [_gpkg.Column(c.name, c.type) for c in columns] +
                 [_gpkg.Column("Loc_Error", _TEXT)], True, True)
    L.insert(*_gpkg.split(out), results)
    return Result(out)


def UpdateMeasuresFromLRS(lrs_network, lrs_date, in_features, route_id_field, from_measure_field,
                          to_measure_field=None, to_route_id_field=None, route_name_field=None,
                          to_route_name_field=None, search_tolerance=None):
    """Each point's route ID and measure, and each line's from and to measures (and to route on a line), from the
    routes active on ``lrs_date``: a route the feature lies on, else the nearest within ``search_tolerance`` (in the
    network's XY units unless a unit is given). A line is updated only when both ends find a route. The measure is the
    route's at the nearest location (INFERRED, §5.9 (f)); a feature that finds none is left as it was. The details
    file is a CSV of the values before and after."""
    tool = "UpdateMeasuresFromLRS"
    file, lrs, network, _ = _network(lrs_network, tool)
    f2, table, where = L.resolve(in_features)
    moment = L.when(lrs_date)
    tolerance = L.tolerance(lrs)
    reach = tolerance
    if L.given(search_tolerance):
        parts_ = str(search_tolerance).split()
        reach = float(parts_[0])
        if len(parts_) > 1:
            from . import _units
            metres = _units.linear(parts_[1])
            if metres is None:
                raise ExecuteError(f"ERROR 000800: {tool}: the unit '{parts_[1]}'.")
            from ._reference import SpatialReference
            reach = reach * metres / (SpatialReference(lrs["srid"]).metersPerUnit if lrs["srid"] else 1.0)
    connection = L.connect(file)
    try:
        routes = L.routes_active(connection, network, moment)
    finally:
        connection.close()
    other = L.connect(f2)
    try:
        kind, srid, _, _ = L.shape_info(other, table)
        if srid != lrs["srid"]:
            raise ExecuteError(f"ERROR 000800: {tool}: the features must share the network's spatial reference.")
        rows = L.read(other, table, where)
        fields = {k: L.field(other, table, v) if L.given(v) else None for k, v in
                  (("route", route_id_field), ("from", from_measure_field), ("to", to_measure_field),
                   ("to_route", to_route_id_field), ("name", route_name_field), ("to_name", to_route_name_field))}
    finally:
        other.close()

    def nearest(x, y):
        best = None
        for r in routes:
            found = M.locate(M.parts(r.shape), x, y, reach)
            if found and (best is None or found[0][1] < best[2] - 1e-12):
                best = (r, found[0][0], found[0][1])
        return best

    report, changes = [], []
    for row in rows:
        if row.shape is None:
            continue
        values = {}
        if kind == "Point":
            hit = nearest(row.shape.x, row.shape.y)
            if hit is None:
                continue
            values[fields["route"]] = str(L.route_id_of(network, hit[0]))
            values[fields["from"]] = hit[1]
            if fields["name"] and network.get("route_name"):
                values[fields["name"]] = hit[0].get(network["route_name"])
        else:
            pieces = M.parts(row.shape)
            first, last = pieces[0][0], pieces[-1][-1]
            candidates = []
            for r in routes:
                p = M.parts(r.shape)
                a = M.locate(p, first[0], first[1], reach)
                b = M.locate(p, last[0], last[1], reach)
                candidates.append((r, a[0] if a else None, b[0] if b else None))
            both = [c for c in candidates if c[1] and c[2]]
            if both:
                r, a, b = min(both, key=lambda c: max(c[1][1], c[2][1]))
                start_hit, end_hit = (r, a[0]), (r, b[0])
            else:
                starts = [(c[0], c[1]) for c in candidates if c[1]]
                ends = [(c[0], c[2]) for c in candidates if c[2]]
                if not (starts and ends and fields["to_route"] and network.get("line")):
                    continue
                s = min(starts, key=lambda c: c[1][1])
                e = min(ends, key=lambda c: c[1][1])
                if str(s[0].get(network["line_id"])) != str(e[0].get(network["line_id"])):
                    continue
                start_hit, end_hit = (s[0], s[1][0]), (e[0], e[1][0])
            values[fields["route"]] = str(L.route_id_of(network, start_hit[0]))
            values[fields["from"]] = start_hit[1]
            if fields["to"]:
                values[fields["to"]] = end_hit[1]
            if fields["to_route"]:
                values[fields["to_route"]] = str(L.route_id_of(network, end_hit[0]))
            if fields["name"] and network.get("route_name"):
                values[fields["name"]] = start_hit[0].get(network["route_name"])
            if fields["to_name"] and network.get("route_name"):
                values[fields["to_name"]] = end_hit[0].get(network["route_name"])
        report.append([row.oid] + [row.get(k) for k in values] + list(values.values()))
        changes.append((row.oid, values))
    for oid, values in changes:
        L.update(f2, table, oid, values)
    from ._runtime import env
    path = os.path.join(env.scratchFolder or os.getcwd(),
                        f"UpdateMeasuresFromLRS_{datetime.datetime.now().strftime('%Y%m%d%H%M%S%f')}.csv")
    with open(path, "w", newline="", encoding="utf-8") as handle:
        writer = csv.writer(handle)
        writer.writerow(["ObjectID", "Before", "After"])
        for line in report:
            half = (len(line) - 1) // 2
            writer.writerow([line[0], ";".join(map(str, line[1:1 + half])), ";".join(map(str, line[1 + half:]))])
    return Result(path, L.full(f2, table))


# ---- intersections ---------------------------------------------------------------------------------------------------

@equivalent(*M.LOCATE)
def CalculateIntersectingRouteMeasures(in_intersection_feature_class, out_dataset, tvd=None):
    """EQUIVALENT: a table of every route of the intersection class's network that each intersection point lies on,
    with its measure there: IntersectionId, IntersectionName, RouteId, Measure, FromDate, ToDate (INFERRED, §5.9 (f):
    the page gives no schema), for the routes active on the temporal view date (blank: the current routes)."""
    tool = "CalculateIntersectingRouteMeasures"
    file, table, where = L.resolve(in_intersection_feature_class)
    connection = L.connect(file)
    try:
        settings = L.intersection_get(connection, table)
        if settings is None:
            raise ExecuteError(f"ERROR 000800: {tool}: '{table}' is not a registered LRS intersection class.")
        network = L.network_get(connection, settings["network"])
        lrs = L.lrs_get(connection, network["lrs"])
        routes = L.routes_active(connection, network, L.when(tvd))
        points = L.read(connection, table, where)
    finally:
        connection.close()
    tolerance = L.tolerance(lrs)
    records = []
    for p in points:
        if p.shape is None:
            continue
        for r in routes:
            found = M.locate(M.parts(r.shape), p.shape.x, p.shape.y, tolerance)
            if found:
                records.append({"IntersectionId": p.get(settings["intersection_id"]),
                                "IntersectionName": p.get(settings["intersection_name"]),
                                "RouteId": str(L.route_id_of(network, r)), "Measure": found[0][0],
                                "FromDate": r.get(network["from_date"]), "ToDate": r.get(network["to_date"])})
    out = _output(out_dataset)
    _gpkg.create(out, "Table", 0, [_gpkg.Column(n, t) for n, t in (
        ("IntersectionId", _TEXT), ("IntersectionName", _TEXT), ("RouteId", _TEXT), ("Measure", _DOUBLE),
        ("FromDate", _DATE), ("ToDate", _DATE))])
    L.insert(*_gpkg.split(out), records)
    return Result(out)


def GenerateIntersections(in_intersection_feature_class, in_network_layer=None, start_date=None,
                          edited_by_current_user="CURRENT_USER", bypass_conflict_prevention="NO_BYPASS_CONFLICT_PREVENTION"):
    """Intersection points between the network's routes and the registered layers, made again for the routes
    processed: those of the network layer's selection and query (all routes of a selected ID across time), edited
    since ``start_date`` and, with CURRENT_USER, last edited by this user — both read from the network's editor
    tracking (ADR-240), which the page requires for them. A route does not intersect itself. Each point is named
    ``<network description><separator><layer description>``; with CONSIDER_Z a crossing counts only where the z
    values agree within the z tolerance. INFERRED (§5.9 (f)): a processed route's earlier intersection points are
    deleted and remade, one per route record, with the record's dates; with no editor tracking, CURRENT_USER (the
    default) cannot narrow and every route is processed, which is said; locks never exist, so there is none to
    bypass."""
    from . import _behaviours
    tool = "GenerateIntersections"
    user_only = L.choose(tool, "edited_by_current_user", edited_by_current_user, ("CURRENT_USER", "ALL_USERS"),
                         "CURRENT_USER") == "CURRENT_USER"
    L.choose(tool, "bypass_conflict_prevention", bypass_conflict_prevention,
             ("BYPASS_CONFLICT_PREVENTION", "NO_BYPASS_CONFLICT_PREVENTION"), "NO_BYPASS_CONFLICT_PREVENTION")
    file, table, _ = L.resolve(in_intersection_feature_class)
    connection = L.connect(file)
    try:
        settings = L.intersection_get(connection, table)
        if settings is None:
            raise ExecuteError(f"ERROR 000800: {tool}: '{table}' is not a registered LRS intersection class.")
        network = L.network_get(connection, settings["network"])
        lrs = L.lrs_get(connection, network["lrs"])
        where = None
        if L.given(in_network_layer):
            f2, net_table, where = L.resolve(in_network_layer)
            if net_table != network["table"]:
                raise ExecuteError(f"ERROR 000800: {tool}: the network layer is not the intersection class's network.")
        routes = L.route_rows(connection, network)
        chosen = L.route_rows(connection, network, where) if where else routes
        tracked = _behaviours.tracking(connection, network["table"])
        if tracked is None and L.given(start_date):
            raise ExecuteError(f"ERROR 000800: {tool}: start_date needs editor tracking on the network.")
        if tracked is not None:
            _, _, editor, edited, _ = tracked
            if L.given(start_date) and edited:
                since = L.when(start_date)
                chosen = [r for r in chosen if L.when(r.get(edited)) is not None and L.when(r.get(edited)) >= since]
            if user_only and editor:
                me = _behaviours.user()
                chosen = [r for r in chosen if str(r.get(editor) or "").upper() == me]
        elif user_only:
            AddWarning(f"{tool}: the network has no editor tracking, so CURRENT_USER cannot narrow the routes; every "
                       "route is processed.")
        ids = {str(L.route_id_of(network, r)) for r in chosen}
        targets = [r for r in routes if str(L.route_id_of(network, r)) in ids and r.shape is not None]
        layers = []
        for layer in settings["layers"]:
            dates = ()
            other_net = L.network_get(connection, layer["table"])
            if other_net:
                dates = (other_net["from_date"], other_net["to_date"])
            layers.append((layer, other_net, [f for f in L.read(connection, layer["table"], None, dates)
                                              if f.shape is not None]))
        old = [p.oid for p in L.read(connection, table) if str(p.get(settings["route_id"])) in ids]
    finally:
        connection.close()
    tolerance = L.tolerance(lrs)
    records = []
    for route in targets:
        pieces = M.parts(route.shape)
        flat = shapely.force_2d(route.shape)
        rid = str(L.route_id_of(network, route))
        rs, re_ = route.get(network["from_date"]), route.get(network["to_date"])
        for layer, other_net, features in layers:
            for f in features:
                start, end = rs, re_
                if other_net is not None:
                    if str(L.route_id_of(other_net, f)) == rid and other_net["table"] == network["table"]:
                        continue
                    fs, fe = f.get(other_net["from_date"]), f.get(other_net["to_date"])
                    if not L.overlaps(rs, re_, fs, fe):
                        continue
                    start, end = R._later(rs, fs), R._earlier(re_, fe)
                crossing = flat.intersection(shapely.force_2d(f.shape.boundary if f.shape.geom_type in (
                    "Polygon", "MultiPolygon") else f.shape))
                for point in _points_of(crossing):
                    found = M.locate(pieces, point.x, point.y, tolerance)
                    if not found:
                        continue
                    measure = found[0][0]
                    here = M.at(pieces, measure)
                    if settings.get("consider_z") and here is not None:
                        theirs = _z_at(f.shape, point)
                        if theirs is not None and abs(theirs - here[2]) > settings["z_tolerance"]:
                            continue
                    if any(r_["RouteId"] == rid and r_["FeatureId"] == str(f.get(layer["id_field"])) and
                           abs(r_["Measure"] - measure) <= tolerance and r_["FromDate"] == start for r_ in records):
                        continue
                    name = f"{route.get(settings['network_description_field'])}{layer['separator']}" \
                           f"{f.get(layer['description_field'])}"
                    records.append({"IntersectionId": L.guid(), "IntersectionName": name, "RouteId": rid,
                                    "FeatureId": str(f.get(layer["id_field"])),
                                    "FeatureClassName": layer["table"].rsplit("/", 1)[-1], "FromDate": start,
                                    "ToDate": end, "Measure": measure,
                                    "SHAPE@": M.point(point.x, point.y, here[2] if here else 0.0, measure)})
    L.delete(file, table, old)
    renamed = [{settings[k]: r[n] for k, n, _ in _INTERSECTION_FIELDS} | {"SHAPE@": r["SHAPE@"]} for r in records]
    L.insert(file, table, renamed)
    return Result(L.full(file, table), _details(tool, [f"{len(records)} intersection(s) for {len(ids)} route(s)."]))


def _points_of(geometry):
    if geometry is None or geometry.is_empty:
        return []
    if geometry.geom_type == "Point":
        return [geometry]
    if geometry.geom_type in ("LineString", "LinearRing"):
        return [shapely.Point(geometry.coords[0]), shapely.Point(geometry.coords[-1])]
    out = []
    for g in geometry.geoms:
        out += _points_of(g)
    return out


def _z_at(shape, point):
    if not shapely.has_z(shape):
        return None
    for p in M.parts(shape):
        if len(p) == 1:
            return float(p[0, 2])
        near = M.candidates([p], point.x, point.y, 1e-6)
        if near:
            _, _, _, i, t = near[0]
            return float(p[i, 2] + t * (p[i + 1, 2] - p[i, 2]))
    return None
