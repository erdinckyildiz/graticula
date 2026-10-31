"""grapy's multidimensional raster — ADR-236.

A multidimensional raster is a set of **variables** over one grid. Each variable has one or more **dimensions**
(``StdTime``, ``StdZ`` or any other name), and is a list of **slices**: one two-dimensional raster — of one band or
several — for each combination of its dimension values. That is the model the Esri pages *An overview of
multidimensional raster data* and the ``Raster`` class describe (``variables``, ``slices``, ``getDimensionNames`` …).

Where it lives on disk (ADR-236 §5.2, INFERRED): **netCDF following the CF conventions** (CF 1.8), an open standard.
grapy writes netCDF-3 (64-bit offset) through scipy, and reads netCDF-3 through scipy and netCDF-4, HDF5 and the other
multidimensional formats GDAL opens through rasterio. Esri's own multidimensional format, CRF, is not published, and a
``.crf`` path is refused by name rather than written in a layout grapy would have to invent.

- **Time** is held as ``numpy.datetime64`` to the second and written as ``days since 1970-01-01 00:00:00`` on the
  standard calendar. A dimension is time when its coordinate's units read ``<unit> since <date>`` (CF §4.4), and it is
  called ``StdTime``, as arcpy calls it. A vertical coordinate (``axis = "Z"`` or ``positive``) is called ``StdZ``.
- **A slice of several bands** (a trend raster's Slope, Intercept and RMSE …) is written with a ``band`` dimension and
  the bands' names in the variable's ``band_names`` attribute (INFERRED: CF has no convention for it).
- **NoData** is ``_FillValue`` (CF §2.5.1), and a NaN.
"""
from __future__ import annotations

import datetime as _datetime
import json
import os
import re

import numpy as np

from ._runtime import NotBuiltError, env
from ._runtime import _is_given  # noqa: E402 — a Raster is never compared with ==

EPOCH = np.datetime64("1970-01-01T00:00:00", "s")
TIME = "StdTime"
_FILL = -3.4028234663852886e38


# ---- time


def to_time(value):
    """A dimension value given as a datetime, a numpy datetime64 or a text date (``2017-1-15``,
    ``2025-01-01T00:00:00``) as ``numpy.datetime64`` to the second."""
    if isinstance(value, np.datetime64):
        return value.astype("datetime64[s]")
    if isinstance(value, _datetime.datetime):
        return np.datetime64(value.replace(tzinfo=None), "s")
    if isinstance(value, _datetime.date):
        return np.datetime64(value, "s")
    text = str(value).strip()
    m = re.fullmatch(r"(\d{1,4})[-/](\d{1,2})[-/](\d{1,2})(?:[T ](\d{1,2}):(\d{1,2})(?::(\d{1,2})(?:\.\d+)?)?)?Z?", text)
    if not m:
        raise ValueError(f"'{value}' is not a date: StdTime values are written YYYY-MM-DDTHH:MM:SS.")
    y, mo, d, h, mi, s = (int(g) if g else 0 for g in m.groups())
    return np.datetime64(_datetime.datetime(y, mo, d, h, mi, s), "s")


def iso(value):
    return str(np.datetime_as_string(np.datetime64(value, "s"), unit="s"))


def days(value):
    """Days since 1970-01-01, as a float."""
    return float((np.datetime64(value, "s") - EPOCH) / np.timedelta64(1, "s")) / 86400.0


def from_days(value):
    return EPOCH + np.timedelta64(int(round(float(value) * 86400.0)), "s")


def parts(value):
    """(year, month, day, hour, day of year, ISO week) of a time."""
    d = np.datetime64(value, "s").astype(_datetime.datetime)
    return d.year, d.month, d.day, d.hour, d.timetuple().tm_yday, d.isocalendar()[1]


def add_months(value, months):
    d = np.datetime64(value, "s").astype(_datetime.datetime)
    total = d.year * 12 + (d.month - 1) + int(months)
    year, month = divmod(total, 12)
    import calendar
    day = min(d.day, calendar.monthrange(year, month + 1)[1])
    return np.datetime64(d.replace(year=year, month=month + 1, day=day), "s")


