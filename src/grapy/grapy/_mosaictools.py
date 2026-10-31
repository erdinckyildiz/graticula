"""The mosaic dataset tools of arcpy.management, over grapy's mosaic dataset (``_mosaic``) — ADR-230.

Each is built to its reference page (doc.esri.com, which pro.arcgis.com's tool pages redirect to, read on 2026-10-30);
what the page leaves open is labelled INFERRED in the tool's docstring, and what grapy cannot do is refused by name.
``arcpy.management`` answers these functions under their own names.
"""
from __future__ import annotations

import fnmatch
import json
import math
import os
import re

import numpy as np
import shapely

from . import _gpkg, _mosaic
from ._mosaic import given as _given
from ._result import Result
from ._runtime import AddWarning, ExecuteError, NotBuiltError

#: The raster file extensions AddRastersToMosaicDataset picks up in a folder (those grapy's Raster reads).
RASTER_EXTENSIONS = (".tif", ".tiff", ".img", ".jp2", ".png", ".jpg", ".jpeg", ".bmp", ".gif")


def _refuse(tool, parameter, value, why=""):
    raise NotBuiltError(f"arcpy.management.{tool}: {parameter} '{value}' is not built in grapy{': ' + why if why else ''}.")


def _word(value, default):
    return str(value).upper() if _given(value) else default


def _list(value):
    from .management import _list as listed
    return listed(value)


def _close(connection):
    connection.commit()
    connection.close()


def _out_path(value):
    from ._mosaic import locate
    file, name, _ = locate(value)
    return file, name


# ---- making a mosaic dataset


def CreateMosaicDataset(in_workspace, in_mosaicdataset_name, coordinate_system, num_bands=None, pixel_type=None,
                        product_definition="NONE", product_band_definitions=None):
    """A new, empty mosaic dataset in a geodatabase — grapy's GeoPackage workspace (CreateFileGDB makes one) — in the
    coordinate system every item is held in, with the band count and pixel type its items must have; without them,
    the first raster added sets both, as the page says of the pixel type (INFERRED for the band count).

    Refused: a folder or other workspace (the page: "The mosaic dataset must be created in a geodatabase"); the 1, 2
    and 4 bit pixel types, which numpy has no cell type for; a product definition other than NONE, which orders bands
    by a sensor's wavelengths for display through a raster type grapy does not have."""
    from ._reference import reference_of
    workspace = _mosaic.text(in_workspace)
    if not workspace.lower().endswith(".gpkg"):
        raise ExecuteError(f"ERROR 000732: {workspace}: the mosaic dataset must be created in a geodatabase "
                           "(grapy's is a GeoPackage, made by CreateFileGDB).")
    if not os.path.isfile(workspace):
        raise ExecuteError(f"ERROR 000732: Output Location: Dataset {workspace} does not exist or is not supported.")
    kind = str(pixel_type).upper() if _given(pixel_type) else None
    if kind is not None and kind not in _mosaic.PIXEL_TYPES:
        _refuse("CreateMosaicDataset", "pixel_type", pixel_type, "numpy has no such cell type")
    if _word(product_definition, "NONE") != "NONE" or _given(product_band_definitions):
        _refuse("CreateMosaicDataset", "product_definition", product_definition,
                "it orders bands by a sensor's wavelengths for display, through raster types grapy does not have "
                "(ADR-230 §5.3)")
    reference = reference_of(coordinate_system) if _given(coordinate_system) else None
    srid = getattr(reference, "factoryCode", 0) or 0
    name = _mosaic.text(in_mosaicdataset_name)
    _mosaic.create(workspace, name, srid, int(num_bands) if _given(num_bands) else None, kind)
    return Result(f"{workspace}/{name}")


def DeleteMosaicDataset(in_mosaic_dataset, delete_overview_images="DELETE_OVERVIEW_IMAGES",
                        delete_item_cache="DELETE_ITEM_CACHE"):
    """The mosaic dataset deleted: its catalog and grapy's rows for it. Its items' raster files are kept (INFERRED:
    the page names the overviews and item cache as what goes with it, and grapy's mosaic datasets have neither). The
    output is ``true``."""
    connection, name, _, _ = _mosaic.open_mosaic(in_mosaic_dataset, "DeleteMosaicDataset")
    try:
        _mosaic.drop(connection, name)
        connection.execute(f'drop table if exists "{name}"')
        _gpkg.forget(connection, name)
    finally:
        _close(connection)
    return Result("true")


def CreateReferencedMosaicDataset(in_dataset, out_mosaic_dataset, coordinate_system=None, number_of_bands=None,
                                  pixel_type=None, where_clause=None, in_template_dataset=None, extent=None,
                                  select_using_features="SELECT_USING_FEATURES", lod_field=None, minPS_field=None,
                                  maxPS_field=None, pixelSize=None, build_boundary="BUILD_BOUNDARY"):
    """A separate mosaic dataset made from items of an existing one, as documented: the items the where clause selects
    and, with a template, those whose footprint meets its features (``SELECT_USING_FEATURES``) or its extent, copied
    with their fields; the source's properties are carried over (INFERRED). ``minPS_field`` and ``maxPS_field`` name
    fields whose values become the items' MinPS and MaxPS; ``pixelSize`` is the largest cell size at which it shows
    items (held as its maximum MaxPS, INFERRED); ``lod_field`` is ignored, as the page says.

    Refused: an output in a folder, which ArcGIS writes as Esri's own .amd file; a coordinate system, band count or
    pixel type other than the source's, which needs items projected or converted on the fly. Overviews are never
    made for it, as documented."""
    from ._reference import reference_of
    connection, source_name, rec, view = _mosaic.open_mosaic(in_dataset, "CreateReferencedMosaicDataset")
    try:
        where = view.where if view is not None and view.where else None
        if _given(where_clause):
            where = f"({where}) and ({where_clause})" if where else where_clause
        rows = _mosaic.items(connection, source_name, where, [minPS_field or "", maxPS_field or ""])
        columns = _mosaic.columns_of(connection, source_name)
        nodata = {r["OBJECTID"]: _mosaic.item_nodata(connection, source_name, r["OBJECTID"]) for r in rows}
        times = dict(connection.execute("select item, mtime from grapy_mosaic_item where mosaic = ?",
                                        (source_name,)).fetchall())
    finally:
        connection.close()
    out = _mosaic.text(out_mosaic_dataset)
    if ".gpkg" not in out.lower():
        _refuse("CreateReferencedMosaicDataset", "out_mosaic_dataset", out,
                "a referenced mosaic dataset in a folder is Esri's .amd file; write it into a GeoPackage")
    if _given(coordinate_system):
        reference = reference_of(coordinate_system)
        if (getattr(reference, "factoryCode", 0) or 0) != rec.srid:
            _refuse("CreateReferencedMosaicDataset", "coordinate_system", coordinate_system,
                    "projecting items on the fly is not built in grapy")
    if _given(number_of_bands) and rec.bands and int(number_of_bands) != rec.bands:
        _refuse("CreateReferencedMosaicDataset", "number_of_bands", number_of_bands,
                "a band count other than the source's needs band definitions grapy does not have")
    if _given(pixel_type) and str(pixel_type).upper() != (rec.pixel_type or str(pixel_type).upper()):
        _refuse("CreateReferencedMosaicDataset", "pixel_type", pixel_type,
                "converting the items' cells on the fly is not built in grapy")
    if _given(in_template_dataset) or _given(extent):
        rows = [r for r in rows if r["shape"] is not None and _meets(r["shape"], in_template_dataset, extent,
                                                                      select_using_features)]
    file, name = _out_path(out)
    properties = dict(rec.properties)
    if _given(pixelSize):
        properties["maxPixelSize"] = float(pixelSize)
    extra = [(c, t) for c, t in columns if c.lower() not in {f.lower() for f, _ in _mosaic.FIELDS}]
    _mosaic.create(file, name, rec.srid, rec.bands, rec.pixel_type, rec.product, True, properties, extra)
    connection = _gpkg.connect(file)
    try:
        for row in rows:
            values = {c: row.get(c) for c, _ in columns}
            if _given(minPS_field):
                values["MinPS"] = row.get(minPS_field)
            if _given(maxPS_field):
                values["MaxPS"] = row.get(maxPS_field)
            if _given(pixelSize) and values.get("MaxPS") not in (None, 0):
                values["MaxPS"] = min(float(values["MaxPS"]), float(pixelSize))
            elif _given(pixelSize):
                values["MaxPS"] = float(pixelSize)
            oid = _insert(connection, name, rec.srid, row["shape"], values, oid=row["OBJECTID"])
            connection.execute("insert into grapy_mosaic_item (mosaic, item, mtime, nodata) values (?, ?, ?, ?)",
                               (name, oid, times.get(row["OBJECTID"]),
                                None if not nodata[row["OBJECTID"]] else json.dumps(nodata[row["OBJECTID"]])))
        if _word(build_boundary, "BUILD_BOUNDARY") in ("BUILD_BOUNDARY", "TRUE"):
            _boundary(connection, name, rec.srid, None, "OVERWRITE", "NONE")
    finally:
        _close(connection)
    return Result(f"{file}/{name}")


def _meets(shape, template, extent, select_using_features):
    """Whether a footprint meets a template's features (or their extent) or an extent."""
    if _given(template):
        from .analysis import _read
        try:
            shapes = [s for _, _, s in _read(_mosaic.text(template))]
        except Exception:  # noqa: BLE001 — a raster template: its extent
            from ._raster import Raster
            e = Raster(template).extent
            shapes = [shapely.box(e.XMin, e.YMin, e.XMax, e.YMax)]
        if _word(select_using_features, "SELECT_USING_FEATURES") not in ("SELECT_USING_FEATURES", "TRUE"):
            shapes = [shapely.box(*shapely.union_all(shapes).bounds)] if shapes else []
        return any(shape.intersects(s) for s in shapes)
    box = _extent_box(extent)
    return box is None or shape.intersects(box)


