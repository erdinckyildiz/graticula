"""arcpy.management's tools for editing behaviours, unregistered tables, registration, views and statistics on a
GeoPackage — ADR-240. ``management`` imports them under their arcpy names; the behaviours live in ``_behaviours``.

What a GeoPackage makes of an enterprise geodatabase's words (INFERRED, ADR-240 §5.6):

- **registered** is listed in ``gpkg_contents``, with an integer primary key as its object ID — what every GeoPackage
  reader, and every grapy tool, takes as a table or feature class. **Unregistered** is a plain SQLite table in the same
  file: grapy's cursors read it by its path, and nothing lists it.
- **database statistics** are SQLite's (``ANALYZE``, read by its query planner); **indexes** are rebuilt with
  ``REINDEX``. A class's adds and deletes — an enterprise geodatabase's delta tables — are grapy's ``<table>__history``
  (ADR-227), which keeps both.
"""
from __future__ import annotations

import os

from . import _behaviours, _gpkg
from ._result import Result
from ._runtime import AddMessage, AddWarning, ExecuteError, NotBuiltError


def _m():
    from . import management
    return management


def _source_path(value):
    m = _m()
    layer = m._LAYERS.get(m._text(value))
    return layer.source if layer is not None else m._text(value)


def _feature_dataset_classes(connection, name):
    """The classes of a feature dataset ``name``, or None when it names none."""
    if not name or not _behaviours._has(connection, "grapy_feature_datasets"):
        return None
    if connection.execute("select 1 from grapy_feature_datasets where name = ?", (name,)).fetchone() is None:
        return None
    return [r[0] for r in connection.execute("select table_name from gpkg_contents where table_name like ? order by 1",
                                             (name + "/%",))]


def _targets(tool, in_dataset):
    """(connection, [tables], dataset name or None): a feature dataset's classes, or the one table a path names."""
    from ._raster import _is_raster_path
    path = _source_path(in_dataset)
    if _is_raster_path(path):
        raise NotBuiltError(f"arcpy.management.{tool} on a raster or mosaic dataset is not built in grapy: grapy's rasters "
                            "are files, which keep no rows to edit.")
    file, table = _gpkg.split(path)
    if not os.path.exists(file):
        raise ExecuteError(f"ERROR 000732: '{file}' does not exist.")
    connection = _gpkg.connect(file)
    classes = _feature_dataset_classes(connection, table)
    if classes is not None:
        return connection, classes, table
    connection.close()
    connection, table = _gpkg.table_of(path)
    if not _exists(connection, table):
        connection.close()
        raise ExecuteError(f"ERROR 000732: '{table}' does not exist in '{file}'.")
    return connection, [table], None


def _exists(connection, table):
    return connection.execute("select 1 from sqlite_master where name = ? and type in ('table', 'view')",
                              (table,)).fetchone() is not None


def _registered(connection, table):
    return connection.execute("select 1 from gpkg_contents where table_name = ?", (table,)).fetchone() is not None \
        if _behaviours._has(connection, "gpkg_contents") else False


def _columns(connection, table):
    """{lower name: (name, declared type)} of every column of a table."""
    return {r[1].lower(): (r[1], (r[2] or "").upper()) for r in connection.execute(f'pragma table_info("{table}")')}


def _kind(declared):
    return _gpkg._BACK.get(declared.split("(")[0], "String") if declared else "String"


def _flag(value, yes, no, tool, name):
    """A Boolean parameter given as True/False or as its keyword."""
    if value is None or str(value).strip() in ("", "#"):
        return None
    if isinstance(value, bool):
        return value
    word = str(value).upper()
    if word in (yes, "TRUE"):
        return True
    if word in (no, "FALSE"):
        return False
    raise ExecuteError(f"ERROR 000800: {tool}: {name} is {yes} or {no}, not '{value}'.")


# ---- editor tracking ------------------------------------------------------------------------------------------------


def _versioned(connection, table):
    from . import _versions
    return _versions.kind(connection, table)


def EnableEditorTracking(in_dataset, creator_field=None, creation_date_field=None, last_editor_field=None,
                         last_edit_date_field=None, add_fields="NO_ADD_FIELDS", record_dates_in="UTC"):
    """Editor tracking on a class, a table, or every class of a feature dataset: the fields named record who created
    each row and when, and who last edited it and when, on every edit grapy makes (``_behaviours``). The creator and
    editor fields are text, the date fields dates; with ``ADD_FIELDS`` a field that is not there is added, without it
    one that is not there is an error. Run again, it may track more fields, but not move a tracked one to another
    field nor change ``record_dates_in``, as documented. A class with attachments has its attachment table tracked
    with it. A branch versioned or archived class needs ``UTC``, as documented.

    INFERRED (ADR-240 §5.6 b): with no field named and ``ADD_FIELDS``, the four fields take the default names the page
    gives for a feature dataset (created_user, created_date, last_edited_user, last_edited_date); an insert stamps all
    four fields (the help topic records values when a row is "inserted or modified in any way"); ``DATABASE_TIME`` is
    the local time of the machine the GeoPackage is written on; with ``NO_ADD_FIELDS`` an attachment table is tracked
    only on the fields it already has; rows there before stay null, as the help topic says."""
    m = _m()
    adding = _flag(add_fields, "ADD_FIELDS", "NO_ADD_FIELDS", "EnableEditorTracking", "add_fields") or False
    zone = str(record_dates_in or "UTC").upper()
    if zone not in ("UTC", "DATABASE_TIME"):
        raise ExecuteError(f"ERROR 000800: EnableEditorTracking: record_dates_in is UTC or DATABASE_TIME, not '{record_dates_in}'.")
    utc = zone == "UTC"
    asked = [m._text(v) if m._given(v) else None for v in (creator_field, creation_date_field, last_editor_field,
                                                           last_edit_date_field)]
    if not any(asked):
        if not adding:
            raise ExecuteError("ERROR 000735: EnableEditorTracking: name at least one field to track, or ADD_FIELDS.")
        asked = list(_behaviours.DEFAULT_TRACKING)
    from . import _attachments
    connection, tables, _ = _targets("EnableEditorTracking", in_dataset)
    try:
        for table in tables:
            _track(connection, table, asked, adding, utc, require=True)
            if _attachments.enabled(connection, table):
                _track(connection, _attachments.attach_table(table), asked, adding, utc, require=False)
        connection.commit()
    finally:
        connection.close()
    return Result(m._text(in_dataset))


