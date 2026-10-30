"""The arcpy tools of ADR-235: attribute rules, field groups and contingent values, and database sequences, each
built from its tool page on doc.esri.com and kept in the GeoPackage by grapy._rules and grapy._sequences.

management.py and da.py name them; they live here so the shared modules stay as they were.
"""
from __future__ import annotations

import csv
import datetime
import json
import os

from . import _gpkg, _rules
from ._result import Result
from ._runtime import ExecuteError, NotBuiltError

_TRUE = ("TRUE", "1", "YES", "Y")


def _text(value):
    return os.fspath(value) if isinstance(value, os.PathLike) else str(value)


def _list(value):
    if value is None or value == "":
        return []
    if isinstance(value, (list, tuple)):
        return [v for v in value]
    return [v.strip().strip("'\"") for v in str(value).split(";") if v.strip()]


def _table(tool, in_table):
    """(connection, table) of the dataset a tool changes — a layer's source, whole."""
    from .management import _model_table
    return _model_table(tool, in_table)


def _keyword(tool, name, value, allowed, default):
    word = default if value in (None, "", "#") else str(value).strip().upper()
    if isinstance(value, bool):
        word = allowed[0] if value else allowed[1]
    if word not in allowed:
        raise ExecuteError(f"ERROR 000800: {tool}'s {name} is one of {', '.join(allowed)}; '{value}' was given.")
    return word


def _columns(connection, table):
    columns, _, _, geometry = _gpkg.describe(connection, table)
    return {c.name.lower(): c for c in columns}, geometry


def _subtype_list(tool, connection, table, value):
    """Subtype codes from names or codes; "ALL" and nothing mean every subtype ([])."""
    from . import _subtypes
    items = _list(value)
    if not items or (len(items) == 1 and str(items[0]).upper() == "ALL"):
        return []
    held = _subtypes.field(connection, table)
    if held is None:
        raise ExecuteError(f"ERROR 000800: {tool}: '{table}' has no subtypes.")
    codes = _subtypes.codes(connection, table)
    out = []
    for item in items:
        text = str(item).split(":", 1)[0].strip()
        found = next((c for c, n in codes.items() if n.lower() == str(item).strip().lower()), None)
        if found is None:
            try:
                found = int(float(text))
            except ValueError:
                found = None
        if found is None or found not in codes:
            raise ExecuteError(f"ERROR 000800: {tool}: '{table}' has no subtype '{item}'.")
        out.append(found)
    return sorted(set(out))


def _field_list(tool, connection, table, value):
    columns, geometry = _columns(connection, table)
    out = []
    for name in _list(value):
        lower = str(name).lower()
        if lower in ("shape", (geometry or "").lower()):
            out.append(geometry or "Shape")
        elif lower in columns:
            out.append(columns[lower].name)
        else:
            raise ExecuteError(f"ERROR 000728: {tool}: field '{name}' does not exist in '{table}'.")
    return out


def _events(tool, value):
    out = [str(e).strip().upper() for e in _list(value)]
    for event in out:
        if event not in ("INSERT", "UPDATE", "DELETE"):
            raise ExecuteError(f"ERROR 000800: {tool}: '{event}' is not a triggering event: INSERT, UPDATE or DELETE.")
    return out


def _compile(tool, script):
    from ._arcade import ArcadeError, compile_script
    if not isinstance(script, str) or not script.strip():
        raise ExecuteError(f"ERROR 000735: {tool}: the script expression is required.")
    try:
        compile_script(script)
    except ArcadeError as error:
        raise ExecuteError(f"ERROR 002717: {tool}: the Arcade expression is not valid: {error}") from None


# ---- attribute rules ---------------------------------------------------------------------------------------------------


def AddAttributeRule(in_table, name, type, script_expression, is_editable="EDITABLE", triggering_events=None,
                     error_number=None, error_message=None, description=None, subtype=None, field=None,
                     exclude_from_client_evaluation="INCLUDE", batch="NOT_BATCH", severity=None, tags=None,
                     triggering_fields=None):
    """An attribute rule added to a table, as the tool page defines it: a CALCULATION rule sets ``field`` (or returns
    a dictionary), a CONSTRAINT rule accepts or refuses the edit, a VALIDATION rule is evaluated in batch. Its Arcade
    expression is compiled when it is added, so a script that does not parse is refused here.

    As documented: names are unique and at most 64 characters; error number and message are required for constraint
    and validation rules; validation rules are batch and constraint rules are not; triggering events do not apply to
    batch calculation rules; severity (1–5) is for validation rules; no triggering fields means every field.
    INFERRED (ADR-235 §5.8): an immediate rule names at least one triggering event; a validation rule is made batch
    whatever ``batch`` says, since the page allows no other value."""
    tool = "AddAttributeRule"
    kind = _keyword(tool, "type", type, ("CALCULATION", "CONSTRAINT", "VALIDATION"), None)
    name = str(name or "").strip()
    if not name or len(name) > 64:
        raise ExecuteError(f"ERROR 000800: {tool}: a rule's name is 1 to 64 characters.")
    _compile(tool, script_expression)
    editable = _keyword(tool, "is_editable", is_editable, ("EDITABLE", "NONEDITABLE"), "EDITABLE")
    batched = _keyword(tool, "batch", batch, ("BATCH", "NOT_BATCH"), "NOT_BATCH") == "BATCH"
    exclude = _keyword(tool, "exclude_from_client_evaluation", exclude_from_client_evaluation, ("EXCLUDE", "INCLUDE"),
                       "INCLUDE") == "EXCLUDE"
    if kind == "VALIDATION":
        batched = True
    if kind == "CONSTRAINT" and batched:
        raise ExecuteError(f"ERROR 000800: {tool}: a constraint rule is NOT_BATCH.")
    if kind != "CALCULATION" and (error_number in (None, "") or error_message in (None, "")):
        raise ExecuteError(f"ERROR 000735: {tool}: a {kind.lower()} rule needs an error number and an error message.")
    if error_message is not None and len(str(error_message)) > 256:
        raise ExecuteError(f"ERROR 000800: {tool}: the error message is at most 256 characters.")
    if description is not None and len(str(description)) > 256:
        raise ExecuteError(f"ERROR 000800: {tool}: the description is at most 256 characters.")
    if kind != "CALCULATION" and editable == "NONEDITABLE":
        raise ExecuteError(f"ERROR 000800: {tool}: is_editable applies to calculation rules only.")
    if kind != "CALCULATION" and field not in (None, ""):
        raise ExecuteError(f"ERROR 000800: {tool}: field applies to calculation rules only.")
    if severity not in (None, ""):
        if kind != "VALIDATION":
            raise ExecuteError(f"ERROR 000800: {tool}: severity applies to validation rules only.")
        if int(severity) not in range(1, 6):
            raise ExecuteError(f"ERROR 000800: {tool}: severity is 1 to 5.")
    events = _events(tool, triggering_events)
    if batched and events:
        raise ExecuteError(f"ERROR 000800: {tool}: triggering events do not apply to a batch rule.")
    if not batched and not events:
        raise ExecuteError(f"ERROR 000735: {tool}: an immediate {kind.lower()} rule needs a triggering event: INSERT, "
                           "UPDATE or DELETE.")
    connection, table = _table(tool, in_table)
    try:
        if _rules.rule(connection, table, name) is not None:
            raise ExecuteError(f"ERROR 000800: {tool}: '{table}' already has a rule named '{name}'.")
        target = None
        if field not in (None, ""):
            target = _field_list(tool, connection, table, [field])[0]
        fields = _field_list(tool, connection, table, triggering_fields)
        if fields and batched:
            raise ExecuteError(f"ERROR 000800: {tool}: triggering fields do not apply to a batch rule.")
        _rules.add(connection, table, {
            "name": name, "type": kind, "script": script_expression, "field": target,
            "user_editable": int(editable == "EDITABLE"), "on_insert": int("INSERT" in events),
            "on_update": int("UPDATE" in events), "on_delete": int("DELETE" in events),
            "error_number": None if error_number in (None, "") else str(error_number),
            "error_message": error_message or None, "description": description or None,
            "subtypes": json.dumps(_subtype_list(tool, connection, table, subtype)), "exclude_client": int(exclude),
            "batch": int(batched), "severity": int(severity) if severity not in (None, "") else None,
            "tags": ";".join(str(t) for t in _list(tags)) or None, "triggering_fields": json.dumps(fields),
            "enabled": 1})
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_table))


