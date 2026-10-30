"""Arcade's data: FeatureSets on the GeoPackage a rule's table lives in, and the feature and FeatureSet functions
(ADR-235 §5.3).

``$datastore`` is a FeatureSetCollection of the GeoPackage; FeatureSetByName opens one of its tables. A FeatureSet is
lazy, as the Types page says: a table, the fields it carries, whether it carries geometry, SQL filters, an order, a
limit, and — after a spatial filter — the object ids it keeps. Filter, OrderBy and the statistics run as SQL in the
GeoPackage, through the connection the edit is being made on, so a rule sees the edit's own uncommitted rows.

INFERRED (ADR-235 §5.8): Stdev and Variance of a FeatureSet are sample statistics (n − 1), as the page says of Stdev;
``@name`` in a Filter is replaced by the variable's value written as an SQL literal (text quoted, a date as its ISO
text, an array as a parenthesised list); FeatureSet titles match without regard to case; a field of type Date reads
as a Date in UTC.
"""
from __future__ import annotations

import datetime
import math
import re
import sqlite3

from ._errors import ArcadeError
from ._fn_core import arity, first_is
from ._library import builtin
from ._values import Date, Dictionary, Feature, Geometry, is_number, to_number, to_text, type_of

_ESRI_TYPES = {"Integer": "esriFieldTypeInteger", "SmallInteger": "esriFieldTypeSmallInteger",
               "BigInteger": "esriFieldTypeBigInteger", "Double": "esriFieldTypeDouble",
               "Single": "esriFieldTypeSingle", "String": "esriFieldTypeString", "Date": "esriFieldTypeDate",
               "Blob": "esriFieldTypeBlob", "OID": "esriFieldTypeOID", "GlobalID": "esriFieldTypeGlobalID"}
_SHAPE_TYPES = {"Point": "esriGeometryPoint", "Multipoint": "esriGeometryMultipoint", "Polyline": "esriGeometryPolyline",
                "Polygon": "esriGeometryPolygon"}


class Table:
    """What a FeatureSet needs to know of one table: its columns and their types, object id, geometry column and
    reference."""

    def __init__(self, connection, name):
        from .. import _gpkg
        self.name = name
        columns, shape, srid, geometry = _gpkg.describe(connection, name)
        own = [r[1] for r in connection.execute(f'pragma table_info("{name}")')]
        self.oid = _gpkg.oid_column(connection, name)
        self.geometry = geometry if geometry in own else None
        self.shape = shape if self.geometry else None
        self.srid = srid if srid > 0 else None
        self.types = {c.name: c.type for c in columns}
        self.columns = [c.name for c in columns]
        self.nullable = {c.name: c.nullable for c in columns}

    @classmethod
    def made(cls, name, oid, geometry, shape, srid, columns):
        """A table described by hand: a temporary one a FeatureSet function made."""
        table = cls.__new__(cls)
        table.name, table.oid, table.geometry, table.shape = name, oid, geometry, shape if geometry else None
        table.srid = int(srid) if srid else None
        table.types = {c: t for c, t in columns}
        table.columns = [c for c, _ in columns]
        table.nullable = {c: True for c, _ in columns}
        return table

    def column(self, name):
        lower = name.lower()
        if lower == self.oid.lower():
            return self.oid
        for column in self.columns:
            if column.lower() == lower:
                return column
        return None


class Datastore:
    """The GeoPackage a rule's table is in, through an open connection."""

    def __init__(self, connection, version=None):
        self.connection = connection
        self.version = version  # the branch version an edit is made in (ADR-227), None for DEFAULT
        self._tables = {}
        _register_aggregates(connection)

    def names(self):
        return [r[0] for r in self.connection.execute("select table_name from gpkg_contents")]

    def table(self, name):
        lower = name.lower()
        if lower not in self._tables:
            found = next((n for n in self.names() if n.lower() == lower), None)
            if found is None:
                raise ArcadeError(f"The datastore has no feature class or table '{name}'.")
            self._tables[lower] = Table(self.connection, found)
        return self._tables[lower]

    def read(self, table, row, columns, geometry):
        """A Feature from a row of ``select oid, <columns>[, geometry]``."""
        from .. import _gpkg
        attributes = Dictionary()
        attributes.set(table.oid, row[0])
        for name, value in zip(columns, row[1:]):
            attributes.set(name, value_of(table.types.get(name), value))
        shape = None
        if geometry and table.geometry:
            decoded = _gpkg.decode(row[-1])
            shape = Geometry(decoded, table.srid) if decoded is not None and not decoded.is_empty else None
        feature = Feature(attributes, shape, immutable=True, source=(self, table))
        return feature


def value_of(kind, value):
    """A stored value as Arcade reads it: a Date field's text as a Date."""
    if value is None:
        return None
    if kind == "Date" and isinstance(value, str):
        from ._fn_date import parse_iso
        return parse_iso(value.replace(" ", "T", 1)) or value
    if isinstance(value, (bytes, memoryview)):
        return None
    return value


