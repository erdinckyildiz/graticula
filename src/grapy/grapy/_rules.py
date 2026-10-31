"""Attribute rules, field groups and contingent values held in the GeoPackage a tool works on, and the engine that
runs them on an edit — ADR-235.

Storage (INFERRED, ADR-235 §5.8: no GeoPackage or GDAL convention holds either) is grapy's own tables, registered in
``gpkg_extensions`` as the ``grapy_attribute_rules`` and ``grapy_contingent_values`` extensions:

- ``grapy_attribute_rule``: one row per rule — its table, name, id, type, Arcade script, target field, triggers,
  subtypes, error number and message, flags, severity, tags, triggering fields, whether it is enabled, and its
  evaluation order.
- ``grapy_field_group``: a table's field groups — name, whether restrictive, and their fields.
- ``grapy_contingent_value``: a field group's contingent values — id, subtype, whether retired, and one entry per
  field of the group: its type (ANY, NULL, CODED_VALUE, RANGE) and value.

The engine runs a table's **immediate** rules on every insert, update and delete made through grapy's cursors
(``arcpy.da.InsertCursor``, ``UpdateCursor``, and the tools that write through them): its calculation rules in
evaluation order, each seeing what the ones before it set, then its constraint rules, then the restrictive field
groups' contingent values. A failing constraint, an ``errorMessage`` or a combination no contingent value allows
raises RuntimeError, as arcpy's cursors do, and the edit is not made. Batch calculation rules and validation rules are
run by EvaluateRules (``evaluate``), which writes what fails to the four validation error tables.
"""
from __future__ import annotations

import datetime
import json

from . import _gpkg
from ._topology import register

RULES = "grapy_attribute_rule"
GROUPS = "grapy_field_group"
VALUES = "grapy_contingent_value"
TYPES = {"CALCULATION": "esriARTCalculation", "CONSTRAINT": "esriARTConstraint", "VALIDATION": "esriARTValidation"}
CV_TYPES = {"ANY": 1, "NULL": 2, "CODED_VALUE": 3, "RANGE": 4}
MAX_DEPTH = 8
ERROR_TABLES = {"Point": "GDB_ValidationPointErrors", "Multipoint": "GDB_ValidationPointErrors",
                "Polyline": "GDB_ValidationLineErrors", "Polygon": "GDB_ValidationPolygonErrors",
                None: "GDB_ValidationObjectErrors"}


def ensure(connection):
    connection.execute(f"""create table if not exists {RULES} (table_name text not null, name text not null collate nocase,
        id integer not null, type text not null, script text not null, field text, user_editable integer not null,
        on_insert integer not null, on_update integer not null, on_delete integer not null, error_number text,
        error_message text, description text, subtypes text not null, exclude_client integer not null,
        batch integer not null, severity integer, tags text, triggering_fields text not null,
        enabled integer not null, evaluation_order integer not null, created text not null,
        primary key (table_name, name))""")
    register(connection, (RULES,), "grapy_attribute_rules", "Graticula ADR-235: grapy's attribute rules")


def ensure_groups(connection):
    connection.execute(f"""create table if not exists {GROUPS} (table_name text not null, name text not null collate nocase,
        restrictive integer not null, fields text not null, primary key (table_name, name))""")
    connection.execute(f"""create table if not exists {VALUES} (table_name text not null, id integer not null,
        field_group text not null collate nocase, subtype_code integer, retired integer not null, entries text not null,
        primary key (table_name, id))""")
    register(connection, (GROUPS, VALUES), "grapy_contingent_values",
             "Graticula ADR-235: grapy's field groups and contingent values")


def _held(connection, name):
    return connection.execute("select 1 from sqlite_master where type = 'table' and name = ?", (name,)).fetchone() is not None


# ---- rules -----------------------------------------------------------------------------------------------------------


