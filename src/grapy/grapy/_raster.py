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
    """arcpy.Raster: ``Raster(path)`` opens a GeoTIFF of one band or several, ``Raster("<path>/Band_2")`` one band of
    it, as Describe's raster band properties page writes a band's path, and ``Raster(RasterInfo)`` a new, empty raster;
    map algebra answers new, temporary rasters; ``save`` writes one.

    A raster of several bands holds them on one grid. The tools whose pages say how a multiband input is processed take
    it — the local mathematics, comparisons and logic, Con, SetNull, IsNull and Test, band by band, as documented — and
    every other tool refuses it by name rather than read one band as if it were the raster."""

    _rat = None  # its raster attribute table (_Rat), or None
    _names = None  # its band names, or None for Band_1, Band_2 …
    readOnly = True

    def __init__(self, inRaster=None, is_multidimensional=False, *_, **__):
        if isinstance(inRaster, Raster):
            self._arrays, self._masks, self._grid = list(inRaster._arrays), list(inRaster._masks), inRaster._grid
            self.catalogPath, self.isTemporary = inRaster.catalogPath, inRaster.isTemporary
            self._rat, self._names = inRaster._rat, inRaster._names
            return
        if isinstance(inRaster, RasterInfo):
            self._from_info(inRaster)
            return
        if inRaster is None:
            raise ValueError("Raster needs a raster dataset.")
        from . import _mdim
        if isinstance(inRaster, (str, os.PathLike)) and (_mdim.is_multidimensional_path(inRaster) or
                                                          os.fspath(inRaster).lower().endswith(".crf")):
            # A multidimensional dataset: netCDF-CF and what GDAL reads (ADR-236).
            _mdim.open_into(self, inRaster, is_multidimensional)
            return
        if is_multidimensional:
            raise NotBuiltError(f"Raster: '{inRaster}' is not a multidimensional dataset grapy reads (netCDF; ADR-236).")
        from . import _mosaic
        if isinstance(inRaster, (str, os.PathLike)) and _mosaic.exists(inRaster):
            # A mosaic dataset or mosaic layer, read as one raster mosaicked by its method (ADR-230).
            self._arrays, self._masks, self._grid, self._names = _mosaic.read(inRaster, "Raster")
            self.catalogPath, self.isTemporary = _mosaic.catalog_path(inRaster), False
            return
        import rasterio
        path = os.fspath(inRaster)
        if not os.path.isabs(path) and not os.path.exists(path):
            path = os.path.join(env.workspace, path)
        dataset, band_name = path, None
        if not os.path.exists(path) and os.path.isfile(os.path.dirname(path)):
            dataset, band_name = os.path.dirname(path), os.path.basename(path)
        with rasterio.open(dataset) as source:
            if source.transform.b != 0 or source.transform.d != 0 or source.transform.e >= 0:
                raise NotBuiltError(f"Raster: '{path}' is rotated or south-up; only north-up grids are built in grapy yet.")
            names = _band_names(source)
            numbers = range(1, source.count + 1)
            if band_name is not None:
                numbers = [_band_number(band_name, names, path)]
            arrays, masks = [], []
            for i in numbers:
                band = source.read(i)
                mask = np.zeros(band.shape, bool) if source.nodata is None else (
                    np.isnan(band) if np.issubdtype(band.dtype, np.floating) and np.isnan(source.nodata)
                    else band == source.nodata)
                if np.issubdtype(band.dtype, np.floating):
                    mask |= np.isnan(band)
                arrays.append(band)
                masks.append(mask)
            t = source.transform
            self._grid = _Grid(t.c, t.f, t.a, -t.e, source.height, source.width, source.crs.to_epsg() if source.crs else 0)
            self._block = source.block_shapes[0] if source.block_shapes else None
        self._arrays, self._masks = arrays, masks
        self._names = [names[i - 1] for i in numbers] if band_name is not None or len(arrays) > 1 else None
        if self._names is not None and all(n == f"Band_{k}" for k, n in enumerate(self._names, 1)):
            self._names = None
        self.catalogPath, self.isTemporary = path, False
        # INFERRED: an attribute table is read for a single-band dataset only; GDAL keeps one per band.
        self._rat = _read_rat(path) if band_name is None and len(arrays) == 1 else None

    @classmethod
    def _of(cls, array, mask, grid):
        raster = cls.__new__(cls)
        raster._array, raster._mask, raster._grid = array, mask, grid
        raster.catalogPath, raster.isTemporary = None, True
        return raster

    @classmethod
    def _of_bands(cls, arrays, masks, grid, names=None):
        raster = cls.__new__(cls)
        raster._arrays, raster._masks, raster._grid = list(arrays), list(masks), grid
        raster.catalogPath, raster.isTemporary = None, True
        raster._names = list(names) if names is not None else None
        return raster

    # ---- one band's cells. Every single-band tool reads these; on a raster of several bands they refuse, so no tool
    # reads band 1 as if it were the raster.

    @property
    def _array(self):
        if len(self._arrays) != 1:
            raise NotBuiltError(f"This tool reads a raster of one band, and {self!r} has {len(self._arrays)}; its page "
                                "does not say how a multiband raster is processed (take a band with getRasterBands).")
        return self._arrays[0]

    @_array.setter
    def _array(self, value):
        self._arrays = [value]

    @property
    def _mask(self):
        if len(self._masks) != 1:
            raise NotBuiltError(f"This tool reads a raster of one band, and {self!r} has {len(self._masks)}; its page "
                                "does not say how a multiband raster is processed (take a band with getRasterBands).")
        return self._masks[0]

    @_mask.setter
    def _mask(self, value):
        self._masks = [value]

    def _band(self, number):
        """Band ``number`` (one-based) as a single-band Raster."""
        if not 1 <= int(number) <= len(self._arrays):
            raise ValueError(f"The raster has {len(self._arrays)} bands; there is no band {number}.")
        band = Raster._of(self._arrays[int(number) - 1], self._masks[int(number) - 1], self._grid)
        if self.catalogPath and len(self._arrays) > 1:
            band.catalogPath, band.isTemporary = os.path.join(self.catalogPath, self.bandNames[int(number) - 1]), False
        return band

    # ---- what arcpy's Raster says about itself

    @property
    def height(self):
        return self._grid.rows

    @property
    def width(self):
        return self._grid.columns

    @property
    def bandCount(self):
        return len(self._arrays)

    @property
    def bandNames(self):
        """The bands' names: a GeoTIFF band's description where it has one, else Band_1, Band_2 … (INFERRED: the
        name Describe's raster band page uses in a band's path)."""
        return list(self._names) if self._names is not None else [f"Band_{k}" for k in range(1, len(self._arrays) + 1)]

    @property
    def noDataValues(self):
        """The NoData value of each band, as a tuple (INFERRED: a tuple of one, its noDataValue, for a single-band
        raster); several bands share the one value a GeoTIFF holds."""
        return (self.noDataValue,) * len(self._arrays)

    @property
    def isMultidimensional(self):
        return getattr(self, "_md_source", None) is not None

    def getRasterBands(self, band_ids_or_names=None):
        """The bands named, as rasters: one band answers a Raster, several a list, and none named every band, as
        documented. Bands are named by one-based index or by name (INFERRED: names match without regard to case)."""
        if band_ids_or_names is None:
            wanted = list(range(1, len(self._arrays) + 1))
        else:
            items = band_ids_or_names if isinstance(band_ids_or_names, (list, tuple)) else [band_ids_or_names]
            wanted = [_band_number(i, self.bandNames, repr(self)) for i in items]
        bands = [self._band(i) for i in wanted]
        return bands[0] if len(bands) == 1 else bands

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
        return all(bool(np.issubdtype(a.dtype, np.integer)) for a in self._arrays)

    @property
    def pixelType(self):
        kind = np.result_type(*[a.dtype for a in self._arrays])
        if kind == np.float32:
            return "F32"
        if kind == np.float64:
            return "F64"
        return {np.dtype(np.uint8): "U8", np.dtype(np.int8): "S8", np.dtype(np.uint16): "U16", np.dtype(np.int16): "S16",
                np.dtype(np.uint32): "U32", np.dtype(np.int32): "S32", np.dtype(np.int64): "S64"}.get(kind, "F64")

    @property
    def noDataValue(self):
        if len(self._arrays) > 1:
            # A GeoTIFF has one NoData value for every band; several bands are saved in one cell type.
            return _nodata_for(_saved_dtype(np.result_type(*[a.dtype for a in self._arrays])))
        return _nodata_for(self._array.dtype)

    def _values(self):
        if len(self._arrays) > 1:
            raise NotBuiltError(f"The statistics of {self!r}, a raster of {len(self._arrays)} bands, are not built in "
                                "grapy: the Raster page does not say what they are for several bands (take a band with "
                                "getRasterBands).")
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
        if len(self._arrays) > 1:
            return self._save_bands(path)
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

    def _save_bands(self, path):
        import rasterio
        dtype = _saved_dtype(np.result_type(*[a.dtype for a in self._arrays]))
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
                if self._names is not None:
                    out.set_band_description(i, self._names[i - 1])
        _write_rat(path, None)
        self.catalogPath, self.isTemporary = path, False
        return path

    def read(self, upper_left_corner=None, origin_coordinate=None, ncols=None, nrows=None, nodata_to_value=None,
             cell_size=None):
        """The cells as a NumPy array of rows, columns and bands, as documented — a single-band raster too has a band
        axis, of one. ``upper_left_corner`` offsets the window from ``origin_coordinate`` (the raster's own corner by
        default); ``ncols`` and ``nrows`` default to the raster's; NoData cells are ``nodata_to_value``, by default the
        raster's NoData value.

        INFERRED: ``upper_left_corner`` is (column, row), the order of x and y; ``origin_coordinate`` must be a cell
        corner. Refused: a ``cell_size`` other than the raster's (the page resamples bilinearly without saying how
        cell edges are treated) and a window reaching outside the raster."""
        from ._runtime import NotBuiltError as _Refused
        column0, row0 = self._window_origin(upper_left_corner, origin_coordinate, "read")
        if cell_size is not None:
            size = cell_size if isinstance(cell_size, (list, tuple)) else (cell_size, cell_size)
            if not (np.isclose(float(size[0]), self._grid.width) and np.isclose(float(size[-1]), self._grid.height)):
                raise _Refused("Raster.read: a cell_size other than the raster's resamples, which is not built in grapy.")
        columns = self._grid.columns - column0 if ncols is None else int(ncols)
        rows = self._grid.rows - row0 if nrows is None else int(nrows)
        if rows <= 0 or columns <= 0 or row0 + rows > self._grid.rows or column0 + columns > self._grid.columns:
            raise _Refused("Raster.read: a window reaching outside the raster is not built in grapy.")
        fill = self.noDataValue if nodata_to_value is None else nodata_to_value
        kind = np.result_type(*[a.dtype for a in self._arrays], np.min_scalar_type(fill))
        window = (slice(row0, row0 + rows), slice(column0, column0 + columns))
        return np.stack([np.where(m[window], np.asarray(fill).astype(kind), a[window].astype(kind))
                         for a, m in zip(self._arrays, self._masks)], axis=-1)

    def write(self, array, upper_left_corner=None, origin_coordinate=None, value_to_nodata=None):
        """A NumPy array of rows, columns and bands written into the raster's cells, as documented: the band axis is
        the raster's band count; cells holding ``value_to_nodata`` (the raster's NoData value by default) become NoData.

        INFERRED: the values take the raster's cell type, and one that does not fit it is refused; ``upper_left_corner``
        is (column, row). Refused: writing into a raster opened from a dataset on disk (the page does not say whether
        the dataset changes) — write into a new one, from a RasterInfo, and save it."""
        from ._runtime import NotBuiltError as _Refused
        if not self.isTemporary:
            raise _Refused("Raster.write into a dataset on disk is not built in grapy; the page does not say whether the "
                           "dataset changes. Write into Raster(RasterInfo) and save it.")
        values = np.asarray(array)
        if values.ndim != 3 or values.shape[2] != len(self._arrays):
            raise ValueError(f"Raster.write takes an array of rows, columns and {len(self._arrays)} band(s); got "
                             f"shape {values.shape}.")
        column0, row0 = self._window_origin(upper_left_corner, origin_coordinate, "write")
        rows, columns = values.shape[0], values.shape[1]
        if row0 + rows > self._grid.rows or column0 + columns > self._grid.columns:
            raise _Refused("Raster.write: a window reaching outside the raster is not built in grapy.")
        fill = self.noDataValue if value_to_nodata is None else value_to_nodata
        window = (slice(row0, row0 + rows), slice(column0, column0 + columns))
        arrays, masks = [], []
        for b, (a, m) in enumerate(zip(self._arrays, self._masks)):
            part = values[:, :, b]
            empty = part == fill
            if np.issubdtype(part.dtype, np.floating):
                empty |= np.isnan(part)
            data = part[~empty]
            if np.issubdtype(a.dtype, np.integer) and data.size and (
                    not np.all(np.mod(data, 1) == 0) or data.min() < np.iinfo(a.dtype).min or
                    data.max() > np.iinfo(a.dtype).max):
                raise _Refused(f"Raster.write: values that do not fit the raster's {self.pixelType} cells are not built "
                               "in grapy.")
            a, m = a.copy(), m.copy()
            a[window] = np.where(empty, 0, part).astype(a.dtype)
            m[window] = empty
            arrays.append(a)
            masks.append(m)
        self._arrays, self._masks = arrays, masks

    def _window_origin(self, upper_left_corner, origin_coordinate, what):
        from ._runtime import NotBuiltError as _Refused
        g = self._grid
        column0 = row0 = 0
        if origin_coordinate is not None:
            fx, fy = (origin_coordinate.X - g.left) / g.width, (g.top - origin_coordinate.Y) / g.height
            if not (np.isclose(fx, round(fx)) and np.isclose(fy, round(fy))) or round(fx) < 0 or round(fy) < 0:
                raise _Refused(f"Raster.{what}: an origin_coordinate that is not a cell corner inside the raster is not "
                               "built in grapy.")
            column0, row0 = int(round(fx)), int(round(fy))
        if upper_left_corner is not None:
            column0, row0 = column0 + int(upper_left_corner[0]), row0 + int(upper_left_corner[1])
        if column0 < 0 or row0 < 0:
            raise _Refused(f"Raster.{what}: a window reaching outside the raster is not built in grapy.")
        return column0, row0

    def __getitem__(self, index):
        """One cell, ``raster[row, column]``: its value, NaN where it is NoData (INFERRED, the page does not say)."""
        row, column = index
        if not (0 <= row < self._grid.rows and 0 <= column < self._grid.columns):
            # INFERRED (ADR-245 §5.7): a read past the edge, which RasterCellIterator's padding allows, is NoData.
            return float("nan")
        value = self._array[row, column]
        return float("nan") if self._mask[row, column] else value.item()

    def __setitem__(self, index, value):
        """One cell set, ``raster[row, column] = value``, where ``readOnly`` is False, as documented; NaN makes it
        NoData (INFERRED)."""
        if self.readOnly:
            raise RuntimeError("The raster is read-only; set readOnly to False to write cells with [row, column].")
        row, column = index
        array, mask = self._array.copy(), self._mask.copy()
        if value is None or (isinstance(value, float) and np.isnan(value)):
            mask[row, column] = True
        else:
            if np.issubdtype(array.dtype, np.integer) and float(value) != int(value):
                raise NotBuiltError("A fractional value in an integer raster's cell is not built in grapy.")
            array[row, column], mask[row, column] = value, False
        self._array, self._mask = array, mask

    def getRasterInfo(self):
        """A RasterInfo holding this raster's band count, cell size, extent, pixel type, NoData values and reference."""
        info = RasterInfo()
        info.setBandCount(len(self._arrays))
        info.setCellSize((self._grid.width, self._grid.height))
        info.setExtent(self.extent)
        info.setPixelType(self.pixelType)
        info.setNoDataValues(self.noDataValue if len(self._arrays) == 1 else self.noDataValues)
        if self.spatialReference is not None:
            info.setSpatialReference(self.spatialReference)
        block = getattr(self, "_block", None)
        if block:
            info.setBlockHeight(int(block[0]))
            info.setBlockWidth(int(block[1]))
        return info

    def _from_info(self, info):
        """A new raster of the RasterInfo's shape. INFERRED: every cell starts as NoData (the page says only that its
        cells are then assigned); it is temporary until saved, and not read-only, as documented."""
        extent, size, kind = info.getExtent(), info.getCellSize(), info.getPixelType()
        missing = [n for n, v in (("extent", extent), ("cell size", size), ("pixel type", kind)) if v is None]
        if missing:
            raise ValueError("Raster(RasterInfo) needs the RasterInfo's " + ", ".join(missing) + ".")
        if kind not in _PIXEL_DTYPES:
            raise NotBuiltError(f"Raster(RasterInfo): pixel type {kind} is not built in grapy; numpy has no such cell type.")
        width, height = float(size[0]), float(size[1])
        columns = int(round((extent.XMax - extent.XMin) / width))
        rows = int(round((extent.YMax - extent.YMin) / height))
        reference = info.getSpatialReference() or getattr(extent, "spatialReference", None)
        srid = getattr(reference, "factoryCode", 0) or 0
        self._grid = _Grid(extent.XMin, extent.YMax, width, height, rows, columns, srid)
        dtype = np.dtype(_PIXEL_DTYPES[kind])
        count = info.getBandCount() or 1
        self._arrays = [np.zeros((rows, columns), dtype) for _ in range(count)]
        self._masks = [np.ones((rows, columns), bool) for _ in range(count)]
        self.catalogPath, self.isTemporary, self.readOnly = None, True, False

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
    def __neg__(self): return _combine([self], np.negative)
    def __pos__(self): return self
    def __abs__(self): return _combine([self], np.abs)
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
    def __invert__(self): return _combine([self], lambda a: (np.asarray(a) == 0).astype(np.int32))
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


