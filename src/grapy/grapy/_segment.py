"""Segment Mean Shift and the tools that work on its segmented rasters (ADR-242).

The pages name the method — *Understanding segmentation and classification*: *"The image segmentation is based on the
Mean Shift approach"*, citing Comaniciu and Meer (2002) — and give its three controls (spectral detail, spatial detail,
minimum segment size), but not the procedure. grapy builds the published one as an equivalent (ADR-224 §5.6):

1. **Mean shift filtering** in the joint spatial–range domain (Comaniciu and Meer 2002, §4.1, the flat kernel): each
   pixel's point (x, y, colour) moves to the mean of the original pixels within ``h_s`` of it in space and ``h_r`` in
   colour, until it moves less than a tenth of a pixel and a tenth of a grey level, or twenty times.
2. **Delineation** (§4.2): neighbouring pixels whose convergence points lie within ``h_s`` and ``h_r`` of each other
   are one segment.
3. **Small regions** (§4.2's last step): a segment smaller than the minimum size is merged with its best fitting
   neighbour — the neighbour whose mean colour is nearest — the smallest first, until none is left.

The bandwidths come from the SegMeanShift function's page, which gives ``spectral_radius`` and ``spatial_radius`` as
the inverse of the detail parameters over the same 0–21 range: ``h_r = 21 − spectral_detail`` grey levels and ``h_s =
21 − spatial_detail`` pixels. Each segment's output colour is the average colour of its pixels, as *Understanding
segmentation and classification* says (*"a segment characterized by an average color"*).
"""
from __future__ import annotations

import numpy as np

from ._raster import Raster, _open_bands, _refuse_environment

SEGMENT_MEAN_SHIFT = ("mean shift filtering in the joint spatial-range domain with a flat kernel, region delineation "
                      "and small-region merging",
                      "Comaniciu and Meer 2002, IEEE Transactions on Pattern Analysis and Machine Intelligence 24(5), "
                      "§4.1-4.2")
SEGMENT_ATTRIBUTES = ("compactness as area over the area of the smallest enclosing circle, rectangularity as area over "
                      "the area of the minimum rotated bounding rectangle",
                      "MacEachren 1985, Geografiska Annaler 67B(1); Rosin 1999, Machine Vision and Applications 11")
TILING_ARTIFACTS = ("segments cut by a tile seam merged when their common boundary lies wholly on the seam and their "
                    "colours agree within the default spectral radius",
                    "Comaniciu and Meer 2002 §4.2's range bandwidth applied across the seams Segment Mean Shift's page "
                    "says its tiled processing leaves")

_DEFAULT_SPECTRAL, _DEFAULT_SPATIAL, _DEFAULT_MINIMUM = 15.5, 15, 20


def _eight_bit(array, mask):
    """A band on the 0–255 scale: 8-bit data as it is; other data stretched linearly from its minimum to its maximum
    (INFERRED — the page asks for 8-bit input prepared with Stretch, and says any raster is accepted)."""
    if array.dtype == np.uint8:
        return array.astype(np.float64)
    values = array[~mask].astype(np.float64)
    if not values.size:
        return np.zeros(array.shape)
    low, high = values.min(), values.max()
    if high == low:
        return np.zeros(array.shape)
    return np.clip((array.astype(np.float64) - low) * 255.0 / (high - low), 0, 255)


def _chosen_bands(source, band_indexes, tool):
    """The bands used: as given; else all three of a 3-band raster; else bands 1, 2 and 3, since grapy's rasters do not
    record which bands are red, green and blue (the page's own fallback); a single band is grey."""
    count = source.bandCount
    if band_indexes not in (None, "", "#"):
        parts = band_indexes if isinstance(band_indexes, (list, tuple)) else str(band_indexes).replace(",", " ").split()
        numbers = [int(p) for p in parts]
        for n in numbers:
            if not 1 <= n <= count:
                raise ValueError(f"{tool}: the raster has {count} bands; there is no band {n}.")
    elif count == 1:
        numbers = [1]
    elif count >= 3:
        numbers = [1, 2, 3]
    else:
        raise ValueError(f"{tool}: a raster of {count} bands; the page segments three bands, or one grey band.")
    if len(numbers) not in (1, 3):
        raise ValueError(f"{tool}: band_indexes names {len(numbers)} bands; the page takes a list of three bands.")
    return numbers