def sql_literal(value):
    if value is None:
        return "NULL"
    if isinstance(value, bool):
        return "1" if value else "0"
    if is_number(value):
        number = float(value)
        return str(int(number)) if number == int(number) else repr(number)
    if isinstance(value, Date):
        return "'" + value.value.astimezone(datetime.timezone.utc).strftime("%Y-%m-%d %H:%M:%S") + "'"
    if isinstance(value, list):
        return "(" + ", ".join(sql_literal(v) for v in value) + ")"
    return "'" + to_text(value).replace("'", "''") + "'"


class FeatureSet:
    def __init__(self, store, table, fields=None, geometry=True, where=(), order=None, limit=None, oids=None):
        self.store, self.table = store, table
        self.fields = fields  # None: every field
        self.geometry = geometry
        self.where, self.order, self.limit, self.oids = tuple(where), order, limit, oids

    def _copy(self, **changes):
        state = dict(fields=self.fields, geometry=self.geometry, where=self.where, order=self.order, limit=self.limit,
                     oids=self.oids)
        state.update(changes)
        return FeatureSet(self.store, self.table, **state)

    def filtered(self, sql):
        if self.limit is not None:
            return self.only([f.oid for f in self.features(geometry=False)])._copy(where=self.where + (sql,))
        return self._copy(where=self.where + (sql,))

    def ordered(self, sql):
        return self._copy(order=sql)

    def top(self, count):
        return self._copy(limit=count if self.limit is None else min(self.limit, count))

    def only(self, oids):
        kept = list(oids)
        if self.oids is not None:
            kept = [o for o in kept if o in set(self.oids)]
        return self._copy(oids=kept, limit=None, order=self.order)

    def _clause(self):
        parts = [f"({w})" for w in self.where]
        if self.oids is not None:
            parts.append(f'"{self.table.oid}" in ({", ".join(str(int(o)) for o in self.oids) or "NULL"})')
        sql = (" where " + " and ".join(parts)) if parts else ""
        if self.order:
            sql += f" order by {self.order}"
        if self.limit is not None:
            sql += f" limit {int(self.limit)}"
        return sql

    def _columns(self):
        if self.fields is None or "*" in self.fields:
            return list(self.table.columns)
        out = []
        for name in self.fields:
            column = self.table.column(name)
            if column is None:
                raise ArcadeError(f"'{self.table.name}' has no field '{name}'.")
            if column != self.table.oid:
                out.append(column)
        return out

    def _run(self, sql, parameters=()):
        try:
            return self.store.connection.execute(sql, parameters).fetchall()
        except sqlite3.Error as error:
            raise ArcadeError(f"The FeatureSet's SQL could not be run on '{self.table.name}': {error}.") from None

    def features(self, geometry=None):
        with_shape = self.geometry if geometry is None else geometry
        columns = self._columns()
        selected = [f'"{self.table.oid}"', *(f'"{c}"' for c in columns)]
        if with_shape and self.table.geometry:
            selected.append(f'"{self.table.geometry}"')
        rows = self._run(f'select {", ".join(selected)} from "{self.table.name}"{self._clause()}')
        out = []
        for row in rows:
            feature = self.store.read(self.table, row, columns, with_shape)
            out.append(_Read(feature, row[0]))
        return [f.feature for f in out] if geometry is None else out

    def subquery(self):
        """The FeatureSet's rows as an SQL subquery, its filters, order and limit applied."""
        return f'(select * from "{self.table.name}"{self._clause()})'

    def derived(self, sql, names):
        """A FeatureSet on a temporary table holding what ``sql`` selects, columns ``names``."""
        rows = self._run(sql)
        return temporary(self.store, [(n, "String") for n in names], rows)

    def count(self):
        if self.limit is None and not self.order:
            return int(self._run(f'select count(*) from "{self.table.name}"{self._clause()}')[0][0])
        return len(self._run(f'select 1 from "{self.table.name}"{self._clause()}'))

    def values(self, expression):
        rows = self._run(f'select {expression} from "{self.table.name}"{self._clause()}')
        return [to_number(r[0]) for r in rows if r[0] is not None]


class _Read:
    """A feature read with its object id kept apart, for spatial filters."""

    __slots__ = ("feature", "oid")

    def __init__(self, feature, oid):
        self.feature, self.oid = feature, oid

    @property
    def geometry(self):
        return self.feature.geometry


class FeatureSetCollection:
    """``$datastore``."""

    def __init__(self, store):
        self.store = store


def _featureset(name, value):
    if not isinstance(value, FeatureSet):
        raise ArcadeError(f"{name} takes a FeatureSet; a {type_of(value)} was given.")
    return value


@builtin("FeatureSetByName")
def _by_name(_, args):
    arity("FeatureSetByName", args, 2, 4)
    collection = args[0]
    if not isinstance(collection, FeatureSetCollection):
        raise ArcadeError(f"FeatureSetByName takes $datastore; a {type_of(collection)} was given.")
    fields = args[2] if len(args) > 2 and args[2] is not None else ["*"]
    if not isinstance(fields, list):
        raise ArcadeError("FeatureSetByName's fields are an array of field names.")
    geometry = bool(args[3]) if len(args) > 3 and args[3] is not None else True
    table = collection.store.table(to_text(args[1]))
    return FeatureSet(collection.store, table, [to_text(f) for f in fields], geometry)