def step(value, amount, unit):
    """A time moved on by ``amount`` HOURS, DAYS, WEEKS, MONTHS or YEARS."""
    unit = str(unit or "").upper()
    seconds = {"HOURS": 3600, "HOUR": 3600, "DAYS": 86400, "DAY": 86400, "WEEKS": 604800, "WEEK": 604800}
    if unit in seconds:
        return np.datetime64(value, "s") + np.timedelta64(int(round(float(amount) * seconds[unit])), "s")
    if unit in ("MONTHS", "MONTH", "YEARS", "YEAR"):
        months = float(amount) * (12 if unit.startswith("YEAR") else 1)
        if months != int(months):
            raise NotBuiltError(f"A step of {amount} {unit} is not a whole number of months, which grapy does not build.")
        return add_months(value, int(months))
    raise ValueError(f"'{unit}' is not one of HOURS, DAYS, WEEKS, MONTHS or YEARS.")


def decimal_year(value):
    """A time as a decimal year: the year plus the fraction of it gone (2015-07-02T12:00 is 2015.5)."""
    d = np.datetime64(value, "s").astype(_datetime.datetime)
    start = _datetime.datetime(d.year, 1, 1)
    length = (_datetime.datetime(d.year + 1, 1, 1) - start).total_seconds()
    return d.year + (d - start).total_seconds() / length


# ---- the model


class Variable:
    """One variable: its dimension names, a coordinate (a dict of dimension values) per slice, and the slices' cells as
    an array of shape (slices, bands, rows, columns) with a NoData mask of the same shape."""

    def __init__(self, name, dims, coords, data, mask, attrs=None, band_names=None, dim_attrs=None):
        self.name, self.dims = str(name), list(dims)
        self.coords = [dict(c) for c in coords]
        self.data = np.asarray(data)
        if self.data.ndim == 3:
            self.data = self.data[:, None]
        self.mask = np.asarray(mask, bool).reshape(self.data.shape)
        self.attrs = dict(attrs or {})
        self.band_names = list(band_names) if band_names else None
        self.dim_attrs = {k: dict(v) for k, v in (dim_attrs or {}).items()}

    @property
    def bands(self):
        return self.data.shape[1]

    def names_of_bands(self):
        return self.band_names or [f"Band_{k}" for k in range(1, self.bands + 1)]

    def values(self, dim):
        """The distinct values of a dimension, in order."""
        found = []
        for c in self.coords:
            if not any(_same(c[dim], f) for f in found):
                found.append(c[dim])
        return sorted(found, key=_key)

    def copy(self, name=None):
        return Variable(name or self.name, self.dims, self.coords, self.data.copy(), self.mask.copy(), self.attrs,
                        self.band_names, self.dim_attrs)

    def along(self, dim):
        """The slices grouped by the values of the other dimensions: [(other values, [slice indexes in order of
        ``dim``])]."""
        if dim not in self.dims:
            raise ValueError(f"The variable '{self.name}' has no dimension '{dim}'; its dimensions are "
                             f"{', '.join(self.dims)}.")
        groups = {}
        for i, c in enumerate(self.coords):
            other = tuple((d, _key(c[d])) for d in self.dims if d != dim)
            groups.setdefault(other, []).append(i)
        out = []
        for other, indexes in groups.items():
            indexes.sort(key=lambda i: _key(self.coords[i][dim]))
            out.append((dict((d, self.coords[indexes[0]][d]) for d, _ in other), indexes))
        return out


def _key(value):
    if isinstance(value, np.datetime64):
        return days(value)
    return float(value)


def _same(a, b):
    return _key(a) == _key(b)


class Cube:
    """Variables on one grid."""

    def __init__(self, grid, variables):
        self.grid = grid
        self.variables = {v.name: v for v in variables}

    def variable(self, name=None):
        if name is None:
            return next(iter(self.variables.values()))
        for key, v in self.variables.items():
            if key.lower() == str(name).lower():
                return v
        raise ValueError(f"The multidimensional raster has no variable '{name}'; its variables are "
                         f"{', '.join(self.variables)}.")

    def chosen(self, variables, dim=None):
        """The variables named (a list, or a ``;``-separated text), or every variable that has ``dim`` when none
        is named."""
        names = _names(variables)
        if names:
            return [self.variable(n) for n in names]
        found = [v for v in self.variables.values() if dim is None or dim in v.dims]
        if not found:
            raise ValueError(f"No variable of the multidimensional raster has the dimension '{dim}'.")
        return found


