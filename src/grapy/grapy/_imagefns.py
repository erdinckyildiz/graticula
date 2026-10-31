"""Raster functions of arcpy.ia and arcpy.sa whose pages were read again on 2026-10-31 (ADR-245).

Each was refused on 2026-10-29 because its reference page answered 404, or because it "makes a display rendering".
The pages are back at doc.esri.com under new slugs (arcpy/image-analyst/heat-index.html, …) and each name here follows
its arcpy page and the raster function page it points to. Equivalents — where the page names a method without giving
it closely enough to reproduce — are marked by the module that answers the name, through ``_runtime.equivalent``, with
the (method, source) pairs below.
"""
from __future__ import annotations

import math
import os
import random

import numpy as np

from ._raster import Raster, _align, _Grid, _open_bands, as_raster
from ._runtime import ExecuteError, NotBuiltError, env

# (method, source) of each equivalent, read by sa and ia when they mark the name
SPECKLE = ("Lee (1980), Enhanced Lee and Gamma-MAP (Lopes et al. 1990), Frost (1982), Kuan (1985) and Refined Lee "
           "(Lee 1981) local-statistics filters, as arcpy.ia.Despeckle builds them",
           "Lee, IEEE PAMI 2(2) 1980; Lee, CGIP 17 1981; Lopes, Touzi and Nezry, IEEE TGRS 28(6) 1990; Frost et al., "
           "IEEE PAMI 4(2) 1982; Kuan et al., IEEE PAMI 7(2) 1985")
VOID_FILL = ("inverse distance weighting (power 2) from the cells bordering each void",
             "Shepard, Proc. 23rd ACM National Conference, 1968")
UNMIXING = ("least squares unmixing: unconstrained, sum-to-one by Lagrange multiplier, non-negative by Lawson-Hanson "
            "NNLS, both by fully constrained least squares",
            "Keshava and Mustard, IEEE Signal Processing Magazine 19(1) 2002; Lawson and Hanson, Solving Least Squares "
            "Problems, 1974, ch. 23; Heinz and Chang, IEEE TGRS 39(3) 2001")
RESAMPLE = ("nearest neighbour, bilinear and Keys cubic convolution (a = −0.5) on cell centres; majority over the 4×4 "
            "nearest centres; average, minimum and maximum over the input cells whose centres the output cell covers",
            "Keys, IEEE TASSP 29(6) 1981; Parker, Kenyon and Troxel, IEEE TMI 2(1) 1983")
HSV = ("Smith's hexcone RGB↔HSV, hue rescaled to 0–240 and saturation and value to 0–255 as the Color Model Conversion "
       "page gives them", "Smith, Color Gamut Transform Pairs, SIGGRAPH '78, 1978")


def _refuse(tool, parameter, value, why=""):
    raise NotBuiltError(f"arcpy.ia.{tool}: {parameter} '{value}' is not built in grapy{': ' + why if why else ''}.")


def _given(value):
    """Whether a parameter or environment setting is set. A Raster compares cell by cell, so it is never tested with
    ``in`` against strings."""
    if value is None:
        return False
    if isinstance(value, str):
        return value.strip() not in ("", "#")
    return True


def _word(value, default):
    return str(value if _given(value) else default).strip()


def _float_band(raster, tool):
    """A single-band raster as float64 with NoData as NaN, and its grid."""
    r = as_raster(raster, tool)
    return np.where(r._mask, np.nan, r._array.astype(np.float64)), r._grid


def _float_raster(values, grid, dtype=np.float32):
    values = np.asarray(values, dtype=np.float64)
    mask = ~np.isfinite(values)
    return Raster._of(np.where(mask, 0, values).astype(dtype), mask, grid)


def _pair(a, b, tool):
    """Two single-band rasters on the grid they share, as float with NoData NaN."""
    ra, rb = as_raster(a, tool), as_raster(b, tool)
    (xa, xb), (ma, mb), grid = _align([ra, rb], tool)
    return np.where(ma, np.nan, xa.astype(np.float64)), np.where(mb, np.nan, xb.astype(np.float64)), grid


# ---- units: exact definitions (NIST SP 811, 2008, appendix B; the international foot and mile, 1959)

_LENGTH = {"centimeters": 0.01, "decimeters": 0.1, "feet": 0.3048, "inches": 0.0254, "kilometers": 1000.0,
           "meters": 1.0, "miles": 1609.344, "millimeters": 0.001, "nauticalmiles": 1852.0, "yards": 0.9144}
_SPEED = {"meterspersecond": 1.0, "kilometersperhour": 1000.0 / 3600.0, "knots": 1852.0 / 3600.0,
          "feetpersecond": 0.3048, "milesperhour": 1609.344 / 3600.0}
_SPEED_WORDS = {"mph": "milesperhour", "km/h": "kilometersperhour", "kmh": "kilometersperhour",
                "m/s": "meterspersecond", "ft/s": "feetpersecond", "kn": "knots", "kt": "knots", "knot": "knots"}
_TEMPERATURE = ("celsius", "fahrenheit", "kelvin")


def _key(unit):
    return str(unit).replace(" ", "").replace("_", "").replace("'", "").lower()


def _to_kelvin(values, unit, tool):
    u = _key(unit)
    if u == "celsius":
        return values + 273.15
    if u == "fahrenheit":
        return (values - 32.0) * 5.0 / 9.0 + 273.15
    if u == "kelvin":
        return values
    raise ExecuteError(f"{tool}: '{unit}' is not a temperature unit; Celsius, Fahrenheit and Kelvin are.")


def _from_kelvin(values, unit, tool):
    u = _key(unit)
    if u == "celsius":
        return values - 273.15
    if u == "fahrenheit":
        return (values - 273.15) * 9.0 / 5.0 + 32.0
    if u == "kelvin":
        return values
    raise ExecuteError(f"{tool}: '{unit}' is not a temperature unit; Celsius, Fahrenheit and Kelvin are.")


def _speed_factor(unit, tool):
    u = _key(unit)
    u = _SPEED_WORDS.get(u, u)
    if u not in _SPEED:
        raise ExecuteError(f"{tool}: '{unit}' is not a wind speed unit; mph, km/h, m/s, ft/s and kn are.")
    return _SPEED[u]


def UnitConversion(raster, from_unit=None, to_unit=None):
    """Pixel values converted between two units of one kind — distance, speed or temperature — as the page lists them.
    The factors are the units' exact definitions (NIST SP 811): the international foot (0.3048 m), yard, inch and mile,
    the nautical mile (1852 m) and the knot (one nautical mile an hour); temperatures through kelvin.

    INFERRED: the answer is 32-bit floating point; NoData stays NoData; both units must be given (the page's default is
    None and it does not say what None converts); unit names match without regard to case. Decimeters and kilometers,
    which the raster function lists and the arcpy page does not, are taken too. The raster function's SAR units are not
    on the arcpy page and are refused."""
    tool = "UnitConversion"
    if not _given(from_unit) or not _given(to_unit):
        raise ExecuteError(f"{tool}: from_unit and to_unit are both needed; the page gives no conversion for None.")
    a, b = _key(from_unit), _key(to_unit)
    if any(u in ("amplitude", "amplitudedb", "intensity", "intensitydb") for u in (a, b)):
        _refuse(tool, "unit", from_unit if a.startswith(("amp", "int")) else to_unit,
                "SAR units are on the raster function's page only, not arcpy's")
    values, grid = _float_band(raster, tool)
    for table in (_LENGTH, _SPEED):
        if a in table and b in table:
            return _float_raster(values * table[a] / table[b], grid)
    if a in _TEMPERATURE and b in _TEMPERATURE:
        return _float_raster(_from_kelvin(_to_kelvin(values, a, tool), b, tool), grid)
    raise ExecuteError(f"{tool}: '{from_unit}' cannot be converted to '{to_unit}'; both must be distances, speeds or "
                       "temperatures from the page's list.")


