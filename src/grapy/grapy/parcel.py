"""arcpy.parcel — parcel fabrics, by their documented names and parameters (ADR-250).

The model is ``grapy._parcel``: a fabric in a feature dataset of the GeoPackage, the classes it controls beside it, its
geodatabase topology (grapy's own, ADR-225, with the six system rules of *Work with the geodatabase topology*), and
two tables of grapy's that say which class is which. Building, seeding, deleting, selecting, labelling, merging and
duplicating are ``grapy._parcel_build``; the least-squares adjustment is ``grapy._parcel_lsa``; copying, appending,
exporting, importing and the checks are ``grapy._parcel_io``.

Five tools are *equivalents* (ADR-224 §5.6): AnalyzeParcelsByLeastSquaresAdjustment (a published least-squares
adjustment for Esri's DynAdjust run), ExportSequencedParcelFeatures (a clockwise walk of the boundary for a sequence the
page shows only by example), FindGapsAndOverlaps (overlay and a negative buffer for a "width" the page does not define),
ValidateParcelFabric (grapy's topology validation for Esri's cracking and clustering) and GenerateParcelFabricLinks
(the points' history from grapy's versioning or archive, ADR-227, for a feature service's). One is refused for its own
reason: UpgradeArcMapParcelFabric. A class of a fabric may be named as the pages' samples name it, under the fabric's
path (``…/Parcels/CountyFabric/Tax_Lines``), as well as by its own path (``…/Parcels/Tax_Lines``).
"""
from __future__ import annotations

from . import _parcel as pf
from ._result import Result
from ._runtime import AddMessage, ExecuteError, NotBuiltError, equivalent

# Each name not built is refused with a reason of its own (ADR-250 §5.6); never a schedule.
_REFUSED: dict[str, str] = {
    "UpgradeArcMapParcelFabric": (
        "reads an ArcMap parcel fabric, a controller dataset that only an Esri geodatabase holds (the page lists file, "
        "enterprise and mobile ones), and grapy reads none of them: its workspaces are GeoPackages, which cannot hold an "
        "ArcMap parcel fabric; a file geodatabase's table format Esri does not publish; and an enterprise geodatabase is "
        "a database server a fenced tool cannot reach (ADR-223 §5.3)"),
}


def __getattr__(name):
    if name.startswith("_"):
        raise AttributeError(name)
    reason = _REFUSED.get(name)
    message = f"arcpy.parcel.{name} {reason}." if reason else f"arcpy.parcel.{name} is not built in grapy yet."

    def missing(*_, **__):
        raise NotBuiltError(message)

    return missing


def _resolve(value):
    """A layer as it is; a path through ``_parcel.resolve`` (a class named under its fabric's path)."""
    from . import _layers
    return value if _layers.find(value) is not None else pf.resolve(value)


def _fabric(value, tool):
    return pf.locate(_resolve(value), tool)


def _input(value, tool, roles):
    """(fabric, table, features, parcel type) of a feature-layer parameter of one of the roles given."""
    value = _resolve(value)
    file, table, features = pf.read_input(value, tool)
    fab = pf.fabric_of_class(file, table)
    role = fab.role_of(table) if fab is not None else None
    if role not in roles:
        raise ExecuteError(f"ERROR 000800: {tool}: '{value}' is not a parcel fabric's "
                           f"{' or '.join(r.replace('_', ' ') for r in roles)}.")
    return fab, table, features, fab.type_of(table)


def _multi(value):
    from .management import _list
    return [value] if not isinstance(value, (list, tuple)) and ";" not in pf._text(value) else _list(value)


# ---- creating a fabric and its parcel types ----------------------------------------------------------------------


def CreateParcelFabric(target_dataset, name, config_keyword=None):
    """A parcel fabric in a feature dataset — one per dataset, in the dataset's spatial reference — with its Records,
    Points and Connections classes, its three adjustment classes, its domains and its geodatabase topology
    ``<name>_Topology``, as documented; each class has global IDs and editor tracking, and the lines are COGO
    enabled (ADR-240). The topology starts disabled, so parcel types can be added (AddParcelType needs it disabled):
    EnableParcelTopology turns it on (INFERRED, §5.8 (d)). ``config_keyword`` is for an enterprise geodatabase's
    storage parameters and is refused when given."""
    from . import _gpkg
    from .management import _feature_dataset_of
    if pf.given(config_keyword):
        raise NotBuiltError("arcpy.parcel.CreateParcelFabric: config_keyword is not built in grapy: it names an "
                            "enterprise geodatabase's storage parameters (DBTUNE), which a GeoPackage has none of.")
    file, dataset = _gpkg.split(pf._text(target_dataset))
    if not dataset or "/" in dataset or _feature_dataset_of(f"{file}/{dataset}/x") is None:
        raise ExecuteError("ERROR 000732: CreateParcelFabric: a parcel fabric is created in a feature dataset "
                           "(CreateFeatureDataset).")
    name = str(name).strip()
    if not name or "/" in name:
        raise ExecuteError(f"ERROR 000800: CreateParcelFabric: '{name}' is not a parcel fabric name.")
    fab = pf.create(file, dataset, name)
    return Result(fab.path)