_ROLES = (("creator", "String"), ("creation date", "Date"), ("last editor", "String"), ("last edit date", "Date"))


def _track(connection, table, asked, adding, utc, require):
    if _versioned(connection, table) and not utc:
        raise ExecuteError(f"ERROR 001464: '{table}' is versioned or archived; its editor tracking dates must be UTC.")
    held = _behaviours.tracking(connection, table)
    if held is not None and held[4] != utc:
        raise ExecuteError(f"ERROR 001464: '{table}' already records its dates in "
                           f"{'UTC' if held[4] else 'database time'}; disable editor tracking to change it.")
    columns = _columns(connection, table)
    final = list(held[:4]) if held is not None else [None, None, None, None]
    for i, name in enumerate(asked):
        if not name:
            continue
        role, kind = _ROLES[i]
        if final[i] is not None and final[i].lower() != name.lower():
            raise ExecuteError(f"ERROR 001464: '{table}' already tracks its {role} in '{final[i]}'; disable editor "
                               "tracking to use another field.")
        found = columns.get(name.lower())
        if found is None:
            if not adding:
                if not require:
                    continue
                raise ExecuteError(f"ERROR 000728: the field '{name}' does not exist in '{table}'; create it or use "
                                   "ADD_FIELDS.")
            connection.execute(f'alter table "{table}" add column "{name}" {"TEXT" if kind == "String" else "DATETIME"}')
            found = (name, "TEXT" if kind == "String" else "DATETIME")
        elif _kind(found[1]) != kind:
            raise ExecuteError(f"ERROR 000338: the {role} field '{found[0]}' of '{table}' is {_kind(found[1])}; "
                               f"it must be a {'text' if kind == 'String' else 'date'} field.")
        final[i] = found[0]
    _behaviours.set_tracking(connection, table, final, utc)


def DisableEditorTracking(in_dataset, creator="DISABLE_CREATOR", creation_date="DISABLE_CREATION_DATE",
                          last_editor="DISABLE_LAST_EDITOR", last_edit_date="DISABLE_LAST_EDIT_DATE"):
    """Editor tracking stopped on the fields asked — all four by default — for a class, a table or every class of a
    feature dataset, and for a class's attachment table with it. The fields and the values in them stay, as the page
    says only that nothing more is recorded in them (INFERRED). A class not tracked gets a warning."""
    from . import _attachments
    m = _m()
    off = [_flag(value, f"DISABLE_{word}", f"NO_DISABLE_{word}", "DisableEditorTracking", word.lower()) is not False
           for value, word in ((creator, "CREATOR"), (creation_date, "CREATION_DATE"), (last_editor, "LAST_EDITOR"),
                               (last_edit_date, "LAST_EDIT_DATE"))]
    connection, tables, _ = _targets("DisableEditorTracking", in_dataset)
    try:
        for table in tables:
            for each in [table] + ([_attachments.attach_table(table)] if _attachments.enabled(connection, table) else []):
                held = _behaviours.tracking(connection, each)
                if held is None:
                    if each == table:
                        AddWarning(f"WARNING 000800: editor tracking is not enabled on '{table}'.")
                    continue
                kept = [None if stop else field for field, stop in zip(held[:4], off)]
                _behaviours.set_tracking(connection, each, kept, held[4])
        connection.commit()
    finally:
        connection.close()
    return Result(m._text(in_dataset))


# ---- last edit time -------------------------------------------------------------------------------------------------


def EnableLastEditTime(in_dataset):
    """A class's last edit time kept: every insert, update and delete — by grapy or by any other writer of the file —
    sets its ``gpkg_contents.last_change``, the GeoPackage standard's own timestamp of a table's last change. On a
    feature dataset, every class in it, and every class created in it later; a class that has it is skipped. The class
    must not be versioned or archived, as documented.

    INFERRED (ADR-240 §5.6 c): an enterprise geodatabase keeps the time in its own system tables, which are not
    published; the GeoPackage's place for the same fact is ``last_change``, and SQLite triggers keep it as the
    database would."""
    m = _m()
    connection, tables, dataset = _targets("EnableLastEditTime", in_dataset)
    try:
        for table in tables:
            if not _registered(connection, table):
                raise ExecuteError(f"ERROR 000800: '{table}' is not registered with the geodatabase "
                                   "(RegisterWithGeodatabase); it has no contents row to keep a last edit time in.")
            if _versioned(connection, table):
                raise ExecuteError(f"ERROR 001270: '{table}' is versioned or archived; last edit time needs nonversioned "
                                   "data.")
            if not _behaviours.last_edit_enabled(connection, table):
                _behaviours.enable_last_edit(connection, table)
        if dataset is not None:
            _behaviours.ensure(connection)
            connection.execute(f"insert or ignore into {_behaviours.LAST_EDIT} values (?)", (dataset + "/",))
        connection.commit()
    finally:
        connection.close()
    return Result(m._text(in_dataset))


def DisableLastEditTime(in_dataset):
    """A class's last edit time no longer kept (its triggers dropped); on a feature dataset, every class in it, and new
    classes no longer take it. The last ``last_change`` written stays."""
    m = _m()
    connection, tables, dataset = _targets("DisableLastEditTime", in_dataset)
    try:
        for table in tables:
            if not _behaviours.last_edit_enabled(connection, table):
                AddWarning(f"WARNING 000800: last edit time is not enabled on '{table}'.")
            _behaviours.disable_last_edit(connection, table)
        if dataset is not None:
            _behaviours.disable_last_edit(connection, dataset + "/")
        connection.commit()
    finally:
        connection.close()
    return Result(m._text(in_dataset))


