"""arcpy's Raster object and the grid it lives on — ADR-224, the raster half of group 1.

A Raster is a two-dimensional array, a mask of NoData cells, a north-up affine grid (top-left corner and square or
rectangular cells) and a reference. It is read from and saved to GeoTIFF through rasterio. Map algebra on it follows
arcpy's documented rules:

- **NoData in, NoData out.** A cell that is NoData in any input of an operator is NoData in the output.
- **Several rasters are combined over the intersection of their extents**, arcpy's default for map algebra, when
  their cells line up: the same cell size and origins a whole number of cells apart. Grids that do not line up need
  resampling, which is not built, and are refused by name.
- **Integer stays integer** where arcpy's operator keeps it (+, -, *, //, %, comparisons and Boolean operators), and
  so does ``/`` of two integers: Esri's Divide page says *"If both inputs are integers, an integer division is
  performed … if 3 is divided by 2, the output is 1."* INFERRED: it truncates toward zero, so −3 / 2 is −1.
"""
from __future__ import annotations

import os

import numpy as np

from ._reference import SpatialReference
from ._runtime import NotBuiltError, env


class _Grid:
    """Where a raster's cells are: top-left corner, cell width and height, rows and columns, and an EPSG code."""

    __slots__ = ("left", "top", "width", "height", "rows", "columns", "srid")

    def __init__(self, left, top, width, height, rows, columns, srid):
        self.left, self.top = float(left), float(top)
        self.width, self.height = float(width), float(height)
        self.rows, self.columns, self.srid = int(rows), int(columns), int(srid or 0)

    @property
    def right(self):
        return self.left + self.columns * self.width

    @property
    def bottom(self):
        return self.top - self.rows * self.height

    def same(self, other):
        return (self.rows, self.columns, self.srid) == (other.rows, other.columns, other.srid) and np.allclose(
            [self.left, self.top, self.width, self.height], [other.left, other.top, other.width, other.height])

    def centres(self):
        """The x of each column's centre and the y of each row's centre."""
        xs = self.left + (np.arange(self.columns) + 0.5) * self.width
        ys = self.top - (np.arange(self.rows) + 0.5) * self.height
        return xs, ys

    def transform(self):
        from rasterio.transform import from_origin
        return from_origin(self.left, self.top, self.width, self.height)

    def cell_of(self, x, y):
        """The row and column holding a point, or None outside the grid."""
        column = int(np.floor((x - self.left) / self.width))
        row = int(np.floor((self.top - y) / self.height))
        if 0 <= row < self.rows and 0 <= column < self.columns:
            return row, column
        return None


def _refuse_environment(tool):
    """Settings arcpy's raster tools honour and grapy does not yet: refused rather than ignored."""
    for name in ("cellSize", "mask", "snapRaster"):
        if getattr(env, name, None) not in (None, "", "MAXOF", "MINOF"):
            raise NotBuiltError(f"{tool}: the '{name}' environment setting is not built in grapy yet.")
    if env.extent not in (None, "", "MAXOF", "MINOF", "INTERSECTION_OF_INPUTS", "DEFAULT"):
        raise NotBuiltError(f"{tool}: the 'extent' environment setting is not built for rasters in grapy yet.")