def _extent_box(value):
    if not _given(value):
        return None
    if hasattr(value, "XMin"):
        return shapely.box(value.XMin, value.YMin, value.XMax, value.YMax)
    parts = str(value).replace(",", " ").split()
    try:
        return shapely.box(*[float(p) for p in parts[:4]])
    except (ValueError, TypeError):
        raise ExecuteError(f"ERROR 000800: '{value}' is not an extent: write x-min y-min x-max y-max.")


def _insert(connection, name, srid, shape, values, oid=None):
    columns = [c for c in values if c != "OBJECTID"]
    fields = ([f'"{_mosaic.OID}"'] if oid is not None else []) + [f'"{_gpkg.GEOMETRY}"'] + [f'"{c}"' for c in columns]
    params = ([oid] if oid is not None else []) + [None if shape is None else _gpkg.encode(_mosaic._multi(shape), srid)] + \
             [values[c] for c in columns]
    cursor = connection.execute(f'insert into "{name}" ({", ".join(fields)}) values ({", ".join("?" * len(params))})',
                                params)
    return cursor.lastrowid if oid is None else oid


# ---- adding, removing and synchronizing items


def AddRastersToMosaicDataset(in_mosaic_dataset, raster_type, input_path, update_cellsize_ranges="UPDATE_CELL_SIZES",
                              update_boundary="UPDATE_BOUNDARY", update_overviews="NO_OVERVIEWS",
                              maximum_pyramid_levels=None, maximum_cell_size=0, minimum_dimension=1500,
                              spatial_reference=None, filter=None, sub_folder="SUBFOLDERS",
                              duplicate_items_action="ALLOW_DUPLICATES", build_pyramids="NO_PYRAMIDS",
                              calculate_statistics="NO_STATISTICS", build_thumbnails="NO_THUMBNAILS",
                              operation_description=None, force_spatial_reference="NO_FORCE_SPATIAL_REFERENCE",
                              estimate_statistics="NO_STATISTICS", aux_inputs=None, enable_pixel_cache="NO_PIXEL_CACHE",
                              cache_location=None):
    """Raster datasets added as items: files, and the raster files of folders (recursively with ``SUBFOLDERS``, the
    default), picked by ``filter`` — a wildcard or ``REGEX:`` pattern matched against the full path, as documented.
    Each item's footprint is its raster's extent, LowPS its cell size and HighPS its coarsest overview's (equal with
    none, as the attribute table page says), Category 1 (primary), Tag ``Dataset``, CenterX and CenterY its footprint's
    centroid, ZOrder null. ``EXCLUDE_DUPLICATES`` skips a raster already an item by path and name, as documented;
    ``OVERWRITE_DUPLICATES`` reads it again into the item it already is (INFERRED: the item keeps its OBJECTID).
    ``UPDATE_CELL_SIZES`` computes the new items' cell size ranges as CalculateCellSizeRanges does by default;
    ``UPDATE_BOUNDARY`` builds the boundary again from every footprint. ``CALCULATE_STATISTICS`` stores each raster's
    statistics as CalculateStatistics does.

    Refused: raster types other than Raster Dataset (they read a sensor's metadata through Esri's raster type
    definitions); a table or mosaic dataset as input; an SQL filter; a raster in another coordinate system than the
    mosaic dataset's, and ``spatial_reference`` other than it (projecting is not built); overviews, pyramids,
    thumbnails, estimated statistics, auxiliary inputs and the pixel cache (ADR-230 §5.5).

    INFERRED (ADR-230 §5.9 (b)): a wildcard and a path match without regard to case; a raster with no reference takes the
    mosaic dataset's; ``operation_description`` is kept in the mosaic dataset's properties."""
    tool = "AddRastersToMosaicDataset"
    kind = re.sub(r"[\s_]", "", str(raster_type or "")).upper()
    if kind == "FRAMECAMERA":
        return _add_frame_camera(in_mosaic_dataset, input_path, update_cellsize_ranges, update_boundary, aux_inputs,
                                 duplicate_items_action, operation_description, tool)
    if kind not in ("RASTERDATASET",):
        _refuse(tool, "raster_type", raster_type, "only the Raster Dataset and Frame Camera types are built; the others "
                "read a sensor's metadata through Esri's raster type definitions (ADR-230 §5.3, ADR-234 §5.3)")
    for name, value, default in (("update_overviews", update_overviews, "NO_OVERVIEWS"),
                                 ("build_pyramids", build_pyramids, "NO_PYRAMIDS"),
                                 ("build_thumbnails", build_thumbnails, "NO_THUMBNAILS"),
                                 ("estimate_statistics", estimate_statistics, "NO_STATISTICS"),
                                 ("enable_pixel_cache", enable_pixel_cache, "NO_PIXEL_CACHE")):
        if _word(value, default) not in (default, "FALSE"):
            _refuse(tool, name, value, _NOT_KEPT[name])
    if _given(aux_inputs):
        _refuse(tool, "aux_inputs", aux_inputs, "they override raster type settings grapy does not have")
    connection, mosaic, rec, _ = _mosaic.open_mosaic(in_mosaic_dataset, tool)
    _close(connection)
    if _given(spatial_reference):
        from ._reference import reference_of
        srid = getattr(reference_of(spatial_reference), "factoryCode", 0) or 0
        if srid != rec.srid:
            _refuse(tool, "spatial_reference", spatial_reference, "projecting items on the fly is not built in grapy")
    files = _gather(input_path, filter, _word(sub_folder, "SUBFOLDERS") in ("SUBFOLDERS", "TRUE"), tool)
    added = _add(in_mosaic_dataset, files, _word(duplicate_items_action, "ALLOW_DUPLICATES"), tool,
                 _word(calculate_statistics, "NO_STATISTICS") in ("CALCULATE_STATISTICS", "TRUE"))
    connection, mosaic, rec, _ = _mosaic.open_mosaic(in_mosaic_dataset, tool)
    try:
        if _given(operation_description):
            properties = dict(rec.properties)
            properties.setdefault("operations", []).append(str(operation_description))
            _mosaic.save_properties(connection, mosaic, properties)
        if added and _word(update_cellsize_ranges, "UPDATE_CELL_SIZES") in ("UPDATE_CELL_SIZES", "TRUE"):
            _cell_sizes(connection, mosaic, f'"{_mosaic.OID}" in ({", ".join(str(i) for i in added)})', True, True, 10,
                        rec.properties.get("cellSizeToleranceFactor") or 0, False)
        if _word(update_boundary, "UPDATE_BOUNDARY") in ("UPDATE_BOUNDARY", "TRUE"):
            _boundary(connection, mosaic, rec.srid, None, "OVERWRITE", "NONE")
    finally:
        _close(connection)
    return Result(_mosaic.text(in_mosaic_dataset))


def _add_frame_camera(in_mosaic_dataset, input_path, update_cellsize_ranges, update_boundary, aux_inputs,
                      duplicate_items_action, operation_description, tool):
    """The Frame Camera raster type (ADR-234 §5.3): ``input_path`` names a frames table — a GeoPackage table or a CSV
    file with the frames table schema's fields — and each row becomes an item: its image, its perspective centre and
    Omega, Phi, Kappa, and its camera's fields from the same row or, given as ``aux_inputs`` ``CameraFile <table>``, from
    a cameras table matched on CameraID; a frame's own camera fields take precedence, as the frames table page says. An
    item's footprint is its image's corners projected to the camera's AverageZ (0 by default); LowPS and HighPS its
    ground sample distance there; CenterX and CenterY its perspective centre (its nadir).

    INFERRED (ADR-234 §5.9 (b)): a relative Raster path is read from the frames table's folder; ``CameraFile`` as the
    name of the cameras table among the auxiliary inputs; the footprint and cell sizes at AverageZ."""
    from . import _ortho
    added = _ortho.add_frames(in_mosaic_dataset, input_path, aux_inputs, _word(duplicate_items_action,
                                                                                "ALLOW_DUPLICATES"), tool)
    connection, mosaic, rec, _ = _mosaic.open_mosaic(in_mosaic_dataset, tool)
    try:
        if _given(operation_description):
            properties = dict(rec.properties)
            properties.setdefault("operations", []).append(str(operation_description))
            _mosaic.save_properties(connection, mosaic, properties)
        if added and _word(update_cellsize_ranges, "UPDATE_CELL_SIZES") in ("UPDATE_CELL_SIZES", "TRUE"):
            _cell_sizes(connection, mosaic, f'"{_mosaic.OID}" in ({", ".join(str(i) for i in added)})', True, True, 10,
                        rec.properties.get("cellSizeToleranceFactor") or 0, False)
        if _word(update_boundary, "UPDATE_BOUNDARY") in ("UPDATE_BOUNDARY", "TRUE"):
            _boundary(connection, mosaic, rec.srid, None, "OVERWRITE", "NONE")
    finally:
        _close(connection)
    return Result(_mosaic.text(in_mosaic_dataset))


_NOT_KEPT = {
    "update_overviews": "overviews are Esri's resampled images of a mosaic dataset, made by a method its pages do not "
                        "specify; grapy reads a mosaic dataset at its items' own cell size (ADR-230 §5.5)",
    "build_pyramids": "grapy reads items at their base cell size and builds no pyramids for them (ADR-230 §5.5)",
    "build_thumbnails": "a thumbnail's size and format are not documented",
    "estimate_statistics": "how statistics are estimated for a mosaic dataset is not documented",
    "enable_pixel_cache": "the pixel cache is Esri's own cache format",
    "build_item_cache": "an item cache is Esri's Cloud Raster Format (CRF), which grapy does not write",
}