def HeatIndex(temperature_raster, relative_humidity_raster, temperature_unit="Fahrenheit", heat_index_unit="Fahrenheit"):
    """The page's formula, in degrees Fahrenheit and percent relative humidity (Rothfusz's regression of Steadman's
    apparent temperature, as the NWS publishes it):

        HI = −42.379 + 2.04901523 T + 10.14333127 R − 0.22475541 TR − 6.83783e−3 T² − 5.481717e−2 R²
             + 1.22874e−3 T²R + 8.5282e−4 TR² − 1.99e−6 T²R²

    The temperature is converted to Fahrenheit first and the answer to ``heat_index_unit``.

    INFERRED: the formula is applied at every cell as the page gives it, without the NWS's adjustments for low humidity
    or for an index below 80 °F, which the page does not give; the answer is 32-bit floating point; an empty unit
    takes the documented default, Fahrenheit."""
    tool = "HeatIndex"
    t, r, grid = _pair(temperature_raster, relative_humidity_raster, tool)
    t = _from_kelvin(_to_kelvin(t, _word(temperature_unit, "Fahrenheit"), tool), "fahrenheit", tool)
    hi = (-42.379 + 2.04901523 * t + 10.14333127 * r - 0.22475541 * t * r - 6.83783e-3 * t * t - 5.481717e-2 * r * r
          + 1.22874e-3 * t * t * r + 8.5282e-4 * t * r * r - 1.99e-6 * t * t * r * r)
    return _float_raster(_from_kelvin(_to_kelvin(hi, "fahrenheit", tool), _word(heat_index_unit, "Fahrenheit"), tool),
                         grid)


def WindChill(temperature_raster, wind_speed_raster, temperature_units="Fahrenheit", wind_speed_units="mph",
              wind_chill_units="Fahrenheit"):
    """The page's formula, with the temperature in degrees Fahrenheit and the wind speed in miles per hour (the NWS
    wind chill of 2001):

        WC = 35.74 + 0.6215 T − 35.75 WS^0.16 + 0.4275 T WS^0.16

    INFERRED: applied at every cell as given — the NWS's validity limits (T ≤ 50 °F, WS > 3 mph) are not on the page;
    a negative wind speed is NoData (it has no 0.16th power); "knots" is taken for kn, as the page's second example
    writes it; the answer is 32-bit floating point."""
    tool = "WindChill"
    t, ws, grid = _pair(temperature_raster, wind_speed_raster, tool)
    t = _from_kelvin(_to_kelvin(t, _word(temperature_units, "Fahrenheit"), tool), "fahrenheit", tool)
    mph = ws * _speed_factor(_word(wind_speed_units, "mph"), tool) / _SPEED["milesperhour"]
    with np.errstate(invalid="ignore"):
        power = np.where(mph >= 0, np.power(np.abs(mph), 0.16), np.nan)
    wc = 35.74 + 0.6215 * t - 35.75 * power + 0.4275 * t * power
    return _float_raster(_from_kelvin(_to_kelvin(wc, "fahrenheit", tool), _word(wind_chill_units, "Fahrenheit"), tool),
                         grid)


# ---- band arithmetic


def SpectralConversion(raster, conversion_matrix):
    """Output band R = Σ_C weight(R, C) × input band C, the page's equation. The matrix is a flat list read by rows —
    one row of weights, one per input band, for each output band — as the page's example spells out (output band 1 =
    0.1 × band 1 + 0.9 × band 2 + 0 × band 3), or a list of rows.

    INFERRED: the number of output bands is the number of weights over the number of input bands; the page says each
    row "should" sum to 1 and grapy does not enforce it; the answer is 32-bit floating point, NoData where any input band
    is."""
    tool = "SpectralConversion"
    bands = _open_bands(raster, tool)
    n = bands.bandCount
    weights = np.asarray(conversion_matrix, dtype=np.float64).ravel()
    if weights.size == 0 or weights.size % n:
        raise ExecuteError(f"{tool}: the matrix holds {weights.size} weights, which is not a whole number of rows of "
                           f"{n}, one weight per input band.")
    matrix = weights.reshape(-1, n)
    stack = np.stack([np.where(m, np.nan, a.astype(np.float64)) for a, m in zip(bands._arrays, bands._masks)])
    out = np.tensordot(matrix, stack, axes=1)
    rasters = [_float_raster(o, bands._grid) for o in out]
    if len(rasters) == 1:
        return rasters[0]
    return Raster._of_bands([r._array for r in rasters], [r._mask for r in rasters], bands._grid)


def VectorField(raster_u_mag, raster_v_dir, input_data_type="Vector-UV", angle_reference_system="Geographic",
                output_data_type="Vector-UV"):
    """Two rasters composited into one of two bands, U and V or magnitude and direction, converted between the two
    forms by the raster function's formulas: in arithmetic degrees u = m cos d, v = m sin d, m = √(u² + v²),
    d = atan2(v, u); in geographic degrees (0° north, 90° east) u = m sin d, v = m cos d, d = atan2(u, v).

    Refused: ``Unknown`` input, which says neither form.

    INFERRED: a direction is the way the vector points, as the page's "0° points due north" reads; an output direction
    is in the input's angle reference system and from 0 to 360; the bands are 32-bit floating point, NoData where
    either input is."""
    tool = "VectorField"
    kind_in = _key(_word(input_data_type, "Vector-UV")).replace("-", "")
    kind_out = _key(_word(output_data_type, "Vector-UV")).replace("-", "")
    forms = {"vectoruv": "uv", "uv": "uv", "vectormagdir": "md", "magnitudedirection": "md", "magdir": "md"}
    if kind_in == "unknown":
        _refuse(tool, "input_data_type", input_data_type, "Unknown says neither U-V nor magnitude-direction")
    if kind_in not in forms or kind_out not in forms:
        raise ExecuteError(f"{tool}: the data types are Vector-UV and Vector-MagDir.")
    reference = _key(_word(angle_reference_system, "Geographic"))
    if reference not in ("geographic", "arithmetic"):
        raise ExecuteError(f"{tool}: angle_reference_system is Geographic or Arithmetic.")
    a, b, grid = _pair(raster_u_mag, raster_v_dir, tool)
    if forms[kind_in] == "md":
        rad = np.radians(b)
        u, v = (a * np.sin(rad), a * np.cos(rad)) if reference == "geographic" else (a * np.cos(rad), a * np.sin(rad))
    else:
        u, v = a, b
    if forms[kind_out] == "uv":
        first, second = u, v
    else:
        first = np.hypot(u, v)
        angle = np.degrees(np.arctan2(u, v) if reference == "geographic" else np.arctan2(v, u))
        second = np.mod(angle, 360.0)
    one, two = _float_raster(first, grid), _float_raster(second, grid)
    return Raster._of_bands([one._array, two._array], [one._mask | two._mask, one._mask | two._mask], grid)


def ColorspaceConversion(raster, conversion_type="rgb_to_hsv"):
    """An unsigned 8-bit three-band image converted between RGB and HSV, as the Color Model Conversion page describes
    them: red, green and blue from 0 to 255; hue from 0 to 240, value and saturation from 0 to 255. RGB to HSV is
    Smith's hexcone: V = max, S = (max − min)/max, H from the sector of the largest component.

    Measured against ArcGIS Pro 3.7 on 2026-10-31 (ADR-245 §5.8 a), replacing the INFERRED 240/359: hue degrees are
    rescaled by 240/360 and rounded half up, and a chromatic red is 240, not 0 (a grey's hue is 0) — Pro writes magenta
    200 where the page lists 201; the inverse reads 240 as 360 degrees. A NoData cell in any band is NoData in every
    band."""
    tool = "ColorspaceConversion"
    bands = _open_bands(raster, tool)
    if bands.bandCount != 3:
        raise ExecuteError(f"{tool}: the input must have three bands; it has {bands.bandCount}.")
    if any(a.dtype != np.uint8 for a in bands._arrays):
        raise ExecuteError(f"{tool}: the input must be an unsigned 8-bit raster, as the page requires.")
    kind = _key(_word(conversion_type, "rgb_to_hsv"))
    x = np.stack([a.astype(np.float64) for a in bands._arrays])
    blank = np.logical_or.reduce(bands._masks)
    if kind == "rgbtohsv":
        r, g, b = x / 255.0
        high, low = x.max(axis=0) / 255.0, x.min(axis=0) / 255.0
        span = high - low
        with np.errstate(divide="ignore", invalid="ignore"):
            s = np.where(high > 0, span / high, 0.0)
            h = np.where(span == 0, 0.0,
                         np.where(high == r, np.mod((g - b) / span, 6.0),
                                  np.where(high == g, (b - r) / span + 2.0, (r - g) / span + 4.0))) * 60.0
        # Pro 3.7, measured 2026-10-31 on every whole-degree hue: H = round(degrees · 240/360), and a chromatic red
        # is 240 rather than 0; a grey's hue is 0.
        hue = np.floor(h * 240.0 / 360.0 + 0.5)
        hue = np.where((hue == 0) & (span > 0), 240.0, hue)
        out = [hue, np.floor(s * 255.0 + 0.5), np.floor(high * 255.0 + 0.5)]
    elif kind == "hsvtorgb":
        h = np.mod(x[0] * 360.0 / 240.0, 360.0) / 60.0
        s, v = x[1] / 255.0, x[2] / 255.0
        c = v * s
        xx = c * (1 - np.abs(np.mod(h, 2.0) - 1))
        m = v - c
        sector = np.floor(h).astype(int) % 6
        table = [(c, xx, 0 * c), (xx, c, 0 * c), (0 * c, c, xx), (0 * c, xx, c), (xx, 0 * c, c), (c, 0 * c, xx)]
        rgb = [np.choose(sector, [t[i] for t in table]) for i in range(3)]
        out = [np.floor((ch + m) * 255.0 + 0.5) for ch in rgb]
    else:
        raise ExecuteError(f"{tool}: conversion_type is rgb_to_hsv or hsv_to_rgb.")
    arrays = [np.clip(o, 0, 255).astype(np.uint8) for o in out]
    return Raster._of_bands(arrays, [blank.copy() for _ in arrays], bands._grid)


