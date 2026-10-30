"""The size of every unit grapy's tools name, in metres or square metres, as ArcGIS Pro measures them — D-323.

One table, because nine copies of it disagreed. Every number below was measured on 2026-10-30 in ArcGIS Pro 3.7
(``arcpy.GetInstallInfo()["Version"]``) by running the tool itself on a geometry of known size — a 1,000 km line, a
1,000 km square, a point buffered by a million feet — in a metre-based projection (UTM 35N), where the two parts in a
million between a US survey and an international foot is far above any rounding. Nothing here is read from a
documentation page; where a page and a run disagree, the run is what is written.

What the runs found, and why a tool's keywords are mapped one by one rather than read by a single rule:

* In a **linear unit** (``"100 Feet"`` in Buffer, Near, GeneratePointsAlongLines, MultipleRingBuffer …), in
  ``LinearUnitConversionFactor`` and in ``Geometry.getLength``, the bare ``Feet``, ``Miles``, ``Yards``, ``Inches`` and
  ``NauticalMiles`` are **US survey** units; ``FeetInt`` … are international. ``Points`` is a US survey inch / 72.
  ``NauticalMiles`` is 1853.248 m and ``NauticalMilesInt`` 1852.
* In an **areal unit** (``"100 SquareFeet"``), in ``ArealUnitConversionFactor`` and in ``Geometry.getArea``, the bare
  ``SquareFeet``, ``Acres``, ``SquareMiles``, ``SquareYards`` and ``SquareInches`` are **international**; the US survey
  ones are spelled ``…US``. ``SquareNauticalMiles`` is 1852² — international, unlike the linear one. Pro's square mile
  is 2,589,988.11 m², not the exact 2,589,988.110336.
* A tool's own keyword list follows neither rule reliably, so each is measured: BearingDistanceToLine's ``FEET`` is
  international but its ``MILES`` US survey; AddGeometryAttributes' ``YARDS`` is a US survey yard and its
  ``SQUARE_YARDS`` an international one; the Spatial Analyst densities' ``SQUARE_FEET`` and ``ACRES`` are US survey while
  TabulateIntersection's are international. The maps below are those measurements, keyword by keyword.

The scripts that took the measurements are not in the repository (they need ArcGIS Pro); their results are in D-323.
"""
from __future__ import annotations

US_FOOT = 1200 / 3937  # m, the US survey foot

# ---- the units themselves -------------------------------------------------------------------------------------------

KILOMETRE, METRE, DECIMETRE, CENTIMETRE, MILLIMETRE = 1000.0, 1.0, 0.1, 0.01, 0.001
FOOT_INT, YARD_INT, MILE_INT, INCH_INT, NAUTICAL_MILE_INT = 0.3048, 0.9144, 1609.344, 0.0254, 1852.0
FOOT_US, YARD_US, MILE_US, INCH_US = US_FOOT, 3 * US_FOOT, 5280 * US_FOOT, US_FOOT / 12
NAUTICAL_MILE_US = 1853.248  # measured: LinearUnitConversionFactor("NauticalMiles", "Meters")
POINT = INCH_US / 72  # measured: 0.0003527784833347445, a US survey inch / 72
FATHOM_US = 6 * US_FOOT  # measured: Adjust3DZ's FATHOMS

SQUARE_MILE_INT = 2589988.11  # measured, Pro's own rounding of 1609.344²
ACRE_INT, ACRE_US = 4046.8564224, 43560 * US_FOOT ** 2

# ---- the Linear Unit and Areal Unit data types, and the geometry methods ---------------------------------------------