class Rule:
    """A stored rule, with the properties arcpy's Describe answers for one (``attributeRules``)."""

    _COLUMNS = ("table_name", "name", "id", "type", "script", "field", "user_editable", "on_insert", "on_update",
                "on_delete", "error_number", "error_message", "description", "subtypes", "exclude_client", "batch",
                "severity", "tags", "triggering_fields", "enabled", "evaluation_order", "created")

    def __init__(self, row):
        for name, value in zip(self._COLUMNS, row):
            setattr(self, "_" + name, value)
        self.table = self._table_name
        self.name = self._name
        self.id = int(self._id)
        self.kind = self._type
        self.type = TYPES[self._type]
        self.scriptExpression = self._script
        self.fieldName = self._field or ""
        self.userEditable = bool(self._user_editable)
        self.events = [e for e, on in (("INSERT", self._on_insert), ("UPDATE", self._on_update),
                                       ("DELETE", self._on_delete)) if on]
        self.triggeringEvents = ["esriARTE" + e.title() for e in self.events]
        self.errorNumber = int(self._error_number) if str(self._error_number or "").lstrip("-").isdigit() else \
            (self._error_number or "")
        self.errorMessage = self._error_message or ""
        self.description = self._description or ""
        self.subtypeCodes = json.loads(self._subtypes)
        self.subtypeCode = self.subtypeCodes[0] if len(self.subtypeCodes) == 1 else -1
        self.excludeFromClientEvaluation = bool(self._exclude_client)
        self.batch = bool(self._batch)
        self.severity = self._severity
        self.tags = self._tags or ""
        self.triggeringFields = json.loads(self._triggering_fields)
        self.isEnabled = bool(self._enabled)
        self.evaluationOrder = int(self._evaluation_order)
        self.creationTime = datetime.datetime.fromisoformat(self._created)
        self.checkParameters = None
        self.referencesExternalService = False
        self.requiredGeodatabaseClientVersion = ""

    def __repr__(self):
        return f"<AttributeRule {self.name}>"

    def applies_to(self, code):
        return not self.subtypeCodes or code in self.subtypeCodes


def rules(connection, table):
    """The table's rules in evaluation order."""
    if not _held(connection, RULES):
        return []
    rows = connection.execute(f"select {', '.join(Rule._COLUMNS)} from {RULES} where table_name = ? "
                              "order by evaluation_order, id", (table,)).fetchall()
    return [Rule(r) for r in rows]


def rule(connection, table, name):
    for found in rules(connection, table):
        if found.name.lower() == str(name).lower():
            return found
    return None


def add(connection, table, values):
    ensure(connection)
    row = connection.execute(f"select coalesce(max(id), 0) + 1, coalesce(max(evaluation_order), 0) + 1 from {RULES} "
                             "where table_name = ?", (table,)).fetchone()
    values = dict(values, table_name=table, id=row[0], evaluation_order=row[1],
                  created=datetime.datetime.now(datetime.timezone.utc).replace(tzinfo=None).isoformat())
    connection.execute(f"insert into {RULES} ({', '.join(Rule._COLUMNS)}) values ({', '.join('?' for _ in Rule._COLUMNS)})",
                       [values[c] for c in Rule._COLUMNS])


def update(connection, table, name, **changes):
    assignments = ", ".join(f"{k} = ?" for k in changes)
    connection.execute(f"update {RULES} set {assignments} where table_name = ? and name = ?",
                       [*changes.values(), table, name])


def renumber(connection, table):
    """Evaluation orders 1, 2, 3 … in their present order."""
    for order, found in enumerate(rules(connection, table), start=1):
        update(connection, table, found.name, evaluation_order=order)


def drop(connection, table):
    """Everything kept for a table, gone with it (Delete)."""
    for name in (RULES, GROUPS, VALUES):
        if _held(connection, name):
            connection.execute(f"delete from {name} where table_name = ?", (table,))


def rename(connection, old, new):
    for name in (RULES, GROUPS, VALUES):
        if _held(connection, name):
            connection.execute(f"update {name} set table_name = ? where table_name = ?", (new, old))


# ---- field groups and contingent values --------------------------------------------------------------------------------