def created_in_dataset(path):
    """CreateFeatureclass's hook: a class made in a feature dataset that keeps last edit time keeps it too."""
    file, table = _gpkg.split(path)
    if not table or "/" not in table:
        return
    connection = _gpkg.connect(file)
    try:
        if _behaviours.last_edit_enabled(connection, table.rsplit("/", 1)[0] + "/"):
            _behaviours.enable_last_edit(connection, table)
            connection.commit()
    finally:
        connection.close()


# ---- COGO -----------------------------------------------------------------------------------------------------------


def EnableCOGO(in_line_features):
    """A line class made COGO enabled: the five COGO fields — Direction, Distance, ArcLength, Radius and Radius2, all
    double — added where missing (one already there with the double type is kept), and the class marked. Direction is
    a north azimuth in decimal degrees, the others in the reference's linear unit, as documented; grapy has no traverse
    tool, so the fields are filled by whatever writes them.

    INFERRED (ADR-240 §5.6 d): the arc length field is named ``ArcLength``, as the page writes it in its field
    descriptions; a COGO field already there with another type is an error. The symbology and label expression the
    page says Pro adds are a map layer's, which a GeoPackage class does not carry."""
    m = _m()
    connection, tables, _ = _targets("EnableCOGO", in_line_features)
    try:
        for table in tables:
            shape = _gpkg.describe(connection, table)[1]
            if shape != "Polyline":
                raise ExecuteError(f"ERROR 000365: '{table}' is a {shape} class; COGO is enabled on line classes.")
            if _behaviours.cogo_enabled(connection, table):
                AddWarning(f"WARNING 000800: '{table}' is already COGO enabled.")
                continue
            columns = _columns(connection, table)
            for name in _behaviours.COGO_FIELDS:
                found = columns.get(name.lower())
                if found is None:
                    connection.execute(f'alter table "{table}" add column "{name}" DOUBLE')
                elif _kind(found[1]) != "Double":
                    raise ExecuteError(f"ERROR 000338: the COGO field '{found[0]}' of '{table}' is {_kind(found[1])}; "
                                       "it must be a double field.")
            _behaviours.ensure(connection)
            connection.execute(f"insert into {_behaviours.COGO} values (?)", (table,))
        connection.commit()
    finally:
        connection.close()
    return Result(m._text(in_line_features))


def DisableCOGO(in_line_features):
    """A line class no longer COGO enabled. Its COGO fields and their values stay, as the page says they "can be
    deleted" afterwards (DeleteField)."""
    m = _m()
    connection, tables, _ = _targets("DisableCOGO", in_line_features)
    try:
        for table in tables:
            if not _behaviours.cogo_enabled(connection, table):
                AddWarning(f"WARNING 000800: '{table}' is not COGO enabled.")
                continue
            connection.execute(f"delete from {_behaviours.COGO} where table_name = ?", (table,))
        connection.commit()
    finally:
        connection.close()
    return Result(m._text(in_line_features))


# ---- unregistered tables, incrementing IDs, registration --------------------------------------------------------------


def _workspace(tool, out_path):
    m = _m()
    file, table = _gpkg.split(m._text(out_path))
    if table:
        raise ExecuteError(f"ERROR 000732: {tool} writes into a GeoPackage, not into '{table}'.")
    if not os.path.exists(file):
        raise ExecuteError(f"ERROR 000732: '{file}' does not exist; create it first (CreateSQLiteDatabase).")
    return file


def _no_keyword(tool, config_keyword):
    if _m()._given(config_keyword):
        raise NotBuiltError(f"arcpy.management.{tool}: config_keyword sets an enterprise geodatabase's storage, which "
                            "a GeoPackage does not have; leave it empty.")


def _template_columns(templates):
    """[(name, declared type)] of the templates' fields, the first template winning a repeated name; each template's
    object ID first, as an integer field."""
    out, seen = [], set()
    for each in templates:
        connection, table = _gpkg.table_of(_source_path(each))
        try:
            oid = _gpkg.oid_column(connection, table)
            columns, _, _, geometry = _gpkg.describe(connection, table)
            listed = [(oid, "INTEGER")] if oid != "rowid" else []
            listed += [(c.name, _gpkg._TYPES.get(c.type.upper(), "TEXT")) for c in columns]
        finally:
            connection.close()
        for name, declared in listed:
            if name.lower() not in seen and name.lower() != _gpkg.GEOMETRY:
                seen.add(name.lower())
                out.append((name, declared))
    return out


def _unregistered(tool, out_path, out_name, columns):
    m = _m()
    file = _workspace(tool, out_path)
    name = m._text(out_name)
    connection = _gpkg.connect(file)
    try:
        _gpkg._ensure_metadata(connection, 0)
        if _exists(connection, name):
            raise ExecuteError(f"ERROR 000258: '{name}' already exists in '{file}'.")
        defined = ", ".join(f'"{n}" {d}' for n, d in columns)
        connection.execute(f'create table "{name}" ({defined})')
        connection.commit()
    finally:
        connection.close()
    return f"{file}/{name}"


def CreateUnRegisteredTable(out_path, out_name, template=None, config_keyword=None):
    """An empty table that is not registered: a plain SQLite table in the GeoPackage, absent from ``gpkg_contents``,
    with an integer ``OBJECTID`` field the database does not maintain — or the fields and types of the templates, as
    documented. grapy's cursors read and write it by its path; RegisterWithGeodatabase registers it.
    ``config_keyword`` is refused: it is an enterprise geodatabase's storage setting."""
    _no_keyword("CreateUnRegisteredTable", config_keyword)
    templates = _m()._list(template)
    columns = _template_columns(templates) if templates else [("OBJECTID", "INTEGER")]
    return Result(_unregistered("CreateUnRegisteredTable", out_path, out_name, columns))


_GEOMETRY_TYPES = {"POINT": "POINT", "MULTIPOINT": "MULTIPOINT", "POLYLINE": "MULTILINESTRING", "POLYGON": "MULTIPOLYGON"}


