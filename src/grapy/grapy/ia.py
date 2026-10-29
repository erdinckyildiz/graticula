"""arcpy.ia — Image Analyst. Its functions that Spatial Analyst shares are answered by grapy.sa, which is what
arcpy documents them as (ADR-224); the rest are refused by name until built."""
from __future__ import annotations

from ._runtime import NotBuiltError
from .sa import (  # noqa: F401 — re-exported under arcpy.ia's names
    ACos, ACosH, ASin, ASinH, ATan, ATan2, ATanH, Abs, Aggregate, Aspect, BitwiseAnd, BitwiseLeftShift, BitwiseNot,
    BitwiseOr, BitwiseRightShift, BitwiseXOr, BooleanAnd, BooleanNot, BooleanOr, BooleanXOr, CellStatistics, Con,
    Contour, Cos, CosH, Curvature, Diff, Divide, EqualTo, EqualToFrequency, Exp, Exp10, Exp2, Float,
    FocalStatistics, GreaterThan, GreaterThanEqual, GreaterThanFrequency, HighestPosition, Hillshade, Int, IsNull,
    LessThan, LessThanEqual, LessThanFrequency, Ln, Log10, Log2, LowestPosition, Minus, Mod, NbrAnnulus, NbrCircle,
    NbrRectangle, Negate, NotEqual, Over, Pick, Plus, Popularity, Power, RadiusFixed, RadiusVariable, Rank, Raster,
    RasterCalculator, RemapRange, RemapValue, RoundDown, RoundUp, SetNull, Sin, SinH, Slope, Square, SquareRoot,
    Tan, TanH, Test, Times, WSTable, WeightedSum, ZonalStatistics, ZonalStatisticsAsTable,
)


import numpy as np  # noqa: E402

from . import sa as _sa  # noqa: E402
from ._raster import Raster as _Raster, _Bands, _open_bands  # noqa: E402


def _refuse(tool, parameter, value, why=""):
    raise NotBuiltError(f"arcpy.ia.{tool}: {parameter} '{value}' is not built in grapy{': ' + why if why else ''}.")


def _given(value):
    return value not in (None, "", "#")


# ---- band indexes: the formulas of each function's page, which are those of the Band Arithmetic raster function


def _band_values(raster, ids, tool):
    """The bands asked for, one-based, as floating point with NoData as NaN, and their grid."""
    bands = _open_bands(raster, tool)
    out = []
    for number in ids:
        if number is None:
            _refuse(tool, "band id", "(empty)", "its default is not documented; pass it")
        band = bands._band(int(number))
        out.append(np.where(band._mask, np.nan, band._array.astype(np.float64)))
    return out, bands._grid


def _index_raster(values, grid):
    """An index's answer: floating point, NoData where any band used was NoData or the formula has no value (a zero
    denominator). INFERRED: 32-bit floating point, as arcpy's float rasters are; the pages give no pixel type."""
    with np.errstate(all="ignore"):
        values = np.asarray(values, dtype=np.float64)
    mask = ~np.isfinite(values)
    return _Raster._of(np.where(mask, 0, values).astype(np.float32), mask, grid)


def _index(name, band_names, defaults, formula, doc, constants=()):
    """An index function: ``name(raster, <band ids…>, <constants…>)`` with the documented defaults."""
    parameters = [*band_names, *[c for c, _ in constants]]

    def tool(raster, *args, **kwargs):
        given = dict(zip(parameters, args))
        given.update(kwargs)
        unknown = set(given) - set(parameters)
        if unknown:
            raise TypeError(f"{name}() got an unexpected argument {sorted(unknown)[0]!r}")
        ids = [given.get(b, defaults.get(b)) for b in band_names]
        values, grid = _band_values(raster, ids, name)
        numbers = [float(given.get(c, d)) for c, d in constants]
        with np.errstate(all="ignore"):
            return _index_raster(formula(*values, *numbers), grid)

    tool.__name__ = tool.__qualname__ = name
    shown = [f"{b}={defaults.get(b)}" for b in band_names] + [f"{c}={d}" for c, d in constants]
    tool.__doc__ = f"arcpy.ia.{name}(raster, {', '.join(shown)}): {doc} Band ids are one-based."
    return tool