def AlterAttributeRule(in_table, name, description=None, error_number=None, error_message=None, tags=None,
                       triggering_events=None, script_expression=None, exclude_from_client_evaluation=None,
                       triggering_fields=None, subtype=None):
    """A rule's properties changed, as documented: an empty value keeps one; RESET clears a description, tags, and —
    on a calculation rule only — the error number and message; new triggering events and tags replace the old ones;
    triggering events apply to immediate calculation and constraint rules, exclude_from_client_evaluation not to
    validation or batch calculation rules."""
    tool = "AlterAttributeRule"
    connection, table = _table(tool, in_table)
    try:
        found = _rules.rule(connection, table, name)
        if found is None:
            raise ExecuteError(f"ERROR 000800: {tool}: '{table}' has no rule named '{name}'.")
        changes = {}

        def reset(value):
            return isinstance(value, str) and value.strip().upper() == "RESET"

        if description not in (None, ""):
            changes["description"] = None if reset(description) else str(description)
        for column, value in (("error_number", error_number), ("error_message", error_message)):
            if value in (None, ""):
                continue
            if reset(value):
                if found.kind != "CALCULATION":
                    raise ExecuteError(f"ERROR 000800: {tool}: the error number and message of a {found.kind.lower()} "
                                       "rule cannot be cleared.")
                changes[column] = None
            else:
                changes[column] = str(value)
        if tags not in (None, ""):
            changes["tags"] = None if reset(tags) else ";".join(str(t) for t in _list(tags))
        if triggering_events not in (None, ""):
            if found.batch or found.kind == "VALIDATION":
                raise ExecuteError(f"ERROR 000800: {tool}: triggering events apply to immediate calculation and "
                                   "constraint rules only.")
            events = _events(tool, triggering_events)
            changes.update(on_insert=int("INSERT" in events), on_update=int("UPDATE" in events),
                           on_delete=int("DELETE" in events))
        if script_expression not in (None, ""):
            _compile(tool, script_expression)
            changes["script"] = script_expression
        if exclude_from_client_evaluation not in (None, ""):
            if found.batch or found.kind == "VALIDATION":
                raise ExecuteError(f"ERROR 000800: {tool}: exclude_from_client_evaluation does not apply to validation "
                                   "or batch calculation rules.")
            changes["exclude_client"] = int(_keyword(tool, "exclude_from_client_evaluation",
                                                     exclude_from_client_evaluation, ("EXCLUDE", "INCLUDE"),
                                                     "INCLUDE") == "EXCLUDE")
        if triggering_fields not in (None, ""):
            changes["triggering_fields"] = json.dumps(_field_list(tool, connection, table, triggering_fields))
        if subtype not in (None, ""):
            changes["subtypes"] = json.dumps(_subtype_list(tool, connection, table, subtype))
        if changes:
            _rules.update(connection, table, found.name, **changes)
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_table))


def _named(tool, connection, table, names, kind):
    """The rules ``names`` names; with ``type``, only those of that type (a rule of another type is left as it is, as
    the Disable page says)."""
    wanted = [str(n) for n in _list(names)]
    if not wanted:
        raise ExecuteError(f"ERROR 000735: {tool}: name at least one rule.")
    kind = _keyword(tool, "type", kind, ("CALCULATION", "CONSTRAINT", "VALIDATION"), None) if kind not in (None, "") \
        else None
    out = []
    for name in wanted:
        found = _rules.rule(connection, table, name)
        if found is None:
            raise ExecuteError(f"ERROR 000800: {tool}: '{table}' has no rule named '{name}'.")
        if kind is None or found.kind == kind:
            out.append(found)
    return out


def DeleteAttributeRule(in_table, names, type=None):
    """Rules removed by name; with ``type``, only those of that type."""
    connection, table = _table("DeleteAttributeRule", in_table)
    try:
        for found in _named("DeleteAttributeRule", connection, table, names, type):
            connection.execute(f"delete from {_rules.RULES} where table_name = ? and name = ?", (table, found.name))
        _rules.renumber(connection, table)
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_table))


def _enable(tool, in_table, names, kind, on):
    connection, table = _table(tool, in_table)
    try:
        for found in _named(tool, connection, table, names, kind):
            _rules.update(connection, table, found.name, enabled=int(on))
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_table))


def DisableAttributeRules(in_table, names, type=None):
    """Rules disabled: they stop running until EnableAttributeRules. With ``type``, a rule of another type is left as
    it is, as documented."""
    return _enable("DisableAttributeRules", in_table, names, type, False)