def _names(value):
    if not _is_given(value):
        return []
    if isinstance(value, (list, tuple)):
        return [str(v) for v in value if v not in (None, "")]
    return [p.strip() for p in str(value).split(";") if p.strip()]


def dim_value(dim, value):
    """A dimension value as the model holds it: a time for StdTime, a float otherwise."""
    return to_time(value) if dim == TIME else float(value)


def shown(dim, value):
    return iso(value) if isinstance(value, np.datetime64) else value


# ---- reading


def is_multidimensional_path(value):
    return isinstance(value, (str, os.PathLike)) and os.fspath(value).lower().endswith((".nc", ".nc4", ".cdf",
                                                                                       ".h5", ".hdf", ".grib", ".grb2"))


def _path(value):
    path = os.fspath(value)
    if not os.path.isabs(path) and not os.path.exists(path):
        path = os.path.join(env.workspace, path)
    return path


def refuse_crf(path, what):
    if os.fspath(path).lower().endswith(".crf"):
        raise NotBuiltError(f"{what}: '{path}' is a Cloud Raster Format (CRF) path. CRF is Esri's format and its layout is "
                            "not published; grapy holds a multidimensional raster as netCDF-CF — give a .nc path "
                            "(ADR-236 §5.2).")


def read(value):
    """A Cube from a netCDF (or other GDAL multidimensional) file."""
    refuse_crf(value, "Raster")
    path = _path(value)
    with open(path, "rb") as f:
        magic = f.read(4)
    if magic[:3] == b"CDF":
        return _read_netcdf3(path)
    return _read_gdal(path)


def _cf_time(units):
    m = re.fullmatch(r"\s*(days|day|d|hours|hour|h|minutes|minute|min|seconds|second|sec|s)\s+since\s+(.+?)\s*", units or "",
                     re.I)
    if not m:
        return None
    factor = {"d": 86400, "h": 3600, "m": 60, "s": 1}[m.group(1)[0].lower()]
    start = re.match(r"[\d\-/]+(?:[T ]\d[\d:.]*)?", m.group(2).strip())
    if not start:
        return None
    origin = to_time(start.group(0).rstrip("."))
    return factor, origin


def _decode(name, values, attrs):
    """A coordinate's values as the model holds them, and the dimension's arcpy name."""
    units = attrs.get("units", "")
    if isinstance(units, bytes):
        units = units.decode()
    calendar = attrs.get("calendar", "standard")
    if isinstance(calendar, bytes):
        calendar = calendar.decode()
    time = _cf_time(units)
    if time:
        if str(calendar).lower() not in ("standard", "gregorian", "proleptic_gregorian"):
            raise NotBuiltError(f"'{name}' is on the {calendar} calendar; grapy reads the standard calendar only.")
        factor, origin = time
        return TIME, [origin + np.timedelta64(int(round(float(v) * factor)), "s") for v in values]
    axis = str(attrs.get("axis", b"") if not isinstance(attrs.get("axis"), bytes) else attrs["axis"].decode()).upper()
    if axis == "Z" or "positive" in attrs:
        return "StdZ", [float(v) for v in values]
    return name, [float(v) for v in values]


def _text(value):
    if isinstance(value, bytes):
        return value.decode("utf-8", "replace")
    if isinstance(value, np.ndarray) and value.size == 1:
        return value.item()
    return value


def _grid_from(xs, ys, srid):
    from ._raster import _Grid
    xs, ys = np.asarray(xs, float), np.asarray(ys, float)
    if xs.size < 2 or ys.size < 2:
        raise NotBuiltError("A multidimensional raster of fewer than two columns or rows is not built in grapy.")
    width, height = (xs[-1] - xs[0]) / (xs.size - 1), abs(ys[-1] - ys[0]) / (ys.size - 1)
    if not (np.allclose(np.diff(xs), width) and np.allclose(np.abs(np.diff(ys)), height)):
        raise NotBuiltError("A multidimensional raster whose x or y coordinates are not evenly spaced is not built in grapy.")
    top = max(ys[0], ys[-1]) + height / 2
    return _Grid(xs[0] - width / 2, top, width, height, ys.size, xs.size, srid)


def _srid_of(wkt):
    if not wkt:
        return 0
    try:
        from pyproj import CRS
        return CRS.from_user_input(wkt).to_epsg() or 0
    except Exception:  # noqa: BLE001 — a reference grapy cannot name by a code is left unknown
        return 0


