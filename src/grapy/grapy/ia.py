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
    LessThanFrequency, Ln, Log10, Log2, Lookup, LowestPosition, MSAVI, Majority, Max, Mean, Median, Merge, Min,
    Minority, Minus, Mod, NBR, NDBI, NDMI, NDSI, NDVI, NDVIre, NDWI, NbrAnnulus, NbrCircle, NbrIrregular,
    NbrRectangle, Negate, NotEqual, Over, PVI, Percentile, Pick, Plus, Point, Popularity, Power, RTVICore,
    RadiusFixed, RadiusVariable, Range, Rank, Raster, RasterCalculator, Remap, RemapRange, RemapValue, RoundDown,
    RoundUp, SAVI, SR, SRre, SetNull, Sin, SinH, Slope, Square, SquareRoot, StdDev, Stretch, SubsetBands, Sum,
    TSAVI, Tan, TanH, Test, TfExponential, TfGaussian, TfLarge, TfLinear, TfLogarithm, TfLogisticDecay,
    TfLogisticGrowth, TfMSLarge, TfMSSmall, TfNear, TfPower, TfSmall, TfSymmetricLinear, Threshold, Times,
    TransposeBits, VARI, Variety, WSTable, WeightedSum, ZonalStatistics, ZonalStatisticsAsTable,
)
from .sa import _NOT_YET, _REFUSED  # noqa: E402 — the reasons are shared with arcpy.sa


# ---- multidimensional rasters (ADR-236)
from . import _change as _change_tools, _collection as _collection_tools, _mdimtools as _md_tools  # noqa: E402

AggregateMultidimensionalRaster = _md_tools.AggregateMultidimensionalRaster
Anomaly = _md_tools.Anomaly
ArgStatistics = _md_tools.ArgStatistics
DimensionalMovingStatistics = _md_tools.DimensionalMovingStatistics
FindArgumentStatistics = _md_tools.FindArgumentStatistics
GenerateMultidimensionalAnomaly = _md_tools.GenerateMultidimensionalAnomaly
GenerateTrend = _md_tools.GenerateTrend
GenerateTrendRaster = _md_tools.GenerateTrendRaster
PredictUsingTrend = _md_tools.PredictUsingTrend
PredictUsingTrendRaster = _md_tools.PredictUsingTrendRaster
TrendToRGB = _md_tools.TrendToRGB
MultidimensionalPrincipalComponents = _md_tools.MultidimensionalPrincipalComponents
MultidimensionalRasterCorrelation = _md_tools.MultidimensionalRasterCorrelation
RasterToXarray = _md_tools.RasterToXarray
XarrayToRaster = _md_tools.XarrayToRaster
InterpolateFromSpatiotemporalPoints = _md_tools.InterpolateFromSpatiotemporalPoints
RasterCollection = _collection_tools.RasterCollection
ComputeChange = _change_tools.ComputeChange
ComputeChangeRaster = _change_tools.ComputeChangeRaster
AnalyzeChangesUsingCCDC = _change_tools.AnalyzeChangesUsingCCDC
AnalyzeChangesUsingLandTrendr = _change_tools.AnalyzeChangesUsingLandTrendr
DetectChangeUsingChangeAnalysis = _change_tools.DetectChangeUsingChangeAnalysis
DetectChangeUsingChangeAnalysisRaster = _change_tools.DetectChangeUsingChangeAnalysisRaster


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