def _mean_shift(colours, mask, hs, hr, iterations=20):
    """Comaniciu and Meer's filtering: the convergence point (y, x, colour) of every pixel."""
    rows, columns, k = colours.shape
    yy, xx = np.mgrid[0:rows, 0:columns]
    valid = ~mask
    py, px = yy[valid].astype(np.float64), xx[valid].astype(np.float64)
    pc = colours[valid].astype(np.float64)
    radius = int(np.floor(hs))
    offsets = [(dy, dx) for dy in range(-radius, radius + 1) for dx in range(-radius, radius + 1)
               if dy * dy + dx * dx <= hs * hs]
    active = np.ones(py.size, bool)
    for _ in range(iterations):
        if not active.any():
            break
        index = np.flatnonzero(active)
        cy, cx, cc = py[index], px[index], pc[index]
        ry, rx = np.rint(cy).astype(int), np.rint(cx).astype(int)
        sy, sx = np.zeros(index.size), np.zeros(index.size)
        sc, n = np.zeros((index.size, k)), np.zeros(index.size)
        for dy, dx in offsets:
            ny, nx = ry + dy, rx + dx
            inside = (ny >= 0) & (ny < rows) & (nx >= 0) & (nx < columns)
            ny, nx = np.where(inside, ny, 0), np.where(inside, nx, 0)
            take = inside & valid[ny, nx]
            # In space the window is round the point's own position, not the pixel it rounds to.
            take &= (ny - cy) ** 2 + (nx - cx) ** 2 <= hs * hs
            neighbour = colours[ny, nx].astype(np.float64)
            take &= ((neighbour - cc) ** 2).sum(axis=1) <= hr * hr
            sy += np.where(take, ny, 0)
            sx += np.where(take, nx, 0)
            sc += np.where(take[:, None], neighbour, 0)
            n += take
        n = np.maximum(n, 1)
        ny_, nx_, nc_ = sy / n, sx / n, sc / n[:, None]
        moved = (np.hypot(ny_ - cy, nx_ - cx) >= 0.1) | (np.abs(nc_ - cc).max(axis=1) >= 0.1)
        py[index], px[index], pc[index] = ny_, nx_, nc_
        active[index[~moved]] = False
    modes = np.zeros((rows, columns, k + 2))
    modes[valid] = np.column_stack([py, px, pc])
    return modes


def _components(rows, columns, pairs, valid):
    """Labels 1.. of the connected pixels, joined by the (a, b) flat index pairs; 0 where not valid."""
    from scipy.sparse import coo_matrix
    from scipy.sparse.csgraph import connected_components
    size = rows * columns
    a, b = pairs
    graph = coo_matrix((np.ones(a.size), (a, b)), shape=(size, size))
    _, labels = connected_components(graph, directed=False)
    labels = labels.reshape(rows, columns)
    out = np.zeros((rows, columns), np.int64)
    numbers = {}
    for flat in np.flatnonzero(valid.ravel()):
        key = labels.flat[flat]
        if key not in numbers:
            numbers[key] = len(numbers) + 1
        out.flat[flat] = numbers[key]
    return out


def _neighbour_pairs(rows, columns, joined):
    """The flat indices of 4-neighbours for which ``joined(r0, c0, r1, c1)`` — given as slices — holds."""
    index = np.arange(rows * columns).reshape(rows, columns)
    a, b = [], []
    for (sl0, sl1) in (((slice(None), slice(0, -1)), (slice(None), slice(1, None))),
                       ((slice(0, -1), slice(None)), (slice(1, None), slice(None)))):
        keep = joined(sl0, sl1)
        a.append(index[sl0][keep])
        b.append(index[sl1][keep])
    return np.concatenate(a), np.concatenate(b)


def _region_means(labels, colours):
    count = labels.max()
    sums = np.zeros((count + 1, colours.shape[2]))
    sizes = np.bincount(labels.ravel(), minlength=count + 1).astype(np.float64)
    for band in range(colours.shape[2]):
        sums[:, band] = np.bincount(labels.ravel(), weights=colours[..., band].ravel(), minlength=count + 1)
    return sums / np.maximum(sizes, 1)[:, None], sizes


def _adjacent(labels):
    """Each region's set of 4-neighbouring regions."""
    pairs = set()
    for x, y in ((labels[:, :-1], labels[:, 1:]), (labels[:-1, :], labels[1:, :])):
        keep = (x != y) & (x > 0) & (y > 0)
        for p, q in zip(x[keep].tolist(), y[keep].tolist()):
            pairs.add((p, q))
            pairs.add((q, p))
    near = {}
    for p, q in pairs:
        near.setdefault(p, set()).add(q)
    return near