# The keywords LinearUnitConversionFactor takes, upper case: the Linear Unit data type's words.
LINEAR = {
    "KILOMETERS": KILOMETRE, "METERS": METRE, "DECIMETERS": DECIMETRE, "CENTIMETERS": CENTIMETRE,
    "MILLIMETERS": MILLIMETRE,
    "FEET": FOOT_US, "YARDS": YARD_US, "MILES": MILE_US, "INCHES": INCH_US, "NAUTICALMILES": NAUTICAL_MILE_US,
    "FEETUS": FOOT_US, "YARDSUS": YARD_US, "MILESUS": MILE_US, "INCHESUS": INCH_US, "NAUTICALMILESUS": NAUTICAL_MILE_US,
    "FEETINT": FOOT_INT, "YARDSINT": YARD_INT, "MILESINT": MILE_INT, "INCHESINT": INCH_INT,
    "NAUTICALMILESINT": NAUTICAL_MILE_INT,
    "POINTS": POINT,
}

# The keywords ArealUnitConversionFactor takes, upper case: the Areal Unit data type's words.
AREAL = {
    "SQUAREKILOMETERS": 1e6, "HECTARES": 1e4, "ARES": 100.0, "SQUAREMETERS": 1.0, "SQUAREDECIMETERS": 0.01,
    "SQUARECENTIMETERS": 1e-4, "SQUAREMILLIMETERS": 1e-6,
    "SQUAREFEET": FOOT_INT ** 2, "SQUAREYARDS": YARD_INT ** 2, "SQUAREMILES": SQUARE_MILE_INT, "ACRES": ACRE_INT,
    "SQUAREINCHES": INCH_INT ** 2, "SQUARENAUTICALMILES": NAUTICAL_MILE_INT ** 2,
    "SQUAREFEETINT": FOOT_INT ** 2, "SQUAREYARDSINT": YARD_INT ** 2, "SQUAREMILESINT": SQUARE_MILE_INT,
    "ACRESINT": ACRE_INT, "SQUAREINCHESINT": INCH_INT ** 2,
    "SQUAREFEETUS": FOOT_US ** 2, "SQUAREYARDSUS": YARD_US ** 2, "SQUAREMILESUS": MILE_US ** 2, "ACRESUS": ACRE_US,
    "SQUAREINCHESUS": INCH_US ** 2, "SQUARENAUTICALMILESUS": NAUTICAL_MILE_US ** 2,
}

# Spellings grapy has always taken beside the documented ones. Pro refuses ``Feet_Int`` and ``Feet_US`` in a linear
# unit, but reading them as the unit they name is never a different number, so they are kept rather than broken.
_LINEAR_ALIASES = {
    "FOOT": "FEET", "INCH": "INCHES", "YARD": "YARDS", "MILE": "MILES", "METER": "METERS", "KILOMETER": "KILOMETERS",
    "FEET_INT": "FEETINT", "INTERNATIONALFEET": "FEETINT", "FEET_US": "FEETUS", "USSURVEYFEET": "FEETUS",
    "US_SURVEY_FEET": "FEETUS", "MILES_INT": "MILESINT", "INTERNATIONALMILES": "MILESINT", "MILES_US": "MILESUS",
    "USSURVEYMILES": "MILESUS", "NAUTICAL_MILES": "NAUTICALMILES",
}


def linear(word):
    """Metres in one ``word`` of a linear unit (``"Feet"`` of ``"100 Feet"``), or None for a word it is not."""
    key = str(word).upper().replace(" ", "")
    key = _LINEAR_ALIASES.get(key, key)
    if key in LINEAR:
        return LINEAR[key]
    if key + "S" in LINEAR:  # a singular, as "Meter" — Pro takes "Foot" and "feet" alike
        return LINEAR[key + "S"]
    return None


def areal(word):
    """Square metres in one ``word`` of an areal unit (``"Acres"``, ``"SquareFeet"``), or None for a word it is not.
    Underscores are ignored, as grapy always has (``SQUARE_FEET``)."""
    return AREAL.get(str(word).upper().replace(" ", "").replace("_", ""))


# ---- each tool's own keywords, as measured -------------------------------------------------------------------------

# Near and GenerateNearTable, distance_unit.
NEAR = {"KILOMETERS": KILOMETRE, "METERS": METRE, "NAUTICALMILESINT": NAUTICAL_MILE_INT, "MILESINT": MILE_INT,
        "YARDSINT": YARD_INT, "FEETINT": FOOT_INT, "NAUTICALMILES": NAUTICAL_MILE_US, "MILES": MILE_US,
        "YARDS": YARD_US, "FEET": FOOT_US}