NDVI = _index("NDVI", ["nir_band_id", "red_band_id"], {"nir_band_id": 4, "red_band_id": 3},
              lambda nir, red: (nir - red) / (nir + red), "(NIR − Red) / (NIR + Red), from −1 to 1.")
SAVI = _index("SAVI", ["nir_band_id", "red_band_id"], {"nir_band_id": 7, "red_band_id": 6},
              lambda nir, red, l: (nir - red) / (nir + red + l) * (1 + l),
              "((NIR − Red) / (NIR + Red + L)) × (1 + L). The defaults are the page's: bands 7 and 6, L 0.33.",
              [("l", 0.33)])
EVI = _index("EVI", ["nir_band_id", "red_band_id", "blue_band_id"], {"nir_band_id": 5, "red_band_id": 4, "blue_band_id": 2},
             lambda nir, red, blue: 2.5 * (nir - red) / (nir + 6 * red - 7.5 * blue + 1),
             "2.5 × (NIR − Red) / (NIR + 6 × Red − 7.5 × Blue + 1).")
MSAVI = _index("MSAVI", ["nir_band_id", "red_band_id"], {"nir_band_id": 4, "red_band_id": 3},
               lambda nir, red: 0.5 * (2 * (nir + 1) - np.sqrt((2 * nir + 1) ** 2 - 8 * (nir - red))),
               "MSAVI2 = ½ (2(NIR + 1) − √((2 NIR + 1)² − 8(NIR − Red))).")
NDWI = _index("NDWI", ["nir_band_id", "green_band_id"], {"nir_band_id": 5, "green_band_id": 3},
              lambda nir, green: (green - nir) / (green + nir), "(Green − NIR) / (Green + NIR).")
NBR = _index("NBR", ["swir_band_id", "nir_band_id"], {"swir_band_id": 7, "nir_band_id": 5},
             lambda swir, nir: (nir - swir) / (nir + swir), "(NIR − SWIR) / (NIR + SWIR).")
BAI = _index("BAI", ["red_band_id", "nir_band_id"], {"red_band_id": 3, "nir_band_id": 4},
             lambda red, nir: 1 / ((0.1 - red) ** 2 + (0.06 - nir) ** 2), "1 / ((0.1 − Red)² + (0.06 − NIR)²).")
CIg = _index("CIg", ["nir_band_id", "green_band_id"], {"nir_band_id": 7, "green_band_id": 3},
             lambda nir, green: nir / green - 1, "(NIR / Green) − 1.")
CIre = _index("CIre", ["nir_band_id", "redEdge_band_id"], {"nir_band_id": 7, "redEdge_band_id": 6},
              lambda nir, edge: nir / edge - 1, "(NIR / RedEdge) − 1.")
ClayMinerals = _index("ClayMinerals", ["swir1_band_id", "swir2_band_id"], {"swir1_band_id": 6, "swir2_band_id": 7},
                      lambda a, b: a / b, "SWIR1 / SWIR2.")
FerrousMinerals = _index("FerrousMinerals", ["swir_band_id", "nir_band_id"], {"swir_band_id": 6},
                         lambda swir, nir: swir / nir,
                         "SWIR / NIR. The page gives no default for nir_band_id, so it must be passed.")
GEMI = _index("GEMI", ["nir_band_id", "red_band_id"], {"nir_band_id": 4, "red_band_id": 3},
              lambda nir, red: (lambda eta: eta * (1 - 0.25 * eta) - (red - 0.125) / (1 - red))(
                  (2 * (nir ** 2 - red ** 2) + 1.5 * nir + 0.5 * red) / (nir + red + 0.5)),
              "η(1 − 0.25η) − (Red − 0.125)/(1 − Red), η = (2(NIR² − Red²) + 1.5 NIR + 0.5 Red)/(NIR + Red + 0.5).")