def _gather(input_path, pattern, deep, tool):
    """The raster files an input path names, by the filter."""
    found = []
    for entry in _list(input_path):
        path = _mosaic.text(entry)
        if ".gpkg" in path.lower():
            _refuse(tool, "input_path", path, "a table or mosaic dataset as input is not built; give raster files or "
                    "folders")
        if os.path.isdir(path):
            for root, folders, names in os.walk(path):
                folders.sort()
                found.extend(os.path.join(root, n) for n in sorted(names) if n.lower().endswith(RASTER_EXTENSIONS))
                if not deep:
                    break
        elif os.path.isfile(path):
            found.append(path)
        else:
            raise ExecuteError(f"ERROR 000732: Input Data: Dataset {path} does not exist or is not supported.")
    if _given(pattern):
        text = str(pattern)
        if text.upper().startswith("REGEX:"):
            expression = re.compile(text[6:], re.IGNORECASE)
            found = [f for f in found if expression.fullmatch(f.replace("\\", "/")) or expression.fullmatch(f)]
        elif any(token in text.upper() for token in ("=", " LIKE ", " AND ", " OR ", "<", ">")):
            _refuse(tool, "filter", text, "an SQL filter selects rows of a table input, which is not built")
        else:
            found = [f for f in found if fnmatch.fnmatch(f.replace("\\", "/").lower(), text.replace("\\", "/").lower())
                     or fnmatch.fnmatch(os.path.basename(f).lower(), text.lower())]
    return [os.path.abspath(f) for f in found]


def _same(a, b):
    return os.path.normcase(os.path.abspath(a or "")) == os.path.normcase(os.path.abspath(b or ""))


def _item_values(source):
    shape = source.box()
    c = shape.centroid
    return shape, {"Raster": source.path, "Name": os.path.splitext(os.path.basename(source.path))[0],
                   "LowPS": source.cell, "HighPS": source.cell * source.high, "Category": 1, "Tag": "Dataset",
                   "CenterX": c.x, "CenterY": c.y}


def _add(in_mosaic_dataset, files, duplicates, tool, statistics=False):
    """Files added as items; their new OBJECTIDs."""
    connection, mosaic, rec, _ = _mosaic.open_mosaic(in_mosaic_dataset, tool)
    added = []
    try:
        existing = _mosaic.items(connection, mosaic)
        bands, kind = rec.bands, rec.pixel_type
        for path in files:
            source = _mosaic.Source(path)
            if source.srid and rec.srid and source.srid != rec.srid:
                _refuse(tool, "input_path", path, f"it is in EPSG:{source.srid} and the mosaic dataset in EPSG:{rec.srid}; "
                        "projecting items on the fly is not built in grapy")
            if bands is None:
                bands = source.count
                connection.execute("update grapy_mosaic set bands = ? where name = ?", (bands, mosaic))
            elif source.count != bands:
                _refuse(tool, "input_path", path, f"it has {source.count} bands and the mosaic dataset {bands}; matching "
                        "bands across items is a raster type's band definition (ADR-230 §5.3)")
            if kind is None:
                kind = next((k for k, v in _mosaic.PIXEL_TYPES.items() if np.dtype(v) == source.dtype), None)
                connection.execute("update grapy_mosaic set pixel_type = ? where name = ?", (kind, mosaic))
            same = [i for i in existing if _same(i.get("Raster"), path)]
            if same and duplicates == "EXCLUDE_DUPLICATES":
                continue
            shape, values = _item_values(source)
            if same and duplicates == "OVERWRITE_DUPLICATES":
                oid = same[0]["OBJECTID"]
                _update(connection, mosaic, rec.srid, oid, shape, values)
            else:
                oid = _insert(connection, mosaic, rec.srid, shape, values)
                existing.append({"OBJECTID": oid, "Raster": path})
            connection.execute("insert or replace into grapy_mosaic_item (mosaic, item, mtime, nodata) values "
                               "(?, ?, ?, (select nodata from grapy_mosaic_item where mosaic = ? and item = ?))",
                               (mosaic, oid, os.path.getmtime(path), mosaic, oid))
            added.append(oid)
    finally:
        _close(connection)
    if statistics:
        from .management import CalculateStatistics
        for path in files:
            CalculateStatistics(path)
    return added


def _update(connection, mosaic, srid, oid, shape, values):
    sets = [f'"{_gpkg.GEOMETRY}" = ?'] + [f'"{c}" = ?' for c in values]
    connection.execute(f'update "{mosaic}" set {", ".join(sets)} where "{_mosaic.OID}" = ?',
                       [_gpkg.encode(_mosaic._multi(shape), srid), *values.values(), oid])


def RemoveRastersFromMosaicDataset(in_mosaic_dataset, where_clause=None, update_boundary="UPDATE_BOUNDARY",
                                   mark_overviews_items="MARK_OVERVIEW_ITEMS",
                                   delete_overview_images="DELETE_OVERVIEW_IMAGES",
                                   delete_item_cache="DELETE_ITEM_CACHE", remove_items="REMOVE_MOSAICDATASET_ITEMS",
                                   update_cellsize_ranges="UPDATE_CELL_SIZES"):
    """The items the where clause selects removed; with no query the tool does not run, as documented. Their raster
    files are kept. ``UPDATE_BOUNDARY`` builds the boundary again from the footprints left. grapy's mosaic datasets
    have no overviews or item caches, so their options have nothing to delete; ``NO_REMOVE_MOSAICDATASET_ITEMS``
    removes nothing. INFERRED: the cell size ranges of the items left do not depend on the ones removed (§5.9 (e)), so
    ``UPDATE_CELL_SIZES`` changes none."""
    if not _given(where_clause):
        raise ExecuteError("ERROR 000735: You must specify a selection or a query; otherwise, the tool will not run.")
    connection, mosaic, rec, view = _mosaic.open_mosaic(in_mosaic_dataset, "RemoveRastersFromMosaicDataset")
    try:
        if _word(remove_items, "REMOVE_MOSAICDATASET_ITEMS") in ("REMOVE_MOSAICDATASET_ITEMS", "TRUE"):
            gone = [i["OBJECTID"] for i in _mosaic.items(connection, mosaic, _and(view, where_clause))]
            for oid in gone:
                connection.execute(f'delete from "{mosaic}" where "{_mosaic.OID}" = ?', (oid,))
                connection.execute("delete from grapy_mosaic_item where mosaic = ? and item = ?", (mosaic, oid))
        if _word(update_boundary, "UPDATE_BOUNDARY") in ("UPDATE_BOUNDARY", "TRUE"):
            _boundary(connection, mosaic, rec.srid, None, "OVERWRITE", "NONE")
    finally:
        _close(connection)
    return Result(_mosaic.text(in_mosaic_dataset))


def _and(view, where_clause):
    """A tool's where clause joined with a mosaic layer's query."""
    shown = view.where if view is not None and view.where else None
    if shown and _given(where_clause):
        return f"({shown}) and ({where_clause})"
    return shown or (where_clause if _given(where_clause) else None)