def AddParcelType(in_parcel_fabric, name, administrative_polygon="TOPOLOGY_POLYGON", strata_parcels="NOT_STRATA_PARCELS"):
    """A parcel type: the polygon class ``<name>`` and the line class ``<name>_Lines`` (INFERRED from the pages'
    samples, §5.8 (a)) with the documented system fields, registered with the fabric; a STRATA_PARCELS type adds
    ``FloorOrder`` to both, and an ADMINISTRATIVE_POLYGON type's polygons stay out of the topology. The fabric's
    topology must be disabled, as documented. Outputs the fabric, the polygon class and the line class."""
    fab = _fabric(in_parcel_fabric, "AddParcelType")[0]
    administrative = pf.word(administrative_polygon, "TOPOLOGY_POLYGON", ("ADMINISTRATIVE_POLYGON", "TOPOLOGY_POLYGON"),
                             "AddParcelType", "administrative_polygon") == "ADMINISTRATIVE_POLYGON"
    strata = pf.word(strata_parcels, "NOT_STRATA_PARCELS", ("STRATA_PARCELS", "NOT_STRATA_PARCELS"), "AddParcelType",
                     "strata_parcels") == "STRATA_PARCELS"
    polygons, lines = pf.add_type(fab, name, administrative, strata)
    return Result(fab.path, fab.full(polygons), fab.full(lines))


def RemoveParcelType(in_parcel_fabric, name):
    """A parcel type no longer controlled by the fabric: its classes leave the fabric and its topology, and stay in the
    GeoPackage, where they can be deleted, as documented. The topology must be disabled."""
    fab = _fabric(in_parcel_fabric, "RemoveParcelType")[0]
    polygons, lines = pf.remove_type(fab, name)
    return Result(fab.path, fab.full(polygons), fab.full(lines))


def EnableParcelTopology(in_parcel_fabric):
    """The fabric's classes — but an administrative type's polygons — and the six system rules added to its topology:
    each topology type's lines must have their endpoints covered by the Points, must not have dangles, self-overlap
    or self-intersect, must be covered by the boundary of the type's polygons, whose boundary must be covered by the
    lines. Connection lines take the first rule only (INFERRED, §5.8 (e))."""
    fab = _fabric(in_parcel_fabric, "EnableParcelTopology")[0]
    pf.enable_topology(fab)
    return Result(fab.path)


def DisableParcelTopology(in_parcel_fabric):
    """The system rules and the fabric's classes removed from the topology; rules a user added stay, as documented,
    and with them the classes they name (INFERRED, §5.8 (e))."""
    fab = _fabric(in_parcel_fabric, "DisableParcelTopology")[0]
    pf.disable_topology(fab)
    return Result(fab.path)


# ---- records, seeds and building -------------------------------------------------------------------------------------


def CreateParcelRecords(in_parcel_features, record_field=None, record_expression=None, record_name_method="FIELD"):
    """Records made from the record names of parcel polygons, parcel lines or connection lines: a text field's values
    (FIELD, the default) or an Arcade expression on ``$feature`` (EXPRESSION, grapy's Arcade, ADR-235). A record is
    made once per name — an existing one is not duplicated, names compared without case (INFERRED, §5.8 (g)) — a
    blank name gives none; each feature's Created By Record is set to its record's global ID; then every record's
    polygon and parcel counts are refreshed from all parcel types, as documented. Outputs the Records class and the
    fabric."""
    from ._arcade import ArcadeError, evaluate
    from ._arcade._values import Dictionary, Feature
    tool = "CreateParcelRecords"
    method = pf.word(record_name_method, "FIELD", ("FIELD", "EXPRESSION"), tool, "record_name_method")
    fab, table, features, _ = _input(in_parcel_features, tool, ("polygons", "lines", "connections"))
    if method == "FIELD":
        if not pf.given(record_field):
            raise ExecuteError(f"ERROR 000735: {tool}: the FIELD method needs a record field.")
        column = next((c for c in pf.columns_of(fab.file, table) if c.name.lower() == str(record_field).lower()), None)
        if column is None:
            raise ExecuteError(f"ERROR 000728: {tool}: the field '{record_field}' does not exist.")
        if column.type != "String":
            raise ExecuteError(f"ERROR 000800: {tool}: the record field must be a text field.")
        names = [f.get(column.name) for f in features]
    else:
        if not pf.given(record_expression):
            raise ExecuteError(f"ERROR 000735: {tool}: the EXPRESSION method needs a record expression.")
        names = []
        for f in features:
            record = Dictionary()
            for key, v in f.items():
                record.set(key, v)
            try:
                value = evaluate(str(record_expression), {"$feature": Feature(record, None, immutable=True)})
            except ArcadeError as error:
                raise ExecuteError(f"ERROR 002717: {tool}: the Arcade expression failed: {error}") from None
            names.append(None if value is None else str(value))
    from . import _parcel_build
    _parcel_build.create_records(fab, table, features, names)
    return Result(fab.full(fab.records), fab.path)