def ColormapToRGB(raster):
    """A single-band raster's colour map turned into three bands, red, green and blue, each pixel taking its value's
    colour, as the raster function says. The colour map is the one the raster's file carries (GDAL's colour table of
    its first band, as a GeoTIFF palette holds it).

    INFERRED: a value the map does not hold is NoData — the raster function's default leaves unmapped pixels without a
    colour, and the arcpy function has no parameter to change it; NoData stays NoData; the bands are unsigned 8-bit."""
    tool = "ColormapToRGB"
    r = as_raster(raster, tool)
    path = r.catalogPath
    colours = None
    if path and os.path.isfile(path):
        import rasterio
        with rasterio.open(path) as source:
            try:
                colours = source.colormap(1)
            except ValueError:
                colours = None
    if not colours:
        raise ExecuteError(f"{tool}: {r!r} carries no colour map.")
    values = r._array
    blank = r._mask.copy()
    out = [np.zeros(values.shape, np.uint8) for _ in range(3)]
    known = np.zeros(values.shape, bool)
    for value, rgba in colours.items():
        here = values == value
        known |= here
        for i in range(3):
            out[i][here] = rgba[i]
    blank |= ~known
    return Raster._of_bands(out, [blank.copy() for _ in out], r._grid)


# ---- InList and ZonalRemap


def InList(in_raster_or_constant, in_raster_or_constants, process_as_multiband=None):
    """The first input's value wherever it equals the value of any of the other inputs at that cell, else NoData, as
    the tool page states. Integer when every input is integer, else floating point; the list's order does not matter.

    Refused: multiband inputs — the page's two statements on how many bands the answer has disagree (one says each band
    of a list raster is processed as its own single-band raster, the next that the answer has as many bands as the
    widest list raster).

    INFERRED: a list raster that is NoData at a cell matches nothing there; a constant first input needs a raster in the
    list."""
    tool = "InList"
    others = list(in_raster_or_constants) if isinstance(in_raster_or_constants, (list, tuple)) else [in_raster_or_constants]
    values = [in_raster_or_constant, *others]
    opened = [as_raster(v, tool, bands=True) if isinstance(v, (Raster, str, os.PathLike)) else v for v in values]
    rasters = [v for v in opened if isinstance(v, Raster)]
    if not rasters:
        raise ExecuteError(f"{tool}: at least one input must be a raster.")
    if any(r.bandCount > 1 for r in rasters):
        _refuse(tool, "a multiband input", "", "the page's statements on the answer's band count disagree")
    arrays, masks, grid = _align(rasters, tool)
    it = iter(zip(arrays, masks))
    planes = []
    for v in opened:
        if isinstance(v, Raster):
            a, m = next(it)
            planes.append((a, m))
        else:
            planes.append((np.full((grid.rows, grid.columns), v), np.zeros((grid.rows, grid.columns), bool)))
    first, first_mask = planes[0]
    found = np.zeros(first.shape, bool)
    for a, m in planes[1:]:
        found |= (~m) & (a == first)
    integer = all(np.issubdtype(np.asarray(a).dtype, np.integer) for a, _ in planes)
    out = first.astype(np.int32 if integer and first.dtype.itemsize <= 4 else (first.dtype if integer else np.float64))
    return Raster._of(out.copy(), first_mask | ~found, grid)


def ZonalRemap(raster, zonal_attribute_table, zone_raster=None, zone_field="ZoneID", min_value_field_name="ZoneMin",
               max_value_field_name="ZoneMax", output_value_field_name="ZoneValue", background_value=0,
               default_output_value=255, where_clause=None):
    """Pixels remapped by a table of thresholds for each zone, as the page defines it: a record applies to the pixels
    of its zone; a pixel above the record's minimum and below its maximum takes the record's output value, or
    ``default_output_value`` where the output field is empty or not named; a record whose minimum or maximum is empty,
    or whose field is not named, does not test that side; NoData in either raster is NoData.

    Measured against ArcGIS Pro 3.7 on 2026-10-31 (ADR-245 §5.8 b), replacing what was INFERRED: both ends are
    exclusive (min < v < max); where records overlap, the later one wins; a pixel no record matches keeps
    ``background_value``, even in a zone that has records; without a zone raster only the records with no zone id
    apply, and with one only those with an id; the answer is 32-bit floating point."""
    from . import da
    tool = "ZonalRemap"
    values = as_raster(raster, tool)
    if _given(zone_raster):
        zones = as_raster(zone_raster, tool)
        (v, z), (vm, zm), grid = _align([values, zones], tool)
    else:
        v, vm, grid = values._array, values._mask, values._grid
        z, zm = None, np.zeros(v.shape, bool)
    zone_name = zone_field if _given(zone_field) else None
    if z is None and zone_name is not None:
        from . import ListFields
        if zone_name.lower() not in {f.name.lower() for f in ListFields(zonal_attribute_table)}:
            zone_name = None  # a table without zone ids: every record is a record for no zone
    fields = [zone_name, min_value_field_name, max_value_field_name, output_value_field_name]
    names = [f for f in fields if _given(f)]
    records = []
    with da.SearchCursor(zonal_attribute_table, names, where_clause if _given(where_clause) else None) as rows:
        for row in rows:
            got = dict(zip(names, row))
            records.append(tuple(got.get(f) if _given(f) else None for f in fields))
    v = v.astype(np.float64)
    default = float(default_output_value if _given(default_output_value) else 255)
    out = np.full(v.shape, float(background_value if _given(background_value) else 0))
    for zone, low, high, target in records:
        # Without a zone raster only the records with no zone id apply; with one, only those with an id
        # (Pro 3.7, measured 2026-10-31).
        if (zone is None) != (z is None):
            continue
        here = np.ones(v.shape, bool) if z is None else (z == zone)
        ok = here & ~vm & ~zm
        if low is not None:
            ok &= v > float(low)
        if high is not None:
            ok &= v < float(high)
        out[ok] = default if target is None else float(target)  # a later record overrides an earlier one
    return Raster._of(out.astype(np.float32), zm | vm, grid)


# ---- Speckle, ElevationVoidFill and Resample


_SPECKLE_TYPES = {"lee": "LEE", "enhancedlee": "ENHANCED_LEE", "frost": "FROST", "kuan": "KUAN", "gammamap": "GAMMA_MAP",
                  "refinedlee": "REFINED_LEE"}
_NOISE = {"multiplicative": "MULTIPLICATIVE_NOISE", "multiplicativenoise": "MULTIPLICATIVE_NOISE",
          "additive": "ADDITIVE_NOISE", "additivenoise": "ADDITIVE_NOISE",
          "additiveandmultiplicative": "ADDITIVE_AND_MULTIPLICATIVE_NOISE",
          "additiveandmultiplicativenoise": "ADDITIVE_AND_MULTIPLICATIVE_NOISE"}