def SynchronizeMosaicDataset(in_mosaic_dataset, where_clause=None, new_items="NO_NEW_ITEMS", sync_only_stale="SYNC_STALE",
                             update_cellsize_ranges="UPDATE_CELL_SIZES", update_boundary="UPDATE_BOUNDARY",
                             update_overviews="NO_OVERVIEWS", build_pyramids="NO_PYRAMIDS",
                             calculate_statistics="NO_STATISTICS", build_thumbnails="NO_THUMBNAILS",
                             build_item_cache="NO_ITEM_CACHE", rebuild_raster="REBUILD_RASTER",
                             update_fields="UPDATE_FIELDS", fields_to_update=None,
                             existing_items="UPDATE_EXISTING_ITEMS", broken_items="IGNORE_BROKEN_ITEMS",
                             skip_existing_items="SKIP_EXISTING_ITEMS", refresh_aggregate_info="NO_REFRESH_INFO",
                             estimate_statistics="NO_STATISTICS"):
    """Items brought up to date with their rasters, as documented: with ``UPDATE_EXISTING_ITEMS``, the items whose file
    changed since it was read (``SYNC_STALE``; every item with ``SYNC_ALL``) are read again — LowPS, HighPS, CenterX,
    CenterY and, with ``REBUILD_RASTER``, the footprint as the raster's extent; ``REMOVE_BROKEN_ITEMS`` removes items
    whose raster cannot be found; ``UPDATE_WITH_NEW_ITEMS`` adds the rasters in the folders of the items that are not
    items yet (``OVERWRITE_EXISTING_ITEMS`` reads those that are again). Then cell size ranges and the boundary, as
    AddRastersToMosaicDataset updates them.

    INFERRED (ADR-230 §5.9 (h)): an item is stale when its file's modification time is not the one recorded when it
    was last read; "the item's workspace" is the folder holding its raster, not its subfolders; ``fields_to_update``
    limits which of the fields above are rewritten.

    Refused: overviews, pyramids, thumbnails, the item cache and estimated statistics, as AddRastersToMosaicDataset
    refuses them; ``REFRESH_INFO``, whose aggregate information is not documented."""
    tool = "SynchronizeMosaicDataset"
    for name, value, default in (("update_overviews", update_overviews, "NO_OVERVIEWS"),
                                 ("build_pyramids", build_pyramids, "NO_PYRAMIDS"),
                                 ("build_thumbnails", build_thumbnails, "NO_THUMBNAILS"),
                                 ("build_item_cache", build_item_cache, "NO_ITEM_CACHE"),
                                 ("estimate_statistics", estimate_statistics, "NO_STATISTICS")):
        if _word(value, default) not in (default, "FALSE"):
            _refuse(tool, name, value, _NOT_KEPT[name])
    if _word(refresh_aggregate_info, "NO_REFRESH_INFO") not in ("NO_REFRESH_INFO", "FALSE"):
        _refuse(tool, "refresh_aggregate_info", refresh_aggregate_info, "its aggregate information is not documented")
    connection, mosaic, rec, view = _mosaic.open_mosaic(in_mosaic_dataset, tool)
    try:
        chosen = _mosaic.items(connection, mosaic, _and(view, where_clause))
        times = dict(connection.execute("select item, mtime from grapy_mosaic_item where mosaic = ?", (mosaic,)).fetchall())
        wanted = {f.lower() for f in _list(fields_to_update)} if _given(fields_to_update) else None
        rewrite = _word(update_fields, "UPDATE_FIELDS") in ("UPDATE_FIELDS", "TRUE")
        touched = []
        for item in chosen:
            path = item.get("Raster")
            if not path or not os.path.isfile(path):
                if _word(broken_items, "IGNORE_BROKEN_ITEMS") == "REMOVE_BROKEN_ITEMS":
                    connection.execute(f'delete from "{mosaic}" where "{_mosaic.OID}" = ?', (item["OBJECTID"],))
                    connection.execute("delete from grapy_mosaic_item where mosaic = ? and item = ?",
                                       (mosaic, item["OBJECTID"]))
                continue
            if _word(existing_items, "UPDATE_EXISTING_ITEMS") != "UPDATE_EXISTING_ITEMS":
                continue
            stale = times.get(item["OBJECTID"]) != os.path.getmtime(path)
            if _word(sync_only_stale, "SYNC_STALE") == "SYNC_STALE" and not stale:
                continue
            source = _mosaic.Source(path)
            shape, values = _item_values(source)
            values = {k: v for k, v in values.items() if k in ("LowPS", "HighPS", "CenterX", "CenterY")
                      and rewrite and (wanted is None or k.lower() in wanted)}
            if _word(rebuild_raster, "REBUILD_RASTER") not in ("REBUILD_RASTER", "TRUE"):
                shape = item["shape"]
            _update(connection, mosaic, rec.srid, item["OBJECTID"], shape, values)
            connection.execute("update grapy_mosaic_item set mtime = ? where mosaic = ? and item = ?",
                               (os.path.getmtime(path), mosaic, item["OBJECTID"]))
            touched.append(item["OBJECTID"])
        folders = sorted({os.path.dirname(i["Raster"]) for i in chosen if i.get("Raster")})
    finally:
        _close(connection)
    if _word(new_items, "NO_NEW_ITEMS") == "UPDATE_WITH_NEW_ITEMS":
        files = []
        for folder in folders:
            if os.path.isdir(folder):
                files.extend(_gather([folder], None, False, tool))
        duplicates = "OVERWRITE_DUPLICATES" if _word(skip_existing_items, "SKIP_EXISTING_ITEMS") == \
            "OVERWRITE_EXISTING_ITEMS" else "EXCLUDE_DUPLICATES"
        touched.extend(_add(in_mosaic_dataset, files, duplicates, tool))
    if _word(calculate_statistics, "NO_STATISTICS") in ("CALCULATE_STATISTICS", "TRUE"):
        from .management import CalculateStatistics
        connection, mosaic, rec, view = _mosaic.open_mosaic(in_mosaic_dataset, tool)
        try:
            paths = [i.get("Raster") for i in _mosaic.items(connection, mosaic) if i["OBJECTID"] in touched]
        finally:
            connection.close()
        for path in paths:
            CalculateStatistics(path)
    connection, mosaic, rec, view = _mosaic.open_mosaic(in_mosaic_dataset, tool)
    try:
        if touched and _word(update_cellsize_ranges, "UPDATE_CELL_SIZES") in ("UPDATE_CELL_SIZES", "TRUE"):
            _cell_sizes(connection, mosaic, f'"{_mosaic.OID}" in ({", ".join(str(i) for i in touched)})', True, True, 10,
                        rec.properties.get("cellSizeToleranceFactor") or 0, False)
        if _word(update_boundary, "UPDATE_BOUNDARY") in ("UPDATE_BOUNDARY", "TRUE"):
            _boundary(connection, mosaic, rec.srid, None, "OVERWRITE", "NONE")
    finally:
        _close(connection)
    return Result(_mosaic.text(in_mosaic_dataset))


def RepairMosaicDatasetPaths(in_mosaic_dataset, paths_list, where_clause=None):
    """Items' raster paths remapped, as documented: each pair is an original path and the path it changes to, and an
    original path of ``*`` changes every path. INFERRED (ADR-230 §5.9 (h)): an original path matches a raster path it
    begins, whole folder names at a time and without regard to case or slash direction, and is replaced by the new
    one; ``*`` keeps each file's name and moves it into the new folder; the first pair that matches is applied."""
    pairs = []
    for entry in _list(paths_list):
        parts = entry if isinstance(entry, (list, tuple)) else _split_pair(entry)
        if len(parts) != 2:
            raise ExecuteError(f"ERROR 000800: '{entry}' is not an original path and a new path.")
        pairs.append((str(parts[0]), str(parts[1])))
    connection, mosaic, rec, view = _mosaic.open_mosaic(in_mosaic_dataset, "RepairMosaicDatasetPaths")
    try:
        for item in _mosaic.items(connection, mosaic, _and(view, where_clause)):
            path = item.get("Raster") or ""
            for old, new in pairs:
                repaired = _repaired(path, old, new)
                if repaired is not None:
                    connection.execute(f'update "{mosaic}" set "Raster" = ? where "{_mosaic.OID}" = ?',
                                       (repaired, item["OBJECTID"]))
                    break
    finally:
        _close(connection)
    return Result(_mosaic.text(in_mosaic_dataset))


def _split_pair(text):
    """'C:/old D:/new', either path quoted where it holds a space."""
    import shlex
    try:
        return shlex.split(text.replace("\\", "/"), posix=True)
    except ValueError:
        return text.split()


def _repaired(path, old, new):
    if old.strip() == "*":
        return os.path.join(new, os.path.basename(path))
    norm = path.replace("\\", "/")
    prefix = old.replace("\\", "/").rstrip("/")
    if norm.lower() == prefix.lower():
        return new
    if norm.lower().startswith(prefix.lower() + "/"):
        return new.replace("\\", "/").rstrip("/") + norm[len(prefix):]
    return None


# ---- footprints, boundary, cell size ranges


def CalculateCellSizeRanges(in_mosaic_dataset, where_clause=None, do_compute_min="MIN_CELL_SIZES",
                            do_compute_max="MAX_CELL_SIZES", max_range_factor=10, cell_size_tolerance_factor=None,
                            update_missing_only="UPDATE_ALL"):
    """MinPS and MaxPS computed for the items the where clause selects (all with none), as documented: MaxPS is the
    native cell size times ``max_range_factor`` — the page's example, a 30 m image visible to 300 m with the default
    10 — and items within ``cell_size_tolerance_factor`` of each other are grouped under one nominal cell size.

    INFERRED (ADR-230 §5.9 (e)): MinPS is 0, so a coarser item stays in view beneath finer ones; a group's nominal cell
    size is its finest item's, and an item joins the group of the finest cell size it is within the factor of; the
    tolerance is 0 when not given (the page gives no default), grouping only equal cell sizes."""
    connection, mosaic, rec, view = _mosaic.open_mosaic(in_mosaic_dataset, "CalculateCellSizeRanges")
    try:
        tolerance = float(cell_size_tolerance_factor) if _given(cell_size_tolerance_factor) else \
            float(rec.properties.get("cellSizeToleranceFactor") or 0)
        _cell_sizes(connection, mosaic, _and(view, where_clause),
                    _word(do_compute_min, "MIN_CELL_SIZES") in ("MIN_CELL_SIZES", "TRUE"),
                    _word(do_compute_max, "MAX_CELL_SIZES") in ("MAX_CELL_SIZES", "TRUE"),
                    float(max_range_factor) if _given(max_range_factor) else 10.0, tolerance,
                    _word(update_missing_only, "UPDATE_ALL") == "UPDATE_MISSING_ONLY")
    finally:
        _close(connection)
    return Result(_mosaic.text(in_mosaic_dataset))


def _cell_sizes(connection, mosaic, where, do_min, do_max, factor, tolerance, missing_only):
    chosen = _mosaic.items(connection, mosaic, where)
    every = _mosaic.items(connection, mosaic)
    nominal = _nominal([float(i.get("LowPS") or 0) for i in every if i.get("LowPS")], tolerance)
    for item in chosen:
        low = float(item.get("LowPS") or 0)
        sets = {}
        if do_min and not (missing_only and item.get("MinPS") is not None):
            sets["MinPS"] = 0.0
        if do_max and low and not (missing_only and item.get("MaxPS") is not None):
            sets["MaxPS"] = nominal.get(round(low, 9), low) * factor
        if sets:
            connection.execute(f'update "{mosaic}" set {", ".join(f"{chr(34)}{k}{chr(34)} = ?" for k in sets)} '
                               f'where "{_mosaic.OID}" = ?', [*sets.values(), item["OBJECTID"]])


def _nominal(sizes, tolerance):
    """Each cell size's group's nominal size: its finest member's."""
    out = {}
    bases = []
    for size in sorted(set(round(s, 9) for s in sizes)):
        base = next((b for b in bases if abs(size - b) <= tolerance * b + 1e-12), None)
        if base is None:
            bases.append(size)
            base = size
        out[size] = base
    return out