def _read_netcdf3(path):
    from scipy.io import netcdf_file
    variables = []
    with netcdf_file(path, "r", mmap=False) as nc:
        names = {k.lower(): k for k in nc.variables}
        xname = next((names[k] for k in ("x", "lon", "longitude") if k in names), None)
        yname = next((names[k] for k in ("y", "lat", "latitude") if k in names), None)
        if xname is None or yname is None:
            raise NotBuiltError(f"'{path}' has no x and y (or longitude and latitude) coordinate variables.")
        xs, ys = nc.variables[xname][:].copy(), nc.variables[yname][:].copy()
        srid = 0
        for v in nc.variables.values():
            wkt = _text(getattr(v, "spatial_ref", None) or getattr(v, "crs_wkt", None) or b"")
            if wkt:
                srid = _srid_of(wkt)
                break
        if not srid and xname.lower().startswith("lon"):
            srid = 4326
        transform = None
        for v in nc.variables.values():
            text = _text(getattr(v, "GeoTransform", None) or b"")
            if text:
                transform = [float(p) for p in str(text).split()]
                break
        if transform and transform[2] == 0 and transform[4] == 0 and transform[5] < 0:
            from ._raster import _Grid
            grid = _Grid(transform[0], transform[3], transform[1], -transform[5], ys.size, xs.size, srid)
        else:
            grid = _grid_from(xs, ys, srid)
        flip = ys.size > 1 and ys[0] < ys[-1]
        coordinates = {}
        for dname in nc.dimensions:
            if dname in nc.variables and dname not in (xname, yname):
                cv = nc.variables[dname]
                coordinates[dname] = _decode(dname, cv[:].copy(), dict(cv._attributes))
        for name, v in nc.variables.items():
            dims = list(v.dimensions)
            if len(dims) < 2 or dims[-2:] != [nc.variables[yname].dimensions[0], nc.variables[xname].dimensions[0]]:
                continue
            attrs = {k: _text(a) for k, a in v._attributes.items()}
            data = v[:].copy().astype(np.float64)
            fill = attrs.pop("_FillValue", attrs.pop("missing_value", None))
            outer = dims[:-2]
            band_names = None
            if outer and outer[-1] == "band":
                band_names = str(attrs.pop("band_names", "")).split(",") or None
                outer = outer[:-1]
            else:
                data = data[..., None, :, :]
            arcpy_dims, axes = [], []
            for d in outer:
                if d not in coordinates:
                    coordinates[d] = (d, [float(i) for i in range(nc.dimensions[d])])
                arcpy_dims.append(coordinates[d][0])
                axes.append(coordinates[d][1])
            count = int(np.prod([len(a) for a in axes])) if axes else 1
            data = data.reshape((count,) + data.shape[-3:])
            mask = ~np.isfinite(data)
            if fill is not None:
                mask |= data == float(fill)
            if flip:
                data, mask = data[..., ::-1, :], mask[..., ::-1, :]
            coords = [dict(zip(arcpy_dims, combo)) for combo in _product(axes)]
            dim_attrs = {}
            for d in outer:
                if d in nc.variables:
                    dim_attrs[coordinates[d][0]] = {k: _text(a) for k, a in nc.variables[d]._attributes.items()
                                                    if k not in ("units",) or coordinates[d][0] != TIME}
            for key in ("grid_mapping", "coordinates"):
                attrs.pop(key, None)
            variables.append(Variable(name, arcpy_dims or ["Band"], coords if axes else [{"Band": 1.0}],
                                      np.where(mask, 0, data), mask, attrs, band_names, dim_attrs))
    if not variables:
        raise NotBuiltError(f"'{path}' holds no variable over its x and y coordinates.")
    return Cube(grid, variables)


def _product(axes):
    if not axes:
        return [()]
    out = [()]
    for axis in axes:
        out = [o + (v,) for o in out for v in axis]
    return out


