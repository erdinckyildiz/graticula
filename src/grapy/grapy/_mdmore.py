"""Multidimensional names refused before ADR-236 gave grapy the model, and built on it now — D-329's sweep (ADR-236).

``Subset`` and ``Foreach`` (arcpy.ia and arcpy.sa), ``SummarizeCategoricalRaster`` (arcpy.ia),
``arcpy.management.ExtractSubDataset`` and ``arcpy.NetCDFFileProperties``. Each was refused with a schedule — *"it
subsets multidimensional rasters … (ADR-224 group 2)"* — or for a reader grapy did not have; ADR-236's Cube and its
netCDF reader answer all five. What their pages leave open is INFERRED and listed in Q-209.
"""
from __future__ import annotations

import inspect
import os

import numpy as np

from . import _mdim
from ._runtime import ExecuteError, NotBuiltError


def _refuse(tool, parameter, value, why):
    raise NotBuiltError(f"{tool}: {parameter} '{value}' is not built in grapy: {why}.")


def _given(value):
    return value not in (None, "", "#")


# ---- Subset


def _dimension_of(variable, key):
    """The variable's dimension a ``dimension_definitions`` key names: by name, ignoring case; else ``time`` names
    StdTime and any other name StdZ, as grapy's reader renames a CF time and vertical coordinate (ADR-236 §5.2;
    INFERRED, Q-209 (a)). None when the variable has no such dimension — its slices are all kept, as the page's
    example says."""
    for d in variable.dims:
        if d.lower() == str(key).lower():
            return d
    if str(key).lower() == "time" and _mdim.TIME in variable.dims:
        return _mdim.TIME
    if str(key).lower() != "time" and "StdZ" in variable.dims:
        return "StdZ"
    return None


def _value(dim, value):
    if dim == _mdim.TIME:
        return _mdim.to_time(value)
    return float(value)


def _matches(dim, values, interval):
    """Which of a dimension's values an interval keeps: a single value, (minimum, maximum) with either None for open,
    (start, end, step, unit) — the window from start to end, repeated every step — or a list of these, as the page
    lists them. INFERRED (Q-209 (a)): bounds are inclusive; an end of None is the start's instant; a step on a
    dimension that is not time is in the dimension's own units and its unit is not read."""
    keys = np.array([_mdim._key(v) for v in values], float)
    if isinstance(interval, list):
        keep = np.zeros(len(values), bool)
        for one in interval:
            keep |= _matches(dim, values, one)
        return keep
    if not isinstance(interval, tuple):
        return np.isclose(keys, _mdim._key(_value(dim, interval)), rtol=0, atol=1e-9)
    if len(interval) == 2:
        lo, hi = interval
        keep = np.ones(len(values), bool)
        if lo is not None:
            keep &= keys >= _mdim._key(_value(dim, lo)) - 1e-9
        if hi is not None:
            keep &= keys <= _mdim._key(_value(dim, hi)) + 1e-9
        return keep
    if len(interval) == 4:
        start, end, step, unit = interval
        if start is None or not _given(step) or float(step) <= 0:
            raise ValueError(f"Subset: the interval {interval!r} needs a start and a positive step.")
        start = _value(dim, start)
        end = start if end is None else _value(dim, end)
        if _mdim._key(end) < _mdim._key(start):
            raise ValueError(f"Subset: the interval {interval!r} ends before it starts.")
        keep = np.zeros(len(values), bool)
        last = keys.max() if len(keys) else 0.0
        k = 0
        while True:
            if dim == _mdim.TIME:
                a, b = _mdim.step(start, float(step) * k, unit), _mdim.step(end, float(step) * k, unit)
            else:
                a, b = start + float(step) * k, end + float(step) * k
            ka, kb = _mdim._key(a), _mdim._key(b)
            if ka > last + 1e-9:
                break
            keep |= (keys >= ka - 1e-9) & (keys <= kb + 1e-9)
            k += 1
        return keep
    raise ValueError(f"Subset: '{interval!r}' is not a value, a (minimum, maximum) pair, a (start, end, step, unit) "
                     "interval or a list of them.")