def BuildFootprints(in_mosaic_dataset, where_clause=None, reset_footprint="RADIOMETRY", min_data_value=None,
                    max_data_value=None, approx_num_vertices=-1, shrink_distance=0, maintain_edges="NO_MAINTAIN_EDGES",
                    skip_derived_images="SKIP_DERIVED_IMAGES", update_boundary="UPDATE_BOUNDARY", request_size=-1,
                    min_region_size=None, simplification_method="NONE", edge_tolerance=None, max_sliver_size=None,
                    min_thinness_ratio=None):
    """Footprints computed again for the items the where clause selects (all with none), as documented: ``RADIOMETRY``
    excludes cells outside ``min_data_value``–``max_data_value`` (and NoData), ``GEOMETRY`` restores the raster's own
    extent, ``NONE`` changes none. ``shrink_distance`` moves the footprint inward by that distance; ``CONVEX_HULL`` and
    ``ENVELOPE`` simplify it; masked regions smaller than ``min_region_size`` cells are kept in it, as documented. Then
    the boundary, with ``UPDATE_BOUNDARY``.

    INFERRED (ADR-230 §5.9 (i)): a cell is valid when any band holds a value in range; with no data values given only
    NoData is excluded; the footprint is the union of the valid cells' squares; a masked region is a 4-connected group
    of invalid cells not touching the raster's edge; ``min_region_size`` is 0 when not given; derived items (there are
    none in grapy's mosaic datasets) are skipped either way.

    Refused: ``COPY_TO_SIBLING`` (no pan-sharpened raster type); an ``approx_num_vertices`` other than -1, since the
    page does not say how vertices are reduced; ``MAINTAIN_EDGES``; a ``request_size`` other than -1, which resamples;
    ``edge_tolerance``, ``max_sliver_size`` and ``min_thinness_ratio``, which serve those."""
    tool = "BuildFootprints"
    method = _word(reset_footprint, "RADIOMETRY")
    if method == "COPY_TO_SIBLING":
        _refuse(tool, "reset_footprint", reset_footprint, "it copies a pan-sharpened raster type's multispectral "
                "footprint, and grapy's only raster type is Raster Dataset")
    if method not in ("RADIOMETRY", "GEOMETRY", "NONE"):
        raise ExecuteError(f"ERROR 000800: reset_footprint '{reset_footprint}' is not RADIOMETRY, GEOMETRY, "
                           "COPY_TO_SIBLING or NONE.")
    if _given(approx_num_vertices) and int(approx_num_vertices) != -1:
        _refuse(tool, "approx_num_vertices", approx_num_vertices, "the page does not say how a footprint's vertices are "
                "reduced; -1 keeps all of them")
    if _word(maintain_edges, "NO_MAINTAIN_EDGES") not in ("NO_MAINTAIN_EDGES", "FALSE"):
        _refuse(tool, "maintain_edges", maintain_edges, "how sheet edges are kept is not documented")
    if _given(request_size) and int(request_size) != -1:
        _refuse(tool, "request_size", request_size, "it computes footprints from a resampled image, by resampling the "
                "page does not specify; -1 reads the raster at its own resolution")
    for name, value in (("edge_tolerance", edge_tolerance), ("max_sliver_size", max_sliver_size),
                        ("min_thinness_ratio", min_thinness_ratio)):
        if _given(value) and float(value) != -1:
            _refuse(tool, name, value, "it serves MAINTAIN_EDGES and resampled requests, which are not built")
    simplify = _word(simplification_method, "NONE")
    if simplify not in ("NONE", "CONVEX_HULL", "ENVELOPE"):
        raise ExecuteError(f"ERROR 000800: simplification_method '{simplification_method}' is not NONE, CONVEX_HULL "
                           "or ENVELOPE.")
    connection, mosaic, rec, view = _mosaic.open_mosaic(in_mosaic_dataset, tool)
    try:
        if method != "NONE":
            for item in _mosaic.items(connection, mosaic, _and(view, where_clause)):
                source = _mosaic.source_of(item.get("Raster"))
                if source is None:
                    AddWarning(f"{tool}: item {item['OBJECTID']}'s raster {item.get('Raster')} cannot be found; its "
                               "footprint is kept.")
                    continue
                if method == "GEOMETRY":
                    shape = source.box()
                else:
                    shape = _radiometry(source, _mosaic.item_nodata(connection, mosaic, item["OBJECTID"]),
                                        min_data_value, max_data_value, int(min_region_size or 0))
                if shape is not None and _given(shrink_distance) and float(shrink_distance):
                    shape = shape.buffer(-float(shrink_distance), join_style="mitre")
                if shape is not None and not shape.is_empty:
                    shape = _simplified(shape, simplify)
                _update(connection, mosaic, rec.srid, item["OBJECTID"], shape if shape is not None and not shape.is_empty
                        else shapely.MultiPolygon(), {})
        if _word(update_boundary, "UPDATE_BOUNDARY") in ("UPDATE_BOUNDARY", "TRUE"):
            _boundary(connection, mosaic, rec.srid, None, "OVERWRITE", "NONE")
    finally:
        _close(connection)
    return Result(_mosaic.text(in_mosaic_dataset))


def _radiometry(source, nodata, low, high, region):
    from rasterio.features import shapes
    from scipy import ndimage
    arrays, masks = source.read()
    masks = _mosaic.apply_nodata(arrays, masks, nodata)
    ok = []
    for a, m in zip(arrays, masks):
        good = ~m
        if _given(low):
            good &= a >= float(low)
        if _given(high):
            good &= a <= float(high)
        ok.append(good)
    valid = np.logical_or.reduce(ok)
    if region > 0:
        labels, count = ndimage.label(~valid)
        if count:
            sizes = ndimage.sum(np.ones_like(labels), labels, range(1, count + 1))
            edge = set(np.unique(np.concatenate([labels[0], labels[-1], labels[:, 0], labels[:, -1]]))) - {0}
            for k, size in enumerate(sizes, 1):
                if size < region and k not in edge:
                    valid |= labels == k
    if not valid.any():
        return None
    from rasterio.transform import from_origin
    transform = from_origin(source.left, source.top, source.width, source.height)
    parts = [shapely.geometry.shape(g) for g, v in shapes(valid.astype(np.uint8), mask=valid, transform=transform) if v]
    return shapely.union_all(parts)


def _simplified(shape, method):
    """CONVEX_HULL or ENVELOPE applied to each continuous part, as BuildBoundary's page says they are."""
    if method == "NONE":
        return shape
    parts = list(shape.geoms) if hasattr(shape, "geoms") else [shape]
    made = [p.convex_hull if method == "CONVEX_HULL" else shapely.box(*p.bounds) for p in parts]
    return shapely.union_all(made)


def BuildBoundary(in_mosaic_dataset, where_clause=None, append_to_existing="APPEND", simplification_method="NONE"):
    """The mosaic dataset's boundary: the perimeter of the footprints the where clause selects (all with none), merged
    into the existing boundary (``APPEND``, the default) or replacing it (``OVERWRITE``), simplified per continuous group
    of footprints by ``CONVEX_HULL`` or ``ENVELOPE``, as documented. INFERRED: the perimeter of the footprints is the
    outline of their union, holes kept."""
    simplify = _word(simplification_method, "NONE")
    if simplify not in ("NONE", "CONVEX_HULL", "ENVELOPE"):
        raise ExecuteError(f"ERROR 000800: simplification_method '{simplification_method}' is not NONE, CONVEX_HULL "
                           "or ENVELOPE.")
    mode = _word(append_to_existing, "APPEND")
    if mode not in ("APPEND", "OVERWRITE", "TRUE", "FALSE"):
        raise ExecuteError(f"ERROR 000800: append_to_existing '{append_to_existing}' is not APPEND or OVERWRITE.")
    connection, mosaic, rec, view = _mosaic.open_mosaic(in_mosaic_dataset, "BuildBoundary")
    try:
        _boundary(connection, mosaic, rec.srid, _and(view, where_clause),
                  "OVERWRITE" if mode in ("OVERWRITE", "FALSE") else "APPEND", simplify)
    finally:
        _close(connection)
    return Result(_mosaic.text(in_mosaic_dataset))


def _boundary(connection, mosaic, srid, where, mode, simplify):
    shapes = [i["shape"] for i in _mosaic.items(connection, mosaic, where) if i["shape"] is not None
              and not i["shape"].is_empty]
    shape = shapely.union_all(shapes) if shapes else None
    if mode == "APPEND":
        old = _mosaic.record(connection, mosaic).boundary
        if old is not None and not old.is_empty:
            shape = old if shape is None else shapely.union(old, shape)
    if shape is not None and not shape.is_empty:
        shape = _simplified(shape, simplify)
    _mosaic.set_boundary(connection, mosaic, srid, shape)


# ---- properties and layers


_SET_NAMES = {
    "rows_maximum_imagesize": "maxRequestSizeY", "columns_maximum_imagesize": "maxRequestSizeX",
    "allowed_compressions": "allowedCompressionMethods", "default_compression_type": "defaultCompressionMethod",
    "JPEG_quality": "JPEGQuality", "LERC_Tolerance": "LERCTolerance", "resampling_type": "defaultResamplingMethod",
    "allowed_mensuration_capabilities": "allowedMensurationCapabilities",
    "default_mensuration_capabilities": "defaultMensurationCapability", "allowed_mosaic_methods": "allowedMosaicMethods",
    "default_mosaic_method": "defaultMosaicMethod", "order_field": "orderField", "order_base": "orderBaseValue",
    "mosaic_operator": "mosaicOperator", "blend_width": "blendWidth", "view_point_x": "viewpointSpacingX",
    "view_point_y": "viewpointSpacingY", "max_num_per_mosaic": "maxRastersPerMosaic",
    "cell_size_tolerance": "cellSizeToleranceFactor", "metadata_level": "rasterMetadataLevel",
    "transmission_fields": "allowedFields", "start_time_field": "startTimeField", "end_time_field": "endTimeField",
    "time_format": "timeValueFormat", "max_num_of_download_items": "maxDownloadImageCount",
    "max_num_of_records_returned": "maxRecordsReturned", "data_source_type": "dataSourceType",
    "minimum_pixel_contribution": "minimumPixelContribution", "time_interval": "timeInterval",
    "time_interval_units": "timeIntervalUnits",
}