def Speckle(raster, filter_type="Lee", filter_size="3x3", noise_model="Multiplicative", noise_var=None,
            additive_noise_mean=None, multiplicative_noise_mean=1, nlooks=1, damp_factor=None):
    """EQUIVALENT: the six filters the page names, each by its paper, as arcpy.ia.Despeckle builds them (see SPECKLE),
    on the raster's values. Refined Lee uses its own 7×7 edge-aligned windows.

    INFERRED: where the arcpy page's default is None, the raster function's is used — noise variance 0.25, additive
    noise mean 0, damping factor 1; a raster of several bands is filtered band by band; NoData cells stay NoData and
    are filled with their window's mean while their neighbours are filtered, as Despeckle does; the answer is 32-bit
    floating point."""
    from . import _sartools as sar
    tool = "Speckle"
    kind = _SPECKLE_TYPES.get(_key(_word(filter_type, "Lee")))
    if kind is None:
        raise ExecuteError(f"{tool}: filter_type '{filter_type}' is not one of Lee, EnhancedLee, Frost, Kuan, Gamma MAP "
                           "and Refined Lee.")
    size = {"3x3": 3, "5x5": 5, "7x7": 7, "9x9": 9, "11x11": 11}.get(_key(_word(filter_size, "3x3")))
    if size is None:
        raise ExecuteError(f"{tool}: filter_size '{filter_size}' is not one of 3x3, 5x5, 7x7, 9x9 and 11x11.")
    model = _NOISE.get(_key(_word(noise_model, "Multiplicative")))
    if model is None:
        raise ExecuteError(f"{tool}: noise_model '{noise_model}' is not Multiplicative, Additive or "
                           "AdditiveAndMultiplicative.")
    looks = float(nlooks if _given(nlooks) else 1)
    if looks <= 0:
        raise ExecuteError(f"{tool}: nlooks must be positive.")
    damp = float(damp_factor if _given(damp_factor) else 1)
    variance = float(noise_var if _given(noise_var) else 0.25)
    add_mean = float(additive_noise_mean if _given(additive_noise_mean) else 0)
    mult_mean = float(multiplicative_noise_mean if _given(multiplicative_noise_mean) else 1)
    bands = _open_bands(raster, tool)
    arrays, masks = [], []
    for a, m in zip(bands._arrays, bands._masks):
        z = np.where(m, np.nan, a.astype(np.float64))
        if m.any():
            from scipy import ndimage
            filled = np.where(m, 0.0, z)
            weight = ndimage.uniform_filter((~m).astype(float), 7)
            z = np.where(m, ndimage.uniform_filter(filled, 7) / np.where(weight > 0, weight, 1), z)
        if kind == "LEE":
            r = sar.lee(z, size, model, variance, add_mean, mult_mean, looks)
        elif kind == "REFINED_LEE":
            r = sar.refined_lee(z, looks)
        elif kind == "ENHANCED_LEE":
            r = sar.enhanced_lee(z, size, looks, damp)
        elif kind == "KUAN":
            r = sar.kuan(z, size, looks)
        elif kind == "GAMMA_MAP":
            r = sar.gamma_map(z, size, looks)
        else:
            r = sar.frost(z, size, damp)
        bad = m | ~np.isfinite(r)
        arrays.append(np.where(bad, 0, r).astype(np.float32))
        masks.append(bad)
    if len(arrays) == 1:
        return Raster._of(arrays[0], masks[0], bands._grid)
    return Raster._of_bands(arrays, masks, bands._grid)


def ElevationVoidFill(raster, max_void_width=0):
    """EQUIVALENT: each void — a connected group of NoData cells — filled by inverse distance weighting, power 2, from
    the valid cells bordering it (see VOID_FILL). The page's own method is an eight-neighbour mean, then plane fitting,
    then IDW where the plane's error is "too large", with no threshold given. ``max_void_width`` is honoured as the page
    defines it: a void whose bounding box is wider or taller than it, in map units, is left; 0 or empty fills every
    void, and −1 none.

    INFERRED: voids are 8-connected; a void touching the raster's edge is a void like any other; the answer keeps the
    input's cell type where it is floating point and is 32-bit floating point otherwise."""
    from scipy import ndimage
    tool = "ElevationVoidFill"
    r = as_raster(raster, tool)
    width = float(max_void_width) if _given(max_void_width) else 0.0
    values = r._array.astype(np.float64)
    mask = r._mask.copy()
    g = r._grid
    if width != -1 and mask.any() and not mask.all():
        labels, count = ndimage.label(mask, structure=np.ones((3, 3), bool))
        boxes = ndimage.find_objects(labels)
        for k, box in enumerate(boxes, 1):
            rows, cols = box
            if width > 0 and ((cols.stop - cols.start) * g.width > width or (rows.stop - rows.start) * g.height > width):
                continue
            r0, r1 = max(rows.start - 1, 0), min(rows.stop + 1, g.rows)
            c0, c1 = max(cols.start - 1, 0), min(cols.stop + 1, g.columns)
            void = labels[r0:r1, c0:c1] == k
            ring = ndimage.binary_dilation(void, structure=np.ones((3, 3), bool)) & ~mask[r0:r1, c0:c1]
            if not ring.any():
                continue
            ry, rx = np.nonzero(ring)
            known = values[r0:r1, c0:c1][ring]
            vy, vx = np.nonzero(void)
            d2 = (vy[:, None] - ry[None, :]) ** 2 * g.height ** 2 + (vx[:, None] - rx[None, :]) ** 2 * g.width ** 2
            w = 1.0 / d2
            fill = (w * known[None, :]).sum(axis=1) / w.sum(axis=1)
            block = values[r0:r1, c0:c1]
            block[vy, vx] = fill
            mask[r0:r1, c0:c1][vy, vx] = False
    dtype = r._array.dtype if np.issubdtype(r._array.dtype, np.floating) else np.float32
    return Raster._of(np.where(mask, 0, values).astype(dtype), mask, g)


def _cubic(t):
    """Keys's cubic convolution kernel, a = −0.5."""
    t = np.abs(t)
    a = -0.5
    return np.where(t <= 1, (a + 2) * t ** 3 - (a + 3) * t ** 2 + 1,
                    np.where(t < 2, a * t ** 3 - 5 * a * t ** 2 + 8 * a * t - 4 * a, 0.0))


def _resample_band(array, mask, g, grid, kind):
    """One band resampled from grid ``g`` onto ``grid``."""
    xs, ys = grid.centres()
    fx = (xs - g.left) / g.width - 0.5  # input column-centre coordinates of each output centre
    fy = (g.top - ys) / g.height - 0.5
    values = np.where(mask, np.nan, array.astype(np.float64))
    if kind == "NEAREST":
        cols = np.clip(np.floor(fx + 0.5).astype(int), 0, g.columns - 1)
        rows = np.clip(np.floor(fy + 0.5).astype(int), 0, g.rows - 1)
        return values[np.ix_(rows, cols)]
    if kind in ("BILINEAR", "BILINEARPLUS", "CUBIC"):
        reach = 1 if kind != "CUBIC" else 2
        c0, r0 = np.floor(fx).astype(int), np.floor(fy).astype(int)
        tx, ty = fx - c0, fy - r0
        offsets = range(0, 2) if reach == 1 else range(-1, 3)
        total = np.zeros((len(ys), len(xs)))
        weights = np.zeros_like(total)
        outside = np.zeros_like(total, bool)
        for dr in offsets:
            rr = r0 + dr
            wy = (1 - np.abs(ty - dr)) if reach == 1 else _cubic(ty - dr)
            for dc in offsets:
                cc = c0 + dc
                wx = (1 - np.abs(tx - dc)) if reach == 1 else _cubic(tx - dc)
                off = (rr[:, None] < 0) | (rr[:, None] >= g.rows) | (cc[None, :] < 0) | (cc[None, :] >= g.columns)
                outside |= off
                sample = values[np.ix_(np.clip(rr, 0, g.rows - 1), np.clip(cc, 0, g.columns - 1))]
                w = wy[:, None] * wx[None, :]
                total += w * sample
                weights += w
        out = total / weights
        if kind == "BILINEARPLUS":
            out = np.where(outside, np.nan, out)
        return out
    if kind == "MAJORITY":
        c0, r0 = np.floor(fx).astype(int) - 1, np.floor(fy).astype(int) - 1
        out = np.full((len(ys), len(xs)), np.nan)
        for i in range(len(ys)):
            rows = np.clip(np.arange(r0[i], r0[i] + 4), 0, g.rows - 1)
            for j in range(len(xs)):
                cols = np.clip(np.arange(c0[j], c0[j] + 4), 0, g.columns - 1)
                block = values[np.ix_(rows, cols)].ravel()
                block = block[np.isfinite(block)]
                if block.size:
                    found, counts = np.unique(block, return_counts=True)
                    out[i, j] = found[np.argmax(counts)]
        return out
    # AVERAGE, MINIMUM, MAXIMUM: the input cells whose centres the output cell covers
    in_xs, in_ys = g.centres()
    out = np.full((len(ys), len(xs)), np.nan)
    function = {"AVERAGE": np.nanmean, "MINIMUM": np.nanmin, "MAXIMUM": np.nanmax}[kind]
    for i, y in enumerate(ys):
        rows = np.nonzero((in_ys <= y + grid.height / 2) & (in_ys > y - grid.height / 2))[0]
        for j, x in enumerate(xs):
            cols = np.nonzero((in_xs >= x - grid.width / 2) & (in_xs < x + grid.width / 2))[0]
            if rows.size and cols.size:
                block = values[np.ix_(rows, cols)]
                if np.isfinite(block).any():
                    out[i, j] = function(block)
    return out