def subset(cube, variables=None, dimension_definitions=None):
    """A new Cube of the variables named (all when none is) and the slices the definitions keep."""
    chosen = cube.chosen(variables) if _given(variables) else list(cube.variables.values())
    definitions = dict(dimension_definitions or {})
    out = []
    for v in chosen:
        keep = np.ones(len(v.coords), bool)
        for key, interval in definitions.items():
            dim = _dimension_of(v, key)
            if dim is None:
                continue
            values = [c[dim] for c in v.coords]
            keep &= _matches(dim, values, interval)
        if not keep.any():
            continue
        index = np.nonzero(keep)[0]
        out.append(_mdim.Variable(v.name, v.dims, [v.coords[i] for i in index], v.data[index], v.mask[index], v.attrs,
                                  v.band_names, v.dim_attrs))
    if not out:
        raise ExecuteError("ERROR: Subset: no slice of the variables chosen falls within the dimension definitions.")
    return _mdim.Cube(cube.grid, out)


def Subset(in_raster, variables=None, dimension_definitions=None):
    """The variables named — all when none is — and the slices of each that ``dimension_definitions`` keeps, as a new
    multidimensional Raster, temporary until saved, as documented. A variable without a dimension the definitions
    name keeps all its slices, as the page's example says; one left with no slice is left out (INFERRED, Q-209 (a))."""
    cube = _mdim.cube_of(in_raster, "Subset")
    if dimension_definitions is not None and not isinstance(dimension_definitions, dict):
        raise TypeError("Subset: dimension_definitions is a dictionary of dimension name: interval, as documented.")
    return _mdim.raster_of(subset(cube, variables, dimension_definitions))


# ---- Foreach


# Raster function argument names (Raster function objects, as the Foreach page's sample passes them) that are not the
# Python parameter of grapy's function of the same name.
_ALIASES = {"ndvi": {"visiblebandid": "red_band_id", "infraredbandid": "nir_band_id"}}


def _norm(name):
    return str(name).replace("_", "").replace(" ", "").lower()


def _function(name):
    from . import ia
    wanted = _norm(name)
    for candidate in dir(ia):
        if not candidate.startswith("_") and _norm(candidate) == wanted:
            found = getattr(ia, candidate)
            if callable(found) and not isinstance(found, type):
                return candidate, found
    raise ValueError(f"Foreach: '{name}' is not a raster function arcpy.ia names.")


def _call(name, function, slice_raster, arguments):
    """The function on one slice: the slice is its first parameter, and the second too when that is a raster the
    arguments do not give (the page: for Arithmetic, Raster and Raster2 come from in_raster); each argument goes to
    the parameter of its name, ignoring case and underscores, or of its raster function name."""
    signature = inspect.signature(function)
    params = [p for p in signature.parameters.values() if p.kind in (p.POSITIONAL_OR_KEYWORD, p.KEYWORD_ONLY)]
    takes_any = any(p.kind == p.VAR_KEYWORD for p in signature.parameters.values())
    by_norm = {_norm(p.name): p.name for p in params}
    aliases = _ALIASES.get(_norm(name), {})
    kwargs = {}
    raster_slots = {params[0].name} if params else set()
    for key, value in (arguments or {}).items():
        k = _norm(key)
        if k in ("raster", "inraster", "raster1") and params:
            continue
        if k == "raster2" and len(params) > 1:
            continue
        target = by_norm.get(k) or aliases.get(k)
        if target is None and not takes_any:
            raise ValueError(f"Foreach: '{key}' is not an argument of {name}; its arguments are "
                             f"{', '.join(p.name for p in params[1:])}.")
        kwargs[target or key] = value
    if len(params) > 1 and params[1].name not in kwargs and "raster" in params[1].name.lower():
        raster_slots.add(params[1].name)
    for slot in raster_slots:
        kwargs[slot] = slice_raster
    return function(**kwargs)