def _renumber(labels):
    """Labels 1.. in the order a scan, left to right and top to bottom, first meets them."""
    out = np.zeros(labels.shape, np.int64)
    flat = labels.ravel()
    order = {}
    for i in np.flatnonzero(flat):
        order.setdefault(int(flat[i]), len(order) + 1)
    lookup = np.zeros(int(flat.max()) + 1 if flat.size else 1, np.int64)
    for old, new in order.items():
        lookup[old] = new
    out.ravel()[:] = lookup[flat]
    return out


def _merge_small(labels, colours, minimum):
    """§4.2's last step: every region under ``minimum`` pixels joins the neighbour nearest it in mean colour, the
    smallest first (INFERRED: ties to the lower label; a region with no neighbour stays)."""
    if minimum <= 1:
        return labels
    for _ in range(int(labels.max()) + 1):
        means, sizes = _region_means(labels, colours)
        small = [r for r in range(1, len(sizes)) if 0 < sizes[r] < minimum]
        if not small:
            break
        near = _adjacent(labels)
        small.sort(key=lambda r: (sizes[r], r))
        lookup = np.arange(len(sizes))
        done = set()
        for r in small:
            if r in done or not near.get(r):
                continue
            candidates = sorted(near[r] - done, key=lambda q: (float(((means[q] - means[r]) ** 2).sum()), q))
            if not candidates:
                continue
            target = candidates[0]
            lookup[r] = target
            done.update({r, target})
        if (lookup == np.arange(len(sizes))).all():
            break
        while True:
            again = lookup[lookup]
            if (again == lookup).all():
                break
            lookup = again
        labels = lookup[labels]
    return _renumber(labels)


def _split_large(labels, maximum):
    """A segment over ``maximum`` pixels divided along the longer side of its bounding box at its median, until no
    part is larger (INFERRED: the page says only that larger segments are divided)."""
    if maximum is None or maximum <= 0:
        return labels
    labels = labels.copy()
    next_label = int(labels.max()) + 1
    queue = list(range(1, next_label))
    while queue:
        r = queue.pop()
        ys, xs = np.nonzero(labels == r)
        if ys.size <= maximum:
            continue
        if np.ptp(ys) >= np.ptp(xs):
            cut = np.median(ys)
            part = ys > cut if (ys > cut).any() and (ys <= cut).any() else ys >= cut
        else:
            cut = np.median(xs)
            part = xs > cut if (xs > cut).any() and (xs <= cut).any() else xs >= cut
        if part.all() or not part.any():
            continue
        labels[ys[part], xs[part]] = next_label
        queue += [r, next_label]
        next_label += 1
    return _renumber(labels)


def segment(colours, mask, hs, hr, minimum, maximum=None):
    """Segment labels 1.. (0 on NoData) of an image of shape (rows, columns, bands) on the 0–255 scale."""
    rows, columns, _ = colours.shape
    hs, hr = max(float(hs), 0.5), max(float(hr), 1e-9)
    modes = _mean_shift(colours, mask, hs, hr)
    valid = ~mask
    space, colour = modes[..., :2], modes[..., 2:]

    def joined(a, b):
        near_space = ((space[a] - space[b]) ** 2).sum(axis=-1) <= hs * hs
        near_colour = ((colour[a] - colour[b]) ** 2).sum(axis=-1) <= hr * hr
        return near_space & near_colour & valid[a] & valid[b]

    labels = _components(rows, columns, _neighbour_pairs(rows, columns, joined), valid)
    labels = _merge_small(labels, colours, int(minimum or 0))
    return _split_large(labels, maximum)


def _painted(labels, colours, mask, grid):
    """Each segment in its average colour, 8-bit, as many bands as the colours."""
    means, _ = _region_means(labels, colours)
    painted = np.rint(means[labels]).clip(0, 255).astype(np.uint8)
    painted[mask] = 0
    bands = [painted[..., b] for b in range(colours.shape[2])]
    if len(bands) == 1:
        return Raster._of(bands[0], mask.copy(), grid)
    return Raster._of_bands(bands, [mask.copy() for _ in bands], grid)