_RESAMPLING = {"nearestneighbor": "NEAREST", "nearest": "NEAREST", "bilinear": "BILINEAR",
               "bilinearinterpolation": "BILINEAR", "cubic": "CUBIC", "cubicconvolution": "CUBIC", "majority": "MAJORITY",
               "bilinearinterpolationplus": "BILINEARPLUS", "average": "AVERAGE", "minimum": "MINIMUM",
               "maximum": "MAXIMUM", "vectoraverage": "VECTOR"}


def resample(raster, kind, cell, tool):
    """``raster`` on cells of ``cell`` (width, height) over its own extent: the grid is anchored at the top-left corner
    and has as many cells as cover the extent (INFERRED)."""
    g = raster._grid
    cw, ch = cell
    columns = max(1, int(math.ceil((g.right - g.left) / cw - 1e-9)))
    rows = max(1, int(math.ceil((g.top - g.bottom) / ch - 1e-9)))
    grid = _Grid(g.left, g.top, cw, ch, rows, columns, g.srid)
    if kind == "VECTOR":
        if raster.bandCount != 2:
            raise ExecuteError(f"{tool}: VectorAverage takes a raster of two bands, magnitude and direction.")
        m = np.where(raster._masks[0], np.nan, raster._arrays[0].astype(np.float64))
        d = np.radians(np.where(raster._masks[1], np.nan, raster._arrays[1].astype(np.float64)))
        u = _resample_band(m * np.sin(d), np.isnan(m * d), g, grid, "AVERAGE")
        v = _resample_band(m * np.cos(d), np.isnan(m * d), g, grid, "AVERAGE")
        planes = [np.hypot(u, v), np.mod(np.degrees(np.arctan2(u, v)), 360.0)]
    else:
        planes = [_resample_band(a, m, g, grid, kind) for a, m in zip(raster._arrays, raster._masks)]
    arrays, masks = [], []
    for source, plane in zip(raster._arrays, planes):
        bad = ~np.isfinite(plane)
        keep = kind in ("NEAREST", "MAJORITY", "MINIMUM", "MAXIMUM") and np.issubdtype(source.dtype, np.integer)
        dtype = source.dtype if keep else (source.dtype if np.issubdtype(source.dtype, np.floating) else np.float32)
        arrays.append(np.where(bad, 0, plane).astype(dtype))
        masks.append(bad)
    if len(arrays) == 1:
        return Raster._of(arrays[0], masks[0], grid)
    return Raster._of_bands(arrays, masks, grid)


def Resample(raster, resampling_type=None, input_cellsize=None, output_cellsize=None):
    """EQUIVALENT: the raster on cells of ``output_cellsize`` over the same extent, by the method named (see RESAMPLE):
    NearestNeighbor, Bilinear, Cubic, Majority, BilinearInterpolationPlus (bilinear, NoData where the four centres
    reach past the edge), Average, Minimum, Maximum, and VectorAverage (a magnitude-direction pair averaged as U and V).

    Refused: BilinearGaussBlur and BilinearGaussBlurPlus, whose Gaussian's width the page does not give; an
    ``input_cellsize`` other than the raster's own, which reads another pyramid level, and grapy's rasters hold none.

    INFERRED: an empty ``resampling_type`` is NearestNeighbor, the raster function's default; an empty
    ``output_cellsize`` keeps the cell size; the output grid starts at the input's top-left corner and covers its extent
    with whole cells; a NoData cell among those an output cell reads makes it NoData for the interpolations, and is
    skipped by Majority, Average, Minimum and Maximum; nearest, majority, minimum and maximum keep an integer cell
    type."""
    tool = "Resample"
    kind = _RESAMPLING.get(_key(_word(resampling_type, "NearestNeighbor")))
    if kind is None:
        word = _key(resampling_type)
        if word.startswith("bilineargauss") or word.startswith("gaussblur"):
            _refuse(tool, "resampling_type", resampling_type, "the Gaussian's width and kernel size are not given")
        raise ExecuteError(f"{tool}: resampling_type '{resampling_type}' is not one of the page's methods.")
    r = _open_bands(raster, tool)
    g = r._grid
    if _given(input_cellsize) and not math.isclose(float(input_cellsize), g.width, rel_tol=1e-9):
        _refuse(tool, "input_cellsize", input_cellsize, "reading the input at another cell size reads a pyramid level, "
                "which grapy's rasters do not hold")
    size = float(output_cellsize) if _given(output_cellsize) else None
    return resample(r, kind, (size or g.width, size or g.height), tool)


# ---- LinearUnmixing


def _class_spectra(features, bands, tool):
    """Each class's mean spectrum over the pixels whose centres fall inside its polygons, in the order the classes
    first appear, with their names."""
    import shapely
    from . import da
    from .analysis import _schema
    columns, kind, _, _ = _schema(features)
    if kind != "Polygon":
        raise ExecuteError(f"{tool}: a training feature class must hold polygons.")
    lower = {c.name.lower(): c.name for c in columns}
    if "classname" not in lower or "classvalue" not in lower:
        raise ExecuteError(f"{tool}: polygon features need the fields classname and classvalue, as the page says.")
    order, shapes = [], {}
    with da.SearchCursor(features, [lower["classname"], lower["classvalue"], "SHAPE@"]) as rows:
        for name, value, shape in rows:
            if shape is None:
                continue
            if value not in shapes:
                order.append((value, name))
                shapes[value] = []
            shapes[value].append(shape._shape)
    g = bands._grid
    xs, ys = g.centres()
    gx, gy = np.meshgrid(xs, ys)
    stack = np.stack([np.where(m, np.nan, a.astype(np.float64)) for a, m in zip(bands._arrays, bands._masks)])
    spectra = []
    for value, name in order:
        inside = shapely.contains_xy(shapely.union_all(shapes[value]), gx, gy)
        pixels = stack[:, inside]
        pixels = pixels[:, np.isfinite(pixels).all(axis=0)]
        if pixels.shape[1] == 0:
            raise ExecuteError(f"{tool}: class {name!r} covers no pixel centre of the raster.")
        spectra.append(pixels.mean(axis=1))
    return np.array(spectra).T, order