def SetMosaicDatasetProperties(in_mosaic_dataset, rows_maximum_imagesize=None, columns_maximum_imagesize=None,
                               allowed_compressions=None, default_compression_type=None, JPEG_quality=None,
                               LERC_Tolerance=None, resampling_type=None, clip_to_footprints=None,
                               footprints_may_contain_nodata=None, clip_to_boundary=None, color_correction=None,
                               allowed_mensuration_capabilities=None, default_mensuration_capabilities=None,
                               allowed_mosaic_methods=None, default_mosaic_method=None, order_field=None,
                               order_base=None, sorting_order=None, mosaic_operator=None, blend_width=None,
                               view_point_x=None, view_point_y=None, max_num_per_mosaic=None, cell_size_tolerance=None,
                               cell_size=None, metadata_level=None, transmission_fields=None, use_time=None,
                               start_time_field=None, end_time_field=None, time_format=None, geographic_transform=None,
                               max_num_of_download_items=None, max_num_of_records_returned=None, data_source_type=None,
                               minimum_pixel_contribution=None, processing_templates=None,
                               default_processing_template=None, time_interval=None, time_interval_units=None,
                               product_definition=None, product_band_definitions=None):
    """The mosaic dataset's properties set; a parameter not given keeps its value. Those that decide how grapy reads it
    as a raster take effect there — the default mosaic method and operator, order field, base and sort order, cell size,
    resampling, clipping to footprints and boundary, whether footprints may contain NoData, the rasters per mosaic and
    the minimum pixel contribution. The others govern an image service's transmission (sizes, compression, mensuration,
    metadata, download counts, time) and are kept and answered by Describe, as the properties they are.

    The default method must be among the allowed ones, as documented. Seamline and BLEND are read as this server's
    image services draw them (ADR-161, ADR-158; ADR-230 §5.4). Refused: Viewpoint as the default method (ADR-230 §5.4),
    colour correction, a geographic transformation (projecting is not built), processing templates and a product
    definition other than NONE (raster function templates and raster type band definitions)."""
    arguments = dict(locals())
    tool = "SetMosaicDatasetProperties"
    connection, mosaic, rec, _ = _mosaic.open_mosaic(in_mosaic_dataset, tool)
    try:
        props = dict(rec.properties)
        given = {k: v for k, v in arguments.items() if k in _SET_NAMES and _given(v)}
        for parameter, value in given.items():
            props[_SET_NAMES[parameter]] = value
        if _given(allowed_mosaic_methods):
            methods = [_method_word(m, tool) for m in _list(allowed_mosaic_methods)]
            props["allowedMosaicMethods"] = ";".join(methods)
        if _given(default_mosaic_method):
            props["defaultMosaicMethod"] = _method_word(default_mosaic_method, tool)
        allowed = [m.lower() for m in str(props["allowedMosaicMethods"]).split(";")]
        if props["defaultMosaicMethod"].lower() not in allowed:
            raise ExecuteError(f"ERROR 000800: the default mosaic method {props['defaultMosaicMethod']} must be set in the "
                               "allowed mosaic methods.")
        if props["defaultMosaicMethod"] == "Viewpoint":
            _refuse(tool, "default_mosaic_method", default_mosaic_method, "its viewpoint is the one a user moves in "
                    "ArcGIS Pro's display, which a tool on this server has no counterpart of (ADR-230 §5.4)")
        if _given(mosaic_operator):
            operator = str(mosaic_operator).upper()
            if operator not in _mosaic.OPERATORS:
                raise ExecuteError(f"ERROR 000800: mosaic_operator '{mosaic_operator}' is not one of "
                                   f"{', '.join(_mosaic.OPERATORS)}.")
            props["mosaicOperator"] = operator
        if _given(sorting_order):
            props["sortAscending"] = str(sorting_order).upper() != "DESCENDING"
        if _given(resampling_type):
            kind = str(resampling_type).upper()
            if kind not in ("NEAREST", "BILINEAR", "CUBIC", "MAJORITY"):
                raise ExecuteError(f"ERROR 000800: resampling_type '{resampling_type}' is not NEAREST, BILINEAR, CUBIC "
                                   "or MAJORITY.")
            props["defaultResamplingMethod"] = kind
        for key, value, yes in (("clipToFootprint", clip_to_footprints, "CLIP"),
                                ("footprintMayContainNoData", footprints_may_contain_nodata,
                                 "FOOTPRINTS_MAY_CONTAIN_NODATA"),
                                ("clipToBoundary", clip_to_boundary, "CLIP"), ("useTime", use_time, "ENABLED")):
            if _given(value):
                props[key] = str(value).upper() in (yes, "TRUE")
        if _given(color_correction) and str(color_correction).upper() not in ("NOT_APPLY", "FALSE"):
            _refuse(tool, "color_correction", color_correction, "ColorBalanceMosaicDataset, which makes it, is not built")
        if _given(geographic_transform):
            _refuse(tool, "geographic_transform", geographic_transform, "projecting items on the fly is not built")
        for name, value in (("processing_templates", processing_templates),
                            ("default_processing_template", default_processing_template)):
            if _given(value) and str(value).upper() != "NONE":
                _refuse(tool, name, value, "a processing template is a raster function template, Esri's own format")
        if _given(product_definition) and str(product_definition).upper() != "NONE" or _given(product_band_definitions):
            _refuse(tool, "product_definition", product_definition, "band definitions come from raster types grapy does "
                    "not have (ADR-230 §5.3)")
        if _given(cell_size):
            parts = cell_size if isinstance(cell_size, (list, tuple)) else str(cell_size).replace(",", " ").split()
            props["cellSize"] = [float(parts[0]), float(parts[-1])]
        if _given(order_base):
            props["orderBaseValue"] = _mosaic._number(order_base) if not isinstance(order_base, str) else order_base
        for key in ("maxRastersPerMosaic", "minimumPixelContribution", "maxRequestSizeX", "maxRequestSizeY",
                    "JPEGQuality", "maxDownloadImageCount", "maxRecordsReturned"):
            if key in props and props[key] is not None and not isinstance(props[key], bool):
                props[key] = int(float(props[key]))
        for key in ("blendWidth", "viewpointSpacingX", "viewpointSpacingY", "cellSizeToleranceFactor", "LERCTolerance"):
            if key in props and props[key] is not None:
                props[key] = float(props[key])
        _mosaic.save_properties(connection, mosaic, props)
    finally:
        _close(connection)
    return Result(_mosaic.text(in_mosaic_dataset))


def _method_word(value, tool):
    word = str(value).strip()
    for method in _mosaic.METHODS:
        if word.lower() == method.lower():
            return method
    mapped = _mosaic.LAYER_METHODS.get(word.upper())
    if mapped:
        return mapped
    raise ExecuteError(f"ERROR 000800: {tool}: mosaic method '{value}' is not one of {', '.join(_mosaic.METHODS)}.")


def MakeMosaicLayer(in_mosaic_dataset, out_mosaic_layer, where_clause=None, template=None, band_index=None,
                    mosaic_method=None, order_field=None, order_base_value=None, lock_rasterid=None,
                    sort_order="ASCENDING", mosaic_operator="FIRST", cell_size=None, processing_template=None):
    """A layer over a mosaic dataset, which the mosaic tools, the cursors (its query applied) and ``Raster`` accept in
    its place for as long as the process runs. It reads the items its where clause selects, in its own mosaic method
    and operator (FIRST by default, as documented), within its template extent — an Extent, ``x-min y-min x-max
    y-max``, ``MAXOF`` (all the items) or ``MINOF`` (their common area) — at its cell size, with the bands of
    ``band_index`` in that order. ``LOCK_RASTER`` shows only the items named by ``lock_rasterid``, OBJECTIDs or names,
    semicolon-delimited. BY_ATTRIBUTE needs ``order_field``; ``order_base_value`` defaults to 0 (INFERRED).

    ``SEAMLINE`` draws each item on its side of the halfway lines between item centres and ``BLEND`` weighs cells by
    their distance inside their item, as this server's image services do (ADR-161, ADR-158; ADR-230 §5.4).

    Refused: ``DISPLAY`` (there is no display), ``CLOSEST_TO_VIEWPOINT`` (ADR-230 §5.4), ``LAST`` with ``SEAMLINE`` as
    documented, and a processing template."""
    from . import _layers
    tool = "MakeMosaicLayer"
    connection, mosaic, rec, view = _mosaic.open_mosaic(in_mosaic_dataset, tool)
    connection.close()
    path = view.path if view is not None else f"{_mosaic.locate(in_mosaic_dataset)[0]}/{mosaic}"
    method = _mosaic.LAYER_METHODS.get(str(mosaic_method).upper()) if _given(mosaic_method) else None
    if _given(mosaic_method) and method is None:
        raise ExecuteError(f"ERROR 000800: mosaic_method '{mosaic_method}' is not one of "
                           f"{', '.join(_mosaic.LAYER_METHODS)}.")
    if method == "Viewpoint":
        _refuse(tool, "mosaic_method", mosaic_method, "its viewpoint is the one a user moves in ArcGIS Pro's display, "
                "which a tool on this server has no counterpart of (ADR-230 §5.4)")
    operator = str(mosaic_operator or "FIRST").upper()
    if operator not in _mosaic.OPERATORS:
        raise ExecuteError(f"ERROR 000800: mosaic_operator '{mosaic_operator}' is not one of {', '.join(_mosaic.OPERATORS)}.")
    if method == "Seamline" and operator == "LAST":
        raise ExecuteError("ERROR 000800: LAST is not valid with SEAMLINE.")
    if method == "ByAttribute" and not _given(order_field):
        raise ExecuteError("ERROR 000735: order_field is required with BY_ATTRIBUTE.")
    if method == "LockRaster" and not _given(lock_rasterid):
        raise ExecuteError("ERROR 000735: lock_rasterid is required with LOCK_RASTER.")
    if _given(processing_template) and str(processing_template).upper() != "NONE":
        _refuse(tool, "processing_template", processing_template, "a raster function template is Esri's own format")
    extent = None
    if _given(template):
        word = str(template).upper() if isinstance(template, str) else ""
        if word == "DISPLAY":
            _refuse(tool, "template", template, "a tool on this server has no display")
        elif word == "MINOF":
            extent = "MINOF"
        elif word != "MAXOF":
            box = _extent_box(template) if hasattr(template, "XMin") or _numbers(template) else None
            if box is None:
                from .analysis import _read
                shapes = [s for _, _, s in _read(_mosaic.text(template))]
                box = shapely.box(*shapely.union_all(shapes).bounds)
            extent = box.bounds
    size = None
    if _given(cell_size):
        parts = cell_size if isinstance(cell_size, (list, tuple)) else str(cell_size).replace(",", " ").split()
        size = (float(parts[0]), float(parts[-1]))
    bands = [int(b) for b in _list(band_index)] if _given(band_index) else None
    if bands is not None and len(bands) == 1 and isinstance(band_index, str) and " " in band_index.strip():
        bands = [int(b) for b in band_index.split()]
    lock = [str(v) for v in _list(lock_rasterid)] if _given(lock_rasterid) else None
    name = _mosaic.text(out_mosaic_layer)
    _mosaic.LAYERS[name] = _mosaic.View(path, where_clause if _given(where_clause) else None, method,
                                        order_field if _given(order_field) else None,
                                        order_base_value if _given(order_base_value) else None, lock,
                                        str(sort_order or "ASCENDING").upper() != "DESCENDING", operator, size, bands,
                                        extent)
    _layers.LAYERS[name] = _layers.Layer(name, path, where_clause if _given(where_clause) else None, "MosaicLayer")
    return Result(name)


