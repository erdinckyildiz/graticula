"""arcpy.ia — Image Analyst. Its functions that Spatial Analyst shares are answered by grapy.sa, which is what
arcpy documents them as (ADR-224); the rest are refused by name until built."""
from __future__ import annotations

from ._runtime import NotBuiltError
from .sa import (  # noqa: F401 — re-exported under arcpy.ia's names
    ACos, ASin, ATan, ATan2, Abs, Aspect, BooleanAnd, BooleanNot, BooleanOr, BooleanXOr, CellStatistics, Con, Cos,
    CosH, Diff, Divide, EqualTo, Exp, Exp10, Exp2, Float, FocalStatistics, GreaterThan, GreaterThanEqual,
    HighestPosition, Hillshade, Int, IsNull, LessThan, LessThanEqual, Ln, Log10, Log2, LowestPosition, Minus, Mod,
    NbrAnnulus, NbrCircle, NbrRectangle, Negate, NotEqual, Pick, Plus, Power, Raster, RasterCalculator, RemapRange,
    RemapValue, RoundDown, RoundUp, SetNull, Sin, SinH, Slope, Square, Tan, TanH, Test, Times, ZonalStatistics,
    ZonalStatisticsAsTable,
)


def __getattr__(name):
    if name.startswith("_"):
        raise AttributeError(name)

    def missing(*_, **__):
        raise NotBuiltError(f"arcpy.ia.{name} is not built in grapy yet.")

    return missing