_VARIABLE = re.compile(r"@([A-Za-z_$][A-Za-z0-9_$]*)")


def _substitute(interpreter, sql):
    def one(match):
        return sql_literal(interpreter.lookup(match.group(1), interpreter.line))
    return _VARIABLE.sub(one, sql)


@builtin("Filter", accepts=first_is(FeatureSet))
def _filter(interpreter, args):
    arity("Filter", args, 2)
    return args[0].filtered(_substitute(interpreter, to_text(args[1])))


@builtin("OrderBy")
def _order_by(interpreter, args):
    arity("OrderBy", args, 2)
    return _featureset("OrderBy", args[0]).ordered(to_text(args[1]))


@builtin("Top", accepts=first_is(FeatureSet))
def _top(_, args):
    arity("Top", args, 2)
    return args[0].top(max(int(to_number(args[1])), 0))


@builtin("First", accepts=first_is(FeatureSet))
def _first(_, args):
    arity("First", args, 1)
    found = args[0].top(1).features()
    return found[0] if found else None


@builtin("FilterBySubtypeCode")
def _filter_by_subtype(_, args):
    arity("FilterBySubtypeCode", args, 2)
    features = _featureset("FilterBySubtypeCode", args[0])
    from .. import _subtypes
    held = _subtypes.field(features.store.connection, features.table.name)
    if held is None:
        raise ArcadeError(f"'{features.table.name}' has no subtypes.")
    return features.filtered(f'"{held[0]}" = {sql_literal(args[1])}')


def _statistic(name, compute):
    @builtin(name, accepts=first_is(FeatureSet))
    def run(_, args):
        arity(name, args, 2)
        return compute(args[0].values(to_text(args[1])))
    return run


def _sample_variance(values):
    if len(values) < 2:
        return math.nan
    mean = sum(values) / len(values)
    return sum((v - mean) ** 2 for v in values) / (len(values) - 1)


_statistic("Sum", lambda v: float(math.fsum(v)))
_statistic("Max", lambda v: max(v) if v else None)
_statistic("Min", lambda v: min(v) if v else None)
_statistic("Mean", lambda v: sum(v) / len(v) if v else None)
_statistic("Average", lambda v: sum(v) / len(v) if v else None)
_statistic("Variance", _sample_variance)
_statistic("Stdev", lambda v: math.sqrt(_sample_variance(v)) if len(v) > 1 else math.nan)


@builtin("Distinct", accepts=first_is(FeatureSet))
def _distinct(_, args):
    """A FeatureSet of the unique values or combinations of the fields or SQL expressions given (text, an array of
    text, a dictionary of name and expression, or an array of them), as documented."""
    arity("Distinct", args, 2)
    features = args[0]
    columns = _named_expressions("Distinct", args[1])
    selected = ", ".join(f'{expression} as "{name}"' for name, expression in columns)
    return features.derived(f"select distinct {selected} from {features.subquery()}", [n for n, _ in columns])


_STATISTICS = {"SUM": "sum", "COUNT": "count", "MIN": "min", "MAX": "max", "AVG": "avg", "STDEV": "grapy_stdev",
               "VAR": "grapy_var"}


@builtin("GroupBy")
def _group_by(_, args):
    """A FeatureSet with one row per group: the grouping columns, then each statistic (SUM, COUNT, MIN, MAX, AVG, STDEV,
    VAR) of its SQL expression, as documented. INFERRED (ADR-235 §5.8): STDEV and VAR are sample statistics, as a
    FeatureSet's Stdev is."""
    arity("GroupBy", args, 3)
    features = _featureset("GroupBy", args[0])
    groups = _named_expressions("GroupBy", args[1])
    statistics = args[2] if isinstance(args[2], list) else [args[2]]
    parts, names = [f'{e} as "{n}"' for n, e in groups], [n for n, _ in groups]
    for statistic in statistics:
        if not isinstance(statistic, Dictionary) or not all(statistic.has(k) for k in ("name", "expression",
                                                                                      "statistic")):
            raise ArcadeError("GroupBy's statistics are dictionaries of name, expression and statistic.")
        kind = to_text(statistic.get("statistic")).upper()
        if kind not in _STATISTICS:
            raise ArcadeError(f"GroupBy: '{kind}' is not a statistic: SUM, COUNT, MIN, MAX, AVG, STDEV or VAR.")
        parts.append(f'{_STATISTICS[kind]}({to_text(statistic.get("expression"))}) as "{to_text(statistic.get("name"))}"')
        names.append(to_text(statistic.get("name")))
    grouped = ", ".join(e for _, e in groups)
    sql = f"select {', '.join(parts)} from {features.subquery()}" + (f" group by {grouped}" if grouped else "")
    return features.derived(sql, names)


