"""Compute Segment Attributes (ADR-243): a segment index raster whose attribute table holds each segment's colour,
size, compactness, rectangularity and, with an additional raster, its mean and standard deviation."""
from __future__ import annotations

import math

import numpy as np

from ._raster import Raster, _Rat, _open_bands, _refuse_environment
from ._runtime import NotBuiltError, equivalent


def _given(value):
    return value is not None and not (isinstance(value, str) and value.strip() in ("", "#"))


METHOD = ("compactness as the isoperimetric quotient 4πA/P² and rectangularity as the area over the minimum "
          "bounding rectangle's, of each segment's cells",
          "Polsby and Popper 1991, Yale Law & Policy Review 9(2) (4πA/P², 1 for a circle); Rosin 1999, Machine "
          "Vision and Applications 11 (rectangularity by the minimum bounding rectangle, 1 for a rectangle)")

_ATTRIBUTES = ("COLOR", "MEAN", "STD", "COUNT", "COMPACTNESS", "RECTANGULARITY")


def segments(arrays, mask):
    """Each cell's segment, numbered 1, 2 … in the row-major order the scan first meets them (0 under NoData): the
    four-connected runs of cells of one colour."""
    from scipy.ndimage import label
    stacked = np.stack([np.asarray(a) for a in arrays], axis=-1).reshape(-1, len(arrays))
    _, key = np.unique(stacked, axis=0, return_inverse=True)
    key = key.reshape(mask.shape)
    out = np.zeros(mask.shape, np.int64)
    offset = 0
    for colour in np.unique(key[~mask]):
        parts, count = label((key == colour) & ~mask)
        out = np.where(parts > 0, parts + offset, out)
        offset += count
    first = {}
    for value in out.ravel():
        if value and value not in first:
            first[value] = len(first) + 1
    lookup = np.zeros(offset + 1, np.int64)
    for old, new in first.items():
        lookup[old] = new
    return lookup[out]


def ComputeSegmentAttributes(in_segmented_raster, in_additional_raster=None, used_attributes=None):
    """EQUIVALENT: the segments of a segmented raster — four-connected cells of one colour, of one or three bands, as
    documented — numbered into an index raster, its attribute table holding for each segment, as asked by
    ``used_attributes`` (default COLOR, COUNT, COMPACTNESS and RECTANGULARITY; MEAN and STD only with an additional
    raster, as documented): its colour, its number of cells, its compactness 4πA/P² (Polsby and Popper 1991; 1 for a
    circle, as the page says) and its rectangularity A/A_MBR, the area over that of its minimum bounding rectangle
    (Rosin 1999; 1 for a rectangle, as the page says), A and P being the area and perimeter of the segment's cells; and
    the mean and standard deviation of each band of the additional raster over it.

    INFERRED: the fields' names — Value, Count, Red, Green and Blue (Gray for one band), Compactness, Rectangularity,
    Mean_n and Std_n for band n; segments are four-connected and numbered in scan order; the standard deviation is the
    population's; P counts the edges of holes."""
    import rasterio.features
    from shapely.geometry import shape as as_shape
    tool = "ComputeSegmentAttributes"
    _refuse_environment(f"arcpy.sa.{tool}")
    source = _open_bands(in_segmented_raster, tool)
    if len(source._arrays) not in (1, 3):
        raise ValueError(f"{tool}: the segmented raster has one band or three, as documented.")
    if isinstance(used_attributes, str):
        wanted = [a.strip().upper() for a in used_attributes.replace(",", ";").split(";") if a.strip()]
    elif used_attributes:
        wanted = [str(a).upper() for a in used_attributes]
    else:
        wanted = ["COLOR", "COUNT", "COMPACTNESS", "RECTANGULARITY"]
    for a in wanted:
        if a not in _ATTRIBUTES:
            raise ValueError(f"{tool}: '{a}' is not one of {', '.join(_ATTRIBUTES)}.")
    extra = None
    if any(a in ("MEAN", "STD") for a in wanted):
        if not _given(in_additional_raster):
            raise ValueError(f"{tool}: MEAN and STD need an additional raster, as documented.")
    if _given(in_additional_raster):
        extra = _open_bands(in_additional_raster, tool)
        if not extra._grid.same(source._grid):
            raise NotBuiltError(f"{tool}: an additional raster on another grid needs resampling, which is not built "
                                "in grapy.")
    grid = source._grid
    mask = np.logical_or.reduce(source._masks)
    index = segments(source._arrays, mask)
    fields = [("Value", "Integer", 5)]
    colour_names = ["Red", "Green", "Blue"] if len(source._arrays) == 3 else ["Gray"]
    if "COLOR" in wanted:
        fields += [(n, "Double", 0) for n in colour_names]
    if "COUNT" in wanted:
        fields.append(("Count", "Integer", 1))
    if "COMPACTNESS" in wanted:
        fields.append(("Compactness", "Double", 0))
    if "RECTANGULARITY" in wanted:
        fields.append(("Rectangularity", "Double", 0))
    if "MEAN" in wanted:
        fields += [(f"Mean_{k + 1}", "Double", 0) for k in range(len(extra._arrays))]
    if "STD" in wanted:
        fields += [(f"Std_{k + 1}", "Double", 0) for k in range(len(extra._arrays))]
    shapes = {}
    for geometry, value in rasterio.features.shapes(index.astype(np.int32), mask=index > 0,
                                                    transform=grid.transform(), connectivity=4):
        shapes.setdefault(int(value), []).append(as_shape(geometry))
    rows = []
    for value in range(1, int(index.max()) + 1):
        inside = index == value
        row = [value]
        if "COLOR" in wanted:
            row += [float(np.asarray(a)[inside].mean()) for a in source._arrays]
        if "COUNT" in wanted:
            row.append(int(inside.sum()))
        if "COMPACTNESS" in wanted or "RECTANGULARITY" in wanted:
            from shapely.ops import unary_union
            polygon = unary_union(shapes[value])
            area, perimeter = polygon.area, polygon.length
            if "COMPACTNESS" in wanted:
                row.append(4 * math.pi * area / (perimeter * perimeter) if perimeter > 0 else 0.0)
            if "RECTANGULARITY" in wanted:
                box = polygon.minimum_rotated_rectangle.area
                row.append(area / box if box > 0 else 0.0)
        if "MEAN" in wanted:
            row += [float(np.asarray(a, float)[inside & ~m].mean()) if (inside & ~m).any() else None
                    for a, m in zip(extra._arrays, extra._masks)]
        if "STD" in wanted:
            row += [float(np.asarray(a, float)[inside & ~m].std()) if (inside & ~m).any() else None
                    for a, m in zip(extra._arrays, extra._masks)]
        rows.append(row)
    result = Raster._of(np.where(mask, 0, index).astype(np.int32), mask.copy(), grid)
    result._rat = _Rat(fields, rows)
    return result


ComputeSegmentAttributes = equivalent(*METHOD)(ComputeSegmentAttributes)