def Foreach(in_raster, raster_function, raster_function_arguments=None):
    """The raster function applied to every slice of every variable, as documented: the output keeps the input's
    variables and dimensions and is temporary until saved. ``raster_function`` is the name of an arcpy.ia function
    (``NDVI``, ``Slope`` …), its arguments by its Python parameter names or by the raster function argument names the
    page's sample uses (``VisibleBandID``, ``InfraredBandID``).

    Refused by name: a raster function template (``.rft.xml``), Esri's XML whose schema is not published, as
    ConvertRasterFunctionTemplate is refused for it. INFERRED (Q-209 (b)): the slice is the function's first raster
    argument, and its second when that is a raster no argument gives; a function that changes the grid must change
    every slice's the same way."""
    from ._raster import Raster
    tool = "Foreach"
    if isinstance(raster_function, (str, os.PathLike)) and (
            os.fspath(raster_function).lower().endswith((".rft.xml", ".xml", ".rft")) or os.path.isfile(
                os.fspath(raster_function))):
        _refuse(tool, "raster_function", raster_function, "a raster function template (.rft.xml) is Esri's XML, "
                "whose schema is not published")
    if not isinstance(raster_function, str):
        raise TypeError("Foreach: raster_function is the name of a raster function, as documented.")
    name, function = _function(raster_function)
    cube = _mdim.cube_of(in_raster, tool)
    out, grid = [], None
    for v in cube.variables.values():
        data, masks, band_names = [], [], None
        for i in range(len(v.coords)):
            piece = Raster._of_bands([v.data[i, b] for b in range(v.bands)], [v.mask[i, b] for b in range(v.bands)],
                                     cube.grid, v.band_names)
            result = _call(name, function, piece, raster_function_arguments)
            if not isinstance(result, Raster):
                raise ExecuteError(f"ERROR: Foreach: {name} answered {type(result).__name__}, not a raster.")
            if grid is None:
                grid = result._grid
            elif not grid.same(result._grid):
                raise ExecuteError(f"ERROR: Foreach: {name} changed the grid differently from one slice to another.")
            data.append(np.stack([np.asarray(a, np.float64) for a in result._arrays]))
            masks.append(np.stack([np.asarray(m, bool) for m in result._masks]))
            band_names = getattr(result, "_names", None)
        out.append(_mdim.Variable(v.name, v.dims, v.coords, np.stack(data), np.stack(masks), v.attrs, band_names,
                                  v.dim_attrs))
    return _mdim.raster_of(_mdim.Cube(grid or cube.grid, out))


# ---- SummarizeCategoricalRaster


def _class_names(variable):
    """Each class value's name: from the CF ``flag_values`` and ``flag_meanings`` attributes when the variable has
    them (CF 1.8 §3.5, a netCDF raster's counterpart of an attribute table's Class_Name), else ``Class_<value>``
    (INFERRED, Q-209 (c))."""
    names = {}
    values, meanings = variable.attrs.get("flag_values"), variable.attrs.get("flag_meanings")
    if values is not None and meanings:
        flags = [int(round(float(x))) for x in np.atleast_1d(np.asarray(
            str(values).replace(",", " ").split() if isinstance(values, str) else values, float))]
        for value, meaning in zip(flags, str(meanings).split()):
            names[value] = meaning
    return names


def _field(name, taken):
    from . import ValidateFieldName
    base = ValidateFieldName(str(name))
    out, k = base, 1
    while out.lower() in taken:
        k += 1
        out = f"{base}_{k}"
    taken.add(out.lower())
    return out