def _saved_dtype(dtype):
    """The cell type several bands are written in: booleans and 64-bit integers as 32-bit integers."""
    dtype = np.dtype(dtype)
    return np.dtype(np.int32) if dtype == np.bool_ or dtype == np.int64 else dtype


_PIXEL_DTYPES = {"U8": "uint8", "S8": "int8", "U16": "uint16", "S16": "int16", "U32": "uint32", "S32": "int32",
                 "F32": "float32", "F64": "float64"}


def _band_names(source):
    return [d if d else f"Band_{k}" for k, d in enumerate(source.descriptions or [None] * source.count, 1)]


def _band_number(band, names, where):
    """A band's one-based number from its index or name (INFERRED: a name matches without regard to case)."""
    if isinstance(band, (int, np.integer)) or (isinstance(band, str) and band.isdigit()):
        number = int(band)
        if not 1 <= number <= len(names):
            raise ValueError(f"{where} has {len(names)} bands; there is no band {band}.")
        return number
    lower = [n.lower() for n in names]
    if str(band).lower() not in lower:
        raise ValueError(f"{where} has no band named '{band}'; its bands are {', '.join(names)}.")
    return lower.index(str(band).lower()) + 1


def as_raster(value, tool="arcpy.sa", bands=False):
    """A raster from a Raster, a path, or None for a constant (answered by the caller). A raster of several bands is
    refused by name unless the tool takes one (``bands``)."""
    if isinstance(value, Raster):
        raster = value
    elif isinstance(value, (str, os.PathLike)):
        raster = Raster(value)
    else:
        raise TypeError(f"{tool}: expected a raster, got {type(value).__name__}.")
    if not bands and raster.bandCount > 1:
        raise NotBuiltError(f"{tool}: '{raster!r}' has {raster.bandCount} bands; this tool is built for a single band "
                            "in grapy (take one with getRasterBands, or the '<path>/Band_n' path).")
    return raster