def CreateParcelSeeds(in_parcel_fabric, record_name):
    """A seed in every closed loop of current lines of the record (named without regard to case, as documented), in
    every parcel type; no seed where a parcel or seed is already there, nor in a loop of other records' or other
    types' lines; a strata type's loops by floor (FloorOrder). A seed is a minimized polygon inside the loop
    (INFERRED, §5.8 (i)), its record the given one."""
    from . import _parcel_build
    fab = _fabric(in_parcel_fabric, "CreateParcelSeeds")[0]
    _parcel_build.create_seeds(fab, record_name)
    return Result(fab.path)


def BuildParcelFabric(in_parcel_fabric, extent=None, record_name=None):
    """Parcels built, as documented: a seed's loop of lines becomes its parcel; a parcel polygon gets a two-point line
    for each of its boundary's segments — collinear vertices give one line, adjacent parcels share one line, which
    takes the record with the latest Recorded Date, then the latest created record; an existing line is not made
    again, and COGO is not filled; a missing point is made at each line's end; each record's COGO Accuracy goes to
    its lines (where the fields exist); record polygons and counts are refreshed. Only current parcels; with an extent
    and a record name, only parcels in both. The topology must be enabled. INFERRED (§5.8 (j)): a neighbour's corner,
    an existing point or line end on a polygon's edge splits the line there; an administrative type is built too."""
    from . import _parcel_build
    tool = "BuildParcelFabric"
    fab = _fabric(in_parcel_fabric, tool)[0]
    pf.require_topology(fab, True, tool)
    box = pf.extent_box(extent, tool, fab)
    guid = None
    if pf.given(record_name):
        record = pf.record_named(fab, record_name)
        if record is None:
            raise ExecuteError(f"ERROR 000800: {tool}: there is no record '{record_name}'.")
        guid = record.get_ci("GlobalID")
    _parcel_build.build(fab, box, guid)
    return Result(fab.path)


def DeleteParcels(in_parcel_polygon_features):
    """The parcels deleted (a layer's selection, or every parcel of the class), and with them their lines that no
    other parcel shares, and their points that no other parcel or line uses — current or historic — that carry no
    value but the system fields and have Preserve and Is Fixed not Yes; record polygons are refreshed. No historic
    parcels are made, as documented."""
    from . import _parcel_build
    fab, table, features, parcel_type = _input(in_parcel_polygon_features, "DeleteParcels", ("polygons",))
    _parcel_build.delete_parcels(fab, parcel_type, features)
    return Result(fab.path)


def DuplicateParcels(in_parcel_polygon_features, record_name, duplicate_to_parcel_type, repeat=None, update_field=None,
                     start=None, increment=None):
    """Parcels copied ``repeat`` times into a parcel type, assigned to the record (made when there is none): into the
    same type the polygons only, into another type or a strata type the lines too, as documented; fields of the same
    name and type are copied (help topic *Duplicate parcels*); ``update_field`` takes ``start`` and then each next
    copy ``increment`` more — FloorOrder when the type is strata and none is named. INFERRED (§5.8 (o)): repeat, start
    and increment default to 1."""
    from . import _parcel_build
    tool = "DuplicateParcels"
    fab, table, features, source_type = _input(in_parcel_polygon_features, tool, ("polygons",))
    target = fab.type_named(duplicate_to_parcel_type, tool)
    count = int(repeat) if pf.given(repeat) else 1
    first = int(start) if pf.given(start) else 1
    step = int(increment) if pf.given(increment) else 1
    if count < 1:
        raise ExecuteError(f"ERROR 000800: {tool}: repeat is at least 1.")
    field = None
    if pf.given(update_field):
        column = next((c for c in pf.columns_of(fab.file, target.polygons)
                       if c.name.lower() == str(update_field).lower()), None)
        if column is None or column.type not in ("Integer", "SmallInteger", "BigInteger"):
            raise ExecuteError(f"ERROR 000800: {tool}: the update field must be a long integer field of "
                               f"'{target.name}'.")
        field = column.name
    elif target.strata:
        field = "FloorOrder"
    if not pf.given(record_name):
        raise ExecuteError(f"ERROR 000735: {tool}: a record name is required.")
    guid = pf.ensure_record(fab, record_name)
    _parcel_build.duplicate(fab, source_type, features, guid, target, count, field, first, step)
    return Result(fab.full(target.polygons), fab.full(target.lines), fab.full(fab.records))