def EnableAttributeRules(in_table, names, type=None):
    """Rules enabled again; new rules are enabled already, as documented."""
    return _enable("EnableAttributeRules", in_table, names, type, True)


def ReorderAttributeRule(in_table, name, evaluation_order):
    """An immediate calculation or constraint rule moved to ``evaluation_order`` (from 1): the rules at and after that
    place shift down by one, as documented."""
    tool = "ReorderAttributeRule"
    connection, table = _table(tool, in_table)
    try:
        found = _rules.rule(connection, table, name)
        if found is None:
            raise ExecuteError(f"ERROR 000800: {tool}: '{table}' has no rule named '{name}'.")
        if found.batch or found.kind == "VALIDATION":
            raise ExecuteError(f"ERROR 000800: {tool}: only immediate calculation and constraint rules are ordered.")
        order = int(evaluation_order)
        if order < 1:
            raise ExecuteError(f"ERROR 000800: {tool}: the evaluation order starts at 1.")
        held = [r for r in _rules.rules(connection, table) if r.name != found.name]
        immediate = [r for r in held if not r.batch and r.kind != "VALIDATION"]
        others = [r for r in held if r.batch or r.kind == "VALIDATION"]
        immediate.insert(min(order, len(immediate) + 1) - 1, found)
        for at, each in enumerate(immediate + others, start=1):
            _rules.update(connection, table, each.name, evaluation_order=at)
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_table))


_EXPORT = ("NAME", "DESCRIPTION", "TYPE", "SUBTYPE", "FIELD", "ISEDITABLE", "TRIGGERINSERT", "TRIGGERDELETE",
           "TRIGGERUPDATE", "SCRIPTEXPRESSION", "ERRORNUMBER", "ERRORMESSAGE", "EXCLUDECLIENTEVALUATION", "ISENABLED",
           "BATCH", "SEVERITY", "TAGS", "CATEGORY", "CHECKPARAMETERS", "TRIGGERINGFIELDS")


def _flag(value):
    return "True" if value else "False"


def ExportAttributeRules(in_table, out_csv_file):
    """A table's rules written to a .csv file with the page's twenty columns, in its order. INFERRED (ADR-235 §5.8):
    TYPE is the tool's keyword (CALCULATION …); SUBTYPE holds the subtype names, ``;`` between several, empty for all;
    TRIGGERINGFIELDS is a JSON list, ``[]`` for every field, as the page writes it; CATEGORY and CHECKPARAMETERS, which
    Data Reviewer rules fill, are empty."""
    from . import _subtypes
    connection, table = _table("ExportAttributeRules", in_table)
    try:
        names = _subtypes.codes(connection, table)
        found = _rules.rules(connection, table)
    finally:
        connection.close()
    with open(_text(out_csv_file), "w", encoding="utf-8", newline="") as written:
        out = csv.writer(written)
        out.writerow(_EXPORT)
        for rule in found:
            out.writerow([rule.name, rule.description, rule.kind, ";".join(names.get(c, str(c)) for c in rule.subtypeCodes),
                          rule.fieldName, _flag(rule.userEditable), _flag("INSERT" in rule.events),
                          _flag("DELETE" in rule.events), _flag("UPDATE" in rule.events), rule.scriptExpression,
                          "" if rule.errorNumber in (None, "") else rule.errorNumber, rule.errorMessage,
                          _flag(rule.excludeFromClientEvaluation), _flag(rule.isEnabled), _flag(rule.batch),
                          "" if rule.severity is None else rule.severity, rule.tags, "", "",
                          json.dumps(rule.triggeringFields)])
    return Result(_text(out_csv_file))


def ImportAttributeRules(target_table, csv_file):
    """The rules of a .csv file ExportAttributeRules wrote, added to a table, each enabled or disabled as the file
    says. A rule whose name the table already has is an error (INFERRED, ADR-235 §5.8)."""
    with open(_text(csv_file), encoding="utf-8-sig", newline="") as read:
        rows = list(csv.DictReader(read))
    for row in rows:
        row = {k.strip().upper(): (v or "") for k, v in row.items() if k}
        kind = row.get("TYPE", "").upper().replace("ESRIART", "")
        events = [e for e, column in (("INSERT", "TRIGGERINSERT"), ("UPDATE", "TRIGGERUPDATE"),
                                      ("DELETE", "TRIGGERDELETE")) if row.get(column, "").upper() in _TRUE]
        batched = row.get("BATCH", "").upper() in _TRUE
        fields = row.get("TRIGGERINGFIELDS", "").strip()
        try:
            fields = json.loads(fields) if fields.startswith("[") else _list(fields)
        except ValueError:
            fields = _list(fields)
        AddAttributeRule(target_table, row.get("NAME"), kind, row.get("SCRIPTEXPRESSION"),
                         "EDITABLE" if row.get("ISEDITABLE", "True").upper() in _TRUE else "NONEDITABLE",
                         None if batched else events, row.get("ERRORNUMBER") or None, row.get("ERRORMESSAGE") or None,
                         row.get("DESCRIPTION") or None, row.get("SUBTYPE") or None, row.get("FIELD") or None,
                         "EXCLUDE" if row.get("EXCLUDECLIENTEVALUATION", "").upper() in _TRUE else "INCLUDE",
                         "BATCH" if batched else "NOT_BATCH", row.get("SEVERITY") or None, row.get("TAGS") or None,
                         fields or None)
        if row.get("ISENABLED", "True").upper() not in _TRUE:
            DisableAttributeRules(target_table, [row.get("NAME")])
    return Result(_text(target_table))


# ---- evaluating batch rules -----------------------------------------------------------------------------------------------

_ERROR_FIELDS = (("RULENAME", "String"), ("RULEID", "Integer"), ("RULETYPE", "String"),
                 ("ORIGINOBJECTCLASSNAME", "String"), ("ORIGINOBJECTID", "Integer"), ("ORIGINGLOBALID", "String"),
                 ("ERRORNUMBER", "String"), ("ERRORMESSAGE", "String"), ("SEVERITY", "Integer"),
                 ("ERRORSTATUS", "Integer"), ("ERRORPHASE", "Integer"), ("DESCRIPTION", "String"),
                 ("CREATIONDATE", "Date"))