class Raster:
    """arcpy.Raster: ``Raster(path)`` opens a GeoTIFF; map algebra answers new, temporary rasters; ``save`` writes one."""

    _rat = None  # its raster attribute table (_Rat), or None

    def __init__(self, inRaster=None, is_multidimensional=False, *_, **__):
        if isinstance(inRaster, Raster):
            self._array, self._mask, self._grid = inRaster._array, inRaster._mask, inRaster._grid
            self.catalogPath, self.isTemporary = inRaster.catalogPath, inRaster.isTemporary
            self._rat = inRaster._rat
            return
        if inRaster is None:
            raise ValueError("Raster needs a raster dataset.")
        import rasterio
        path = os.fspath(inRaster)
        if not os.path.isabs(path) and not os.path.exists(path):
            path = os.path.join(env.workspace, path)
        with rasterio.open(path) as source:
            if source.count != 1:
                raise NotBuiltError(f"Raster: '{path}' has {source.count} bands; multiband rasters are not built in grapy yet.")
            if source.transform.b != 0 or source.transform.d != 0 or source.transform.e >= 0:
                raise NotBuiltError(f"Raster: '{path}' is rotated or south-up; only north-up grids are built in grapy yet.")
            band = source.read(1)
            mask = np.zeros(band.shape, bool) if source.nodata is None else (
                np.isnan(band) if np.issubdtype(band.dtype, np.floating) and np.isnan(source.nodata) else band == source.nodata)
            if np.issubdtype(band.dtype, np.floating):
                mask |= np.isnan(band)
            t = source.transform
            self._grid = _Grid(t.c, t.f, t.a, -t.e, source.height, source.width, source.crs.to_epsg() if source.crs else 0)
        self._array, self._mask = band, mask
        self.catalogPath, self.isTemporary = path, False
        self._rat = _read_rat(path)

    @classmethod
    def _of(cls, array, mask, grid):
        raster = cls.__new__(cls)
        raster._array, raster._mask, raster._grid = array, mask, grid
        raster.catalogPath, raster.isTemporary = None, True
        return raster

    # ---- what arcpy's Raster says about itself

    @property
    def height(self):
        return self._grid.rows

    @property
    def width(self):
        return self._grid.columns

    @property
    def bandCount(self):
        return 1

    @property
    def meanCellWidth(self):
        return self._grid.width

    @property
    def meanCellHeight(self):
        return self._grid.height

    @property
    def extent(self):
        from ._reference import Extent
        g = self._grid
        return Extent(g.left, g.bottom, g.right, g.top, self.spatialReference)

    @property
    def spatialReference(self):
        return SpatialReference(self._grid.srid) if self._grid.srid else None

    @property
    def isInteger(self):
        return bool(np.issubdtype(self._array.dtype, np.integer))

    @property
    def pixelType(self):
        kind = self._array.dtype
        if kind == np.float32:
            return "F32"
        if kind == np.float64:
            return "F64"
        return {np.dtype(np.uint8): "U8", np.dtype(np.int8): "S8", np.dtype(np.uint16): "U16", np.dtype(np.int16): "S16",
                np.dtype(np.uint32): "U32", np.dtype(np.int32): "S32", np.dtype(np.int64): "S64"}.get(kind, "F64")

    @property
    def noDataValue(self):
        return _nodata_for(self._array.dtype)

    def _values(self):
        return self._array[~self._mask]

    @property
    def minimum(self):
        values = self._values()
        return float(values.min()) if values.size else None

    @property
    def maximum(self):
        values = self._values()
        return float(values.max()) if values.size else None

    @property
    def mean(self):
        values = self._values()
        return float(values.mean()) if values.size else None

    @property
    def standardDeviation(self):
        values = self._values()
        return float(values.std()) if values.size else None

    @property
    def hasRAT(self):
        """Whether the raster carries an attribute table (its .aux.xml's GDALRasterAttributeTable)."""
        return self._rat is not None

    @property
    def name(self):
        return os.path.basename(self.catalogPath) if self.catalogPath else ""

    def save(self, name=None):
        """Writes the raster as a GeoTIFF; a name without an extension is given ``.tif``."""
        import rasterio
        if name is None:
            if not self.isTemporary:
                return
            raise ValueError("A temporary raster needs a name to be saved under.")
        path = os.fspath(name)
        if not os.path.isabs(path):
            path = os.path.join(env.workspace, path)
        if not os.path.splitext(path)[1]:
            path += ".tif"
        array = self._array
        if array.dtype == np.bool_:
            array = array.astype(np.uint8)
        if array.dtype == np.int64:
            # A GeoTIFF's NoData is a double, which cannot hold int64's own minimum exactly; values that fit are written
            # as 32-bit integers, and the rest as doubles.
            values = array[~self._mask]
            fits = values.size == 0 or (values.min() > np.iinfo(np.int32).min and values.max() <= np.iinfo(np.int32).max)
            array = array.astype(np.int32 if fits else np.float64)
        nodata = _nodata_for(array.dtype)
        written = np.where(self._mask, nodata, array).astype(array.dtype)
        g = self._grid
        profile = {"driver": "GTiff", "height": g.rows, "width": g.columns, "count": 1, "dtype": array.dtype.name,
                   "transform": g.transform(), "nodata": nodata}
        if g.srid:
            profile["crs"] = f"EPSG:{g.srid}"
        os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
        with rasterio.open(path, "w", **profile) as out:
            out.write(written, 1)
        # Its attribute table goes into the .aux.xml beside it, as GDAL keeps one; a stale one there is removed.
        _write_rat(path, self._rat)
        self.catalogPath, self.isTemporary = path, False
        return path

    def read(self, *_, **__):
        """The cells as a NumPy array, NoData as NaN."""
        return np.where(self._mask, np.nan, self._array.astype(float))

    def __repr__(self):
        return self.catalogPath or "<temporary Raster>"

    # ---- map algebra

    def _binary(self, other, operation, integer=True):
        return _combine([self, other], lambda a, b: operation(a, b), keep_integer=integer)

    def _rbinary(self, other, operation, integer=True):
        return _combine([other, self], lambda a, b: operation(a, b), keep_integer=integer)

    def __add__(self, o): return self._binary(o, np.add)
    def __radd__(self, o): return self._rbinary(o, np.add)
    def __sub__(self, o): return self._binary(o, np.subtract)
    def __rsub__(self, o): return self._rbinary(o, np.subtract)
    def __mul__(self, o): return self._binary(o, np.multiply)
    def __rmul__(self, o): return self._rbinary(o, np.multiply)
    def __truediv__(self, o): return _divide(self, o)
    def __rtruediv__(self, o): return _divide(o, self)
    def __floordiv__(self, o): return _combine([self, o], _floor_divide)
    def __rfloordiv__(self, o): return _combine([o, self], _floor_divide)
    def __mod__(self, o): return _combine([self, o], _modulo)
    def __rmod__(self, o): return _combine([o, self], _modulo)
    def __pow__(self, o): return _combine([self, o], lambda a, b: np.power(a.astype(float), b), keep_integer=False)
    def __rpow__(self, o): return _combine([o, self], lambda a, b: np.power(a.astype(float), b), keep_integer=False)
    def __neg__(self): return Raster._of(-self._array, self._mask.copy(), self._grid)
    def __pos__(self): return self
    def __abs__(self): return Raster._of(np.abs(self._array), self._mask.copy(), self._grid)
    def __lt__(self, o): return _compare(self, o, np.less)
    def __le__(self, o): return _compare(self, o, np.less_equal)
    def __gt__(self, o): return _compare(self, o, np.greater)
    def __ge__(self, o): return _compare(self, o, np.greater_equal)
    def __eq__(self, o): return _compare(self, o, np.equal)
    def __ne__(self, o): return _compare(self, o, np.not_equal)
    def __and__(self, o): return _boolean(self, o, np.logical_and)
    def __rand__(self, o): return _boolean(o, self, np.logical_and)
    def __or__(self, o): return _boolean(self, o, np.logical_or)
    def __ror__(self, o): return _boolean(o, self, np.logical_or)
    def __xor__(self, o): return _boolean(self, o, np.logical_xor)
    def __rxor__(self, o): return _boolean(o, self, np.logical_xor)
    def __invert__(self): return Raster._of((self._array == 0).astype(np.int32), self._mask.copy(), self._grid)
    __hash__ = object.__hash__