def _named_expressions(name, value):
    """[(column name, SQL expression)] from text, an array of text, a dictionary or an array of dictionaries."""
    items = value if isinstance(value, list) else [value]
    out = []
    for item in items:
        if isinstance(item, Dictionary):
            if not item.has("name") or not item.has("expression"):
                raise ArcadeError(f"{name}: a dictionary names a column (name) and its SQL expression (expression).")
            out.append((to_text(item.get("name")), to_text(item.get("expression"))))
        elif isinstance(item, str):
            out.append((item, f'"{item}"'))
        else:
            raise ArcadeError(f"{name} takes field names or dictionaries of name and expression.")
    if not out:
        raise ArcadeError(f"{name} needs at least one field.")
    return out


class _Sample:
    """A sample variance aggregate for SQLite, which has none."""

    def __init__(self):
        self.values = []

    def step(self, value):
        if value is not None:
            try:
                self.values.append(float(value))
            except (TypeError, ValueError):
                pass

    def finalize(self):
        if len(self.values) < 2:
            return None
        mean = sum(self.values) / len(self.values)
        return sum((v - mean) ** 2 for v in self.values) / (len(self.values) - 1)


class _SampleDeviation(_Sample):
    def finalize(self):
        variance = super().finalize()
        return None if variance is None else math.sqrt(variance)


def _register_aggregates(connection):
    try:
        connection.create_aggregate("grapy_var", 1, _Sample)
        connection.create_aggregate("grapy_stdev", 1, _SampleDeviation)
    except sqlite3.Error:
        pass


_TEMPORARY = [0]
_ESRI_BACK = {v: k for k, v in _ESRI_TYPES.items()}


def temporary(store, columns, rows, geometry=None, srid=None, shape=None, oid="ROW__ID"):
    """A FeatureSet on a temporary table of the connection: ``columns`` [(name, type)], ``rows`` [[values]] and, with
    ``geometry``, a shape per row (shapely or None). INFERRED (ADR-235 §5.8): its object id is ROW__ID unless the
    definition names one."""
    _TEMPORARY[0] += 1
    name = f"grapy_arcade_{_TEMPORARY[0]}"
    defined = [f'"{oid}" integer primary key'] + [f'"{c}"' for c, _ in columns]
    if geometry is not None:
        defined.append('"shape" blob')
    store.connection.execute(f'create temp table "{name}" ({", ".join(defined)})')
    from .. import _gpkg
    names = [c for c, _ in columns] + (["shape"] if geometry is not None else [])
    for at, row in enumerate(rows):
        values = list(row) + ([_gpkg.encode(geometry[at], int(srid or 0))] if geometry is not None else [])
        store.connection.execute(f'insert into "{name}" ({", ".join(chr(34) + n + chr(34) for n in names)}) values '
                                 f'({", ".join("?" for _ in names)})', values)
    table = Table.made(name, oid, "shape" if geometry is not None else None, shape, srid, columns)
    return FeatureSet(store, table)


def _esri_definition(definition):
    import json
    if isinstance(definition, str):
        try:
            definition = json.loads(definition)
        except ValueError:
            raise ArcadeError("FeatureSet: the text is not JSON.") from None
    else:
        from ._values import to_json
        definition = to_json(definition)
    if not isinstance(definition, dict):
        raise ArcadeError("FeatureSet takes a JSON object with fields and features.")
    return definition


@builtin("FeatureSet")
def _featureset_of(interpreter, args):
    """A FeatureSet from ArcGIS REST JSON — text or a dictionary of fields, geometryType, spatialReference and
    features — held in a temporary table of the datastore's connection, or of one of its own without a datastore."""
    arity("FeatureSet", args, 1)
    definition = _esri_definition(args[0])
    collection = interpreter.profile.get("$datastore")
    store = collection.store if isinstance(collection, FeatureSetCollection) else _scratch(interpreter)
    fields = definition.get("fields") or []
    oid = next((f["name"] for f in fields if f.get("type") == "esriFieldTypeOID"), "ROW__ID")
    columns = [(f["name"], _ESRI_BACK.get(f.get("type"), "String")) for f in fields
               if f.get("type") != "esriFieldTypeOID"]
    reference = definition.get("spatialReference") or {}
    srid = reference.get("latestWkid") or reference.get("wkid")
    shape = {v: k for k, v in _SHAPE_TYPES.items()}.get(definition.get("geometryType"))
    rows, shapes, oids = [], [], []
    from ..conversion import _from_esri
    for feature in definition.get("features") or []:
        attributes = feature.get("attributes") or {}
        rows.append([attributes.get(c) for c, _ in columns])
        oids.append(attributes.get(oid))
        shapes.append(_from_esri(feature.get("geometry")) if feature.get("geometry") else None)
    made = temporary(store, columns, rows, shapes if shape else None, srid, shape, oid)
    if oid != "ROW__ID":
        for at, given in enumerate(oids, start=1):
            if given is not None:
                store.connection.execute(f'update "{made.table.name}" set "{oid}" = ? where rowid = ?', (given, at))
    return made


def _scratch(interpreter):
    if getattr(interpreter, "scratch", None) is None:
        interpreter.scratch = Datastore(sqlite3.connect(":memory:"))
    return interpreter.scratch


# ---- feature functions -----------------------------------------------------------------------------------------------