def _error_table(connection, file, kind, srid):
    """The validation error table for a shape type, made when first needed."""
    name = _rules.ERROR_TABLES.get(kind, _rules.ERROR_TABLES[None])
    if connection.execute("select 1 from gpkg_contents where table_name = ?", (name,)).fetchone():
        return name
    columns = [_gpkg.Column(n, t) for n, t in _ERROR_FIELDS]
    shape = {"GDB_ValidationPointErrors": "Multipoint", "GDB_ValidationLineErrors": "Polyline",
             "GDB_ValidationPolygonErrors": "Polygon"}.get(name)
    connection.commit()
    if shape:
        _gpkg.create(f"{file}/{name}", shape, srid, columns)
    else:
        _gpkg.create(f"{file}/{name}", "Geometry", 0, columns)
    return name


def _extent_filter(extent):
    if extent in (None, "", "#", "MAXOF", "MINOF"):
        return None
    if hasattr(extent, "XMin"):
        return (extent.XMin, extent.YMin, extent.XMax, extent.YMax)
    parts = str(extent).split()
    if len(parts) >= 4:
        try:
            return tuple(float(p) for p in parts[:4])
        except ValueError:
            pass
    raise NotBuiltError(f"arcpy.management.EvaluateRules: the extent '{extent}' is not built in grapy; give an Extent, "
                        "four coordinates, or MAXOF.")


def EvaluateRules(in_workspace, evaluation_types, extent=None, run_async=None):
    """The workspace's batch rules evaluated over every feature, or those meeting ``extent``: CALCULATION_RULES runs the
    enabled batch calculation rules and writes what they return; VALIDATION_RULES runs the enabled validation rules.
    A feature a rule fails on — a validation rule answering false or an errorMessage, a batch calculation rule's
    errorMessage — is written to the validation error table of its shape type (GDB_ValidationPointErrors, …Line…,
    …Polygon…, or GDB_ValidationObjectErrors for a table), with its geometry.

    INFERRED (ADR-235 §5.8): the error tables' fields (RULENAME, RULEID, RULETYPE, ORIGINOBJECTCLASSNAME,
    ORIGINOBJECTID, ORIGINGLOBALID, ERRORNUMBER, ERRORMESSAGE, SEVERITY, ERRORSTATUS, ERRORPHASE, DESCRIPTION,
    CREATIONDATE), since the pages name the tables but not their fields; evaluating again replaces a rule's earlier
    errors; grapy keeps no validation status per feature, so every feature is evaluated each time; ``run_async``, a
    feature service's, changes nothing on a GeoPackage."""
    from . import _attachments
    from ._arcade._data import Datastore, Table
    from ._arcade._values import Dictionary, to_text
    kinds = {str(k).strip().upper() for k in _list(evaluation_types)}
    if not kinds or kinds - {"CALCULATION_RULES", "VALIDATION_RULES"}:
        raise ExecuteError("ERROR 000800: EvaluateRules' evaluation types are CALCULATION_RULES and VALIDATION_RULES.")
    box = _extent_filter(extent)
    file, _ = _gpkg.split(_text(in_workspace))
    connection = _gpkg.connect(file)
    try:
        tables = [r[0] for r in connection.execute("select table_name from gpkg_contents where data_type in "
                                                   "('features', 'attributes')")]
        now = datetime.datetime.now(datetime.timezone.utc).replace(tzinfo=None, microsecond=0)
        for table in tables:
            if table in _rules.ERROR_TABLES.values():
                continue
            batch = [r for r in _rules.rules(connection, table) if r.isEnabled and r.batch and
                     (("CALCULATION_RULES" in kinds and r.kind == "CALCULATION") or
                      ("VALIDATION_RULES" in kinds and r.kind == "VALIDATION"))]
            if not batch:
                continue
            engine = _rules.Engine.__new__(_rules.Engine)
            engine.connection, engine.table, engine.depth = connection, table, 0
            engine.store, engine.layout = Datastore(connection), Table(connection, table)
            engine.has_z, engine.has_m = _gpkg.dimensions(connection, table)
            from . import _subtypes
            held = _subtypes.field(connection, table)
            engine.subtype_field = held[0] if held else None
            layout = engine.layout
            global_id = _attachments.global_id_field(connection, table)
            for rule in batch:
                errors = connection.execute("select table_name from gpkg_contents where table_name like "
                                            "'GDB_Validation%Errors'").fetchall()
                for (error_table,) in errors:
                    connection.execute(f'delete from "{error_table}" where ORIGINOBJECTCLASSNAME = ? and RULENAME = ?',
                                       (table, rule.name))
                cursor = connection.execute(f'select * from "{table}"')
                names = [d[0] for d in cursor.description]
                for values in cursor.fetchall():
                    row = dict(zip(names, values))
                    shape = None
                    if layout.geometry and row.get(layout.geometry):
                        shape = _gpkg.decode(row[layout.geometry])
                    if box is not None:
                        from shapely.geometry import box as make_box
                        if shape is None or not shape.intersects(make_box(*box)):
                            continue
                    if not rule.applies_to(engine.subtype_of(row)):
                        continue
                    failure = None
                    try:
                        value = engine._evaluate(rule, "NA", dict(row), None)
                    except _rules.RuleError as error:
                        failure = str(error)
                        value = None
                    if failure is None and rule.kind == "VALIDATION":
                        if value is False:
                            failure = rule.errorMessage
                        elif isinstance(value, Dictionary) and value.has("errorMessage"):
                            failure = to_text(value.get("errorMessage"))
                        elif value is not True:
                            failure = "The validation rule did not return true, false or a dictionary with errorMessage."
                    elif failure is None:
                        if isinstance(value, Dictionary) and value.has("errorMessage"):
                            failure = to_text(value.get("errorMessage"))
                        else:
                            edits = []
                            changed = dict(row)
                            try:
                                engine._calculation(rule, "UPDATE", changed, value, edits)
                                for edit in edits:
                                    _rules.apply_edit(connection, edit, 1)
                            except _rules.RuleError as error:
                                failure = str(error)
                            else:
                                differ = [k for k in changed if k != layout.oid and changed[k] != row.get(k)]
                                if differ:
                                    connection.execute(
                                        f'update "{table}" set {", ".join(chr(34) + k + chr(34) + " = ?" for k in differ)}'
                                        f' where "{layout.oid}" = ?', [*(changed[k] for k in differ), row[layout.oid]])
                    if failure is None:
                        continue
                    target = _error_table(connection, file, layout.shape if shape is not None else None,
                                          layout.srid or 0)
                    columns = [n for n, _ in _ERROR_FIELDS]
                    data = [rule.name, rule.id, rule.type, table, row[layout.oid],
                            row.get(global_id) if global_id else None,
                            None if rule.errorNumber in ("", None) else str(rule.errorNumber), failure,
                            rule.severity, 1, 1 if rule.kind == "VALIDATION" else 2, rule.description, now]
                    error_geometry = [c for c in connection.execute("select column_name, srs_id from "
                                                                    "gpkg_geometry_columns where table_name = ?",
                                                                    (target,))]
                    if error_geometry and shape is not None and error_geometry[0][1] == (layout.srid or 0):
                        from shapely.geometry import MultiPoint
                        stored = MultiPoint([shape]) if shape.geom_type == "Point" else shape
                        columns.append(error_geometry[0][0])
                        data.append(_gpkg.encode(stored, layout.srid or 0))
                    connection.execute(f'insert into "{target}" ({", ".join(chr(34) + c + chr(34) for c in columns)}) '
                                       f'values ({", ".join("?" for _ in columns)})', data)
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_workspace))