def _nodata_for(dtype):
    dtype = np.dtype(dtype)
    if np.issubdtype(dtype, np.floating):
        return float(np.finfo(dtype).min)
    if dtype == np.uint8:
        return 255
    if np.issubdtype(dtype, np.unsignedinteger):
        return int(np.iinfo(dtype).max)
    return int(np.iinfo(dtype).min)


def as_raster(value, tool="arcpy.sa"):
    """A raster from a Raster, a path, or None for a constant (answered by the caller)."""
    if isinstance(value, Raster):
        return value
    if isinstance(value, (str, os.PathLike)):
        return Raster(value)
    raise TypeError(f"{tool}: expected a raster, got {type(value).__name__}.")


def _align(rasters, tool="arcpy.sa"):
    """The rasters cut to the intersection of their extents, on a grid they share — or a refusal naming why not."""
    grids = [r._grid for r in rasters]
    first = grids[0]
    if all(first.same(g) for g in grids[1:]):
        return [r._array for r in rasters], [r._mask for r in rasters], first
    for g in grids[1:]:
        if g.srid != first.srid:
            raise NotBuiltError(f"{tool}: rasters in different references need projecting, which is not built in grapy yet.")
        if not np.isclose(g.width, first.width) or not np.isclose(g.height, first.height):
            raise NotBuiltError(f"{tool}: rasters with different cell sizes need resampling, which is not built in grapy yet.")
        for offset, size in ((g.left - first.left, first.width), (g.top - first.top, first.height)):
            if not np.isclose(offset / size, round(offset / size)):
                raise NotBuiltError(f"{tool}: rasters whose cells do not line up need resampling, which is not built in grapy yet.")
    left = max(g.left for g in grids)
    top = min(g.top for g in grids)
    right = min(g.right for g in grids)
    bottom = max(g.bottom for g in grids)
    columns = int(round((right - left) / first.width))
    rows = int(round((top - bottom) / first.height))
    if rows <= 0 or columns <= 0:
        raise ValueError(f"{tool}: the input rasters do not overlap.")
    grid = _Grid(left, top, first.width, first.height, rows, columns, first.srid)
    arrays, masks = [], []
    for r in rasters:
        row0 = int(round((r._grid.top - top) / first.height))
        column0 = int(round((left - r._grid.left) / first.width))
        arrays.append(r._array[row0:row0 + rows, column0:column0 + columns])
        masks.append(r._mask[row0:row0 + rows, column0:column0 + columns])
    return arrays, masks, grid