# ---- selecting, labelling, merging ----------------------------------------------------------------------------------


def _layer_over(path, oids):
    """A feature layer over a class with the given selection, named after the class (``_1``, ``_2`` … when that name
    is a layer over something else; INFERRED, §5.8 (k))."""
    from . import _layers, management
    short = path.replace("\\", "/").rsplit("/", 1)[-1]
    name, i = short, 0
    while name in _layers.LAYERS and _layers.LAYERS[name].source != path:
        i += 1
        name = f"{short}_{i}"
    if name not in _layers.LAYERS:
        management.MakeFeatureLayer(path, name)
    _layers.LAYERS[name].selection = set(oids)
    return name


def SelectParcelFeatures(in_parcel_polygon_features):
    """The lines and points of the parcels selected (or of every parcel of the class): the lines of the parcel type on
    their boundaries and the points at those lines' ends and inflections; a historic parcel's historic lines and
    points, its current ones where there are none, as documented. Outputs a lines layer and a points layer with those
    features selected."""
    from . import _parcel_build
    fab, table, features, parcel_type = _input(in_parcel_polygon_features, "SelectParcelFeatures", ("polygons",))
    lines, points = _parcel_build.select_parcel_features(fab, parcel_type, features)
    AddMessage(f"{len(lines)} lines and {len(points)} points selected.")
    return Result(_layer_over(fab.full(parcel_type.lines), lines), _layer_over(fab.full(fab.points), points))


def SetParcelLineLabelPosition(in_parcel_features):
    """Each current parcel line's Label Position: Right where its parcel lies to the right of the line's direction,
    Left where it lies to the left, Centered where parcels lie on both sides — whatever the COGO dimensions say, as
    documented. Historic lines and connection lines are left as they are; a line on no parcel too (INFERRED,
    §5.8 (k))."""
    from . import _parcel_build
    outputs = []
    for value in _multi(in_parcel_features):
        fab, table, features, parcel_type = _input(value, "SetParcelLineLabelPosition", ("lines", "connections"))
        if parcel_type is not None:
            _parcel_build.label_positions(fab, parcel_type, features)
        outputs.append(pf._text(value))
    return Result(";".join(outputs))


def MergeCollinearParcelBoundaries(in_parcel_boundaries, offset_tolerance):
    """Connected collinear parcel lines (or connection lines) merged into one, as documented: two lines meeting at a
    point no other line meets, the point within the offset tolerance of the straight line — or of the curve of their
    radius — between their outer ends. The merged shape keeps the vertex; Distance and Arc Length are summed;
    Direction is the longest line's when the two agree within rounding, else null; COGO Type becomes Computed; the
    shared point is deleted unless Preserve is Yes, and the originals are deleted. INFERRED (§5.8 (l)): rounding is
    one second of arc; lines of different records are not merged; the merged line keeps the longer line's other
    fields; a positive radius curves clockwise."""
    from . import _parcel_build
    fab, table, features, _ = _input(in_parcel_boundaries, "MergeCollinearParcelBoundaries", ("lines", "connections"))
    tolerance = pf.linear(offset_tolerance, fab.srid)
    if tolerance is None or tolerance < 0:
        raise ExecuteError("ERROR 000735: MergeCollinearParcelBoundaries: an offset tolerance is required.")
    merges = _parcel_build.merge_collinear(fab, table, features, tolerance)
    AddMessage(f"{merges} merges.")
    return Result(pf._text(in_parcel_boundaries))


# ---- least squares ------------------------------------------------------------------------------------------------


@equivalent("parametric least-squares adjustment of distances and direction sets, iterated to convergence; free "
            "network by inner constraints; error ellipses and standardized residuals",
            "Ghilani and Wolf, Adjustment Computations, 5th ed. (2010), ch. 10-21; DynAdjust user guide "
            "(Geoscience Australia)")