# ---- field groups and contingent values ------------------------------------------------------------------------------


def _group_fields(tool, connection, table, fields):
    from . import _subtypes
    columns, geometry = _columns(connection, table)
    oid = _gpkg.oid_column(connection, table)
    subtype = _subtypes.field(connection, table)
    out = []
    for name in _list(fields):
        lower = str(name).lower()
        if lower in (oid.lower(), "shape", (geometry or "").lower()) or (subtype and lower == subtype[0].lower()):
            raise ExecuteError(f"ERROR 000800: {tool}: '{name}' is a system-maintained field or the subtype field, "
                               "which a field group cannot hold.")
        if lower not in columns:
            raise ExecuteError(f"ERROR 000728: {tool}: field '{name}' does not exist in '{table}'.")
        out.append(columns[lower].name)
    if not out:
        raise ExecuteError(f"ERROR 000735: {tool}: a field group holds at least one field.")
    return out


def _group(tool, connection, table, name):
    for found, restrictive, fields in _rules.groups(connection, table):
        if found.lower() == str(name).lower():
            return found, restrictive, fields
    raise ExecuteError(f"ERROR 000800: {tool}: '{table}' has no field group '{name}'.")


def CreateFieldGroup(target_table, name, fields, is_restrictive="RESTRICT"):
    """A field group: a name unique to the table, its fields (not the object id, the shape or the subtype field), and
    whether it is restrictive — RESTRICT, the default, or DO_NOT_RESTRICT."""
    tool = "CreateFieldGroup"
    restrictive = _keyword(tool, "is_restrictive", is_restrictive, ("RESTRICT", "DO_NOT_RESTRICT"), "RESTRICT")
    connection, table = _table(tool, target_table)
    try:
        held = _group_fields(tool, connection, table, fields)
        if any(g.lower() == str(name).lower() for g, _, _ in _rules.groups(connection, table)):
            raise ExecuteError(f"ERROR 000800: {tool}: '{table}' already has a field group '{name}'.")
        _rules.ensure_groups(connection)
        connection.execute(f"insert into {_rules.GROUPS} values (?, ?, ?, ?)",
                           (table, str(name), int(restrictive == "RESTRICT"), json.dumps(held)))
        connection.commit()
    finally:
        connection.close()
    return Result(_text(target_table))


def AlterFieldGroup(target_table, name, new_name=None, fields=None, is_restrictive=None):
    """A field group renamed, its fields replaced, or its restriction changed, as documented: a field removed takes
    its part of every contingent value with it; a field added joins each as ANY; nothing given keeps what is there."""
    tool = "AlterFieldGroup"
    connection, table = _table(tool, target_table)
    try:
        found, restrictive, held = _group(tool, connection, table, name)
        if new_name not in (None, "") and str(new_name).lower() != found.lower():
            if any(g.lower() == str(new_name).lower() for g, _, _ in _rules.groups(connection, table)):
                raise ExecuteError(f"ERROR 000800: {tool}: '{table}' already has a field group '{new_name}'.")
        renamed = str(new_name) if new_name not in (None, "") else found
        fields_now = _group_fields(tool, connection, table, fields) if _list(fields) else held
        if is_restrictive not in (None, ""):
            restrictive = _keyword(tool, "is_restrictive", is_restrictive, ("RESTRICT", "DO_NOT_RESTRICT"),
                                   "RESTRICT") == "RESTRICT"
        connection.execute(f"update {_rules.GROUPS} set name = ?, restrictive = ?, fields = ? where table_name = ? and "
                           "name = ?", (renamed, int(restrictive), json.dumps(fields_now), table, found))
        for cv_id, _, _, _, entries in _rules.contingent_values(connection, table, found):
            by_field = {e[0].lower(): list(e) for e in entries}
            kept = [by_field.get(f.lower(), [f, "ANY", None]) for f in fields_now]
            connection.execute(f"update {_rules.VALUES} set field_group = ?, entries = ? where table_name = ? and id = ?",
                               (renamed, json.dumps(kept), table, cv_id))
        connection.commit()
    finally:
        connection.close()
    return Result(_text(target_table))


def DeleteFieldGroup(target_table, name):
    """A field group deleted, with its contingent values, as the Alter page says deleting one does."""
    tool = "DeleteFieldGroup"
    connection, table = _table(tool, target_table)
    try:
        found, _, _ = _group(tool, connection, table, name)
        connection.execute(f"delete from {_rules.GROUPS} where table_name = ? and name = ?", (table, found))
        connection.execute(f"delete from {_rules.VALUES} where table_name = ? and field_group = ?", (table, found))
        connection.commit()
    finally:
        connection.close()
    return Result(_text(target_table))


def _domain_codes(connection, table, field, code):
    """The coded values of the domain a field takes (in a subtype), or None when it has none."""
    from . import _subtypes
    from . import _field_domains
    domain = None
    if code is not None:
        domain = _subtypes.domains(connection, table).get((field.lower(), code))
    domain = domain or _field_domains(connection, table).get(field.lower())
    if not domain:
        return None
    rows = connection.execute("select value, description from gpkg_data_column_constraints where constraint_name = ? "
                              "and constraint_type = 'enum' order by rowid", (domain,)).fetchall()
    return {str(v): d for v, d in rows} if rows else None