def _combine(values, operation, keep_integer=True, tool="arcpy.sa"):
    """An operator over rasters and constants: NoData where any raster input is NoData."""
    rasters = [v for v in values if isinstance(v, Raster) or isinstance(v, (str, os.PathLike))]
    if not rasters:
        raise TypeError(f"{tool}: at least one input must be a raster.")
    rasters = [as_raster(r, tool) for r in rasters]
    arrays, masks, grid = _align(rasters, tool)
    it = iter(arrays)
    operands = [next(it) if isinstance(v, (Raster, str, os.PathLike)) else v for v in values]
    mask = np.logical_or.reduce(masks)
    with np.errstate(all="ignore"):
        result = operation(*operands)
    if isinstance(result, tuple):
        result, extra = result
        mask = mask | np.broadcast_to(extra, mask.shape)
    result = np.asarray(result)
    if result.dtype == np.bool_:
        result = result.astype(np.int32)
    if not keep_integer and np.issubdtype(result.dtype, np.integer):
        result = result.astype(np.float64)
    if np.issubdtype(result.dtype, np.floating):
        mask = mask | ~np.isfinite(result)
    return Raster._of(result, mask, grid)


def _divide(a, b):
    def operation(x, y):
        x, y = np.asarray(x), np.asarray(y)
        zero = y == 0
        quotient = np.divide(x.astype(float), np.where(zero, 1.0, y.astype(float)))
        if np.issubdtype(x.dtype, np.integer) and np.issubdtype(y.dtype, np.integer):
            return np.trunc(quotient).astype(np.int64), zero
        return quotient, zero
    return _combine([a, b], operation)