IronOxide = _index("IronOxide", ["red_band_id", "blue_band_id"], {"red_band_id": 4, "blue_band_id": 2},
                   lambda red, blue: red / blue, "Red / Blue.")
NDBI = _index("NDBI", ["swir_band_id", "nir_band_id"], {"swir_band_id": 6, "nir_band_id": 5},
              lambda swir, nir: (swir - nir) / (swir + nir), "(SWIR − NIR) / (SWIR + NIR).")
NDMI = _index("NDMI", ["nir_band_id", "swir1_band_id"], {"nir_band_id": 5, "swir1_band_id": 6},
              lambda nir, swir: (nir - swir) / (nir + swir), "(NIR − SWIR1) / (NIR + SWIR1).")
NDSI = _index("NDSI", ["green_band_id", "swir_band_id"], {"green_band_id": 4, "swir_band_id": 6},
              lambda green, swir: (green - swir) / (green + swir), "(Green − SWIR) / (Green + SWIR).")
NDVIre = _index("NDVIre", ["nir_band_id", "redEdge_band_id"], {"nir_band_id": 7, "redEdge_band_id": 6},
                lambda nir, edge: (nir - edge) / (nir + edge), "(NIR − RedEdge) / (NIR + RedEdge).")
PVI = _index("PVI", ["nir_band_id", "red_band_id"], {"nir_band_id": 4, "red_band_id": 3},
             lambda nir, red, a, b: (nir - a * red - b) / np.sqrt(1 + a * a), "(NIR − a·Red − b) / √(1 + a²).",
             [("a", 0.3), ("b", 0.5)])
RTVICore = _index("RTVICore", ["nir_band_id", "redEdge_band_id", "green_band_id"],
                  {"nir_band_id": 7, "redEdge_band_id": 6, "green_band_id": 3},
                  lambda nir, edge, green: 100 * (nir - edge) - 10 * (nir - green), "100(NIR − RedEdge) − 10(NIR − Green).")
SR = _index("SR", ["nir_band_id", "red_band_id"], {"nir_band_id": 5, "red_band_id": 4}, lambda nir, red: nir / red,
            "NIR / Red.")
SRre = _index("SRre", ["nir_band_id", "redEdge_band_id"], {"nir_band_id": 7, "redEdge_band_id": 6},
              lambda nir, edge: nir / edge, "NIR / RedEdge.")
TSAVI = _index("TSAVI", ["nir_band_id", "red_band_id"], {"nir_band_id": 4, "red_band_id": 3},
               lambda nir, red, s, a, x: s * (nir - s * red - a) / (a * nir + red - a * s + x * (1 + s * s)),
               "s(NIR − s·Red − a) / (a·NIR + Red − a·s + X(1 + s²)).", [("s", 0.33), ("a", 0.5), ("X", 1.5)])
VARI = _index("VARI", ["red_band_id", "green_band_id", "blue_band_id"], {"red_band_id": 3, "green_band_id": 2, "blue_band_id": 1},
              lambda red, green, blue: (green - red) / (green + red - blue), "(Green − Red) / (Green + Red − Blue).")


