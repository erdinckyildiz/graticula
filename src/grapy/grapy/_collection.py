"""arcpy.ia.RasterCollection — a list of rasters with attributes, filtered, grouped and reduced (ADR-236).

An item is a dict with the raster under ``"Raster"`` and its attributes under their field names, as the class page's
examples read them (``rc[0]["Raster"]``, ``item["AcquisitionDate"]``). A list of rasters gives each item a ``Name``,
the raster's file name; a multidimensional raster gives an item per slice, with ``Variable`` and its dimension values.

INFERRED (ADR-236 §5.8): ``fields`` lists ``Raster`` first and then the attribute fields; ``reduce`` calls its
function with the list of rasters and ``func_args`` as keywords; the pixel statistics divide the standard deviation by
n, as CellStatistics' does.
"""
from __future__ import annotations

import datetime as _dt
import os

import numpy as np

from . import _mdim
from ._runtime import NotBuiltError

_OPERATORS = ("EQUALS", "NOT_EQUALS", "GREATER_THAN", "NOT_GREATER_THAN", "LESS_THAN", "NOT_LESS_THAN", "IN", "NOT_IN",
              "CONTAINS", "NOT_CONTAINS", "STARTS_WITH", "NOT_STARTS_WITH", "ENDS_WITH", "NOT_ENDS_WITH")


def _test(value, operator, wanted):
    op = str(operator).upper()
    if op not in _OPERATORS:
        raise ValueError(f"'{operator}' is not one of {', '.join(_OPERATORS)}.")
    negate = op.startswith("NOT_")
    base = op[4:] if negate else op
    if base == "EQUALS":
        hit = value == wanted
    elif base == "GREATER_THAN":
        hit = value is not None and value > wanted
    elif base == "LESS_THAN":
        hit = value is not None and value < wanted
    elif base == "IN":
        hit = value in (wanted if isinstance(wanted, (list, tuple, set)) else [wanted])
    else:
        text, items = str(value), wanted if isinstance(wanted, (list, tuple)) else [wanted]
        if base == "CONTAINS":
            hit = any(str(w) in text for w in items)
        elif base == "STARTS_WITH":
            hit = any(text.startswith(str(w)) for w in items)
        else:
            hit = any(text.endswith(str(w)) for w in items)
    return not hit if negate else hit


def _time(value, date_time_format=None):
    if value is None:
        return None
    if isinstance(value, _dt.datetime):
        return value
    if isinstance(value, _dt.date):
        return _dt.datetime(value.year, value.month, value.day)
    if isinstance(value, np.datetime64):
        return value.astype("datetime64[s]").astype(_dt.datetime)
    if date_time_format:
        return _dt.datetime.strptime(str(value), date_time_format)
    return _mdim.to_time(value).astype(_dt.datetime)