# GenerateOriginDestinationLinks, distance_unit: every bare name a US survey unit.
OD_LINKS = {"KILOMETERS": KILOMETRE, "METERS": METRE, "MILES": MILE_US, "NAUTICALMILES": NAUTICAL_MILE_US,
            "YARDS": YARD_US, "FEET": FOOT_US}

# PolygonNeighbors and PairwisePolygonNeighbors out_linear_units, and TabulateIntersection's linear out_units.
NEIGHBOR_LINEAR = {
    "INCHES": INCH_US, "POINTS": POINT, "FEET": FOOT_US, "YARDS": YARD_US, "MILES": MILE_US,
    "NAUTICAL_MILES": NAUTICAL_MILE_US, "INCHES_INTERNATIONAL": INCH_INT, "FEET_INTERNATIONAL": FOOT_INT,
    "YARDS_INTERNATIONAL": YARD_INT, "MILES_INTERNATIONAL": MILE_INT, "NAUTICAL_MILES_INTERNATIONAL": NAUTICAL_MILE_INT,
    "MILLIMETERS": MILLIMETRE, "CENTIMETERS": CENTIMETRE, "METERS": METRE, "KILOMETERS": KILOMETRE,
    "DECIMETERS": DECIMETRE,
}

# PolygonNeighbors and PairwisePolygonNeighbors out_area_units, and TabulateIntersection's areal out_units: the bare
# names international, the US survey ones ``…_US``.
NEIGHBOR_AREAL = {
    "SQUARE_INCHES": INCH_INT ** 2, "SQUARE_FEET": FOOT_INT ** 2, "SQUARE_YARDS": YARD_INT ** 2, "ACRES": ACRE_INT,
    "SQUARE_MILES": SQUARE_MILE_INT, "SQUARE_NAUTICAL_MILES": NAUTICAL_MILE_INT ** 2, "SQUARE_MILLIMETERS": 1e-6,
    "SQUARE_DECIMETERS": 0.01, "SQUARE_CENTIMETERS": 1e-4, "SQUARE_METERS": 1.0, "SQUARE_KILOMETERS": 1e6,
    "ARES": 100.0, "HECTARES": 1e4, "SQUARE_INCHES_US": INCH_US ** 2, "SQUARE_FEET_US": FOOT_US ** 2,
    "SQUARE_YARDS_US": YARD_US ** 2, "ACRES_US": ACRE_US, "SQUARE_MILES_US": MILE_US ** 2,
    "SQUARE_NAUTICAL_MILES_US": NAUTICAL_MILE_US ** 2,
}

# CalculateGeometryAttributes, length_unit and area_unit.
CALCULATE_LENGTH = {"KILOMETERS": KILOMETRE, "METERS": METRE, "NAUTICAL_MILES_INT": NAUTICAL_MILE_INT,
                    "MILES_INT": MILE_INT, "YARDS_INT": YARD_INT, "FEET_INT": FOOT_INT,
                    "NAUTICAL_MILES": NAUTICAL_MILE_US, "MILES_US": MILE_US, "YARDS": YARD_US, "FEET_US": FOOT_US}
CALCULATE_AREA = {"SQUARE_KILOMETERS": 1e6, "HECTARES": 1e4, "SQUARE_METERS": 1.0, "SQUARE_MILES_INT": SQUARE_MILE_INT,
                  "SQUARE_NAUTICAL_MILES": NAUTICAL_MILE_INT ** 2, "ACRES": ACRE_INT, "SQUARE_YARDS": YARD_INT ** 2,
                  "SQUARE_FEET_INT": FOOT_INT ** 2, "SQUARE_MILES_US": MILE_US ** 2,
                  "SQUARE_NAUTICAL_MILES_US": NAUTICAL_MILE_US ** 2, "ACRES_US": ACRE_US,
                  "SQUARE_YARDS_US": YARD_US ** 2, "SQUARE_FEET_US": FOOT_US ** 2}