def _read_gdal(path):
    """A netCDF-4, HDF5 or GRIB file through GDAL: each subdataset a variable, each band a slice, its dimension values
    in GDAL's NETCDF_DIM_* metadata."""
    import rasterio
    with rasterio.open(path) as top:
        subs = list(top.subdatasets) or [path]
    variables, grid = [], None
    for sub in subs:
        with rasterio.open(sub) as source:
            t = source.transform
            if t.b != 0 or t.d != 0:
                raise NotBuiltError(f"'{sub}' is rotated; only north-up grids are built in grapy.")
            from ._raster import _Grid
            here = _Grid(t.c, t.f if t.e < 0 else t.f + source.height * t.e, t.a, abs(t.e), source.height,
                         source.width, source.crs.to_epsg() if source.crs else 0)
            if grid is None:
                grid = here
            elif not grid.same(here):
                continue
            tags = source.tags()
            extra = [d for d in tags.get("NETCDF_DIM_EXTRA", "{}").strip("{}").split(",") if d]
            name = (source.tags(1).get("NETCDF_VARNAME") if source.count else None) or tags.get("NETCDF_VARNAME") or                 os.path.basename(sub.split(":")[-1])
            coords, arrays, masks = [], [], []
            dims = []
            for d in extra:
                dims.append(_decode(d, [0], {k.split("#", 1)[1]: v for k, v in tags.items()
                                             if k.startswith(d + "#")})[0])
            for b in range(1, source.count + 1):
                bt = source.tags(b)
                coord = {}
                for d, arcpy_name in zip(extra, dims):
                    attrs = {k.split("#", 1)[1]: v for k, v in tags.items() if k.startswith(d + "#")}
                    coord[arcpy_name] = _decode(d, [float(bt[f"NETCDF_DIM_{d}"])], attrs)[1][0]
                coords.append(coord or {"Band": float(b)})
                band = source.read(b).astype(np.float64)
                mask = ~np.isfinite(band)
                if source.nodata is not None:
                    mask |= band == source.nodata
                if t.e > 0:
                    band, mask = band[::-1], mask[::-1]
                arrays.append(np.where(mask, 0, band))
                masks.append(mask)
            variables.append(Variable(name, dims or ["Band"], coords, np.stack(arrays), np.stack(masks)))
    if not variables:
        raise NotBuiltError(f"'{path}' holds no raster variable GDAL can read.")
    return Cube(grid, variables)


# ---- writing


def netcdf3(path, dimensions, variables, attributes=None):
    """grapy's one netCDF writer (ADR-236, and ADR-238's voxels through it): a netCDF-3 file (64-bit offset) written
    by scipy, as Unidata's classic format defines it. ``dimensions`` is [(name, coordinate values, {attribute: value})],
    each a dimension with a double coordinate variable of the same name; ``variables`` is [(name, dimension names,
    array, {attribute: value})], where ``_FillValue`` is written in the variable's own type; ``attributes`` are the
    global ones. Text that is not ASCII is written as its UTF-8 bytes, as netCDF-3 holds char attributes."""
    from scipy.io import netcdf_file
    if os.path.exists(path):
        os.remove(path)
    with netcdf_file(path, "w", version=2) as nc:
        for key, value in (attributes or {}).items():
            setattr(nc, key, _attr(value))
        for name, values, attrs in dimensions:
            nc.createDimension(name, len(values))
            cv = nc.createVariable(name, "d", (name,))
            cv[:] = np.asarray(values, float)
            for key, value in (attrs or {}).items():
                setattr(cv, key, _attr(value))
        for name, dims, data in ((n, tuple(d), a) for n, d, a, _ in variables if n is None):
            nc.createDimension(dims[0], data)  # a dimension with no coordinate variable (a band count)
        for name, dims, data, attrs in variables:
            if name is None:
                continue
            data = np.asarray(data)
            kind = {"f": "f" if data.dtype == np.float32 else "d", "i": "i", "u": "i", "b": "b"}[data.dtype.kind]
            out = nc.createVariable(name, kind, tuple(dims))
            if data.ndim:  # a scalar (a grid-mapping variable) carries only its attributes
                out[:] = data
            for key, value in (attrs or {}).items():
                if key == "_FillValue":
                    value = out.data.dtype.type(value)
                setattr(out, key, _attr(value))
    return path