# BandArithmetic's method codes and, for each, the formula over the bands named in band_ids and the constants that
# follow them in the same string, as the Band Arithmetic raster function documents.
_METHODS = {
    1: ("NDVI", 2, lambda nir, red: (nir - red) / (nir + red)),
    2: ("SAVI", 3, lambda nir, red, l: (nir - red) / (nir + red + l) * (1 + l)),
    3: ("TSAVI", 5, lambda nir, red, s, a, x: s * (nir - s * red - a) / (a * nir + red - a * s + x * (1 + s * s))),
    4: ("MSAVI", 2, lambda nir, red: 0.5 * (2 * (nir + 1) - np.sqrt((2 * nir + 1) ** 2 - 8 * (nir - red)))),
    5: ("GEMI", 2, lambda nir, red: (lambda eta: eta * (1 - 0.25 * eta) - (red - 0.125) / (1 - red))(
        (2 * (nir ** 2 - red ** 2) + 1.5 * nir + 0.5 * red) / (nir + red + 0.5))),
    6: ("PVI", 4, lambda nir, red, a, b: (nir - a * red - b) / np.sqrt(1 + a * a)),
    7: ("GVITM", 6, lambda b1, b2, b3, b4, b5, b7: -0.2848 * b1 - 0.2435 * b2 - 0.5436 * b3 + 0.7243 * b4 + 0.0840 * b5
        - 1.1800 * b7),
    9: ("VARI", 3, lambda red, green, blue: (green - red) / (green + red - blue)),
    10: ("GNDVI", 2, lambda nir, green: (nir - green) / (nir + green)),
    11: ("SR", 2, lambda nir, red: nir / red),
    12: ("NDVIre", 2, lambda nir, edge: (nir - edge) / (nir + edge)),
    13: ("SRre", 2, lambda nir, edge: nir / edge),
    15: ("RTVICore", 3, lambda nir, edge, green: 100 * (nir - edge) - 10 * (nir - green)),
    16: ("CIre", 2, lambda nir, edge: nir / edge - 1),
    17: ("CIg", 2, lambda nir, green: nir / green - 1),
    19: ("EVI", 3, lambda nir, red, blue: 2.5 * (nir - red) / (nir + 6 * red - 7.5 * blue + 1)),
    20: ("Iron oxide", 2, lambda red, blue: red / blue),
    21: ("Ferrous minerals", 2, lambda swir, nir: swir / nir),
    22: ("Clay minerals", 2, lambda a, b: a / b),
    23: ("WNDWI", 4, lambda green, nir, swir, alpha: (green - alpha * nir - (1 - alpha) * swir)
         / (green + alpha * nir + (1 - alpha) * swir)),
}
# How many of each method's values are band ids; the rest are constants written in the same string.
_BANDS_IN = {2: 2, 3: 2, 6: 2, 23: 3}


def BandArithmetic(raster, band_ids, method=1):
    """One of the Band Arithmetic raster function's indexes by its method code, over the bands ``band_ids`` names —
    ``"5 4"`` — in the order the method documents, constants following the bands (``"4 3 0.5"`` for SAVI).

    Refused: 0, a user-defined expression, whose syntax the page shows only by one example; 8, Sultan's formula, which
    answers three bands; 14, MTVI2, whose formula the page's text renders garbled; 18, NDWI, whose band order the page
    gives as NIR then Green while the formula reads Green first.

    INFERRED: GVITM's six ids are Bands 1, 2, 3, 4, 5 and 7 of the formula, in that order; WNDWI's α defaults to 0.5,
    as the page says, when only three ids are given."""
    code = int(method)
    if code not in _METHODS:
        reasons = {0: "a user-defined expression's syntax is shown only by one example",
                   8: "Sultan's formula answers three bands", 14: "the page's text renders the formula garbled",
                   18: "the page's band order and formula disagree"}
        _refuse("BandArithmetic", "method", method, reasons.get(code, "it is not one of the documented codes"))
    name, count, formula = _METHODS[code]
    parts = [p for p in str(band_ids).replace(",", " ").split() if p]
    if code == 23 and len(parts) == 3:
        parts.append("0.5")
    if len(parts) != count:
        raise ValueError(f"BandArithmetic {name} takes {count} values in band_ids; {len(parts)} were given.")
    bands_in = _BANDS_IN.get(code, count)
    values, grid = _band_values(raster, [int(float(p)) for p in parts[:bands_in]], "BandArithmetic")
    with np.errstate(all="ignore"):
        return _index_raster(formula(*values, *[float(p) for p in parts[bands_in:]]), grid)


# ---- bands


def CompositeBand(raster):
    """The rasters' bands, in order, as one raster of several bands; every input must lie on one grid (cells of the same
    size, lined up and covering the same extent), since resampling is not built."""
    inputs = raster if isinstance(raster, (list, tuple)) else [raster]
    bands = [_open_bands(r, "CompositeBand") for r in inputs]
    grid = bands[0]._grid
    for b in bands[1:]:
        if not b._grid.same(grid):
            _refuse("CompositeBand", "raster", b, "rasters on different grids need resampling")
    return _Bands([a for b in bands for a in b._arrays], [m for b in bands for m in b._masks], grid)


