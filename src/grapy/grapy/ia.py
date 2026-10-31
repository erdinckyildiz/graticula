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
from .sa import (  # noqa: E402,F401 — the path distance factor classes, arcpy.ia's as well (ADR-241)
    HfBinary, HfForward, HfInverseLinear, HfLinear, HfTable, VfBidirHikingTime, VfBinary, VfCos, VfCosSec,
    VfHikingTime, VfInverseLinear, VfLinear, VfSec, VfSecCos, VfSymInverseLinear, VfSymLinear, VfTable,
)
from .sa import (  # noqa: E402,F401 — ADR-242, shared with arcpy.sa
    CombinatorialAnd, CombinatorialOr, CombinatorialXOr, ComputeSegmentAttributes, RemoveRasterSegmentTilingArtifacts,
    SegMeanShift, SegmentMeanShift, TopoBoundary, TopoCliff, TopoCoast, TopoContour, TopoExclusion, TopoLake,
    TopoPointElevation, TopoSink, TopoStream,
)


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
from . import _mdmore as _md_more  # noqa: E402 — D-329's sweep
from . import _classify as _cls, _solartools as _solar_tools  # noqa: E402 — ADR-243

TrainIsoClusterClassifier = _cls.TrainIsoClusterClassifier
TrainKNearestNeighborClassifier = _cls.TrainKNearestNeighborClassifier
TrainMaximumLikelihoodClassifier = _cls.TrainMaximumLikelihoodClassifier
TrainSupportVectorMachineClassifier = _cls.TrainSupportVectorMachineClassifier
ClassifyRaster = _cls.ClassifyRaster
Classify = _cls.Classify
MLClassify = _cls.MLClassifyFunction
Pansharpen = _cls.Pansharpen
RegionGrow = _cls.RegionGrow
TimeWithinDay = _solar_tools.TimeWithinDay
TimeMultipleDays = _solar_tools.TimeMultipleDays
TimeSpecialDays = _solar_tools.TimeSpecialDays
TimeWholeYear = _solar_tools.TimeWholeYear
InspectTrainingSamples = _cls.InspectTrainingSamples

Foreach = _md_more.Foreach
Subset = _md_more.Subset
SummarizeCategoricalRaster = _md_more.SummarizeCategoricalRaster
Complex = _md_more.Complex


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


# ---- SAR and motion imagery (ADR-237): built in _sar, _sartools and _video, answered here under arcpy.ia's names


from . import _sartools as _sar_tools, _video  # noqa: E402
from ._runtime import equivalent as _equivalent  # noqa: E402

ApplyRadiometricCalibration = _sar_tools.ApplyRadiometricCalibration
RemoveThermalNoise = _sar_tools.RemoveThermalNoise
Multilook = _sar_tools.Multilook
ConvertSARUnits = _sar_tools.ConvertSARUnits
GenerateMaskedSARRaster = _sar_tools.GenerateMaskedSARRaster
GenerateMultitemporalCoherence = _sar_tools.GenerateMultitemporalCoherence
ApplyOrbitCorrection = _sar_tools.ApplyOrbitCorrection
Deburst = _equivalent(*_sar_tools.DEBURST)(_sar_tools.Deburst)
Despeckle = _equivalent(*_sar_tools.DESPECKLE)(_sar_tools.Despeckle)
ComputeCoherence = _equivalent(*_sar_tools.COHERENCE)(_sar_tools.ComputeCoherence)
GenerateInterferogram = _equivalent(*_sar_tools.INTERFEROGRAM)(_sar_tools.GenerateInterferogram)
ApplyComplexDataFilter = _equivalent(*_sar_tools.GOLDSTEIN)(_sar_tools.ApplyComplexDataFilter)
UnwrapPhase = _equivalent(*_sar_tools.UNWRAP)(_sar_tools.UnwrapPhase)
ComputeSARIndices = _equivalent(*_sar_tools.INDICES)(_sar_tools.ComputeSARIndices)
DetectBrightOceanObjects = _equivalent(*_sar_tools.OCEAN_OBJECTS)(_sar_tools.DetectBrightOceanObjects)
DetectDarkOceanAreas = _equivalent(*_sar_tools.DARK_AREAS)(_sar_tools.DetectDarkOceanAreas)
ExtractOceanWinds = _equivalent(*_sar_tools.WINDS)(_sar_tools.ExtractOceanWinds)
ApplyGeometricTerrainCorrection = _equivalent(*_sar_tools.GTC)(_sar_tools.ApplyGeometricTerrainCorrection)
ApplyRadiometricTerrainFlattening = _equivalent(*_sar_tools.FLATTENING)(_sar_tools.ApplyRadiometricTerrainFlattening)
ApplyCoregistration = _equivalent(*_sar_tools.COREGISTRATION)(_sar_tools.ApplyCoregistration)
GenerateRadiometricTerrainCorrectedData = _equivalent(*_sar_tools.RTC)(
    _sar_tools.GenerateRadiometricTerrainCorrectedData)