def LinearUnmixing(in_raster, in_spectral_profile_file, value_option=None):
    """EQUIVALENT: each pixel's spectrum written as a combination of the class spectra, solved by least squares (see
    UNMIXING): unconstrained; ``SUM_TO_ONE`` by a Lagrange multiplier; ``NON_NEGATIVE`` by Lawson and Hanson's NNLS;
    both by Heinz and Chang's fully constrained least squares. One band per class, in the order of the spectral
    profile, as the raster function says.

    The class spectra come from a polygon feature class with ``classname`` and ``classvalue``, as the page requires.
    Refused: a classifier definition (.ecd), whose layout is not published; a .json profile, whose layout the page does
    not give; spectral libraries (.esl, .sli).

    INFERRED: a class's spectrum is the mean of the pixels whose centres fall inside its polygons; classes are ordered
    by first appearance; an empty ``value_option`` applies no constraint; a pixel with NoData in any band is NoData; the
    bands are 32-bit floating point."""
    from scipy.optimize import nnls
    tool = "LinearUnmixing"
    bands = _open_bands(in_raster, tool)
    source = str(in_spectral_profile_file)
    extension = os.path.splitext(source)[1].lower()
    if extension in (".ecd", ".json", ".esl", ".sli"):
        _refuse(tool, "in_spectral_profile_file", source,
                {".ecd": "a classifier definition's layout is not published",
                 ".json": "the page does not give the layout of a JSON spectral profile"}.get(extension,
                                                                                            "spectral library layouts are not given"))
    options = {o.strip().upper() for o in str(value_option or "").split(";") if o.strip()}
    if options - {"SUM_TO_ONE", "NON_NEGATIVE"}:
        raise ExecuteError(f"{tool}: value_option takes SUM_TO_ONE and NON_NEGATIVE.")
    E, order = _class_spectra(in_spectral_profile_file, bands, tool)
    n_bands, n_classes = E.shape
    if n_classes > n_bands:
        raise ExecuteError(f"{tool}: {n_classes} classes cannot be unmixed from {n_bands} bands, as the page says.")
    stack = np.stack([np.where(m, np.nan, a.astype(np.float64)) for a, m in zip(bands._arrays, bands._masks)])
    pixels = stack.reshape(n_bands, -1)
    good = np.isfinite(pixels).all(axis=0)
    out = np.full((n_classes, pixels.shape[1]), np.nan)
    y = pixels[:, good]
    if options == set():
        out[:, good] = np.linalg.lstsq(E, y, rcond=None)[0]
    elif options == {"SUM_TO_ONE"}:
        gram = np.linalg.inv(E.T @ E)
        f = gram @ E.T @ y
        ones = np.ones((n_classes, 1))
        correction = gram @ ones / (ones.T @ gram @ ones)
        out[:, good] = f - correction @ (ones.T @ f - 1)
    else:
        if "SUM_TO_ONE" in options:
            delta = 1e3 * max(1.0, float(np.abs(E).max()))
            A = np.vstack([E, delta * np.ones((1, n_classes))])
            Y = np.vstack([y, delta * np.ones((1, y.shape[1]))])
        else:
            A, Y = E, y
        out[:, good] = np.array([nnls(A, Y[:, k])[0] for k in range(Y.shape[1])]).T if Y.shape[1] else out[:, good]
    rows, columns = bands._grid.rows, bands._grid.columns
    planes = [_float_raster(o.reshape(rows, columns), bands._grid) for o in out]
    if len(planes) == 1:
        return planes[0]
    return Raster._of_bands([p._array for p in planes], [p._mask for p in planes], bands._grid,
                            [str(name) for _, name in order])


# ---- ApplyEnvironment


def _environment_extent(value, tool):
    from ._reference import Extent
    if isinstance(value, Extent):
        return value.XMin, value.YMin, value.XMax, value.YMax
    if isinstance(value, Raster):
        g = value._grid
        return g.left, g.bottom, g.right, g.top
    parts = str(value).split()
    try:
        return tuple(float(p) for p in parts[:4])
    except ValueError:
        g = as_raster(value, tool)._grid
        return g.left, g.bottom, g.right, g.top


def _environment_cell(value, tool):
    if isinstance(value, (int, float)):
        return float(value)
    text = str(value).strip()
    try:
        return float(text)
    except ValueError:
        return as_raster(value, tool)._grid.width


def ApplyEnvironment(in_raster):
    """A copy of the raster with the environment applied, band by band, in this order: the extent (the input's cells
    whose centres fall in it — the grid is not moved), the cell size (nearest neighbour, the Resampling Method
    environment's default), and the mask (cells outside it NoData). With none set it is a copy. A number with the
    extent and cell size set answers a raster of that constant, as the page's example shows.

    Refused: an output coordinate system other than the raster's (projecting a raster needs a resampling rule this page
    does not give); a snap raster whose cells do not line up with the input's; MAXOF and MINOF for the cell size,
    which name rasters this one function does not have.

    INFERRED: nearest neighbour gives an output centre that falls on the edge between two input cells the cell to its
    right or below (the page does not say); a constant raster's grid starts at the extent's top-left corner, and its cell type is 32-bit integer for
    an integer and 32-bit float otherwise."""
    from ._reference import SpatialReference
    tool = "ApplyEnvironment"
    words = ("MAXOF", "MINOF", "INTERSECTION_OF_INPUTS", "UNION_OF_INPUTS", "DEFAULT")
    extent = env.extent if _given(env.extent) and not (isinstance(env.extent, str) and env.extent.upper() in words)         else None
    cell = env.cellSize if _given(env.cellSize) and not (isinstance(env.cellSize, str)
                                                          and env.cellSize.upper() in ("MAXOF", "MINOF")) else None
    if isinstance(env.cellSize, str) and env.cellSize.upper() in ("MAXOF", "MINOF"):
        _refuse(tool, "cellSize environment", env.cellSize, "it names other inputs, and this function has one")
    if isinstance(in_raster, (int, float)) and not isinstance(in_raster, bool):
        if extent is None or cell is None:
            raise ExecuteError(f"{tool}: a constant needs the cell size and extent environments set.")
        xmin, ymin, xmax, ymax = _environment_extent(extent, tool)
        size = _environment_cell(cell, tool)
        reference = env.outputCoordinateSystem or getattr(extent, "spatialReference", None)
        srid = getattr(reference, "factoryCode", 0) or 0
        columns, rows = int(math.ceil((xmax - xmin) / size - 1e-9)), int(math.ceil((ymax - ymin) / size - 1e-9))
        dtype = np.int32 if isinstance(in_raster, int) else np.float32
        raster = Raster._of(np.full((rows, columns), in_raster, dtype), np.zeros((rows, columns), bool),
                            _Grid(xmin, ymax, size, size, rows, columns, srid))
    else:
        raster = _open_bands(in_raster, tool)
    g = raster._grid
    out_reference = env.outputCoordinateSystem
    if _given(out_reference):
        code = getattr(out_reference, "factoryCode", None)
        if code is None:
            code = SpatialReference(out_reference).factoryCode
        if code and g.srid and int(code) != g.srid:
            _refuse(tool, "outputCoordinateSystem environment", code, "projecting a raster needs a resampling rule the "
                    "page does not give")
    if _given(env.snapRaster):
        snap = as_raster(env.snapRaster, tool)._grid
        for offset, size in ((snap.left - g.left, g.width), (snap.top - g.top, g.height)):
            if not (math.isclose(snap.width, g.width) and math.isclose(snap.height, g.height)
                    and math.isclose(offset / size, round(offset / size), abs_tol=1e-9)):
                _refuse(tool, "snapRaster environment", env.snapRaster, "its cells do not line up with the input's, and "
                        "moving them needs resampling")
    arrays, masks = list(raster._arrays), list(raster._masks)
    if extent is not None:
        xmin, ymin, xmax, ymax = _environment_extent(extent, tool)
        xs, ys = g.centres()
        cols = np.nonzero((xs >= xmin) & (xs <= xmax))[0]
        rows = np.nonzero((ys >= ymin) & (ys <= ymax))[0]
        if not cols.size or not rows.size:
            raise ExecuteError(f"{tool}: the extent environment does not overlap the raster.")
        arrays = [a[rows[0]:rows[-1] + 1, cols[0]:cols[-1] + 1] for a in arrays]
        masks = [m[rows[0]:rows[-1] + 1, cols[0]:cols[-1] + 1] for m in masks]
        g = _Grid(g.left + cols[0] * g.width, g.top - rows[0] * g.height, g.width, g.height, rows.size, cols.size, g.srid)
    raster = Raster._of_bands(arrays, masks, g)
    if cell is not None:
        size = _environment_cell(cell, tool)
        if not (math.isclose(size, g.width) and math.isclose(size, g.height)):
            raster = resample(raster, "NEAREST", (size, size), tool)
            if raster.bandCount == 1:
                raster = Raster._of_bands([raster._array], [raster._mask], raster._grid)
    if _given(env.mask):
        from . import sa
        masked = [sa.ExtractByMask(raster._band(b), env.mask) for b in range(1, raster.bandCount + 1)]
        raster = Raster._of_bands([m._array for m in masked], [m._mask for m in masked], masked[0]._grid)
    if raster.bandCount == 1:
        return Raster._of(raster._arrays[0].copy(), raster._masks[0].copy(), raster._grid)
    return Raster._of_bands([a.copy() for a in raster._arrays], [m.copy() for m in raster._masks], raster._grid)