def ExtractBand(raster, band_ids=None, band_names=None, band_wavelengths=None, missing_band_action=None,
                wavelength_match_tolerance=None):
    """The bands ``band_ids`` names, in that order: one band answers a Raster, several a raster of several bands.

    Refused: ``band_names`` and ``band_wavelengths``, which need band metadata grapy's rasters do not carry; a band id
    the raster does not have, since BestMatch's choice of a substitute is not specified.

    INFERRED: band ids are one-based, as every other arcpy.ia band id is; the page's example passes [1, 2]."""
    if _given(band_names):
        _refuse("ExtractBand", "band_names", band_names, "band names are metadata grapy's rasters do not carry")
    if _given(band_wavelengths):
        _refuse("ExtractBand", "band_wavelengths", band_wavelengths, "wavelengths are metadata grapy's rasters do not carry")
    bands = _open_bands(raster, "ExtractBand")
    ids = band_ids if isinstance(band_ids, (list, tuple)) else [band_ids]
    if not ids or ids == [None]:
        raise ValueError("ExtractBand needs band_ids.")
    for i in ids:
        if not 1 <= int(i) <= bands.bandCount:
            _refuse("ExtractBand", "band_ids", i, f"the raster has {bands.bandCount} bands, and BestMatch's substitute is "
                    "not specified")
    if len(ids) == 1:
        return bands._band(int(ids[0]))
    return _Bands([bands._arrays[int(i) - 1] for i in ids], [bands._masks[int(i) - 1] for i in ids], bands._grid)


# ---- local statistics across rasters, answered by arcpy.sa.CellStatistics


def _same_grid_or_intersection(rasters, extent_type, cellsize_type, tool):
    """The extent and cell size choices: the same answer whatever they are when every input lies on one grid; otherwise
    only IntersectionOf over cells of one size, which is how grapy aligns rasters."""
    opened = [_sa.as_raster(r, tool) if isinstance(r, str) else r for r in rasters]
    grids = [r._grid for r in opened if isinstance(r, _Raster)]
    if all(grids[0].same(g) for g in grids[1:]):
        return opened
    if str(extent_type or "FirstOf") != "IntersectionOf":
        _refuse(tool, "extent_type", extent_type, "rasters on different grids are built only for IntersectionOf")
    widths = {round(g.width, 9) for g in grids}
    if len(widths) != 1:
        _refuse(tool, "cellsize_type", cellsize_type, "rasters of different cell sizes need resampling")
    return opened


def _local(name, statistic, doc):
    def tool(rasters, extent_type="FirstOf", cellsize_type="FirstOf", ignore_nodata=False, process_as_multiband=False):
        if process_as_multiband:
            _refuse(name, "process_as_multiband", process_as_multiband)
        opened = _same_grid_or_intersection(list(rasters), extent_type, cellsize_type, name)
        return _sa.CellStatistics(opened, statistic, "DATA" if ignore_nodata else "NODATA")
    tool.__name__ = tool.__qualname__ = name
    tool.__doc__ = (f"arcpy.ia.{name}: {doc}, cell by cell — arcpy.sa.CellStatistics' {statistic}. NoData anywhere makes "
                    "the cell NoData unless ``ignore_nodata`` is True.")
    return tool


Max = _local("Max", "MAXIMUM", "the largest value across the rasters")
Min = _local("Min", "MINIMUM", "the smallest value across the rasters")
Mean = _local("Mean", "MEAN", "the mean across the rasters")
Median = _local("Median", "MEDIAN", "the median across the rasters")
Sum = _local("Sum", "SUM", "the sum across the rasters")
Range = _local("Range", "RANGE", "the largest less the smallest value across the rasters")
StdDev = _local("StdDev", "STD", "the standard deviation across the rasters")
Majority = _local("Majority", "MAJORITY", "the most frequent value across the rasters")
Minority = _local("Minority", "MINORITY", "the least frequent value across the rasters")
Variety = _local("Variety", "VARIETY", "the number of distinct values across the rasters")


