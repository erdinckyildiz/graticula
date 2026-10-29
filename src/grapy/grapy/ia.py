"""arcpy.ia — Image Analyst. Its functions that Spatial Analyst shares are answered by grapy.sa, which is what
arcpy documents them as (ADR-224); the rest are refused by name until built."""
from __future__ import annotations

from ._runtime import NotBuiltError
from .sa import (  # noqa: F401 — re-exported under arcpy.ia's names
    ACos, ACosH, ASin, ASinH, ATan, ATan2, ATanH, Abs, Aggregate, Arithmetic, Aspect, BAI, BandArithmetic,
    BitwiseAnd, BitwiseLeftShift, BitwiseNot, BitwiseOr, BitwiseRightShift, BitwiseXOr, BooleanAnd, BooleanNot,
    BooleanOr, BooleanXOr, Buffered, CIg, CIre, CellStatistics, ClayMinerals, CompositeBand, Con, Contour, Cos,
    CosH, Curvature, Diff, Divide, EVI, EqualTo, EqualToFrequency, Exp, Exp10, Exp2, Extent, ExtractBand,
    FerrousMinerals, Float, FocalStatistics, FuzzyGaussian, FuzzyLarge, FuzzyLinear, FuzzyMSLarge, FuzzyMSSmall,
    FuzzyNear, FuzzySmall, GEMI, GVITM, GreaterThan, GreaterThanEqual, GreaterThanFrequency, HighestPosition,
    Hillshade, Int, IronOxide, IsNull, KrigingModelOrdinary, KrigingModelUniversal, LessThan, LessThanEqual,
    LessThanFrequency, Ln, Log10, Log2, LowestPosition, MSAVI, Majority, Max, Mean, Median, Merge, Min, Minority,
    Minus, Mod, NBR, NDBI, NDMI, NDSI, NDVI, NDVIre, NDWI, NbrAnnulus, NbrCircle, NbrIrregular, NbrRectangle,
    Negate, NotEqual, Over, PVI, Percentile, Pick, Plus, Point, Popularity, Power, RTVICore, RadiusFixed,
    RadiusVariable, Range, Rank, Raster, RasterCalculator, Remap, RemapRange, RemapValue, RoundDown, RoundUp, SAVI,
    SR, SRre, SetNull, Sin, SinH, Slope, Square, SquareRoot, StdDev, Stretch, SubsetBands, Sum, TSAVI, Tan, TanH,
    Test, TfExponential, TfGaussian, TfLarge, TfLinear, TfLogarithm, TfLogisticDecay, TfLogisticGrowth, TfMSLarge,
    TfMSSmall, TfNear, TfPower, TfSmall, TfSymmetricLinear, Threshold, Times, TransposeBits, VARI, Variety, WSTable,
    WeightedSum, ZonalStatistics, ZonalStatisticsAsTable,
)
from .sa import _NOT_YET, _REFUSED  # noqa: E402 — the reasons are shared with arcpy.sa


def __getattr__(name):
    if name.startswith("_"):
        raise AttributeError(name)
    why = _REFUSED.get(name)
    later = _NOT_YET.get(name)

    def missing(*_, **__):
        if why:
            raise NotBuiltError(f"arcpy.ia.{name} is not built in grapy: {why}.")
        raise NotBuiltError(f"arcpy.ia.{name} is not built in grapy yet" + (f": {later}." if later else "."))

    return missing