def _detail(value, default, name, tool, integer=False):
    if value in (None, "", "#"):
        return default
    number = float(value)
    if not 1.0 <= number <= 20.0:
        raise ValueError(f"{tool}: {name} ranges from 1 to 20, as the page says; {value} is outside it.")
    if integer and not number.is_integer():
        raise ValueError(f"{tool}: {name} is a whole number (Long), as the page says.")
    return number


def SegmentMeanShift(in_raster, spectral_detail=None, spatial_detail=None, min_segment_size=None, band_indexes=None,
                     max_segment_size=-1):
    """EQUIVALENT: Comaniciu and Meer's (2002) mean shift segmentation; see the module's docstring.

    The output is an 8-bit image, three bands for a multispectral input and one for a grey one, each segment in its
    average colour. ``max_segment_size`` (default −1, no limit) divides larger segments.

    INFERRED: the defaults 15.5, 15 and 20, which the page does not state; bands 1, 2 and 3 of a raster of more than
    three, grapy's rasters recording no red, green and blue; non-8-bit data stretched from its minimum to its maximum;
    NoData stays NoData and takes part in no segment."""
    tool = "SegmentMeanShift"
    _refuse_environment(f"arcpy.sa.{tool}")
    spectral = _detail(spectral_detail, _DEFAULT_SPECTRAL, "spectral_detail", tool)
    spatial = _detail(spatial_detail, _DEFAULT_SPATIAL, "spatial_detail", tool, integer=True)
    minimum = int(min_segment_size) if min_segment_size not in (None, "", "#") else _DEFAULT_MINIMUM
    maximum = int(max_segment_size) if max_segment_size not in (None, "", "#") else -1
    source = _open_bands(in_raster, tool)
    numbers = _chosen_bands(source, band_indexes, tool)
    mask = np.logical_or.reduce([source._masks[n - 1] for n in numbers])
    colours = np.stack([_eight_bit(source._arrays[n - 1], mask) for n in sorted(numbers)], axis=-1)
    labels = segment(colours, mask, 21.0 - spatial, 21.0 - spectral, minimum, maximum)
    return _painted(labels, colours, mask, source._grid)


def SegMeanShift(raster, spectral_detail=None, spatial_detail=None, spectral_radius=None, spatial_radius=None,
                 min_num_pixels_per_segment=None, astype=None):
    """EQUIVALENT: the Segment Mean Shift raster function, the same method as SegmentMeanShift. Its page asks for a
    3-band, unsigned 8-bit image and gives each radius as the inverse of its detail over 0–21.

    INFERRED: a radius given wins over its detail; neither given takes the tool's defaults (15.5, 15) and an omitted
    minimum 20; ``astype`` is honoured for 8-bit only, since the segment colours are 8-bit."""
    tool = "SegMeanShift"
    source = _open_bands(raster, tool)
    if source.bandCount != 3 or any(a.dtype != np.uint8 for a in source._arrays):
        raise ValueError("SegMeanShift: the input raster must be a 3-band, unsigned 8-bit image, as the page says.")
    if astype not in (None, "", "#") and str(astype).upper() not in ("U8", "UINT8", "8_BIT_UNSIGNED"):
        from .sa import _refuse
        _refuse(tool, "astype", astype, "the segments' colours are 8-bit, and the page lists no other pixel type")

    def radius(given, detail, default, name):
        if given not in (None, "", "#"):
            value = float(given)
        elif detail not in (None, "", "#"):
            value = 21.0 - float(detail)
        else:
            return 21.0 - default
        if not 0.0 <= value <= 21.0:
            raise ValueError(f"SegMeanShift: {name} ranges from 0 to 21, as the page says.")
        return value

    hr = radius(spectral_radius, spectral_detail, _DEFAULT_SPECTRAL, "spectral_radius")
    hs = radius(spatial_radius, spatial_detail, _DEFAULT_SPATIAL, "spatial_radius")
    minimum = int(min_num_pixels_per_segment) if min_num_pixels_per_segment not in (None, "", "#") else _DEFAULT_MINIMUM
    mask = np.logical_or.reduce(source._masks)
    colours = np.stack([a.astype(np.float64) for a in source._arrays], axis=-1)
    labels = segment(colours, mask, hs, hr, minimum)
    return _painted(labels, colours, mask, source._grid)


# ---- the tools that read a segmented raster