def Arithmetic(raster1, raster2, operation_type="Plus", extent_type="FirstOf", cellsize_type="FirstOf"):
    """Two rasters (or a raster and a number) combined cell by cell: Plus, Minus, Multiply, Divide or Power — arcpy.sa's
    Plus, Minus, Times, Divide and Power.

    Refused: Mode, whose answer for two different values is not specified.

    INFERRED: NoData in either input is NoData, arcpy's general rule; Divide divides two integers as arcpy.sa.Divide
    documents (an integer division)."""
    operations = {"PLUS": _sa.Plus, "MINUS": _sa.Minus, "MULTIPLY": _sa.Times, "DIVIDE": _sa.Divide, "POWER": _sa.Power}
    kind = str(operation_type or "Plus").upper()
    if kind not in operations:
        _refuse("Arithmetic", "operation_type", operation_type, "the answer of Mode for two different values is not specified")
    rasters = [r for r in (raster1, raster2) if isinstance(r, (_Raster, str))]
    _same_grid_or_intersection(rasters, extent_type, cellsize_type, "Arithmetic")
    return operations[kind](raster1, raster2)


def Remap(raster, input_ranges=None, output_values=None, no_data_ranges=None, allow_unmatched=True, replacement_value=None):
    """Values remapped by ranges, each from (inclusive) to (exclusive): ``input_ranges`` ``[from, to, from, to, …]`` with
    one output value each; values in ``no_data_ranges`` become NoData; unmatched values keep their value, or — with
    ``allow_unmatched`` False — become ``replacement_value``, or NoData when there is none.

    INFERRED: a value in a NoData range is NoData even if an input range also holds it; of overlapping input ranges the
    first listed wins; the output is integer when the input and every output and replacement value are integers."""
    from ._raster import as_raster
    source = as_raster(raster, "Remap")
    ranges = list(input_ranges or [])
    outputs = list(output_values or [])
    if len(ranges) % 2 or len(ranges) // 2 != len(outputs):
        raise ValueError("Remap: input_ranges are pairs, one output value for each pair.")
    a = source._array.astype(np.float64)
    out = np.full(a.shape, np.nan)
    done = np.zeros(a.shape, bool)
    for (low, high), value in zip(zip(ranges[0::2], ranges[1::2]), outputs):
        hit = (a >= float(low)) & (a < float(high)) & ~done
        out[hit] = float(value)
        done |= hit
    nodata = source._mask.copy()
    empty = list(no_data_ranges or [])
    for low, high in zip(empty[0::2], empty[1::2]):
        nodata |= (a >= float(low)) & (a < float(high))
    if allow_unmatched:
        out = np.where(done, out, a)
    elif _given(replacement_value):
        out = np.where(done, out, float(replacement_value))
    else:
        nodata |= ~done
    values = [*outputs, *([replacement_value] if _given(replacement_value) else [])]
    integer = source.isInteger and all(float(v).is_integer() for v in values)
    out = np.where(nodata, 0, out)
    return _Raster._of(out.astype(np.int32) if integer else out, nodata, source._grid)


# ---- refused by name, with the reason

_GROUP_3 = "it needs a pretrained model or a deep learning framework, which a user tool on this server does not have " \
           "(ADR-224 group 3)"