def groups(connection, table):
    """[(name, restrictive, [fields])] of a table."""
    if not _held(connection, GROUPS):
        return []
    return [(n, bool(r), json.loads(f)) for n, r, f in connection.execute(
        f"select name, restrictive, fields from {GROUPS} where table_name = ? order by rowid", (table,))]


def contingent_values(connection, table, group=None, subtype=None):
    """[(id, group, subtype code or None, retired, [(field, type, value)])]."""
    if not _held(connection, VALUES):
        return []
    sql = f"select id, field_group, subtype_code, retired, entries from {VALUES} where table_name = ?"
    parameters = [table]
    if group is not None:
        sql += " and field_group = ?"
        parameters.append(group)
    if subtype is not None:
        sql += " and subtype_code is ?"
        parameters.append(subtype)
    return [(int(i), g, s, bool(r), [tuple(e) for e in json.loads(e)]) for i, g, s, r, e in
            connection.execute(sql + " order by id", parameters)]


def _matches(entry, value):
    kind, expected = entry[1], entry[2]
    if kind == "ANY":
        return True
    if kind == "NULL":
        return value is None
    if value is None:
        return False
    if kind == "RANGE":
        low, high = expected
        try:
            return float(low) <= float(value) <= float(high)
        except (TypeError, ValueError):
            return False
    if isinstance(value, (int, float)) and not isinstance(value, bool):
        try:
            return float(expected) == float(value)
        except (TypeError, ValueError):
            return False
    return str(expected) == str(value)


# ---- the engine -------------------------------------------------------------------------------------------------------


class RuleError(RuntimeError):
    """An edit a rule or a contingent value refused."""