def CreateUnRegisteredFeatureclass(out_path, out_name, geometry_type="POLYGON", template=None, has_m="DISABLED",
                                   has_z="DISABLED", spatial_reference=None, config_keyword=None):
    """An empty feature class that is not registered: a plain SQLite table with an integer ``OBJECTID`` field (or the
    templates' fields) and a geometry column, absent from ``gpkg_contents`` and ``gpkg_geometry_columns``.

    The geometry column is declared with the GeoPackage geometry type name (POINT, MULTIPOINT, MULTILINESTRING or
    MULTIPOLYGON), as the GeoPackage standard declares one. The reference and the z and m flags are kept in
    ``grapy_unregistered`` until RegisterWithGeodatabase registers the class with them.

    INFERRED (ADR-240 §5.6 e): the geometry column is named ``geom``, grapy's own, so grapy's cursors read and write
    its shapes before it is registered; without ``spatial_reference``, the first template's reference is used.
    ``config_keyword`` is refused: it is an enterprise geodatabase's storage setting."""
    from ._reference import reference_of
    _no_keyword("CreateUnRegisteredFeatureclass", config_keyword)
    word = str(geometry_type or "POLYGON").upper()
    if word not in _GEOMETRY_TYPES:
        raise ExecuteError(f"ERROR 000800: CreateUnRegisteredFeatureclass: geometry_type is POINT, MULTIPOINT, "
                           f"POLYLINE or POLYGON, not '{geometry_type}'.")
    m = _m()
    templates = m._list(template)
    template_z = template_m = False
    srid = 0
    if templates:
        connection, table = _gpkg.table_of(_source_path(templates[0]))
        try:
            template_z, template_m = _gpkg.dimensions(connection, table)
            srid = _gpkg.describe(connection, table)[2]
        finally:
            connection.close()

    def dimension(value, own, name):
        said = str(value or "DISABLED").upper()
        if said == "SAME_AS_TEMPLATE":
            return own
        if said not in ("ENABLED", "DISABLED"):
            raise ExecuteError(f"ERROR 000800: CreateUnRegisteredFeatureclass: {name} is ENABLED, DISABLED or "
                               f"SAME_AS_TEMPLATE, not '{value}'.")
        return said == "ENABLED"

    with_z, with_m = dimension(has_z, template_z, "has_z"), dimension(has_m, template_m, "has_m")
    if m._given(spatial_reference):
        reference = reference_of(spatial_reference)
        srid = reference.factoryCode if reference is not None else 0
    columns = _template_columns(templates) if templates else [("OBJECTID", "INTEGER")]
    columns.append((_gpkg.GEOMETRY, _GEOMETRY_TYPES[word]))
    path = _unregistered("CreateUnRegisteredFeatureclass", out_path, out_name, columns)
    file, table = _gpkg.split(path)
    connection = _gpkg.connect(file)
    try:
        _gpkg._ensure_metadata(connection, srid)
        _behaviours.ensure(connection)
        connection.execute(f"insert into {_behaviours.UNREGISTERED} values (?, ?, ?, ?, ?)",
                           (table, _GEOMETRY_TYPES[word], srid, int(with_z), int(with_m)))
        connection.commit()
    finally:
        connection.close()
    return Result(path)


def AddIncrementingIDField(in_table, field_name="ObjectID"):
    """A database-maintained ID field added to an unregistered table: an integer field numbering the rows there
    1, 2 … and, through a trigger, every row inserted after — by grapy or by any other writer — the next number, unless
    the insert gives one. As documented: a registered table is refused (it has its object ID), a name already taken
    stops the tool, and a table that has such a field gets a warning and no second one.

    INFERRED (ADR-240 §5.6 f): SQLite has no identity column to add to a table, so the field is an integer column kept
    by an AFTER INSERT trigger and a unique index; existing rows are numbered in their storage (rowid) order."""
    m = _m()
    name = m._text(field_name) if m._given(field_name) else "ObjectID"
    path = _source_path(in_table)
    connection, table = _gpkg.table_of(path)
    try:
        if not _exists(connection, table):
            raise ExecuteError(f"ERROR 000732: '{table}' does not exist.")
        if _registered(connection, table):
            raise ExecuteError(f"ERROR 001259: '{table}' is registered with the geodatabase; its object ID is its "
                               "incrementing ID, and this tool runs on unregistered tables.")
        if _behaviours.incrementing(connection, table):
            AddWarning(f"WARNING 001260: '{table}' already has an incrementing ID field, "
                       f"'{_behaviours.incrementing(connection, table)[0]}'; none was added.")
            return Result(m._text(in_table))
        if name.lower() in _columns(connection, table):
            raise ExecuteError(f"ERROR 000012: '{name}' already exists in '{table}'; give the ID field another name.")
        _behaviours.add_incrementing(connection, table, name)
        connection.commit()
    finally:
        connection.close()
    return Result(m._text(in_table))


_REGISTER_TYPES = {"POINT": "POINT", "MULTIPOINT": "MULTIPOINT", "POLYLINE": "MULTILINESTRING",
                   "POLYGON": "MULTIPOLYGON"}
_SHAPE_OF = {"POINT": ("Point",), "MULTIPOINT": ("MultiPoint", "Point"),
             "MULTILINESTRING": ("LineString", "MultiLineString"), "MULTIPOLYGON": ("Polygon", "MultiPolygon")}