def write(cube, path):
    """The cube as netCDF-3 (64-bit offset) following CF 1.8, through ``netcdf3``."""
    refuse_crf(path, "save")
    path = _path(path)
    if not os.path.splitext(path)[1]:
        path += ".nc"
    if os.path.splitext(path)[1].lower() != ".nc":
        raise NotBuiltError(f"save: grapy writes a multidimensional raster as netCDF (.nc), not '{path}'.")
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    g = cube.grid
    xs, ys = g.centres()
    axes = {}
    for v in cube.variables.values():
        for d in v.dims:
            for value in v.values(d):
                axes.setdefault(d, [])
                if not any(_same(value, f) for f in axes[d]):
                    axes[d].append(value)
    for d in axes:
        axes[d].sort(key=_key)
    dimensions = [("x", xs, {"standard_name": "projection_x_coordinate", "axis": "X"}),
                  ("y", ys, {"standard_name": "projection_y_coordinate", "axis": "Y"})]
    for d, values in axes.items():
        attrs = {}
        if d == TIME:
            coordinate = [days(v) for v in values]
            attrs.update(units="days since 1970-01-01 00:00:00", calendar="standard", standard_name="time", axis="T")
        else:
            coordinate = values
            if d == "StdZ":
                attrs.update(axis="Z", positive="up")
        for variable in cube.variables.values():
            for key, a in variable.dim_attrs.get(d, {}).items():
                if key not in ("units", "calendar", "axis", "standard_name") and isinstance(a, (str, int, float)):
                    attrs[key] = a
        dimensions.append((d, coordinate, attrs))
    crs = {"GeoTransform": f"{g.left!r} {g.width!r} 0 {g.top!r} 0 {-g.height!r}"}
    if g.srid:
        from pyproj import CRS
        reference = CRS.from_epsg(g.srid)
        crs.update(spatial_ref=reference.to_wkt(version="WKT1_GDAL"), crs_wkt=reference.to_wkt())
    variables = [("crs", (), np.int32(0), crs)]
    bands = {v.bands for v in cube.variables.values() if v.bands > 1}
    for b in bands:
        variables.append((None, (f"band{b}" if len(bands) > 1 else "band",), b, {}))
    for v in cube.variables.values():
        shape = [len(axes[d]) for d in v.dims]
        full = np.full(shape + [v.bands, g.rows, g.columns], _FILL, np.float32 if _fits32(v) else np.float64)
        for i, c in enumerate(v.coords):
            index = tuple(next(k for k, f in enumerate(axes[d]) if _same(c[d], f)) for d in v.dims)
            full[index] = np.where(v.mask[i], _FILL, v.data[i])
        dims = tuple(v.dims)
        if v.bands > 1:
            dims += ((f"band{v.bands}" if len(bands) > 1 else "band"),)
        else:
            full = full[..., 0, :, :]
        attrs = {"_FillValue": _FILL, "grid_mapping": "crs"}
        if v.bands > 1:
            attrs["band_names"] = ",".join(v.names_of_bands())
        for key, a in v.attrs.items():
            if isinstance(a, (str, int, float)) and key not in ("_FillValue",):
                attrs[key] = a
        variables.append((v.name, dims + ("y", "x"), full, attrs))
    return netcdf3(path, dimensions, variables, {"Conventions": "CF-1.8", "source": "grapy (Graticula), ADR-236"})


def _attr(value):
    """An attribute as netCDF-3 holds it: text that is not ASCII as its UTF-8 bytes (NUG: char attributes)."""
    if isinstance(value, str) and not value.isascii():
        return value.encode("utf-8")
    return value


def _fits32(variable):
    values = variable.data[~variable.mask]
    return values.size == 0 or (np.allclose(values.astype(np.float32), values, rtol=0, atol=0)
                                if np.issubdtype(values.dtype, np.integer) else values.dtype == np.float32)


# ---- the Raster of a multidimensional dataset


def open_into(raster, value, is_multidimensional):
    """Raster(path) on a multidimensional file. With ``is_multidimensional`` the Raster is the whole dataset (every
    slice of every variable); without it, a plain raster of the first variable's first slice (INFERRED: the page says
    only that True processes every slice), which still says ``isMultidimensional``."""
    from ._raster import Raster
    cube = read(value)
    path = _path(value)
    if is_multidimensional:
        raster.__class__ = bind()
        raster._cube = cube
    else:
        v = cube.variable()
        raster._arrays = [v.data[0, b] for b in range(v.bands)]
        raster._masks = [v.mask[0, b] for b in range(v.bands)]
        raster._names = v.band_names
    raster._grid = cube.grid
    raster._md_source = path
    raster.catalogPath, raster.isTemporary = path, False
    assert isinstance(raster, Raster)