def _floor_divide(x, y):
    """Integer division; a zero divisor is NoData, as arcpy's is."""
    y = np.asarray(y)
    zero = y == 0
    return np.floor_divide(x, np.where(zero, 1, y)), zero


def _modulo(x, y):
    y = np.asarray(y)
    zero = y == 0
    return np.fmod(x, np.where(zero, 1, y)), zero


def _compare(a, b, operation):
    return _combine([a, b], lambda x, y: operation(x, y).astype(np.int32))


def _boolean(a, b, operation):
    return _combine([a, b], lambda x, y: operation(np.asarray(x) != 0, np.asarray(y) != 0).astype(np.int32))


def NumPyArrayToRaster(in_array, lower_left_corner=None, x_cell_size=1, y_cell_size=None, value_to_nodata=None):
    """A raster from a two-dimensional array; the first row is the northern one, as arcpy's is."""
    array = np.asarray(in_array)
    if array.ndim != 2:
        raise NotBuiltError("NumPyArrayToRaster: only two-dimensional arrays are built in grapy yet.")
    x0, y0 = (lower_left_corner.X, lower_left_corner.Y) if lower_left_corner is not None else (0.0, 0.0)
    height = float(y_cell_size if y_cell_size is not None else x_cell_size)
    grid = _Grid(x0, y0 + array.shape[0] * height, float(x_cell_size), height, array.shape[0], array.shape[1],
                 getattr(getattr(env, "outputCoordinateSystem", None), "factoryCode", 0) or 0)
    mask = np.zeros(array.shape, bool) if value_to_nodata is None else array == value_to_nodata
    if np.issubdtype(array.dtype, np.floating):
        mask |= np.isnan(array)
    return Raster._of(array.copy(), mask, grid)


def RasterToNumPyArray(in_raster, lower_left_corner=None, ncols=None, nrows=None, nodata_to_value=None):
    """The cells as an array, NoData written as ``nodata_to_value`` (arcpy's default: the raster's own NoData value)."""
    raster = as_raster(in_raster, "RasterToNumPyArray")
    if lower_left_corner is not None or ncols is not None or nrows is not None:
        raise NotBuiltError("RasterToNumPyArray: reading a window (lower_left_corner, ncols, nrows) is not built in grapy yet.")
    fill = raster.noDataValue if nodata_to_value is None else nodata_to_value
    array = raster._array
    # A fill the cell type cannot hold (-1 in an unsigned raster, 0.5 in an integer one) widens the array to one that can.
    kind = np.result_type(array.dtype, np.min_scalar_type(fill))
    return np.where(raster._mask, np.asarray(fill).astype(kind), array.astype(kind))


# ---- several bands (ADR-224, arcpy.ia). A Raster stays single-band, as before; a raster of several bands is this
# separate holder, which only the band tools take, so no single-band tool can read the wrong band by accident.