def _segments(source, tool):
    """A segmented raster's segments: the 4-connected regions of one colour, numbered 1.. in scan order (INFERRED —
    the page says only that a segment's pixels share one converged colour)."""
    if source.bandCount not in (1, 3):
        raise ValueError(f"{tool}: the input is a 3-band or 1-band segmented raster, as the page says.")
    mask = np.logical_or.reduce(source._masks)
    colours = np.stack([a.astype(np.float64) for a in source._arrays], axis=-1)
    rows, columns, _ = colours.shape
    valid = ~mask

    def joined(a, b):
        return (colours[a] == colours[b]).all(axis=-1) & valid[a] & valid[b]

    labels = _components(rows, columns, _neighbour_pairs(rows, columns, joined), valid)
    return labels, colours, mask


def _shapes_of(labels, grid):
    """Each segment's outline as one shapely polygon, in map units."""
    from rasterio.features import shapes
    from shapely.geometry import shape as as_shape
    from shapely.ops import unary_union
    parts = {}
    for geometry, value in shapes(labels.astype(np.int32), mask=labels > 0, transform=grid.transform()):
        parts.setdefault(int(value), []).append(as_shape(geometry))
    return {k: unary_union(v) for k, v in parts.items()}


_ATTRIBUTES = ("COLOR", "MEAN", "STD", "COUNT", "COMPACTNESS", "RECTANGULARITY")


def ComputeSegmentAttributes(in_segmented_raster, in_additional_raster=None, used_attributes=None):
    """EQUIVALENT for compactness and rectangularity (the page gives only their ranges, 1 for a circle and for a
    rectangle): the segment index raster, each segment numbered, with its attributes in the attribute table.

    COLOR is the segment's colour, ``Red``, ``Green``, ``Blue`` (``Gray`` for one band); COUNT its pixels, ``Count``;
    MEAN and STD the additional raster's per band, ``Mean_<band>`` and ``Std_<band>``, refused without one, as the page
    says; COMPACTNESS the area over that of the smallest circle enclosing the segment (MacEachren 1985), and
    RECTANGULARITY the area over that of its minimum rotated bounding rectangle (Rosin 1999). The default is COLOR,
    COUNT, COMPACTNESS and RECTANGULARITY, as documented.

    INFERRED: the field names; segments numbered in scan order; STD the population standard deviation; the
    additional raster on the segmented raster's grid."""
    from ._raster import _Rat
    from .management import _list
    tool = "ComputeSegmentAttributes"
    _refuse_environment(f"arcpy.sa.{tool}")
    chosen = [str(a).upper() for a in _list(used_attributes)] if used_attributes not in (None, "", "#") else \
        ["COLOR", "COUNT", "COMPACTNESS", "RECTANGULARITY"]
    for name in chosen:
        if name not in _ATTRIBUTES:
            raise ValueError(f"{tool}: '{name}' is not one of {', '.join(_ATTRIBUTES)}.")
    extra = None
    if in_additional_raster is not None and not (isinstance(in_additional_raster, str) and
                                                 in_additional_raster.strip() in ("", "#")):
        extra = _open_bands(in_additional_raster, tool)
    elif {"MEAN", "STD"} & set(chosen):
        raise ValueError(f"{tool}: MEAN and STD are available only with an additional input raster, as the page says.")
    source = _open_bands(in_segmented_raster, tool)
    labels, colours, mask = _segments(source, tool)
    count = int(labels.max())
    means, sizes = _region_means(labels, colours)
    fields, columns = [("Value", "Integer", 5)], [list(range(1, count + 1))]
    if "COLOR" in chosen:
        names = ["Gray"] if colours.shape[2] == 1 else ["Red", "Green", "Blue"]
        for b, name in enumerate(names):
            fields.append((name, "Integer", 0))
            columns.append([int(round(means[r, b])) for r in range(1, count + 1)])
    if "COUNT" in chosen:
        fields.append(("Count", "Integer", 1))
        columns.append([int(sizes[r]) for r in range(1, count + 1)])
    if extra is not None and {"MEAN", "STD"} & set(chosen):
        if not extra._grid.same(source._grid):
            raise ValueError(f"{tool}: the additional raster must be on the segmented raster's grid in grapy.")
        flat = labels.ravel()
        for b in range(extra.bandCount):
            values = extra._arrays[b].astype(np.float64).ravel()
            keep = (flat > 0) & ~extra._masks[b].ravel()
            n = np.bincount(flat[keep], minlength=count + 1).astype(np.float64)
            s = np.bincount(flat[keep], weights=values[keep], minlength=count + 1)
            s2 = np.bincount(flat[keep], weights=values[keep] ** 2, minlength=count + 1)
            with np.errstate(invalid="ignore", divide="ignore"):
                mean = s / n
                std = np.sqrt(np.maximum(s2 / n - mean ** 2, 0))
            if "MEAN" in chosen:
                fields.append((f"Mean_{b + 1}", "Double", 0))
                columns.append([None if n[r] == 0 else float(mean[r]) for r in range(1, count + 1)])
            if "STD" in chosen:
                fields.append((f"Std_{b + 1}", "Double", 0))
                columns.append([None if n[r] == 0 else float(std[r]) for r in range(1, count + 1)])
    if {"COMPACTNESS", "RECTANGULARITY"} & set(chosen):
        import shapely
        outlines = _shapes_of(labels, source._grid)
        if "COMPACTNESS" in chosen:
            fields.append(("Compactness", "Double", 0))
            columns.append([float(outlines[r].area / (np.pi * shapely.minimum_bounding_radius(outlines[r]) ** 2))
                            for r in range(1, count + 1)])
        if "RECTANGULARITY" in chosen:
            fields.append(("Rectangularity", "Double", 0))
            columns.append([float(outlines[r].area / shapely.oriented_envelope(outlines[r]).area)
                            for r in range(1, count + 1)])
    result = Raster._of(labels.astype(np.int32), mask, source._grid)
    result._rat = _Rat(fields, [list(row) for row in zip(*columns)])
    return result