class RasterCollection:
    """arcpy.ia.RasterCollection: rasters with attributes, filtered, sorted, grouped, mapped and reduced pixel by
    pixel, as the class page documents. ``fromSTACAPI`` and ``fromSTACCatalog`` are refused: they reach the network,
    and a fenced user tool has none (ADR-223 §5.4)."""

    def __init__(self, rasters, attribute_dict=None):
        from ._raster import Raster, _open_bands
        self._items = []
        if isinstance(rasters, RasterCollection):
            self._items = [dict(i) for i in rasters._items]
            return
        if isinstance(rasters, list) and rasters and isinstance(rasters[0], dict) and "Raster" in rasters[0]:
            self._items = [dict(i) for i in rasters]
            return
        md = isinstance(rasters, _mdim.bind()) or _mdim.is_multidimensional_path(rasters) or (
            isinstance(rasters, Raster) and rasters.isMultidimensional)
        if md:
            if attribute_dict:
                raise ValueError("RasterCollection: attribute_dict is for a list of rasters, as documented.")
            cube = _mdim.cube_of(rasters, "RasterCollection")
            for v in cube.variables.values():
                for i, c in enumerate(v.coords):
                    raster = Raster._of_bands([v.data[i, b] for b in range(v.bands)],
                                              [v.mask[i, b] for b in range(v.bands)], cube.grid, v.band_names)
                    item = {"Raster": raster, "Variable": v.name}
                    for d, value in c.items():
                        item[d] = value.astype("datetime64[s]").astype(_dt.datetime) if isinstance(
                            value, np.datetime64) else value
                    self._items.append(item)
            return
        if isinstance(rasters, (str, os.PathLike)) and not os.fspath(rasters).lower().endswith((".tif", ".tiff")):
            from . import _mosaic
            if _mosaic.exists(rasters):
                raise NotBuiltError("RasterCollection: a mosaic dataset as a collection is not built in grapy; its "
                                    "items are read by grapy's mosaic dataset tools (ADR-230).")
        listed = list(rasters) if isinstance(rasters, (list, tuple)) else [rasters]
        opened = [_open_bands(r, "RasterCollection") for r in listed]
        if len({(round(r.meanCellWidth, 9), round(r.meanCellHeight, 9), r._grid.srid) for r in opened}) > 1:
            raise ValueError("RasterCollection: the rasters of a list must share cell size and spatial reference, as "
                             "documented.")
        attributes = dict(attribute_dict or {})
        for name, values in attributes.items():
            if len(values) != len(opened):
                raise ValueError(f"RasterCollection: attribute '{name}' needs a value per raster.")
        for k, raster in enumerate(opened):
            item = {"Raster": raster, "Name": os.path.splitext(raster.name)[0] if raster.name else f"Raster_{k + 1}"}
            for name, values in attributes.items():
                item[name] = values[k]
            self._items.append(item)

    # ---- what it holds

    @property
    def count(self):
        return len(self._items)

    @property
    def fields(self):
        names = ["Raster"]
        for item in self._items:
            for key in item:
                if key not in names:
                    names.append(key)
        return names

    def __len__(self):
        return len(self._items)

    def __iter__(self):
        return iter([dict(i) for i in self._items])

    def __getitem__(self, index):
        if isinstance(index, slice):
            return RasterCollection(self._items[index])
        return dict(self._items[index])

    def __repr__(self):
        return f"<RasterCollection of {len(self._items)} items>"

    def _new(self, items):
        out = RasterCollection.__new__(RasterCollection)
        out._items = [dict(i) for i in items]
        return out

    def _field(self, field_name):
        for name in self.fields:
            if name.lower() == str(field_name).lower():
                return name
        raise ValueError(f"The collection has no field '{field_name}'; its fields are {', '.join(self.fields)}.")

    # ---- filters

    def filter(self, where_clause=None, query_geometry_or_extent=None):
        """Items matching a SQL where clause on their attributes and intersecting a geometry or extent; with neither,
        a copy of every item. The clause is evaluated by SQLite over the attribute fields."""
        items = self._items
        if where_clause not in (None, ""):
            import sqlite3
            fields = [f for f in self.fields if f != "Raster"]
            db = sqlite3.connect(":memory:")
            try:
                db.execute("CREATE TABLE t (" + ", ".join(['"_k" INTEGER'] + [f'"{f}"' for f in fields]) + ")")
                for k, item in enumerate(items):
                    row = [k] + [_sql(item.get(f)) for f in fields]
                    db.execute("INSERT INTO t VALUES (" + ",".join("?" * len(row)) + ")", row)
                keep = {r[0] for r in db.execute(f'SELECT "_k" FROM t WHERE {where_clause}')}
            except sqlite3.Error as e:
                raise ValueError(f"RasterCollection.filter: the where clause '{where_clause}' is not valid: {e}.")
            finally:
                db.close()
            items = [i for k, i in enumerate(items) if k in keep]
        out = self._new(items)
        if query_geometry_or_extent is not None:
            out = out.filterByGeometry(query_geometry_or_extent)
        return out

    def filterByAttribute(self, field_name, operator, field_values):
        name = self._field(field_name)
        return self._new([i for i in self._items if _test(i.get(name), operator, field_values)])

    def filterByRasterProperty(self, property_name, operator, property_values):
        return self._new([i for i in self._items if _test(getattr(i["Raster"], property_name), operator,
                                                          property_values)])

    def filterByTime(self, start_time=None, end_time=None, time_field_name="StdTime", date_time_format=None):
        """Items whose time is from ``start_time`` to ``end_time``; times as text follow ``date_time_format``
        (``%Y-%m-%dT%H:%M:%S`` by default, as documented).

        INFERRED: both ends are included."""
        if start_time in (None, "") and end_time in (None, ""):
            raise ValueError("filterByTime needs a start time, an end time or both.")
        fmt = date_time_format or "%Y-%m-%dT%H:%M:%S"
        name = self._field(time_field_name)
        lo = _time(start_time, fmt) if start_time not in (None, "") else None
        hi = _time(end_time, fmt) if end_time not in (None, "") else None
        keep = []
        for i in self._items:
            t = _time(i.get(name), fmt if isinstance(i.get(name), str) else None)
            if t is not None and (lo is None or t >= lo) and (hi is None or t <= hi):
                keep.append(i)
        return self._new(keep)

    def filterByCalendarRange(self, calendar_field, start, end=None, time_field_name="StdTime", date_time_format=None):
        """Items whose time's calendar part — HOUR, DAY_OF_WEEK, DAY_OF_MONTH, DAY_OF_YEAR, WEEK_OF_YEAR, MONTH, YEAR or
        QUARTER — is from ``start`` to ``end`` (or equals ``start``).

        INFERRED: DAY_OF_WEEK counts Monday as 1 (ISO 8601), WEEK_OF_YEAR is the ISO week; both ends are included."""
        part = str(calendar_field).upper()
        getters = {"HOUR": lambda t: t.hour, "DAY_OF_WEEK": lambda t: t.isoweekday(), "DAY_OF_MONTH": lambda t: t.day,
                   "DAY_OF_YEAR": lambda t: t.timetuple().tm_yday, "WEEK_OF_YEAR": lambda t: t.isocalendar()[1],
                   "MONTH": lambda t: t.month, "YEAR": lambda t: t.year, "QUARTER": lambda t: (t.month - 1) // 3 + 1}
        if part not in getters:
            raise ValueError(f"filterByCalendarRange: calendar_field must be one of {', '.join(getters)}.")
        name = self._field(time_field_name)
        lo, hi = int(start), int(end if end not in (None, "") else start)
        keep = []
        for i in self._items:
            t = _time(i.get(name), date_time_format if isinstance(i.get(name), str) else None)
            if t is not None and lo <= getters[part](t) <= hi:
                keep.append(i)
        return self._new(keep)

    def filterByGeometry(self, query_geometry_or_extent):
        """Items whose extent intersects a Geometry, an Extent, a Raster or a feature class's features."""
        shape = _shape_of(query_geometry_or_extent)
        from shapely.geometry import box
        keep = []
        for i in self._items:
            e = i["Raster"].extent
            if box(e.XMin, e.YMin, e.XMax, e.YMax).intersects(shape):
                keep.append(i)
        return self._new(keep)

    # ---- fields

    def addField(self, field_name, field_values):
        values = list(field_values)
        if len(values) != len(self._items):
            raise ValueError("addField: a value per item is needed, as documented.")
        return self._new([{**i, str(field_name): v} for i, v in zip(self._items, values)])

    def getFieldValues(self, field_name, max_count=None):
        name = self._field(field_name)
        values = [i.get(name) for i in self._items]
        return values[:int(max_count)] if max_count not in (None, "") else values

    def groupBy(self, field_name):
        name = self._field(field_name)
        groups = {}
        for i in self._items:
            groups.setdefault(i.get(name), []).append(i)
        return {k: self._new(v) for k, v in groups.items()}

    def sort(self, field_name, ascending=True):
        name = self._field(field_name)
        return self._new(sorted(self._items, key=lambda i: (i.get(name) is None, i.get(name)), reverse=not ascending))

    def summarizeField(self, field_name, summary_type="ALL"):
        """The field's COUNT, COUNT_DISTINCT, FIRST, MAX, MEAN, MIN, PRODUCT, SAMPLE_SD, SAMPLE_VAR, SUM, TOTAL_SD and
        TOTAL_VAR, or ALL of them. HISTOGRAM is refused: the page does not give its bins or its form."""
        values = [v for v in self.getFieldValues(field_name) if v is not None]
        kinds = ("COUNT", "COUNT_DISTINCT", "FIRST", "MAX", "MEAN", "MIN", "PRODUCT", "SAMPLE_SD", "SAMPLE_VAR", "SUM",
                 "TOTAL_SD", "TOTAL_VAR")
        wanted = str(summary_type or "ALL").upper()
        if wanted == "HISTOGRAM":
            raise NotBuiltError("summarizeField: HISTOGRAM is not built in grapy: the page gives neither its bins nor "
                                "its form.")
        if wanted != "ALL" and wanted not in kinds:
            raise ValueError(f"summarizeField: summary_type must be one of {', '.join(kinds)}, HISTOGRAM or ALL.")
        numbers = [float(v) for v in values if isinstance(v, (int, float, np.number)) and not isinstance(v, bool)]
        out = {"COUNT": len(values), "COUNT_DISTINCT": len(set(values)), "FIRST": values[0] if values else None,
               "MAX": max(values) if values else None, "MIN": min(values) if values else None}
        if numbers:
            a = np.array(numbers)
            out.update({"MEAN": float(a.mean()), "PRODUCT": float(a.prod()), "SUM": float(a.sum()),
                        "TOTAL_SD": float(a.std()), "TOTAL_VAR": float(a.var()),
                        "SAMPLE_SD": float(a.std(ddof=1)) if a.size > 1 else None,
                        "SAMPLE_VAR": float(a.var(ddof=1)) if a.size > 1 else None})
        return out if wanted == "ALL" else {wanted: out.get(wanted)}

    def merge(self, collection2):
        return self._new(self._items + list(RasterCollection(collection2)._items))

    def selectBands(self, band_ids_or_names):
        out = []
        for i in self._items:
            bands = i["Raster"].getRasterBands(band_ids_or_names)
            bands = bands if isinstance(bands, list) else [bands]
            from ._raster import Raster
            raster = Raster._of_bands([b._array for b in bands], [b._mask for b in bands], bands[0]._grid)
            out.append({**i, "Raster": raster})
        return self._new(out)

    # ---- pixel statistics across the items

    def _stack(self, extent_type, cellsize_type, tool):
        from ._raster import _align
        if not self._items:
            raise ValueError(f"RasterCollection.{tool}: the collection is empty.")
        rasters = [i["Raster"] for i in self._items]
        counts = {r.bandCount for r in rasters}
        if len(counts) > 1:
            raise ValueError(f"RasterCollection.{tool}: every item must have the same bands, as documented.")
        if str(cellsize_type or "FirstOf").lower() not in ("firstof", "lastof", "minof", "maxof", "meanof",
                                                            "intersectionof", "unionof"):
            raise ValueError(f"RasterCollection.{tool}: '{cellsize_type}' is not a cell size type.")
        same = all(r._grid.same(rasters[0]._grid) for r in rasters[1:])
        kind = str(extent_type or "FirstOf").lower()
        if not same and kind != "intersectionof":
            raise NotBuiltError(f"RasterCollection.{tool}: items of different extents are combined over their "
                                "intersection only in grapy (extent_type IntersectionOf); another extent needs NoData "
                                "filling or resampling.")
        bands = []
        grid = None
        for b in range(1, counts.pop() + 1):
            arrays, masks, grid = _align([r._band(b) for r in rasters], tool)
            bands.append((np.stack([a.astype(np.float64) for a in arrays]), np.stack(masks)))
        return bands, grid

    def _statistic(self, method, ignore_nodata, extent_type, cellsize_type):
        from ._mdimtools import reduce
        from ._raster import Raster
        bands, grid = self._stack(extent_type, cellsize_type, method.lower())
        outputs = [reduce(stack[:, None], mask[:, None], method, bool(ignore_nodata)) for stack, mask in bands]
        integer = all(i["Raster"].isInteger for i in self._items) and method in ("MAX", "MIN", "MAJORITY", "SUM")
        arrays = [o[0][0].astype(np.int64) if integer else o[0][0] for o in outputs]
        masks = [o[1][0] for o in outputs]
        if len(arrays) == 1:
            return Raster._of(arrays[0], masks[0], grid)
        return Raster._of_bands(arrays, masks, grid)

    def max(self, ignore_nodata=True, extent_type="FirstOf", cellsize_type="FirstOf"):
        return self._statistic("MAX", ignore_nodata, extent_type, cellsize_type)

    def min(self, ignore_nodata=True, extent_type="FirstOf", cellsize_type="FirstOf"):
        return self._statistic("MIN", ignore_nodata, extent_type, cellsize_type)

    def mean(self, ignore_nodata=True, extent_type="FirstOf", cellsize_type="FirstOf"):
        return self._statistic("MEAN", ignore_nodata, extent_type, cellsize_type)

    def median(self, ignore_nodata=True, extent_type="FirstOf", cellsize_type="FirstOf"):
        return self._statistic("MEDIAN", ignore_nodata, extent_type, cellsize_type)

    def majority(self, ignore_nodata=True, extent_type="FirstOf", cellsize_type="FirstOf"):
        return self._statistic("MAJORITY", ignore_nodata, extent_type, cellsize_type)

    def sum(self, ignore_nodata=True, extent_type="FirstOf", cellsize_type="FirstOf"):
        return self._statistic("SUM", ignore_nodata, extent_type, cellsize_type)

    def std(self, ignore_nodata=True, extent_type="FirstOf", cellsize_type="FirstOf"):
        return self._statistic("STD", ignore_nodata, extent_type, cellsize_type)

    # ---- mosaics

    def mosaic(self, mosaic_method="FIRST"):
        """Every item merged into one raster over the union of their extents: where they overlap, the FIRST or LAST
        item's valid value, or their MEAN, MINIMUM, MAXIMUM or SUM. Items must line up on one cell size."""
        from ._raster import Raster, _Grid
        method = str(mosaic_method or "FIRST").upper()
        if method not in ("FIRST", "LAST", "MEAN", "MINIMUM", "MAXIMUM", "SUM"):
            raise ValueError("mosaic: mosaic_method must be FIRST, LAST, MEAN, MINIMUM, MAXIMUM or SUM.")
        rasters = [i["Raster"] for i in self._items]
        if not rasters:
            raise ValueError("mosaic: the collection is empty.")
        g0 = rasters[0]._grid
        for r in rasters[1:]:
            g = r._grid
            if not (np.isclose(g.width, g0.width) and np.isclose(g.height, g0.height)) or g.srid != g0.srid:
                raise NotBuiltError("mosaic: items of different cell sizes or references need resampling, which is "
                                    "not built in grapy.")
            for offset, size in ((g.left - g0.left, g0.width), (g.top - g0.top, g0.height)):
                if not np.isclose(offset / size, round(offset / size)):
                    raise NotBuiltError("mosaic: items whose cells do not line up need resampling, which is not built "
                                        "in grapy.")
        left = min(r._grid.left for r in rasters)
        top = max(r._grid.top for r in rasters)
        right = max(r._grid.right for r in rasters)
        bottom = min(r._grid.bottom for r in rasters)
        grid = _Grid(left, top, g0.width, g0.height, int(round((top - bottom) / g0.height)),
                     int(round((right - left) / g0.width)), g0.srid)
        count = {r.bandCount for r in rasters}
        if len(count) > 1:
            raise ValueError("mosaic: every item must have the same bands.")
        arrays, masks = [], []
        order = rasters[::-1] if method == "LAST" else rasters
        for b in range(1, count.pop() + 1):
            stack = np.full((len(order), grid.rows, grid.columns), np.nan)
            for k, r in enumerate(order):
                band = r._band(b)
                row0 = int(round((top - r._grid.top) / g0.height))
                col0 = int(round((r._grid.left - left) / g0.width))
                stack[k, row0:row0 + r._grid.rows, col0:col0 + r._grid.columns] = np.where(
                    band._mask, np.nan, band._array.astype(np.float64))
            valid = np.isfinite(stack)
            import warnings
            with warnings.catch_warnings():
                warnings.simplefilter("ignore", RuntimeWarning)
                if method in ("FIRST", "LAST"):
                    first = np.argmax(valid, axis=0)
                    value = np.take_along_axis(stack, first[None], 0)[0]
                else:
                    value = {"MEAN": np.nanmean, "MINIMUM": np.nanmin, "MAXIMUM": np.nanmax,
                             "SUM": np.nansum}[method](stack, axis=0)
            empty = ~valid.any(axis=0)
            integer = all(r.isInteger for r in rasters) and method != "MEAN"
            value = np.where(empty, 0, value)
            arrays.append(value.astype(np.int64) if integer else value)
            masks.append(empty)
        if len(arrays) == 1:
            return Raster._of(arrays[0], masks[0], grid)
        return Raster._of_bands(arrays, masks, grid)

    def qualityMosaic(self, quality_rc_or_list, statistic_type="MAX"):
        """Each pixel taken from the item whose quality raster there is the largest (MAX), smallest (MIN) or the
        median (MEDIAN). INFERRED: the median of an even count is the lower one; a tie takes the first item; items and
        quality rasters share one grid."""
        from ._raster import Raster, _open_bands
        quality = quality_rc_or_list
        rasters_q = [i["Raster"] for i in quality._items] if isinstance(quality, RasterCollection) else [
            _open_bands(q, "qualityMosaic") for q in quality]
        if len(rasters_q) != len(self._items):
            raise ValueError("qualityMosaic: a quality raster is needed per item, as documented.")
        stat = str(statistic_type or "MAX").upper()
        if stat not in ("MAX", "MIN", "MEDIAN"):
            raise ValueError("qualityMosaic: statistic_type must be MAX, MIN or MEDIAN.")
        rasters = [i["Raster"] for i in self._items]
        grid = rasters[0]._grid
        if not all(r._grid.same(grid) for r in rasters + rasters_q):
            raise NotBuiltError("qualityMosaic: items and quality rasters on different grids need resampling, which "
                                "is not built in grapy.")
        q = np.stack([np.where(r._mask, np.nan, r._array.astype(np.float64)) for r in rasters_q])
        filled = np.where(np.isfinite(q), q, -np.inf if stat == "MAX" else np.inf)
        if stat == "MAX":
            pick = np.argmax(filled, axis=0)
        elif stat == "MIN":
            pick = np.argmin(filled, axis=0)
        else:
            import warnings
            with warnings.catch_warnings():
                warnings.simplefilter("ignore", RuntimeWarning)
                med = np.nanpercentile(q, 50, axis=0, method="inverted_cdf")
            pick = np.argmax(q == med, axis=0)
        none = ~np.isfinite(q).any(axis=0)
        arrays, masks = [], []
        for b in range(1, rasters[0].bandCount + 1):
            stack = np.stack([r._band(b)._array for r in rasters])
            mstack = np.stack([r._band(b)._mask for r in rasters])
            value = np.take_along_axis(stack, pick[None], 0)[0]
            empty = np.take_along_axis(mstack, pick[None], 0)[0] | none
            arrays.append(np.where(empty, 0, value))
            masks.append(empty)
        if len(arrays) == 1:
            return Raster._of(arrays[0], masks[0], grid)
        return Raster._of_bands(arrays, masks, grid)

    # ---- processing

    def map(self, func):
        """A new collection of what ``func`` answers for each item: a dict whose ``raster`` key is the item's raster
        and whose other keys are its attributes, as documented."""
        out = []
        for i in self._items:
            answer = func(dict(i))
            if not isinstance(answer, dict) or not any(k.lower() == "raster" for k in answer):
                raise ValueError("RasterCollection.map: the function must answer a dict with a 'raster' key.")
            item = {"Raster": next(v for k, v in answer.items() if k.lower() == "raster")}
            item.update({k: v for k, v in answer.items() if k.lower() != "raster"})
            out.append(item)
        return self._new(out)

    def reduce(self, func, func_args=None):
        return func([i["Raster"] for i in self._items], **dict(func_args or {}))

    def toMultidimensionalRaster(self, variable_field_name, dimension_field_names):
        """A multidimensional raster with a slice per item: its variable from ``variable_field_name`` and its dimension
        values from ``dimension_field_names``. A time field (StdTime, Date, Time or AcquisitionDate) becomes StdTime;
        any other dimension must be a number, as documented."""
        from ._mdim import TIME, Cube, Variable
        names = dimension_field_names if isinstance(dimension_field_names, (list, tuple)) else [dimension_field_names]
        names = [self._field(n) for n in names]
        variable_field = self._field(variable_field_name)
        dims = [TIME if n.lower() in ("stdtime", "date", "time", "acquisitiondate") else n for n in names]
        if len(set(dims)) != len(dims):
            raise ValueError("toMultidimensionalRaster: two fields would both be StdTime.")
        grid = self._items[0]["Raster"]._grid if self._items else None
        groups = {}
        for i in self._items:
            r = i["Raster"]
            if not r._grid.same(grid):
                raise NotBuiltError("toMultidimensionalRaster: items on different grids need resampling, which is not "
                                    "built in grapy.")
            coord = {}
            for n, d in zip(names, dims):
                value = i.get(n)
                coord[d] = _mdim.to_time(_time(value)) if d == TIME else float(value)
            groups.setdefault(str(i.get(variable_field)), []).append((coord, r))
        variables = []
        for name, slices in groups.items():
            bands = {r.bandCount for _, r in slices}
            if len(bands) > 1:
                raise ValueError(f"toMultidimensionalRaster: the slices of '{name}' have different band counts.")
            data = np.stack([np.stack([a.astype(np.float64) for a in r._arrays]) for _, r in slices])
            mask = np.stack([np.stack(r._masks) for _, r in slices])
            variables.append(Variable(name, dims, [c for c, _ in slices], np.where(mask, 0, data), mask, {},
                                      slices[0][1]._names))
        return _mdim.raster_of(Cube(grid, variables))

    @staticmethod
    def fromSTACAPI(*_, **__):
        raise NotBuiltError("RasterCollection.fromSTACAPI is not built in grapy: it searches a STAC API on the network, "
                            "and a fenced user tool has no network (ADR-223 §5.4).")

    @staticmethod
    def fromSTACCatalog(*_, **__):
        raise NotBuiltError("RasterCollection.fromSTACCatalog is not built in grapy: it reads a STAC catalog on the "
                            "network, and a fenced user tool has no network (ADR-223 §5.4).")


def _sql(value):
    if isinstance(value, (_dt.datetime, _dt.date)):
        return value.isoformat(sep=" ") if isinstance(value, _dt.datetime) else value.isoformat()
    if isinstance(value, np.generic):
        return value.item()
    if value is None or isinstance(value, (int, float, str)):
        return value
    return str(value)


def _shape_of(value):
    from shapely.geometry import box
    from ._raster import Raster
    from ._reference import Extent
    if isinstance(value, Extent):
        return box(value.XMin, value.YMin, value.XMax, value.YMax)
    if isinstance(value, Raster):
        e = value.extent
        return box(e.XMin, e.YMin, e.XMax, e.YMax)
    if hasattr(value, "_shape"):
        return value._shape
    if isinstance(value, (str, os.PathLike)):
        from shapely.ops import unary_union
        from . import da
        with da.SearchCursor(value, ["SHAPE@"]) as rows:
            return unary_union([r[0]._shape for r in rows if r[0] is not None])
    raise TypeError(f"filterByGeometry: expected a Geometry, an Extent, a Raster or a feature class, got "
                    f"{type(value).__name__}.")