def AnalyzeParcelsByLeastSquaresAdjustment(in_parcel_fabric, analysis_type, convergence_tolerance=None):
    """EQUIVALENT: a parametric least-squares adjustment of the 2D network the lines measure, as Ghilani and Wolf,
    *Adjustment Computations* (5th ed., 2010) publish it, for the DynAdjust run the pages describe. Each line with a
    distance (or a curve's chord from its radius and arc length) is a distance; at each point the directions of the
    lines of one record are a direction set with its own orientation (two lines at least); defaults 0.15 m and 30
    seconds; ground dimensions go to the grid by Scale and Rotation. CONSISTENCY_CHECK solves the free network by
    inner constraints; WEIGHTED_LEAST_SQUARES adds fixed points (Is Fixed, or XYZ constrained) at 5 mm and weighted
    points (XY Accuracy) as coordinate observations, and needs two. Iterations stop when no coordinate moves more than
    the tolerance (default 0.05 m). The three adjustment classes are rewritten; the message gives the standard error.

    The input is the whole fabric, or a layer over a parcel or line class with a selection: selected parcels give
    their lines, selected lines themselves, connection lines only when selected, current and historic alike. What
    differs from the pages, by choice (§5.8 (p)): weighted points are grid coordinates, not geodetic latitude and
    longitude, and are not written to AdjustmentLines; AzimuthType 2–5 is refused; the Standardized Normal Statistic is
    the correction over its own standard deviation (the published w-test) where the page's sentence divides by the
    adjusted measurement's; Student's t is that over the a-posteriori sigma; Pelzer's reliability the measurement's
    standard deviation over the correction's; XY Uncertainty 2.4477 times the semi-major axis."""
    from . import _parcel_build, _parcel_lsa
    tool = "AnalyzeParcelsByLeastSquaresAdjustment"
    kind = pf.word(analysis_type, None, ("CONSISTENCY_CHECK", "WEIGHTED_LEAST_SQUARES"), tool, "analysis_type")
    fab, layer, table = _fabric(in_parcel_fabric, tool)
    tolerance = pf.linear(convergence_tolerance, fab.srid, "0.05 Meters")
    chosen = pf.selection_of(layer.name) if layer is not None else None
    lines = []
    if chosen is None:
        for line_table in fab.line_classes():
            lines.extend((line_table, ln) for ln in pf.read(fab.file, line_table) if ln.shape is not None)
    else:
        role = fab.role_of(table)
        if role == "polygons":
            parcel_type = fab.type_of(table)
            all_lines = pf.read(fab.file, parcel_type.lines)
            picked = {}
            for polygon in pf.read(fab.file, table):
                if polygon.oid in chosen and polygon.shape is not None:
                    on = [ln for ln in all_lines
                          if _parcel_build._covered_by(ln, polygon.shape.boundary, fab.tolerance)]
                    picked.update((ln.oid, ln) for ln in on)
            lines = [(parcel_type.lines, ln) for ln in picked.values()]
        elif role in ("lines", "connections"):
            lines = [(table, ln) for ln in pf.read(fab.file, table) if ln.oid in chosen and ln.shape is not None]
        else:
            raise ExecuteError(f"ERROR 000800: {tool}: select parcels or lines of the fabric.")
    _parcel_lsa.analyze(fab, lines, kind, tolerance)
    return Result(fab.path, fab.full(fab.roles["adjustment_points"]), fab.full(fab.roles["adjustment_lines"]),
                  fab.full(fab.roles["adjustment_vectors"]))


def ApplyParcelLeastSquaresAdjustment(in_parcel_fabric, movement_tolerance=None, update_attributes="NO_UPDATE_ATTRIBUTES"):
    """The last adjustment applied, as documented: each AdjustmentPoints row finds its point by Point ID, and the point
    moves to the adjusted coordinates when they are farther than the movement tolerance (default 0.05 m) — not when
    Update Source is No, nor when the point Is Fixed; a constrained point not at its X and Y moves to them; the lines
    AdjustmentLines names (Line ID, Source) are reshaped between their moved ends, a polyline by the similarity
    transformation of its ends, their COGO dimensions unchanged; polygons follow their moved vertices.
    UPDATE_ATTRIBUTES copies XY Uncertainty and the error ellipse to the points. Selections are not honoured, as
    documented."""
    from . import _parcel_lsa
    tool = "ApplyParcelLeastSquaresAdjustment"
    fab = _fabric(in_parcel_fabric, tool)[0]
    tolerance = pf.linear(movement_tolerance, fab.srid, "0.05 Meters")
    update = pf.word(update_attributes, "NO_UPDATE_ATTRIBUTES", ("UPDATE_ATTRIBUTES", "NO_UPDATE_ATTRIBUTES"), tool,
                     "update_attributes") == "UPDATE_ATTRIBUTES"
    _parcel_lsa.apply(fab, tolerance, update)
    return Result(fab.path)


# ---- copying and appending ----------------------------------------------------------------------------------------


def CopyParcels(in_parcel_fabric, target_database, out_dataset_name=None, out_fabric_name=None):
    """A new feature dataset in the target GeoPackage holding a new parcel fabric of the same schema — its parcel
    types, every field, subtypes, domains, user topology rules and attribute rules (but those that read a class outside
    the fabric, as documented) — and the parcels: all, or a layer's selected parcels with their lines, points,
    connection lines and records; attachments too. Global IDs are kept, so records stay linked (INFERRED, §5.8 (q));
    names default to the input's dataset and fabric names. A fabric class in a relationship class with another table is
    refused by name."""
    from . import _gpkg, _parcel_io
    tool = "CopyParcels"
    fab, layer, table = _fabric(in_parcel_fabric, tool)
    parcels = _parcel_io.Parcels(fab, layer, table, tool)
    text = pf._text(target_database)
    file, inner = _gpkg.split(text if ".gpkg" in text.lower() else text + ".gpkg")
    if inner:
        raise ExecuteError(f"ERROR 000732: {tool}: the target database is a GeoPackage, not '{target_database}'.")
    import os
    if not os.path.exists(file):
        raise ExecuteError(f"ERROR 000732: {tool}: '{file}' does not exist.")
    dataset = str(out_dataset_name).strip() if pf.given(out_dataset_name) else fab.dataset
    name = str(out_fabric_name).strip() if pf.given(out_fabric_name) else fab.name
    _parcel_io.copy_parcels(parcels, file, dataset, name)
    given_path = text.rstrip("/\\") if ".gpkg" in text.lower() else file
    return Result(f"{given_path}/{dataset}", f"{given_path}/{dataset}/{name}")