def RegisterWithGeodatabase(in_dataset, in_object_id_field=None, in_shape_field=None, in_geometry_type=None,
                            in_spatial_reference=None, in_extent=None):
    """An unregistered table or feature class registered: listed in ``gpkg_contents`` (and, with a shape field, in
    ``gpkg_geometry_columns``), its object ID made the table's integer primary key — after which every grapy tool and
    every GeoPackage reader sees it as a table or feature class.

    As documented: the object ID is the integer field named, or else one created and numbered; with a shape field the
    geometry type is required and must match the shapes there; the reference must match them, and is needed only when
    the table is empty; the extent is the one given, or the shapes'. A view (read-only, with no primary key grapy's
    cursors can key rows by) and a raster are refused.

    INFERRED (ADR-240 §5.6 g): the table is rebuilt with the object ID as its ``integer primary key autoincrement``,
    SQLite having no way to make an existing column the key; without a field named, an integer ``OBJECTID`` already
    there is used, its empty values numbered after its largest, and otherwise a new ``OBJECTID`` is created; values
    that repeat are an error. Without a shape field named, a class made by CreateUnRegisteredFeatureclass registers its
    ``geom`` column with the type, reference and dimensions it was made with. MULTIPATCH registers polygons with z as a
    multipatch class, each polygon a face, as ADR-238 holds one (§5.2 (a)); D-329's sweep."""
    from ._reference import reference_of
    from ._raster import _is_raster_path
    m = _m()
    path = _source_path(in_dataset)
    if _is_raster_path(path):
        raise NotBuiltError("arcpy.management.RegisterWithGeodatabase on a raster is not built in grapy: grapy's rasters "
                            "are files beside the GeoPackage, not tables in it.")
    connection, table = _gpkg.table_of(path)
    try:
        kind = connection.execute("select type from sqlite_master where name = ?", (table,)).fetchone()
        if kind is None:
            raise ExecuteError(f"ERROR 000732: '{table}' does not exist.")
        if kind[0] == "view":
            raise NotBuiltError("arcpy.management.RegisterWithGeodatabase on a view is not built in grapy: grapy's "
                                "cursors key every row by its table's integer primary key, which a SQLite view has not.")
        if _registered(connection, table):
            raise ExecuteError(f"ERROR 000800: '{table}' is already registered with the geodatabase.")
        columns = _columns(connection, table)
        pending = connection.execute(f"select geometry_type, srs_id, z, m from {_behaviours.UNREGISTERED} where "
                                     "table_name = ?", (table,)).fetchone() \
            if _behaviours._has(connection, _behaviours.UNREGISTERED) else None
        # The shape.
        multipatch = False
        shape_field = m._text(in_shape_field) if m._given(in_shape_field) else (_gpkg.GEOMETRY if pending else None)
        stored = None
        if shape_field is not None:
            found = columns.get(shape_field.lower())
            if found is None:
                raise ExecuteError(f"ERROR 000728: the field '{shape_field}' does not exist in '{table}'.")
            shape_field = found[0]
            if m._given(in_geometry_type):
                word = str(in_geometry_type).upper()
                if word == "MULTIPATCH":  # ADR-238: faces as MultiPolygon Z, marked as a multipatch class
                    multipatch, word = True, "POLYGON"
                if word not in _REGISTER_TYPES:
                    raise ExecuteError(f"ERROR 000800: in_geometry_type is POINT, MULTIPOINT, POLYLINE, POLYGON or "
                                       f"MULTIPATCH, not '{in_geometry_type}'.")
                stored = _REGISTER_TYPES[word]
            elif pending:
                stored = pending[0]
            else:
                raise ExecuteError("ERROR 000735: in_geometry_type is required when a shape field is given.")
        oid = _object_id(connection, table, columns, m._text(in_object_id_field) if m._given(in_object_id_field) else None)
        srid = z = mm = 0
        bounds = None
        if shape_field is not None:
            srid, z, mm, bounds = _shapes(connection, table, shape_field, stored)
            if multipatch:
                z = 1  # a multipatch's z is mandatory (ADR-238 §5.2 (a))
            if m._given(in_spatial_reference):
                reference = reference_of(in_spatial_reference)
                given = reference.factoryCode if reference is not None else 0
                if srid and given != srid:
                    raise ExecuteError(f"ERROR 000800: the shapes in '{table}' are in EPSG:{srid}, not EPSG:{given}.")
                srid = given
            elif srid == 0 and pending:
                srid = pending[1]
            if srid == 0 and bounds is None and not pending:
                raise ExecuteError(f"ERROR 000735: '{table}' is empty; in_spatial_reference is required.")
            if pending:
                z, mm = max(z, pending[2]), max(mm, pending[3])
        if m._given(in_extent):
            text = _extent_text(in_extent) if hasattr(in_extent, "XMin") else str(in_extent)
            bounds = tuple(float(v) for v in text.replace(",", " ").split()[:4])
        if shape_field is not None and srid:
            _stamp_reference(connection, table, shape_field, srid)
        _rebuild(connection, table, oid, shape_field, stored)
        _gpkg._ensure_metadata(connection, srid)
        data_type = "features" if shape_field is not None else "attributes"
        connection.execute("insert into gpkg_contents (table_name, data_type, identifier, srs_id, min_x, min_y, max_x, "
                           "max_y) values (?, ?, ?, ?, ?, ?, ?, ?)",
                           (table, data_type, table, srid if shape_field is not None else None, *(bounds or (None,) * 4)))
        if shape_field is not None:
            connection.execute("insert into gpkg_geometry_columns values (?, ?, ?, ?, ?, ?)",
                               (table, shape_field, stored, srid, z, mm))
        if multipatch:
            from . import _multipatch
            connection.execute("create table if not exists gpkg_extensions (table_name text, column_name text, "
                               "extension_name text not null, definition text not null, scope text not null, "
                               "constraint ge_tce unique (table_name, column_name, extension_name))")
            connection.execute("insert or ignore into gpkg_extensions values (?, ?, ?, ?, 'read-write')",
                               (table, shape_field, _multipatch.EXTENSION, _multipatch.DEFINITION))
        if pending:
            connection.execute(f"delete from {_behaviours.UNREGISTERED} where table_name = ?", (table,))
        _behaviours.restore_triggers(connection, table)
        connection.commit()
    finally:
        connection.close()
    return Result(m._text(in_dataset))


def _extent_text(extent):
    return f"{extent.XMin} {extent.YMin} {extent.XMax} {extent.YMax}"