RadarCalibration = _equivalent(*_sar_tools.CALIBRATION_FUNCTION)(_sar_tools.RadarCalibration)
ConvertVideoMetadata = _video.ConvertVideoMetadata

for _built in ("ApplyRadiometricCalibration", "RemoveThermalNoise", "Multilook", "ConvertSARUnits",
               "GenerateMaskedSARRaster", "GenerateMultitemporalCoherence", "ApplyOrbitCorrection", "Deburst",
               "Despeckle", "ComputeCoherence", "GenerateInterferogram", "ApplyComplexDataFilter", "UnwrapPhase",
               "ComputeSARIndices", "DetectBrightOceanObjects", "DetectDarkOceanAreas", "ExtractOceanWinds",
               "ApplyGeometricTerrainCorrection", "ApplyRadiometricTerrainFlattening", "ApplyCoregistration",
               "GenerateRadiometricTerrainCorrectedData", "RadarCalibration", "ConvertVideoMetadata"):
    _REFUSED.pop(_built, None)

_NO_VIDEO = ("and a user tool on this server has no video demuxer, muxer or decoder: none is among grapy's libraries, "
             "and grapy does not vendor one (ADR-237 §5.6)")
_NO_S1_PAGE = ("its arcpy.ia reference page could not be read (doc.esri.com's arcpy/image-analyst and raster function "
               "pages answered 404 on 2026-10-31), so its parameters are unknown")
_REFUSED.update({
    "DownloadOrbitFile": "it downloads orbit files from the Copernicus Data Space Ecosystem, and a user tool on this "
                         "server has no network (ADR-223 §5.3); ApplyOrbitCorrection applies an orbit file the tool "
                         "is given",
    "S1RadiometricCalibration": _NO_S1_PAGE + "; ApplyRadiometricCalibration and RadarCalibration calibrate "
                                              "Sentinel-1 data",
    "S1ThermalNoiseRemoval": _NO_S1_PAGE + "; RemoveThermalNoise removes Sentinel-1 thermal noise",
    "ExtractVideoFramesToImages": "it decodes the frames of a video file (.ts, .mp4, .avi …) into images, " + _NO_VIDEO,
    "GenerateVideoMetadataStationary": "it writes a .ts video file, its input's video stream multiplexed with "
                                       "generated MISB ST 0601 metadata, " + _NO_VIDEO,
    "VideoMetadataToFeatureClass": "it reads the MISB ST 0601 KLV metadata from inside a video container (.ts, .mp4, "
                                   ".mkv …), " + _NO_VIDEO + "; ConvertVideoMetadata works on a metadata .csv",
    "VideoMultiplexer": "it writes a .ts video file, its input's video stream multiplexed with KLV metadata, from "
                        "containers as varied as .mp4, .avi and .wmv, " + _NO_VIDEO,
})


# ---- ADR-245: the raster functions whose pages answered 404 on 2026-10-29, read again on 2026-10-31
from . import _imagefns as _image_fns, sa as _sa  # noqa: E402

RasterInfo = _sa.RasterInfo
UnitConversion = _sa.UnitConversion
HeatIndex = _sa.HeatIndex
WindChill = _sa.WindChill
SpectralConversion = _sa.SpectralConversion
VectorField = _sa.VectorField
InList = _sa.InList
ZonalRemap = _sa.ZonalRemap
ColormapToRGB = _sa.ColormapToRGB
ApplyEnvironment = _sa.ApplyEnvironment
ColorspaceConversion = _sa.ColorspaceConversion
Speckle = _sa.Speckle
ElevationVoidFill = _sa.ElevationVoidFill
LinearUnmixing = _sa.LinearUnmixing
Resample = _sa.Resample
GNDVI = _sa.GNDVI
PixelBlock = _image_fns.PixelBlock
PixelBlockCollection = _image_fns.PixelBlockCollection
Mensuration = _image_fns.Mensuration