def _numbers(value):
    try:
        return len([float(p) for p in str(value).replace(",", " ").split()]) == 4
    except ValueError:
        return False


def DefineMosaicDatasetNoData(in_mosaic_dataset, num_bands, bands_for_nodata_value=None, bands_for_valid_data_range=None,
                              where_clause=None, Composite_nodata_value="NO_COMPOSITE_NODATA"):
    """NoData defined for the items the where clause selects (all with none), as documented: values per band (``BAND_n``
    or ``ALL_BANDS``, several separated by spaces) become NoData, values outside a band's valid range become NoData,
    and ``COMPOSITE_NODATA`` makes a cell NoData only where every band is, ``NO_COMPOSITE_NODATA`` (the default) where
    any is. INFERRED (ADR-230 §5.9 (i)): a band may also be written by its number; a new definition replaces an item's
    old one; the definition also counts for BuildFootprints."""
    tool = "DefineMosaicDatasetNoData"
    connection, mosaic, rec, view = _mosaic.open_mosaic(in_mosaic_dataset, tool)
    try:
        bands = rec.bands
        if bands is not None and int(num_bands) != bands:
            raise ExecuteError(f"ERROR 000800: num_bands is {num_bands} and the mosaic dataset has {bands} bands.")
        values, ranges = {}, {}
        for entry in _list(bands_for_nodata_value):
            parts = entry if isinstance(entry, (list, tuple)) else _split_pair(str(entry))
            band = _band_key(parts[0], int(num_bands))
            numbers = " ".join(str(p) for p in parts[1:]).replace(",", " ").split()
            values.setdefault(band, []).extend(float(n) for n in numbers)
        for entry in _list(bands_for_valid_data_range):
            parts = entry if isinstance(entry, (list, tuple)) else str(entry).split()
            if len(parts) != 3:
                raise ExecuteError(f"ERROR 000800: '{entry}' is not a band, a minimum and a maximum.")
            ranges[_band_key(parts[0], int(num_bands))] = (float(parts[1]), float(parts[2]))
        composite = _word(Composite_nodata_value, "NO_COMPOSITE_NODATA") in ("COMPOSITE_NODATA", "TRUE")
        definition = {"values": values, "ranges": ranges, "composite": composite}
        for item in _mosaic.items(connection, mosaic, _and(view, where_clause)):
            connection.execute("insert into grapy_mosaic_item (mosaic, item, nodata) values (?, ?, ?) on conflict "
                               "(mosaic, item) do update set nodata = excluded.nodata",
                               (mosaic, item["OBJECTID"], json.dumps(definition)))
    finally:
        _close(connection)
    return Result(_mosaic.text(in_mosaic_dataset))


def _band_key(word, count):
    text = str(word).strip().upper()
    if text == "ALL_BANDS":
        return "ALL"
    match = re.fullmatch(r"(?:BAND_?)?(\d+)", text)
    if not match or not 1 <= int(match.group(1)) <= count:
        raise ExecuteError(f"ERROR 000800: '{word}' is not ALL_BANDS or a band of the {count}.")
    return str(int(match.group(1)))


# ---- exporting and importing


def ExportMosaicDatasetPaths(in_mosaic_dataset, out_table, where_clause=None, export_mode="ALL", types_of_paths=None):
    """A table of the items' file paths, as documented: one row per path, ``SourceOID`` the item's OBJECTID; ``BROKEN``
    exports only paths whose file cannot be found. grapy's items have raster paths only — no item caches — so
    ``ITEM_CACHE`` alone exports none.

    INFERRED (ADR-230 §5.9 (j)): the other fields, which the page does not name, are ``Path`` and ``Type`` (``RASTER``);
    a ``.dbf`` output is refused, grapy writing tables into GeoPackages."""
    from .stats import _write_table
    if _mosaic.text(out_table).lower().endswith(".dbf"):
        _refuse("ExportMosaicDatasetPaths", "out_table", out_table, "grapy writes tables into GeoPackages, not dBASE")
    kinds = {str(k).upper() for k in _list(types_of_paths)} if _given(types_of_paths) else {"RASTER", "ITEM_CACHE"}
    connection, mosaic, rec, view = _mosaic.open_mosaic(in_mosaic_dataset, "ExportMosaicDatasetPaths")
    try:
        chosen = _mosaic.items(connection, mosaic, _and(view, where_clause))
    finally:
        connection.close()
    broken_only = _word(export_mode, "ALL") == "BROKEN"
    rows = [(i["OBJECTID"], i.get("Raster") or "", "RASTER") for i in chosen if "RASTER" in kinds
            and (not broken_only or not os.path.isfile(i.get("Raster") or ""))]
    columns = [_gpkg.Column("SourceOID", "Integer"), _gpkg.Column("Path", "String"), _gpkg.Column("Type", "String")]
    return Result(_write_table(out_table, columns, rows))


def ExportMosaicDatasetItems(in_mosaic_dataset, out_folder, out_base_name=None, where_clause=None, format="TIFF",
                             nodata_value=None, clip_type="NONE", template_dataset=None, cell_size=None,
                             image_space="MAPSPACE", remove_distortion=None, band_method=None,
                             band_name_selection=None, band_id_selection=None):
    """Each selected item saved as a raster in ``out_folder``, as documented: named ``out_base_name`` followed by its
    OBJECTID, or by its ``Name`` with none; cells holding ``nodata_value`` become NoData; ``EXTENT`` clips to an extent
    (``template_dataset``: an Extent, ``x-min y-min x-max y-max`` or a dataset's extent), ``FEATURE_CLASS`` to a
    feature class's extent. INFERRED (ADR-230 §5.9 (j)): a clip keeps the item's whole cells that the extent overlaps;
    the output's NoData value is ``nodata_value`` when given; ``.tif`` is added to the name.

    Refused: formats other than TIFF (CRF and MRF are Esri's; the others are not written by grapy); ``IMAGESPACE``,
    distortion removal and band selection (sensor models and band definitions grapy does not have); a cell size other
    than the item's, which resamples; ``DISPLAY`` and ``MINOF`` templates."""
    tool = "ExportMosaicDatasetItems"
    if str(format or "TIFF").upper() not in ("TIFF", "TIF"):
        _refuse(tool, "format", format, "grapy writes GeoTIFF; CRF and MRF are Esri's own formats")
    if _word(image_space, "MAPSPACE") != "MAPSPACE":
        _refuse(tool, "image_space", image_space, "image space needs a sensor model grapy does not have")
    for name, value in (("remove_distortion", remove_distortion), ("band_method", band_method),
                        ("band_name_selection", band_name_selection), ("band_id_selection", band_id_selection)):
        if _given(value) and str(value).upper() not in ("NO_REMOVE_DISTORTION", "ALL", "FALSE"):
            _refuse(tool, name, value, "it needs sensor models or band definitions grapy does not have")
    clip = _word(clip_type, "NONE")
    if clip not in ("NONE", "EXTENT", "FEATURE_CLASS"):
        raise ExecuteError(f"ERROR 000800: clip_type '{clip_type}' is not NONE, EXTENT or FEATURE_CLASS.")
    box = None
    if clip != "NONE":
        word = str(template_dataset).upper() if isinstance(template_dataset, str) else ""
        if word in ("DISPLAY", "MINOF"):
            _refuse(tool, "template_dataset", template_dataset, "there is no display, and a common extent of items "
                    "exported one by one is not defined")
        if word != "MAXOF" and _given(template_dataset):
            if hasattr(template_dataset, "XMin") or _numbers(template_dataset):
                box = _extent_box(template_dataset)
            else:
                from . import Describe
                e = Describe(template_dataset).extent
                box = shapely.box(e.XMin, e.YMin, e.XMax, e.YMax)
    folder = _mosaic.text(out_folder)
    if not os.path.isdir(folder):
        raise ExecuteError(f"ERROR 000732: Output Folder: Dataset {folder} does not exist or is not supported.")
    connection, mosaic, rec, view = _mosaic.open_mosaic(in_mosaic_dataset, tool)
    try:
        chosen = _mosaic.items(connection, mosaic, _and(view, where_clause))
    finally:
        connection.close()
    written = []
    for item in chosen:
        source = _mosaic.source_of(item.get("Raster"))
        if source is None:
            AddWarning(f"{tool}: item {item['OBJECTID']}'s raster {item.get('Raster')} cannot be found; it is not "
                       "exported.")
            continue
        if _given(cell_size):
            parts = cell_size if isinstance(cell_size, (list, tuple)) else str(cell_size).replace(",", " ").split()
            if not (np.isclose(float(parts[0]), source.width) and np.isclose(float(parts[-1]), source.height)):
                _refuse(tool, "cell_size", cell_size, "a cell size other than the item's resamples, which is not built")
        stem = f"{out_base_name}{item['OBJECTID']}" if _given(out_base_name) else (item.get("Name") or
                                                                                  f"item{item['OBJECTID']}")
        path = os.path.join(folder, stem + ".tif")
        if _export(source, path, nodata_value, box):
            written.append(path)
    return Result(folder)