def _entry(tool, connection, table, field, kind, value, code, nullable):
    kind = str(kind or "").strip().upper()
    if kind not in _rules.CV_TYPES:
        raise ExecuteError(f"ERROR 000800: {tool}: '{kind}' is not a contingent value type: ANY, NULL, CODED_VALUE or "
                           "RANGE.")
    if kind == "ANY":
        return [field, "ANY", None]
    if kind == "NULL":
        if not nullable:
            raise ExecuteError(f"ERROR 000800: {tool}: '{field}' does not allow nulls, so NULL is no value for it.")
        return [field, "NULL", None]
    if kind == "RANGE":
        parts = value if isinstance(value, (list, tuple)) else str(value).split(";")
        try:
            low, high = float(parts[0]), float(parts[1])
        except (IndexError, ValueError):
            raise ExecuteError(f"ERROR 000800: {tool}: a RANGE value is 'min;max'.") from None
        if low > high:
            raise ExecuteError(f"ERROR 000800: {tool}: a RANGE value's minimum is not above its maximum.")
        return [field, "RANGE", [low, high]]
    codes = _domain_codes(connection, table, field, code)
    if codes is None:
        raise ExecuteError(f"ERROR 000800: {tool}: '{field}' has no coded value domain, so it takes ANY, NULL or "
                           "RANGE.")
    text = str(value)
    if text not in codes:
        numeric = next((c for c in codes if _same_number(c, text)), None)
        if numeric is None:
            raise ExecuteError(f"ERROR 000800: {tool}: '{value}' is not a code of the domain of '{field}'.")
        text = numeric
    stored = value
    try:
        stored = int(text) if str(int(float(text))) == text else float(text)
    except ValueError:
        stored = text
    return [field, "CODED_VALUE", stored]


def _same_number(a, b):
    try:
        return float(a) == float(b)
    except ValueError:
        return False


def _subtype_code(tool, connection, table, subtype):
    from . import _subtypes
    held = _subtypes.field(connection, table)
    if held is None:
        if subtype not in (None, ""):
            raise ExecuteError(f"ERROR 000800: {tool}: '{table}' has no subtypes.")
        return None
    if subtype in (None, ""):
        raise ExecuteError(f"ERROR 000735: {tool}: '{table}' has subtypes; name the one the value is for.")
    return _subtype_list(tool, connection, table, [subtype])[0]


def _add_value(tool, connection, table, group, fields, entries, code, retired, wanted_id=None):
    _rules.ensure_groups(connection)
    for held_id, _, held_code, _, held_entries in _rules.contingent_values(connection, table, group):
        if held_code == code and [list(e) for e in held_entries] == [list(e) for e in entries]:
            raise ExecuteError(f"ERROR 000800: {tool}: field group '{group}' already holds this contingent value "
                               f"(id {held_id}).")
    if wanted_id is None:
        wanted_id = connection.execute(f"select coalesce(max(id), 0) + 1 from {_rules.VALUES} where table_name = ?",
                                       (table,)).fetchone()[0]
    connection.execute(f"insert into {_rules.VALUES} values (?, ?, ?, ?, ?, ?)",
                       (table, int(wanted_id), group, code, int(bool(retired)), json.dumps(entries)))
    return wanted_id


def AddContingentValue(target_table, field_group_name, values, subtype=None, retire_value="DO_NOT_RETIRE"):
    """A contingent value added to a field group: ``values`` is a value table of [field, type, value] — CODED_VALUE
    with a code of the field's domain, RANGE as 'min;max', ANY, or NULL on a field that allows nulls. A table with
    subtypes takes one subtype per value, as documented; RETIRE marks it retired.

    INFERRED (ADR-235 §5.8): a field of the group the value table leaves out is ANY; a value the group already holds is
    an error; a CODED_VALUE needs the field's coded value domain (in the subtype); a new value's id is one more
    than the highest the table holds."""
    tool = "AddContingentValue"
    retired = _keyword(tool, "retire_value", retire_value, ("RETIRE", "DO_NOT_RETIRE"), "DO_NOT_RETIRE") == "RETIRE"
    connection, table = _table(tool, target_table)
    try:
        group, _, fields = _group(tool, connection, table, field_group_name)
        code = _subtype_code(tool, connection, table, subtype)
        columns, _ = _columns(connection, table)
        given = {}
        rows = values if isinstance(values, (list, tuple)) else [v.split(maxsplit=2) for v in _list(values)]
        for row in rows:
            row = list(row) if isinstance(row, (list, tuple)) else str(row).split(maxsplit=2)
            if len(row) < 2:
                raise ExecuteError(f"ERROR 000800: {tool}: each value is [field, type, value].")
            field = next((f for f in fields if f.lower() == str(row[0]).lower()), None)
            if field is None:
                raise ExecuteError(f"ERROR 000800: {tool}: '{row[0]}' is not a field of the group '{group}'.")
            given[field] = _entry(tool, connection, table, field, row[1], row[2] if len(row) > 2 else None, code,
                                  columns[field.lower()].nullable)
        entries = [given.get(f, [f, "ANY", None]) for f in fields]
        _add_value(tool, connection, table, group, fields, entries, code, retired)
        connection.commit()
    finally:
        connection.close()
    return Result(_text(target_table))


def RemoveContingentValue(target_table, id):
    """The contingent value with that id removed."""
    tool = "RemoveContingentValue"
    connection, table = _table(tool, target_table)
    try:
        if not any(v[0] == int(id) for v in _rules.contingent_values(connection, table)):
            raise ExecuteError(f"ERROR 000800: {tool}: '{table}' has no contingent value with id {id}.")
        connection.execute(f"delete from {_rules.VALUES} where table_name = ? and id = ?", (table, int(id)))
        connection.commit()
    finally:
        connection.close()
    return Result(_text(target_table))


def _write_groups(path, groups):
    width = max([len(f) for _, _, f in groups] or [1])
    with open(_text(path), "w", encoding="utf-8", newline="") as written:
        out = csv.writer(written)
        out.writerow(["NAME", "IS_RESTRICTIVE", *[f"FIELD{i}" for i in range(1, width + 1)]])
        for name, restrictive, fields in groups:
            out.writerow([name, "TRUE" if restrictive else "FALSE", *fields, *[""] * (width - len(fields))])


def _cv_text(entry):
    kind, value = entry[1], entry[2]
    if kind in ("ANY", "NULL"):
        return ""
    if kind == "RANGE":
        return f"{_number(value[0])};{_number(value[1])}"
    return _number(value) if isinstance(value, (int, float)) else str(value)