def _object_id(connection, table, columns, named):
    """The object ID column of a table being registered, its values made whole: the field named, an OBJECTID there,
    or a new OBJECTID numbered in row order."""
    name = named or ("OBJECTID" if "objectid" in columns else None)
    if name is None:
        connection.execute(f'alter table "{table}" add column "OBJECTID" INTEGER')
        for number, (rowid,) in enumerate(connection.execute(f'select rowid from "{table}" order by rowid').fetchall(), 1):
            connection.execute(f'update "{table}" set "OBJECTID" = ? where rowid = ?', (number, rowid))
        return "OBJECTID"
    found = columns.get(name.lower())
    if found is None:
        raise ExecuteError(f"ERROR 000728: the field '{name}' does not exist in '{table}'.")
    if _kind(found[1]) not in ("Integer", "SmallInteger"):
        raise ExecuteError(f"ERROR 000338: the object ID field '{found[0]}' must be an integer field, not {_kind(found[1])}.")
    name = found[0]
    repeated = connection.execute(f'select "{name}" from "{table}" where "{name}" is not null group by "{name}" '
                                  "having count(*) > 1 limit 1").fetchone()
    if repeated is not None:
        raise ExecuteError(f"ERROR 000800: '{name}' repeats the value {repeated[0]}; an object ID must be unique.")
    top = connection.execute(f'select coalesce(max("{name}"), 0) from "{table}"').fetchone()[0]
    for rowid, in connection.execute(f'select rowid from "{table}" where "{name}" is null order by rowid').fetchall():
        top += 1
        connection.execute(f'update "{table}" set "{name}" = ? where rowid = ?', (top, rowid))
    return name


def _shapes(connection, table, field, stored):
    """(srid, z, m, bounds) of the shapes in a column, each checked against the geometry type."""
    import shapely
    srid, z, mm, bounds = 0, 0, 0, None
    allowed = _SHAPE_OF[stored]
    for (blob,) in connection.execute(f'select "{field}" from "{table}" where "{field}" is not null'):
        if not isinstance(blob, bytes) or blob[:2] != b"GP":
            raise ExecuteError(f"ERROR 000800: '{field}' holds a value that is not a GeoPackage geometry.")
        own = int.from_bytes(blob[4:8], "little", signed=True)
        if srid and own != srid:
            raise ExecuteError(f"ERROR 000800: the shapes in '{field}' are in more than one spatial reference.")
        srid = own
        shape = _gpkg.decode(blob)
        if shape is None or shape.is_empty:
            continue
        if shape.geom_type not in allowed:
            raise ExecuteError(f"ERROR 000800: '{field}' holds a {shape.geom_type}, which is not a {stored}.")
        z, mm = max(z, int(shapely.has_z(shape))), max(mm, int(shapely.has_m(shape)))
        b = shape.bounds
        bounds = b if bounds is None else (min(bounds[0], b[0]), min(bounds[1], b[1]), max(bounds[2], b[2]),
                                           max(bounds[3], b[3]))
    return max(srid, 0), z, mm, bounds


def _stamp_reference(connection, table, field, srid):
    """Shapes written before registration with no reference (0) given the class's, in their GeoPackage header, which
    the standard requires to match the geometry column's."""
    import struct
    for rowid, blob in connection.execute(f'select rowid, "{field}" from "{table}" where "{field}" is not null').fetchall():
        if int.from_bytes(blob[4:8], "little", signed=True) == 0:
            connection.execute(f'update "{table}" set "{field}" = ? where rowid = ?',
                               (blob[:4] + struct.pack("<i", srid) + blob[8:], rowid))


def _rebuild(connection, table, oid, shape_field=None, stored=None):
    """The table made again with ``oid`` as its integer primary key, the other columns in their order — the shape
    field, when there is one, declared with its geometry type name, as GeoPackage 1.4 Requirement 31 asks."""
    listed = [(r[1], r[2] or "") for r in connection.execute(f'pragma table_info("{table}")')]
    others = [(n, stored if shape_field is not None and n == shape_field else d) for n, d in listed if n != oid]
    temporary = f"{table}__registering"
    defined = ", ".join([f'"{oid}" integer primary key autoincrement'] + [f'"{n}" {d}'.rstrip() for n, d in others])
    connection.execute(f'create table "{temporary}" ({defined})')
    names = ", ".join(f'"{n}"' for n in [oid] + [n for n, _ in others])
    connection.execute(f'insert into "{temporary}" ({names}) select {names} from "{table}"')
    connection.execute(f'drop table "{table}"')
    connection.execute(f'alter table "{temporary}" rename to "{table}"')


# ---- views ----------------------------------------------------------------------------------------------------------


def CreateDatabaseView(input_database, view_name, view_definition):
    """A view in the GeoPackage — ``create view <name> as <definition>`` — which SQLite validates when the tool runs,
    as the page says the database does. The view is not registered with the geodatabase, as documented; grapy's
    SearchCursor reads it by ``<file>.gpkg/<view>``. One statement only: a definition that carries a second statement
    is an error.

    INFERRED (ADR-240 §5.6 i): the view's object ID (``OID@``) is its first integer field, as a GeoPackage view's
    pseudo-key is; a view with none gets a warning, since ``OID@`` has nothing to read in it."""
    import sqlite3
    m = _m()
    file = _workspace("CreateDatabaseView", input_database)
    name = m._text(view_name)
    connection = _gpkg.connect(file)
    try:
        if _exists(connection, name):
            raise ExecuteError(f"ERROR 000258: '{name}' already exists in '{file}'.")
        try:
            connection.execute(f'create view "{name}" as {m._text(view_definition)}')
            connection.execute(f'select * from "{name}" limit 0')
            if _gpkg.oid_column(connection, name) == "rowid":
                AddWarning(f"WARNING 000800: the view '{name}' has no integer field to serve as its object ID; read it "
                           "by its named fields.")
        except (sqlite3.Error, sqlite3.Warning) as error:
            connection.rollback()
            connection.execute(f'drop view if exists "{name}"')
            raise ExecuteError(f"ERROR 000800: the view definition is not valid SQL for a GeoPackage: {error}") from None
        connection.commit()
    finally:
        connection.close()
    return Result(f"{file}/{name}")


# ---- statistics and indexes -------------------------------------------------------------------------------------------