def SummarizeCategoricalRaster(in_raster, out_table, dimension=None, aoi=None, aoi_id_field=None):
    """The pixel count of each class in each slice of an integer multidimensional raster, as documented, within each
    area of interest when ``aoi`` is given; the classes are the raster's unique values. With ``dimension``, the slices
    are summarised along that dimension; without it, every slice (every combination of dimension values) is.

    INFERRED (Q-209 (c)), the page giving no layout: one row per area and slice (or dimension value), with the area's
    ID (``aoi_id_field``, else its object ID, in ``AOI_ID``), a field per dimension (time as ISO 8601 text) and a field
    per class holding its pixel count, named by its class name (CF ``flag_meanings``) or ``Class_<value>``; with
    ``dimension``, a row's counts are summed over the other dimensions' slices; a pixel is in an area when its centre
    is; the first variable is summarised. A .csv or .txt path writes text, any other a table."""
    import shapely
    from ._mdimtools import _write_table
    tool = "SummarizeCategoricalRaster"
    cube = _mdim.cube_of(in_raster, tool)
    v = cube.variable()
    valid = ~v.mask
    values = v.data[valid]
    if values.size and not np.allclose(values, np.round(values)):
        raise ExecuteError(f"ERROR: {tool}: the input raster must be of integer type, as documented.")
    classes = sorted({int(x) for x in np.round(values)}) if values.size else []
    names = _class_names(v)
    if _given(dimension):
        dim = next((d for d in v.dims if d.lower() == str(dimension).lower()), None)
        if dim is None:
            raise ExecuteError(f"ERROR: {tool}: the variable '{v.name}' has no dimension '{dimension}'.")
        groups = [({dim: c[dim]}, idx) for c, idx in (
            ({dim: value}, [i for i, c in enumerate(v.coords) if _mdim._same(c[dim], value)]) for value in v.values(dim))]
        dims = [dim]
    else:
        groups = [(c, [i]) for i, c in enumerate(v.coords)]
        dims = list(v.dims)
    xs, ys = cube.grid.centres()
    gx, gy = np.meshgrid(xs, ys)
    areas = [(None, np.ones(gx.shape, bool))]
    id_kind = None
    if _given(aoi):
        from . import da
        id_name = str(aoi_id_field) if _given(aoi_id_field) else "OID@"
        areas = []
        with da.SearchCursor(aoi, [id_name, "SHAPE@"]) as rows:
            for key, shape in rows:
                if shape is None:
                    continue
                inside = shapely.contains_xy(shape._shape, gx, gy) | shapely.intersects_xy(
                    shape._shape.boundary, gx, gy)
                areas.append((key, inside))
        id_kind = "String" if any(isinstance(k, str) for k, _ in areas) else "Integer"
    taken = set()
    columns = []
    if id_kind:
        columns.append((_field("AOI_ID" if not _given(aoi_id_field) else aoi_id_field, taken), id_kind))
    for d in dims:
        columns.append((_field(d, taken), "String" if d == _mdim.TIME else "Double"))
    class_fields = [_field(names.get(c, f"Class_{c}" if c >= 0 else f"Class_m{-c}"), taken) for c in classes]
    columns += [(f, "Integer") for f in class_fields]
    rows = []
    for key, inside in areas:
        for coord, indexes in groups:
            counts = []
            stack_values = v.data[indexes, 0][:, inside]
            stack_valid = valid[indexes, 0][:, inside]
            picked = np.round(stack_values[stack_valid]).astype(np.int64)
            for c in classes:
                counts.append(int(np.sum(picked == c)))
            row = [key] if id_kind else []
            row += [_mdim.shown(d, coord[d]) if d == _mdim.TIME else float(coord[d]) for d in dims]
            rows.append(row + counts)
    from ._result import Result
    return Result(_write_table(out_table, columns, rows, tool))


# ---- ExtractSubDataset