def _band_wise(values, function, tool):
    """Multiband inputs processed band by band, as the local tools' pages document: every raster input multiband with
    the same number of bands, or a multiband raster with constants. A multiband raster with a single-band one, on which
    the pages are silent, is refused. Answers None when no input has several bands."""
    from ._runtime import ExecuteError
    rasters = [v for v in values if isinstance(v, Raster)]
    counts = {r.bandCount for r in rasters}
    if counts <= {1}:
        return None
    if 1 in counts:
        raise NotBuiltError(f"{tool}: a multiband raster with a single-band raster is not built in grapy; the page "
                            "documents multiband inputs together, or a multiband input with constants.")
    if len(counts) > 1:
        raise ExecuteError(f"{tool}: the number of bands in each multiband input must be the same.")
    outputs = [function(*[v._band(b) if isinstance(v, Raster) else v for v in values])
               for b in range(1, counts.pop() + 1)]
    return Raster._of_bands([o._array for o in outputs], [o._mask for o in outputs], outputs[0]._grid)


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
    values = [as_raster(v, tool, bands=True) if isinstance(v, (Raster, str, os.PathLike)) else v for v in values]
    multiband = _band_wise(values, lambda *vs: _combine(list(vs), operation, keep_integer, tool), tool)
    if multiband is not None:
        return multiband
    rasters = [v for v in values if isinstance(v, Raster)]
    if not rasters:
        raise TypeError(f"{tool}: at least one input must be a raster.")
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