def cube_of(value, tool):
    """The Cube a tool works on: a multidimensional Raster, a Raster opened from a multidimensional file, or a path."""
    from ._raster import Raster
    if isinstance(value, bind()):
        return value._cube
    if isinstance(value, Raster):
        source = getattr(value, "_md_source", None)
        if source:
            return read(source)
        raise ValueError(f"{tool}: '{value!r}' is not a multidimensional raster.")
    if isinstance(value, (str, os.PathLike)):
        return read(value)
    raise TypeError(f"{tool}: expected a multidimensional raster, got {type(value).__name__}.")


def raster_of(cube):
    """A temporary multidimensional Raster holding a cube."""
    from ._raster import Raster
    raster = Raster.__new__(bind())
    raster._cube, raster._grid = cube, cube.grid
    raster._md_source = None
    raster.catalogPath, raster.isTemporary = None, True
    return raster


def _class():
    from ._raster import Raster

    class MultidimensionalRasterClass(Raster):
        """A Raster that is a whole multidimensional dataset: ``Raster(path, True)``, or what a multidimensional tool
        answers. It is not a two-dimensional array: map algebra and the single-raster tools refuse it, as no page says
        which slice they would read; the multidimensional tools take it."""

        _md_source = None

        @property
        def _arrays(self):
            raise NotBuiltError(f"{self!r} is a multidimensional raster of {len(self.slices)} slices; map algebra and "
                                "the single-raster tools on every slice are not built in grapy (take a slice with "
                                "Raster(path) or use the multidimensional tools).")

        @_arrays.setter
        def _arrays(self, value):
            pass

        @property
        def _masks(self):
            return self._arrays

        @_masks.setter
        def _masks(self, value):
            pass

        @property
        def isMultidimensional(self):
            return True

        @property
        def bandCount(self):
            return self._cube.variable().bands

        @property
        def bandNames(self):
            return self._cube.variable().names_of_bands()

        @property
        def pixelType(self):
            return "F64"

        @property
        def noDataValue(self):
            return _FILL

        @property
        def variableNames(self):
            return list(self._cube.variables)

        @property
        def variables(self):
            out = []
            for v in self._cube.variables.values():
                out.append(f"{v.name}(" + ",".join(f"{d}={len(v.values(d))}" for d in v.dims) + ")")
            return out

        @property
        def slices(self):
            return [{"variable": v.name, **{d: shown(d, c[d]) for d in v.dims}}
                    for v in self._cube.variables.values() for c in v.coords]

        @property
        def mdinfo(self):
            """The variables, their description and unit, and each dimension's values, as JSON text (INFERRED: the
            page's example shows the ``variables`` list and ``layout`` key only; the fields inside are grapy's)."""
            out = []
            for v in self._cube.variables.values():
                dims = []
                for d in v.dims:
                    values = v.values(d)
                    dims.append({"name": d, "unit": self.getDimensionAttributes(v.name, d).get("unit", ""),
                                 "values": [shown(d, x) for x in values], "count": len(values)})
                out.append({"name": v.name, "description": v.attrs.get("long_name", ""), "unit": v.attrs.get("units", ""),
                            "dimensions": dims})
            return json.dumps({"variables": out, "layout": 1})

        @property
        def hasTranspose(self):
            return False

        def getDimensionNames(self, variable_name):
            return list(self._cube.variable(variable_name).dims)

        def getDimensionValues(self, variable_name, dimension_name):
            v = self._cube.variable(variable_name)
            if dimension_name not in v.dims:
                raise ValueError(f"The variable '{variable_name}' has no dimension '{dimension_name}'.")
            return [shown(dimension_name, x) for x in v.values(dimension_name)]

        def getDimensionAttributes(self, variable_name, dimension_name):
            """Minimum, maximum and count of a dimension's values, its unit, and for time the interval when the
            values are evenly spaced (INFERRED key names; the page names the contents but not the keys)."""
            v = self._cube.variable(variable_name)
            values = v.values(dimension_name)
            out = dict(v.dim_attrs.get(dimension_name, {}))
            out.update({"Minimum": shown(dimension_name, values[0]), "Maximum": shown(dimension_name, values[-1]),
                        "Count": len(values)})
            if dimension_name == TIME:
                out.setdefault("unit", "ISO8601")
                gaps = np.diff([days(x) for x in values])
                if gaps.size and np.allclose(gaps, gaps[0]):
                    out["Interval"], out["IntervalUnit"] = float(gaps[0]), "Days"
            return out

        def getVariableAttributes(self, variable_name):
            v = self._cube.variable(variable_name)
            out = {k: a for k, a in v.attrs.items()}
            out.setdefault("Description", v.attrs.get("long_name", ""))
            out.setdefault("Unit", v.attrs.get("units", ""))
            return out

        def setVariableAttributes(self, variable_name, variable_attributes):
            v = self._cube.variable(variable_name)
            v.attrs = dict(variable_attributes)
            return dict(v.attrs)

        def renameVariable(self, current_variable_name, new_variable_name):
            v = self._cube.variable(current_variable_name)
            if str(new_variable_name) in self._cube.variables:
                raise ValueError(f"The raster already has a variable '{new_variable_name}'.")
            self._cube.variables = {(str(new_variable_name) if k == v.name else k): x
                                    for k, x in self._cube.variables.items()}
            v.name = str(new_variable_name)
            return list(self._cube.variables)

        def removeVariables(self, variable_names):
            for name in _names(variable_names):
                self._cube.variables.pop(self._cube.variable(name).name)
            if not self._cube.variables:
                raise ValueError("A multidimensional raster needs at least one variable.")
            return list(self._cube.variables)

        def appendSlices(self, mdRaster):
            """The other raster's slices added, as documented: the same variables, dimension names and grid."""
            other = cube_of(mdRaster, "appendSlices")
            if not other.grid.same(self._cube.grid):
                raise ValueError("appendSlices: the cell size, extent and spatial reference must match.")
            if set(other.variables) != set(self._cube.variables):
                raise ValueError("appendSlices: the variables must match.")
            for name, v in self._cube.variables.items():
                o = other.variables[name]
                if o.dims != v.dims or o.bands != v.bands:
                    raise ValueError(f"appendSlices: the dimensions of '{name}' must match.")
                v.coords += o.coords
                v.data = np.concatenate([v.data, o.data])
                v.mask = np.concatenate([v.mask, o.mask])
            return ";".join(self.variables)

        def getStatistics(self, variable_name=None):
            v = self._cube.variable(variable_name)
            values = v.data[~v.mask]
            if not values.size:
                return {}
            return {"min": float(values.min()), "max": float(values.max()), "mean": float(values.mean()),
                    "standardDeviation": float(values.std())}

        def computeStatistics(self, variable=None, aoi=None, cellsize=None):
            if aoi is not None or cellsize is not None:
                raise NotBuiltError("computeStatistics: aoi and cellsize are not built in grapy.")
            names = _names(variable) or list(self._cube.variables)
            return [self.getStatistics(n) for n in names]

        def read(self, upper_left_corner=None, origin_coordinate=None, ncols=None, nrows=None, nodata_to_value=None,
                 cell_size=None):
            """The first variable's cells as an array of (slices, rows, columns, bands), as documented for a
            multidimensional raster. A window is refused by name."""
            if any(x is not None for x in (upper_left_corner, origin_coordinate, ncols, nrows, cell_size)):
                raise NotBuiltError("Raster.read: a window of a multidimensional raster is not built in grapy.")
            v = self._cube.variable()
            fill = _FILL if nodata_to_value is None else nodata_to_value
            return np.moveaxis(np.where(v.mask, fill, v.data), 1, -1)

        def getRasterInfo(self):
            from ._raster import RasterInfo
            info = RasterInfo()
            info.setBandCount(self.bandCount)
            info.setCellSize((self._grid.width, self._grid.height))
            info.setExtent(self.extent)
            info.setPixelType("F64")
            if self.spatialReference is not None:
                info.setSpatialReference(self.spatialReference)
            return info

        def save(self, name=None):
            """Writes the dataset as netCDF-CF; a name without an extension is given ``.nc``, and a ``.crf`` is
            refused (ADR-236 §5.2)."""
            if name is None:
                if not self.isTemporary:
                    return self.catalogPath
                raise ValueError("A temporary raster needs a name to be saved under.")
            path = write(self._cube, name)
            self.catalogPath, self.isTemporary, self._md_source = path, False, path
            return path

        def __repr__(self):
            return self.catalogPath or "<temporary multidimensional Raster>"

    return MultidimensionalRasterClass


MultidimensionalRaster = None  # set by _raster after Raster exists


def bind():
    global MultidimensionalRaster
    if MultidimensionalRaster is None:
        MultidimensionalRaster = _class()
    return MultidimensionalRaster