@builtin("Feature")
def _feature(_, args):
    from ._geometry import _from_definition, geometry_of
    if len(args) == 1:
        value = args[0]
        if isinstance(value, Feature):
            return Feature(Dictionary(value.attributes.items()), value.geometry)
        if isinstance(value, str):
            from ._fn_core import _dictionary
            value = _dictionary(None, [value])
        if isinstance(value, Dictionary):
            attributes = value.get("attributes")
            geometry = value.get("geometry")
            made = None
            if geometry is not None:
                made = geometry if isinstance(geometry, Geometry) else _from_definition(geometry)
            return Feature(Dictionary(attributes.items()) if isinstance(attributes, Dictionary) else Dictionary(), made)
        raise ArcadeError(f"Feature takes a feature, a dictionary or JSON text; a {type_of(value)} was given.")
    if not args:
        raise ArcadeError("Feature takes a geometry and attributes.")
    geometry = geometry_of(args[0], "Feature") if args[0] is not None else None
    if len(args) == 2 and isinstance(args[1], Dictionary):
        return Feature(Dictionary(args[1].items()), geometry)
    if len(args) % 2 == 0:
        raise ArcadeError("Feature takes a geometry, then pairs of an attribute name and a value.")
    attributes = Dictionary()
    for at in range(1, len(args), 2):
        attributes.set(to_text(args[at]), args[at + 1])
    return Feature(attributes, geometry)


@builtin("Expects")
def _expects(_, args):
    return None  # grapy reads every field of a feature; Expects asks for nothing it does not already have


@builtin("TimeReceived")
def _time_received(_, args):
    arity("TimeReceived", args, 1)
    return None  # not a stream layer's feature: null, as documented


@builtin("GetUser")
def _get_user(_, args):
    """A GeoPackage has no users: null, as the page says of a file geodatabase."""
    arity("GetUser", args, 0, 2)
    return None


def _source(name, value):
    """(datastore, table) of a feature or FeatureSet read from the GeoPackage."""
    if isinstance(value, FeatureSet):
        return value.store, value.table
    if isinstance(value, Feature) and value.source is not None:
        return value.source
    raise ArcadeError(f"{name} takes a feature or FeatureSet read from a table; a {type_of(value)} was given.")


@builtin("GdbVersion")
def _gdb_version(_, args):
    arity("GdbVersion", args, 1)
    store, table = _source("GdbVersion", args[0])
    from .. import _versions
    if _versions.kind(store.connection, table.name) is not None:
        # A versioned class (ADR-227): the version the edit is made in, DEFAULT unqualified as grapy names it.
        return store.version or "DEFAULT"
    return ""  # not a multi-user geodatabase: empty text, as documented


@builtin("Schema")
def _schema(_, args):
    arity("Schema", args, 1)
    store, table = _source("Schema", args[0])
    from .. import _attachments, _field_domains
    domains = _field_domains(store.connection, table.name)
    global_id = _attachments.global_id_field(store.connection, table.name)
    fields = [Dictionary({"name": table.oid, "alias": table.oid, "type": "esriFieldTypeOID", "editable": False,
                          "nullable": False})]
    for column in table.columns:
        kind = "GlobalID" if global_id and column.lower() == global_id.lower() else table.types.get(column, "String")
        fields.append(Dictionary({"name": column, "alias": column, "type": _ESRI_TYPES.get(kind, "esriFieldTypeString"),
                                  "editable": kind != "GlobalID", "nullable": table.nullable.get(column, True),
                                  "domain": _domain_dictionary(store.connection, domains.get(column.lower()))}))
    return Dictionary({"objectIdField": table.oid, "globalIdField": global_id or "",
                       "geometryType": _SHAPE_TYPES.get(table.shape, "esriGeometryNull"), "fields": fields})


def _domain_dictionary(connection, name):
    if not name:
        return None
    try:
        row = connection.execute("select domain_type, field_type from grapy_domains where name = ?", (name,)).fetchone()
    except sqlite3.Error:
        row = None
    kind = (row[0] if row else "CODED").upper()
    if kind == "CODED":
        coded = []
        for value, text in connection.execute("select value, description from gpkg_data_column_constraints where "
                                              "constraint_name = ? and constraint_type = 'enum' order by rowid", (name,)):
            code = value
            if row and row[1] and row[1].upper() in ("SHORT", "LONG", "FLOAT", "DOUBLE", "BIGINTEGER"):
                try:
                    code = float(value)
                except (TypeError, ValueError):
                    pass
            coded.append(Dictionary({"name": text, "code": code}))
        return Dictionary({"type": "codedValue", "name": name, "codedValues": coded})
    span = connection.execute("select min, max from gpkg_data_column_constraints where constraint_name = ? and "
                              "constraint_type = 'range'", (name,)).fetchone()
    return Dictionary({"type": "range", "name": name, "range": list(span) if span else None})