# ---- PixelBlock and PixelBlockCollection


class PixelBlock:
    """arcpy.ia.PixelBlock: a block of pixels — a NumPy array of (rows, columns, bands) — and the extent it covers."""

    def __init__(self, data, extent):
        self._data = np.asarray(data)
        self._extent = extent

    @property
    def cell_size(self):
        """(x size, y size): the extent's width and height over the block's columns and rows."""
        rows, columns = self._data.shape[-3], self._data.shape[-2]
        e = self._extent
        return ((e.XMax - e.XMin) / columns, (e.YMax - e.YMin) / rows)

    @property
    def tlc(self):
        """The block's upper left corner, a Point."""
        from ._geometry import Point
        return Point(self._extent.XMin, self._extent.YMax)

    @property
    def spatial_reference(self):
        return getattr(self._extent, "spatialReference", None)

    def getData(self):
        return self._data


class PixelBlockCollection:
    """arcpy.ia.PixelBlockCollection: every pixel block of a raster, or of several rasters together, read only, in
    (rows, columns) blocks every ``stride`` pixels. One raster answers ``collection[i][j]`` (or ``[i, j]``), a PixelBlock;
    several answer a list of them, one per raster, as documented; ``next()`` walks the blocks row by row, ``reset()``
    starts again and ``shuffle()`` puts them in random order.

    Refused: multidimensional rasters (their slices are not held in a pixel block here).

    INFERRED: blocks start at the top-left pixel and every ``stride`` pixels after it, and one starting inside the
    raster near its edge is cut short rather than padded; ``nodata_to_values`` fills NoData (one value, or one per
    raster), and without it NoData is NaN in a floating-point raster and the raster's noDataValue otherwise; UNION fills
    pixels outside a raster as NoData; ``shuffle`` draws from Python's ``random``, seeded by ``env.randomGenerator``'s
    seed when one is set."""

    def __init__(self, in_rasters, pixel_block_size=(512, 512), stride=None, overlay_type="INTERSECTION",
                 nodata_to_values=None):
        from ._reference import Extent
        tool = "PixelBlockCollection"
        self._single = not isinstance(in_rasters, (list, tuple))
        rasters = [_open_bands(r, tool) for r in ([in_rasters] if self._single else in_rasters)]
        for r in rasters:
            if r.isMultidimensional:
                _refuse(tool, "in_rasters", r, "multidimensional rasters' slices are not held in a pixel block here")
        first = rasters[0]._grid
        for r in rasters[1:]:
            g = r._grid
            if g.srid != first.srid or not (math.isclose(g.width, first.width) and math.isclose(g.height, first.height)):
                raise ExecuteError(f"{tool}: the rasters must all have the same cell size and spatial reference.")
        overlay = _word(overlay_type, "INTERSECTION").upper()
        if overlay == "INTERSECTION":
            left, top = max(r._grid.left for r in rasters), min(r._grid.top for r in rasters)
            right, bottom = min(r._grid.right for r in rasters), max(r._grid.bottom for r in rasters)
        elif overlay == "UNION":
            left, top = min(r._grid.left for r in rasters), max(r._grid.top for r in rasters)
            right, bottom = max(r._grid.right for r in rasters), min(r._grid.bottom for r in rasters)
        else:
            raise ExecuteError(f"{tool}: overlay_type is INTERSECTION or UNION.")
        if right <= left or top <= bottom:
            raise ExecuteError(f"{tool}: the rasters do not overlap.")
        columns = int(round((right - left) / first.width))
        rows = int(round((top - bottom) / first.height))
        self._grid = _Grid(left, top, first.width, first.height, rows, columns, first.srid)
        fills = nodata_to_values if isinstance(nodata_to_values, (list, tuple)) else [nodata_to_values] * len(rasters)
        self._cubes = []
        for r, fill in zip(rasters, fills):
            planes = []
            for a, m in zip(r._arrays, r._masks):
                if fill is not None:
                    kind = np.result_type(a.dtype, np.min_scalar_type(fill))
                    blank = fill
                elif np.issubdtype(a.dtype, np.floating):
                    kind, blank = a.dtype, np.nan
                else:
                    kind, blank = a.dtype, r.noDataValue
                plane = np.full((rows, columns), blank, dtype=kind)
                g = r._grid
                r0, c0 = int(round((g.top - top) / g.height)), int(round((g.left - left) / g.width))
                src_r0, src_c0 = max(0, -r0), max(0, -c0)
                dst_r0, dst_c0 = max(0, r0), max(0, c0)
                n_r = min(g.rows - src_r0, rows - dst_r0)
                n_c = min(g.columns - src_c0, columns - dst_c0)
                if n_r > 0 and n_c > 0:
                    piece = np.where(m, blank, a)[src_r0:src_r0 + n_r, src_c0:src_c0 + n_c]
                    plane[dst_r0:dst_r0 + n_r, dst_c0:dst_c0 + n_c] = piece
                planes.append(plane)
            self._cubes.append(np.stack(planes, axis=-1))
        size = tuple(int(v) for v in (pixel_block_size or (512, 512)))
        step = tuple(int(v) for v in stride) if stride else size
        if min(size) <= 0 or min(step) <= 0:
            raise ExecuteError(f"{tool}: the block size and stride must be positive.")
        self._size, self._step = size, step
        self._reference = rasters[0].spatialReference
        self._extent_class = Extent
        self._order = [(i, j) for i in range(self.size[0]) for j in range(self.size[1])]
        self._at = 0

    @property
    def size(self):
        g = self._grid
        return (int(math.ceil(g.rows / self._step[0])), int(math.ceil(g.columns / self._step[1])))

    def _block(self, i, j, k):
        g = self._grid
        r0, c0 = i * self._step[0], j * self._step[1]
        data = self._cubes[k][r0:r0 + self._size[0], c0:c0 + self._size[1], :]
        left, top = g.left + c0 * g.width, g.top - r0 * g.height
        extent = self._extent_class(left, top - data.shape[0] * g.height, left + data.shape[1] * g.width, top,
                                    self._reference)
        return PixelBlock(data, extent)

    def _cell(self, i, j):
        ny, nx = self.size
        if not (0 <= i < ny and 0 <= j < nx):
            raise IndexError(f"There is no pixel block ({i}, {j}); the collection is {ny} by {nx}.")
        blocks = [self._block(i, j, k) for k in range(len(self._cubes))]
        return blocks[0] if self._single else blocks

    def __getitem__(self, index):
        if isinstance(index, tuple):
            return self._cell(*index)
        return _Row(self, index)

    def __iter__(self):
        return self

    def __next__(self):
        if self._at >= len(self._order):
            raise StopIteration
        i, j = self._order[self._at]
        self._at += 1
        return self._cell(i, j)

    def __len__(self):
        return len(self._order)

    def reset(self):
        self._at = 0
        return self

    def shuffle(self):
        generator = env.randomGenerator
        seed = None
        if _given(generator):
            parts = str(generator).split()
            seed = int(parts[0]) if parts and parts[0].lstrip("-").isdigit() else None
        random.Random(seed).shuffle(self._order)
        self._at = 0
        return self


class _Row:
    def __init__(self, collection, i):
        self._collection, self._i = collection, i

    def __getitem__(self, j):
        return self._collection._cell(self._i, j)


# ---- Mensuration (RPC sensor models, ADR-234)


class _Measurement:
    def __init__(self, **values):
        self.__dict__.update(values)

    def __repr__(self):
        return f"{type(self).__name__}({', '.join(f'{k}={v!r}' for k, v in self.__dict__.items())})"


class PointMeasurement(_Measurement):
    pass


class LinearMeasurement(_Measurement):
    pass


class AreaMeasurement(_Measurement):
    pass