def _history(connection, table):
    from . import _versions
    name = _versions.history(table)
    return [name] if _versioned(connection, table) and _exists(connection, name) else []


def _say_analyzed(tool, names):
    AddMessage(f"{tool}: statistics updated for {', '.join(names) if names else 'no table'}.")


def _analyze(connection, names):
    for name in names:
        connection.execute(f'analyze "{name}"')


_COMPONENTS = ("BUSINESS", "FEATURE", "RASTER", "ADDS", "DELETES")


def Analyze(in_dataset, components):
    """A dataset's database statistics updated — SQLite's, which its query planner reads (``ANALYZE``). Components:
    BUSINESS and FEATURE, the class's table (a GeoPackage keeps attributes and shapes in one table); ADDS and DELETES,
    its ``__history`` table when it is versioned or archived (ADR-227); RASTER, its raster tables, of which a
    GeoPackage feature class or table has none. At least one component is required, as documented."""
    m = _m()
    asked = [str(c).upper() for c in m._list(components)]
    if not asked:
        raise ExecuteError("ERROR 000735: Analyze: components is required.")
    for word in asked:
        if word not in _COMPONENTS:
            raise ExecuteError(f"ERROR 000800: Analyze: a component is one of {', '.join(_COMPONENTS)}, not '{word}'.")
    connection, tables, _ = _targets("Analyze", in_dataset)
    try:
        done = []
        for table in tables:
            if {"BUSINESS", "FEATURE"} & set(asked):
                done.append(table)
            if {"ADDS", "DELETES"} & set(asked):
                done += _history(connection, table)
        _analyze(connection, done)
        connection.commit()
    finally:
        connection.close()
    _say_analyzed("Analyze", done)
    return Result(m._text(in_dataset))


def _system_tables(connection):
    return [r[0] for r in connection.execute("select name from sqlite_master where type = 'table' and "
                                             "(name like 'gpkg%' or name like 'grapy%') order by name")]


def _datasets(tool, connection, include_system, in_datasets):
    """The tables a database tool works on: each dataset named (a feature dataset's classes), relative to the
    GeoPackage, as documented; at least one dataset unless ``SYSTEM``."""
    m = _m()
    named = m._list(in_datasets)
    if not named and not include_system:
        raise ExecuteError(f"ERROR 000735: {tool}: name at least one dataset, or include the system tables (SYSTEM).")
    tables = []
    for name in named:
        if "/" in name.replace("\\", "/") and name.lower().endswith(".gpkg"):
            raise ExecuteError(f"ERROR 000800: {tool}: a dataset is named relative to the database, not '{name}'.")
        classes = _feature_dataset_classes(connection, name)
        if classes is not None:
            tables += classes
        elif _exists(connection, name):
            tables.append(name)
        else:
            raise ExecuteError(f"ERROR 000732: {tool}: '{name}' does not exist in the database.")
    return tables


def AnalyzeDatasets(input_database, include_system="NO_SYSTEM", in_datasets=None, analyze_base="ANALYZE_BASE",
                    analyze_delta="ANALYZE_DELTA", analyze_archive="ANALYZE_ARCHIVE"):
    """The database statistics (SQLite's ``ANALYZE``) of the datasets named in a GeoPackage, and with ``SYSTEM`` of its
    system tables. ``analyze_base``: each dataset's own table. ``analyze_delta`` and ``analyze_archive``: its
    ``__history`` table when it is versioned or archived (ADR-227), which holds both its edits and its archive.

    INFERRED (ADR-240 §5.6 h): the system tables are the GeoPackage's ``gpkg_*`` tables and grapy's ``grapy_*`` ones —
    grapy's versions and moments among them, the states and state lineages tables' part."""
    m = _m()
    system = _flag(include_system, "SYSTEM", "NO_SYSTEM", "AnalyzeDatasets", "include_system") or False
    base = _flag(analyze_base, "ANALYZE_BASE", "NO_ANALYZE_BASE", "AnalyzeDatasets", "analyze_base") is not False
    delta = _flag(analyze_delta, "ANALYZE_DELTA", "NO_ANALYZE_DELTA", "AnalyzeDatasets", "analyze_delta") is not False
    archive = _flag(analyze_archive, "ANALYZE_ARCHIVE", "NO_ANALYZE_ARCHIVE", "AnalyzeDatasets",
                    "analyze_archive") is not False
    file = _workspace("AnalyzeDatasets", input_database)
    connection = _gpkg.connect(file)
    try:
        done = []
        for table in _datasets("AnalyzeDatasets", connection, system, in_datasets):
            if base:
                done.append(table)
            if delta or archive:
                done += _history(connection, table)
        if system:
            done += _system_tables(connection)
        done = list(dict.fromkeys(done))
        _analyze(connection, done)
        connection.commit()
    finally:
        connection.close()
    _say_analyzed("AnalyzeDatasets", done)
    return Result(m._text(input_database))


def RebuildIndexes(input_database, include_system="NO_SYSTEM", in_datasets=None, delta_only="ONLY_DELTAS"):
    """The indexes of the datasets named in a GeoPackage rebuilt (SQLite's ``REINDEX``), and with ``SYSTEM`` those of
    its system tables. ``ONLY_DELTAS``, the default, rebuilds only each dataset's ``__history`` table — grapy's delta
    tables (ADR-227) — so a dataset neither versioned nor archived has none to rebuild; ``ALL`` rebuilds the dataset's
    own table's indexes too."""
    m = _m()
    system = _flag(include_system, "SYSTEM", "NO_SYSTEM", "RebuildIndexes", "include_system") or False
    word = str(delta_only or "ONLY_DELTAS").upper() if not isinstance(delta_only, bool) else \
        ("ONLY_DELTAS" if delta_only else "ALL")
    if word not in ("ONLY_DELTAS", "ALL"):
        raise ExecuteError(f"ERROR 000800: RebuildIndexes: delta_only is ONLY_DELTAS or ALL, not '{delta_only}'.")
    file = _workspace("RebuildIndexes", input_database)
    connection = _gpkg.connect(file)
    try:
        done = []
        for table in _datasets("RebuildIndexes", connection, system, in_datasets):
            if word == "ALL":
                done.append(table)
            done += _history(connection, table)
        if system:
            done += _system_tables(connection)
        done = list(dict.fromkeys(done))
        for name in done:
            connection.execute(f'reindex "{name}"')
        connection.commit()
    finally:
        connection.close()
    AddMessage(f"RebuildIndexes: indexes rebuilt for {', '.join(done) if done else 'no table'}.")
    return Result(m._text(input_database))