class Engine:
    """A table's immediate rules and restrictive field groups, ready to run on its edits. ``active`` is False when
    there is nothing to run, so a cursor on a table without either costs one query."""

    def __init__(self, connection, table, depth=0, version=None):
        self.connection, self.table, self.depth = connection, table, depth
        found = [r for r in rules(connection, table) if r.isEnabled and not r.batch and r.kind != "VALIDATION"]
        # Calculation rules first, in evaluation order, then constraint rules (INFERRED, ADR-235 §5.8).
        self.rules = [r for r in found if r.kind == "CALCULATION"] + [r for r in found if r.kind == "CONSTRAINT"]
        self.groups = [(n, f) for n, restrictive, f in groups(connection, table) if restrictive]
        self.values = contingent_values(connection, table) if self.groups else []
        self.active = bool(self.rules or self.groups)
        if self.active:
            from ._arcade._data import Datastore, Table
            self.store = Datastore(connection, version)
            self.layout = Table(connection, table)
            self.has_z, self.has_m = _gpkg.dimensions(connection, table)
            from . import _subtypes
            held = _subtypes.field(connection, table)
            self.subtype_field = held[0] if held else None

    # -- rows and features
    def feature(self, row):
        from ._arcade._data import value_of
        from ._arcade._values import Dictionary, Feature, Geometry
        attributes = Dictionary()
        geometry = None
        # Every field is present: a new row's object id, and a field it does not name, read null (INFERRED, §5.8).
        attributes.set(self.layout.oid, None)
        for column in self.layout.columns:
            attributes.set(column, None)
        for column, value in row.items():
            if self.layout.geometry and column.lower() == self.layout.geometry.lower():
                decoded = _gpkg.decode(value) if isinstance(value, (bytes, bytearray, memoryview)) and value else None
                geometry = Geometry(decoded, self.layout.srid) if decoded is not None and not decoded.is_empty else None
                continue
            attributes.set(column, value_of(self.layout.types.get(column), value))
        return Feature(attributes, geometry, immutable=True, source=(self.store, self.layout))

    def stored(self, column, value):
        """An Arcade value as the column stores it."""
        from ._arcade._values import Date, DateOnly, Dictionary, Feature, Geometry, Time, is_number
        kind = self.layout.types.get(column)
        if value is None:
            return None
        if isinstance(value, bool):
            return int(value)
        if isinstance(value, Date):
            return value.value.astimezone(datetime.timezone.utc).replace(tzinfo=None)
        if isinstance(value, DateOnly):
            return datetime.datetime.combine(value.value, datetime.time())
        if isinstance(value, Time):
            return value.iso()
        if isinstance(value, (list, Dictionary, Feature, Geometry)):
            raise RuleError(f"An attribute rule set '{column}' to a {type(value).__name__}, which a field cannot hold.")
        if is_number(value):
            if kind in ("Integer", "SmallInteger", "BigInteger"):
                return int(round(float(value)))
            if kind == "String":
                from ._arcade._values import number_text
                return number_text(value)
            return float(value) if not isinstance(value, int) else value
        if kind in ("Integer", "SmallInteger", "BigInteger", "Double", "Single"):
            try:
                return float(value) if kind in ("Double", "Single") else int(float(value))
            except (TypeError, ValueError):
                raise RuleError(f"An attribute rule set the numeric field '{column}' to '{value}'.") from None
        return value

    def column(self, name):
        if self.layout.geometry and name.lower() in (self.layout.geometry.lower(), "shape"):
            return self.layout.geometry
        return self.layout.column(name)

    def subtype_of(self, row):
        if not self.subtype_field:
            return None
        for column, value in row.items():
            if column.lower() == self.subtype_field.lower():
                try:
                    return int(value) if value is not None else None
                except (TypeError, ValueError):
                    return None
        return None

    # -- the three events
    def insert(self, row):
        return self._run("INSERT", None, dict(row))

    def update(self, old, new):
        return self._run("UPDATE", old, dict(new))

    def delete(self, old):
        self._run("DELETE", old, dict(old))

    def _profile(self, event, current, original):
        from ._arcade._data import FeatureSetCollection
        from ._arcade._values import Dictionary
        return {"$feature": self.feature(current),
                "$originalFeature": self.feature(original) if original is not None else None,
                "$editcontext": Dictionary({"editType": event}, immutable=True),
                "$datastore": FeatureSetCollection(self.store)}

    def _changed(self, old, new):
        lowered = {k.lower(): v for k, v in old.items()}
        return {k.lower() for k, v in new.items() if lowered.get(k.lower()) != v}

    def _run(self, event, original, row):
        changed = self._changed(original, row) if event == "UPDATE" else set()
        code = self.subtype_of(row)
        edits = []
        for rule in self.rules:
            if (event == "UPDATE" and rule.kind == "CALCULATION" and not rule.userEditable and rule.fieldName
                    and rule.fieldName.lower() in changed and rule.applies_to(code)):
                raise RuleError(f"'{rule.fieldName}' of '{self.table}' is calculated by the attribute rule "
                                f"'{rule.name}' and cannot be edited.")
            if event not in rule.events or not rule.applies_to(code):
                continue
            if event == "UPDATE" and rule.triggeringFields:
                wanted = {f.lower() for f in rule.triggeringFields}
                if self.layout.geometry and ("shape" in wanted or self.layout.geometry.lower() in wanted):
                    wanted.add(self.layout.geometry.lower())
                if not wanted & changed:
                    continue
            value = self._evaluate(rule, event, row, original)
            if rule.kind == "CONSTRAINT":
                self._constraint(rule, event, value)
            else:
                self._calculation(rule, event, row, value, edits)
        if event != "DELETE":
            self._contingent(row, original)
        for edit in edits:
            apply_edit(self.connection, edit, self.depth + 1)
        return row

    def _evaluate(self, rule, event, row, original):
        from ._arcade import ArcadeError, run
        try:
            value, console = run(rule.scriptExpression, self._profile(event, row, original))
        except ArcadeError as error:
            raise RuleError(f"The attribute rule '{rule.name}' of '{self.table}' failed on {event.lower()}: "
                            f"{error}") from None
        if console:
            from ._runtime import AddMessage
            for line in console:
                AddMessage(line)
        return value

    def _refuse(self, rule, event, message=None):
        number = f"{rule.errorNumber}: " if rule.errorNumber not in ("", None) else ""
        raise RuleError(f"The attribute rule '{rule.name}' of '{self.table}' refused the {event.lower()}: "
                        f"{number}{message if message is not None else rule.errorMessage}")

    def _constraint(self, rule, event, value):
        from ._arcade._values import Dictionary, to_text
        if value is True:
            return
        if value is False:
            self._refuse(rule, event)
        if isinstance(value, Dictionary) and value.has("errorMessage"):
            self._refuse(rule, event, to_text(value.get("errorMessage")))
        raise RuleError(f"The constraint rule '{rule.name}' of '{self.table}' must return true, false or a dictionary "
                        "with errorMessage.")

    def _calculation(self, rule, event, row, value, edits):
        from ._arcade._values import Dictionary, Feature, Geometry, to_text
        if isinstance(value, Dictionary):
            if value.has("errorMessage"):
                self._refuse(rule, event, to_text(value.get("errorMessage")))
            if value.has("edit"):
                found = value.get("edit")
                edits.extend(found if isinstance(found, list) else [found])
            if not value.has("result"):
                return  # calculationRequired and validationRequired mark features grapy re-evaluates whole (§5.8)
            value = value.get("result")
            if isinstance(value, Dictionary) and (value.has("attributes") or value.has("geometry")):
                if event == "DELETE":
                    return
                attributes = value.get("attributes")
                if isinstance(attributes, Dictionary):
                    for name, given in attributes.items():
                        column = self.column(name)
                        if column is None:
                            raise RuleError(f"The attribute rule '{rule.name}' returned '{name}', which "
                                            f"'{self.table}' has no field named.")
                        self._set(row, column, given)
                if value.has("geometry"):
                    self._set(row, self.layout.geometry, value.get("geometry"))
                return
        if event == "DELETE":
            return
        if not rule.fieldName:
            raise RuleError(f"The calculation rule '{rule.name}' of '{self.table}' has no field, so it returns a "
                            "dictionary with result attributes.")
        self._set(row, self.column(rule.fieldName) or rule.fieldName, value)
        _ = (Feature, Geometry)

    def _set(self, row, column, value):
        from ._arcade._values import Feature, Geometry
        for key in list(row):
            if key.lower() == column.lower():
                del row[key]
        if self.layout.geometry and column.lower() == self.layout.geometry.lower():
            if isinstance(value, Feature):
                value = value.geometry
            if value is not None and not isinstance(value, Geometry):
                raise RuleError(f"An attribute rule set the geometry of '{self.table}' to something not a geometry.")
            row[column] = _gpkg.encode(value.shape if value is not None else None, self.layout.srid or 0,
                                       self.has_z, self.has_m)
        else:
            row[column] = self.stored(column, value)

    def _contingent(self, row, original):
        """Each restrictive field group's fields hold a combination one of its contingent values allows, for the row's
        subtype: a retired value only on a row that already held it. INFERRED (ADR-235 §5.8): a group with no
        contingent values for the subtype, and a row whose group fields are all null, are not restricted."""
        values = {k.lower(): v for k, v in row.items()}
        before = {k.lower(): v for k, v in (original or {}).items()}
        code = self.subtype_of(row)
        for name, fields in self.groups:
            entries = [v for v in self.values if v[1].lower() == name.lower() and v[2] == (code if self.subtype_field
                                                                                            else None)]
            if not entries:
                continue
            current = [values.get(f.lower()) for f in fields]
            if all(v is None for v in current):
                continue
            unchanged = original is not None and all(before.get(f.lower()) == values.get(f.lower()) for f in fields)
            for _, _, _, retired, parts in entries:
                if retired and not unchanged:
                    continue
                by_field = {p[0].lower(): p for p in parts}
                if all(_matches(by_field.get(f.lower(), (f, "ANY", None)), values.get(f.lower())) for f in fields):
                    break
            else:
                shown = ", ".join(f"{f} = {values.get(f.lower())!r}" for f in fields)
                raise RuleError(f"The field group '{name}' of '{self.table}' is restrictive, and no contingent value "
                                f"allows {shown}.")