def AppendParcels(in_parcel_fabric, target_parcel_fabric, replace_point_method="KEEP_BOTH"):
    """Parcels appended to another fabric, as documented: the parcel types must match; fields the target lacks are
    dropped; records are matched by name (INFERRED, §5.8 (q)) and updated, not duplicated; a layer's selected
    parcels bring their lines, points, records and the connection lines meeting their points; polygons are appended
    even without lines or points. Coincident points (within the target's xy tolerance; INFERRED): KEEP_BOTH keeps
    both, KEEP_SOURCE updates the target's point with the source's values, KEEP_TARGET keeps the target's."""
    from . import _parcel_io
    tool = "AppendParcels"
    method = pf.word(replace_point_method, "KEEP_BOTH", ("KEEP_BOTH", "KEEP_SOURCE", "KEEP_TARGET"), tool,
                     "replace_point_method")
    fab, layer, table = _fabric(in_parcel_fabric, tool)
    target = _fabric(target_parcel_fabric, tool)[0]
    if target.path == fab.path:
        raise ExecuteError(f"ERROR 000800: {tool}: the input and target parcel fabrics are the same.")
    parcels = _parcel_io.Parcels(fab, layer, table, tool)
    _parcel_io.append_parcels(parcels, target, method)
    return Result(target.path)


# ---- exports and imports --------------------------------------------------------------------------------------------


def ExportParcelRecordFeatures(in_record_polygon_feature, target_feature_dataset):
    """The features one record created or retired, written to a feature dataset: per parcel type a polygon and a
    line class, a connection line, a point and a records class, each made even when empty, reused when there, and
    emptied first on every run, as documented; each feature's ``ExportedStatus`` says Created By Record, Retired By
    Record or both (domain ``ExportStatus``). The classes take the fabric's class names (INFERRED, §5.8 (m)). Exactly
    one record must be selected."""
    from . import _parcel_io
    tool = "ExportParcelRecordFeatures"
    fab, table, features, _ = _input(in_record_polygon_feature, tool, ("records",))
    if len(features) != 1:
        raise ExecuteError(f"ERROR 000800: {tool}: select exactly one record; {len(features)} are given.")
    target = _parcel_io._target_dataset(target_feature_dataset, tool)
    return Result(_parcel_io.export_record(fab, features[0], target))


@equivalent("clockwise walk of the parcel's exterior ring from the boundary point nearest the chosen corner of its "
            "envelope", "ADR-250 §5.4")
def ExportSequencedParcelFeatures(in_parcel_polygon_feature, target_feature_dataset, start_sequence_corner="NE",
                                  points_sequence_format="#", lines_sequence_format="#",
                                  sequence_curves_separately="SEQUENCE_TOGETHER"):
    """EQUIVALENT: one selected parcel exported with its lines, points, connection lines and record to a feature
    dataset, the lines and points numbered clockwise in ``Sequence`` from the corner chosen. The page says the sequence
    "starts with the line that is closest to the quadrant specified" and shows its formats only by example; grapy
    walks the parcel's exterior ring clockwise from the line end (point) nearest that corner of the parcel's envelope,
    orders lines by their middles and points by their places along it, and formats a run of ``#`` (and digits after
    it) as the number zero-padded to the run's width (``ABC-##1`` → ``ABC-001``). Curves (a radius) have IsCurve 1
    and, with SEQUENCE_SEPARATELY, their own numbering from 1. The classes take the fabric's names (§5.8 (n))."""
    from . import _parcel_io
    tool = "ExportSequencedParcelFeatures"
    fab, table, features, parcel_type = _input(in_parcel_polygon_feature, tool, ("polygons",))
    if len(features) != 1 or features[0].shape is None:
        raise ExecuteError(f"ERROR 000800: {tool}: select exactly one parcel; {len(features)} are given.")
    corner = pf.word(start_sequence_corner, "NE", ("NE", "SE", "SW", "NW"), tool, "start_sequence_corner")
    separately = pf.word(sequence_curves_separately, "SEQUENCE_TOGETHER", ("SEQUENCE_SEPARATELY", "SEQUENCE_TOGETHER"),
                         tool, "sequence_curves_separately") == "SEQUENCE_SEPARATELY"
    target = _parcel_io._target_dataset(target_feature_dataset, tool)
    outputs = _parcel_io.export_sequenced(fab, parcel_type, features[0], target, corner, points_sequence_format,
                                          lines_sequence_format, separately)
    return Result(*outputs)