# ---- refused, each with its own reason ------------------------------------------------------------------------------

_ANNOTATION = ("keeps each feature's text and its symbol in the Element BLOB field and the class's text symbols in its "
               "symbol collection, both in Esri's binary form, which is not published")

REFUSED = {
    "AppendAnnotation": "appends geodatabase annotation feature classes, whose features " + _ANNOTATION,
    "CreateEnterpriseGeodatabase": (
        "creates a database on a DBMS server and Esri's geodatabase system tables in it, under an ArcGIS Server "
        "authorization file; a fenced user tool has no database server connection (ADR-223 §5.3) and those system "
        "tables are not published — CreateSQLiteDatabase makes grapy's GeoPackage workspace"),
    "EnableEnterpriseGeodatabase": (
        "installs Esri's geodatabase system tables, whose schema is not published, into an existing DBMS database "
        "reached through a database server connection, which a fenced user tool does not have (ADR-223 §5.3)"),
    "UpdateEnterpriseGeodatabaseLicense": (
        "writes an ArcGIS Server authorization (keycodes) file into an enterprise geodatabase's licence; a GeoPackage "
        "needs no licence, and Esri's keycodes are Esri's to issue"),
    "CreateDatabaseUser": (
        "creates a login and user on a DBMS server (and its schema in an enterprise geodatabase); a GeoPackage file has "
        "no users, and a fenced user tool has no database server connection (ADR-223 §5.3)"),
    "CreateRole": (
        "creates a DBMS role and grants it to users; a GeoPackage file has no users or roles to grant — who reads it "
        "is the file system's to decide"),
    "ChangePrivileges": (
        "grants and revokes a DBMS user's or role's SELECT, INSERT, UPDATE and DELETE privileges on a dataset; SQLite "
        "has no GRANT, and access to a GeoPackage is access to its file"),
    "CreateSpatialType": (
        "installs Esri's ST_Geometry type and its library into an Oracle or PostgreSQL database; a GeoPackage's "
        "geometry is the standard's binary encoding (OGC 12-128 §2.1.3) and needs no type installed"),
    "ConfigureGeodatabaseLogFileTables": (
        "sets how an enterprise geodatabase keeps the log file tables ArcGIS clients write for large selections "
        "(session, shared or temporary tables per user); grapy's selections live in its layers, and no log file "
        "table is written"),
    "ExportGeodatabaseConfigurationKeywords": (
        "writes out the configuration keywords of an enterprise geodatabase's DBTUNE table, which set DBMS storage "
        "for each dataset; a GeoPackage has no DBTUNE table and no storage keywords to export"),
    "ImportGeodatabaseConfigurationKeywords": (
        "loads a configuration keyword file into an enterprise geodatabase's DBTUNE table, to set the DBMS storage new "
        "datasets get; SQLite has no storage parameters such a file could set"),
    "DeleteSchemaGeodatabase": (
        "deletes a geodatabase held in an Oracle user's schema, with its system tables; a GeoPackage has no schemas — "
        "Delete removes a GeoPackage file"),
    "MigrateStorage": (
        "moves a dataset's geometry or raster storage from one DBMS type to another (Esri's binary, ST_Geometry, "
        "SDO_Geometry, PostGIS geometry …); a GeoPackage has one geometry storage, the standard's binary encoding"),
    "UpdatePortalDatasetOwner": (
        "changes the owner of a dataset's items in an ArcGIS Enterprise portal; it reaches a portal, and a fenced "
        "user tool has no network and no access to its portal (ADR-223 §5.3)"),
    "EnableFeatureBinning": (
        "turns on database-computed bins that ArcGIS clients and feature services draw for a point class; their bin "
        "size at each level of detail is not defined on its page or ManageFeatureBinCache's (which names the levels "
        "WORLD … CITY only), and nothing in arcpy reads a bin back, so a grid of grapy's own would have no reader"),
    "DisableFeatureBinning": (
        "turns off database-computed feature binning, the drawing behaviour of ArcGIS clients and feature services that "
        "EnableFeatureBinning sets; grapy has no binned drawing for it to switch off"),
    "ManageFeatureBinCache": (
        "builds the static cache of bin statistics at the levels WORLD … CITY, whose bin sizes and storage its page "
        "does not define; a cache only ArcGIS's binned layers read would have no reader in grapy"),
    "SetFeatureClassSplitModel": (
        "sets whether ArcGIS Pro's split edit updates the original feature and inserts the smaller piece or deletes it "
        "and inserts both; grapy has no split edit — SplitLine and SplitLineAtPoint write a new class — so the model "
        "would govern nothing"),
    "CreateTrajectoryDataset": (
        "creates a trajectory dataset, whose footprint, metadata and file-reference tables its page does not define, "
        "and which only AddDataToTrajectoryDataset's satellite trajectory types fill"),
    "AddDataToTrajectoryDataset": (
        "reads satellite altimetry products (Sentinel-3 SRAL, Sentinel-6, CryoSat-2, ICESat-2 …) through Esri's "
        "trajectory raster types, whose footprints and variable mapping their pages do not define"),
    "MakeTrajectoryLayer": (
        "makes a layer over a trajectory dataset's footprints and variables, a dataset whose tables its pages do not "
        "define (CreateTrajectoryDataset)"),
    "RepairTrajectoryDatasetPaths": (
        "rewrites the paths of the satellite files a trajectory dataset references, in tables its pages do not "
        "define (CreateTrajectoryDataset)"),
}