def engine(connection, table, depth=0, version=None):
    return Engine(connection, table, depth, version)


# ---- edits a calculation rule returns ------------------------------------------------------------------------------------


def _row(connection, table, where, parameters):
    cursor = connection.execute(f'select * from "{table}" where {where}', parameters)
    found = cursor.fetchone()
    if found is None:
        return None
    return dict(zip([d[0] for d in cursor.description], found))


def apply_edit(connection, edit, depth):
    """One ``edit`` entry of a calculation rule's dictionary: adds, updates and deletes on ``className``, each run
    through that class's own rules."""
    from . import _attachments, _behaviours, _relationships, _subtypes
    from ._arcade._values import Dictionary, to_text
    if depth > MAX_DEPTH:
        raise RuleError("Attribute rules edited one another more than eight levels deep; a rule's edit that triggers "
                        "itself never ends.")
    if not isinstance(edit, Dictionary) or not edit.has("className"):
        raise RuleError("An attribute rule's edit names its className.")
    from ._arcade._data import Datastore
    store = Datastore(connection)
    from ._arcade import ArcadeError
    try:
        layout = store.table(to_text(edit.get("className")))
    except ArcadeError as error:
        raise RuleError(str(error)) from None
    table = layout.name
    target = Engine(connection, table, depth)
    has_z, has_m = _gpkg.dimensions(connection, table)
    global_id = _attachments.global_id_field(connection, table)

    def values(entry):
        out = {}
        attributes = entry.get("attributes") if entry.has("attributes") else None
        for name, value in (attributes.items() if isinstance(attributes, Dictionary) else []):
            column = layout.column(name)
            if column is None:
                raise RuleError(f"An attribute rule's edit names '{name}', which '{table}' has no field named.")
            out[column] = (target.stored(column, value) if target.active else _plain(value))
        if entry.has("geometry") and layout.geometry:
            shape = entry.get("geometry")
            out[layout.geometry] = _gpkg.encode(getattr(shape, "shape", None), layout.srid or 0, has_z, has_m)
        return out

    def locate(entry):
        if entry.has("objectID"):
            return _row(connection, table, f'"{layout.oid}" = ?', (int(entry.get("objectID")),))
        if entry.has("globalID") and global_id:
            return _row(connection, table, f'upper("{global_id}") = upper(?)', (to_text(entry.get("globalID")),))
        raise RuleError("An attribute rule's update or delete names its objectID or globalID.")

    for entry in edit.get("adds") or [] if edit.has("adds") else []:
        row = _subtypes.InsertPlan(connection, table).apply(values(entry))
        if target.active:
            row = target.insert(row)
        row = _behaviours.stamp_insert(connection, table, row)  # editor tracking (ADR-240)
        names = list(row)
        connection.execute(f'insert into "{table}" ({", ".join(chr(34) + n + chr(34) for n in names)}) values '
                           f'({", ".join("?" for _ in names)})', [row[n] for n in names])
    for entry in edit.get("updates") or [] if edit.has("updates") else []:
        old = locate(entry)
        if old is None:
            raise RuleError(f"An attribute rule's update found no row in '{table}'.")
        new = dict(old)
        new.update(values(entry))
        if target.active:
            new = target.update(old, new)
        new = _behaviours.stamp_update(connection, table, new)  # editor tracking (ADR-240)
        changed = [k for k in new if k != layout.oid and new[k] != old.get(k)]
        if changed:
            connection.execute(f'update "{table}" set {", ".join(chr(34) + k + chr(34) + " = ?" for k in changed)} '
                               f'where "{layout.oid}" = ?', [*(new[k] for k in changed), old[layout.oid]])
    for entry in edit.get("deletes") or [] if edit.has("deletes") else []:
        old = locate(entry)
        if old is None:
            continue
        if target.active:
            target.delete(old)
        _relationships.cascade(connection, table, [old[layout.oid]])
        connection.execute(f'delete from "{table}" where "{layout.oid}" = ?', (old[layout.oid],))


def _plain(value):
    from ._arcade._values import Date, is_number
    if isinstance(value, Date):
        return value.value.astimezone(datetime.timezone.utc).replace(tzinfo=None)
    if isinstance(value, bool):
        return int(value)
    if is_number(value):
        return value
    return value