def RemoveRasterSegmentTilingArtifacts(in_segmented_raster, tileSizeX=None, tileSizeY=None):
    """EQUIVALENT: segments cut by the tiles of a tiled segmentation joined again. The seams are every ``tileSizeX``
    columns and ``tileSizeY`` rows from the raster's top-left corner (512 by default, as documented); two segments
    meeting across a seam become one when every edge they share lies on a seam — a cut, not a boundary the
    segmentation drew — and their colours differ by no more than Segment Mean Shift's default spectral radius,
    5.5 grey levels, in each band. The joined segment takes the pixel-weighted average of their colours.

    INFERRED: the criterion; the seams counted from the top-left corner; the output keeps the input's bands."""
    tool = "RemoveRasterSegmentTilingArtifacts"
    _refuse_environment(f"arcpy.sa.{tool}")
    tx = int(tileSizeX) if tileSizeX not in (None, "", "#") else 512
    ty = int(tileSizeY) if tileSizeY not in (None, "", "#") else 512
    if tx <= 0 or ty <= 0:
        raise ValueError(f"{tool}: the tile width and height are positive numbers of pixels.")
    source = _open_bands(in_segmented_raster, tool)
    labels, colours, mask = _segments(source, tool)
    rows, columns = labels.shape
    seam_shared, other_shared = set(), set()
    for x, y, on_seam in (
            (labels[:, :-1], labels[:, 1:], np.broadcast_to((np.arange(1, columns) % tx == 0)[None, :],
                                                            (rows, columns - 1))),
            (labels[:-1, :], labels[1:, :], np.broadcast_to((np.arange(1, rows) % ty == 0)[:, None],
                                                            (rows - 1, columns)))):
        differ = (x != y) & (x > 0) & (y > 0)
        for p, q, seam in zip(x[differ].tolist(), y[differ].tolist(), on_seam[differ].tolist()):
            key = (min(p, q), max(p, q))
            (seam_shared if seam else other_shared).add(key)
    means, sizes = _region_means(labels, colours)
    parent = list(range(int(labels.max()) + 1))

    def find(a):
        while parent[a] != a:
            parent[a] = parent[parent[a]]
            a = parent[a]
        return a

    for p, q in sorted(seam_shared - other_shared):
        if np.abs(means[p] - means[q]).max() <= 5.5:
            parent[find(q)] = find(p)
    lookup = np.array([find(a) for a in range(len(parent))])
    joined = _renumber(lookup[labels])
    merged, _ = _region_means(joined, colours)
    painted = np.rint(merged[joined]).clip(0, 255)
    dtype = source._arrays[0].dtype
    bands = [np.where(mask, 0, painted[..., b]).astype(dtype) for b in range(colours.shape[2])]
    if len(bands) == 1:
        return Raster._of(bands[0], mask.copy(), source._grid)
    return Raster._of_bands(bands, [mask.copy() for _ in bands], source._grid)