_GROUP_2 = "it needs a data model this server does not hold yet (ADR-224 group 2)"
_UNSPECIFIED = "its algorithm is named or described but not specified closely enough to reproduce"
_REFUSED = {name: _GROUP_3 for name in (
    "ClassifyObjectsUsingDeepLearning", "ClassifyPixelsUsingDeepLearning", "ComputeAccuracyForObjectDetection",
    "ComputeAccuracyForPixelClassification", "DeepLearningModelToEcd", "DetectChangeUsingDeepLearning",
    "DetectObjectsUsingDeepLearning", "DetectObjectsUsingText", "ExportTrainingDataForDeepLearning",
    "ExtractFeaturesUsingAIModels", "NonMaximumSuppression", "TrainDeepLearningModel", "TrainUsingAutoDL",
    "TranslatePixelsUsingDeepLearning",
)}
_REFUSED.update({name: _GROUP_2 for name in (
    "AggregateMultidimensionalRaster", "AnalyzeChangesUsingCCDC", "AnalyzeChangesUsingLandTrendr", "Anomaly",
    "DimensionalMovingStatistics", "FindArgumentStatistics", "ArgStatistics", "GenerateMultidimensionalAnomaly",
    "GenerateTrend", "GenerateTrendRaster", "InterpolateRasterByDimension", "MultidimensionalPrincipalComponents",
    "MultidimensionalRasterCorrelation", "PredictUsingTrend", "PredictUsingTrendRaster", "RasterToXarray",
    "XarrayToRaster", "TrendToRGB", "ComputeChange", "ComputeChangeRaster", "DetectChangeUsingChangeAnalysis",
    "DetectChangeUsingChangeAnalysisRaster", "InterpolateFromSpatiotemporalPoints",
    "ApplyCoregistration", "ApplyGeometricTerrainCorrection", "ApplyOrbitCorrection", "ApplyRadiometricCalibration",
    "ApplyRadiometricTerrainFlattening", "ApplyComplexDataFilter", "ComputeCoherence", "ComputeSARIndices",
    "ConvertSARUnits", "Deburst", "Despeckle", "DetectBrightOceanObjects", "DetectDarkOceanAreas", "DownloadOrbitFile",
    "ExtractOceanWinds", "GenerateInterferogram", "GenerateMaskedSARRaster", "GenerateMultitemporalCoherence",
    "GenerateRadiometricTerrainCorrectedData", "Multilook", "RadarCalibration", "RemoveThermalNoise",
    "S1RadiometricCalibration", "S1ThermalNoiseRemoval", "UnwrapPhase",
    "ConvertVideoMetadata", "ExtractVideoFramesToImages", "GenerateVideoMetadataStationary", "VideoMetadataToFeatureClass",
    "VideoMultiplexer", "RasterCollection",
)})
_REFUSED.update({name: _UNSPECIFIED for name in (
    "TrainIsoClusterClassifier", "TrainKNearestNeighborClassifier", "TrainMaximumLikelihoodClassifier",
    "TrainRandomTreesClassifier", "TrainRandomTreesRegressionModel", "TrainSupportVectorMachineClassifier",
    "ClassifyRaster", "Classify", "MLClassify", "PredictUsingRegressionModel", "SegmentMeanShift", "SegMeanShift",
    "Threshold", "Pansharpen", "TasseledCap", "RegionGrow",
)})
_REFUSED.update({
    "MTVI2": "the formula on its page is rendered garbled in the text, and grapy does not guess where the root falls",
    "Stretch": "its pages describe MinMax, StdDev and PercentClip without giving their formulas",
    "Convolution": "the page maps only two of its 22 kernel codes to kernels, and edge handling is not documented",
    "Percentile": "which interpolation AUTO_DETECT's NEAREST and LINEAR use between two values is not specified",
    "Sultan": "it answers three bands from an ordering of five band ids the page does not spell out",
    "GNDVI": "its arcpy.ia page could not be read (404 on 2026-10-29), so its parameter names and defaults are unknown",
    "MNDWI": "its arcpy.ia page could not be read (404 on 2026-10-29), so its parameter names and defaults are unknown",
    "FloatDivide": "its reference page could not be read (404 on 2026-10-29)",
    "FloorDivide": "its reference page could not be read (404 on 2026-10-29)",
    "InList": "its reference page could not be read (404 on 2026-10-29)",
})


def __getattr__(name):
    if name.startswith("_"):
        raise AttributeError(name)

    why = _REFUSED.get(name)

    def missing(*_, **__):
        raise NotBuiltError(f"arcpy.ia.{name} is not built in grapy" + (f": {why}." if why else " yet."))

    return missing