def ExtractSubDataset(in_raster, out_raster, subdataset_index=None):
    """The subdatasets chosen of an HDF or NITF file (or any file whose subdatasets GDAL lists) as one raster, their
    bands in order, as documented; without a choice, the first. A file with no subdatasets is its own first.

    INFERRED (Q-209 (d)): the index counts from 1, as GDAL numbers a file's subdatasets and as the page's example
    (``"2"``, ``"5;6;7"``) reads; the subdatasets chosen must share a grid and coordinate system."""
    import rasterio
    from ._raster import Raster, _Grid
    tool = "arcpy.management.ExtractSubDataset"
    path = os.fspath(getattr(in_raster, "catalogPath", None) or in_raster)
    if not os.path.isabs(path):
        from ._runtime import env
        candidate = os.path.join(env.workspace or "", path)
        path = candidate if os.path.exists(candidate) else path
    with rasterio.open(path) as top:
        subs = list(top.subdatasets) or [path]
    if _given(subdataset_index):
        items = subdataset_index if isinstance(subdataset_index, (list, tuple)) else \
            str(subdataset_index).replace(",", ";").split(";")
        chosen = []
        for item in items:
            item = item[0] if isinstance(item, (list, tuple)) else item
            if str(item).strip() in ("", "#"):
                continue
            k = int(str(item).strip())
            if not 1 <= k <= len(subs):
                raise ExecuteError(f"ERROR: {tool}: subdataset {k} is not one of the file's 1 to {len(subs)}.")
            chosen.append(subs[k - 1])
    else:
        chosen = subs[:1]
    arrays, masks, grid = [], [], None
    for sub in chosen:
        with rasterio.open(sub) as source:
            t = source.transform
            if t.b != 0 or t.d != 0:
                raise NotBuiltError(f"{tool}: '{sub}' is rotated; only north-up grids are built in grapy.")
            here = _Grid(t.c, t.f if t.e < 0 else t.f + source.height * t.e, t.a, abs(t.e), source.height,
                         source.width, source.crs.to_epsg() if source.crs else 0)
            if grid is None:
                grid = here
            elif not grid.same(here):
                raise ExecuteError(f"ERROR: {tool}: the subdatasets chosen are on different grids.")
            for b in range(1, source.count + 1):
                band = source.read(b)
                mask = np.zeros(band.shape, bool) if source.nodata is None else band == source.nodata
                if t.e > 0:
                    band, mask = band[::-1], mask[::-1]
                arrays.append(band)
                masks.append(mask)
    from ._result import Result
    return Result(Raster._of_bands(arrays, masks, grid).save(out_raster))


# ---- NetCDFFileProperties


_FIELD_TYPES = {"d": "Double", "f": "Single", "i": "Integer", "h": "SmallInteger", "b": "SmallInteger",
                "c": "String"}