def _field_domain(name, value, field, subtype=None):
    store, table = _source(name, value)
    column = table.column(to_text(field))
    if column is None:
        raise ArcadeError(f"'{table.name}' has no field '{field}'.")
    from .. import _field_domains, _subtypes
    code = None
    if subtype is not None:
        code = int(to_number(subtype))
    elif isinstance(value, Feature):
        held = _subtypes.field(store.connection, table.name)
        if held is not None:
            current = value.attributes.get(held[0])
            code = int(current) if is_number(current) else None
    domain = None
    if code is not None:
        domain = _subtypes.domains(store.connection, table.name).get((column.lower(), code))
    if domain is None:
        domain = _field_domains(store.connection, table.name).get(column.lower())
    return _domain_dictionary(store.connection, domain), column


@builtin("Domain")
def _domain(_, args):
    arity("Domain", args, 2, 3)
    return _field_domain("Domain", args[0], args[1], args[2] if len(args) > 2 else None)[0]


@builtin("DomainName")
def _domain_name(_, args):
    """INFERRED (ADR-235 §5.8): without a coded value domain, or for a code the domain does not hold, the code itself."""
    arity("DomainName", args, 2, 4)
    domain, column = _field_domain("DomainName", args[0], args[1], args[3] if len(args) > 3 else None)
    if len(args) > 2 and args[2] is not None:
        code = args[2]
    elif isinstance(args[0], Feature):
        code = args[0].attributes.get(column)
    else:
        raise ArcadeError("DomainName on a FeatureSet takes the code.")
    if domain is None or domain.get("type") != "codedValue":
        return code
    for entry in domain.get("codedValues"):
        if _same_code(entry.get("code"), code):
            return entry.get("name")
    return code


@builtin("DomainCode")
def _domain_code(_, args):
    arity("DomainCode", args, 2, 4)
    domain, column = _field_domain("DomainCode", args[0], args[1], args[3] if len(args) > 3 else None)
    if len(args) > 2 and args[2] is not None:
        name = args[2]
    elif isinstance(args[0], Feature):
        name = args[0].attributes.get(column)
    else:
        raise ArcadeError("DomainCode on a FeatureSet takes the description.")
    if domain is None or domain.get("type") != "codedValue":
        return None
    for entry in domain.get("codedValues"):
        if to_text(entry.get("name")) == to_text(name):
            return entry.get("code")
    return None


def _same_code(a, b):
    if is_number(a) and is_number(b):
        return float(a) == float(b)
    return to_text(a) == to_text(b)


@builtin("Subtypes")
def _subtypes_of(_, args):
    arity("Subtypes", args, 1)
    store, table = _source("Subtypes", args[0])
    from .. import _subtypes
    held = _subtypes.field(store.connection, table.name)
    if held is None:
        return None
    codes = _subtypes.codes(store.connection, table.name)
    return Dictionary({"subtypeField": held[0],
                       "subtypes": [Dictionary({"name": n, "code": float(c)}) for c, n in codes.items()]})


@builtin("SubtypeCode")
def _subtype_code(_, args):
    arity("SubtypeCode", args, 1)
    store, table = _source("SubtypeCode", args[0])
    from .. import _subtypes
    held = _subtypes.field(store.connection, table.name)
    if held is None or not isinstance(args[0], Feature):
        return None
    return args[0].attributes.get(held[0])


@builtin("SubtypeName")
def _subtype_name(_, args):
    arity("SubtypeName", args, 1)
    store, table = _source("SubtypeName", args[0])
    from .. import _subtypes
    held = _subtypes.field(store.connection, table.name)
    if held is None or not isinstance(args[0], Feature):
        return None
    code = args[0].attributes.get(held[0])
    if not is_number(code):
        return None
    return _subtypes.codes(store.connection, table.name).get(int(code))


@builtin("FeatureInFilter")
def _feature_in_filter(interpreter, args):
    """The where clause is run against the feature's own attributes, in a table of one row."""
    arity("FeatureInFilter", args, 2)
    feature, where = args
    if feature is None:
        return False
    if not isinstance(feature, Feature):
        raise ArcadeError(f"FeatureInFilter takes a feature; a {type_of(feature)} was given.")
    clause = to_text(where).strip()
    if not clause:
        return True
    pairs = feature.attributes.items()
    memory = sqlite3.connect(":memory:")
    try:
        columns = ", ".join(f'"{k}"' for k, _ in pairs) or '"_"'
        memory.execute(f"create table row ({columns})")
        values = [sql_literal(v)[1:-1] if isinstance(v, Date) else (None if not isinstance(v, (str, int, float))
                  else v) for _, v in pairs] or [None]
        memory.execute(f"insert into row values ({', '.join('?' for _ in values)})", values)
        clause = _substitute(interpreter, clause)
        return bool(memory.execute(f"select count(*) from row where {clause}").fetchone()[0])
    except sqlite3.Error as error:
        raise ArcadeError(f"FeatureInFilter could not run '{clause}': {error}.") from None
    finally:
        memory.close()


@builtin("NextSequenceValue")
def _next_sequence_value(interpreter, args):
    arity("NextSequenceValue", args, 1)
    collection = interpreter.profile.get("$datastore")
    if not isinstance(collection, FeatureSetCollection):
        raise ArcadeError("NextSequenceValue needs a database: it runs in a profile with $datastore.")
    from .. import _sequences
    value = _sequences.next_value(collection.store.connection, to_text(args[0]))
    if value is None:
        raise ArcadeError(f"The database sequence '{args[0]}' does not exist.")
    return float(value)