class HeightMeasurement(_Measurement):
    pass


class Mensuration:
    """arcpy.ia.Mensuration: point, distance, area and height measured through an image's sensor model, as documented,
    on an image that carries rational polynomial coefficients (RPC00B, read through GDAL, the sensor model
    CreateOrthoCorrectedRasterDataset uses, ADR-234). Points are (column, row) image coordinates, or Point or
    PointGeometry objects in a spatial reference, which are carried to the image first, as the page says. Distances
    and azimuths are geodesics on WGS 84, and areas and perimeters are on the ellipsoid, as the page says.

    Refused: ``base_to_shadow`` and ``top_to_shadow``, which need sun angles from the image's metadata, and grapy reads
    none; an image without RPCs.

    INFERRED (ADR-245 §5.4): the ground height for a point is the RPC's height offset — the page says only that "an
    average height" may be used and the API takes no terrain model; ``height`` finds the ellipsoidal height at which the
    top point's ray passes over the base point's ground position, and answers None (no value, as the page allows)
    where it misses by more than a pixel; ``sensor_name`` is "RPC"; the three height-type constants are 1, 2 and 3; an
    azimuth is from 0 to 360."""

    base_to_top = 1
    base_to_shadow = 2
    top_to_shadow = 3

    def __init__(self, raster):
        from . import _ortho
        path = raster.catalogPath if isinstance(raster, Raster) else os.fspath(raster)
        model = _ortho.rpc_of(path) if path and os.path.isfile(str(path)) else None
        if model is None:
            raise ExecuteError(f"Mensuration: {raster!r} carries no sensor model (rational polynomial coefficients).")
        self._model = model
        self._h = float(model.rpc.height_off)

    def _image(self, point):
        """(column, row) of a tuple, Point or PointGeometry."""
        from ._geometry import PointGeometry
        if isinstance(point, (tuple, list)):
            return float(point[0]), float(point[1])
        reference = None
        if isinstance(point, PointGeometry):
            reference = point.spatialReference
            point = point.firstPoint
        x, y = float(point.X), float(point.Y)
        code = getattr(reference, "factoryCode", None) or getattr(getattr(point, "spatialReference", None),
                                                                     "factoryCode", None)
        if not code:
            return x, y
        if int(code) != 4326:
            from pyproj import Transformer
            x, y = Transformer.from_crs(int(code), 4326, always_xy=True).transform(x, y)
        column, row = self._model.project(x, y, self._h)
        return float(column), float(row)

    def _ground(self, column, row, h=None):
        lon, lat = self._model.to_ground([column], [row], self._h if h is None else h)
        return float(lon[0]), float(lat[0])

    def point(self, point):
        column, row = self._image(point)
        lon, lat = self._ground(column, row)
        return PointMeasurement(lon=lon, lat=lat, elev=self._h, sensor_name="RPC")

    def distance(self, from_point, to_point):
        from pyproj import Geod
        a, b = self._ground(*self._image(from_point)), self._ground(*self._image(to_point))
        azimuth, _, metres = Geod(ellps="WGS84").inv(a[0], a[1], b[0], b[1])
        return LinearMeasurement(distance=float(metres), azimuth=float(azimuth % 360.0), sensor_name="RPC")

    def area(self, polygon):
        from pyproj import Geod
        from ._geometry import Polygon
        if isinstance(polygon, Polygon):
            reference = polygon.spatialReference
            import shapely
            from ._geometry import PointGeometry, Point
            ring = shapely.get_coordinates(polygon._shape.exterior if hasattr(polygon._shape, "exterior")
                                           else polygon._shape.geoms[0].exterior)
            points = [PointGeometry(Point(x, y), reference) for x, y in ring]
        else:
            points = list(polygon)
        ground = [self._ground(*self._image(p)) for p in points]
        lons, lats = [g[0] for g in ground], [g[1] for g in ground]
        area, perimeter = Geod(ellps="WGS84").polygon_area_perimeter(lons, lats)
        return AreaMeasurement(area=abs(float(area)), perimeter=float(perimeter), sensor_name="RPC")

    def height(self, base_point, top_point, height_type=1):
        from scipy.optimize import minimize_scalar
        kind = int(height_type) if _given(height_type) else 1
        if kind != self.base_to_top:
            _refuse("Mensuration.height", "height_type", height_type, "shadow heights need sun angles from the image's "
                    "metadata, which grapy does not read")
        base = self._ground(*self._image(base_point))
        top = self._image(top_point)
        scale = float(self._model.rpc.height_scale) or 1000.0

        def miss(h):
            column, row = self._model.project(base[0], base[1], h)
            return float((column - top[0]) ** 2 + (row - top[1]) ** 2)

        found = minimize_scalar(miss, bounds=(self._h - 2 * scale, self._h + 2 * scale), method="bounded",
                                options={"xatol": 1e-4})
        if not found.success or math.sqrt(found.fun) > 1.0:
            return HeightMeasurement(height=None, sensor_name="RPC")
        return HeightMeasurement(height=float(found.x - self._h), sensor_name="RPC")


# ---- RasterCellIterator


class RasterCellIterator:
    """arcpy.sa.RasterCellIterator: the (row, column) pairs of the first raster's grid, for ``raster[row, column]``
    reads and writes, skipping cells where any raster of ``skipNoData`` is NoData; used as a context manager, as the
    page's examples are. A read past the raster's edge, which ``padding`` allows, answers NaN.

    Refused: rasters on a grid other than the first's — the page applies the first raster's environment to them, which
    means resampling it does not specify.

    INFERRED: rows then columns, the order the examples' outputs (a row raster, a raster shifted by one cell) need;
    ``padding`` is a memory hint for pixel blocks, which grapy's in-memory rasters do not use."""

    def __init__(self, *args):
        spec = args[0] if args else {}
        if not isinstance(spec, dict) or "rasters" not in spec:
            raise ValueError("RasterCellIterator takes a dictionary with a 'rasters' list.")
        self._rasters = list(spec["rasters"])
        if not self._rasters or not all(isinstance(r, Raster) for r in self._rasters):
            raise TypeError("RasterCellIterator: 'rasters' must be a list of arcpy.Raster objects.")
        self.padding = int(spec.get("padding", 0) or 0)
        self._skip = list(spec.get("skipNoData", []) or [])
        if any(not any(s is r for r in self._rasters) for s in self._skip):
            raise ValueError("RasterCellIterator: the rasters in skipNoData must be among the rasters.")
        grid = self._rasters[0]._grid
        for r in self._rasters[1:]:
            if not r._grid.same(grid):
                _refuse("RasterCellIterator", "rasters", r, "a raster on another grid than the first's is resampled to "
                        "it, by a rule the page does not give")
        self._grid = grid

    def __enter__(self):
        return self

    def __exit__(self, *_):
        return False

    def __iter__(self):
        rows, columns = self._grid.rows, self._grid.columns
        skip = np.zeros((rows, columns), bool)
        for r in self._skip:
            for m in r._masks:
                skip |= m
        for row in range(rows):
            for column in range(columns):
                if not skip[row, column]:
                    yield row, column

    def reset(self):
        return self


# ---- arcpy.GetUTMFromLocation (ArcMap's arcpy function page; absent from Pro's function list)


def GetUTMFromLocation(longitude, latitude):
    """The WGS 84 UTM zone spatial reference of a longitude and latitude, named WGS_1984_UTM_Zone_XXH as documented:
    zone = ⌊(longitude + 180)/6⌋ + 1, H is N north of the equator and S south of it (EPSG 326xx and 327xx).

    INFERRED (ADR-245 §5.5): the equator is north; longitude 180 is zone 60; the Norway and Svalbard exceptions to the
    6° zones, which the page does not mention, are not applied."""
    from ._reference import SpatialReference
    lon, lat = float(longitude), float(latitude)
    if not (-180.0 <= lon <= 180.0 and -90.0 <= lat <= 90.0):
        raise ValueError(f"GetUTMFromLocation: ({lon}, {lat}) is not a longitude and latitude.")
    zone = min(int(math.floor((lon + 180.0) / 6.0)) + 1, 60)
    north = lat >= 0
    reference = SpatialReference((32600 if north else 32700) + zone)
    reference.name = f"WGS_1984_UTM_Zone_{zone}{'N' if north else 'S'}"
    return reference