class _Bands:
    """A raster of several bands on one grid: what CompositeBand answers and the band tools read. Opening a GeoTIFF
    of several bands with ``Raster`` is still refused; the band tools open it as this."""

    def __init__(self, arrays, masks, grid):
        self._arrays, self._masks, self._grid = list(arrays), list(masks), grid
        self.catalogPath, self.isTemporary = None, True

    @property
    def bandCount(self):
        return len(self._arrays)

    @property
    def width(self):
        return self._grid.columns

    @property
    def height(self):
        return self._grid.rows

    @property
    def meanCellWidth(self):
        return self._grid.width

    @property
    def meanCellHeight(self):
        return self._grid.height

    @property
    def spatialReference(self):
        return SpatialReference(self._grid.srid) if self._grid.srid else None

    @property
    def extent(self):
        from ._reference import Extent
        g = self._grid
        return Extent(g.left, g.bottom, g.right, g.top, self.spatialReference)

    def _band(self, number):
        """Band ``number`` (one-based) as a single-band Raster."""
        if not 1 <= int(number) <= len(self._arrays):
            raise ValueError(f"The raster has {len(self._arrays)} bands; there is no band {number}.")
        return Raster._of(self._arrays[int(number) - 1], self._masks[int(number) - 1], self._grid)

    def save(self, name=None):
        """Writes the bands as one GeoTIFF; NoData cells of each band are written with the type's NoData value."""
        import rasterio
        if name is None:
            raise ValueError("A temporary raster needs a name to be saved under.")
        path = os.fspath(name)
        if not os.path.isabs(path):
            path = os.path.join(env.workspace, path)
        if not os.path.splitext(path)[1]:
            path += ".tif"
        dtype = np.result_type(*[a.dtype for a in self._arrays])
        if dtype == np.bool_ or dtype == np.int64:
            dtype = np.dtype(np.int32)
        nodata = _nodata_for(dtype)
        g = self._grid
        profile = {"driver": "GTiff", "height": g.rows, "width": g.columns, "count": len(self._arrays),
                   "dtype": dtype.name, "transform": g.transform(), "nodata": nodata}
        if g.srid:
            profile["crs"] = f"EPSG:{g.srid}"
        os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
        with rasterio.open(path, "w", **profile) as out:
            for i, (a, m) in enumerate(zip(self._arrays, self._masks), 1):
                out.write(np.where(m, nodata, a).astype(dtype), i)
        self.catalogPath, self.isTemporary = path, False
        return path

    def __repr__(self):
        return self.catalogPath or "<temporary multiband Raster>"


def _open_bands(value, tool="arcpy.ia"):
    """Any raster as bands: a Raster is one band, a _Bands is itself, and a GeoTIFF path is read whole."""
    if isinstance(value, _Bands):
        return value
    if isinstance(value, Raster):
        return _Bands([value._array], [value._mask], value._grid)
    if not isinstance(value, (str, os.PathLike)):
        raise TypeError(f"{tool}: expected a raster, got {type(value).__name__}.")
    import rasterio
    path = os.fspath(value)
    if not os.path.isabs(path) and not os.path.exists(path):
        path = os.path.join(env.workspace, path)
    with rasterio.open(path) as source:
        if source.transform.b != 0 or source.transform.d != 0 or source.transform.e >= 0:
            raise NotBuiltError(f"{tool}: '{path}' is rotated or south-up; only north-up grids are built in grapy yet.")
        arrays, masks = [], []
        for i in range(1, source.count + 1):
            band = source.read(i)
            mask = np.zeros(band.shape, bool) if source.nodata is None else (
                np.isnan(band) if np.issubdtype(band.dtype, np.floating) and np.isnan(source.nodata) else band == source.nodata)
            if np.issubdtype(band.dtype, np.floating):
                mask |= np.isnan(band)
            arrays.append(band)
            masks.append(mask)
        t = source.transform
        grid = _Grid(t.c, t.f, t.a, -t.e, source.height, source.width, source.crs.to_epsg() if source.crs else 0)
    bands = _Bands(arrays, masks, grid)
    bands.catalogPath, bands.isTemporary = path, False
    return bands


# ---- raster attribute tables (RAT), stored as GDAL stores them: a ``GDALRasterAttributeTable`` in the dataset's
# ``.aux.xml`` PAM file. Field types are GDAL's GFT codes (0 integer, 1 real, 2 string) and usages its GFU codes
# (0 generic, 1 pixel count, 5 min-max, the value column).

_GFT = {"Integer": 0, "Double": 1, "String": 2}
_GFT_BACK = {0: "Integer", 1: "Double", 2: "String"}