def ImportParcelFabricPoints(source_points, target_parcel_fabric, match_point_method="PROXIMITY", search_distance=None,
                             update_type="ALL", record_name=None, match_field=None, conflicts_table=None,
                             update_create_option="UPDATE_AND_CREATE", target_points=None, where_clause=None):
    """Points imported into a fabric, as documented: each source point (a layer's selection, or all) is matched to the
    fabric points within the search distance — by proximity, or also by name or global ID in ``match_field`` — and
    ALL updates a match's shape and the fields both share, GEOMETRY_XYZ only its shape (and Z when the source has z),
    RETIRE_AND_REPLACE retires it by the record and creates a new point; an unmatched point is created, under the
    record (made when there is none); UPDATE_ONLY and CREATE_ONLY restrict this. An Is Fixed point is never updated;
    nothing is deleted; lines and polygons follow moved points; the source is projected to the fabric's reference by
    grapy's own projection (pyproj's default transformation). More than one match is a warning and a conflicts-table
    row (``SourceOID``, ``TargetOID``) and the source point is not imported. ``target_points`` and ``where_clause``
    filter the fabric points matched. INFERRED (§5.8 (r)): the search distance defaults to the fabric's xy tolerance;
    the conflict rule; the conflicts table's fields."""
    from . import _parcel_io
    tool = "ImportParcelFabricPoints"
    fab = _fabric(target_parcel_fabric, tool)[0]
    method = pf.word(match_point_method, "PROXIMITY", ("PROXIMITY", "NAME_AND_PROXIMITY", "GLOBALID_AND_PROXIMITY"),
                     tool, "match_point_method")
    kind = pf.word(update_type, "ALL", ("ALL", "GEOMETRY_XYZ", "RETIRE_AND_REPLACE"), tool, "update_type")
    option = pf.word(update_create_option, "UPDATE_AND_CREATE", ("UPDATE_AND_CREATE", "CREATE_ONLY", "UPDATE_ONLY"),
                     tool, "update_create_option")
    search = pf.linear(search_distance, fab.srid) if pf.given(search_distance) else fab.tolerance
    where = None
    if pf.given(target_points):
        file, table, _ = pf.read_input(_resolve(target_points), tool)
        if table != fab.points:
            raise ExecuteError(f"ERROR 000800: {tool}: target_points must be the target fabric's points.")
        from .management import _source
        where = _source(_resolve(target_points))[1]
    if pf.given(where_clause):
        where = f"({where}) and ({where_clause})" if where else str(where_clause)
    _parcel_io.import_points(fab, source_points, method, search, kind, record_name, match_field, conflicts_table,
                             option, where)
    return Result(fab.path)


# ---- checks ----------------------------------------------------------------------------------------------------------


def FindAdjacentParcelPoints(in_parcel_points, out_feature_class, tolerance):
    """Every point (of a layer's selection, or all) that lies within the tolerance of another — at 0, the stacked
    ones — copied with its fields to the output; the count is a message, as documented. The symbology the page names
    is a map's. INFERRED (§5.8 (r)): "closer than" includes the tolerance itself, so 0 finds coincident points."""
    from . import _parcel_io
    value = _resolve(in_parcel_points)
    file, table, _ = pf.read_input(value, "FindAdjacentParcelPoints")
    fab = pf.fabric_of_class(file, table)
    srid = fab.srid if fab is not None else 0
    distance = pf.linear(tolerance, srid, "0 Meters")
    path, _ = _parcel_io.adjacent_points(value, pf._text(out_feature_class), distance)
    return Result(path)


def FindDisconnectedParcelPoints(in_parcel_points, out_feature_class, ignore_fixed="IGNORE_FIXED",
                                 ignore_constrained="IGNORE_CONSTRAINED", consider_edge="NO_CONSIDER_EDGE"):
    """The fabric points at no end of any parcel line or connection line — historic lines count, so a point only on
    historic lines is not disconnected — copied to the output, the count a message, as documented. IGNORE_FIXED and
    IGNORE_CONSTRAINED (the defaults) leave out Is Fixed and XYZ constrained points; a point on a polygon's edge or a
    line's interior, which splits neither, is reported only with CONSIDER_EDGE."""
    from . import _parcel_io
    tool = "FindDisconnectedParcelPoints"
    fixed = pf.word(ignore_fixed, "IGNORE_FIXED", ("IGNORE_FIXED", "NO_IGNORE_FIXED"), tool, "ignore_fixed")
    constrained = pf.word(ignore_constrained, "IGNORE_CONSTRAINED", ("IGNORE_CONSTRAINED", "NO_IGNORE_CONSTRAINED"),
                          tool, "ignore_constrained")
    edge = pf.word(consider_edge, "NO_CONSIDER_EDGE", ("CONSIDER_EDGE", "NO_CONSIDER_EDGE"), tool, "consider_edge")
    path, _ = _parcel_io.disconnected_points(_resolve(in_parcel_points), pf._text(out_feature_class),
                                             fixed == "IGNORE_FIXED", constrained == "IGNORE_CONSTRAINED",
                                             edge == "CONSIDER_EDGE")
    return Result(path)