# ---- several bands (ADR-224, arcpy.ia): a Raster of several bands, which the band tools build and read.


def _Bands(arrays, masks, grid, names=None):
    """A Raster of several bands on one grid (once a separate holder; kept as the band tools' constructor)."""
    return Raster._of_bands(arrays, masks, grid, names)


def _open_bands(value, tool="arcpy.ia"):
    """Any raster, of one band or several, for the band tools."""
    if isinstance(value, Raster):
        return value
    if not isinstance(value, (str, os.PathLike)):
        raise TypeError(f"{tool}: expected a raster, got {type(value).__name__}.")
    return Raster(value)


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


# ---- arcpy.RasterInfo: the properties a new raster is made from


class RasterInfo:
    """arcpy.RasterInfo: band count, block size, cell size, extent, NoData values, pixel type and reference, with the
    documented getters and setters and their JSON form. INFERRED: a new RasterInfo has one band and no other property;
    the block size, which grapy does not use, is kept as given."""

    def __init__(self):
        self._band_count, self._block_width, self._block_height = 1, None, None
        self._cell_size, self._extent, self._nodata, self._pixel_type, self._reference = None, None, None, None, None

    def getBandCount(self): return self._band_count
    def setBandCount(self, band_count): self._band_count = int(band_count)
    def getBlockWidth(self): return self._block_width
    def setBlockWidth(self, block_width): self._block_width = int(block_width)
    def getBlockHeight(self): return self._block_height
    def setBlockHeight(self, block_height): self._block_height = int(block_height)
    def getCellSize(self): return self._cell_size
    def getExtent(self): return self._extent
    def setExtent(self, extent): self._extent = extent
    def getSpatialReference(self): return self._reference
    def setSpatialReference(self, spatial_reference):
        self._reference = SpatialReference(spatial_reference) if isinstance(spatial_reference, (int, str)) else spatial_reference
    def getPixelType(self): return self._pixel_type

    def setCellSize(self, cell_size):
        """A (width, height) tuple, as documented."""
        width, height = cell_size
        self._cell_size = (float(width), float(height))

    def setPixelType(self, pixel_type):
        kind = str(pixel_type).upper()
        if kind not in ("U1", "U2", "U4", "S8", "U8", "S16", "U16", "S32", "U32", "F32", "F64"):
            raise ValueError(f"RasterInfo.setPixelType: '{pixel_type}' is not one of the documented pixel types.")
        self._pixel_type = kind

    def getNoDataValues(self):
        """One number for one band; a tuple, a value per band, for several, as documented."""
        if self._nodata is None or self._band_count == 1 or isinstance(self._nodata, tuple):
            return self._nodata
        return (self._nodata,) * self._band_count

    def setNoDataValues(self, nodata_values):
        """A number for every band, or a tuple of one per band, which must match the band count, as documented."""
        if isinstance(nodata_values, (list, tuple)):
            if len(nodata_values) != self._band_count:
                raise ValueError("RasterInfo.setNoDataValues: a value per band, and the band count is "
                                 f"{self._band_count}.")
            self._nodata = tuple(nodata_values)
        else:
            self._nodata = nodata_values

    def toJSONString(self):
        """The JSON form the page's sample shows: extent, geodataXform, blockWidth, blockHeight, bandCount, pixelType,
        noData, pixelSizeX and pixelSizeY. INFERRED: a reference is written as wkid and latestWkid, both its code; a
        property not set is left out."""
        import json
        reference = None
        if self._reference is not None and getattr(self._reference, "factoryCode", 0):
            reference = {"wkid": self._reference.factoryCode, "latestWkid": self._reference.factoryCode}
        out = {}
        if self._extent is not None:
            e = self._extent
            out["extent"] = {"xmin": e.XMin, "ymin": e.YMin, "xmax": e.XMax, "ymax": e.YMax}
            if reference:
                out["extent"]["spatialReference"] = reference
        if reference:
            out["geodataXform"] = {"spatialReference": reference, "type": "IdentityXform"}
        if self._block_width is not None:
            out["blockWidth"] = self._block_width
        if self._block_height is not None:
            out["blockHeight"] = self._block_height
        out["bandCount"] = self._band_count
        if self._pixel_type is not None:
            out["pixelType"] = self._pixel_type
        if self._nodata is not None:
            out["noData"] = list(self._nodata) if isinstance(self._nodata, tuple) else self._nodata
        if self._cell_size is not None:
            out["pixelSizeX"], out["pixelSizeY"] = self._cell_size
        return json.dumps(out)

    def fromJSONString(self, json_string):
        """Properties read from that JSON form; a key not given leaves its property as it was. A geodataXform other
        than IdentityXform is refused."""
        import json
        from ._reference import Extent
        data = json.loads(json_string)
        xform = data.get("geodataXform")
        if xform and xform.get("type", "IdentityXform") != "IdentityXform":
            raise NotBuiltError(f"RasterInfo.fromJSONString: geodataXform '{xform.get('type')}' is not built in grapy.")
        reference = (data.get("extent") or {}).get("spatialReference") or (xform or {}).get("spatialReference")
        if reference:
            self.setSpatialReference(int(reference.get("latestWkid") or reference["wkid"]))
        if "bandCount" in data:
            self.setBandCount(data["bandCount"])
        if "extent" in data:
            e = data["extent"]
            self._extent = Extent(e["xmin"], e["ymin"], e["xmax"], e["ymax"], self._reference)
        if "blockWidth" in data:
            self.setBlockWidth(data["blockWidth"])
        if "blockHeight" in data:
            self.setBlockHeight(data["blockHeight"])
        if "pixelType" in data:
            self.setPixelType(data["pixelType"])
        if "noData" in data:
            self.setNoDataValues(tuple(data["noData"]) if isinstance(data["noData"], list) else data["noData"])
        if "pixelSizeX" in data or "pixelSizeY" in data:
            self.setCellSize((data.get("pixelSizeX"), data.get("pixelSizeY", data.get("pixelSizeX"))))