# ---- related rows, associations and attachments (ADR-225, ADR-231, ADR-228) ------------------------------------------


def _feature_source(name, feature):
    if not isinstance(feature, Feature):
        raise ArcadeError(f"{name} takes a feature; a {type_of(feature)} was given.")
    if feature.source is None:
        raise ArcadeError(f"{name} takes a feature read from a table; this one was made in the expression.")
    return feature.source


def _global_id(name, store, table, feature):
    from .. import _attachments
    field = _attachments.global_id_field(store.connection, table.name)
    if field is None:
        raise ArcadeError(f"{name}: '{table.name}' has no global IDs.")
    return feature.attributes.get(field)


@builtin("FeatureSetByRelationshipClass")
def _by_relationship_class(_, args):
    """The rows related to a feature through a relationship class of the GeoPackage (ADR-225), named as it was made:
    destination rows for an origin feature, origin rows for a destination feature. A many-to-many or attributed class
    is read through its relationship table. ``fieldNames`` are the related table's fields."""
    arity("FeatureSetByRelationshipClass", args, 2, 4)
    store, table = _feature_source("FeatureSetByRelationshipClass", args[0])
    from .. import _relationships
    wanted = to_text(args[1]).lower()
    found = [rc for rc in _relationships.classes(store.connection)
             if rc["name"].lower() == wanted or rc["name"].rsplit("/", 1)[-1].lower() == wanted]
    if not found:
        raise ArcadeError(f"The datastore has no relationship class '{args[1]}'.")
    rc = found[0]
    feature = args[0]
    if rc["origin"].lower() == table.name.lower():
        own_key, other = rc["origin_primary_key"], rc["destination"]
        if rc["relationship_table"]:
            where = (f'"{rc["destination_primary_key"]}" in (select "{rc["destination_foreign_key"]}" from '
                     f'"{rc["relationship_table"]}" where "{rc["origin_foreign_key"]}" = {{key}})')
        else:
            where = f'"{rc["origin_foreign_key"]}" = {{key}}'
    elif rc["destination"].lower() == table.name.lower():
        other = rc["origin"]
        if rc["relationship_table"]:
            own_key = rc["destination_primary_key"]
            where = (f'"{rc["origin_primary_key"]}" in (select "{rc["origin_foreign_key"]}" from '
                     f'"{rc["relationship_table"]}" where "{rc["destination_foreign_key"]}" = {{key}})')
        else:
            own_key = rc["origin_foreign_key"]
            where = f'"{rc["origin_primary_key"]}" = {{key}}'
    else:
        raise ArcadeError(f"'{table.name}' does not take part in the relationship class '{args[1]}'.")
    key = feature.attributes.get(own_key)
    related = store.table(other)
    fields = args[2] if len(args) > 2 and args[2] is not None else ["*"]
    geometry = bool(args[3]) if len(args) > 3 and args[3] is not None else True
    features = FeatureSet(store, related, [to_text(f) for f in fields], geometry)
    return features.filtered(where.format(key=sql_literal(key)))


_ASSOCIATIONS = {"connected": ((1,), None), "container": ((2,), "to"), "content": ((2,), "from"),
                 "structure": ((3,), "to"), "attached": ((3,), "from"), "junctionedge": ((4, 6), None),
                 "midspan": ((5,), None)}


