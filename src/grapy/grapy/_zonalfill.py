"""Zonal Fill (ADR-243): every zone filled with the least weight found along its boundary, as the tool's summary says."""
from __future__ import annotations

import numpy as np

from ._raster import Raster, _refuse_environment, as_raster
from ._runtime import NotBuiltError


def ZonalFill(in_zone_raster, in_weight_raster):
    """Each zone of ``in_zone_raster`` — the cells sharing a value, integer or floating point, as documented — takes
    the minimum of ``in_weight_raster`` along the zone's boundary; the output has the weight raster's type, as
    documented. NoData zones stay NoData.

    INFERRED: a zone's boundary is its own cells with one of their eight neighbours in another zone, in NoData or
    off the raster (the zone's edge cells in the digital topology of Rosenfeld 1970); weights that are NoData are
    passed over, and a zone with no valid weight on its boundary is NoData."""
    _refuse_environment("arcpy.sa.ZonalFill")
    zones = as_raster(in_zone_raster, "ZonalFill")
    weights = as_raster(in_weight_raster, "ZonalFill")
    if not zones._grid.same(weights._grid):
        raise NotBuiltError("ZonalFill: rasters on different grids need resampling, which is not built in grapy.")
    z, zm = np.asarray(zones._array), zones._mask
    w, wm = np.asarray(weights._array), weights._mask
    rows, columns = z.shape
    padded = np.pad(z.astype(np.float64), 1, constant_values=np.nan)
    padded_mask = np.pad(zm, 1, constant_values=True)
    edge = np.zeros(z.shape, bool)
    for dr in (-1, 0, 1):
        for dc in (-1, 0, 1):
            if dr or dc:
                neighbour = padded[1 + dr:1 + dr + rows, 1 + dc:1 + dc + columns]
                gone = padded_mask[1 + dr:1 + dr + rows, 1 + dc:1 + dc + columns]
                edge |= gone | (neighbour != z)
    edge &= ~zm
    out = np.zeros(z.shape, dtype=w.dtype)
    mask = zm.copy()
    for zone in np.unique(z[~zm]):
        inside = (z == zone) & ~zm
        candidates = w[inside & edge & ~wm]
        if candidates.size:
            out[inside] = candidates.min()
        else:
            mask |= inside
    return Raster._of(np.where(mask, 0, out).astype(w.dtype), mask, zones._grid)