def _number(value):
    return str(int(value)) if float(value) == int(float(value)) else repr(float(value))


def _write_values(path, rows, groups, names):
    width = max([len(f) for _, _, f in groups] or [1])
    with open(_text(path), "w", encoding="utf-8", newline="") as written:
        out = csv.writer(written)
        header = ["CAV_ID", "IS_RETIRED", "FIELD_GROUP", "SUBTYPE", "SUBTYPE_NAME"]
        for i in range(1, width + 1):
            header += [f"CV_TYPE{i}", f"CV_VALUE{i}", f"DESCRIPTION{i}"]
        out.writerow(header)
        for cv_id, group, code, retired, entries, descriptions in rows:
            line = [cv_id, "True" if retired else "False", group, "" if code is None else code,
                    "" if code is None else names.get(code, "")]
            for entry, description in zip(entries, descriptions):
                line += [_rules.CV_TYPES[entry[1]], _cv_text(entry), description or ""]
            line += [""] * (3 * (width - len(entries)))
            out.writerow(line)


def _descriptions(connection, table, entries, code):
    out = []
    for entry in entries:
        if entry[1] != "CODED_VALUE":
            out.append("")
            continue
        codes = _domain_codes(connection, table, entry[0], code) or {}
        out.append(next((d for c, d in codes.items() if _same_number(c, str(entry[2])) or c == str(entry[2])), ""))
    return out


def ExportContingentValues(target_table, field_groups_file, contingent_values_file):
    """A table's field groups and contingent values written to the two .csv files the page describes. INFERRED
    (ADR-235 §5.8): a RANGE value is written 'min;max'; IS_RESTRICTIVE as TRUE or FALSE, IS_RETIRED as True or False,
    as the page's examples show them."""
    from . import _subtypes
    connection, table = _table("ExportContingentValues", target_table)
    try:
        groups = _rules.groups(connection, table)
        names = _subtypes.codes(connection, table)
        rows = [(i, g, c, r, e, _descriptions(connection, table, e, c))
                for i, g, c, r, e in _rules.contingent_values(connection, table)]
    finally:
        connection.close()
    _write_groups(field_groups_file, groups)
    _write_values(contingent_values_file, rows, groups, names)
    return Result(_text(target_table))


def ImportContingentValues(target_table, field_group_file, contingent_value_file, import_type="UNION"):
    """Field groups and contingent values read from the two .csv files ExportContingentValues writes: UNION (the
    default) adds them to what the table holds, leaving out duplicates; REPLACE replaces everything. As documented, a
    group or value whose field or value the table does not have is not imported."""
    tool = "ImportContingentValues"
    kind = _keyword(tool, "import_type", import_type, ("UNION", "REPLACE"), "UNION")
    with open(_text(field_group_file), encoding="utf-8-sig", newline="") as read:
        group_rows = [{k.strip().upper(): (v or "").strip() for k, v in r.items() if k} for r in csv.DictReader(read)]
    with open(_text(contingent_value_file), encoding="utf-8-sig", newline="") as read:
        value_rows = [{k.strip().upper(): (v or "").strip() for k, v in r.items() if k} for r in csv.DictReader(read)]
    connection, table = _table(tool, target_table)
    try:
        _rules.ensure_groups(connection)
        columns, _ = _columns(connection, table)
        if kind == "REPLACE":
            connection.execute(f"delete from {_rules.GROUPS} where table_name = ?", (table,))
            connection.execute(f"delete from {_rules.VALUES} where table_name = ?", (table,))
        imported = {}
        for row in group_rows:
            fields = [row[k] for k in sorted((k for k in row if k.startswith("FIELD") and k[5:].isdigit()),
                                             key=lambda k: int(k[5:])) if row[k]]
            if not row.get("NAME") or any(f.lower() not in columns for f in fields):
                continue
            fields = [columns[f.lower()].name for f in fields]
            held = next((g for g in _rules.groups(connection, table) if g[0].lower() == row["NAME"].lower()), None)
            if held is None:
                connection.execute(f"insert into {_rules.GROUPS} values (?, ?, ?, ?)",
                                   (table, row["NAME"], int(row.get("IS_RESTRICTIVE", "TRUE").upper() in _TRUE),
                                    json.dumps(fields)))
                held = (row["NAME"], True, fields)
            imported[row["NAME"].lower()] = held
        reverse = {v: k for k, v in _rules.CV_TYPES.items()}
        for row in value_rows:
            group = imported.get(row.get("FIELD_GROUP", "").lower())
            if group is None:
                continue
            code = int(float(row["SUBTYPE"])) if row.get("SUBTYPE") else None
            entries = []
            try:
                for i, field in enumerate(group[2], start=1):
                    cv_kind = reverse.get(int(float(row.get(f"CV_TYPE{i}") or 1)), "ANY")
                    entries.append(_entry(tool, connection, table, field, cv_kind, row.get(f"CV_VALUE{i}"), code,
                                          columns[field.lower()].nullable))
            except (ExecuteError, ValueError):
                continue
            try:
                _add_value(tool, connection, table, group[0], group[2], entries, code,
                           row.get("IS_RETIRED", "").upper() in _TRUE)
            except ExecuteError:
                continue  # a duplicate, left out under UNION
        connection.commit()
    finally:
        connection.close()
    return Result(_text(target_table))