# AddGeometryAttributes, Length_Unit and Area_Unit.
ADD_GEOMETRY_LENGTH = {"FEET_US": FOOT_US, "METERS": METRE, "KILOMETERS": KILOMETRE, "MILES_US": MILE_US,
                       "NAUTICAL_MILES": NAUTICAL_MILE_US, "YARDS": YARD_US}
ADD_GEOMETRY_AREA = {"ACRES": ACRE_INT, "HECTARES": 1e4, "SQUARE_MILES_US": MILE_US ** 2, "SQUARE_KILOMETERS": 1e6,
                     "SQUARE_METERS": 1.0, "SQUARE_FEET_US": FOOT_US ** 2, "SQUARE_YARDS": YARD_INT ** 2,
                     "SQUARE_NAUTICAL_MILES": NAUTICAL_MILE_INT ** 2}

# Adjust3DZ, from_units and to_units: every imperial name a US survey unit, a fathom six of its feet.
ADJUST_Z = {"MILLIMETERS": MILLIMETRE, "CENTIMETERS": CENTIMETRE, "METERS": METRE, "INCHES": INCH_US,
            "FEET": FOOT_US, "YARDS": YARD_US, "FATHOMS": FATHOM_US}

# BearingDistanceToLine and TableToEllipse, distance_units: FEET international beside US_SURVEY_FEET, but MILES a US
# survey mile and NAUTICAL_MILES the international one.
BEARING_DISTANCE = {"METERS": METRE, "KILOMETERS": KILOMETRE, "MILES": MILE_US, "NAUTICAL_MILES": NAUTICAL_MILE_INT,
                    "FEET": FOOT_INT, "US_SURVEY_FEET": FOOT_US}

# KernelDensity, PointDensity and LineDensity, area_unit_scale_factor: every imperial name a US survey unit.
DENSITY_AREA = {"SQUARE_KILOMETERS": 1e6, "SQUARE_METERS": 1.0, "SQUARE_CENTIMETERS": 1e-4, "SQUARE_MILLIMETERS": 1e-6,
                "SQUARE_MILES": MILE_US ** 2, "SQUARE_YARDS": YARD_US ** 2, "SQUARE_FEET": FOOT_US ** 2,
                "SQUARE_INCHES": INCH_US ** 2, "HECTARES": 1e4, "ACRES": ACRE_US}

# arcpy.nax's DistanceUnits, and the units of a network dataset's own distance attributes: every imperial name a US
# survey unit (a route of a known 1,000 km solved in each, and an attribute written in each and read back in metres).
NETWORK = {"METERS": METRE, "KILOMETERS": KILOMETRE, "FEET": FOOT_US, "YARDS": YARD_US, "MILES": MILE_US,
           "NAUTICALMILES": NAUTICAL_MILE_US, "INCHES": INCH_US, "CENTIMETERS": CENTIMETRE, "MILLIMETERS": MILLIMETRE,
           "DECIMETERS": DECIMETRE}

# SummarizeWithin follows the server's own Summarize Within (Q-193), so these equal AnalysisEndpoints.SummaryLengthUnits
# and AnalysisEndpoints.AreaUnits in Graticula.Host. Both follow Pro's SummarizeWithin, measured the same day: its FEET,
# YARDS and MILES are US survey units and its areas international (owner decision, 2026-10-30).
SERVER_LENGTH = {"METERS": METRE, "KILOMETERS": KILOMETRE, "FEET": FOOT_US, "YARDS": YARD_US, "MILES": MILE_US,
                 "NAUTICALMILES": NAUTICAL_MILE_INT}
SERVER_AREA = {"SQUAREMETERS": 1.0, "SQUAREKILOMETERS": 1e6, "HECTARES": 1e4, "ACRES": ACRE_INT,
               "SQUAREMILES": 2_589_988.110336, "SQUAREYARDS": YARD_INT ** 2, "SQUAREFEET": FOOT_INT ** 2}