class NetCDFFileProperties:
    """A netCDF file's variables, dimensions and attributes, typed as the file holds them, read with scipy's netCDF
    reader (Unidata's classic format, which grapy writes, ADR-236).

    Refused by value: a netCDF-4 file — it is HDF5, which grapy reads only through GDAL's raster driver, as metadata
    text. INFERRED (Q-209 (e)): getFieldType answers arcpy's field type words (Double, Single, Integer, SmallInteger,
    String), the page naming none; a dimension's type and values are its coordinate variable's, and a dimension
    without one is an Integer index; getAttributeNames with no variable, or an empty one, answers the global
    attributes; getSpatialReference reads the variable's grid mapping (``crs_wkt`` or ``spatial_ref``), a global
    ``crs_wkt``, or longitude and latitude coordinates as WGS 84."""

    def __init__(self, netcdffile):
        path = os.fspath(netcdffile)
        if not os.path.isabs(path) and not os.path.exists(path):
            from ._runtime import env
            path = os.path.join(env.workspace or "", path)
        if not os.path.exists(path):
            raise ExecuteError(f"ERROR 000732: '{netcdffile}' does not exist.")
        with open(path, "rb") as f:
            magic = f.read(4)
        if magic[:3] != b"CDF":
            raise NotBuiltError(f"arcpy.NetCDFFileProperties: '{netcdffile}' is not built in grapy: it is a netCDF-4 "
                                "(HDF5) file, which grapy reads only through GDAL's raster driver, as metadata text, "
                                "not as the typed variables and dimensions this class answers.")
        from scipy.io import netcdf_file
        with netcdf_file(path, "r", mmap=False) as nc:
            self._dimensions = {name: (size if size is not None else nc.variables[name].shape[0]
                                       if name in nc.variables else 0) for name, size in nc.dimensions.items()}
            self._variables = {}
            for name, v in nc.variables.items():
                self._variables[name] = {"dims": list(v.dimensions), "type": v.typecode(),
                                         "attrs": {k: _mdim._text(a) for k, a in v._attributes.items()},
                                         "values": v[:].copy() if len(v.dimensions) == 1 else None}
            self._globals = {k: _mdim._text(a) for k, a in nc._attributes.items()}
        self._path = path

    def _variable(self, name):
        if name not in self._variables:
            raise ValueError(f"NetCDFFileProperties: the file has no variable '{name}'.")
        return self._variables[name]

    def getVariables(self):
        return [n for n in self._variables if n not in self._dimensions]

    def getDimensions(self):
        return list(self._dimensions)

    def getVariablesByDimension(self, dimension_name):
        return [n for n, v in self._variables.items() if dimension_name in v["dims"] and n not in self._dimensions]

    def getDimensionsByVariable(self, variable_name):
        return list(self._variable(variable_name)["dims"])

    def getDimensionSize(self, dimension_name):
        if dimension_name not in self._dimensions:
            raise ValueError(f"NetCDFFileProperties: the file has no dimension '{dimension_name}'.")
        return int(self._dimensions[dimension_name])

    def _coordinate(self, dimension_name):
        size = self.getDimensionSize(dimension_name)
        v = self._variables.get(dimension_name)
        if v is not None and v["values"] is not None:
            return v["values"]
        return np.arange(size)

    def getDimensionValue(self, dimension_name, index):
        values = self._coordinate(dimension_name)
        return values[int(index)].item()

    def getDimensionIndex(self, dimension_name, value):
        values = self._coordinate(dimension_name)
        found = np.nonzero(np.isclose(values.astype(float), float(value), rtol=0, atol=1e-9))[0]
        if not found.size:
            raise ValueError(f"NetCDFFileProperties: '{value}' is not a value of the dimension '{dimension_name}'.")
        return int(found[0])

    def getFieldType(self, name):
        v = self._variables.get(name)
        if v is None:
            if name in self._dimensions:
                return "Integer"
            raise ValueError(f"NetCDFFileProperties: the file has no variable or dimension '{name}'.")
        return _FIELD_TYPES.get(v["type"], "Double")

    def getAttributeNames(self, variable_name=None):
        if not _given(variable_name):
            return list(self._globals)
        names = variable_name if isinstance(variable_name, (list, tuple)) else [variable_name]
        out = []
        for n in names:
            out.extend(k for k in self._variable(n)["attrs"] if k not in out)
        return out

    def getAttributeValue(self, variable_name, attribute_name):
        attrs = self._globals if not _given(variable_name) else self._variable(variable_name)["attrs"]
        if attribute_name not in attrs:
            raise ValueError(f"NetCDFFileProperties: '{variable_name}' has no attribute '{attribute_name}'.")
        value = attrs[attribute_name]
        if isinstance(value, np.ndarray):
            return value.tolist()
        return value.item() if isinstance(value, np.generic) else value

    def getSpatialReference(self, variable_name, x_dimension, y_dimension):
        from ._reference import SpatialReference
        v = self._variable(variable_name)
        mapping = v["attrs"].get("grid_mapping")
        wkt = None
        if mapping and mapping in self._variables:
            attrs = self._variables[mapping]["attrs"]
            wkt = attrs.get("crs_wkt") or attrs.get("spatial_ref")
        wkt = wkt or v["attrs"].get("crs_wkt") or v["attrs"].get("spatial_ref") or self._globals.get("crs_wkt")
        if wkt:
            srid = _mdim._srid_of(wkt)
            if srid:
                return SpatialReference(srid)
            reference = SpatialReference()
            reference.loadFromString(str(wkt))
            return reference
        x, y = str(x_dimension).lower(), str(y_dimension).lower()
        if x.startswith("lon") and y.startswith("lat"):
            return SpatialReference(4326)
        return None

    def __repr__(self):
        return f"<NetCDFFileProperties {self._path}>"


# ---- Complex (ADR-237's complex SAR data)


def Complex(raster):
    """The magnitude of each complex cell, |a + bi| = √(a² + b²), as the page's summary says ("computes the magnitude
    from complex values"); every band, as a temporary raster. ADR-237 gave grapy complex rasters (complex64 GeoTIFF);
    the reason this was refused — *"grapy's rasters hold no complex pixel types"* — no longer held. A raster that is
    not complex is answered as its absolute value, the magnitude of a real number (INFERRED, Q-209 (l))."""
    from ._raster import Raster, as_raster
    source = as_raster(raster, "Complex", bands=True)
    arrays = [np.abs(np.asarray(a)).astype(np.float32 if np.iscomplexobj(a) and np.asarray(a).dtype == np.complex64
                                             else np.float64) for a in source._arrays]
    return Raster._of_bands(arrays, [np.asarray(m).copy() for m in source._masks], source._grid,
                            getattr(source, "_names", None))