@builtin("FeatureSetByAssociation")
def _by_association(_, args):
    """The associations of a utility network feature (ADR-231), as the page lists them: one row per associated
    feature with className, globalId, objectId (the association's row), isContentVisible, percentAlong and side.
    ``connected`` takes either end, and with ``terminalName`` only associations at that terminal of the feature;
    ``container`` and ``structure`` answer the feature's container or structure, ``content`` and ``attached`` what it
    contains or has attached; ``junctionEdge`` answers side 'from' or 'to', ``midspan`` the percentAlong.

    INFERRED (ADR-235 §5.8): className is the class's own name, without its feature dataset; isContentVisible and
    percentAlong are null where the association type has none."""
    arity("FeatureSetByAssociation", args, 2, 3)
    store, table = _feature_source("FeatureSetByAssociation", args[0])
    kind = to_text(args[1]).lower()
    if kind not in _ASSOCIATIONS:
        raise ArcadeError("FeatureSetByAssociation's type is connected, container, content, structure, attached, "
                          "junctionEdge or midspan.")
    terminal = args[2] if len(args) > 2 and args[2] not in (None, "") else None
    if terminal is not None and kind != "connected":
        raise ArcadeError("FeatureSetByAssociation: a terminal name applies to connected associations only.")
    connection = store.connection
    exists = connection.execute("select 1 from sqlite_master where name = 'grapy_un_source'").fetchone()
    sources = connection.execute("select network, source_id from grapy_un_source where lower(class_name) = lower(?)",
                                 (table.name,)).fetchall() if exists else []
    if not sources:
        raise ArcadeError(f"'{table.name}' is not a class of a utility network.")
    network = sources[0][0]
    dataset = table.name.rsplit("/", 1)[0] if "/" in table.name else None
    associations = f"{dataset}/{network}_Associations" if dataset else f"{network}_Associations"
    names = {s: c for s, c in connection.execute("select source_id, class_name from grapy_un_source where network = ?",
                                                 (network,))}
    me = _global_id("FeatureSetByAssociation", store, table, args[0])
    terminal_id = _terminal_id(connection, network, table.name, args[0], terminal) if terminal is not None else None
    types, own_end = _ASSOCIATIONS[kind]
    rows = connection.execute(
        f'select fid, FROMNETWORKSOURCEID, FROMGLOBALID, FROMTERMINALID, TONETWORKSOURCEID, TOGLOBALID, TOTERMINALID, '
        f'ASSOCIATIONTYPE, ISCONTENTVISIBLE, PERCENTALONG from "{associations}" where ASSOCIATIONTYPE in '
        f'({", ".join(str(t) for t in types)}) and (upper(FROMGLOBALID) = upper(?) or upper(TOGLOBALID) = upper(?))',
        (me, me)).fetchall()
    out = []
    for oid, f_src, f_gid, f_term, t_src, t_gid, t_term, a_type, visible, percent in rows:
        from_me = str(f_gid).upper() == str(me).upper()
        if own_end == "from" and not from_me or own_end == "to" and from_me:
            continue
        if terminal_id is not None and (f_term if from_me else t_term) != terminal_id:
            continue
        other_source, other_gid = (t_src, t_gid) if from_me else (f_src, f_gid)
        side = None
        if a_type in (4, 6):
            side = "from" if a_type == 4 else "to"
        out.append([names.get(other_source, str(other_source)).rsplit("/", 1)[-1], other_gid, oid,
                    visible if a_type == 2 else None, percent if a_type == 5 else None, side])
    columns = [("className", "String"), ("globalId", "String"), ("objectId", "Integer"),
               ("isContentVisible", "Integer"), ("percentAlong", "Double"), ("side", "String")]
    return temporary(store, columns, out)


def _terminal_id(connection, network, class_name, feature, terminal):
    group, kind = feature.attributes.get("ASSETGROUP"), feature.attributes.get("ASSETTYPE")
    row = connection.execute(
        "select c.terminals from grapy_un_terminal_asset a join grapy_un_terminal_config c on c.network = a.network and "
        "c.id = a.config_id where a.network = ? and lower(a.class_name) = lower(?) and a.asset_group = ? and "
        "a.asset_type = ?", (network, class_name, group, kind)).fetchone()
    if row is None:
        raise ArcadeError(f"FeatureSetByAssociation: the feature's asset type has no terminal named '{terminal}'.")
    import json
    for terminal_id, name, *_ in json.loads(row[0]):
        if name.lower() == to_text(terminal).lower():
            return terminal_id
    raise ArcadeError(f"FeatureSetByAssociation: the feature's asset type has no terminal named '{terminal}'.")


@builtin("Attachments")
def _attachments_of(_, args):
    """A feature's attachments (ADR-228), each a dictionary of id, name, contentType and size, in order of id.
    ``options`` filters them by ``types`` (file extensions), ``minsize`` and ``maxsize`` in bytes; with ``metadata`` true
    an attachment also answers exifInfo and keywords, as UpgradeAttachments keeps them (null otherwise).

    INFERRED (ADR-235 §5.8): an attachment is a Dictionary; a type matches the name's extension without regard to case."""
    arity("Attachments", args, 1, 2)
    store, table = _feature_source("Attachments", args[0])
    from .. import _attachments
    if not _attachments.enabled(store.connection, table.name):
        return []
    me = _global_id("Attachments", store, table, args[0])
    options = args[1] if len(args) > 1 and isinstance(args[1], Dictionary) else Dictionary()
    types = [to_text(t).lower().lstrip(".") for t in (options.get("types") or [])]
    low, high = options.get("minsize"), options.get("maxsize")
    metadata = options.get("metadata") is True
    attach = _attachments.attach_table(table.name)
    columns = {r[1].upper() for r in store.connection.execute(f'pragma table_info("{attach}")')}
    extra = [c for c in ("EXIFINFO", "KEYWORDS") if c in columns]
    selected = ", ".join(["ATTACHMENTID", "ATT_NAME", "CONTENT_TYPE", "DATA_SIZE", *extra])
    out = []
    for row in store.connection.execute(f'select {selected} from "{attach}" where upper(REL_GLOBALID) = upper(?) '
                                        "order by ATTACHMENTID", (me,)):
        attachment_id, name, content_type, size = row[:4]
        if types and (name or "").rsplit(".", 1)[-1].lower() not in types:
            continue
        if low is not None and (size or 0) < to_number(low) or high is not None and (size or 0) > to_number(high):
            continue
        entry = Dictionary({"id": attachment_id, "name": name, "contentType": content_type, "size": size})
        if metadata:
            found = dict(zip(extra, row[4:]))
            entry.set("exifInfo", found.get("EXIFINFO"))
            entry.set("keywords", found.get("KEYWORDS"))
        out.append(entry)
    return out