class _Rat:
    """A raster attribute table: its fields — (name, type word, GDAL usage) — and its rows, a list of lists."""

    def __init__(self, fields, rows):
        self.fields = [tuple(f) for f in fields]
        self.rows = [list(r) for r in rows]

    def names(self):
        return [f[0] for f in self.fields]

    def index(self, name):
        lower = [f[0].lower() for f in self.fields]
        if str(name).lower() not in lower:
            raise KeyError(name)
        return lower.index(str(name).lower())

    def column(self, name):
        i = self.index(name)
        return [r[i] for r in self.rows]

    def copy(self):
        return _Rat(self.fields, self.rows)


def _aux_path(path):
    return os.fspath(path) + ".aux.xml"


def _read_rat(path):
    """The RAT of a GeoTIFF's first band from its .aux.xml, or None."""
    import xml.etree.ElementTree as ET
    aux = _aux_path(path)
    if not os.path.exists(aux):
        return None
    root = ET.parse(aux).getroot()
    for band in root.iter("PAMRasterBand"):
        if band.get("band", "1") != "1":
            continue
        table = band.find("GDALRasterAttributeTable")
        if table is None:
            return None
        fields = []
        for defn in sorted(table.findall("FieldDefn"), key=lambda d: int(d.get("index", 0))):
            fields.append((defn.findtext("Name", ""), _GFT_BACK.get(int(defn.findtext("Type", "2")), "String"),
                           int(defn.findtext("Usage", "0"))))
        rows = []
        for row in sorted(table.findall("Row"), key=lambda r: int(r.get("index", 0))):
            values = []
            for (name, kind, _), cell in zip(fields, row.findall("F")):
                text = cell.text
                if text is None or text == "":
                    values.append(None if kind != "String" else "")
                elif kind == "Integer":
                    values.append(int(float(text)))
                elif kind == "Double":
                    values.append(float(text))
                else:
                    values.append(text)
            rows.append(values)
        return _Rat(fields, rows)
    return None


def _write_rat(path, rat):
    """The RAT written into the dataset's .aux.xml as GDAL's PAM writes it; with None, a RAT already there is removed
    (and the file with it when it holds nothing else)."""
    import xml.etree.ElementTree as ET
    aux = _aux_path(path)
    root = ET.parse(aux).getroot() if os.path.exists(aux) else ET.Element("PAMDataset")
    band = next((b for b in root.iter("PAMRasterBand") if b.get("band", "1") == "1"), None)
    if band is None:
        if rat is None:
            return
        band = ET.SubElement(root, "PAMRasterBand", band="1")
    for old in band.findall("GDALRasterAttributeTable"):
        band.remove(old)
    if rat is not None:
        table = ET.SubElement(band, "GDALRasterAttributeTable", tableType="thematic")
        for i, (name, kind, usage) in enumerate(rat.fields):
            defn = ET.SubElement(table, "FieldDefn", index=str(i))
            ET.SubElement(defn, "Name").text = name
            ET.SubElement(defn, "Type").text = str(_GFT.get(kind, 2))
            ET.SubElement(defn, "Usage").text = str(usage)
        for i, row in enumerate(rat.rows):
            element = ET.SubElement(table, "Row", index=str(i))
            for value in row:
                ET.SubElement(element, "F").text = "" if value is None else (
                    repr(float(value)) if isinstance(value, float) else str(value))
    if len(band) == 0:
        root.remove(band)
    if len(root) == 0:
        if os.path.exists(aux):
            os.remove(aux)
        return
    ET.ElementTree(root).write(aux, encoding="utf-8", xml_declaration=False)


def _value_count(array, mask):
    """The Value and Count table of an integer raster: each value present, ascending, with how many cells hold it."""
    values, counts = np.unique(np.asarray(array)[~mask], return_counts=True)
    return _Rat([("Value", "Integer", 5), ("Count", "Integer", 1)],
                [[int(v), int(c)] for v, c in zip(values, counts)])


def _is_raster_path(value):
    return isinstance(value, (str, os.PathLike)) and os.fspath(value).lower().endswith((".tif", ".tiff"))