def GenerateContingentValues(in_table, field_groups_file, contingent_values_file, mode="DATA", field_groups=None):
    """The contingent values a table's field groups could hold, written to the two .csv files (not applied — import
    them with ImportContingentValues): DATA, the default, from the combinations the rows hold, per subtype; SCHEMA, every
    combination of the coded values of the fields' domains. As documented, a field with no domain is ANY, and also
    NULL when it allows nulls.

    INFERRED (ADR-235 §5.8): in DATA mode a value of a field with a coded domain is a CODED_VALUE, a null is NULL; a
    field with a range domain counts as having no coded domain; ids number from 1 in the order written."""
    import itertools
    from . import _subtypes
    tool = "GenerateContingentValues"
    mode = _keyword(tool, "mode", mode, ("DATA", "SCHEMA"), "DATA")
    connection, table = _table(tool, in_table)
    try:
        groups = _rules.groups(connection, table)
        if not groups:
            raise ExecuteError(f"ERROR 000800: {tool}: '{table}' has no field groups.")
        wanted = [str(g).lower() for g in _list(field_groups)]
        if wanted:
            groups = [g for g in groups if g[0].lower() in wanted]
        held = _subtypes.field(connection, table)
        names = _subtypes.codes(connection, table)
        codes = list(names) if held else [None]
        columns, _ = _columns(connection, table)
        rows, next_id = [], 1
        for group, restrictive, fields in groups:
            for code in codes:
                options = []
                for field in fields:
                    domain = _domain_codes(connection, table, field, code)
                    nullable = columns[field.lower()].nullable
                    if domain is None:
                        options.append([[field, "ANY", None]] + ([[field, "NULL", None]] if nullable else []))
                    elif mode == "SCHEMA":
                        options.append([_entry(tool, connection, table, field, "CODED_VALUE", c, code, nullable)
                                        for c in domain] + ([[field, "NULL", None]] if nullable else []))
                    else:
                        options.append(None)
                if mode == "DATA":
                    where = f' where "{held[0]}" = {int(code)}' if held else ""
                    seen = []
                    quoted = ", ".join(f'"{f}"' for f in fields)
                    for found in connection.execute(f'select distinct {quoted} from "{table}"{where}'):
                        combination = []
                        for field, option, value in zip(fields, options, found):
                            if option is not None:
                                combination.append(option[0] if len(option) == 1 or value is not None else option[1])
                            elif value is None:
                                combination.append([field, "NULL", None])
                            else:
                                combination.append(_entry(tool, connection, table, field, "CODED_VALUE", value, code,
                                                          True))
                        if combination not in seen:
                            seen.append(combination)
                    combinations = seen
                else:
                    combinations = [list(c) for c in itertools.product(*options)]
                for combination in combinations:
                    rows.append((next_id, group, code, False, combination,
                                 _descriptions(connection, table, combination, code)))
                    next_id += 1
    finally:
        connection.close()
    _write_groups(field_groups_file, groups)
    _write_values(contingent_values_file, rows, groups, names)
    return Result(_text(contingent_values_file))


class ContingentFieldValue:
    """One field's part of a contingent value, as ListContingentValues answers it: ``name``, ``type`` (ANY, NULL,
    CODED_VALUE or RANGE), and ``code`` for a coded value or ``range`` ([min, max]) for a range."""

    def __init__(self, name, type, code=None, range=None):
        self.name, self.type, self.code, self.range = name, type, code, range

    def __repr__(self):
        return f"<ContingentFieldValue {self.name} {self.type}>"


class ContingentValue:
    """A contingent value, as ListContingentValues answers it: ``id``, ``fieldGroupName``, ``subtype`` (its code, or
    None), ``isRetired`` and ``values`` (ContingentFieldValue objects, in the group's field order)."""

    def __init__(self, id, fieldGroupName, subtype, isRetired, values):
        self.id, self.fieldGroupName, self.subtype, self.isRetired = id, fieldGroupName, subtype, isRetired
        self.values = list(values)

    def __repr__(self):
        return f"<ContingentValue {self.id}>"


def ListContingentValues(table, field_group_name=None, subtype_code=None):
    """A table's contingent values, all, or those of one field group or one subtype."""
    connection, name = _gpkg.table_of(_text(table))
    try:
        found = _rules.contingent_values(connection, name)
    finally:
        connection.close()
    out = []
    for cv_id, group, code, retired, entries in found:
        if field_group_name not in (None, "", "All") and group.lower() != str(field_group_name).lower():
            continue
        if subtype_code not in (None, "", "All") and code != int(subtype_code):
            continue
        out.append(ContingentValue(cv_id, group, code, retired, [
            ContingentFieldValue(e[0], e[1], e[2] if e[1] == "CODED_VALUE" else None,
                                 list(e[2]) if e[1] == "RANGE" else None) for e in entries]))
    return out


# ---- database sequences ---------------------------------------------------------------------------------------------------


def CreateDatabaseSequence(in_workspace, seq_name, seq_start_id=1, seq_inc_value=1):
    """A database sequence in a GeoPackage: its name, unique there, its start (greater than 0, 1 by default) and its
    increment (1 by default), as documented. NextSequenceValue draws from it."""
    from . import _sequences
    tool = "CreateDatabaseSequence"
    name = str(seq_name or "").strip()
    if not name:
        raise ExecuteError(f"ERROR 000735: {tool}: the sequence needs a name.")
    start = 1 if seq_start_id in (None, "") else int(seq_start_id)
    step = 1 if seq_inc_value in (None, "") else int(seq_inc_value)
    if start <= 0:
        raise ExecuteError(f"ERROR 000800: {tool}: the start value is greater than 0.")
    if step == 0:
        raise ExecuteError(f"ERROR 000800: {tool}: the increment is not 0.")
    file, _ = _gpkg.split(_text(in_workspace))
    connection = _gpkg.connect(file)
    try:
        if _sequences.exists(connection, name):
            raise ExecuteError(f"ERROR 000800: {tool}: the sequence '{name}' already exists.")
        _sequences.create(connection, name, start, step)
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_workspace))


def DeleteDatabaseSequence(in_workspace, seq_name):
    """A database sequence removed; one that does not exist is an error (INFERRED, ADR-235 §5.8)."""
    from . import _sequences
    file, _ = _gpkg.split(_text(in_workspace))
    connection = _gpkg.connect(file)
    try:
        if not _sequences.exists(connection, str(seq_name)):
            raise ExecuteError(f"ERROR 000800: DeleteDatabaseSequence: the sequence '{seq_name}' does not exist.")
        _sequences.delete(connection, str(seq_name))
        connection.commit()
    finally:
        connection.close()
    return Result(_text(in_workspace))


class DatabaseSequence:
    """A database sequence, as ListDatabaseSequences answers it: ``name``, ``startValue``, ``incrementValue`` and
    ``currentValue`` — the last value drawn, or the start before any (INFERRED, ADR-235 §5.8)."""

    def __init__(self, name, startValue, incrementValue, currentValue):
        self.name, self.startValue, self.incrementValue, self.currentValue = name, startValue, incrementValue, currentValue

    def __repr__(self):
        return f"<DatabaseSequence {self.name}>"


def ListDatabaseSequences(workspace):
    """The database sequences of a GeoPackage."""
    from . import _sequences
    file, _ = _gpkg.split(_text(workspace))
    if not os.path.exists(file):
        raise FileNotFoundError(f"'{file}' does not exist.")
    connection = _gpkg.connect(file)
    try:
        return [DatabaseSequence(*row) for row in _sequences.listed(connection)]
    finally:
        connection.close()