def _export(source, path, nodata_value, box):
    import rasterio
    from rasterio.windows import Window
    with rasterio.open(source.path) as raster:
        window = Window(0, 0, raster.width, raster.height)
        if box is not None:
            x0, y0, x1, y1 = box.bounds
            c0 = max(0, int(math.floor((x0 - source.left) / source.width + 1e-9)))
            c1 = min(raster.width, int(math.ceil((x1 - source.left) / source.width - 1e-9)))
            r0 = max(0, int(math.floor((source.top - y1) / source.height + 1e-9)))
            r1 = min(raster.height, int(math.ceil((source.top - y0) / source.height - 1e-9)))
            if c1 <= c0 or r1 <= r0:
                return False
            window = Window(c0, r0, c1 - c0, r1 - r0)
        data = raster.read(window=window)
        profile = raster.profile.copy()
        profile.update(driver="GTiff", width=int(window.width), height=int(window.height),
                       transform=raster.window_transform(window))
        for key in ("blockxsize", "blockysize", "tiled"):
            profile.pop(key, None)
        if _given(nodata_value):
            profile["nodata"] = float(nodata_value) if np.issubdtype(data.dtype, np.floating) else int(float(nodata_value))
    with rasterio.open(path, "w", **profile) as out:
        out.write(data)
    return True


def ExportMosaicDatasetGeometry(in_mosaic_dataset, out_feature_class, where_clause=None, geometry_type="FOOTPRINT"):
    """The mosaic dataset's geometry as a polygon feature class, as documented: ``FOOTPRINT`` each selected item's
    footprint, ``BOUNDARY`` the boundary, ``LEVEL`` the footprints grouped by cell size level.

    INFERRED (ADR-230 §5.9 (j)), the page naming no fields: a footprint keeps the catalog's fields, with ``SourceOID``
    its item's OBJECTID; the boundary has no fields; a level is the union of the footprints of one LowPS, with
    ``LowPS``, the least ``MinPS`` and the greatest ``MaxPS`` of its items; ``FOOTPRINT`` is the default.

    Refused: ``SEAMLINE`` — grapy stores no seamlines: the Seamline method draws the halfway lines between item
    centres as a mosaic is read (ADR-161), and BuildSeamlines is not built."""
    from .analysis import _out
    kind = _word(geometry_type, "FOOTPRINT")
    if kind == "SEAMLINE":
        _refuse("ExportMosaicDatasetGeometry", "geometry_type", geometry_type, "grapy stores no seamlines; the "
                "Seamline method draws them as a mosaic dataset is read (ADR-161, ADR-230 §5.4)")
    if kind not in ("FOOTPRINT", "BOUNDARY", "LEVEL"):
        raise ExecuteError(f"ERROR 000800: geometry_type '{geometry_type}' is not FOOTPRINT, BOUNDARY, SEAMLINE or LEVEL.")
    connection, mosaic, rec, view = _mosaic.open_mosaic(in_mosaic_dataset, "ExportMosaicDatasetGeometry")
    try:
        chosen = _mosaic.items(connection, mosaic, _and(view, where_clause),
                               [c for c, _ in _mosaic.columns_of(connection, mosaic)])
        columns = _mosaic.columns_of(connection, mosaic)
    finally:
        connection.close()
    path = _out(out_feature_class)
    if kind == "BOUNDARY":
        rows = [] if rec.boundary is None or rec.boundary.is_empty else [((), rec.boundary)]
        fields = []
    elif kind == "FOOTPRINT":
        fields = [_gpkg.Column("SourceOID", "Integer"), *[_gpkg.Column(c, t) for c, t in columns]]
        rows = [((i["OBJECTID"], *[i.get(c) for c, _ in columns]), i["shape"]) for i in chosen if i["shape"] is not None]
    else:
        fields = [_gpkg.Column("LowPS", "Double"), _gpkg.Column("MinPS", "Double"), _gpkg.Column("MaxPS", "Double")]
        groups = {}
        for i in chosen:
            if i["shape"] is not None:
                groups.setdefault(round(float(i.get("LowPS") or 0), 9), []).append(i)
        rows = []
        for low in sorted(groups):
            members = groups[low]
            mins = [float(m["MinPS"]) for m in members if m.get("MinPS") is not None]
            maxs = [float(m["MaxPS"]) for m in members if m.get("MaxPS") is not None]
            rows.append(((low, min(mins) if mins else None, max(maxs) if maxs else None),
                         shapely.union_all([m["shape"] for m in members])))
    _gpkg.create(path, "Polygon", rec.srid, fields)
    from . import da
    with da.InsertCursor(path, [*(c.name for c in fields), "SHAPE@"]) as cursor:
        for values, shape in rows:
            cursor.insertRow([*values, _mosaic._multi(shape)])
    return Result(path)


def ImportMosaicDatasetGeometry(in_mosaic_dataset, target_featureclass_type, target_join_field, input_featureclass,
                                input_join_field):
    """Footprints or the boundary replaced by geometry from a feature class, as documented: each item whose
    ``target_join_field`` equals an input feature's ``input_join_field`` takes that feature's shape.

    INFERRED (ADR-230 §5.9 (j)): an item matched by several features takes their union; for ``BOUNDARY``, which grapy
    holds as one shape with no fields, the boundary becomes the union of the input features whose join value is that
    of an item (or of every input feature when the target field is OBJECTID and no item matches).

    Refused: ``SEAMLINE`` (grapy stores no seamlines, ADR-230 §5.4)."""
    from .analysis import _read
    kind = _word(target_featureclass_type, "")
    if kind == "SEAMLINE":
        _refuse("ImportMosaicDatasetGeometry", "target_featureclass_type", target_featureclass_type,
                "grapy stores no seamlines; the Seamline method draws them as a mosaic dataset is read "
                "(ADR-161, ADR-230 §5.4)")
    if kind not in ("FOOTPRINT", "BOUNDARY"):
        raise ExecuteError(f"ERROR 000800: target_featureclass_type '{target_featureclass_type}' is not FOOTPRINT, "
                           "SEAMLINE or BOUNDARY.")
    columns_in = _read_columns(input_featureclass)
    position = next((k for k, c in enumerate(columns_in) if c.lower() == str(input_join_field).lower()), None)
    by_oid = position is None and str(input_join_field).lower() in ("objectid", "fid", "oid", "oid@")
    if position is None and not by_oid:
        raise ExecuteError(f"ERROR 000728: Field {input_join_field} does not exist within table.")
    features = [(oid if by_oid else values[position], shape)
                for oid, values, shape in _read(_mosaic.text(input_featureclass))]
    connection, mosaic, rec, view = _mosaic.open_mosaic(in_mosaic_dataset, "ImportMosaicDatasetGeometry")
    try:
        target = str(target_join_field)
        present = {c.lower(): c for c, _ in _mosaic.columns_of(connection, mosaic)}
        present[_mosaic.OID.lower()] = _mosaic.OID
        if target.lower() not in present:
            raise ExecuteError(f"ERROR 000728: Field {target_join_field} does not exist within table.")
        target = present[target.lower()]
        catalog = _mosaic.items(connection, mosaic, None, [target])
        if kind == "FOOTPRINT":
            for item in catalog:
                key = item["OBJECTID"] if target == _mosaic.OID else item.get(target)
                matched = [s for v, s in features if v is not None and _equal(v, key)]
                if matched:
                    _update(connection, mosaic, rec.srid, item["OBJECTID"], shapely.union_all(matched), {})
        else:
            keys = [item["OBJECTID"] if target == _mosaic.OID else item.get(target) for item in catalog]
            matched = [s for v, s in features if any(_equal(v, k) for k in keys)]
            if not matched and target == _mosaic.OID:
                matched = [s for _, s in features]
            if matched:
                _mosaic.set_boundary(connection, mosaic, rec.srid, shapely.union_all(matched))
    finally:
        _close(connection)
    return Result(_mosaic.text(in_mosaic_dataset))


def _read_columns(path):
    from .analysis import _schema
    return [c.name for c in _schema(path)[0]]


def _equal(a, b):
    if a is None or b is None:
        return False
    try:
        return float(a) == float(b)
    except (TypeError, ValueError):
        return str(a) == str(b)