@equivalent("overlaps as pairwise intersections and gaps as holes of the union, each kept when a negative buffer of "
            "half the maximum width empties it (morphological opening)", "OGC Simple Features 1.2.1 (06-103r4) §6.1.15")
def FindGapsAndOverlaps(in_parcel_features, out_feature_class, maximum_width=None, detection_type="WITHIN_LAYER"):
    """EQUIVALENT: the page does not define a gap's or an overlap's "width". grapy takes an overlap as the
    intersection of two current parcels and a gap as a hole in the union of the parcels, and keeps each that a negative
    buffer of half the maximum width (default 2 m) empties — narrower than the width (OGC Simple Features buffer and
    intersection). WITHIN_LAYER looks inside each input; BETWEEN_LAYERS also between them, filling ParcelInput1 and
    ParcelInput2 with the inputs' names. Each output polygon's ``Type`` is Gap or Overlap (INFERRED, §5.8 (s): the
    page shows the kind only by symbology)."""
    from . import _parcel_io
    tool = "FindGapsAndOverlaps"
    inputs = [_resolve(v) for v in _multi(in_parcel_features)]
    if not inputs:
        raise ExecuteError(f"ERROR 000735: {tool}: give at least one parcel polygon layer.")
    file, table, _ = pf.read_input(inputs[0], tool)
    fab = pf.fabric_of_class(file, table)
    width = pf.linear(maximum_width, fab.srid if fab else 0, "2 Meters")
    between = pf.word(detection_type, "WITHIN_LAYER", ("BETWEEN_LAYERS", "WITHIN_LAYER"), tool,
                      "detection_type") == "BETWEEN_LAYERS"
    path, _ = _parcel_io.gaps_and_overlaps(inputs, pf._text(out_feature_class), width, between)
    return Result(path)


@equivalent("vertices within the cluster tolerance snap to the best-ranked class's vertex; the rules evaluated with "
            "OGC Simple Features predicates", "ADR-225 §5.3")
def ValidateParcelFabric(in_parcel_fabric, extent=None):
    """EQUIVALENT: the fabric's topology validated — system and user rules — as ValidateTopology validates a topology
    (ADR-225 §5.3), Esri's cracking and clustering being described only in words; the errors replace the last
    validation's. With an extent, only the features meeting it are clustered and evaluated, and only the errors meeting
    it are replaced. The page marks the tool deprecated in favour of Validate Topology."""
    from . import _parcel_io
    tool = "ValidateParcelFabric"
    fab = _fabric(in_parcel_fabric, tool)[0]
    _parcel_io.validate(fab, pf.extent_box(extent, tool, fab))
    return Result(fab.path)


@equivalent("each point's location at the From and To dates read from its class's archive or branch-version history",
            "ADR-227")
def GenerateParcelFabricLinks(target_parcel_fabric, out_links_feature_class, out_anchor_points_feature_class, from_date,
                              to_date=None, min_link_length=None, extent=None):
    """EQUIVALENT: displacement links from each point's original location — where it was at ``from_date`` — to where it
    is at ``to_date`` (or now, when none is given or it lies ahead), and anchor points at the original locations of
    the points that did not move, or moved less than ``min_link_length``, as documented. The page reads the original
    locations from a parcel fabric published as a feature service, on its DEFAULT version; grapy reads them from the
    history ADR-227 keeps for the fabric's points, DEFAULT's — so the points must be branch versioned
    (RegisterAsVersioned) or archived (EnableArchiving) before the edits. Only points whose original location falls
    in the extent are processed. INFERRED (§5.8 (u)): dates are UTC; each output carries ``PointID``, the point's
    global ID; a point made after ``from_date`` has no original location and gives neither."""
    from . import _parcel_io
    tool = "GenerateParcelFabricLinks"
    fab = _fabric(target_parcel_fabric, tool)[0]
    start = _parcel_io._when(from_date, tool, "from_date")
    end = _parcel_io._when(to_date, tool, "to_date") if pf.given(to_date) else None
    minimum = pf.linear(min_link_length, fab.srid, "0 Meters")
    box = pf.extent_box(extent, tool, fab)
    outputs = _parcel_io.links(fab, pf._text(out_links_feature_class), pf._text(out_anchor_points_feature_class), start,
                               end, minimum, box)
    return Result(*outputs)
